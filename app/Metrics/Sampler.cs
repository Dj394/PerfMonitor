using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using PerfMonitorLive.Alerts;

namespace PerfMonitorLive.Metrics
{
    /// <summary>Collecte les compteurs système, les capteurs matériels, le score de redémarrage et les fuites.
    ///
    /// Trois threads, et c'est un choix de conception à respecter :
    /// <list type="bullet">
    /// <item>« Sampler » — la mesure. Ne fait que du travail immédiat : compteurs noyau (<see cref="Native"/>) et
    /// assemblage des valeurs publiées par les deux autres. C'est lui qui tient la cadence dont dépendent les
    /// alertes à maintien et la régularité de l'historique.</item>
    /// <item>« SamplerWmi » — disques, réseau, pression mémoire (PDH en processus, repli WMI), processus
    /// (NtQuerySystemInformation, repli API .NET), score de redémarrage, batterie.</item>
    /// <item>« SamplerSensors » — LibreHardwareMonitor et SMART, et la température ACPI de repli (WMI).</item>
    /// </list>
    ///
    /// Toute nouvelle source pouvant bloquer (WMI, pilote, disque, réseau) va sur un thread de publication,
    /// jamais dans <c>Collect</c> : sur une machine saturée, ces sources ont été mesurées entre 5 et 19 s par appel,
    /// ce qui faisait tomber la cadence à 20-60 s et empêchait les alertes de partir au moment où elles servent.
    /// Le chronométrage par phase journalise tout dépassement dans <c>data\live.log</c>.</summary>
    public class Sampler : IDisposable
    {
        public event Action<Sample> SampleReady;
        readonly Thread _thread; volatile bool _run = true;
        /// <summary>Réveille les threads en attente à l'arrêt (au lieu d'attendre la fin d'un Thread.Sleep).</summary>
        readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        /// <summary>Période d'échantillonnage (1000 ms normal, 2000 ms en mode économie).</summary>
        public volatile int IntervalMs = 1000;
        /// <summary>Coût de PerfMonitor lui-même (dernière mesure).</summary>
        public double SelfCpuPct { get; private set; }
        public double SelfMemMB { get; private set; }
        readonly int _cores = Environment.ProcessorCount;
        double _totalMB;
        volatile List<ProcSample> _lastProcs = new List<ProcSample>();
        long _nextProcTick;   // Stopwatch : prochaine énumération des processus
        long _batNextTick; double? _bat; bool? _ac; bool _batAbsent;
        public Hardware Hw { get; } = new Hardware();
        /// <summary>Compteur d'images par seconde (ETW). Son <see cref="FpsMeter.TargetPid"/> est branche par l'application
        /// sur le PID du jeu detecte ; sans jeu au premier plan il ne renvoie rien.</summary>
        public FpsMeter Fps { get; } = new FpsMeter();
        public LeakDetector Leaks { get; } = new LeakDetector();
        readonly RebootCheck _reboot = new RebootCheck();
        static readonly Regex NetSkip = new Regex("isatap|Loopback|Teredo", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Dernières valeurs publiées par le thread WMI (disques, réseau, pression mémoire).</summary>
        class WmiSnap { public List<DiskSample> Disks = new List<DiskSample>(); public double Rx, Tx, PageIn; }
        volatile WmiSnap _wmi;
        volatile Hardware.Reading _sensors;
        volatile RebootInfo _rebootCache;
        readonly Thread _wmiThread, _sensorThread;

        public Sampler()
        {
            // AboveNormal : en BelowNormal, une machine saturée affame le thread de mesure (mesures espacées de 20 s au lieu de 5),
            // et les alertes à maintien (« au-dessus du seuil depuis 30 s ») ne partent jamais quand la charge est maximale.
            _thread = new Thread(Loop) { IsBackground = true, Name = "Sampler", Priority = ThreadPriority.AboveNormal };
            _wmiThread = new Thread(WmiLoop) { IsBackground = true, Name = "SamplerWmi" };
            _sensorThread = new Thread(SensorLoop) { IsBackground = true, Name = "SamplerSensors" };
        }
        public void Start() { Fps.Start(); _thread.Start(); _wmiThread.Start(); _sensorThread.Start(); }

        /// <summary>Attend la fin de la période commencée à <paramref name="t0"/> (horodatage <see cref="Stopwatch"/>), ou l'arrêt.</summary>
        void Pause(long t0)
        {
            double el = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            int period = IntervalMs;
            if (el < period) _stop.Wait((int)(period - el));
        }
        static long Ms(long ms) => ms * Stopwatch.Frequency / 1000;

        // ---------------------------------------------------------------- capteurs

        /// <summary>Capteurs matériels sur leur propre thread : LibreHardwareMonitor et la lecture SMART ont été
        /// mesurés jusqu'à 14,7 s sur une machine saturée. Températures et consommations n'ont pas besoin d'être
        /// fraîches à la seconde ; la mesure lit la dernière publication.</summary>
        void SensorLoop()
        {
            Hw.TryInit();
            while (_run)
            {
                long t0 = Stopwatch.GetTimestamp();
                try { _sensors = Hw.Read(); }
                catch (Exception ex) { Paths.Log("Capteurs: " + ex.Message); }
                if (!Hw.Available) RefreshAcpiTemp(t0);
                Pause(t0);
            }
        }

        // Température de repli (sans LibreHardwareMonitor) : MSAcpi_ThermalZoneTemperature, requête WMI lue ici toutes
        // les 5 s et non plus à chaque mesure sur le thread de mesure. NaN = pas de valeur à cette lecture.
        double _acpiTemp = double.NaN;
        volatile bool _tempTried, _tempAvailable;
        long _tempNextTick;
        void RefreshAcpiTemp(long t0)
        {
            if (_tempTried && !_tempAvailable) return;   // absente : on n'insiste pas (comme avant)
            if (_tempTried && t0 < _tempNextTick) return;
            _tempNextTick = t0 + Ms(5000);
            double t = double.NaN;
            try
            {
                foreach (var o in Query("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", @"root\wmi"))
                { t = Math.Round(D(o["CurrentTemperature"]) / 10.0 - 273.15, 1); _tempAvailable = true; break; }
            }
            catch { _tempAvailable = false; }
            Volatile.Write(ref _acpiTemp, t);
            _tempTried = true;
        }

        // ---------------------------------------------------------------- thread lent

        /// <summary>Boucle lente séparée : une requête lente (jusqu'à 19 s constatées sous charge) ne doit jamais
        /// retarder la mesure ni les alertes ; la mesure lit simplement la dernière publication.</summary>
        void WmiLoop()
        {
            try
            {
                while (_run)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    var now = DateTime.Now;
                    try { var snap = ReadCounters(); if (snap != null) _wmi = snap; }
                    catch (Exception ex) { Paths.Log("WMI: " + ex.Message); }
                    try
                    {
                        if (t0 >= _nextProcTick)   // énumération des processus toutes les 5 s
                        {
                            var list = CollectProcesses(now);
                            _lastProcs = Top(list, 6);
                            _nextProcTick = t0 + Ms(5000);
                            Leaks.Feed(now, list);
                        }
                    }
                    catch (Exception ex) { Paths.Log("Procs: " + ex.Message); }
                    try { _rebootCache = _reboot.Get(now); }   // trois requêtes WMI toutes les 60 s : jusqu'à 13,6 s sous charge
                    catch (Exception ex) { Paths.Log("Reboot: " + ex.Message); }
                    try { RefreshBattery(t0); }
                    catch (Exception ex) { Paths.Log("Batterie: " + ex.Message); }
                    Pause(t0);
                }
            }
            finally { _pdh?.Dispose(); _pdh = null; _procSnap?.Dispose(); }
        }

        // ---------------------------------------------------------------- disques, réseau, pression mémoire

        /// <summary>Format des compteurs PDH. Les propriétés <c>Win32_PerfFormattedData_*</c> lues jusqu'ici sont des
        /// entiers : WMI tronque la valeur calculée (vérifié : % temps disque 80 → file d'attente 0, 177 → 1) et ne la
        /// plafonne pas à 100. FMT_LARGE | NOCAP100 reproduit exactement cette conversion pour les taux et pourcentages.</summary>
        const uint CounterFormat = Pdh.FMT_LARGE | Pdh.FMT_NOCAP100;
        /// <summary>File d'attente et latence en valeurs décimales : tronquées à l'entier (comme WMI jusqu'à la 3.2.0),
        /// la latence était lue en secondes entières et valait toujours 0, ce qui rendait les règles « disk.lat » inopérantes.
        /// Le repli WMI garde cette limite.</summary>
        const uint FractionalFormat = Pdh.FMT_DOUBLE | Pdh.FMT_NOCAP100;
        static readonly string[] PdhPaths =
        {
            @"\Memory\Pages Input/sec",
            @"\PhysicalDisk(*)\% Disk Time", @"\PhysicalDisk(*)\Disk Read Bytes/sec", @"\PhysicalDisk(*)\Disk Write Bytes/sec",
            @"\PhysicalDisk(*)\Avg. Disk Queue Length", @"\PhysicalDisk(*)\Avg. Disk sec/Transfer",
            @"\Network Interface(*)\Bytes Received/sec", @"\Network Interface(*)\Bytes Sent/sec",
        };
        const int CPageIn = 0, CPct = 1, CRead = 2, CWrite = 3, CQueue = 4, CLat = 5, CRx = 6, CTx = 7;
        Pdh _pdh; bool _pdhTried, _pdhErrLogged, _selfTested;
        int _pdhFailures;
        long _pdhBaseTick;
        readonly IntPtr[] _pdhCounters = new IntPtr[PdhPaths.Length];
        readonly Pdh.Item[][] _pdhItems = new Pdh.Item[PdhPaths.Length][];
        readonly int[] _pdhCounts = new int[PdhPaths.Length];
        static readonly System.Buffers.SearchValues<char> WmiReplaced = System.Buffers.SearchValues.Create("()#/\\");
        readonly Dictionary<string, string> _wmiNames = new Dictionary<string, string>();

        /// <summary>Disques, réseau et pression mémoire : PDH en processus ; WMI si PDH est indisponible.
        /// Renvoie null tant que PDH n'a qu'une seule collecte (un taux en demande deux).</summary>
        WmiSnap ReadCounters()
        {
            if (!_pdhTried) InitPdh();
            if (_pdh != null)
            {
                if (Stopwatch.GetElapsedTime(_pdhBaseTick).TotalMilliseconds < 500) return null;
                var netNames = _selfTested ? null : new List<string>();
                var snap = ReadPdh(netNames, out string err);
                if (snap != null)
                {
                    _pdhFailures = 0;
                    if (!_selfTested) SelfTest(snap, netNames);
                    return snap;
                }
                if (!_pdhErrLogged) { _pdhErrLogged = true; Paths.Log("PDH: " + err + " — repli sur WMI pour cette lecture"); }
                if (++_pdhFailures >= 5) { Paths.Log("PDH: 5 échecs consécutifs, abandon au profit de WMI"); _pdh.Dispose(); _pdh = null; }
            }
            return ReadWmi(null);
        }

        void InitPdh()
        {
            _pdhTried = true;
            var q = new Pdh();
            string step = "PdhOpenQuery";
            uint st = q.Open();
            for (int i = 0; i < PdhPaths.Length && st == 0; i++) { step = PdhPaths[i]; st = q.Add(PdhPaths[i], out _pdhCounters[i]); }
            if (st == 0) { step = "PdhCollectQueryData"; st = q.Collect(); }
            if (st != 0)
            {
                q.Dispose();
                Paths.Log("PDH indisponible (" + step + " : 0x" + st.ToString("X8") + "), disques/réseau/pression mémoire lus par WMI");
                return;
            }
            _pdh = q; _pdhBaseTick = Stopwatch.GetTimestamp();
        }

        /// <summary>Nom d'instance tel que WMI l'expose : WMI remplace ( ) # / \ dans les noms de compteurs
        /// (parenthèses → crochets, le reste → « _ »). Les règles d'alerte et l'historique sont liés à ces noms.</summary>
        string WmiName(string pdhName)
        {
            if (pdhName.AsSpan().IndexOfAny(WmiReplaced) < 0) return pdhName;
            if (_wmiNames.TryGetValue(pdhName, out var n)) return n;
            if (_wmiNames.Count > 256) _wmiNames.Clear();
            return _wmiNames[pdhName] = ToWmiName(pdhName);
        }

        /// <summary>Transformation pure (testée) : ( ) → [ ], et # / \ → _.</summary>
        internal static string ToWmiName(string pdhName)
        {
            var sb = new StringBuilder(pdhName);
            sb.Replace('(', '[').Replace(')', ']').Replace('#', '_').Replace('/', '_').Replace('\\', '_');
            return sb.ToString();
        }

        WmiSnap ReadPdh(List<string> netNames, out string err)
        {
            err = null;
            uint st = _pdh.Collect();
            if (st != 0) { err = "PdhCollectQueryData 0x" + st.ToString("X8"); return null; }
            var snap = new WmiSnap();
            st = _pdh.Value(_pdhCounters[CPageIn], CounterFormat, out double pageIn);
            if (st != 0) { err = PdhPaths[CPageIn] + " 0x" + st.ToString("X8"); return null; }
            snap.PageIn = pageIn;
            var counts = _pdhCounts;
            for (int c = CPct; c < PdhPaths.Length; c++)
            {
                st = _pdh.Array(_pdhCounters[c], c == CQueue || c == CLat ? FractionalFormat : CounterFormat, ref _pdhItems[c], out counts[c]);
                if (st != 0) { err = PdhPaths[c] + " 0x" + st.ToString("X8"); return null; }
            }
            var pct = _pdhItems[CPct];
            for (int i = 0; i < counts[CPct]; i++)
            {
                var name = pct[i].Name; if (name == "_Total") continue;
                snap.Disks.Add(new DiskSample
                {
                    n = WmiName(name), pct = (int)Math.Min(100, pct[i].Value),
                    r = Math.Round(Find(CRead, counts, i, name) / 1048576.0, 2), w = Math.Round(Find(CWrite, counts, i, name) / 1048576.0, 2),
                    q = Math.Round(Find(CQueue, counts, i, name), 2), lat = Math.Round(Find(CLat, counts, i, name) * 1000, 1)
                });
            }
            double rx = 0, tx = 0;
            var rxi = _pdhItems[CRx];
            for (int i = 0; i < counts[CRx]; i++)
            {
                var n = WmiName(rxi[i].Name);
                if (NetSkip.IsMatch(n)) continue;
                netNames?.Add(n);
                rx += rxi[i].Value;
            }
            var txi = _pdhItems[CTx];
            for (int i = 0; i < counts[CTx]; i++) { if (NetSkip.IsMatch(WmiName(txi[i].Name))) continue; tx += txi[i].Value; }
            snap.Rx = Math.Round(rx / 1024, 1); snap.Tx = Math.Round(tx / 1024, 1);
            return snap;
        }

        /// <summary>Valeur de l'instance <paramref name="name"/> dans un autre compteur du même objet (même rang en pratique).</summary>
        double Find(int counter, int[] counts, int index, string name)
        {
            var items = _pdhItems[counter];
            if (index < counts[counter] && items[index].Name == name) return items[index].Value;
            for (int i = 0; i < counts[counter]; i++) if (items[i].Name == name) return items[i].Value;
            return 0;
        }

        /// <summary>Méthode d'origine (repli) : trois requêtes Win32_PerfFormattedData, servies par WmiPrvSE.</summary>
        WmiSnap ReadWmi(List<string> netNames)
        {
            var snap = new WmiSnap();
            foreach (var o in Query("SELECT PagesInputPersec FROM Win32_PerfFormattedData_PerfOS_Memory"))
                snap.PageIn = D(o["PagesInputPersec"]);
            foreach (var o in Query("SELECT Name,PercentDiskTime,DiskReadBytesPersec,DiskWriteBytesPersec,AvgDiskQueueLength,AvgDisksecPerTransfer FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk"))
            {
                var name = (string)o["Name"]; if (name == "_Total") continue;
                snap.Disks.Add(new DiskSample
                {
                    n = name, pct = (int)Math.Min(100, D(o["PercentDiskTime"])),
                    r = Math.Round(D(o["DiskReadBytesPersec"]) / 1048576.0, 2), w = Math.Round(D(o["DiskWriteBytesPersec"]) / 1048576.0, 2),
                    q = Math.Round(D(o["AvgDiskQueueLength"]), 2), lat = Math.Round(D(o["AvgDisksecPerTransfer"]) * 1000, 1)
                });
            }
            double rx = 0, tx = 0;
            foreach (var o in Query("SELECT Name,BytesReceivedPersec,BytesSentPersec FROM Win32_PerfFormattedData_Tcpip_NetworkInterface"))
            {
                var n = (string)o["Name"] ?? "";
                if (NetSkip.IsMatch(n)) continue;
                netNames?.Add(n);
                rx += D(o["BytesReceivedPersec"]); tx += D(o["BytesSentPersec"]);
            }
            snap.Rx = Math.Round(rx / 1024, 1); snap.Tx = Math.Round(tx / 1024, 1);
            return snap;
        }

        /// <summary>Une seule fois, à la première lecture PDH : compare à WMI et journalise (validation sur la machine).
        /// Les fenêtres de calcul diffèrent d'environ une seconde : les noms doivent être identiques, les valeurs proches.</summary>
        void SelfTest(WmiSnap p, List<string> pdhNet)
        {
            _selfTested = true;
            try
            {
                var wmiNet = new List<string>();
                var w = ReadWmi(wmiNet);
                var pd = string.Join(" | ", p.Disks.Select(d => d.n)); var wd = string.Join(" | ", w.Disks.Select(d => d.n));
                var pn = string.Join(" | ", pdhNet); var wn = string.Join(" | ", wmiNet);
                var sb = new StringBuilder("Auto-test PDH/WMI : ");
                sb.Append("disques ").Append(pd == wd ? "identiques [" + pd + "]" : "DIFFÉRENTS pdh=[" + pd + "] wmi=[" + wd + "]");
                sb.Append(" · réseau ").Append(pn == wn ? "identiques [" + pn + "]" : "DIFFÉRENTS pdh=[" + pn + "] wmi=[" + wn + "]");
                sb.Append(" · pageIn pdh=").Append(p.PageIn).Append(" wmi=").Append(w.PageIn);
                sb.Append(" · rx/tx Ko/s pdh=").Append(p.Rx).Append('/').Append(p.Tx).Append(" wmi=").Append(w.Rx).Append('/').Append(w.Tx);
                foreach (var d in p.Disks)
                {
                    var o = w.Disks.FirstOrDefault(x => x.n == d.n);
                    sb.Append(" · ").Append(d.n).Append(" pdh pct=").Append(d.pct).Append(" r=").Append(d.r).Append(" w=").Append(d.w).Append(" q=").Append(d.q).Append(" lat=").Append(d.lat);
                    if (o != null) sb.Append(" / wmi pct=").Append(o.pct).Append(" r=").Append(o.r).Append(" w=").Append(o.w).Append(" q=").Append(o.q).Append(" lat=").Append(o.lat);
                }
                Paths.Log(sb.ToString());
            }
            catch (Exception ex) { Paths.Log("Auto-test PDH/WMI : " + ex.Message); }
        }

        // ---------------------------------------------------------------- processus

        /// <summary>% CPU = delta temps CPU / delta temps réel. Base par PID : (horodatage Stopwatch, temps CPU en 100 ns).</summary>
        readonly Dictionary<int, KeyValuePair<long, long>> _procCpu = new Dictionary<int, KeyValuePair<long, long>>();
        readonly Dictionary<int, KeyValuePair<DateTime, TimeSpan>> _procCpuNet = new Dictionary<int, KeyValuePair<DateTime, TimeSpan>>();
        readonly int _selfPid = Environment.ProcessId;
        readonly ProcessSnapshot _procSnap = ProcessSnapshot.Supported ? new ProcessSnapshot() : null;
        readonly List<ProcEntry> _procEntries = new List<ProcEntry>();
        readonly List<int> _deadPids = new List<int>();
        readonly HashSet<int> _alive = new HashSet<int>();
        readonly Dictionary<string, ProcSample> _groups = new Dictionary<string, ProcSample>();
        readonly Dictionary<string, double> _groupMaxCpu = new Dictionary<string, double>();
        bool _procErrLogged;

        /// <summary>Tous les processus regroupés par programme (comme WMI ramenait chrome#1, chrome#2… à « chrome ») :
        /// le top et le détecteur de fuites suivent le programme. PID du groupe = l'instance la plus active.</summary>
        List<ProcSample> CollectProcesses(DateTime now)
        {
            if (_procSnap != null)
            {
                int st;
                try { st = _procSnap.Read(_procEntries); }
                catch (Exception ex) { st = -1; if (!_procErrLogged) Paths.Log("NtQuerySystemInformation: " + ex.Message); }
                if (st == 0) return CollectNative();
                if (!_procErrLogged) { _procErrLogged = true; Paths.Log("NtQuerySystemInformation 0x" + st.ToString("X8") + " — repli sur l'API .NET pour cette lecture"); }
            }
            return CollectDotNet(now);
        }

        List<ProcSample> CollectNative()
        {
            long ts = Stopwatch.GetTimestamp();
            var list = new List<ProcSample>(); _groups.Clear(); _groupMaxCpu.Clear();
            foreach (var e in _procEntries)
            {
                if (e.Pid == 0) continue;
                double pct = 0;
                if (_procCpu.TryGetValue(e.Pid, out var prev))
                {
                    double wall = (double)(ts - prev.Key) / Stopwatch.Frequency;
                    if (wall > 0.5) pct = Math.Max(0, (e.CpuTime - prev.Value) / 1e7 / wall / _cores * 100);
                }
                _procCpu[e.Pid] = new KeyValuePair<long, long>(ts, e.CpuTime);
                double memMB = e.PrivateBytes / 1048576.0;
                AddToGroup(list, e.Name, e.Pid, Math.Round(pct, 1), Math.Round(memMB), e.Handles);
                if (e.Pid == _selfPid) { SelfCpuPct = Math.Round(pct, 2); SelfMemMB = Math.Round(memMB); }
            }
            // processus disparus : _procEntries contient tous les PID vivants
            if (_procCpu.Count > _procEntries.Count - 1)
            {
                _alive.Clear(); foreach (var e in _procEntries) _alive.Add(e.Pid);
                _deadPids.Clear(); foreach (var k in _procCpu.Keys) if (!_alive.Contains(k)) _deadPids.Add(k);
                foreach (var k in _deadPids) _procCpu.Remove(k);
            }
            foreach (var g in list) g.cpu = Math.Round(g.cpu, 1);
            return list;
        }

        /// <summary>Méthode d'origine (repli) : Process.GetProcesses + TotalProcessorTime (un OpenProcess par processus).</summary>
        List<ProcSample> CollectDotNet(DateTime now)
        {
            var list = new List<ProcSample>(); _groups.Clear(); _groupMaxCpu.Clear();
            var seen = new HashSet<int>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        int pid = p.Id; if (pid == 0) continue;
                        seen.Add(pid);
                        TimeSpan cpuT; try { cpuT = p.TotalProcessorTime; } catch { cpuT = TimeSpan.Zero; }
                        double pct = 0;
                        if (_procCpuNet.TryGetValue(pid, out var prev))
                        {
                            var wall = (now - prev.Key).TotalSeconds;
                            if (wall > 0.5) pct = Math.Max(0, (cpuT - prev.Value).TotalSeconds / wall / _cores * 100);
                        }
                        _procCpuNet[pid] = new KeyValuePair<DateTime, TimeSpan>(now, cpuT);
                        double memMB = p.PrivateMemorySize64 / 1048576.0;
                        AddToGroup(list, p.ProcessName, pid, Math.Round(pct, 1), Math.Round(memMB), p.HandleCount);
                        if (pid == _selfPid) { SelfCpuPct = Math.Round(pct, 2); SelfMemMB = Math.Round(memMB); }
                    }
                    catch { }
                }
            }
            foreach (var dead in _procCpuNet.Keys.Where(k => !seen.Contains(k)).ToList()) _procCpuNet.Remove(dead);
            foreach (var g in list) g.cpu = Math.Round(g.cpu, 1);
            return list;
        }

        /// <summary>Cumule une instance dans le groupe de son programme. Équivaut à l'ancien
        /// <c>GroupBy(n)</c> : ordre de première apparition, sommes dans l'ordre d'énumération, PID = premier au CPU maximal.
        /// <c>cpu</c> contient la somme brute jusqu'à l'arrondi final.</summary>
        void AddToGroup(List<ProcSample> list, string name, int pid, double cpu, double mem, double handles)
        {
            if (!_groups.TryGetValue(name, out var g))
            {
                g = new ProcSample { n = name, Pid = pid };
                _groups[name] = g; _groupMaxCpu[name] = cpu; list.Add(g);
            }
            else if (cpu > _groupMaxCpu[name]) { _groupMaxCpu[name] = cpu; g.Pid = pid; }
            g.cpu += cpu; g.mem += mem; g.h += handles;
        }

        /// <summary>Les <paramref name="n"/> premiers par CPU puis mémoire décroissants, à égalité dans l'ordre de la liste
        /// (= OrderByDescending.ThenByDescending.Take, tri stable) sans trier toute la liste.</summary>
        static List<ProcSample> Top(List<ProcSample> list, int n)
        {
            var top = new List<ProcSample>(n + 1);
            foreach (var p in list)
            {
                int i = top.Count;
                while (i > 0 && (p.cpu > top[i - 1].cpu || p.cpu == top[i - 1].cpu && p.mem > top[i - 1].mem)) i--;
                if (i >= n) continue;
                top.Insert(i, p);
                if (top.Count > n) top.RemoveAt(n);
            }
            return top;
        }

        // ---------------------------------------------------------------- batterie

        /// <summary>Batterie (portables) : Win32_Battery toutes les 20 s (jusqu'à 5 s de réponse sous charge),
        /// appelée par le thread lent ; abandonnée si absente.</summary>
        void RefreshBattery(long t0)
        {
            if (_batAbsent) return;
            if (t0 >= _batNextTick)
            {
                _batNextTick = t0 + Ms(20000); bool found = false;
                try
                {
                    foreach (var o in Query("SELECT EstimatedChargeRemaining,BatteryStatus FROM Win32_Battery"))
                    { found = true; _bat = D(o["EstimatedChargeRemaining"]); var st = (int)D(o["BatteryStatus"]); _ac = st == 2 || st >= 6 && st <= 9; break; }
                }
                catch { }
                if (!found) { _batAbsent = true; _bat = null; _ac = null; }
            }
        }

        // ---------------------------------------------------------------- mesure

        void Loop()
        {
            Native.Memory(out double totMB, out _);
            _totalMB = Math.Round(totMB);
            if (_totalMB <= 0) _totalMB = 1;
            long prev = Stopwatch.GetTimestamp(), lastLate = 0; bool lateLogged = false;
            while (_run)
            {
                long t0 = Stopwatch.GetTimestamp();
                var now = DateTime.Now;
                double gap = Stopwatch.GetElapsedTime(prev, t0).TotalMilliseconds; prev = t0;
                if (gap > 3 * IntervalMs && (!lateLogged || Stopwatch.GetElapsedTime(lastLate, t0).TotalSeconds > 15))   // trou de mesure : fausse les alertes à maintien
                {
                    lastLate = t0; lateLogged = true;
                    Paths.Log("Mesure en retard : " + (gap / 1000).ToString("0.0") + " s au lieu de " + (IntervalMs / 1000.0).ToString("0.#") + " s · phases précédentes : " + FormatPhases());
                }
                try
                {
                    var s = Collect(now);
                    _phase.Restart();
                    SampleReady?.Invoke(s);
                    _phaseMs[PhaseSubscribers] = _phase.ElapsedMilliseconds;
                }
                catch (Exception ex) { Paths.Log("Sample: " + ex.Message); }
                Pause(t0);
            }
        }

        /// <summary>Chronométrage des phases : renseigné à chaque mesure (des entiers, sans allocation), mis en forme
        /// seulement quand une mesure en retard est journalisée.</summary>
        readonly Stopwatch _phase = new Stopwatch();
        static readonly string[] PhaseNames = { "wmi", "procs", "bat", "reboot", "capteurs", "fps", "abonnés" };
        const int PhaseSubscribers = 6;
        readonly long[] _phaseMs = new long[7];
        void Lap(int phase) { _phaseMs[phase] = _phase.ElapsedMilliseconds; _phase.Restart(); }
        string FormatPhases()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < PhaseNames.Length; i++) sb.Append(PhaseNames[i]).Append('=').Append(_phaseMs[i]).Append("ms ");
            return sb.ToString();
        }

        Sample Collect(DateTime now)
        {
            _phase.Restart();
            var s = new Sample { Time = now, TotalMB = _totalMB };   // ts : formaté à la sérialisation (voir Sample.ts)
            // CPU et mémoire : API noyau en processus (quelques microsecondes). WMI mettait jusqu'à 19 s à répondre
            // sur une machine saturée, ce qui trouait l'historique et retardait les alertes à maintien.
            s.cpu = Native.CpuPercent();
            Native.Memory(out double totalMB, out double availMB);
            if (totalMB > 0) { _totalMB = totalMB; s.TotalMB = totalMB; }
            s.memMB = Math.Max(0, _totalMB - availMB);
            s.memPct = _totalMB > 0 ? Math.Round(s.memMB * 100 / _totalMB, 1) : 0;
            // disques, réseau et pression mémoire : mesurés par le thread lent — on lit sa dernière publication
            var w = _wmi;
            if (w != null) { s.disks = w.Disks; s.rx = w.Rx; s.tx = w.Tx; s.pageIn = w.PageIn; }
            Lap(0);

            s.procs = _lastProcs;
            s.Leaks = Leaks.Current;
            Lap(1);
            s.bat = _bat; s.ac = _ac;
            Lap(2);
            s.rb = _rebootCache;
            Lap(3);

            var r = _sensors ?? new Hardware.Reading();
            Lap(4);
            s.temp = r.Cpu; s.gpu = r.Gpu; s.cpuMHz = r.CpuMHz; s.gpuMHz = r.GpuMHz; s.cpuW = r.CpuW; s.gpuW = r.GpuW;
            s.fans = r.Fans; s.stor = r.Storage;
            s.v12 = r.V12; s.v5 = r.V5; s.v33 = r.V33; s.volts = r.Volts;
            if (!Hw.Available && _tempAvailable)   // repli ACPI, lu par le thread des capteurs
            {
                double t = Volatile.Read(ref _acpiTemp);
                if (!double.IsNaN(t)) s.temp = t;
            }
            s.fps = Fps.Read();   // un seul appel par mesure : la lecture consomme le compteur pour calculer un debit
            Lap(5);
            return s;
        }

        // ---------------------------------------------------------------- utilitaires

        static double D(object v) { if (v == null) return 0; try { return Convert.ToDouble(v); } catch { return 0; } }
        /// <summary>Requête WMI ; chaque objet est disposé dès que l'appelant passe au suivant ou sort de la boucle.</summary>
        static IEnumerable<ManagementObject> Query(string wql, string ns = @"root\cimv2")
        {
            using (var s = new ManagementObjectSearcher(ns, wql, new EnumerationOptions { Rewindable = false, ReturnImmediately = true }))
            using (var c = s.Get())
                foreach (ManagementObject o in c) { using (o) yield return o; }
        }

        /// <summary>Arrête les trois threads et attend leur fin, 600 ms au plus pour les trois ensemble (appelé depuis le
        /// thread UI à la fermeture : WMI ou un capteur bloqué ne doit pas figer l'interface). Réveillés par <see cref="_stop"/>,
        /// ils terminent la lecture en cours ; ce sont des threads d'arrière-plan, la fin du processus les emporte sinon.</summary>
        public void Stop()
        {
            _run = false; _stop.Set();
            const int BudgetMs = 600;
            long t0 = Stopwatch.GetTimestamp();
            foreach (var t in new[] { _thread, _wmiThread, _sensorThread })
            {
                if (!t.IsAlive || t == Thread.CurrentThread) continue;
                int left = Math.Max(0, BudgetMs - (int)Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                if (!t.Join(left)) Paths.Log("Arrêt : le thread " + t.Name + " n'a pas fini dans le délai (" + BudgetMs + " ms au total)");
            }
        }

        public void Dispose()
        {
            Stop();
            // LibreHardwareMonitor n'est fermé qu'une fois le thread des capteurs sorti de Update()
            if (!_sensorThread.IsAlive) Hw.Dispose();
            else Paths.Log("Arrêt : capteurs encore en lecture, LibreHardwareMonitor laissé ouvert (libéré à la fin du processus)");
            Fps.Dispose();
        }
    }
}
