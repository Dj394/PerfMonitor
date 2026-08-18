using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace PerfMonitorLive.Metrics
{
    /// <summary>Capteurs matériels : températures CPU/GPU, ventilateurs, fréquences, consommation (LibreHardwareMonitor)
    /// et santé/température des disques (compteurs de fiabilité Windows). Nécessite les droits administrateur.</summary>
    public class Hardware : IDisposable
    {
        Computer _pc;
        public bool Available { get; private set; }
        public string Status { get; private set; } = "non initialisé";
        public static bool IsElevated
        {
            get { try { using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); } catch { return false; } }
        }

        public bool TryInit()
        {
            if (!IsElevated) { Status = "droits administrateur requis"; return false; }
            try
            {
                _pc = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true, IsStorageEnabled = false, IsMemoryEnabled = false, IsNetworkEnabled = false, IsControllerEnabled = false };
                _pc.Open();
                Available = true; Status = "ok";
                foreach (var h in _pc.Hardware)
                {
                    h.Update();
                    Paths.Log("Capteur " + h.HardwareType + " [" + Clean(h.Name) + "] : " + string.Join(" | ", h.Sensors.Where(x => x.SensorType == SensorType.Temperature || x.SensorType == SensorType.Fan || x.SensorType == SensorType.Clock || x.SensorType == SensorType.Power).Select(x => x.SensorType + ":" + x.Name + "=" + x.Value)));
                    foreach (var sub in h.SubHardware) { sub.Update(); Paths.Log("  sous-capteur " + sub.HardwareType + " [" + Clean(sub.Name) + "] : " + string.Join(" | ", sub.Sensors.Where(x => x.SensorType == SensorType.Fan || x.SensorType == SensorType.Temperature).Select(x => x.SensorType + ":" + x.Name + "=" + x.Value))); }
                }
                return true;
            }
            catch (Exception ex) { Status = "erreur capteurs : " + ex.Message; Paths.Log("LHM: " + ex); Available = false; return false; }
        }

        public class Reading
        {
            public double? Cpu, Gpu, CpuMHz, GpuMHz, CpuW, GpuW;
            public List<FanSample> Fans = new List<FanSample>();
            public List<StorSample> Storage = new List<StorSample>();
        }

        /// <summary>Lit tous les capteurs (LHM) + santé disques (Windows, toutes les 5 s).</summary>
        public Reading Read()
        {
            var r = new Reading();
            if (Available)
            {
                try
                {
                    bool mbDue = (DateTime.Now - _mbTime).TotalSeconds >= 5;   // carte mère (ventilateurs) : lecture SuperIO coûteuse, toutes les 5 s
                    foreach (var h in _pc.Hardware)
                    {
                        if (h.HardwareType == HardwareType.Motherboard)
                        {
                            if (mbDue)
                            {
                                h.Update(); foreach (var sub in h.SubHardware) sub.Update();
                                _mbTime = DateTime.Now; var tmp = new Reading();
                                AddFans(h, "", tmp); foreach (var sub in h.SubHardware) AddFans(sub, "", tmp);
                                _mbFans = tmp.Fans;
                            }
                            r.Fans.AddRange(_mbFans);
                            continue;
                        }
                        h.Update();
                        switch (h.HardwareType)
                        {
                            case HardwareType.Cpu:
                                r.Cpu = Pick(h, SensorType.Temperature, "Core (Tctl/Tdie)", "Tctl/Tdie", "CCD1 (Tdie)", "Core (Tctl)", "CPU Package", "Package", "Core Average", "Core Max") ?? Max(h, SensorType.Temperature);
                                r.CpuMHz = Max(h, SensorType.Clock, x => x.Name.StartsWith("Core"));
                                r.CpuW = Pick(h, SensorType.Power, "Package", "CPU Package", "CPU Cores") ?? Max(h, SensorType.Power);
                                break;
                            case HardwareType.GpuAmd: case HardwareType.GpuNvidia: case HardwareType.GpuIntel:
                                r.Gpu = Pick(h, SensorType.Temperature, "GPU Core", "GPU Hot Spot") ?? Max(h, SensorType.Temperature);
                                r.GpuMHz = Pick(h, SensorType.Clock, "GPU Core") ?? Max(h, SensorType.Clock);
                                r.GpuW = Pick(h, SensorType.Power, "GPU Package", "GPU Core", "GPU Total") ?? Max(h, SensorType.Power);
                                AddFans(h, "GPU", r);
                                break;
                        }
                    }
                }
                catch (Exception ex) { Paths.Log("LHM read: " + ex.Message); }
            }
            ReadStorageWmi(r);
            return r;
        }

        DateTime _mbTime = DateTime.MinValue; List<FanSample> _mbFans = new List<FanSample>();
        static void AddFans(IHardware h, string prefix, Reading r)
        {
            foreach (var s in h.Sensors.Where(x => x.SensorType == SensorType.Fan && x.Value.HasValue))
            {
                var name = prefix.Length > 0 && !s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? prefix + " " + s.Name : s.Name;
                name = name.Replace("Fan #", "n°");
                if (r.Fans.Any(f => f.n == name)) name += " (" + Clean(h.Name) + ")";
                r.Fans.Add(new FanSample { n = name, rpm = (int)Math.Round(s.Value.Value) });
            }
        }
        static string Clean(string n) => (n ?? "").Replace("\0", "").Trim();
        static double? Pick(IHardware h, SensorType type, params string[] names)
        {
            var list = h.Sensors.Where(s => s.SensorType == type && s.Value.HasValue).ToList();
            foreach (var n in names) { var s = list.FirstOrDefault(x => x.Name == n); if (s != null) return Math.Round(s.Value.Value, 1); }
            return null;
        }
        static double? Max(IHardware h, SensorType type, Func<ISensor, bool> filter = null)
        {
            var list = h.Sensors.Where(s => s.SensorType == type && s.Value.HasValue && s.Value.Value > 0 && (type != SensorType.Temperature || s.Value.Value < 150) && (filter == null || filter(s))).ToList();
            return list.Count == 0 ? (double?)null : Math.Round(list.Max(s => s.Value.Value), 1);
        }

        // --- Santé & température disques via Windows (MSFT_PhysicalDisk + MSFT_StorageReliabilityCounter, admin requis)
        List<StorSample> _storCache = new List<StorSample>();
        DateTime _storTime = DateTime.MinValue;
        Dictionary<string, string> _diskNames;
        int _diag;
        void ReadStorageWmi(Reading r)
        {
            if (!IsElevated) return;
            if ((DateTime.Now - _storTime).TotalSeconds >= 30)   // SMART : lent à évoluer, toutes les 30 s
            {
                _storTime = DateTime.Now;
                var list = new List<StorSample>();
                try
                {
                    if (_diskNames == null) _diskNames = DiskNames();
                    using (var s = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk"))
                        foreach (System.Management.ManagementObject pd in s.Get())
                        {
                            var id = pd["DeviceId"] as string; if (id == null) continue;
                            var st = new StorSample { n = _diskNames.TryGetValue(id, out var n) ? n : "Disque " + id, health = ToInt(pd["HealthStatus"]) };
                            using (var rel = pd.GetRelated("MSFT_StorageReliabilityCounter"))
                                foreach (System.Management.ManagementObject o in rel)
                                {
                                    double t = ToD(o["Temperature"]);
                                    if (t > 0 && t < 150) st.t = t;
                                    st.tmax = ToD(o["TemperatureMax"]);
                                    st.wear = ToD(o["Wear"]);
                                    st.hours = ToD(o["PowerOnHours"]);
                                    st.rerr = ToD(o["ReadErrorsTotal"]); st.rerrU = ToD(o["ReadErrorsUncorrected"]);
                                    st.werr = ToD(o["WriteErrorsTotal"]); st.werrU = ToD(o["WriteErrorsUncorrected"]);
                                    st.starts = ToD(o["StartStopCycleCount"]);
                                }
                            list.Add(st);
                        }
                }
                catch (Exception ex) { if (_diag++ < 3) Paths.Log("Storage WMI: " + ex.Message); }
                _storCache = list;
            }
            r.Storage.AddRange(_storCache);
        }
        static double ToD(object v) { if (v == null) return 0; try { return Convert.ToDouble(v); } catch { return 0; } }
        static int ToInt(object v) { if (v == null) return 0; try { return Convert.ToInt32(v); } catch { return 0; } }

        static Dictionary<string, string> DiskNames()
        {
            var names = new Dictionary<string, string>();
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DeviceId,FriendlyName FROM MSFT_PhysicalDisk"))
                    foreach (System.Management.ManagementObject o in s.Get()) names[(string)o["DeviceId"]] = ((o["FriendlyName"] as string) ?? "").Replace("NVMe ", "").Trim();
                using (var s = new System.Management.ManagementObjectSearcher("SELECT DeviceID,Index FROM Win32_DiskDrive"))
                    foreach (System.Management.ManagementObject d in s.Get())
                    {
                        var idx = Convert.ToInt32(d["Index"]).ToString(); var letters = new List<string>();
                        using (var ps = new System.Management.ManagementObjectSearcher("ASSOCIATORS OF {Win32_DiskDrive.DeviceID='" + d["DeviceID"] + "'} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
                            foreach (System.Management.ManagementObject part in ps.Get())
                                using (var ls = new System.Management.ManagementObjectSearcher("ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + part["DeviceID"] + "'} WHERE AssocClass=Win32_LogicalDiskToPartition"))
                                    foreach (System.Management.ManagementObject ld in ls.Get()) letters.Add((string)ld["DeviceID"]);
                        if (letters.Count > 0 && names.ContainsKey(idx)) names[idx] = names[idx] + " (" + string.Join(", ", letters) + ")";
                    }
            }
            catch (Exception ex) { Paths.Log("DiskNames: " + ex.Message); }
            return names;
        }
        public void Dispose() { try { _pc?.Close(); } catch { } }
    }
}
