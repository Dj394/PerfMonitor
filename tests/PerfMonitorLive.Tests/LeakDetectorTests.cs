using System;
using System.Collections.Generic;
using System.Linq;
using PerfMonitorLive.Alerts;
using PerfMonitorLive.Metrics;
using Xunit;

namespace PerfMonitorLive.Tests
{
    public class LeakDetectorTests
    {
        static readonly DateTime T0 = new DateTime(2026, 3, 5, 8, 0, 0);

        static List<ProcSample> Procs(params (string n, double mem, double h)[] p) =>
            p.Select(x => new ProcSample { n = x.n, mem = x.mem, h = x.h }).ToList();

        /// <summary>Un point par minute pendant <paramref name="minutes"/> minutes.</summary>
        static LeakDetector Run(int minutes, Func<int, List<ProcSample>> at)
        {
            var d = new LeakDetector();
            for (int m = 0; m <= minutes; m++) d.Feed(T0.AddMinutes(m), at(m));
            return d;
        }

        [Fact]
        public void Croissance_memoire_soutenue_est_un_suspect()
        {
            // +10 Mo/min pendant 90 min : +600 Mo sur la dernière heure
            var d = Run(90, m => Procs(("leaky", 300 + 10 * m, 1000)));
            var s = d.Suspects().Single();
            Assert.Equal("leaky", s.Name);
            Assert.Equal(1200, s.MemNowMB);
            Assert.InRange(s.MemDelta1hMB, 590, 610);
            Assert.InRange(s.SpanMin, 89, 91);
            Assert.NotNull(d.Get("leaky"));
        }

        [Fact]
        public void Croissance_de_handles_est_un_suspect()
        {
            // +100 handles/min : +6000 sur l'heure, mémoire stable
            var d = Run(70, m => Procs(("handleleak", 200, 1000 + 100 * m)));
            Assert.Equal("handleleak", d.Suspects().Single().Name);
            Assert.True(d.Get("handleleak").HandlesDelta1h >= 5000);
        }

        [Fact]
        public void Pas_de_faux_positif_sur_valeur_stable()
        {
            var d = Run(200, m => Procs(("stable", 800, 2500)));
            Assert.Empty(d.Suspects());
            var info = d.Get("stable");
            Assert.NotNull(info);                // suivi, mais sans croissance
            Assert.Equal(0, info.MemDelta1hMB);
            Assert.Equal(0, info.MemDelta3hMB);
            Assert.Equal(0, info.HandlesDelta1h);
        }

        [Fact]
        public void Pas_de_faux_positif_sur_petite_oscillation()
        {
            var d = Run(200, m => Procs(("noisy", 800 + (m % 2 == 0 ? 20 : -20), 2500 + (m % 3) * 10)));
            Assert.Empty(d.Suspects());
        }

        [Fact]
        public void Croissance_courte_ou_trop_peu_de_points_n_est_pas_signalee()
        {
            // 8 points : sous le minimum de 10
            var few = Run(8, m => Procs(("grow", 300 + 100 * m, 1000)));
            Assert.Empty(few.Current);
            // 30 min seulement (SpanMin < 50) : même une forte croissance n'est pas encore « soutenue »
            var shortRun = Run(30, m => Procs(("grow", 300 + 50 * m, 1000)));
            Assert.Empty(shortRun.Suspects());
        }

        [Fact]
        public void Processus_insignifiants_ne_sont_pas_suivis()
        {
            var d = Run(90, m => Procs(("tiny", 50 + m, 100)));   // mem < 150 et handles < 3000
            Assert.Empty(d.Current);
        }

        [Fact]
        public void Feed_ignore_les_appels_a_moins_d_une_minute()
        {
            var d = new LeakDetector();
            for (int i = 0; i < 100; i++) d.Feed(T0.AddSeconds(i * 5), Procs(("p", 500 + i, 1000)));   // 500 s au total : 9 points
            Assert.Empty(d.Current);
        }

        [Fact]
        public void Processus_disparu_est_oublie()
        {
            var d = new LeakDetector();
            int m = 0;
            for (; m <= 20; m++) d.Feed(T0.AddMinutes(m), Procs(("gone", 500, 1000)));
            Assert.NotNull(d.Get("gone"));
            for (; m <= 40; m++) d.Feed(T0.AddMinutes(m), Procs(("other", 500, 1000)));
            Assert.Null(d.Get("gone"));
        }
    }
}
