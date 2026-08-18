using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
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
    /// <item>« SamplerWmi » — disques, réseau, pression mémoire, processus, score de redémarrage, batterie.</item>
    /// <item>« SamplerSensors » — LibreHardwareMonitor et SMART.</item>
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
        /// <summary>Période d'échantillonnage (1000 ms normal, 2000 ms en mode économie).</summary>
        public volatile int IntervalMs = 1000;
        /// <summary>Coût de PerfMonitor lui-même (dernière mesure).</summary>
        public double SelfCpuPct { get; private set; }
        public double SelfMemMB { get; private set; }
        readonly int _cores = Environment.ProcessorCount;
        double _totalMB;
        volatile List<ProcSample> _lastProcs = new List<ProcSample>();
        DateTime _lastProcTime = DateTime.MinValue;
        bool _tempTried, _tempAvailable;
        DateTime _batTime = DateTime.MinValue; double? _bat; bool? _ac; bool _batAbsent;
        public Hardware Hw { get; } = new Hardware();
        public LeakDetector Leaks { get; } = new LeakDetector();
        readonly RebootCheck _reboot = new RebootCheck();
        static readonly Regex ProcSuffix = new Regex(@"#\d+$", RegexOptions.Compiled);
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
        public void Start() { _thread.Start(); _wmiThread.Start(); _sensorThread.Start(); }

        /// <summary>Capteurs matériels sur leur propre thread : LibreHardwareMonitor et la lecture SMART ont été
        /// mesurés jusqu'à 14,7 s sur une machine saturée. Températures et consommations n'ont pas besoin d'être
        /// fraîches à la seconde ; la mesure lit la dernière publication.</summary>
        void SensorLoop()
        {
            Hw.TryInit();
            while (_run)
            {
                var t0 = DateTime.Now;
                try { _sensors = Hw.Read(); }
                catch (Exception ex) { Paths.Log("Capteurs: " + ex.Message); }
                var el = (DateTime.Now - t0).TotalMilliseconds;
                int period = IntervalMs;
                if (el < period) Thread.Sleep((int)(period - el));
            }
        }

        /// <summary>Boucle WMI séparée : une requête lente (jusqu'à 19 s constatées sous charge) ne doit jamais
        /// retarder la mesure ni les alertes ; la mesure lit simplement la dernière publication.</summary>
        void WmiLoop()
        {
            while (_run)
            {
                var t0 = DateTime.Now;
                try
                {
                    var snap = new WmiSnap();
                    foreach (var o in Query("SELECT AvailableMBytes,PagesInputPersec FROM Win32_PerfFormattedData_PerfOS_Memory"))
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
                        if (NetSkip.IsMatch((string)o["Name"] ?? "")) continue;
                        rx += D(o["BytesReceivedPersec"]); tx += D(o["BytesSentPersec"]);
                    }
                    snap.Rx = Math.Round(rx / 1024, 1); snap.Tx = Math.Round(tx / 1024, 1);
                    _wmi = snap;
                }
                catch (Exception ex) { Paths.Log("WMI: " + ex.Message); }
                try
                {
                    if ((t0 - _lastProcTime).TotalSeconds >= 5)   // énumération des processus : jusqu'à 3 s sous charge
                    {
                        var list = CollectProcesses(t0);
                        _lastProcs = list.OrderByDescending(p => p.cpu).ThenByDescending(p => p.mem).Take(6).ToList();
                        _lastProcTime = t0;
                        Leaks.Feed(t0, list);
                    }
                }
                catch (Exception ex) { Paths.Log("Procs: " + ex.Message); }
                try { _rebootCache = _reboot.Get(t0); }   // trois requêtes WMI toutes les 60 s : jusqu'à 13,6 s sous charge
                catch (Exception ex) { Paths.Log("Reboot: " + ex.Message); }
                try { RefreshBattery(t0); }
                catch (Exception ex) { Paths.Log("Batterie: " + ex.Message); }
                var el = (DateTime.Now - t0).TotalMilliseconds;
                int period = IntervalMs;
                if (el < period) Thread.Sleep((int)(period - el));
            }
        }

        void Loop()
        {
            Native.Memory(out double totMB, out _);
            _totalMB = Math.Round(totMB);
            if (_totalMB <= 0) _totalMB = 1;
            var prev = DateTime.Now; var lastLate = DateTime.MinValue;
            while (_run)
            {
                var t0 = DateTime.Now;
                var gap = (t0 - prev).TotalMilliseconds; prev = t0;
                if (gap > 3 * IntervalMs && (t0 - lastLate).TotalSeconds > 15)   // trou de mesure : fausse les alertes à maintien
                {
                    lastLate = t0;
                    Paths.Log("Mesure en retard : " + (gap / 1000).ToString("0.0") + " s au lieu de " + (IntervalMs / 1000.0).ToString("0.#") + " s · phases précédentes : " + _phases);
                }
                try { var s = Collect(t0); SampleReady?.Invoke(s); }
                catch (Exception ex) { Paths.Log("Sample: " + ex.Message); }
                var el = (DateTime.Now - t0).TotalMilliseconds;
                int period = IntervalMs;
                if (el < period) Thread.Sleep((int)(period - el));
            }
        }

        /// <summary>Chronométrage des phases : renseigné à chaque mesure, journalisé quand une mesure prend trop de temps.</summary>
        readonly System.Diagnostics.Stopwatch _phase = new System.Diagnostics.Stopwatch();
        string _phases;
        void Lap(System.Text.StringBuilder sb, string name) { sb.Append(name).Append('=').Append(_phase.ElapsedMilliseconds).Append("ms "); _phase.Restart(); }

        Sample Collect(DateTime now)
        {
            var sb = new System.Text.StringBuilder(); _phase.Restart();
            var s = new Sample { Time = now, ts = now.ToString("yyyy-MM-ddTHH:mm:ss"), TotalMB = _totalMB };
            // CPU et mémoire : API noyau en processus (quelques microsecondes). WMI mettait jusqu'à 19 s à répondre
            // sur une machine saturée, ce qui trouait l'historique et retardait les alertes à maintien.
            s.cpu = Native.CpuPercent();
            Native.Memory(out double totalMB, out double availMB);
            if (totalMB > 0) { _totalMB = totalMB; s.TotalMB = totalMB; }
            s.memMB = Math.Max(0, _totalMB - availMB);
            s.memPct = _totalMB > 0 ? Math.Round(s.memMB * 100 / _totalMB, 1) : 0;
            // disques, réseau et pression mémoire : toujours WMI, mais mesurés par un thread dédié — on lit sa dernière publication
            var w = _wmi;
            if (w != null) { s.disks = w.Disks; s.rx = w.Rx; s.tx = w.Tx; s.pageIn = w.PageIn; }
            Lap(sb, "wmi");

            s.procs = _lastProcs;
            s.Leaks = Leaks.Current;
            Lap(sb, "procs");
            s.bat = _bat; s.ac = _ac;
            Lap(sb, "bat");
            s.rb = _rebootCache;
            Lap(sb, "reboot");

            var r = _sensors ?? new Hardware.Reading();
            Lap(sb, "capteurs");
            _phases = sb.ToString();
            s.temp = r.Cpu; s.gpu = r.Gpu; s.cpuMHz = r.CpuMHz; s.gpuMHz = r.GpuMHz; s.cpuW = r.CpuW; s.gpuW = r.GpuW;
            s.fans = r.Fans; s.stor = r.Storage;
            if (!Hw.Available && (!_tempTried || _tempAvailable))
            {
                try
                {
                    foreach (var o in Query("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", @"root\wmi"))
                    { s.temp = Math.Round(D(o["CurrentTemperature"]) / 10.0 - 273.15, 1); _tempAvailable = true; break; }
                }
                catch { _tempAvailable = false; }
                _tempTried = true;
            }
            return s;
        }

        // --- Processus via l'API .NET (bien moins coûteux que Win32_PerfFormattedData_PerfProc_Process) : % CPU = delta temps CPU / delta temps réel
        readonly Dictionary<int, KeyValuePair<DateTime, TimeSpan>> _procCpu = new Dictionary<int, KeyValuePair<DateTime, TimeSpan>>();
        readonly int _selfPid = System.Diagnostics.Process.GetCurrentProcess().Id;
        List<ProcSample> CollectProcesses(DateTime now)
        {
            var list = new List<ProcSample>();
            var seen = new HashSet<int>();
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        int pid = p.Id; if (pid == 0) continue;
                        seen.Add(pid);
                        TimeSpan cpuT; try { cpuT = p.TotalProcessorTime; } catch { cpuT = TimeSpan.Zero; }
                        double pct = 0;
                        if (_procCpu.TryGetValue(pid, out var prev))
                        {
                            var wall = (now - prev.Key).TotalSeconds;
                            if (wall > 0.5) pct = Math.Max(0, (cpuT - prev.Value).TotalSeconds / wall / _cores * 100);
                        }
                        _procCpu[pid] = new KeyValuePair<DateTime, TimeSpan>(now, cpuT);
                        double memMB = p.PrivateMemorySize64 / 1048576.0;
                        list.Add(new ProcSample { n = p.ProcessName, Pid = pid, cpu = Math.Round(pct, 1), mem = Math.Round(memMB), h = p.HandleCount });
                        if (pid == _selfPid) { SelfCpuPct = Math.Round(pct, 2); SelfMemMB = Math.Round(memMB); }
                    }
                    catch { }
                }
            }
            foreach (var dead in _procCpu.Keys.Where(k => !seen.Contains(k)).ToList()) _procCpu.Remove(dead);
            // regroupe les instances d'un même programme (comme WMI ramenait chrome#1, chrome#2… à « chrome ») : le top et le détecteur de fuites suivent le programme
            return list.GroupBy(x => x.n).Select(g => new ProcSample { n = g.Key, Pid = g.OrderByDescending(x => x.cpu).First().Pid, cpu = Math.Round(g.Sum(x => x.cpu), 1), mem = g.Sum(x => x.mem), h = g.Sum(x => x.h) }).ToList();
        }

        /// <summary>Batterie (portables) : Win32_Battery toutes les 20 s ; ignoré si absente.</summary>
        /// <summary>Batterie : requête WMI toutes les 20 s (jusqu'à 5 s de réponse sous charge), appelée par le thread lent.</summary>
        void RefreshBattery(DateTime now)
        {
            if (_batAbsent) return;
            if ((now - _batTime).TotalSeconds >= 20)
            {
                _batTime = now; bool found = false;
                try
                {
                    foreach (var o in Query("SELECT EstimatedChargeRemaining,BatteryStatus FROM Win32_Battery"))
                    { found = true; _bat = D(o["EstimatedChargeRemaining"]); var st = (int)D(o["BatteryStatus"]); _ac = st == 2 || st >= 6 && st <= 9; break; }
                }
                catch { }
                if (!found) { _batAbsent = true; _bat = null; _ac = null; }
            }
        }

        static double D(object v) { if (v == null) return 0; try { return Convert.ToDouble(v); } catch { return 0; } }
        static IEnumerable<ManagementObject> Query(string wql, string ns = @"root\cimv2")
        {
            using (var s = new ManagementObjectSearcher(ns, wql))
            using (var c = s.Get())
                foreach (ManagementObject o in c) { using (o) yield return o; }
        }
        public void Dispose() { _run = false; Hw.Dispose(); }
    }
}
