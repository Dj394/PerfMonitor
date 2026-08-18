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
                if (el < 1000) Thread.Sleep((int)(1000 - el));
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

            if ((now - _lastProcTime).TotalSeconds >= 3)
            {
                var list = new List<ProcSample>();
                foreach (var o in Query("SELECT Name,IDProcess,PercentProcessorTime,WorkingSetPrivate,HandleCount FROM Win32_PerfFormattedData_PerfProc_Process"))
                {
                    var name = (string)o["Name"]; if (name == "_Total" || name == "Idle") continue;
                    list.Add(new ProcSample { n = ProcSuffix.Replace(name, ""), Pid = (int)D(o["IDProcess"]), cpu = Math.Round(D(o["PercentProcessorTime"]) / _cores, 1), mem = Math.Round(D(o["WorkingSetPrivate"]) / 1048576.0), h = D(o["HandleCount"]) });
                }
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
