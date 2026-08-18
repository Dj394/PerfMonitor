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
        /// <summary>Depuis la version 0.9.5, LibreHardwareMonitor n'embarque plus de pilote noyau : l'accès MSR (températures et
        /// fréquences CPU, consommation) et SuperIO (ventilateurs) passe par PawnIO, à installer séparément (https://pawnio.eu).
        /// Sans lui, seuls les capteurs lisibles en espace utilisateur répondent (GPU via NVAPI/ADL, SMART via Windows).</summary>
        public static bool PawnIOInstalled
        {
            get
            {
                if (_pawn.HasValue) return _pawn.Value;
                bool ok = false;
                try
                {
                    ok = System.IO.File.Exists(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll"))
                      || System.IO.File.Exists(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "PawnIO.sys"));
                    if (!ok) using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO")) ok = k != null;
                }
                catch { }
                _pawn = ok; return ok;
            }
        }
        static bool? _pawn;

        /// <summary>Raison affichée sur une carte dont la mesure manque.</summary>
        public string MissingReason => !Available ? Status : PawnIOInstalled ? "capteur introuvable" : "pilote PawnIO absent (pawnio.eu)";

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
                if (!PawnIOInstalled) Paths.Log("PawnIO absent : températures/fréquences/consommation CPU et ventilateurs indisponibles (LibreHardwareMonitor 0.9.6 n'embarque plus de pilote noyau — https://pawnio.eu)");
                foreach (var h in _pc.Hardware)
                {
                    h.Update();
                    Note(h);
                    Paths.Log("Capteur " + h.HardwareType + " [" + Clean(h.Name) + "] : " + string.Join(" | ", h.Sensors.Where(x => x.SensorType == SensorType.Temperature || x.SensorType == SensorType.Fan || x.SensorType == SensorType.Clock || x.SensorType == SensorType.Power).Select(x => x.SensorType + ":" + x.Name + "=" + x.Value)));
                    foreach (var sub in h.SubHardware) { sub.Update(); Note(sub); Paths.Log("  sous-capteur " + sub.HardwareType + " [" + Clean(sub.Name) + "] : " + string.Join(" | ", sub.Sensors.Where(x => x.SensorType == SensorType.Fan || x.SensorType == SensorType.Temperature).Select(x => x.SensorType + ":" + x.Name + "=" + x.Value))); }
                }
                return true;
            }
            catch (Exception ex) { Status = "erreur capteurs : " + ex.Message; Paths.Log("LHM: " + ex); Available = false; return false; }
        }

        /// <summary>Capteurs vus au moins une fois (à l'initialisation ou pendant une mesure). Un GPU hybride (Optimus)
        /// répond quand il est actif puis s'endort : la carte doit rester affichée, avec « en veille » à la place de la valeur.</summary>
        public bool SawCpuTemp, SawCpuClock, SawCpuPower, SawGpuTemp, SawGpuClock, SawGpuPower, SawFans;

        void Note(IHardware h)
        {
            bool isGpu = h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuNvidia || h.HardwareType == HardwareType.GpuIntel;
            if (isGpu && GpuRank(h) < GpuWanted()) return;   // GPU secondaire (intégré d'un portable hybride) : pas suivi
            bool isCpu = h.HardwareType == HardwareType.Cpu;
            foreach (var s in h.Sensors)
            {
                if (!s.Value.HasValue || s.Value.Value <= 0) continue;
                switch (s.SensorType)
                {
                    case SensorType.Temperature:
                        if (s.Value.Value < 150) { if (isGpu) SawGpuTemp = true; else if (isCpu) SawCpuTemp = true; }
                        break;
                    case SensorType.Fan: SawFans = true; break;
                    case SensorType.Clock: if (isGpu) SawGpuClock = true; else if (isCpu) SawCpuClock = true; break;
                    case SensorType.Power: if (isGpu) SawGpuPower = true; else if (isCpu) SawCpuPower = true; break;
                }
            }
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
                                // AMD nomme ses capteurs « Core #1 », Intel « CPU Core #1 » — et il ne faut pas prendre « Bus Speed »
                                r.CpuMHz = Max(h, SensorType.Clock, x => x.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0);
                                r.CpuW = Pick(h, SensorType.Power, "Package", "CPU Package", "CPU Cores") ?? Max(h, SensorType.Power);
                                break;
                            case HardwareType.GpuAmd: case HardwareType.GpuNvidia: case HardwareType.GpuIntel:
                                if (GpuRank(h) < GpuWanted()) break;   // portable hybride : ignorer l'intégré, qui écraserait la carte dédiée
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
            if (r.Cpu.HasValue) SawCpuTemp = true;
            if (r.Gpu.HasValue) SawGpuTemp = true;
            if (r.CpuMHz.HasValue) SawCpuClock = true;
            if (r.GpuMHz.HasValue) SawGpuClock = true;
            if (r.CpuW.HasValue) SawCpuPower = true;
            if (r.GpuW.HasValue) SawGpuPower = true;
            if (r.Fans.Any(f => f.rpm > 0)) SawFans = true;
            ReadStorageWmi(r);
            return r;
        }

        int _gpuWant = -1;
        /// <summary>Rang d'un GPU : 2 = carte dédiée reconnue dans l'inventaire, 1 = dédiée supposée, 0 = intégrée.
        /// Sur un portable hybride, le GPU intégré répond en permanence : sans priorité il écrase les mesures de la carte dédiée.</summary>
        static int GpuRank(IHardware h)
        {
            var name = Clean(h.Name); var inv = Inventory.Current;
            if (inv != null && inv.Gpus != null)
                foreach (var g in inv.Gpus)
                    if (!string.IsNullOrEmpty(g.Name) && (g.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf(g.Name, StringComparison.OrdinalIgnoreCase) >= 0))
                        return g.Integrated ? 0 : 2;
            return h.HardwareType == HardwareType.GpuIntel ? 0 : 1;
        }
        /// <summary>Rang du GPU à suivre : le meilleur présent sur la machine (une machine sans carte dédiée suit son GPU intégré).</summary>
        int GpuWanted()
        {
            if (_gpuWant < 0)
                foreach (var h in _pc.Hardware)
                    if (h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuNvidia || h.HardwareType == HardwareType.GpuIntel)
                        _gpuWant = Math.Max(_gpuWant, GpuRank(h));
            return _gpuWant < 0 ? 0 : _gpuWant;
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
            // 0 = capteur présent mais non renseigné (portable Intel sans pilote noyau : Power:CPU Package=0) → pas une mesure
            var list = h.Sensors.Where(s => s.SensorType == type && s.Value.HasValue && s.Value.Value > 0).ToList();
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
