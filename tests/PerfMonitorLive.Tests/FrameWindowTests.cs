using System;
using System.Collections.Generic;
using System.Text.Json;
using PerfMonitorLive.Alerts;
using PerfMonitorLive.Metrics;
using Xunit;

namespace PerfMonitorLive.Tests
{
    /// <summary>FPS enrichis : temps d'image moyen, « 1 % low », saccades, sur des séries d'horodatages synthétiques.</summary>
    public class FrameWindowTests
    {
        const double W = 2000, Stale = 3000;

        /// <summary>Ajoute des images aux écarts donnés à partir de <paramref name="t"/> ; renvoie l'horodatage de la dernière.</summary>
        static double Series(FrameWindow w, int pid, double t, IEnumerable<double> gaps)
        {
            w.Add(pid, t);
            foreach (var g in gaps) { t += g; w.Add(pid, t); }
            return t;
        }

        static IEnumerable<double> Gaps(int count, double ms, params (int index, double ms)[] hitches)
        {
            for (int i = 0; i < count; i++)
            {
                double g = ms;
                foreach (var h in hitches) if (h.index == i) g = h.ms;
                yield return g;
            }
        }

        [Fact]
        public void Serie_reguliere_low_egal_au_debit_et_aucune_saccade()
        {
            var w = new FrameWindow(); w.Reset(1);
            double last = Series(w, 1, 0, Gaps(100, 1000.0 / 60));
            var st = w.Stats(last, W, Stale);
            Assert.Equal(60, st.Fps);
            Assert.Equal(60, st.Low1);
            Assert.Equal(16.67, st.FrameMs);
            Assert.Equal(0, st.Stutters);
            Assert.Equal(0, w.StutterTotal);
        }

        [Fact]
        public void Serie_saccadee_low_suit_les_pires_images_et_saccades_comptees()
        {
            // 150 images à 10 ms dont 3 à 50 ms (> 2,5 × la médiane de 10 ms) : 1620 ms, tout dans la fenêtre de 2 s
            var w = new FrameWindow(); w.Reset(1);
            double last = Series(w, 1, 0, Gaps(150, 10, (40, 50), (80, 50), (120, 50)));
            var st = w.Stats(last, W, Stale);
            Assert.Equal(93, st.Fps);            // 150 images en 1620 ms
            Assert.Equal(10.8, st.FrameMs);
            Assert.Equal(20, st.Low1);           // 99e centile des temps d'image = 50 ms (3 % des images)
            Assert.Equal(3, st.Stutters);
            Assert.Equal(3, w.StutterTotal);

            // relue sans nouvelle image : la fenêtre en compte toujours 3, le total ne les recompte pas
            Assert.Equal(3, w.Stats(last + 500, W, Stale).Stutters);
            Assert.Equal(3, w.StutterTotal);

            // une saccade de plus (40 ms > 25 ms) dans le lot suivant : total 4
            last = Series(w, 1, last + 10, Gaps(20, 10, (5, 40)));
            Assert.Equal(4, w.Stats(last, W, Stale).Stutters);
            Assert.Equal(4, w.StutterTotal);
        }

        [Fact]
        public void Une_seule_saccade_sur_beaucoup_d_images_ne_fait_pas_le_1_pourcent()
        {
            // 199 temps d'image dont un seul long (0,5 %) : le 99e centile reste à 10 ms, mais la saccade est comptée
            var w = new FrameWindow(); w.Reset(1);
            double last = Series(w, 1, 0, Gaps(199, 10, (100, 60)));
            var st = w.Stats(last, W, Stale);
            Assert.Equal(100, st.Low1);
            Assert.Equal(1, st.Stutters);
        }

        [Fact]
        public void Images_livrees_dans_le_desordre_donnent_le_meme_resultat()
        {
            var a = new FrameWindow(); a.Reset(1);
            var b = new FrameWindow(); b.Reset(1);
            var ts = new List<double>(); double t = 0;
            ts.Add(t); foreach (var g in Gaps(150, 10, (40, 50), (80, 50), (120, 50))) { t += g; ts.Add(t); }
            foreach (var x in ts) a.Add(1, x);
            for (int i = ts.Count - 1; i >= 0; i--) b.Add(1, ts[i]);
            var sa = a.Stats(t, W, Stale); var sb = b.Stats(t, W, Stale);
            Assert.Equal(sa.Fps, sb.Fps); Assert.Equal(sa.Low1, sb.Low1); Assert.Equal(sa.FrameMs, sb.FrameMs); Assert.Equal(sa.Stutters, sb.Stutters);
        }

        [Fact]
        public void Fenetre_vide_tout_est_null()
        {
            var w = new FrameWindow(); w.Reset(1);
            var st = w.Stats(0, W, Stale);
            Assert.Null(st.Fps); Assert.Null(st.Low1); Assert.Null(st.FrameMs); Assert.Null(st.Stutters);
            w.Add(1, 5);                                   // une seule image : toujours rien
            st = w.Stats(10, W, Stale);
            Assert.Null(st.Fps); Assert.Null(st.Low1); Assert.Null(st.FrameMs); Assert.Null(st.Stutters);
            Assert.Equal(0, w.StutterTotal);
        }

        [Fact]
        public void Reset_vide_la_fenetre_mais_garde_le_total_des_saccades()
        {
            var w = new FrameWindow(); w.Reset(1);
            double last = Series(w, 1, 0, Gaps(150, 10, (40, 50)));
            Assert.Equal(1, w.Stats(last, W, Stale).Stutters);
            w.Reset(2);                                    // changement de jeu
            Assert.Null(w.Stats(last, W, Stale).Fps);
            Assert.Equal(1, w.StutterTotal);
            last = Series(w, 2, 0, Gaps(150, 10, (40, 50)));   // horodatages repartis de 0 : comptés quand même
            Assert.Equal(1, w.Stats(last + 10_000, W, Stale).Stutters);
            Assert.Equal(2, w.StutterTotal);
        }

        [Fact]
        public void Session_de_jeu_resume_fps_moyen_low_et_saccades()
        {
            GameSession saved = null;
            var d = new GameDetector(new Settings()) { Log = _ => { }, Save = s => saved = s };
            var t = new DateTime(2026, 1, 1, 20, 0, 0);
            for (int s = 0; s <= 10; s += 2)
                d.Step("GameA", 10, t.AddSeconds(s), new Sample { fps = s >= 8 ? 90 + s : (double?)null, fps1 = 60, StutterTotal = 5 + s });
            for (int s = 12; s <= 40; s += 2)
                d.Step(null, 0, t.AddSeconds(s), new Sample { StutterTotal = 20 });   // alt-tab puis fin au bout de 30 s
            Assert.NotNull(saved);
            Assert.Equal(99, saved.fpsAvg);                // (98 + 100) / 2
            Assert.Equal(60, saved.fps1);
            Assert.Equal(7, saved.sacc);                   // 20 - 13 (total au début de la session)
            Assert.Contains("1 % low 60", saved.FpsText);
            Assert.Contains("7 saccades", saved.FpsText);
        }

        [Fact]
        public void Gel_de_2_5_s_compte_comme_saccade_a_la_reprise()
        {
            // 1 s à 10 ms, gel de 2,5 s, reprise à 10 ms ; lectures toutes les secondes (horloge du lecteur = horodatages)
            var w = new FrameWindow(); w.Reset(1);
            double t0 = Series(w, 1, 0, Gaps(100, 10));                  // dernière image avant le gel : 1000
            Assert.Equal(0, w.Stats(1000, W, Stale).Stutters);
            Assert.Equal(0, w.Stats(2000, W, Stale).Stutters);             // 1 s sans image : simple retard de livraison possible
            var gel = w.Stats(3000, W, Stale);                             // 2 s sans image : gel en cours
            Assert.Equal(0, gel.Low1);                                      // 1 % low ramené au temps d'image en cours (2 s)
            Assert.Equal(1, gel.Stutters);
            Assert.Equal(0, w.StutterTotal);                                // compté une seule fois, à la reprise
            double last = Series(w, 1, t0 + 2500, Gaps(100, 10));          // reprise à 3500, jusqu'à 4500
            var st = w.Stats(4500, W, Stale);                              // fenêtre [2500, 4500] : le gel commence avant elle
            Assert.Equal(1, st.Stutters);
            Assert.Equal(1, w.StutterTotal);
            Assert.Equal(100, st.Fps);                                      // débit et temps moyen : fenêtre seule
            Assert.Equal(10, st.FrameMs);
            Assert.Equal(1, w.Stats(5000, W, Stale).Stutters);              // relue : pas recomptée
            Assert.Equal(1, w.StutterTotal);
            Assert.True(Count(w.FrameTimes, 2500));                         // l'intervalle de 2,5 s est dans l'histogramme (débordement)
        }

        static bool Count(FrameTimeHistogram h, double ms)
        {
            var c = new long[FrameTimeHistogram.Bins]; h.CopyTo(c);
            int i = ms >= FrameTimeHistogram.MaxMs ? FrameTimeHistogram.Bins - 1 : (int)(ms / FrameTimeHistogram.BinMs);
            return c[i] > 0;
        }

        [Fact]
        public void Histogramme_compte_chaque_image_une_seule_fois()
        {
            var w = new FrameWindow(); w.Reset(1);
            double last = Series(w, 1, 0, Gaps(150, 10, (40, 50)));
            w.Stats(last, W, Stale); w.Stats(last + 500, W, Stale);
            var c = new long[FrameTimeHistogram.Bins]; w.FrameTimes.CopyTo(c);
            long total = 0; foreach (var x in c) total += x;
            Assert.Equal(150, total);
            Assert.InRange(FrameTimeHistogram.Percentile(c, null, 0.999).Value, 49.9, 50.2);
            Assert.InRange(FrameTimeHistogram.Percentile(c, null, 0.99).Value, 9.9, 10.2);
        }

        [Fact]
        public void Low_de_session_est_le_centile_de_toutes_les_images()
        {
            // 10 % du temps à 30 FPS, 90 % à 120 FPS : ~2,7 % des images à 33 ms → vrai 1 % low ~30, pas la moyenne des fenêtres (~111)
            var h = new FrameTimeHistogram();
            GameSession saved = null;
            var d = new GameDetector(new Settings()) { Log = _ => { }, Save = s => saved = s, FrameTimes = h };
            var t = new DateTime(2026, 1, 1, 20, 0, 0);
            for (int i = 0; i < 500; i++) h.Add(5);                         // avant la session : ignorées
            for (int s = 0; s <= 8; s += 2) d.Step("GameA", 10, t.AddSeconds(s), new Sample { fps = 120, fps1 = 115 });
            for (int i = 0; i < 6 * 30; i++) h.Add(1000.0 / 30);
            for (int i = 0; i < 54 * 120; i++) h.Add(1000.0 / 120);
            d.Step("GameA", 10, t.AddSeconds(10), new Sample { fps = 120, fps1 = 115 });
            d.ForceEnd();
            Assert.Equal(30, saved.fps1);
        }

        [Fact]
        public void Session_sans_fps_et_anciennes_lignes_restent_lisibles()
        {
            GameSession saved = null;
            var d = new GameDetector(new Settings()) { Log = _ => { }, Save = s => saved = s };
            var t = new DateTime(2026, 1, 1, 20, 0, 0);
            for (int s = 0; s <= 10; s += 2) d.Step("GameA", 10, t.AddSeconds(s), new Sample());
            d.ForceEnd();
            Assert.Null(saved.fpsAvg); Assert.Null(saved.fps1); Assert.Null(saved.sacc); Assert.Null(saved.FpsText);
            var json = JsonSerializer.Serialize(saved);
            Assert.DoesNotContain("fps", json);            // ligne identique à l'ancien format
            var old = JsonSerializer.Deserialize<GameSession>("{\"ts\":\"2026-01-01T20:00:00\",\"game\":\"x\",\"min\":12,\"cpuAvg\":30,\"cpuMax\":80,\"tempMax\":70,\"gpuMax\":65,\"memMax\":50}");
            Assert.Null(old.fpsAvg); Assert.Null(old.FpsText);
        }

        [Fact]
        public void Echantillon_historique_retrocompatible()
        {
            var s = Sample.FromJson("{\"ts\":\"2026-01-01T20:00:00\",\"cpu\":10,\"fps\":120}");
            Assert.Null(s.fps1); Assert.Null(s.fpsMs); Assert.Null(s.sacc);
            s.fps1 = 80; s.fpsMs = 8.33; s.sacc = 1;
            var r = Sample.FromJson(s.ToJson());
            Assert.Equal(80, HistoryReader.Value(r, "fps1"));
            Assert.Equal(8.33, HistoryReader.Value(r, "fpsMs"));
            Assert.Equal(1, r.sacc);
            Assert.DoesNotContain("StutterTotal", s.ToJson());
        }
    }
}
