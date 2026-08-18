using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PerfMonitorLive.Metrics;

namespace PerfMonitorLive
{
    /// <summary>Une règle d'alerte : métrique + seuil + durée soutenue.</summary>
    public class Rule
    {
        public string Metric { get; set; }      // cpu | memPct | temp | gpuTemp | rx | tx | pageIn | reboot | cpuW | gpuW | disk.pct | disk.lat | disk.rw | stor.temp | stor.err | stor.health | fan
        public string Disk { get; set; }        // nom du disque physique / ventilateur pour les métriques par instance
        public double Threshold { get; set; }
        public int SustainSec { get; set; } = 30;
        public bool Enabled { get; set; } = true;

        [JsonIgnore] public string Id => Disk == null ? Metric : Metric + ":" + Disk;
        [JsonIgnore] public string Label
        {
            get
            {
                var d = Disk == null ? "" : " " + Disk;
                switch (Metric)
                {
                    case "cpu": return "CPU";
                    case "memPct": return "Mémoire";
                    case "temp": return "Température CPU";
                    case "gpuTemp": return "Température GPU";
                    case "cpuW": return "Consommation CPU";
                    case "gpuW": return "Consommation GPU";
                    case "stor.temp": return "Température " + (Disk ?? "disque");
                    case "stor.err": return "Erreurs disque " + (Disk ?? "");
                    case "stor.health": return "État SMART " + (Disk ?? "");
                    case "reboot": return "Besoin de redémarrage";
                    case "rx": return "Réseau reçu";
                    case "tx": return "Réseau envoyé";
                    case "pageIn": return "Pages lues/s";
                    case "fan": return "Ventilateur" + d;
                    case "disk.pct": return "Disque" + d + " – activité";
                    case "disk.lat": return "Disque" + d + " – latence";
                    case "disk.rw": return "Disque" + d + " – débit";
                }
                return Metric;
            }
        }
        [JsonIgnore] public string Unit
        {
            get
            {
                switch (Metric)
                {
                    case "cpu": case "memPct": case "disk.pct": return "%";
                    case "temp": case "gpuTemp": case "stor.temp": return "°C";
                    case "rx": case "tx": case "disk.rw": return "Mo/s";
                    case "disk.lat": return "ms";
                    case "pageIn": return "/s";
                    case "reboot": return "pts";
                    case "cpuW": case "gpuW": return "W";
                    case "fan": return "tr/min";
                    case "stor.err": return "err.";
                    case "stor.health": return "";
                }
                return "";
            }
        }
        /// <summary>Valeur courante de la métrique dans l'échantillon (null si indisponible).</summary>
        public double? Value(Sample s)
        {
            switch (Metric)
            {
                case "cpu": return s.cpu;
                case "memPct": return s.memPct;
                case "temp": return s.temp;
                case "gpuTemp": return s.gpu;
                case "cpuW": return s.cpuW;
                case "gpuW": return s.gpuW;
                case "reboot": return s.rb?.score;
                case "rx": return s.rx / 1024;
                case "tx": return s.tx / 1024;
                case "pageIn": return s.pageIn;
                case "stor.temp": { var st = s.Stor(Disk); return st == null || st.t <= 0 ? (double?)null : st.t; }
                case "stor.err": { var st = s.Stor(Disk); return st == null ? (double?)null : st.Errors - Baseline(st); }
                case "stor.health": { var st = s.Stor(Disk); return st == null ? (double?)null : st.health; }
                case "fan": { var f = s.Fan(Disk); return f == null ? (double?)null : f.rpm; }
            }
            var d = s.Disk(Disk); if (d == null) return null;
            switch (Metric)
            {
                case "disk.pct": return d.pct;
                case "disk.lat": return d.lat;
                case "disk.rw": return d.r + d.w;
            }
            return null;
        }
        // erreurs disque : on ne compte que les nouvelles depuis le lancement de l'appli
        static readonly Dictionary<string, double> ErrBaseline = new Dictionary<string, double>();
        static double Baseline(StorSample st) { if (!ErrBaseline.TryGetValue(st.n, out var b)) ErrBaseline[st.n] = b = st.Errors; return b; }

        public string Format(double v)
        {
            switch (Unit)
            {
                case "%": return Math.Round(v) + " %";
                case "°C": return v.ToString("0") + " °C";
                case "Mo/s": return (v < 10 ? v.ToString("0.0") : v.ToString("0")) + " Mo/s";
                case "ms": return (v < 10 ? v.ToString("0.0") : v.ToString("0")) + " ms";
                case "": return v == 0 ? "OK" : v == 1 ? "Avertissement" : "Défaillant";
                default: return v.ToString("0") + " " + Unit;
            }
        }
        public Rule Clone() => new Rule { Metric = Metric, Disk = Disk, Threshold = Threshold, SustainSec = SustainSec, Enabled = Enabled };
    }

    /// <summary>Un profil de seuils (Travail / Jeu / Nuit…).</summary>
    public class Profile
    {
        public List<Rule> Rules { get; set; } = new List<Rule>();
        public bool OnlyCritical { get; set; }          // ne notifier que les alertes critiques
    }

    public class Settings
    {
        // --- règles (profil actif) et profils
        public List<Rule> Rules { get; set; } = new List<Rule>();
        public string ActiveProfile { get; set; } = "Travail";
        public bool ProfileAuto { get; set; } = true;
        public int NightStart { get; set; } = 23;
        public int NightEnd { get; set; } = 7;
        public Dictionary<string, Profile> Profiles { get; set; } = new Dictionary<string, Profile>();
        // --- notifications
        public int CooldownSec { get; set; } = 300;
        public int ToastSec { get; set; } = 8;
        public string Corner { get; set; } = "BottomRight";     // BottomRight | TopRight | BottomLeft | TopLeft
        public string ScreenMode { get; set; } = "Secondary";   // Secondary | Primary | Index
        public int ScreenIndex { get; set; } = 1;
        public bool Sound { get; set; } = true;
        public bool NotifyRecovery { get; set; } = true;
        public DateTime? PausedUntil { get; set; }
        // --- général
        public bool StartWithWindows { get; set; } = true;
        public bool ShowAdvisor { get; set; } = true;
        public List<string> AdvisorDismissed { get; set; } = new List<string>();
        public string Theme { get; set; } = "Auto";             // Auto | Dark | Light
        public bool Compact { get; set; }
        // --- jeux / overlay / widget
        public List<string> Games { get; set; } = new List<string>();
        public bool GameAutoDetect { get; set; } = true;
        public bool OverlayEnabled { get; set; } = true;
        public bool WidgetEnabled { get; set; }
        public double WidgetX { get; set; } = double.NaN;
        public double WidgetY { get; set; } = double.NaN;
        public double WidgetOpacity { get; set; } = 0.9;
        // --- Telegram / résumé
        public string TelegramTokenEnc { get; set; }             // chiffré DPAPI (utilisateur)
        public string TelegramChatId { get; set; }
        public bool TelegramCritical { get; set; } = true;
        public bool TelegramDigest { get; set; } = true;
        public bool DigestEnabled { get; set; } = true;
        public string LastDigestDate { get; set; }               // yyyy-MM-dd
        public bool MachineTuned { get; set; }
        public bool EcoAuto { get; set; } = true;                // mode économie : 1 mesure / 2 s sur batterie ou fenêtre fermée > 5 min                   // seuils par défaut adaptés au matériel (portable, HDD/SSD…) déjà appliqués

        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        static readonly object Lock = new object();
        [JsonIgnore] public bool ActiveOnlyCritical => Profiles.TryGetValue(ActiveProfile, out var p) && p.OnlyCritical;

        public static Settings Load()
        {
            try
            {
                if (File.Exists(Paths.SettingsFile))
                {
                    var txt = File.ReadAllText(Paths.SettingsFile);
                    var s = JsonSerializer.Deserialize<Settings>(txt, Opts);
                    if (s != null) { if (!txt.Contains("\"MachineTuned\"")) s.MachineTuned = true; s.EnsureDefaults(); s.EnsureProfiles(); return s; }
                }
            }
            catch (Exception ex) { Paths.Log("Settings load: " + ex.Message); }
            var n = new Settings(); n.EnsureDefaults(); n.EnsureProfiles(); n.Save(); return n;
        }
        public void Save()
        {
            lock (Lock)
            {
                try
                {
                    if (Profiles.TryGetValue(ActiveProfile, out var p)) p.Rules = Rules;   // le profil actif reflète toujours les règles en cours
                    File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, Opts));
                }
                catch (Exception ex) { Paths.Log("Settings save: " + ex.Message); }
            }
        }

        public Rule Get(string metric, string disk = null) => Rules.FirstOrDefault(r => r.Metric == metric && r.Disk == disk);

        Rule Ensure(string metric, string disk, double thr, int sustain, bool enabled)
        {
            var r = Get(metric, disk);
            if (r == null) { r = new Rule { Metric = metric, Disk = disk, Threshold = thr, SustainSec = sustain, Enabled = enabled }; Rules.Add(r); }
            return r;
        }
        public void EnsureDefaults()
        {
            Ensure("cpu", null, 90, 30, true);
            Ensure("memPct", null, 85, 30, true);
            Ensure("temp", null, 85, 10, true);
            Ensure("gpuTemp", null, 90, 10, true);
            Ensure("reboot", null, 70, 120, true);
            Ensure("cpuW", null, 140, 60, false);
            Ensure("gpuW", null, 330, 60, false);
            Ensure("rx", null, 50, 30, false);
            Ensure("tx", null, 50, 30, false);
            Ensure("pageIn", null, 2000, 30, false);
        }
        public bool EnsureDisk(string disk, Metrics.DiskKind kind = Metrics.DiskKind.Unknown)
        {
            int before = Rules.Count;
            Ensure("disk.pct", disk, 90, 60, true);
            Ensure("disk.lat", disk, LatencyDefault(kind), 30, true);
            Ensure("disk.rw", disk, kind == Metrics.DiskKind.Hdd || kind == Metrics.DiskKind.Usb ? 100 : 200, 30, false);
            return Rules.Count != before;
        }
        /// <summary>Latence d'alerte par défaut selon le type de disque : NVMe 20 ms, SSD SATA 30 ms, HDD/USB 100 ms, inconnu 50 ms.</summary>
        public static double LatencyDefault(Metrics.DiskKind kind) => kind == Metrics.DiskKind.Nvme ? 20 : kind == Metrics.DiskKind.Ssd ? 30 : kind == Metrics.DiskKind.Hdd || kind == Metrics.DiskKind.Usb ? 100 : 50;
        public static double StorTempDefault(Metrics.DiskKind kind) => kind == Metrics.DiskKind.Hdd ? 50 : 65;
        public bool EnsureStorage(string name, Metrics.DiskKind kind = Metrics.DiskKind.Unknown)
        {
            int b = Rules.Count;
            Ensure("stor.temp", name, StorTempDefault(kind), 60, true);
            Ensure("stor.err", name, 1, 0, true);
            Ensure("stor.health", name, 1, 0, true);
            return Rules.Count != b;
        }
        public bool EnsureFan(string name) { int b = Rules.Count; Ensure("fan", name, 6000, 60, false); return Rules.Count != b; }
        /// <summary>Adapte les seuils par défaut au matériel détecté (une seule fois, sur une installation neuve).</summary>
        public bool ApplyMachine(Metrics.MachineInfo m)
        {
            if (MachineTuned || m == null || m.Cpu == null || m.Cpu.Name == "?") return false;
            var t = Get("temp"); if (t != null) t.Threshold = m.Laptop ? 95 : m.Cpu.Vendor == Metrics.Vendor.Intel ? 95 : 85;
            var g = Get("gpuTemp"); if (g != null) g.Threshold = m.Laptop ? 90 : 90;
            var cw = Get("cpuW"); if (cw != null) cw.Threshold = m.Laptop ? 60 : m.Cpu.Vendor == Metrics.Vendor.Intel ? 250 : 140;
            var gw = Get("gpuW"); if (gw != null) gw.Threshold = m.HasDedicatedGpu ? (m.MainGpu.VramGB >= 16 ? 350 : m.MainGpu.VramGB >= 12 ? 300 : 220) : 80;
            foreach (var r in Rules.Where(x => x.Metric == "disk.lat" && x.Disk != null)) { var d = m.DiskByPerfName(r.Disk); if (d != null) r.Threshold = LatencyDefault(d.Kind); }
            foreach (var r in Rules.Where(x => x.Metric == "stor.temp" && x.Disk != null)) { var d = m.DiskByStorName(r.Disk); if (d != null) r.Threshold = StorTempDefault(d.Kind); }
            MachineTuned = true;
            foreach (var kv in Profiles) if (kv.Value.Rules != Rules)
                foreach (var r in Rules)
                {
                    var pr = kv.Value.Rules.FirstOrDefault(x => x.Id == r.Id);
                    if (pr == null || !(r.Metric == "temp" || r.Metric == "cpuW" || r.Metric == "gpuW" || r.Metric == "disk.lat" || r.Metric == "stor.temp")) continue;
                    pr.Threshold = r.Metric == "temp" && kv.Key == "Jeu" ? Math.Min(r.Threshold + 5, 100) : r.Threshold;
                }
            Save();
            return true;
        }
        public bool IsPaused => PausedUntil.HasValue && PausedUntil.Value > DateTime.Now;

        // --- profils
        public void EnsureProfiles()
        {
            if (!Profiles.ContainsKey("Travail")) Profiles["Travail"] = new Profile { Rules = Rules.Select(r => r.Clone()).ToList() };
            if (!Profiles.ContainsKey("Jeu"))
            {
                var p = new Profile { OnlyCritical = true, Rules = Rules.Select(r => r.Clone()).ToList() };
                foreach (var r in p.Rules)
                {
                    if (r.Metric == "cpu") { r.Threshold = 95; r.SustainSec = 120; }
                    if (r.Metric == "gpuTemp") r.Threshold = 92;
                    if (r.Metric == "memPct") r.Threshold = 92;
                    if (r.Metric == "rx" || r.Metric == "tx" || r.Metric == "pageIn" || r.Metric == "disk.pct" || r.Metric == "disk.rw") r.Enabled = false;
                }
                Profiles["Jeu"] = p;
            }
            if (!Profiles.ContainsKey("Nuit"))
            {
                var p = new Profile { Rules = Rules.Select(r => r.Clone()).ToList() };
                foreach (var r in p.Rules)
                    if (r.Metric == "cpu" || r.Metric == "memPct" || r.Metric == "rx" || r.Metric == "tx" || r.Metric == "pageIn" || r.Metric == "reboot" || r.Metric == "disk.pct" || r.Metric == "disk.rw") r.Enabled = false;
                Profiles["Nuit"] = p;
            }
            if (!Profiles.ContainsKey(ActiveProfile)) ActiveProfile = "Travail";
            Profiles[ActiveProfile].Rules = Rules;
        }
        /// <summary>Active un profil : les règles en cours deviennent celles du profil (les règles manquantes sont créées).</summary>
        public void SwitchProfile(string name)
        {
            if (!Profiles.ContainsKey(name) || name == ActiveProfile) return;
            Profiles[ActiveProfile].Rules = Rules.Select(r => r.Clone()).ToList();
            ActiveProfile = name;
            Rules = Profiles[name].Rules.Select(r => r.Clone()).ToList();
            EnsureDefaults();
            Profiles[name].Rules = Rules;
            Save();
        }
        /// <summary>Profil que l'automatisme choisirait maintenant.</summary>
        public string AutoProfile(bool gameActive, DateTime now)
        {
            if (gameActive) return "Jeu";
            int h = now.Hour;
            bool night = NightStart > NightEnd ? (h >= NightStart || h < NightEnd) : (h >= NightStart && h < NightEnd);
            return night ? "Nuit" : "Travail";
        }
    }
}
