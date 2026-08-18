using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfMonitorLive.Metrics
{
    public enum Vendor { Unknown, Amd, Intel, Nvidia, Qualcomm, Apple }
    public enum DiskKind { Unknown, Nvme, Ssd, Hdd, Usb }

    public class CpuInfo
    {
        public string Name { get; set; } = "?";
        public Vendor Vendor { get; set; }
        public int Cores { get; set; }
        public int Threads { get; set; }
        public int MaxMHz { get; set; }
        [JsonIgnore] public bool IsRyzen => Vendor == Vendor.Amd && Name.IndexOf("Ryzen", StringComparison.OrdinalIgnoreCase) >= 0;
        [JsonIgnore] public string Short => Shorten(Name);
        internal static string Shorten(string n) => (n ?? "").Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "").Replace("CPU", "").Replace("Processor", "").Replace("with Radeon Graphics", "").Replace("  ", " ").Trim().TrimEnd('@').Trim();
    }
    public class GpuInfo
    {
        public string Name { get; set; } = "?";
        public Vendor Vendor { get; set; }
        public double VramGB { get; set; }
        public string Driver { get; set; }
        public bool Integrated { get; set; }
        [JsonIgnore] public string Short => CpuInfo.Shorten(Name);
    }
    public class RamModule { public double SizeGB { get; set; } public int SpeedMHz { get; set; } public int ConfiguredMHz { get; set; } public string Type { get; set; } public string Slot { get; set; } public string Maker { get; set; } }
    public class DiskInfo
    {
        public int Index { get; set; }
        public string Model { get; set; } = "?";
        public DiskKind Kind { get; set; }
        public string Bus { get; set; }
        public double SizeGB { get; set; }
        public List<string> Letters { get; set; } = new List<string>();
        public bool System { get; set; }
        [JsonIgnore] public string KindText => Kind == DiskKind.Nvme ? "SSD NVMe" : Kind == DiskKind.Ssd ? "SSD" : Kind == DiskKind.Hdd ? "Disque dur (HDD)" : Kind == DiskKind.Usb ? "USB" : "Disque";
        [JsonIgnore] public bool IsSsd => Kind == DiskKind.Nvme || Kind == DiskKind.Ssd;
        /// <summary>Nom du compteur PerfDisk correspondant ("0 C:", "1 D:")</summary>
        [JsonIgnore] public string PerfName => Index + (Letters.Count > 0 ? " " + string.Join(" ", Letters) : "");
    }
    public class ScreenInfo { public string Name { get; set; } public int W { get; set; } public int H { get; set; } public bool Primary { get; set; } }
    public class NetInfo { public string Name { get; set; } public double SpeedMbps { get; set; } public bool Wireless { get; set; } }

    /// <summary>Photographie du matériel de la machine (scan WMI au démarrage, ~1–2 s, ne nécessite pas les droits admin).</summary>
    public class MachineInfo
    {
        public string Host { get; set; }
        public string Os { get; set; }
        public string OsBuild { get; set; }
        public bool Laptop { get; set; }
        public bool HasBattery { get; set; }
        public string Chassis { get; set; }
        public string Maker { get; set; }
        public string Model { get; set; }
        public CpuInfo Cpu { get; set; } = new CpuInfo();
        public List<GpuInfo> Gpus { get; set; } = new List<GpuInfo>();
        public double RamGB { get; set; }
        public string RamType { get; set; }
        public int RamMHz { get; set; }
        public List<RamModule> RamModules { get; set; } = new List<RamModule>();
        public string Board { get; set; }
        public string BoardMaker { get; set; }
        public string Bios { get; set; }
        public string BiosDate { get; set; }
        public List<DiskInfo> Disks { get; set; } = new List<DiskInfo>();
        public List<ScreenInfo> Screens { get; set; } = new List<ScreenInfo>();
        public List<NetInfo> Nets { get; set; } = new List<NetInfo>();
        public DateTime ScannedAt { get; set; }
        public int ScanMs { get; set; }

        // --- Capteurs effectivement disponibles (renseigné après les premières mesures)
        public bool CapCpuTemp { get; set; }
        public bool CapGpuTemp { get; set; }
        public bool CapFans { get; set; }
        public bool CapStorTemp { get; set; }
        public bool CapPower { get; set; }
        public bool CapClocks { get; set; }
        public bool Elevated { get; set; }

        [JsonIgnore] public GpuInfo MainGpu => Gpus.FirstOrDefault(g => !g.Integrated) ?? Gpus.FirstOrDefault();
        [JsonIgnore] public bool HasDedicatedGpu => Gpus.Any(g => !g.Integrated);
        [JsonIgnore] public Vendor GpuVendor => MainGpu?.Vendor ?? Vendor.Unknown;
        [JsonIgnore] public DiskInfo SystemDisk => Disks.FirstOrDefault(d => d.System) ?? Disks.FirstOrDefault();
        [JsonIgnore] public bool SystemOnSsd => SystemDisk == null || SystemDisk.IsSsd;
        [JsonIgnore] public int RamGBRound => (int)Math.Round(RamGB);
        [JsonIgnore] public string RamText => RamGBRound + " Go" + (RamType != null ? " " + RamType : "") + (RamMHz > 0 ? " " + RamMHz + " MHz" : "");
        [JsonIgnore] public string KindText => Laptop ? "portable" : "PC fixe";
        /// <summary>Une seule barrette sur un PC fixe/portable à 2 slots : mémoire en simple canal.</summary>
        [JsonIgnore] public bool SingleChannel => RamModules.Count == 1;

        public DiskInfo DiskByPerfName(string perfName)
        {
            if (string.IsNullOrEmpty(perfName)) return null;
            var idx = perfName.Split(' ')[0];
            return int.TryParse(idx, out var i) ? Disks.FirstOrDefault(d => d.Index == i) : null;
        }
        /// <summary>Retrouve un disque à partir du nom SMART ("KINGSTON SKC3000S1024G (C:)").</summary>
        public DiskInfo DiskByStorName(string storName)
        {
            if (string.IsNullOrEmpty(storName)) return null;
            foreach (var d in Disks)
                if (d.Letters.Count > 0 && d.Letters.All(l => storName.Contains(l))) return d;
            var m = storName.Split('(')[0].Trim();
            return Disks.FirstOrDefault(d => d.Model.Equals(m, StringComparison.OrdinalIgnoreCase) || storName.IndexOf(d.Model, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public string Summary()
        {
            var g = MainGpu;
            return Host + " · " + KindText + " · " + Cpu.Short + " (" + Cpu.Cores + "c/" + Cpu.Threads + "t) · " + (g != null ? g.Short + (g.VramGB > 0 ? " " + g.VramGB.ToString("0") + " Go" : "") : "pas de GPU") + " · RAM " + RamText + " · " + Disks.Count + " disque(s) · " + Screens.Count + " écran(s) · " + Os;
        }

        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter() } };
        public static string File => Path.Combine(Paths.DataDir, "inventory.json");
        public void Save() { try { Directory.CreateDirectory(Paths.DataDir); System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, Opts)); } catch (Exception ex) { Paths.Log("Inventory save: " + ex.Message); } }
        public static MachineInfo LoadCached()
        {
            try { if (System.IO.File.Exists(File)) return JsonSerializer.Deserialize<MachineInfo>(System.IO.File.ReadAllText(File), Opts); } catch { }
            return null;
        }
    }

    public static class Inventory
    {
        public static MachineInfo Current { get; private set; } = MachineInfo.LoadCached() ?? new MachineInfo();
        public static event Action<MachineInfo> Scanned;

        /// <summary>Scan complet (à lancer hors du thread UI).</summary>
        public static MachineInfo Scan()
        {
            var t0 = DateTime.Now;
            var m = new MachineInfo { Host = Environment.MachineName, ScannedAt = t0, Elevated = Hardware.IsElevated };
            Try("os", () =>
            {
                foreach (var o in Q("SELECT Caption,Version,BuildNumber,TotalVisibleMemorySize FROM Win32_OperatingSystem"))
                { m.Os = ((string)o["Caption"] ?? "").Replace("Microsoft ", "").Trim(); m.OsBuild = (string)o["Version"]; if (m.RamGB <= 0) m.RamGB = Math.Round(D(o["TotalVisibleMemorySize"]) / 1048576.0, 1); }
            });
            Try("cs", () =>
            {
                foreach (var o in Q("SELECT Manufacturer,Model,PCSystemType,TotalPhysicalMemory FROM Win32_ComputerSystem"))
                {
                    m.Maker = Clean((string)o["Manufacturer"]); m.Model = Clean((string)o["Model"]);
                    var type = (int)D(o["PCSystemType"]);   // 2 = Mobile
                    if (type == 2) m.Laptop = true;
                    var total = D(o["TotalPhysicalMemory"]) / 1073741824.0;
                    if (total > 0) m.RamGB = Math.Round(total, 1);
                }
            });
            Try("chassis", () =>
            {
                foreach (var o in Q("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
                {
                    var arr = o["ChassisTypes"] as ushort[]; if (arr == null || arr.Length == 0) continue;
                    int c = arr[0]; m.Chassis = ChassisName(c);
                    if (c == 8 || c == 9 || c == 10 || c == 11 || c == 12 || c == 14 || c == 18 || c == 21 || c == 30 || c == 31 || c == 32) m.Laptop = true;
                }
            });
            Try("battery", () => { foreach (var o in Q("SELECT BatteryStatus FROM Win32_Battery")) { m.HasBattery = true; m.Laptop = true; } });
            Try("cpu", () =>
            {
                foreach (var o in Q("SELECT Name,Manufacturer,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed FROM Win32_Processor"))
                {
                    m.Cpu.Name = Clean((string)o["Name"]);
                    m.Cpu.Vendor = VendorOf((string)o["Manufacturer"] + " " + m.Cpu.Name);
                    m.Cpu.Cores += (int)D(o["NumberOfCores"]); m.Cpu.Threads += (int)D(o["NumberOfLogicalProcessors"]);
                    m.Cpu.MaxMHz = Math.Max(m.Cpu.MaxMHz, (int)D(o["MaxClockSpeed"]));
                }
            });
            Try("gpu", () =>
            {
                foreach (var o in Q("SELECT Name,AdapterRAM,DriverVersion,AdapterCompatibility,PNPDeviceID FROM Win32_VideoController"))
                {
                    var name = Clean((string)o["Name"]); if (name.Length == 0) continue;
                    var pnp = ((string)o["PNPDeviceID"] ?? "").ToUpperInvariant();
                    if (!pnp.StartsWith("PCI") && pnp.IndexOf("ROOT", StringComparison.Ordinal) >= 0) continue;   // adaptateurs virtuels (RDP, Parsec, DisplayLink…)
                    if (name.IndexOf("Remote", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Basic Display", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var g = new GpuInfo { Name = name, Vendor = VendorOf((string)o["AdapterCompatibility"] + " " + name), Driver = (string)o["DriverVersion"] };
                    g.VramGB = Math.Round(D(o["AdapterRAM"]) / 1073741824.0, 1);
                    g.Integrated = IsIntegrated(name, g.Vendor);
                    m.Gpus.Add(g);
                }
                // VRAM > 4 Go : Win32_VideoController plafonne à 4 Go (uint32) → registre
                foreach (var g in m.Gpus.Where(x => x.VramGB >= 3.9)) g.VramGB = Math.Max(g.VramGB, VramFromRegistry(g.Name));
            });
            Try("ram", () =>
            {
                foreach (var o in Q("SELECT Capacity,Speed,ConfiguredClockSpeed,SMBIOSMemoryType,MemoryType,DeviceLocator,Manufacturer FROM Win32_PhysicalMemory"))
                {
                    var mod = new RamModule { SizeGB = Math.Round(D(o["Capacity"]) / 1073741824.0, 1), SpeedMHz = (int)D(o["Speed"]), ConfiguredMHz = (int)D(o["ConfiguredClockSpeed"]), Slot = (string)o["DeviceLocator"], Maker = Clean((string)o["Manufacturer"]) };
                    mod.Type = RamType((int)D(o["SMBIOSMemoryType"]), (int)D(o["MemoryType"]));
                    m.RamModules.Add(mod);
                }
                if (m.RamModules.Count > 0)
                {
                    m.RamType = m.RamModules.Select(x => x.Type).FirstOrDefault(x => x != null);
                    m.RamMHz = m.RamModules.Max(x => x.ConfiguredMHz > 0 ? x.ConfiguredMHz : x.SpeedMHz);
                }
            });
            Try("board", () =>
            {
                foreach (var o in Q("SELECT Manufacturer,Product FROM Win32_BaseBoard")) { m.BoardMaker = Clean((string)o["Manufacturer"]); m.Board = Clean((string)o["Product"]); }
                foreach (var o in Q("SELECT SMBIOSBIOSVersion,ReleaseDate FROM Win32_BIOS"))
                {
                    m.Bios = (string)o["SMBIOSBIOSVersion"];
                    var rd = (string)o["ReleaseDate"]; if (rd != null && rd.Length >= 8) m.BiosDate = rd.Substring(6, 2) + "/" + rd.Substring(4, 2) + "/" + rd.Substring(0, 4);
                }
            });
            Try("disks", () =>
            {
                var sysLetter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
                var kinds = new Dictionary<string, Tuple<DiskKind, string>>();
                try
                {
                    using (var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DeviceId,MediaType,BusType,FriendlyName FROM MSFT_PhysicalDisk"))
                        foreach (ManagementObject o in s.Get())
                        {
                            int media = (int)D(o["MediaType"]), bus = (int)D(o["BusType"]);
                            string busName = BusName(bus);
                            var kind = bus == 17 ? DiskKind.Nvme : bus == 7 ? DiskKind.Usb : media == 4 ? DiskKind.Ssd : media == 3 ? DiskKind.Hdd : DiskKind.Unknown;
                            kinds[(string)o["DeviceId"]] = Tuple.Create(kind, busName);
                        }
                }
                catch (Exception ex) { Paths.Log("Inventory MSFT_PhysicalDisk: " + ex.Message); }
                foreach (var d in Q("SELECT DeviceID,Index,Model,Size,InterfaceType,MediaType FROM Win32_DiskDrive"))
                {
                    var di = new DiskInfo { Index = (int)D(d["Index"]), Model = CleanModel((string)d["Model"]), SizeGB = Math.Round(D(d["Size"]) / 1e9), Bus = (string)d["InterfaceType"] };
                    if (kinds.TryGetValue(di.Index.ToString(), out var k)) { di.Kind = k.Item1; di.Bus = k.Item2; }
                    if (di.Kind == DiskKind.Unknown)
                    {
                        var mt = ((string)d["MediaType"] ?? "") + " " + di.Model + " " + di.Bus;
                        if (di.Bus != null && di.Bus.Equals("USB", StringComparison.OrdinalIgnoreCase)) di.Kind = DiskKind.Usb;
                        else if (mt.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0) di.Kind = DiskKind.Nvme;
                        else if (mt.IndexOf("SSD", StringComparison.OrdinalIgnoreCase) >= 0) di.Kind = DiskKind.Ssd;
                    }
                    try
                    {
                        using (var ps = new ManagementObjectSearcher("ASSOCIATORS OF {Win32_DiskDrive.DeviceID='" + d["DeviceID"] + "'} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
                            foreach (ManagementObject part in ps.Get())
                                using (var ls = new ManagementObjectSearcher("ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + part["DeviceID"] + "'} WHERE AssocClass=Win32_LogicalDiskToPartition"))
                                    foreach (ManagementObject ld in ls.Get()) di.Letters.Add((string)ld["DeviceID"]);
                    }
                    catch { }
                    di.System = di.Letters.Contains(sysLetter);
                    m.Disks.Add(di);
                }
                m.Disks = m.Disks.OrderBy(x => x.Index).ToList();
                if (!m.Disks.Any(x => x.System) && m.Disks.Count > 0) m.Disks[0].System = true;
            });
            Try("screens", () =>
            {
                foreach (var s in System.Windows.Forms.Screen.AllScreens)
                    m.Screens.Add(new ScreenInfo { Name = s.DeviceName.Replace(@"\\.\", ""), W = s.Bounds.Width, H = s.Bounds.Height, Primary = s.Primary });
            });
            Try("net", () =>
            {
                foreach (var o in Q("SELECT Name,Speed,NetConnectionStatus,PhysicalAdapter,AdapterTypeID FROM Win32_NetworkAdapter WHERE PhysicalAdapter=TRUE AND NetConnectionStatus=2"))
                {
                    var name = Clean((string)o["Name"]);
                    if (name.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("VPN", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    m.Nets.Add(new NetInfo { Name = name, SpeedMbps = Math.Round(D(o["Speed"]) / 1e6), Wireless = (int)D(o["AdapterTypeID"]) == 9 || name.IndexOf("Wi-Fi", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Wireless", StringComparison.OrdinalIgnoreCase) >= 0 });
                }
            });
            m.ScanMs = (int)(DateTime.Now - t0).TotalMilliseconds;
            // conserve les capacités capteurs déjà connues (le scan matériel ne les mesure pas)
            var prev = Current;
            if (prev != null) { m.CapCpuTemp = prev.CapCpuTemp; m.CapGpuTemp = prev.CapGpuTemp; m.CapFans = prev.CapFans; m.CapStorTemp = prev.CapStorTemp; m.CapPower = prev.CapPower; m.CapClocks = prev.CapClocks; }
            Current = m;
            m.Save();
            Paths.Log("Inventaire (" + m.ScanMs + " ms) : " + m.Summary());
            foreach (var d in m.Disks) Paths.Log("  disque " + d.Index + " : " + d.Model + " · " + d.KindText + " · " + d.Bus + " · " + d.SizeGB + " Go · " + string.Join(",", d.Letters) + (d.System ? " · système" : ""));
            foreach (var g in m.Gpus) Paths.Log("  GPU : " + g.Name + " · " + g.Vendor + " · " + g.VramGB + " Go" + (g.Integrated ? " · intégré" : "") + " · pilote " + g.Driver);
            Scanned?.Invoke(m);
            return m;
        }

        /// <summary>Met à jour les capacités capteurs à partir d'un échantillon (appelé pendant les premières minutes).</summary>
        public static bool UpdateCapabilities(Sample s, bool elevated)
        {
            var m = Current; bool ch = false;
            void Set(ref bool f, bool v) { if (v && !f) { f = true; ch = true; } }
            bool a = m.CapCpuTemp, b = m.CapGpuTemp, c = m.CapFans, d = m.CapStorTemp, e = m.CapPower, f = m.CapClocks;
            Set(ref a, s.temp.HasValue); Set(ref b, s.gpu.HasValue); Set(ref c, s.fans != null && s.fans.Any(x => x.rpm > 0));
            Set(ref d, s.stor != null && s.stor.Any(x => x.t > 0)); Set(ref e, s.cpuW.HasValue || s.gpuW.HasValue); Set(ref f, s.cpuMHz.HasValue || s.gpuMHz.HasValue);
            m.CapCpuTemp = a; m.CapGpuTemp = b; m.CapFans = c; m.CapStorTemp = d; m.CapPower = e; m.CapClocks = f;
            if (m.Elevated != elevated) { m.Elevated = elevated; ch = true; }
            if (ch) m.Save();
            return ch;
        }

        // --- helpers
        static void Try(string what, Action a) { try { a(); } catch (Exception ex) { Paths.Log("Inventory " + what + ": " + ex.Message); } }
        static IEnumerable<ManagementObject> Q(string wql)
        {
            using (var s = new ManagementObjectSearcher(wql)) using (var c = s.Get()) foreach (ManagementObject o in c) { using (o) yield return o; }
        }
        static double D(object v) { if (v == null) return 0; try { return Convert.ToDouble(v); } catch { return 0; } }
        static string Clean(string s) => (s ?? "").Replace("\0", "").Trim();
        static string CleanModel(string s)
        {
            s = Clean(s);
            foreach (var junk in new[] { " SCSI Disk Device", " ATA Device", " USB Device", " SCSI Device" }) if (s.EndsWith(junk, StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - junk.Length);
            if (s.StartsWith("NVMe ", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5);
            return s.Trim();
        }
        public static Vendor VendorOf(string s)
        {
            s = (s ?? "").ToLowerInvariant();
            if (s.Contains("nvidia") || s.Contains("geforce") || s.Contains("quadro") || s.Contains("rtx")) return Vendor.Nvidia;
            if (s.Contains("amd") || s.Contains("radeon") || s.Contains("advanced micro") || s.Contains("ati ")) return Vendor.Amd;
            if (s.Contains("intel") || s.Contains("genuineintel")) return Vendor.Intel;
            if (s.Contains("qualcomm") || s.Contains("snapdragon")) return Vendor.Qualcomm;
            return Vendor.Unknown;
        }
        static bool IsIntegrated(string name, Vendor v)
        {
            var n = name.ToLowerInvariant();
            if (v == Vendor.Intel) return !(n.Contains("arc a") || n.Contains("arc b") || n.Contains("arc pro"));   // Arc A/B = dédiée, le reste (UHD, Iris, « Arc Graphics » Meteor Lake) = intégrée
            if (v == Vendor.Amd) return n.Contains("graphics") && !n.Contains("rx ");                             // « Radeon(TM) Graphics », « Radeon 780M Graphics », Vega 8…
            if (v == Vendor.Qualcomm) return true;
            return false;
        }
        static double VramFromRegistry(string gpuName)
        {
            try
            {
                using (var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (root == null) return 0;
                    foreach (var sub in root.GetSubKeyNames())
                    {
                        if (!sub.All(char.IsDigit)) continue;
                        using (var k = root.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            var desc = k.GetValue("DriverDesc") as string;
                            if (desc == null || !desc.Equals(gpuName, StringComparison.OrdinalIgnoreCase)) continue;
                            var q = k.GetValue("HardwareInformation.qwMemorySize");
                            if (q != null) return Math.Round(Convert.ToDouble(q) / 1073741824.0, 1);
                        }
                    }
                }
            }
            catch { }
            return 0;
        }
        static string RamType(int smbios, int legacy)
        {
            switch (smbios) { case 26: return "DDR4"; case 34: return "DDR5"; case 24: return "DDR3"; case 22: return "DDR2"; case 30: return "LPDDR4"; case 35: return "LPDDR5"; case 29: return "LPDDR3"; }
            switch (legacy) { case 24: return "DDR3"; case 26: return "DDR4"; case 21: return "DDR2"; }
            return null;
        }
        static string BusName(int b)
        {
            switch (b) { case 17: return "NVMe"; case 11: return "SATA"; case 7: return "USB"; case 8: return "RAID"; case 10: return "SAS"; case 3: return "ATA"; case 1: return "SCSI"; case 15: return "File"; case 16: return "Spaces"; case 12: return "SD"; case 13: return "MMC"; case 14: return "Virtuel"; }
            return b == 0 ? "?" : "bus " + b;
        }
        static string ChassisName(int c)
        {
            switch (c)
            {
                case 3: return "Bureau"; case 4: return "Bureau bas profil"; case 5: return "Boîtier pizza"; case 6: return "Mini-tour"; case 7: return "Tour"; case 8: return "Portable"; case 9: return "Portable"; case 10: return "Notebook"; case 11: return "Portatif";
                case 12: return "Station d'accueil"; case 13: return "Tout-en-un"; case 14: return "Sub-notebook"; case 15: return "Format compact"; case 16: return "Lunch box"; case 17: return "Serveur"; case 21: return "Périphérique"; case 22: return "Boîtier"; case 23: return "Rack";
                case 24: return "Boîtier scellé"; case 30: return "Tablette"; case 31: return "Convertible"; case 32: return "Détachable"; case 34: return "Mini PC"; case 35: return "Stick PC";
            }
            return "type " + c;
        }
    }
}
