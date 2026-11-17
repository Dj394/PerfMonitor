using System;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;

namespace PerfMonitorLive.Metrics
{
    /// <summary>Sérialisation de <see cref="Sample"/> générée à la compilation (sans réflexion). Mêmes options que
    /// <see cref="Sample.ToJson"/> — champs nuls omis, noms de propriétés tels quels — donc exactement le même JSON,
    /// relu par <see cref="HistoryReader"/> et report.ps1.</summary>
    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(Sample))]
    internal partial class HistoryJsonContext : JsonSerializerContext { }

    /// <summary>Écrit un échantillon toutes les 5 s dans data\perf-yyyy-MM-dd.jsonl (format identique à collect.ps1).</summary>
    public class HistoryWriter
    {
        DateTime _last = DateTime.MinValue;
        DateTime _day = DateTime.MinValue; string _file;   // dossier créé et chemin calculé une fois par jour
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        public int IntervalSec { get; set; } = 5;
        public void OnSample(Sample s)
        {
            if ((s.Time - _last).TotalSeconds < IntervalSec) return;
            _last = s.Time;
            try
            {
                if (_file == null || s.Time.Date != _day)
                {
                    _file = null;   // si CreateDirectory échoue, on réessaie au prochain échantillon
                    Directory.CreateDirectory(Paths.DataDir);
                    _file = Path.Combine(Paths.DataDir, "perf-" + s.Time.ToString("yyyy-MM-dd") + ".jsonl");
                    _day = s.Time.Date;
                }
                string line = System.Text.Json.JsonSerializer.Serialize(s, HistoryJsonContext.Default.Sample) + "\n";
                try { File.AppendAllText(_file, line, Utf8); }
                catch (DirectoryNotFoundException) { Directory.CreateDirectory(Paths.DataDir); File.AppendAllText(_file, line, Utf8); }   // dossier supprimé en cours de route
            }
            catch (Exception ex) { Paths.Log("History: " + ex.Message); }
        }
    }
}
