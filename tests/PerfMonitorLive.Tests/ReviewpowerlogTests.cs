using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PerfMonitorLive.Metrics;
using Xunit;

namespace PerfMonitorLive.Tests
{
    /// <summary>Correctifs de revue du journal alimentation (PowerLog) et de sa relecture (CrashReport).</summary>
    public class ReviewpowerlogTests
    {
        static string CsvFor(DateTime day) => Path.Combine(Paths.DataDir, "power-" + day.ToString("yyyy-MM-dd") + ".csv");

        static string[] ReadShared(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
            }
            catch (IOException) { return new string[0]; }
        }

        static string[] WaitLines(string path, int count, int timeoutMs = 10000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            string[] lines = new string[0];
            while (DateTime.UtcNow < end)
            {
                if (File.Exists(path)) { lines = ReadShared(path); if (lines.Length >= count) return lines; }
                Thread.Sleep(20);
            }
            Assert.Fail("Fichier " + Path.GetFileName(path) + " : " + lines.Length + " ligne(s) au lieu de " + count);
            return lines;
        }

        static int LogCount(string needle) => File.Exists(Paths.LogFile) ? ReadShared(Paths.LogFile).Count(l => l.Contains(needle)) : 0;

        // --- défaut 1 : l'horloge recule (heure d'hiver, NTP) -> le journal ne doit pas rester muet le temps du recul
        [Fact]
        public void Horloge_qui_recule_n_interrompt_pas_le_journal()
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 10, 25, 2, 59, 59);   // 03:00 CEST -> 02:00 CET
            var pl = new PowerLog();
            pl.OnSample(TestData.FullSample(t));
            pl.OnSample(TestData.FullSample(new DateTime(2026, 10, 25, 2, 0, 0)));
            pl.OnSample(TestData.FullSample(new DateTime(2026, 10, 25, 2, 0, 0, 500)));   // cadence conservée après le recul
            pl.OnSample(TestData.FullSample(new DateTime(2026, 10, 25, 2, 0, 1)));
            var lines = WaitLines(CsvFor(t), 4);
            Thread.Sleep(200);
            lines = ReadShared(CsvFor(t));
            Assert.Equal(new[] { "02:59:59", "02:00:00", "02:00:01" }, lines.Skip(1).Select(l => l.Split(';')[0]).ToArray());
        }

        // --- défauts 2 et 3 : CSV verrouillé par un tableur -> une seule ligne dans live.log, lignes gardées puis écrites
        [Fact]
        public void Fichier_verrouille_par_un_tableur_lignes_gardees_et_erreur_journalisee_une_fois()
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 4, 2, 10, 0, 0);
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(CsvFor(t), PowerLog.Header + "\n");
            var pl = new PowerLog { RetryMs = 100 };
            // Comme Excel : ouvert en lecture-écriture, écriture refusée aux autres.
            using (var excel = new FileStream(CsvFor(t), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                for (int i = 0; i < 5; i++) { pl.OnSample(TestData.FullSample(t.AddSeconds(i))); Thread.Sleep(150); }
                Thread.Sleep(400);   // plusieurs essais en échec
                Assert.Equal(1, LogCount("PowerLog: "));
            }
            var lines = WaitLines(CsvFor(t), 6);
            Assert.Equal(PowerLog.Header, lines[0]);
            Assert.Equal(new[] { "10:00:00", "10:00:01", "10:00:02", "10:00:03", "10:00:04" }, lines.Skip(1).Select(l => l.Split(';')[0]).ToArray());
            for (int i = 0; i < 200 && LogCount("écriture rétablie") == 0; i++) Thread.Sleep(20);
            Assert.Equal(1, LogCount("écriture rétablie"));
            Assert.Equal(1, LogCount("PowerLog: "));

            // nouvelle série d'échecs : journalisée de nouveau (drapeau réarmé au succès)
            using (var excel = new FileStream(CsvFor(t), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                pl.OnSample(TestData.FullSample(t.AddSeconds(10)));
                for (int i = 0; i < 200 && LogCount("PowerLog: ") < 2; i++) Thread.Sleep(20);
                Assert.Equal(2, LogCount("PowerLog: "));
            }
            WaitLines(CsvFor(t), 7);
        }

        // --- objection : power-J.csv verrouillé à minuit ne doit pas retarder les lignes de power-J+1.csv
        [Fact]
        public void Fichier_verrouille_ne_retarde_pas_les_lignes_d_un_autre_fichier()
        {
            TestEnvironment.Reset();
            var j = new DateTime(2026, 4, 6, 23, 59, 58);
            var j1 = new DateTime(2026, 4, 7, 0, 0, 0);
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(CsvFor(j), PowerLog.Header + "\n");
            var pl = new PowerLog { RetryMs = 5000 };   // essai lent : la ligne de J+1 ne doit pas l'attendre
            using (var excel = new FileStream(CsvFor(j), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                pl.OnSample(TestData.FullSample(j));
                pl.OnSample(TestData.FullSample(j.AddSeconds(1)));
                pl.OnSample(TestData.FullSample(j1));
                pl.OnSample(TestData.FullSample(j1.AddSeconds(1)));
                var lines = WaitLines(CsvFor(j1), 3, 2000);
                Assert.Equal(new[] { "00:00:00", "00:00:01" }, lines.Skip(1).Select(l => l.Split(';')[0]).ToArray());
                pl.OnSample(TestData.FullSample(j1.AddSeconds(2)));   // une écriture réussie ne réarme pas le journal d'erreur
                WaitLines(CsvFor(j1), 4, 2000);
                Assert.Equal(1, LogCount("PowerLog: "));
                Assert.Equal(0, LogCount("écriture rétablie"));
            }
            // verrou levé : les lignes de J, gardées de côté, sont écrites dans l'ordre au prochain essai
            var lj = WaitLines(CsvFor(j), 3, 15000);
            Assert.Equal(new[] { "23:59:58", "23:59:59" }, lj.Skip(1).Select(l => l.Split(';')[0]).ToArray());
            for (int i = 0; i < 200 && LogCount("écriture rétablie") == 0; i++) Thread.Sleep(20);
            Assert.Equal(1, LogCount("écriture rétablie"));
            Assert.Equal(1, LogCount("PowerLog: "));
        }

        // --- défaut 8 : virgule décimale à l'écriture, les deux notations à la relecture
        [Fact]
        public void CrashReport_relit_l_ancien_format_point_et_le_nouveau_format_virgule()
        {
            TestEnvironment.Reset();
            var day = new DateTime(2026, 4, 3);
            Directory.CreateDirectory(Paths.DataDir);
            var oldF = Path.Combine(Paths.DataDir, "power-2026-04-03.101500.csv");
            var newF = CsvFor(day);
            File.WriteAllText(oldF, "ts;fps;v12;v5;v33;cpuW;gpuW;cpuC;gpuC;gpuMHz;fanMax;cpuPct;jeu\n10:00:00;60;12.035;5.04;3.3;45.3;;71.4;66.5;1950;1250;12;1\n");
            File.WriteAllText(newF, PowerLog.Header + "\n11:00:00;60;12,035;5,04;3,3;45,3;;71,4;66,5;1950;1250;12;1\n");
            foreach (var (f, h) in new[] { (oldF, 10), (newF, 11) })
            {
                var rows = new List<PowerRow>();
                CrashReport.ReadFileTail(f, day, day, day.AddDays(1), 10, rows);
                var r = Assert.Single(rows);
                Assert.Equal(day.AddHours(h), r.Time);
                Assert.Equal(60, r.Fps); Assert.Equal(12.035, r.V12); Assert.Equal(5.04, r.V5); Assert.Equal(3.3, r.V33);
                Assert.Equal(45.3, r.CpuW); Assert.Null(r.GpuW); Assert.Equal(71.4, r.CpuC); Assert.Equal(66.5, r.GpuC);
                Assert.Equal(1950, r.GpuMHz); Assert.Equal(1250, r.FanMax); Assert.Equal(12, r.CpuPct); Assert.True(r.Game);
            }
        }

        [Fact]
        public void Aller_retour_PowerLog_vers_CrashReport()
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 4, 4, 12, 30, 15);
            var s = TestData.FullSample(t);
            new PowerLog { GameActive = () => true }.OnSample(s);
            WaitLines(CsvFor(t), 2);
            var rows = new List<PowerRow>();
            CrashReport.ReadFileTail(CsvFor(t), t.Date, t.Date, t.Date.AddDays(1), 10, rows);
            var r = Assert.Single(rows);
            Assert.Equal(t, r.Time);
            Assert.Equal(12.052, r.V12); Assert.Equal(5.01, r.V5); Assert.Equal(3.3, r.V33);
            Assert.Equal(Math.Round(65.25, 1), r.CpuW); Assert.Equal(120.5, r.GpuW); Assert.Equal(61.5, r.CpuC);
            Assert.Equal(144, r.Fps); Assert.True(r.Game);
        }

        [Fact]
        public void Fichier_du_jour_a_l_ancien_format_point_est_mis_de_cote()
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 4, 5, 9, 0, 0);
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(CsvFor(t), "ts;fps;v12;v5;v33;cpuW;gpuW;cpuC;gpuC;gpuMHz;fanMax;cpuPct;jeu\n08:59:59;60;12.035;;;;;;;;0;0;0\n");
            new PowerLog().OnSample(TestData.FullSample(t));
            for (int i = 0; i < 500 && Directory.GetFiles(Paths.DataDir, "power-2026-04-05.*.csv").Length == 0; i++) Thread.Sleep(20);
            var lines = WaitLines(CsvFor(t), 2);
            Assert.Equal(PowerLog.Header, lines[0]);
            Assert.DoesNotContain(lines, l => l.Contains("12.035"));
            Assert.Single(Directory.GetFiles(Paths.DataDir, "power-2026-04-05.*.csv"));
        }
    }
}
