using System;
using System.IO;
using System.Linq;
using PerfMonitorLive.Metrics;
using Xunit;

namespace PerfMonitorLive.Tests
{
    /// <summary>Revue, groupe « données » : défauts 7 (historique FPS / tensions / batterie) et 9 (isolation du banc).</summary>
    public class ReviewdonneesTests
    {
        [Theory]
        [InlineData("fps", 143.9)]
        [InlineData("v12", 12.052)]
        [InlineData("v5", 5.01)]
        [InlineData("v33", 3.3)]
        [InlineData("bat", 80)]
        public void Historique_lit_fps_tensions_et_batterie(string key, double expected)
        {
            var t = new DateTime(2026, 3, 5, 14, 7, 9);
            var s = TestData.FullSample(t);
            Assert.Equal(expected, HistoryReader.Value(s, key));
            Assert.Null(HistoryReader.Value(TestData.MinimalSample(t), key));
            var b = HistoryReader.Aggregate(new[] { s, TestData.FullSample(t.AddSeconds(5)) }, key, 60);
            Assert.Single(b);
            Assert.Equal(2, b[0].N);
            Assert.Equal(expected, b[0].Avg, 6);
        }

        [Fact]
        public void Historique_fps_relu_depuis_le_fichier()
        {
            TestEnvironment.Reset();
            var t = DateTime.Today.AddHours(10);
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(Path.Combine(Paths.DataDir, "perf-" + t.ToString("yyyy-MM-dd") + ".jsonl"), TestData.FullSample(t).ToJson() + "\n");
            var samples = HistoryReader.Load(t.AddMinutes(-1), t.AddMinutes(1));
            Assert.Single(samples);
            Assert.NotEmpty(HistoryReader.Aggregate(samples, "fps", 5));
            Assert.NotEmpty(HistoryReader.Aggregate(samples, "v12", 5));
        }

        [Fact]
        public void Banc_dans_le_dossier_installe_isole_reglages_et_donnees()
        {
            var dir = Path.Combine(TestEnvironment.BaseDir, "install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "report.ps1"), "");
                Assert.Equal(Path.Combine(dir, "bench"), Paths.ResolveStateDir(dir, bench: true, forcedBaseDir: false));
                // Instance installée normale : inchangée.
                Assert.Equal(dir, Paths.ResolveStateDir(dir, bench: false, forcedBaseDir: false));
                // Surcharge PERFMON_BASEDIR (tests, choix explicite) : inchangée même en banc.
                Assert.Equal(dir, Paths.ResolveStateDir(dir, bench: true, forcedBaseDir: true));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Banc_dans_un_dossier_isole_garde_son_dossier()
        {
            var dir = Path.Combine(TestEnvironment.BaseDir, "banc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try { Assert.Equal(dir, Paths.ResolveStateDir(dir, bench: true, forcedBaseDir: false)); }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Banc_installe_cree_son_dossier_et_desactive_le_rapport()
        {
            var dir = Path.Combine(TestEnvironment.BaseDir, "install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "report.ps1"), "");
                var state = Paths.ResolveStateDir(dir, bench: true, forcedBaseDir: false);
                // (a) dossier d'état créé dès sa résolution : Settings.Save ne doit pas échouer au premier lancement.
                Assert.True(Directory.Exists(state));
                // (b) report.ps1 lit BaseDir\data : il ne doit pas servir pour le banc isolé dans BaseDir\bench.
                Assert.False(Paths.ReportTargetsState(dir, state));
                Assert.True(Paths.ReportTargetsState(dir, Paths.ResolveStateDir(dir, bench: false, forcedBaseDir: false)));
                Assert.True(Paths.ReportTargetsState(dir, dir + Path.DirectorySeparatorChar));
                // Instance de test : rapport disponible (StateDir = BaseDir).
                Assert.True(Paths.ReportAvailable);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Instance_de_test_donnees_sous_BaseDir()
        {
            Assert.Equal(Path.GetFullPath(Paths.BaseDir), Path.GetFullPath(Paths.StateDir));
            Assert.Equal(Path.Combine(Paths.BaseDir, "data"), Paths.DataDir);
            Assert.Equal(Path.Combine(Paths.BaseDir, "settings.json"), Paths.SettingsFile);
        }
    }
}
