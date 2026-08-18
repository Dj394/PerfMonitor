using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfMonitorLive.Metrics
{
    public class DiskSample
    {
        public string n { get; set; }
        public int pct { get; set; }
        public double r { get; set; }
        public double w { get; set; }
        public double q { get; set; }
        public double lat { get; set; }
    }
    public class ProcSample
    {
        public string n { get; set; }
        public double cpu { get; set; }
        public double mem { get; set; }
        public double h { get; set; }         // handles
        [JsonIgnore] public int Pid { get; set; }
    }
    /// <summary>Santé / température d'un disque physique (compteurs de fiabilité Windows).</summary>
    public class StorSample
    {
        public string n { get; set; }
        public double t { get; set; }         // température °C (0 = inconnue)
        public double tmax { get; set; }
        public double wear { get; set; }      // % d'usure
        public double hours { get; set; }     // heures de fonctionnement
        public double rerr { get; set; }      // erreurs lecture (total)
        public double rerrU { get; set; }     // erreurs lecture non corrigées
        public double werr { get; set; }
        public double werrU { get; set; }
        public double starts { get; set; }
        public int health { get; set; }       // 0 = sain, 1 = avertissement, 2 = défaillant
        [JsonIgnore] public double Errors => rerr + werr;
        [JsonIgnore] public string HealthText => health == 0 ? "OK" : health == 1 ? "Avertissement" : "Défaillant";
    }
    public class FanSample { public string n { get; set; } public int rpm { get; set; } }

    /// <summary>Fuite détectée sur un processus (calculée toutes les 60 s, non écrite dans l'historique).</summary>
    public class LeakInfo
    {
        public string Name; public double MemNowMB, MemDelta1hMB, MemDelta3hMB, HandlesNow, HandlesDelta1h; public double SpanMin;
    }

    public class Sample
    {
        public string ts { get; set; }
        public double cpu { get; set; }
        public double memMB { get; set; }
        public double memPct { get; set; }
        public double pageIn { get; set; }
        public List<DiskSample> disks { get; set; } = new List<DiskSample>();
        public double rx { get; set; }
        public double tx { get; set; }
        public List<ProcSample> procs { get; set; } = new List<ProcSample>();
        public double? temp { get; set; }
        public double? gpu { get; set; }
        public double? cpuMHz { get; set; }
        public double? gpuMHz { get; set; }
        public double? cpuW { get; set; }
        public double? gpuW { get; set; }
        public List<FanSample> fans { get; set; } = new List<FanSample>();
        public List<StorSample> stor { get; set; } = new List<StorSample>();
        public RebootInfo rb { get; set; }
        public double? bat { get; set; }      // % batterie (portables)
        public bool? ac { get; set; }         // sur secteur
        [JsonIgnore] public DateTime Time { get; set; }
        [JsonIgnore] public double TotalMB { get; set; }
        [JsonIgnore] public List<LeakInfo> Leaks { get; set; }
        [JsonIgnore] public bool GameActive { get; set; }

        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        public string ToJson() => JsonSerializer.Serialize(this, Opts);
        public static Sample FromJson(string line)
        {
            var s = JsonSerializer.Deserialize<Sample>(line, Opts);
            if (s != null && DateTime.TryParse(s.ts, out var t)) s.Time = t;
            return s;
        }

        public DiskSample Disk(string name) { foreach (var d in disks) if (d.n == name) return d; return null; }
        public StorSample Stor(string name) { foreach (var d in stor) if (d.n == name) return d; return null; }
        public FanSample Fan(string name) { foreach (var f in fans) if (f.n == name) return f; return null; }
    }
}
