using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using PerfMonitorLive.Alerts;

namespace PerfMonitorLive.Metrics
{
    /// <summary>Collecte les compteurs via WMI (indépendant de la langue) + capteurs matériels + score de redémarrage + fuites.</summary>
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
        List<ProcSample> _lastProcs = new List<ProcSample>();
        DateTime _lastProcTime = DateTime.MinValue;
        bool _tempTried, _tempAvailable;
        DateTime _batTime = DateTime.MinValue; double? _bat; bool? _ac; bool _batAbsent;
        public Hardware Hw { get; } = new Hardware();
        public LeakDetector Leaks { get; } = new LeakDetector();
        readonly RebootCheck _reboot = new RebootCheck();
        static readonly Regex ProcSuffix = new Regex(@"#\d+$", RegexOptions.Compiled);
        static readonly Regex NetSkip = new Regex("isatap|Loopback|Teredo", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public Sampler()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Sampler", Priority = ThreadPriority.BelowNormal };
        }
        public void Start() => _thread.Start();

        void Loop()
        {
            try { foreach (var o in Query("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem")) _totalMB = Math.Round(D(o["TotalVisibleMemorySize"]) / 1024); }
            catch (Exception ex) { Paths.Log("OS query: " + ex.Message); }
            if (_totalMB <= 0) _totalMB = 1;
            Hw.TryInit();
            while (_run)
            {
                var t0 = DateTime.Now;
                try { var s = Collect(t0); SampleReady?.Invoke(s); }
                catch (Exception ex) { Paths.Log("Sample: " + ex.Message); }
                var el = (DateTime.Now - t0).TotalMilliseconds;
                int period = IntervalMs;
                if (el < period) Thread.Sleep((int)(period - el));
            }
        }

        Sample Collect(DateTime now)
        {
            var s = new Sample { Time = now, ts = now.ToString("yyyy-MM-ddTHH:mm:ss"), TotalMB = _totalMB };
            foreach (var o in Query("SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'"))
                s.cpu = D(o["PercentProcessorTime"]);
            foreach (var o in Query("SELECT AvailableMBytes,PagesInputPersec FROM Win32_PerfFormattedData_PerfOS_Memory"))
            {
                s.memMB = _totalMB - D(o["AvailableMBytes"]);
                s.memPct = Math.Round(s.memMB * 100 / _totalMB, 1);
                s.pageIn = D(o["PagesInputPersec"]);
            }
            foreach (var o in Query("SELECT Name,PercentDiskTime,DiskReadBytesPersec,DiskWriteBytesPersec,AvgDiskQueueLength,AvgDisksecPerTransfer FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk"))
            {
                var name = (string)o["Name"]; if (name == "_Total") continue;
                s.disks.Add(new DiskSample
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
            s.rx = Math.Round(rx / 1024, 1); s.tx = Math.Round(tx / 1024, 1);

            if ((now - _lastProcTime).TotalSeconds >= 5)
            {
                var list = CollectProcesses(now);
                _lastProcs = list.OrderByDescending(p => p.cpu).ThenByDescending(p => p.mem).Take(6).ToList();
                _lastProcTime = now;
                Leaks.Feed(now, list);
            }
            s.procs = _lastProcs;
            s.Leaks = Leaks.Current;
            ReadBattery(now, s);
            try { s.rb = _reboot.Get(now); } catch (Exception ex) { Paths.Log("Reboot: " + ex.Message); }

            var r = Hw.Read();
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
        void ReadBattery(DateTime now, Sample s)
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
            s.bat = _bat; s.ac = _ac;
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
