using System;
using System.Collections.Generic;
using System.Linq;
using PerfMonitorLive.Metrics;

namespace PerfMonitorLive.Alerts
{
    /// <summary>Suivi mémoire/handles par processus (1 point/min, 3 h) pour repérer les fuites.</summary>
    public class LeakDetector
    {
        struct Pt { public DateTime T; public double Mem, Handles; }
        readonly Dictionary<string, List<Pt>> _hist = new Dictionary<string, List<Pt>>();
        DateTime _last = DateTime.MinValue;
        public List<LeakInfo> Current { get; private set; } = new List<LeakInfo>();
        static readonly TimeSpan Keep = TimeSpan.FromHours(3), Recent = TimeSpan.FromMinutes(2), Hour = TimeSpan.FromMinutes(60);
        // réutilisés d'un appel à l'autre (Feed n'est appelé que par le thread de collecte)
        readonly HashSet<string> _seen = new HashSet<string>();
        readonly List<string> _gone = new List<string>();
        DateTime _now;
        readonly Predicate<Pt> _tooOld;
        public LeakDetector() { _tooOld = p => _now - p.T > Keep; }   // délégué créé une fois (pas de fermeture par appel)

        /// <summary>À appeler avec la liste complète (nom, mem Mo, handles), un élément par programme (noms uniques :
        /// le <see cref="Sampler"/> regroupe déjà les instances) — enregistre 1 point/min et recalcule.</summary>
        public void Feed(DateTime now, List<ProcSample> procs)
        {
            if ((now - _last).TotalSeconds < 60) return;
            _last = now; _now = now;
            _seen.Clear();
            foreach (var p in procs)
            {
                double mem = p.mem, h = p.h;
                if (mem < 150 && h < 3000) continue;   // on ne suit que les processus significatifs
                _seen.Add(p.n);
                if (!_hist.TryGetValue(p.n, out var l)) _hist[p.n] = l = new List<Pt>();
                l.Add(new Pt { T = now, Mem = mem, Handles = h });
                l.RemoveAll(_tooOld);
            }
            _gone.Clear();
            foreach (var kv in _hist)
            {
                if (_seen.Contains(kv.Key)) continue;
                kv.Value.Add(new Pt { T = now, Mem = double.NaN, Handles = double.NaN });
                int nan = 0; foreach (var p in kv.Value) if (double.IsNaN(p.Mem)) nan++;
                if (nan > 5) _gone.Add(kv.Key);
            }
            foreach (var k in _gone) _hist.Remove(k);

            var res = new List<LeakInfo>();
            foreach (var kv in _hist)
            {
                // points valides (hors absences) : premier, premier de la dernière heure, dernier
                int count = 0, first = -1, firstHour = -1, last = -1;
                var pts = kv.Value;
                for (int i = 0; i < pts.Count; i++)
                {
                    if (double.IsNaN(pts[i].Mem)) continue;
                    count++; last = i;
                    if (first < 0) first = i;
                    if (firstHour < 0 && now - pts[i].T <= Hour) firstHour = i;
                }
                if (count < 10) continue;
                var pl = pts[last];
                if (now - pl.T > Recent) continue;
                var p3 = pts[first];
                res.Add(new LeakInfo
                {
                    Name = kv.Key, MemNowMB = pl.Mem, HandlesNow = pl.Handles,
                    MemDelta1hMB = firstHour < 0 ? 0 : pl.Mem - pts[firstHour].Mem,
                    MemDelta3hMB = pl.Mem - p3.Mem,
                    HandlesDelta1h = firstHour < 0 ? 0 : pl.Handles - pts[firstHour].Handles,
                    SpanMin = (pl.T - p3.T).TotalMinutes
                });
            }
            Current = res;
        }

        /// <summary>Fuites probables : croissance soutenue et monotone.</summary>
        public IEnumerable<LeakInfo> Suspects() => Current.Where(l =>
            (l.SpanMin >= 50 && l.MemDelta1hMB >= 400) || (l.SpanMin >= 150 && l.MemDelta3hMB >= 1000) || (l.SpanMin >= 50 && l.HandlesDelta1h >= 5000));

        public LeakInfo Get(string name) => Current.FirstOrDefault(l => l.Name == name);
    }
}
