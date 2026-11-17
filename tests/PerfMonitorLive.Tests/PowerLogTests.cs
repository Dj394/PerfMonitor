using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using PerfMonitorLive.Metrics;
using Xunit;

namespace PerfMonitorLive.Tests
{
    public class PowerLogTests
    {
        const string Header = "heure;fps;v12;v5;v33;cpuW;gpuW;cpuC;gpuC;gpuMHz;fanMax;cpuPct;jeu;fps1;fpsMs;sacc";

        static string CsvFor(DateTime day) => Path.Combine(Paths.DataDir, "power-" + day.ToString("yyyy-MM-dd") + ".csv");

        /// <summary>L'écriture se fait sur un thread dédié : on attend que le fichier contienne le nombre de lignes voulu.</summary>
        static string[] WaitLines(string path, int count, int timeoutMs = 10000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            string[] lines = new string[0];
            while (DateTime.UtcNow < end)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var sr = new StreamReader(fs);
                        lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length >= count) return lines;
                    }
                }
                catch (IOException) { }
                Thread.Sleep(20);
            }
            Assert.Fail("Fichier " + Path.GetFileName(path) + " : " + lines.Length + " ligne(s) au lieu de " + count);
            return lines;
        }

        static void WithCulture(string name, Action a)
        {
            var old = CultureInfo.CurrentCulture;
            try { CultureInfo.CurrentCulture = new CultureInfo(name); a(); }
            finally { CultureInfo.CurrentCulture = old; }
        }

        /// <summary>Format « CSV à la française », fixe quelle que soit la culture du thread : « ; » et virgule décimale
        /// (lu en nombres par Excel en français ; avant, 12.035 y devenait du texte et 3.3 une date).</summary>
        [Theory]
        [InlineData("fr-FR")]
        [InlineData("en-US")]
        [InlineData("")]
        public void Ligne_complete_format_csv_virgule_decimale_quelle_que_soit_la_culture(string culture)
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 3, 5, 14, 7, 9);
            var s = TestData.FullSample(t);
            s.fps = 59.96; s.fps1 = 41.2; s.fpsMs = 16.678; s.sacc = 2; s.v12 = 12.0349; s.v5 = 5.0; s.v33 = null; s.cpuW = 45.26; s.gpuW = null;
            s.temp = 71.449; s.gpu = 66.55; s.gpuMHz = 1950.4; s.cpu = 12.5;
            s.fans = new List<FanSample> { new FanSample { n = "a", rpm = 900 }, new FanSample { n = "b", rpm = 1250 }, new FanSample { n = "c", rpm = 400 } };
            WithCulture(culture, () => new PowerLog { GameActive = () => true }.OnSample(s));
            var lines = WaitLines(CsvFor(t), 2);
            Assert.Equal(Header, lines[0].TrimEnd('\r'));
            var f = lines[1].Split(';');
            Assert.Equal(16, f.Length);
            Assert.Equal("14:07:09", f[0]);
            Assert.Equal("60", f[1]);          // fps arrondi à l'unité
            Assert.Equal("12,035", f[2]);      // 3 décimales, virgule décimale
            Assert.Equal("5", f[3]);           // pas de « 5,000 »
            Assert.Equal("", f[4]);            // valeur absente = cellule vide
            Assert.Equal("45,3", f[5]);
            Assert.Equal("", f[6]);
            Assert.Equal("71,4", f[7]);
            Assert.Equal(Math.Round(66.55, 1).ToString(CultureInfo.InvariantCulture).Replace('.', ','), f[8]);
            Assert.Equal("1950", f[9]);
            Assert.Equal("1250", f[10]);       // ventilateur le plus rapide
            Assert.Equal("12", f[11]);         // Math.Round(12.5) = 12 (arrondi bancaire)
            Assert.Equal("1", f[12]);          // jeu actif
            Assert.Equal("41", f[13]);         // 1 % low arrondi à l'unité
            Assert.Equal("16,68", f[14]);      // temps d'image moyen, 2 décimales
            Assert.Equal("2", f[15]);          // saccades sur la fenêtre de 2 s
            Assert.DoesNotContain('.', lines[1]);   // aucun point décimal (ni séparateur de milliers)
        }

        [Fact]
        public void Valeurs_absentes_donnent_des_cellules_vides_et_pas_des_zeros()
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 3, 5, 14, 7, 10);
            new PowerLog().OnSample(new Sample { Time = t });   // tout à null, aucun ventilateur
            var lines = WaitLines(CsvFor(t), 2);
            Assert.Equal("14:07:10" + new string(';', 10) + "0;0;0;;;", lines[1].TrimEnd('\r'));
        }

        [Fact]
        public void Zero_negatif_ecrit_tel_quel_et_vrai_zero_distinct_de_l_absence()
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 3, 5, 14, 7, 11);
            var s = new Sample { Time = t, v12 = 0.0, v5 = -0.0004, cpu = -0.2 };   // arrondis : 0, -0, -0
            new PowerLog().OnSample(s);
            var f = WaitLines(CsvFor(t), 2)[1].TrimEnd('\r').Split(';');
            Assert.Equal("0", f[2]);      // un rail à 0 V est écrit : on le remarque
            Assert.Equal("-0", f[3]);     // comportement actuel de double.ToString : -0 conservé (documenté ici)
            Assert.Equal("-0", f[11]);
            Assert.Equal("", f[4]);       // alors qu'une valeur absente reste vide
        }

        [Fact]
        public void En_tete_ecrit_une_seule_fois_dans_un_nouveau_fichier()
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 3, 5, 10, 0, 0);
            var pl = new PowerLog();
            pl.OnSample(TestData.FullSample(t));
            pl.OnSample(TestData.FullSample(t.AddSeconds(1)));
            pl.OnSample(TestData.FullSample(t.AddSeconds(2)));
            var lines = WaitLines(CsvFor(t), 4);
            Assert.Equal(4, lines.Length);
            Assert.Equal(Header, lines[0].TrimEnd('\r'));
            Assert.Equal(1, lines.Count(l => l.StartsWith("heure;")));
            Assert.Equal(new[] { "10:00:00", "10:00:01", "10:00:02" }, lines.Skip(1).Select(l => l.Split(';')[0]).ToArray());
        }

        [Fact]
        public void Cadence_une_ligne_par_intervalle()
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 3, 5, 10, 0, 0);
            var pl = new PowerLog { IntervalSec = 2 };
            pl.OnSample(TestData.FullSample(t));
            pl.OnSample(TestData.FullSample(t.AddMilliseconds(500)));    // ignoré
            pl.OnSample(TestData.FullSample(t.AddMilliseconds(1900)));   // ignoré
            pl.OnSample(TestData.FullSample(t.AddSeconds(2)));
            WaitLines(CsvFor(t), 3);
            Thread.Sleep(200);
            Assert.Equal(3, WaitLines(CsvFor(t), 3).Length);
        }

        [Fact]
        public void Changement_de_jour_ouvre_un_nouveau_fichier_avec_son_en_tete()
        {
            TestEnvironment.Reset();
            var t1 = new DateTime(2026, 3, 5, 23, 59, 59);
            var t2 = t1.AddSeconds(1);   // 6 mars 00:00:00
            var pl = new PowerLog();
            pl.OnSample(TestData.FullSample(t1));
            pl.OnSample(TestData.FullSample(t2));
            var a = WaitLines(CsvFor(t1), 2);
            var b = WaitLines(CsvFor(t2), 2);
            Assert.NotEqual(CsvFor(t1), CsvFor(t2));
            Assert.Equal(Header, a[0].TrimEnd('\r')); Assert.StartsWith("23:59:59;", a[1]);
            Assert.Equal(Header, b[0].TrimEnd('\r')); Assert.StartsWith("00:00:00;", b[1]);
            Assert.Equal(2, a.Length);
            Assert.Equal(2, b.Length);
        }

        [Fact]
        public void Fichier_existant_avec_d_autres_colonnes_est_mis_de_cote()
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 3, 7, 9, 30, 0);
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(CsvFor(t), "ts;fps;v12\n09:00:00;60;12.1\n");
            new PowerLog().OnSample(TestData.FullSample(t));
            for (int i = 0; i < 500 && Directory.GetFiles(Paths.DataDir, "power-2026-03-07.*.csv").Length == 0; i++) Thread.Sleep(20);   // l'ancien fichier a déjà 2 lignes : attendre sa mise de côté
            var lines = WaitLines(CsvFor(t), 2);
            Assert.Equal(Header, lines[0].TrimEnd('\r'));
            var backups = Directory.GetFiles(Paths.DataDir, "power-2026-03-07.*.csv");
            Assert.Single(backups);
            Assert.Contains("09:00:00;60;12.1", File.ReadAllText(backups[0]));
        }

        [Fact]
        public void Fichier_existant_au_bon_format_est_complete_sans_second_en_tete()
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 3, 8, 9, 30, 0);
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(CsvFor(t), Header + "\n09:00:00;60;;;;;;;;;0;0;0\n");
            new PowerLog().OnSample(TestData.FullSample(t));
            var lines = WaitLines(CsvFor(t), 3);
            Assert.Equal(3, lines.Length);
            Assert.Equal(1, lines.Count(l => l.StartsWith("heure;")));
        }

        [Fact]
        public void Desactive_n_ecrit_rien()
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 3, 9, 9, 30, 0);
            new PowerLog { Enabled = false }.OnSample(TestData.FullSample(t));
            Thread.Sleep(300);
            Assert.False(File.Exists(CsvFor(t)));
        }
    }
}
