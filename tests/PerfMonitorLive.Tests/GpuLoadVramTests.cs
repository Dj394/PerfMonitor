using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PerfMonitorLive.Alerts;
using PerfMonitorLive.Metrics;
using Xunit;

namespace PerfMonitorLive.Tests
{
    /// <summary>Charge GPU et VRAM : sérialisation dans l'historique (et relecture des anciens fichiers), pourcentage de VRAM,
    /// règle d'alerte, indice « limité par le GPU / par le CPU » et rapport de session.</summary>
    public class GpuLoadVramTests
    {
        static Sample WithGpu(DateTime t)
        {
            var s = TestData.FullSample(t);
            s.gpuLoad = 97.5; s.vram = 7.81; s.vramPct = 65.1; s.cpuCore = 88;
            return s;
        }

        [Fact]
        public void Aller_retour_json_des_champs_gpu()
        {
            var t = new DateTime(2026, 3, 5, 21, 0, 0);
            var line = WithGpu(t).ToJson();
            Assert.Contains("\"gpuLoad\":97.5", line);
            Assert.Contains("\"vram\":7.81", line);
            Assert.Contains("\"vramPct\":65.1", line);
            Assert.Contains("\"cpuCore\":88", line);
            var r = Sample.FromJson(line);
            Assert.Equal(97.5, r.gpuLoad); Assert.Equal(7.81, r.vram); Assert.Equal(65.1, r.vramPct); Assert.Equal(88, r.cpuCore);
        }

        [Fact]
        public void Champs_absents_ne_sont_pas_ecrits()
        {
            var line = TestData.MinimalSample(new DateTime(2026, 3, 5, 21, 0, 0)).ToJson();
            Assert.DoesNotContain("gpuLoad", line);
            Assert.DoesNotContain("vram", line);
            Assert.DoesNotContain("cpuCore", line);
        }

        [Fact]
        public void Ancien_fichier_sans_ces_champs_se_relit()
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 3, 5, 21, 0, 0);
            // ligne telle qu'écrite par une version antérieure (sans gpuLoad / vram / vramPct / cpuCore)
            var old = TestData.FullSample(t).ToJson();
            Assert.DoesNotContain("gpuLoad", old);
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllLines(Path.Combine(Paths.DataDir, "perf-2026-03-05.jsonl"), new[] { old, WithGpu(t.AddSeconds(5)).ToJson() });

            var list = HistoryReader.Load(t.AddMinutes(-1), t.AddMinutes(1));
            Assert.Equal(2, list.Count);
            Assert.Null(list[0].gpuLoad); Assert.Null(list[0].vram); Assert.Null(list[0].vramPct); Assert.Null(list[0].cpuCore);
            Assert.Equal(61.5, list[0].temp);   // le reste de l'ancienne ligne est intact
            Assert.Null(HistoryReader.Value(list[0], "gpuLoad"));
            Assert.Equal(97.5, HistoryReader.Value(list[1], "gpuLoad"));
            Assert.Equal(65.1, HistoryReader.Value(list[1], "vramPct"));
            Assert.Equal(7.81, HistoryReader.Value(list[1], "vram"));
            // courbe de l'historique : seule la mesure qui a la valeur compte
            var b = HistoryReader.Aggregate(list, "gpuLoad", 60);
            Assert.Single(b); Assert.Equal(1, b[0].N); Assert.Equal(97.5, b[0].Avg);
        }

        [Theory]
        [InlineData(4096, 8192.0, 50.0)]
        [InlineData(8192, 8192.0, 100.0)]
        [InlineData(8300, 8192.0, 100.0)]   // léger dépassement (arrondis des capteurs) : plafonné
        [InlineData(9000, 8192.0, null)]    // incohérent : pas de pourcentage
        [InlineData(4096, null, null)]      // total inconnu
        [InlineData(4096, 0.0, null)]
        public void Pourcentage_de_vram(double used, double? total, double? expected)
        {
            Assert.Equal(expected, Hardware.VramPercent(used, total));
        }

        [Fact]
        public void Regle_vram_desactivee_par_defaut_et_lit_vramPct()
        {
            var s = new Settings(); s.EnsureDefaults();
            var r = s.Get("vramPct");
            Assert.NotNull(r);
            Assert.False(r.Enabled);
            Assert.Equal(95, r.Threshold); Assert.Equal(60, r.SustainSec);
            Assert.Equal("%", r.Unit);
            Assert.Equal(65.1, r.Value(WithGpu(DateTime.Now)));
            Assert.Null(r.Value(TestData.MinimalSample(DateTime.Now)));
        }

        [Theory]
        [InlineData(99.0, 50.0, 30.0, Bottleneck.Gpu)]     // GPU à fond
        [InlineData(95.0, 100.0, 90.0, Bottleneck.Gpu)]    // GPU à fond même si le CPU l'est aussi
        [InlineData(60.0, 98.0, 25.0, Bottleneck.Cpu)]     // un cœur saturé, CPU total bas : le fil principal du jeu
        [InlineData(79.9, 90.0, 20.0, Bottleneck.Cpu)]
        [InlineData(60.0, 70.0, 40.0, Bottleneck.None)]    // GPU bas, aucun cœur saturé : FPS plafonnés (V-Sync, limiteur)
        [InlineData(85.0, 100.0, 50.0, Bottleneck.None)]   // zone grise 80-95 % : pas de conclusion
        [InlineData(null, 100.0, 100.0, Bottleneck.None)]  // pas de charge GPU : rien à dire
        public void Classification_d_une_mesure(double? gpu, double? core, double cpu, int expected)
        {
            Assert.Equal(expected, Bottleneck.Classify(gpu, core, cpu));
        }

        [Fact]
        public void Sans_mesure_par_coeur_le_cpu_total_sert_de_repli()
        {
            Assert.Equal(Bottleneck.Cpu, Bottleneck.Classify(50, null, 92));
            Assert.Equal(Bottleneck.None, Bottleneck.Classify(50, null, 40));
        }

        [Theory]
        [InlineData(6, 0, 10, "gpu")]
        [InlineData(5, 0, 10, "gpu")]
        [InlineData(1, 7, 10, "cpu")]
        [InlineData(3, 3, 10, "")]
        [InlineData(0, 0, 0, null)]
        public void Verdict_de_session(int nGpu, int nCpu, int n, string expected)
        {
            Assert.Equal(expected, Bottleneck.Verdict(nGpu, nCpu, n));
        }

        static GameSession PlaySession(Func<int, Sample> sample)
        {
            GameSession saved = null;
            var d = new GameDetector(new Settings()) { Log = _ => { }, Save = s => saved = s };
            var t = new DateTime(2026, 1, 1, 20, 0, 0);
            for (int i = 0; i <= 60; i += 2) d.Step("Jeu", 10, t.AddSeconds(i), sample(i));
            d.Step(null, 0, t.AddSeconds(100), null);   // 30 s sans jeu : fin de session
            Assert.NotNull(saved);
            return saved;
        }

        [Fact]
        public void Session_limitee_par_le_gpu()
        {
            var sess = PlaySession(i => new Sample { cpu = 35, gpuLoad = 98, cpuCore = 70 });
            Assert.Equal(98, sess.gpuLoadAvg);
            Assert.Equal("gpu", sess.bound);
            Assert.Contains("GPU 98 % moy · limité par le GPU", sess.GpuText);
        }

        [Fact]
        public void Session_limitee_par_le_cpu()
        {
            var sess = PlaySession(i => new Sample { cpu = 30, gpuLoad = 55, cpuCore = 99 });
            Assert.Equal("cpu", sess.bound);
            Assert.Contains("limité par le CPU (probable)", sess.GpuText);
        }

        [Fact]
        public void Session_sans_charge_gpu_et_ancienne_session_relue()
        {
            var sess = PlaySession(i => new Sample { cpu = 30 });
            Assert.Null(sess.gpuLoadAvg); Assert.Null(sess.bound); Assert.Equal("", sess.GpuText);

            // ligne de sessions.jsonl écrite avant l'ajout de ces champs
            var old = JsonSerializer.Deserialize<GameSession>("{\"ts\":\"2026-01-01T20:00:00\",\"game\":\"Jeu\",\"min\":12.5,\"cpuAvg\":40,\"cpuMax\":90,\"tempMax\":80,\"gpuMax\":70,\"memMax\":60}");
            Assert.Null(old.gpuLoadAvg); Assert.Null(old.bound); Assert.Equal("", old.GpuText);

            // aller-retour avec les nouveaux champs ; le texte calculé n'est pas sérialisé
            var json = JsonSerializer.Serialize(new GameSession { ts = "2026-01-01T20:00:00", game = "Jeu", gpuLoadAvg = 87, bound = "" });
            Assert.DoesNotContain("GpuText", json);
            var back = JsonSerializer.Deserialize<GameSession>(json);
            Assert.Equal(87, back.gpuLoadAvg); Assert.Equal("", back.bound);
            Assert.Equal(" · GPU 87 % moy · ni CPU ni GPU à fond", back.GpuText);
        }
    }
}
