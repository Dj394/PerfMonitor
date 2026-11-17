using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace PerfMonitorLive.Metrics
{
    /// <summary>Une ligne du journal alimentation (<c>data\power-AAAA-MM-JJ.csv</c>). Valeur absente = null.</summary>
    public class PowerRow
    {
        public DateTime Time;
        public double? Fps, V12, V5, V33, CpuW, GpuW, CpuC, GpuC, GpuMHz, FanMax, CpuPct;
        public bool? Game;
        public double? TotalW => CpuW.HasValue || GpuW.HasValue ? (CpuW ?? 0) + (GpuW ?? 0) : (double?)null;
    }

    /// <summary>Un arrêt inattendu relevé dans le journal Windows, tel qu'enregistré dans <c>data\crashes.jsonl</c>
    /// (qui sert à la fois d'historique et de mémoire des arrêts déjà notifiés).</summary>
    public class CrashEntry
    {
        public string boot { get; set; }       // redémarrage qui a suivi (horodatage de l'événement Windows) : clé
        public string sessionStart { get; set; } // démarrage précédent = début de la session interrompue, si connu
        public string sessionEnd { get; set; }   // démarrage suivant (Kernel-General 12), borne haute des lignes du journal
        public string winTime { get; set; }    // heure d'arrêt notée par Windows (EventLog 6008), si connue
        public string lastLog { get; set; }    // dernière ligne du journal alimentation avant l'arrêt
        public string source { get; set; }     // « Kernel-Power 41 », « EventLog 6008 »…
        public string bugcheck { get; set; }   // code d'écran bleu (hexa) si Windows en a enregistré un
        public bool powerButton { get; set; }  // bouton d'alimentation maintenu
        public bool sleep { get; set; }        // arrêt pendant une mise en veille / veille moderne
        public int whea { get; set; }          // erreurs matérielles WHEA comptées au démarrage suivant
        public string level { get; set; }      // "critical" | "warning"
        public string summary { get; set; }    // 2-3 lignes pour la notification
        public bool notified { get; set; }

        static DateTime? P(string s) => !string.IsNullOrEmpty(s) && DateTime.TryParseExact(s, Fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : (DateTime?)null;
        internal const string Fmt = "yyyy-MM-ddTHH:mm:ss";
        [JsonIgnore] public DateTime BootTime => P(boot) ?? DateTime.MinValue;
        [JsonIgnore] public DateTime? SessionStart => P(sessionStart);
        [JsonIgnore] public DateTime? SessionEnd => P(sessionEnd);
        [JsonIgnore] public DateTime? WinTime => P(winTime);
        [JsonIgnore] public DateTime? LastLog => P(lastLog);
        /// <summary>Instant le plus probable de l'arrêt : dernière ligne du journal alimentation, sinon heure Windows, sinon redémarrage.</summary>
        [JsonIgnore] public DateTime When => LastLog ?? WinTime ?? BootTime;
        [JsonIgnore] public bool Critical => level == "critical";
    }

    public class CrashFinding
    {
        public int Level;        // 0 = information, 1 = indice, 2 = indice fort
        public string Text;
        public CrashFinding(int level, string text) { Level = level; Text = text; }
    }

    public class CrashAnalysis
    {
        public List<PowerRow> Rows = new List<PowerRow>();
        public List<CrashFinding> Findings = new List<CrashFinding>();
        public List<string> Files = new List<string>();
        public bool HasData => Rows.Count > 0;
        public bool HasSensors;
        public int MaxLevel => Findings.Count == 0 ? 0 : Findings.Max(f => f.Level);
    }

    /// <summary>Rapport d'arrêt brutal : au démarrage (une fois, hors thread UI), relève dans le journal System les arrêts
    /// inattendus (Kernel-Power 41, EventLog 6008), relit la fin du journal alimentation de la session interrompue
    /// et en tire quelques indices. Lecture bornée : on part de la fin des fichiers et on s'arrête dès qu'on a la minute
    /// utile. Les indices restent des indices : une mesure logicielle ne voit ni un pic de quelques microsecondes
    /// ni la cause d'un blocage de pilote.</summary>
    public static class CrashReport
    {
        public const int Seconds = 60;
        const int MaxEvents = 600, MaxHistory = 100, LookbackDays = 30;
        const long MaxBytesPerFile = 16L * 1024 * 1024;
        static readonly object FileLock = new object();
        // accents lisibles dans l'historique (fichier local, jamais inséré dans une page)
        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        static string File_ => Path.Combine(Paths.DataDir, "crashes.jsonl");

        // ------------------------------------------------------------------ historique crashes.jsonl
        public static List<CrashEntry> Load()
        {
            var list = new List<CrashEntry>();
            lock (FileLock)
            {
                try
                {
                    if (File.Exists(File_))
                        foreach (var l in File.ReadAllLines(File_))
                            if (l.Length > 10) { try { var e = JsonSerializer.Deserialize<CrashEntry>(l, Opts); if (e?.boot != null) list.Add(e); } catch { } }
                }
                catch (Exception ex) { Paths.Log("crashes load: " + ex.Message); }
            }
            return list.OrderBy(e => e.BootTime).ToList();
        }

        static void Save(List<CrashEntry> list)
        {
            lock (FileLock)
            {
                try
                {
                    Directory.CreateDirectory(Paths.DataDir);
                    var sb = new StringBuilder();
                    foreach (var e in list.OrderBy(e => e.BootTime).Skip(Math.Max(0, list.Count - MaxHistory))) sb.Append(JsonSerializer.Serialize(e, Opts)).Append('\n');
                    var tmp = File_ + ".tmp";
                    File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                    File.Move(tmp, File_, true);
                }
                catch (Exception ex) { Paths.Log("crashes save: " + ex.Message); }
            }
        }

        /// <summary>Marque l'arrêt comme notifié (ne sera plus jamais proposé en notification).</summary>
        public static void MarkNotified(CrashEntry e)
        {
            var list = Load();
            foreach (var x in list) if (x.boot == e.boot) x.notified = true;
            e.notified = true;
            Save(list);
        }

        /// <summary>Thread de fond, au démarrage : enregistre les arrêts inattendus pas encore connus.
        /// <paramref name="toNotify"/> = l'arrêt à notifier (le plus récent survenu juste avant cette session Windows,
        /// ou un arrêt récent dont la notification avait été différée), sinon null. Renvoie le dernier arrêt connu.</summary>
        public static CrashEntry Refresh(out CrashEntry toNotify)
        {
            toNotify = null;
            var list = Load();
            var known = new HashSet<string>(list.Select(e => e.boot));
            DateTime windowsBoot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
            bool added = false;
            foreach (var e in ReadIncidents())
            {
                if (known.Contains(e.boot)) continue;
                var a = Analyze(e);
                Summarize(e, a);
                // Un arrêt antérieur à la session Windows en cours (première utilisation, ou appli non lancée depuis) est
                // gardé dans l'historique sans notification : on ne prévient que de ce qui vient d'arriver.
                e.notified = e.BootTime < windowsBoot.AddMinutes(-10);
                list.Add(e); known.Add(e.boot); added = true;
                Paths.Log("Arrêt inattendu relevé : " + e.When.ToString("s") + " (" + e.source + ")" + (a.HasData ? ", " + a.Rows.Count + " s de journal alimentation" : ", sans journal alimentation"));
            }
            var pending = list.Where(e => !e.notified && e.BootTime > DateTime.Now.AddDays(-3)).OrderBy(e => e.BootTime).ToList();
            if (pending.Count > 0)
            {
                toNotify = pending[pending.Count - 1];
                foreach (var e in pending) if (e != toNotify) { e.notified = true; added = true; }   // un seul toast, le plus récent
            }
            if (added) Save(list);
            return list.LastOrDefault();
        }

        // ------------------------------------------------------------------ journal Windows
        /// <summary>Arrêts inattendus des <see cref="LookbackDays"/> derniers jours, du plus ancien au plus récent.
        /// Un même redémarrage produit en général Kernel-Power 41 et EventLog 6008 à quelques secondes d'écart : regroupés.</summary>
        public static List<CrashEntry> ReadIncidents()
        {
            var k41 = new List<(DateTime t, Dictionary<string, string> d)>();
            var e6008 = new List<(DateTime t, DateTime? win)>();
            var boots = new List<DateTime>();   // Kernel-General 12 : démarrage du système
            try
            {
                long ms = (long)TimeSpan.FromDays(LookbackDays).TotalMilliseconds;
                string xpath = "*[System[((Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or (Provider[@Name='EventLog'] and EventID=6008) or (Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12)) and TimeCreated[timediff(@SystemTime) <= " + ms + "]]]";
                var q = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };
                using (var reader = new EventLogReader(q))
                {
                    EventRecord rec; int n = 0;
                    while ((rec = reader.ReadEvent()) != null && n++ < MaxEvents)
                    {
                        using (rec)
                        {
                            var t = rec.TimeCreated ?? DateTime.MinValue;
                            if (t == DateTime.MinValue) continue;
                            if (rec.Id == 12) boots.Add(t);
                            else if (rec.Id == 41) k41.Add((t, ParseData(rec.ToXml(), out _)));
                            else if (rec.Id == 6008) { ParseData(rec.ToXml(), out var bin); e6008.Add((t, Parse6008(bin))); }
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException) { Paths.Log("CrashReport : journal System inaccessible — arrêts inattendus non relevés"); }
            catch (Exception ex) { Paths.Log("CrashReport: " + ex.Message); }
            return BuildIncidents(k41, e6008, boots);
        }

        public static List<CrashEntry> BuildIncidents(List<(DateTime t, Dictionary<string, string> d)> k41, List<(DateTime t, DateTime? win)> e6008, List<DateTime> boots)
        {
            boots = boots.OrderBy(b => b).ToList();
            var used = new HashSet<int>();
            var res = new List<CrashEntry>();
            foreach (var (t, d) in k41.OrderBy(x => x.t))
            {
                var e = new CrashEntry { boot = t.ToString(CrashEntry.Fmt), source = "Kernel-Power 41" };
                int bc = (int)Num(d, "BugcheckCode");
                if (bc != 0) e.bugcheck = "0x" + bc.ToString("X8");
                e.powerButton = Num(d, "PowerButtonTimestamp") != 0 || Flag(d, "LongPowerButtonPressDetected");
                e.sleep = Num(d, "SleepInProgress") != 0 || Flag(d, "ConnectedStandbyInProgress");
                e.whea = (int)Num(d, "WHEABootErrorCount");
                for (int i = 0; i < e6008.Count; i++)
                    if (!used.Contains(i) && Math.Abs((e6008[i].t - t).TotalMinutes) < 5) { used.Add(i); e.winTime = e6008[i].win?.ToString(CrashEntry.Fmt); e.source += " + EventLog 6008"; break; }
                res.Add(e);
            }
            for (int i = 0; i < e6008.Count; i++)
                if (!used.Contains(i)) res.Add(new CrashEntry { boot = e6008[i].t.ToString(CrashEntry.Fmt), winTime = e6008[i].win?.ToString(CrashEntry.Fmt), source = "EventLog 6008" });
            foreach (var e in res)
            {
                var b = e.BootTime;
                // démarrage qui a suivi l'arrêt : l'événement 12 juste avant (quelques secondes) l'événement d'arrêt
                DateTime? cur = boots.LastOrDefault(x => x <= b && x >= b.AddMinutes(-5)) is var c && c != default ? c : (DateTime?)null;
                if (cur.HasValue) e.sessionEnd = cur.Value.ToString(CrashEntry.Fmt);
                var before = cur ?? b;
                var prev = boots.LastOrDefault(x => x < before);
                if (prev != default) e.sessionStart = prev.ToString(CrashEntry.Fmt);
            }
            return res.OrderBy(e => e.BootTime).ToList();
        }

        static double Num(Dictionary<string, string> d, string k)
        {
            if (!d.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)) return 0;
            v = v.Trim();
            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return long.TryParse(v.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : 0;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
        }
        static bool Flag(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) && (v == "true" || v == "1");

        static Dictionary<string, string> ParseData(string xml, out string binary)
        {
            var d = new Dictionary<string, string>(); binary = null;
            try
            {
                var doc = new XmlDocument(); doc.LoadXml(xml);
                foreach (XmlNode n in doc.GetElementsByTagName("Data")) { var name = n.Attributes?["Name"]?.Value; if (name != null) d[name] = n.InnerText; }
                var b = doc.GetElementsByTagName("Binary"); if (b.Count > 0) binary = b[0].InnerText;
            }
            catch { }
            return d;
        }

        /// <summary>EventLog 6008 : les données binaires contiennent deux SYSTEMTIME, heure locale puis UTC, de l'arrêt.
        /// On prend l'UTC converti en heure locale (insensible au format de date régional des chaînes du message).</summary>
        public static DateTime? Parse6008(string hex)
        {
            try
            {
                if (string.IsNullOrEmpty(hex) || hex.Length < 64) return null;
                var b = Convert.FromHexString(hex.Substring(0, 64));
                DateTime? St(int o)
                {
                    int U(int i) => b[o + i] | b[o + i + 1] << 8;
                    int y = U(0), mo = U(2), d = U(6), h = U(8), mi = U(10), s = U(12);
                    if (y < 2000 || y > 2100 || mo < 1 || mo > 12 || d < 1 || d > 31 || h > 23 || mi > 59 || s > 59) return null;
                    return new DateTime(y, mo, d, h, mi, s);
                }
                var utc = St(16);
                if (utc.HasValue) return DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime();
                return St(0);
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ journal alimentation
        /// <summary>Les ~60 dernières lignes du journal alimentation de la session interrompue (entre le démarrage
        /// précédent et le redémarrage), lues depuis la fin des fichiers.</summary>
        public static CrashAnalysis Analyze(CrashEntry e)
        {
            var a = new CrashAnalysis();
            try { ReadRows(e, a); } catch (Exception ex) { Paths.Log("CrashReport rows: " + ex.Message); }
            if (a.HasData) e.lastLog = a.Rows[a.Rows.Count - 1].Time.ToString(CrashEntry.Fmt);
            Diagnose(e, a);
            return a;
        }

        static void ReadRows(CrashEntry e, CrashAnalysis a)
        {
            DateTime upper = e.SessionEnd ?? e.BootTime;
            DateTime lower = e.SessionStart ?? (e.WinTime.HasValue ? e.WinTime.Value.AddMinutes(-30) : upper.AddHours(-48));
            if (lower < upper.AddDays(-3)) lower = upper.AddDays(-3);   // session très longue : on ne remonte pas au-delà de 3 jours de fichiers
            var dir = Paths.DataDir;
            if (!Directory.Exists(dir)) return;
            var rows = new List<PowerRow>();
            for (var day = upper.Date; day >= lower.Date && rows.Count < Seconds; day = day.AddDays(-1))
            {
                var name = "power-" + day.ToString("yyyy-MM-dd");
                var files = new List<string>();
                var main = Path.Combine(dir, name + ".csv");
                if (File.Exists(main)) files.Add(main);
                // anciens fichiers du même jour mis de côté au changement de colonnes (power-AAAA-MM-JJ.HHMMSS.csv) : plus anciens que le fichier principal
                try { files.AddRange(Directory.GetFiles(dir, name + ".*.csv").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)); } catch { }
                foreach (var f in files)
                {
                    if (rows.Count >= Seconds) break;
                    int before = rows.Count;
                    ReadFileTail(f, day, lower, upper, Seconds - rows.Count, rows);
                    if (rows.Count > before) a.Files.Add(f);
                }
            }
            rows.Sort((x, y) => x.Time.CompareTo(y.Time));
            if (rows.Count > Seconds) rows.RemoveRange(0, rows.Count - Seconds);
            // Données sans rapport avec l'arrêt : le journal s'arrête plus de 30 min avant l'heure notée par Windows.
            if (rows.Count > 0 && e.WinTime.HasValue && (e.WinTime.Value - rows[rows.Count - 1].Time).TotalMinutes > 30) { rows.Clear(); a.Files.Clear(); }
            a.Rows = rows;
        }

        /// <summary>Lit un fichier depuis la fin : lignes dans ]lower ; upper[, au plus <paramref name="want"/>.
        /// S'arrête dès qu'une ligne ≤ lower est rencontrée ou après <see cref="MaxBytesPerFile"/> octets.</summary>
        public static void ReadFileTail(string path, DateTime day, DateTime lower, DateTime upper, int want, List<PowerRow> outRows)
        {
            var cols = ReadHeader(path);
            int got = 0;
            foreach (var line in ReadLinesBackward(path, MaxBytesPerFile))
            {
                var r = ParseRow(line, cols, day);
                if (r == null) continue;
                if (r.Time >= upper) continue;          // lignes écrites après le redémarrage (même fichier du jour)
                if (r.Time <= lower) break;
                outRows.Add(r);
                if (++got >= want) break;
            }
        }

        static readonly string[] DefaultCols = "ts;fps;v12;v5;v33;cpuW;gpuW;cpuC;gpuC;gpuMHz;fanMax;cpuPct;jeu".Split(';');

        /// <summary>Colonnes du fichier d'après son en-tête (elles changent selon la version de PerfMonitor). La colonne de
        /// l'heure s'appelle « ts » dans les anciens fichiers (point décimal) et « heure » dans les nouveaux (virgule
        /// décimale) : les deux sont acceptées, « heure » est aussi exposée sous le nom « ts ».</summary>
        public static Dictionary<string, int> ReadHeader(string path)
        {
            string first = null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8)) first = sr.ReadLine();
            }
            catch { }
            var h = first?.TrimStart('﻿');
            var names = h != null && (h.StartsWith("ts", StringComparison.OrdinalIgnoreCase) || h.StartsWith("heure", StringComparison.OrdinalIgnoreCase)) ? h.Split(';') : DefaultCols;
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Length; i++) { var n = names[i].Trim(); if (n.Length > 0 && !d.ContainsKey(n)) d[n] = i; }
            if (!d.ContainsKey("ts") && d.TryGetValue("heure", out int hi)) d["ts"] = hi;
            return d;
        }

        public static PowerRow ParseRow(string line, Dictionary<string, int> cols, DateTime day)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            var c = line.Split(';');
            if (!cols.TryGetValue("ts", out int ti) || ti >= c.Length) return null;
            // heure écrite au format HH:mm:ss de la culture courante : on ne garde que les nombres (séparateur indifférent)
            var parts = c[ti].Split(new[] { ':', '.', ' ', 'h', 'H' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m) || !int.TryParse(parts[2], out int s)) return null;
            if (h > 23 || m > 59 || s > 59) return null;
            double? V(string name)
            {
                if (!cols.TryGetValue(name, out int i) || i >= c.Length) return null;
                // Deux notations acceptées, cellule par cellule : point décimal (anciens fichiers) et virgule décimale
                // (fichiers écrits depuis, lisibles par Excel en français). Aucun séparateur de milliers n'est jamais écrit.
                var t = c[i].Trim().Replace(',', '.');
                return t.Length > 0 && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && !double.IsNaN(x) ? x : (double?)null;
            }
            var g = V("jeu");
            return new PowerRow
            {
                Time = day.Date.AddHours(h).AddMinutes(m).AddSeconds(s),
                Fps = V("fps"), V12 = V("v12"), V5 = V("v5"), V33 = V("v33"), CpuW = V("cpuW"), GpuW = V("gpuW"),
                CpuC = V("cpuC"), GpuC = V("gpuC"), GpuMHz = V("gpuMHz"), FanMax = V("fanMax"), CpuPct = V("cpuPct"),
                Game = g.HasValue ? g.Value != 0 : (bool?)null,
            };
        }

        /// <summary>Lignes d'un fichier de la dernière à la première, par blocs de 64 Ko lus depuis la fin.
        /// Partage lecture/écriture/suppression : le journal en cours d'écriture (ou son renommage) n'est pas gêné.</summary>
        public static IEnumerable<string> ReadLinesBackward(string path, long maxBytes)
        {
            FileStream fs;
            try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1); }
            catch (Exception ex) { Paths.Log("CrashReport lecture " + Path.GetFileName(path) + ": " + ex.Message); yield break; }
            using (fs)
            {
                long pos = fs.Length, read = 0;
                var buf = new byte[64 * 1024];
                byte[] carry = Array.Empty<byte>();
                while (pos > 0 && read < maxBytes)
                {
                    int n = (int)Math.Min(buf.Length, pos);
                    pos -= n; read += n;
                    fs.Position = pos;
                    int off = 0; while (off < n) { int k = fs.Read(buf, off, n - off); if (k <= 0) break; off += k; }
                    var data = new byte[n + carry.Length];
                    Buffer.BlockCopy(buf, 0, data, 0, n); Buffer.BlockCopy(carry, 0, data, n, carry.Length);
                    int end = data.Length;
                    for (int i = data.Length - 1; i >= 0; i--)
                    {
                        if (data[i] != (byte)'\n') continue;
                        if (end - i - 1 > 0) yield return Encoding.UTF8.GetString(data, i + 1, end - i - 1).TrimEnd('\r');
                        end = i;
                    }
                    carry = end == 0 ? Array.Empty<byte>() : data.AsSpan(0, end).ToArray();
                }
                if (pos == 0 && carry.Length > 0) yield return Encoding.UTF8.GetString(carry).TrimEnd('\r');
            }
        }

        // ------------------------------------------------------------------ diagnostic
        static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
        static string F(double v, int d = 0) => v.ToString(d == 0 ? "0" : d == 1 ? "0.0" : d == 2 ? "0.00" : "0.000", Fr);
        static double Median(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); if (a.Length == 0) return double.NaN; return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }
        static string Dur(TimeSpan t) => t.TotalMinutes >= 90 ? F(t.TotalHours, 1) + " h" : t.TotalSeconds >= 90 ? F(t.TotalMinutes) + " min" : F(t.TotalSeconds) + " s";

        /// <summary>Règles simples, formulées comme des indices. Niveau 2 = indice fort (rail hors tolérance, écran bleu,
        /// erreur matérielle, température au plafond), 1 = indice, 0 = information.</summary>
        public static void Diagnose(CrashEntry e, CrashAnalysis a)
        {
            var f = a.Findings;
            var rows = a.Rows;
            // ce que Windows a noté
            if (!string.IsNullOrEmpty(e.bugcheck)) f.Add(new CrashFinding(2, "Windows a enregistré un écran bleu (code " + e.bugcheck + ") : arrêt provoqué par le système (pilote, RAM, instabilité), pas une simple coupure de courant."));
            if (e.whea > 0) f.Add(new CrashFinding(2, "Windows a relevé " + e.whea + " erreur(s) matérielle(s) WHEA au redémarrage : processeur, RAM ou PCIe instables (overclock, undervolt, profil XMP/EXPO…)."));
            if (e.powerButton) f.Add(new CrashFinding(1, "Le bouton d'alimentation a été maintenu (extinction forcée) : si c'était toi après un blocage, c'est le blocage qu'il faut expliquer."));
            if (e.sleep) f.Add(new CrashFinding(1, "L'arrêt est survenu pendant une mise en veille ou à la sortie de veille : piste pilote (GPU, chipset, stockage) plutôt qu'alimentation."));

            if (!a.HasData)
            {
                f.Add(new CrashFinding(0, "Pas de journal alimentation pour cette session : PerfMonitor ne tournait pas au moment de l'arrêt (ou le journal était désactivé)."));
                if (f.All(x => x.Level == 0)) f.Add(new CrashFinding(0, "Sans mesures, impossible de trancher : coupure secteur, bouton, ou instabilité (pilote, RAM, overclock)."));
                return;
            }

            var last = rows[rows.Count - 1];
            var recent = rows.Skip(Math.Max(0, rows.Count - 10)).ToList();   // ~10 dernières secondes
            a.HasSensors = rows.Any(r => r.V12.HasValue || r.V5.HasValue || r.V33.HasValue || r.CpuC.HasValue || r.GpuC.HasValue || r.CpuW.HasValue || r.GpuW.HasValue);

            // journal interrompu bien avant l'heure d'arrêt notée par Windows
            if (e.WinTime.HasValue && !e.sleep)
            {
                var gap = e.WinTime.Value - last.Time;
                if (gap.TotalMinutes > 3) f.Add(new CrashFinding(1, "Le journal s'arrête " + Dur(gap) + " avant l'heure d'arrêt notée par Windows (" + e.WinTime.Value.ToString("HH:mm:ss") + ") : la machine s'est peut-être figée avant de s'éteindre — ou PerfMonitor avait été fermé."));
            }

            if (!a.HasSensors)
                f.Add(new CrashFinding(0, "Le journal ne contient pas de mesures capteurs (tensions, températures, consommations) : PerfMonitor tournait sans droits administrateur ou sans PawnIO. Diagnostic limité à la charge CPU et aux images par seconde."));

            int before = f.Count(x => x.Level >= 1);
            Rail(rows, r => r.V12, 12.0, "12 V", f);
            Rail(rows, r => r.V5, 5.0, "5 V", f);
            Rail(rows, r => r.V33, 3.3, "3,3 V", f);
            Temp(rows, recent, r => r.CpuC, "CPU", 90, 100, f);
            Temp(rows, recent, r => r.GpuC, "GPU", 85, 95, f);

            // consommation : pic juste avant l'arrêt
            var tot = rows.Where(r => r.TotalW.HasValue).Select(r => r.TotalW.Value).ToList();
            var totRecent = recent.Where(r => r.TotalW.HasValue).Select(r => r.TotalW.Value).ToList();
            if (tot.Count >= 5 && totRecent.Count > 0)
            {
                double med = Median(tot), peak = totRecent.Max();
                string parts = PartsText(recent);
                if (peak >= med * 1.3 && peak - med >= 60)
                    f.Add(new CrashFinding(1, "Pic de consommation juste avant l'arrêt : CPU + GPU " + F(peak) + " W" + parts + ", contre " + F(med) + " W en médiane sur la minute. Un appel de courant brusque peut déclencher la protection d'une alimentation juste ou vieillissante."));
                else if (med >= 350)
                    f.Add(new CrashFinding(1, "Forte charge soutenue avant l'arrêt : CPU + GPU ≈ " + F(med) + " W (hors carte mère, RAM, disques, ventilateurs) — à comparer à la puissance de l'alimentation."));
            }

            // jeu et images par seconde
            bool game = recent.Any(r => r.Game == true);
            var fps = rows.Where(r => r.Fps.HasValue && r.Fps > 0).Select(r => r.Fps.Value).ToList();
            if (game)
                f.Add(new CrashFinding(0, "Un jeu était actif" + (fps.Count > 0 ? " (" + F(Median(fps)) + " images/s en médiane sur la minute, " + (last.Fps.HasValue ? F(last.Fps.Value) : "—") + " à la dernière seconde)" : "") + "."));
            if (fps.Count >= 10)
            {
                double med = Median(fps);
                var tail = rows.Skip(Math.Max(0, rows.Count - 3)).Select(r => r.Fps ?? 0).ToList();
                if (med >= 20 && tail.All(v => v <= med * 0.3))
                    f.Add(new CrashFinding(1, "Les images par seconde se sont effondrées juste avant (" + F(med) + " → " + F(tail.Last()) + ") : blocage graphique avant l'arrêt (pilote ou GPU instable, ou alimentation qui décroche sous la charge)."));
            }

            if (f.Count(x => x.Level >= 1) == before && string.IsNullOrEmpty(e.bugcheck) && e.whea == 0 && !e.powerButton && !e.sleep)
                f.Add(new CrashFinding(0, a.HasSensors
                    ? "Rien d'anormal dans les capteurs juste avant l'arrêt : coupure secteur, bouton, ou instabilité non visible dans les capteurs (pilote, RAM, overclock)."
                    : "Rien d'anormal dans les mesures disponibles : coupure secteur, bouton, ou instabilité non visible ici (pilote, RAM, overclock)."));
        }

        static string PartsText(List<PowerRow> recent)
        {
            var r = recent.Where(x => x.TotalW.HasValue).OrderByDescending(x => x.TotalW.Value).FirstOrDefault();
            if (r == null || !r.CpuW.HasValue || !r.GpuW.HasValue) return "";
            return " (CPU " + F(r.CpuW.Value) + " W, GPU " + F(r.GpuW.Value) + " W)";
        }

        /// <summary>Tolérance ATX ±5 %. Une valeur très éloignée en permanence (> 30 %) est un capteur non étalonné
        /// sur cette carte mère (diviseur inconnu de LibreHardwareMonitor), pas un rail effondré : signalé, mais ignoré.</summary>
        static void Rail(List<PowerRow> rows, Func<PowerRow, double?> get, double nominal, string name, List<CrashFinding> f)
        {
            var vals = rows.Select(get).Where(v => v.HasValue && v.Value > 0).Select(v => v.Value).ToList();
            if (vals.Count == 0) return;
            double last = vals[vals.Count - 1], med = Median(vals), min = vals.Min(), max = vals.Max();
            if (Math.Abs(med - nominal) / nominal > 0.30)
            {
                f.Add(new CrashFinding(0, "Rail " + name + " lu à " + F(med, 2) + " V en permanence : capteur probablement non étalonné sur cette carte mère, valeur ignorée."));
                return;
            }
            double lo = nominal * 0.95, hi = nominal * 1.05;
            string range = " (tolérance ATX ±5 % : " + F(lo, 2) + "–" + F(hi, 2) + " V)";
            if (last < lo || last > hi)
                f.Add(new CrashFinding(2, "Rail " + name + " hors tolérance à la dernière mesure : " + F(last, 2) + " V" + range + ". Piste alimentation (bloc, câbles, prise)."));
            else if (min < lo || max > hi)
                f.Add(new CrashFinding(2, "Rail " + name + " sorti de la tolérance dans la dernière minute (" + F(min, 2) + "–" + F(max, 2) + " V)" + range + ". Piste alimentation."));
            else if (med - last >= nominal * 0.03)
                f.Add(new CrashFinding(1, "Chute du rail " + name + " juste avant l'arrêt : " + F(med, 2) + " → " + F(last, 2) + " V (encore dans la tolérance, mais l'alimentation fléchissait)."));
        }

        static void Temp(List<PowerRow> rows, List<PowerRow> recent, Func<PowerRow, double?> get, string name, double high, double ceiling, List<CrashFinding> f)
        {
            var r = recent.Select(get).Where(v => v.HasValue && v.Value > 0).Select(v => v.Value).ToList();
            if (r.Count == 0) return;
            double peak = r.Max();
            var all = rows.Select(get).Where(v => v.HasValue && v.Value > 0).Select(v => v.Value).ToList();
            double first = all[0];
            if (peak >= ceiling)
                f.Add(new CrashFinding(2, "Température " + name + " au plafond juste avant l'arrêt : " + F(peak) + " °C. Un arrêt de protection thermique est possible (refroidissement, pâte thermique, ventilateur)."));
            else if (peak >= high)
                f.Add(new CrashFinding(1, "Température " + name + " très élevée juste avant l'arrêt : " + F(peak) + " °C" + (peak - first >= 10 ? " (en hausse : " + F(first) + " °C une minute plus tôt)" : "") + "."));
            else if (peak - first >= 15)
                f.Add(new CrashFinding(1, "Température " + name + " en forte hausse sur la dernière minute (" + F(first) + " → " + F(peak) + " °C)."));
        }

        /// <summary>Résumé (2-3 lignes) et gravité, stockés dans l'entrée.</summary>
        public static void Summarize(CrashEntry e, CrashAnalysis a)
        {
            e.level = a.MaxLevel >= 2 ? "critical" : "warning";
            // notification : la première phrase des deux indices principaux (le détail complet est dans la fenêtre)
            var lines = a.Findings.OrderByDescending(x => x.Level).Select(x => FirstSentence(x.Text, 160)).Take(2).ToList();
            if (a.HasData) lines.Add("Les " + a.Rows.Count + " dernières secondes du journal alimentation sont dans le détail.");
            e.summary = string.Join("\n", lines);
        }

        static string FirstSentence(string s, int max)
        {
            int i = s.IndexOf(". ", StringComparison.Ordinal);
            if (i > 0) s = s.Substring(0, i + 1);
            return s.Length <= max ? s : s.Substring(0, max - 1).TrimEnd() + "…";
        }

        // ------------------------------------------------------------------ texte à copier (forum / support)
        public static string BuildText(CrashEntry e, CrashAnalysis a, string machine)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Arrêt brutal du " + e.When.ToString("dd/MM/yyyy") + " vers " + e.When.ToString("HH:mm:ss") + " — rapport PerfMonitor");
            sb.Append("Windows : " + e.source + ", redémarrage à " + e.BootTime.ToString("dd/MM HH:mm:ss"));
            if (e.WinTime.HasValue) sb.Append(", arrêt noté à " + e.WinTime.Value.ToString("HH:mm:ss"));
            sb.Append(", écran bleu : " + (string.IsNullOrEmpty(e.bugcheck) ? "aucun" : e.bugcheck));
            if (e.whea > 0) sb.Append(", erreurs WHEA : " + e.whea);
            sb.AppendLine();
            if (!string.IsNullOrEmpty(machine)) sb.AppendLine("Machine : " + machine);
            sb.AppendLine();
            sb.AppendLine("Indices (mesures logicielles, à confirmer) :");
            foreach (var x in a.Findings.OrderByDescending(x => x.Level)) sb.AppendLine("- " + x.Text);
            if (a.HasData)
            {
                sb.AppendLine();
                sb.AppendLine("Dernières secondes avant l'arrêt (journal alimentation, 1 ligne/s ; tensions relues toutes les 5 s) :");
                sb.AppendLine(TableHeader());
                foreach (var r in a.Rows) sb.AppendLine(TableRow(r));
            }
            return sb.ToString();
        }

        static string C(double? v, int d, int w) => (v.HasValue ? v.Value.ToString(d == 0 ? "0" : d == 1 ? "0.0" : "0.00", CultureInfo.InvariantCulture) : "—").PadLeft(w);
        public static string TableHeader() => "heure     " + "FPS".PadLeft(5) + "12V".PadLeft(7) + "5V".PadLeft(6) + "3.3V".PadLeft(6) + "CPU W".PadLeft(7) + "GPU W".PadLeft(7) + "CPU°C".PadLeft(7) + "GPU°C".PadLeft(7) + "ventil".PadLeft(7) + "CPU%".PadLeft(6) + " jeu";
        public static string TableRow(PowerRow r) => r.Time.ToString("HH:mm:ss") + "  " + C(r.Fps, 0, 5) + C(r.V12, 2, 7) + C(r.V5, 2, 6) + C(r.V33, 2, 6) + C(r.CpuW, 0, 7) + C(r.GpuW, 0, 7) + C(r.CpuC, 0, 7) + C(r.GpuC, 0, 7) + C(r.FanMax, 0, 7) + C(r.CpuPct, 0, 6) + (r.Game == true ? "  oui" : r.Game == false ? "  non" : "    —");
    }
}
