using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PerfMonitorLive.Metrics
{
    public class BenchResult
    {
        public string ts { get; set; }
        public string disk { get; set; }
        public double cpuMBs { get; set; }       // SHA-256 multi-thread, Mo/s
        public double cpu1MBs { get; set; }      // mono-thread
        public double ramGBs { get; set; }       // copie mémoire, Go/s
        public double seqWriteMBs { get; set; }
        public double seqReadMBs { get; set; }
        public double rnd4kIops { get; set; }
        public string note { get; set; }
        public DateTime Time => DateTime.TryParse(ts, out var t) ? t : DateTime.MinValue;
    }

    /// <summary>Benchmark rapide (~40 s) : CPU, RAM, disque ; résultats dans data\bench.jsonl.</summary>
    public static class Bench
    {
        static string File_ => Path.Combine(Paths.DataDir, "bench.jsonl");
        public static List<BenchResult> Load()
        {
            var l = new List<BenchResult>();
            try { if (File.Exists(File_)) foreach (var line in File.ReadAllLines(File_)) if (line.Length > 10) l.Add(JsonSerializer.Deserialize<BenchResult>(line)); } catch { }
            return l.OrderBy(b => b.Time).ToList();
        }

        public static async Task<BenchResult> RunAsync(string driveLetter, IProgress<string> progress, CancellationToken ct)
        {
            var r = new BenchResult { ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"), disk = driveLetter };
            progress?.Report("CPU multi-thread…");
            r.cpuMBs = await Task.Run(() => CpuTest(Environment.ProcessorCount, 6, ct), ct);
            progress?.Report("CPU mono-thread…");
            r.cpu1MBs = await Task.Run(() => CpuTest(1, 4, ct), ct);
            progress?.Report("Mémoire…");
            r.ramGBs = await Task.Run(() => RamTest(4, ct), ct);
            progress?.Report("Disque " + driveLetter + " : écriture séquentielle…");
            var (w, rd, iops) = await Task.Run(() => DiskTest(driveLetter, progress, ct), ct);
            r.seqWriteMBs = w; r.seqReadMBs = rd; r.rnd4kIops = iops;
            try { Directory.CreateDirectory(Paths.DataDir); File.AppendAllText(File_, JsonSerializer.Serialize(r) + "\n", new UTF8Encoding(false)); } catch (Exception ex) { Paths.Log("bench save: " + ex.Message); }
            return r;
        }

        static double CpuTest(int threads, double seconds, CancellationToken ct)
        {
            long total = 0; var sw = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, threads).Select(i => Task.Run(() =>
            {
                var buf = new byte[4 * 1024 * 1024]; new Random(i).NextBytes(buf); long n = 0;
                using (var sha = SHA256.Create())
                    while (sw.Elapsed.TotalSeconds < seconds && !ct.IsCancellationRequested) { sha.ComputeHash(buf); n += buf.Length; }
                Interlocked.Add(ref total, n);
            })).ToArray();
            Task.WaitAll(tasks);
            return Math.Round(total / 1048576.0 / sw.Elapsed.TotalSeconds);
        }
        static double RamTest(double seconds, CancellationToken ct)
        {
            const int size = 256 * 1024 * 1024;
            var a = new byte[size]; var b = new byte[size]; new Random(1).NextBytes(a);
            var sw = Stopwatch.StartNew(); long bytes = 0;
            while (sw.Elapsed.TotalSeconds < seconds && !ct.IsCancellationRequested) { Buffer.BlockCopy(a, 0, b, 0, size); Buffer.BlockCopy(b, 0, a, 0, size); bytes += 2L * size; }
            return Math.Round(bytes / 1073741824.0 / sw.Elapsed.TotalSeconds, 1);
        }
        static (double, double, double) DiskTest(string drive, IProgress<string> progress, CancellationToken ct)
        {
            string dir = drive.EndsWith(":") ? drive + "\\" : drive;
            string tmp = Path.Combine(dir, "PerfMonitor-bench.tmp");
            const int block = 4 * 1024 * 1024; long size = 1024L * 1024 * 1024; // 1 Go
            var buf = new byte[block]; new Random(7).NextBytes(buf);
            double w = 0, rd = 0, iops = 0;
            try
            {
                var sw = Stopwatch.StartNew();
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, block, FileOptions.WriteThrough | (FileOptions)0x20000000))
                    for (long off = 0; off < size && !ct.IsCancellationRequested; off += block) fs.Write(buf, 0, block);
                w = Math.Round(size / 1048576.0 / sw.Elapsed.TotalSeconds);
                progress?.Report("Disque " + drive + " : lecture séquentielle…");
                sw.Restart();
                using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None, block, (FileOptions)0x20000000))
                { int n; long got = 0; while ((n = fs.Read(buf, 0, block)) > 0 && !ct.IsCancellationRequested) got += n; rd = Math.Round(got / 1048576.0 / sw.Elapsed.TotalSeconds); }
                progress?.Report("Disque " + drive + " : accès aléatoires 4 Ko…");
                var small = new byte[4096]; var rnd = new Random(3); long ops = 0; sw.Restart();
                using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None, 4096, (FileOptions)0x20000000))
                    while (sw.Elapsed.TotalSeconds < 4 && !ct.IsCancellationRequested) { long pos = (long)(rnd.NextDouble() * (size / 4096 - 1)) * 4096; fs.Seek(pos, SeekOrigin.Begin); fs.Read(small, 0, 4096); ops++; }
                iops = Math.Round(ops / sw.Elapsed.TotalSeconds);
            }
            catch (Exception ex) { Paths.Log("bench disk: " + ex.Message); }
            finally { try { File.Delete(tmp); } catch { } }
            return (w, rd, iops);
        }
    }
}
