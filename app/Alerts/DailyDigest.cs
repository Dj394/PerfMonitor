using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using PerfMonitorLive.Metrics;

namespace PerfMonitorLive.Alerts
{
    public class GameSession
    {
        public string ts { get; set; } public string game { get; set; } public double min { get; set; }
        public double cpuAvg { get; set; } public double cpuMax { get; set; } public double tempMax { get; set; } public double gpuMax { get; set; } public double memMax { get; set; }
        public DateTime Time => DateTime.TryParse(ts, out var t) ? t : DateTime.MinValue;
    }

    /// <summary>Bilan quotidien (veille) : texte prêt pour un toast et pour Telegram.</summary>
    public static class DailyDigest
    {
        public static List<GameSession> LoadSessions(DateTime from, DateTime to)
        {
            var res = new List<GameSession>();
            try
            {
                var f = Path.Combine(Paths.DataDir, "sessions.jsonl");
                if (File.Exists(f)) foreach (var l in File.ReadAllLines(f)) if (l.Length > 10) { try { var s = JsonSerializer.Deserialize<GameSession>(l); if (s.Time >= from && s.Time <= to) res.Add(s); } catch { } }
            }
            catch { }
            return res;
        }

        /// <summary>Construit le résumé du jour <paramref name="day"/> (par défaut la veille). Null si aucune donnée.</summary>
        public static (string title, string body) Build(DateTime day)
        {
            var from = day.Date; var to = from.AddDays(1).AddSeconds(-1);
            var samples = HistoryReader.Load(from, to);
            if (samples.Count < 12) return (null, null);
            var sb = new StringBuilder();
            string F(double v, string u) => double.IsNaN(v) ? "–" : Math.Round(v) + " " + u;
            var cpu = HistoryReader.Stats(samples, "cpu"); var mem = HistoryReader.Stats(samples, "memPct");
            var t = HistoryReader.Stats(samples, "temp"); var g = HistoryReader.Stats(samples, "gpuTemp");
            var hours = samples.Count * 5.0 / 3600;
            sb.Append("Suivi ").Append(hours.ToString("0.#")).Append(" h · CPU moy ").Append(F(cpu.avg, "%")).Append(", max ").Append(F(cpu.max, "%"));
            // pic CPU : quand ?
            var peak = samples.OrderByDescending(s => s.cpu).FirstOrDefault();
            if (peak != null && peak.procs.Count > 0) sb.Append(" (").Append(peak.Time.ToString("HH:mm")).Append(", ").Append(peak.procs[0].n).Append(")");
            sb.Append("\nRAM moy ").Append(F(mem.avg, "%")).Append(", max ").Append(F(mem.max, "%"));
            if (!double.IsNaN(t.max)) sb.Append(" · CPU max ").Append(F(t.max, "°C"));
            if (!double.IsNaN(g.max)) sb.Append(" · GPU max ").Append(F(g.max, "°C"));
            // disques
            var storNames = samples.SelectMany(s => s.stor).Select(s => s.n).Distinct().ToList();
            foreach (var n in storNames) { var st = HistoryReader.Stats(samples, "stor.temp:" + n); if (!double.IsNaN(st.max)) sb.Append("\n").Append(Short(n)).Append(" : max ").Append(F(st.max, "°C")); }
            var diskNames = samples.SelectMany(s => s.disks).Select(d => d.n).Distinct().ToList();
            foreach (var n in diskNames) { var lat = HistoryReader.Stats(samples, "disk.lat:" + n); if (!double.IsNaN(lat.p95) && lat.p95 >= 5) sb.Append("\nLatence disque ").Append(n.Split(' ').Last()).Append(" p95 ").Append(lat.p95.ToString("0")).Append(" ms"); }
            // alertes
            var alerts = HistoryReader.LoadAlerts(from, to).Where(a => a.sev != "Ok" && a.sev != "Info").ToList();
            sb.Append("\nAlertes : ").Append(alerts.Count);
            if (alerts.Count > 0) sb.Append(" (").Append(string.Join(", ", alerts.GroupBy(a => a.rule).OrderByDescending(x => x.Count()).Take(3).Select(x => x.Key + " ×" + x.Count()))).Append(")");
            // reboot
            var lastRb = samples.LastOrDefault(s => s.rb != null)?.rb;
            if (lastRb != null) sb.Append("\nScore redémarrage en fin de journée : ").Append(lastRb.score).Append("/100 (").Append(lastRb.Level.ToLower()).Append(")");
            // sessions de jeu
            var sess = LoadSessions(from, to);
            if (sess.Count > 0) sb.Append("\nJeu : ").Append(string.Join(", ", sess.Select(s => s.game + " " + Math.Round(s.min) + " min (GPU " + Math.Round(s.gpuMax) + " °C)")));
            return ("Bilan du " + day.ToString("dddd d MMMM"), sb.ToString());
        }
        static string Short(string n) => n.Length > 22 ? n.Substring(0, 21) + "…" : n;
    }
}
