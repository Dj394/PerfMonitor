using System;
using System.Collections.Generic;
using System.Linq;
using PerfMonitorLive.Metrics;

namespace PerfMonitorLive.Alerts
{
    /// <summary>Suivi mémoire/handles par processus (1 point/min, 3 h) pour repérer les fuites.</summary>
    public class LeakDetector
    {
        class Pt { public DateTime T; public double Mem, Handles; }
        readonly Dictionary<string, List<Pt>> _hist = new Dictionary<string, List<Pt>>();
        DateTime _last = DateTime.MinValue;
        public List<LeakInfo> Current { get; private set; } = new List<LeakInfo>();
        static readonly TimeSpan Keep = TimeSpan.FromHours(3);

        /// <summary>À appeler avec la liste complète (nom, mem Mo, handles) — enregistre 1 point/min et recalcule.</summary>
        public void Feed(DateTime now, IEnumerable<ProcSample> procs)
        {
            if ((now - _last).TotalSeconds < 60) return;
            _last = now;
            var seen = new HashSet<string>();
            foreach (var g in procs.GroupBy(p => p.n))
            {
                var mem = g.Sum(p => p.mem); var h = g.Sum(p => p.h);
                if (mem < 150 && h < 3000) continue;   // on ne suit que les processus significatifs
                seen.Add(g.Key);
                if (!_hist.TryGetValue(g.Key, out var l)) _hist[g.Key] = l = new List<Pt>();
                l.Add(new Pt { T = now, Mem = mem, Handles = h });
                l.RemoveAll(p => now - p.T > Keep);
            }
            foreach (var k in _hist.Keys.ToList()) if (!seen.Contains(k)) { _hist[k].Add(new Pt { T = now, Mem = double.NaN, Handles = double.NaN }); if (_hist[k].Count(p => double.IsNaN(p.Mem)) > 5) _hist.Remove(k); }

            var res = new List<LeakInfo>();
            foreach (var kv in _hist)
            {
                var pts = kv.Value.Where(p => !double.IsNaN(p.Mem)).ToList();
                if (pts.Count < 10) continue;
                var last = pts[pts.Count - 1];
                if (now - last.T > TimeSpan.FromMinutes(2)) continue;
                var p1 = pts.FirstOrDefault(p => now - p.T <= TimeSpan.FromMinutes(60));
                var p3 = pts[0];
                var info = new LeakInfo
                {
                    Name = kv.Key, MemNowMB = last.Mem, HandlesNow = last.Handles,
                    MemDelta1hMB = p1 == null ? 0 : last.Mem - p1.Mem,
                    MemDelta3hMB = last.Mem - p3.Mem,
                    HandlesDelta1h = p1 == null ? 0 : last.Handles - p1.Handles,
                    SpanMin = (last.T - p3.T).TotalMinutes
                };
                res.Add(info);
            }
            Current = res;
        }

        /// <summary>Fuites probables : croissance soutenue et monotone.</summary>
        public IEnumerable<LeakInfo> Suspects() => Current.Where(l =>
            (l.SpanMin >= 50 && l.MemDelta1hMB >= 400) || (l.SpanMin >= 150 && l.MemDelta3hMB >= 1000) || (l.SpanMin >= 50 && l.HandlesDelta1h >= 5000));

        public LeakInfo Get(string name) => Current.FirstOrDefault(l => l.Name == name);
    }
}
