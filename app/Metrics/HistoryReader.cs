using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PerfMonitorLive.Metrics
{
    public class Note { public string ts { get; set; } public string text { get; set; } public DateTime Time => DateTime.TryParse(ts, out var t) ? t : DateTime.MinValue; }
    public class Bucket { public DateTime T; public double Avg, Max, Min; public int N; }

    /// <summary>Lecture / agrégation des fichiers data\perf-*.jsonl, notes, alertes.</summary>
    public static class HistoryReader
    {
        /// <summary>Échantillons entre deux dates (lecture des fichiers concernés).</summary>
        public static List<Sample> Load(DateTime from, DateTime to)
        {
            var res = new List<Sample>();
            try
            {
                if (!Directory.Exists(Paths.DataDir)) return res;
                for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
                {
                    var f = Path.Combine(Paths.DataDir, "perf-" + d.ToString("yyyy-MM-dd") + ".jsonl");
                    if (!File.Exists(f)) continue;
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (line.Length < 30) continue;
                            // filtre rapide sur le timestamp sans parser tout le JSON
                            var ts = line.Substring(7, 19);
                            if (string.CompareOrdinal(ts, from.ToString("yyyy-MM-ddTHH:mm:ss")) < 0 || string.CompareOrdinal(ts, to.ToString("yyyy-MM-ddTHH:mm:ss")) > 0) continue;
                            try { var s = Sample.FromJson(line); if (s != null) res.Add(s); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex) { Paths.Log("History load: " + ex.Message); }
            return res;
        }

        /// <summary>Valeur d'une clé de carte dans un échantillon (mêmes clés que MainWindow / Rule).</summary>
        public static double? Value(Sample s, string key)
        {
            int i = key.IndexOf(':'); string m = i < 0 ? key : key.Substring(0, i), inst = i < 0 ? null : key.Substring(i + 1);
            switch (m)
            {
                case "cpu": return s.cpu;
                case "memPct": return s.memPct;
                case "temp": return s.temp;
                case "gpuTemp": return s.gpu;
                case "cpuMHz": return s.cpuMHz;
                case "gpuMHz": return s.gpuMHz;
                case "cpuW": return s.cpuW;
                case "gpuW": return s.gpuW;
                case "rx": return s.rx / 1024;
                case "tx": return s.tx / 1024;
                case "pageIn": return s.pageIn;
                case "reboot": return s.rb?.score;
                case "disk.pct": return s.Disk(inst)?.pct;
                case "disk.lat": return s.Disk(inst)?.lat;
                case "disk.rw": { var d = s.Disk(inst); return d == null ? (double?)null : d.r + d.w; }
                case "stor.temp": { var st = s.Stor(inst); return st == null || st.t <= 0 ? (double?)null : st.t; }
                case "stor.health": return s.Stor(inst)?.Errors;
                case "fan": return s.Fan(inst)?.rpm;
            }
            return null;
        }

        /// <summary>Agrège une série en seaux de <paramref name="stepSec"/> secondes.</summary>
        public static List<Bucket> Aggregate(IEnumerable<Sample> samples, string key, int stepSec)
        {
            var res = new List<Bucket>();
            Bucket cur = null; long curSlot = long.MinValue;
            foreach (var s in samples)
            {
                var v = Value(s, key); if (v == null) continue;
                long slot = (long)(s.Time.Ticks / TimeSpan.TicksPerSecond / stepSec);
                if (cur == null || slot != curSlot)
                {
                    if (cur != null) res.Add(cur);
                    cur = new Bucket { T = new DateTime(slot * stepSec * TimeSpan.TicksPerSecond), Avg = 0, Max = double.MinValue, Min = double.MaxValue, N = 0 }; curSlot = slot;
                }
                cur.Avg += v.Value; cur.N++; cur.Max = Math.Max(cur.Max, v.Value); cur.Min = Math.Min(cur.Min, v.Value);
            }
            if (cur != null) res.Add(cur);
            foreach (var b in res) b.Avg /= Math.Max(1, b.N);
            return res;
        }
        public static int StepFor(TimeSpan span) => span.TotalHours <= 1 ? 5 : span.TotalHours <= 6 ? 30 : span.TotalHours <= 24 ? 60 : span.TotalDays <= 7 ? 300 : 900;

        public static List<Note> LoadNotes()
        {
            var res = new List<Note>();
            try
            {
                var f = Path.Combine(Paths.DataDir, "notes.jsonl");
                if (File.Exists(f)) foreach (var l in File.ReadAllLines(f)) if (l.Length > 10) { try { res.Add(JsonSerializer.Deserialize<Note>(l)); } catch { } }
            }
            catch { }
            return res.OrderBy(n => n.Time).ToList();
        }
        public static void AddNote(string text)
        {
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                File.AppendAllText(Path.Combine(Paths.DataDir, "notes.jsonl"), JsonSerializer.Serialize(new { ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"), text }) + "\n", new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) { Paths.Log("note: " + ex.Message); }
        }

        public class AlertLine { public string ts { get; set; } public string rule { get; set; } public string sev { get; set; } public string title { get; set; } public string detail { get; set; } public DateTime Time => DateTime.TryParse(ts, out var t) ? t : DateTime.MinValue; }
        public static List<AlertLine> LoadAlerts(DateTime from, DateTime to)
        {
            var res = new List<AlertLine>();
            try
            {
                var f = Path.Combine(Paths.DataDir, "alerts.jsonl");
                if (File.Exists(f)) foreach (var l in File.ReadAllLines(f)) if (l.Length > 10) { try { var a = JsonSerializer.Deserialize<AlertLine>(l); if (a.Time >= from && a.Time <= to) res.Add(a); } catch { } }
            }
            catch { }
            return res;
        }

        /// <summary>Statistiques simples d'une clé sur une liste d'échantillons.</summary>
        public static (double avg, double p95, double max, int n) Stats(List<Sample> samples, string key)
        {
            var vals = samples.Select(s => Value(s, key)).Where(v => v.HasValue).Select(v => v.Value).OrderBy(v => v).ToList();
            if (vals.Count == 0) return (double.NaN, double.NaN, double.NaN, 0);
            return (vals.Average(), vals[Math.Min(vals.Count - 1, (int)(vals.Count * 0.95))], vals[vals.Count - 1], vals.Count);
        }
    }
}
