using System;
using System.IO;
using System.Linq;
using PerfMonitorLive.Metrics;
using Xunit;

namespace PerfMonitorLive.Tests
{
    public class SettingsTests
    {
        static readonly string[] DefaultMetrics = { "cpu", "memPct", "temp", "gpuTemp", "reboot", "cpuW", "gpuW", "rx", "tx", "pageIn" };

        [Fact]
        public void Load_sans_fichier_cree_les_valeurs_par_defaut_et_les_enregistre()
        {
            TestEnvironment.Reset();
            var s = Settings.Load();
            Assert.True(File.Exists(Paths.SettingsFile));
            Assert.Equal(DefaultMetrics, s.Rules.Select(r => r.Metric).ToArray());
            Assert.Equal("Travail", s.ActiveProfile);
            Assert.Equal(new[] { "Jeu", "Nuit", "Travail" }, s.Profiles.Keys.OrderBy(k => k).ToArray());
        }

        [Fact]
        public void Load_d_un_json_partiel_complete_regles_et_profils_sans_ecraser_l_existant()
        {
            TestEnvironment.Reset();
            File.WriteAllText(Paths.SettingsFile,
                "{\"Rules\":[{\"Metric\":\"cpu\",\"Threshold\":50,\"SustainSec\":5,\"Enabled\":true}],\"ActiveProfile\":\"Nuit\",\"CooldownSec\":123}");
            var s = Settings.Load();

            // la règle personnalisée est conservée, les autres sont ajoutées
            var cpu = s.Get("cpu");
            Assert.Equal(50, cpu.Threshold); Assert.Equal(5, cpu.SustainSec);
            Assert.Equal(123, s.CooldownSec);
            Assert.Equal(DefaultMetrics.OrderBy(x => x), s.Rules.Select(r => r.Metric).OrderBy(x => x));
            Assert.Equal(85, s.Get("memPct").Threshold);
            Assert.False(s.Get("cpuW").Enabled);

            // les trois profils existent, le profil actif est celui demandé et partage la liste de règles courante
            Assert.Equal("Nuit", s.ActiveProfile);
            Assert.Equal(3, s.Profiles.Count);
            Assert.Same(s.Rules, s.Profiles["Nuit"].Rules);

            // Jeu : seuils relevés, notifications critiques seulement, réseau coupé
            var jeu = s.Profiles["Jeu"];
            Assert.True(jeu.OnlyCritical);
            Assert.Equal(95, jeu.Rules.Single(r => r.Metric == "cpu").Threshold);
            Assert.Equal(120, jeu.Rules.Single(r => r.Metric == "cpu").SustainSec);
            Assert.Equal(92, jeu.Rules.Single(r => r.Metric == "memPct").Threshold);
            Assert.False(jeu.Rules.Single(r => r.Metric == "rx").Enabled);

            // Travail : copie des règles au moment du chargement (indépendante de la liste active)
            Assert.NotSame(s.Rules, s.Profiles["Travail"].Rules);
            Assert.Equal(50, s.Profiles["Travail"].Rules.Single(r => r.Metric == "cpu").Threshold);
        }

        [Fact]
        public void Load_sans_MachineTuned_dans_le_fichier_le_considere_comme_deja_adapte()
        {
            TestEnvironment.Reset();
            File.WriteAllText(Paths.SettingsFile, "{}");
            Assert.True(Settings.Load().MachineTuned);
        }

        [Fact]
        public void Load_d_un_json_corrompu_retombe_sur_les_valeurs_par_defaut()
        {
            TestEnvironment.Reset();
            File.WriteAllText(Paths.SettingsFile, "{ pas du json ");
            var s = Settings.Load();
            Assert.Equal(DefaultMetrics.Length, s.Rules.Count);
            Assert.Equal("Travail", s.ActiveProfile);
            Assert.Contains("Settings load", File.ReadAllText(Paths.LogFile));
        }

        [Fact]
        public void Aller_retour_Save_Load()
        {
            TestEnvironment.Reset();
            var s = Settings.Load();
            s.Get("cpu").Threshold = 77; s.Get("cpu").SustainSec = 12;
            s.Get("cpuW").Enabled = true;
            s.EnsureDisk("0 C:", DiskKind.Nvme);
            s.CooldownSec = 42; s.Theme = "Dark"; s.WidgetOpacity = 0.55;
            s.Games.Add("game.exe");
            s.PausedUntil = new DateTime(2030, 1, 2, 3, 4, 5);
            s.SwitchProfile("Jeu");   // enregistre aussi
            s.Get("cpu").Threshold = 99;
            s.Save();

            var r = Settings.Load();
            Assert.Equal("Jeu", r.ActiveProfile);
            Assert.Equal(42, r.CooldownSec);
            Assert.Equal("Dark", r.Theme);
            Assert.Equal(0.55, r.WidgetOpacity);
            Assert.Equal(new[] { "game.exe" }, r.Games.ToArray());
            Assert.Equal(new DateTime(2030, 1, 2, 3, 4, 5), r.PausedUntil);
            Assert.True(double.IsNaN(r.WidgetX));   // NaN (valeur par défaut) survit à l'aller-retour
            Assert.Equal(99, r.Get("cpu").Threshold);
            Assert.Equal(20, r.Profiles["Travail"].Rules.Single(x => x.Metric == "disk.lat" && x.Disk == "0 C:").Threshold);   // NVMe
            Assert.Equal(77, r.Profiles["Travail"].Rules.Single(x => x.Metric == "cpu").Threshold);
            Assert.True(r.Profiles["Travail"].Rules.Single(x => x.Metric == "cpuW").Enabled);
            Assert.Same(r.Rules, r.Profiles["Jeu"].Rules);
        }

        [Fact]
        public void Save_ecrit_dans_le_dossier_temporaire_uniquement()
        {
            TestEnvironment.Reset();
            new Settings().Save();
            Assert.StartsWith(TestEnvironment.BaseDir, Paths.SettingsFile, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(TestEnvironment.BaseDir, "settings.json")));
        }

        [Theory]
        [InlineData(DiskKind.Nvme, 20)]
        [InlineData(DiskKind.Ssd, 30)]
        [InlineData(DiskKind.Hdd, 100)]
        [InlineData(DiskKind.Usb, 100)]
        [InlineData(DiskKind.Unknown, 50)]
        public void LatencyDefault_selon_le_type_de_disque(DiskKind kind, double expected)
        {
            Assert.Equal(expected, Settings.LatencyDefault(kind));
        }

        [Theory]
        [InlineData(DiskKind.Nvme, 20, 200)]
        [InlineData(DiskKind.Ssd, 30, 200)]
        [InlineData(DiskKind.Hdd, 100, 100)]
        [InlineData(DiskKind.Usb, 100, 100)]
        [InlineData(DiskKind.Unknown, 50, 200)]
        public void EnsureDisk_applique_les_seuils_du_type_de_disque(DiskKind kind, double lat, double rw)
        {
            var s = new Settings();
            Assert.True(s.EnsureDisk("1 D:", kind));
            Assert.Equal(lat, s.Get("disk.lat", "1 D:").Threshold);
            Assert.Equal(rw, s.Get("disk.rw", "1 D:").Threshold);
            Assert.Equal(90, s.Get("disk.pct", "1 D:").Threshold);
            Assert.False(s.EnsureDisk("1 D:", DiskKind.Hdd));   // déjà présent : rien n'est modifié
            Assert.Equal(lat, s.Get("disk.lat", "1 D:").Threshold);
        }

        [Fact]
        public void StorTempDefault_hdd_50_sinon_65()
        {
            Assert.Equal(50, Settings.StorTempDefault(DiskKind.Hdd));
            Assert.Equal(65, Settings.StorTempDefault(DiskKind.Nvme));
            Assert.Equal(65, Settings.StorTempDefault(DiskKind.Unknown));
        }

        [Theory]
        [InlineData(true, 12, "Jeu")]
        [InlineData(false, 12, "Travail")]
        [InlineData(false, 23, "Nuit")]
        [InlineData(false, 3, "Nuit")]
        [InlineData(false, 7, "Travail")]
        public void AutoProfile(bool game, int hour, string expected)
        {
            Assert.Equal(expected, new Settings().AutoProfile(game, new DateTime(2026, 3, 5, hour, 0, 0)));
        }
    }
}
