using System;
using System.IO;
using System.Text;

namespace PerfMonitorLive.Metrics
{
    /// <summary>Écrit un échantillon toutes les 5 s dans data\perf-yyyy-MM-dd.jsonl (format identique à collect.ps1).</summary>
    public class HistoryWriter
    {
        DateTime _last = DateTime.MinValue;
        public int IntervalSec { get; set; } = 5;
        public void OnSample(Sample s)
        {
            if ((s.Time - _last).TotalSeconds < IntervalSec) return;
            _last = s.Time;
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                var f = Path.Combine(Paths.DataDir, "perf-" + s.Time.ToString("yyyy-MM-dd") + ".jsonl");
                File.AppendAllText(f, s.ToJson() + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex) { Paths.Log("History: " + ex.Message); }
        }
    }
}
