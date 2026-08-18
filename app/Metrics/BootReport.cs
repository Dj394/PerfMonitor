using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace PerfMonitorLive.Metrics
{
    public class BootSlow { public string n { get; set; } public double ms { get; set; } public string kind { get; set; } }
    public class BootEntry
    {
        public string ts { get; set; }        // date/heure du démarrage
        public double bootMs { get; set; }    // temps total (BootTime)
        public double mainMs { get; set; }    // MainPathBootTime (jusqu'au bureau)
        public double postMs { get; set; }    // BootPostBootTime (fin de chargement après le bureau)
        public List<BootSlow> slow { get; set; } = new List<BootSlow>();
        [System.Text.Json.Serialization.JsonIgnore] public DateTime Time => DateTime.TryParse(ts, out var t) ? t : DateTime.MinValue;
    }

    /// <summary>Lit le journal « Diagnostics-Performance » (événements 100..103) et tient data\boot.jsonl à jour.</summary>
    public static class BootReport
    {
        static string File_ => Path.Combine(Paths.DataDir, "boot.jsonl");
        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions();

        public static List<BootEntry> Load()
        {
            var list = new List<BootEntry>();
            try { if (File.Exists(File_)) foreach (var l in File.ReadAllLines(File_)) if (l.Length > 10) list.Add(JsonSerializer.Deserialize<BootEntry>(l, Opts)); } catch (Exception ex) { Paths.Log("boot load: " + ex.Message); }
            return list.OrderBy(b => b.Time).ToList();
        }

        /// <summary>Met à jour boot.jsonl avec les démarrages non encore enregistrés. Renvoie le nombre ajouté.</summary>
        public static int Refresh()
        {
            int added = 0;
            try
            {
                var known = new HashSet<string>(Load().Select(b => b.ts));
                var q = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[(EventID>=100 and EventID<=103)]]") { ReverseDirection = true };
                var entries = new Dictionary<DateTime, BootEntry>();
                var slows = new List<(DateTime t, BootSlow s)>();
                using (var reader = new EventLogReader(q))
                {
                    EventRecord rec; int n = 0;
                    while ((rec = reader.ReadEvent()) != null && n++ < 400)
                    {
                        using (rec)
                        {
                            var data = ParseData(rec.ToXml());
                            var t = rec.TimeCreated ?? DateTime.MinValue;
                            if (rec.Id == 100)
                            {
                                var start = data.TryGetValue("BootStartTime", out var bs) && DateTime.TryParse(bs, null, System.Globalization.DateTimeStyles.RoundtripKind, out var st) ? st.ToLocalTime() : t;
                                var e = new BootEntry { ts = start.ToString("yyyy-MM-ddTHH:mm:ss"), bootMs = D(data, "BootTime"), mainMs = D(data, "MainPathBootTime"), postMs = D(data, "BootPostBootTime") };
                                entries[start] = e;
                            }
                            else
                            {
                                var name = data.TryGetValue("Name", out var nm) ? nm : data.TryGetValue("FriendlyName", out var fn) ? fn : data.TryGetValue("FileName", out var f) ? f : "?";
                                var ms = D(data, "TotalTime"); if (ms <= 0) ms = D(data, "DegradationTime");
                                if (ms > 0) slows.Add((t, new BootSlow { n = name, ms = ms, kind = rec.Id == 101 ? "application" : rec.Id == 102 ? "pilote" : "service" }));
                            }
                        }
                    }
                }
                foreach (var kv in entries)
                {
                    var e = kv.Value;
                    // événements 101-103 émis dans les minutes qui suivent le démarrage
                    e.slow = slows.Where(s => s.t >= kv.Key && s.t <= kv.Key.AddMinutes(30)).Select(s => s.s).OrderByDescending(s => s.ms).Take(10).ToList();
                    if (known.Contains(e.ts)) continue;
                    Directory.CreateDirectory(Paths.DataDir);
                    File.AppendAllText(File_, JsonSerializer.Serialize(e, Opts) + "\n", new UTF8Encoding(false));
                    added++;
                }
            }
            catch (UnauthorizedAccessException) { Paths.Log("BootReport : journal Diagnostics-Performance inaccessible (droits administrateur requis) — démarrages Windows non relevés"); }
            catch (Exception ex) { Paths.Log("BootReport: " + ex.Message); }
            return added;
        }

        static double D(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : 0;
        static Dictionary<string, string> ParseData(string xml)
        {
            var d = new Dictionary<string, string>();
            try
            {
                var doc = new XmlDocument(); doc.LoadXml(xml);
                foreach (XmlNode n in doc.GetElementsByTagName("Data")) { var name = n.Attributes?["Name"]?.Value; if (name != null) d[name] = n.InnerText; }
            }
            catch { }
            return d;
        }
    }
}
