using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using PerfMonitorLive.Metrics;
using Xunit;

namespace PerfMonitorLive.Tests
{
    public class HistoryTests
    {
        static string JsonlFor(DateTime day) => Path.Combine(Paths.DataDir, "perf-" + day.ToString("yyyy-MM-dd") + ".jsonl");

        [Fact]
        public void Json_source_generated_identique_a_Sample_ToJson()
        {
            var t = new DateTime(2026, 3, 5, 14, 7, 9);
            foreach (var s in new[] { TestData.FullSample(t), TestData.MinimalSample(t), new Sample() })
            {
                var generated = JsonSerializer.Serialize(s, HistoryJsonContext.Default.Sample);
                Assert.Equal(s.ToJson(), generated);
            }
        }

        [Fact]
        public void Json_omet_les_champs_nuls_et_garde_les_noms_de_proprietes()
        {
            var json = JsonSerializer.Serialize(TestData.MinimalSample(new DateTime(2026, 3, 5, 14, 7, 9)), HistoryJsonContext.Default.Sample);
            Assert.StartsWith("{\"ts\":\"2026-03-05T14:07:09\",\"cpu\":1,", json);   // HistoryReader lit le ts à l'offset 7
            Assert.DoesNotContain("\"temp\"", json);
            Assert.DoesNotContain("\"fps\"", json);
            Assert.DoesNotContain("Time", json);
            Assert.DoesNotContain("Pid", json);
        }

        [Fact]
        public void Aller_retour_HistoryWriter_vers_HistoryReader()
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 3, 5, 14, 7, 9);
            var w = new HistoryWriter();
            var a = TestData.FullSample(t);
            var b = TestData.FullSample(t.AddSeconds(5)); b.cpu = 80; b.temp = null;
            w.OnSample(a);
            w.OnSample(TestData.FullSample(t.AddSeconds(2)));   // < 5 s : ignoré
            w.OnSample(b);

            var lines = File.ReadAllLines(JsonlFor(t));
            Assert.Equal(2, lines.Length);
            Assert.Equal(a.ToJson(), lines[0]);
            Assert.Equal(b.ToJson(), lines[1]);

            var back = HistoryReader.Load(t.AddMinutes(-1), t.AddMinutes(1));
            Assert.Equal(2, back.Count);
            Assert.Equal(t, back[0].Time);
            Assert.Equal(t.AddSeconds(5), back[1].Time);
            Assert.Equal(a.cpu, back[0].cpu); Assert.Equal(80, back[1].cpu);
            Assert.Equal(61.5, back[0].temp); Assert.Null(back[1].temp);
            Assert.Equal(a.v12, back[0].v12);
            Assert.Equal("chrome", back[0].procs.Single().n);
            Assert.Equal(1500, back[0].procs.Single().mem);
            Assert.Equal("0 C:", back[0].Disk("0 C:").n);
            Assert.Equal(900, back[0].Fan("CPU").rpm);
            Assert.Equal(12.05, back[0].Volt("+12V").v);
            Assert.True(back[0].rb.pfr);
            Assert.Equal(a.ToJson(), back[0].ToJson());   // relecture sans perte
        }

        [Fact]
        public void Un_fichier_par_jour()
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 3, 5, 23, 59, 58);
            var w = new HistoryWriter();
            w.OnSample(TestData.MinimalSample(t));
            w.OnSample(TestData.MinimalSample(t.AddSeconds(5)));   // 6 mars
            Assert.True(File.Exists(JsonlFor(t)));
            Assert.True(File.Exists(JsonlFor(t.AddSeconds(5))));
            Assert.Equal(2, HistoryReader.Load(t.AddMinutes(-1), t.AddMinutes(1)).Count);
        }

        [Fact]
        public void Reader_filtre_par_dates_et_ignore_les_lignes_invalides()
        {
            TestEnvironment.Reset();
            var t = new DateTime(2026, 3, 5, 12, 0, 0);
            var w = new HistoryWriter { IntervalSec = 1 };
            for (int i = 0; i < 10; i++) w.OnSample(TestData.MinimalSample(t.AddSeconds(i)));
            File.AppendAllText(JsonlFor(t), "{ceci n'est pas du json valide et assez long pour passer le filtre}\n");
            var part = HistoryReader.Load(t.AddSeconds(3), t.AddSeconds(6));
            Assert.Equal(4, part.Count);
            Assert.Equal(10, HistoryReader.Load(t.AddMinutes(-1), t.AddMinutes(1)).Count);
        }

        [Fact]
        public void Aggregate_et_Stats_sur_donnees_en_memoire()
        {
            var t = new DateTime(2026, 3, 5, 12, 0, 0);
            var list = Enumerable.Range(0, 10).Select(i => { var s = TestData.MinimalSample(t.AddSeconds(i)); s.cpu = i * 10; return s; }).ToList();
            var buckets = HistoryReader.Aggregate(list, "cpu", 5);
            Assert.Equal(2, buckets.Count);
            Assert.Equal(20, buckets[0].Avg);
            Assert.Equal(70, buckets[1].Avg);
            Assert.Equal(90, HistoryReader.Stats(list, "cpu").max);
        }
    }
}
