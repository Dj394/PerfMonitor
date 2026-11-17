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
    /// <summary>Tension d'un rail lue par le Super I/O de la carte mere (Volts).
    /// Mesure indirecte : la carte mere ecoute le rail, l'alimentation elle-meme ne dit rien.
    /// Precision de l'ordre de +/-5 % et cadence 1 Hz : utile pour voir un affaissement lent
    /// sous charge, incapable de voir un pic transitoire de quelques microsecondes.</summary>
    public class VoltSample { public string n { get; set; } public double v { get; set; } }

    /// <summary>Fuite détectée sur un processus (calculée toutes les 60 s, non écrite dans l'historique).</summary>
    public class LeakInfo
    {
        public string Name; public double MemNowMB, MemDelta1hMB, MemDelta3hMB, HandlesNow, HandlesDelta1h; public double SpanMin;
    }

    public class Sample
    {
        /// <summary>Horodatage sérialisé. Formaté à la première lecture depuis <see cref="Time"/> (en pratique : à l'écriture
        /// de l'historique, une mesure sur cinq), plutôt qu'à chaque mesure sur le thread de mesure.</summary>
        public string ts { get => _ts ??= Time == default ? null : Time.ToString("yyyy-MM-ddTHH:mm:ss"); set => _ts = value; }
        string _ts;
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
        public double? fps { get; set; }      // images par seconde du jeu au premier plan (ETW/DXGI)
        // Charge GPU et VRAM (LibreHardwareMonitor) : absentes des fichiers antérieurs, relues alors à null
        public double? gpuLoad { get; set; }  // % de charge du cœur GPU
        public double? vram { get; set; }     // mémoire vidéo utilisée, Go
        public double? vramPct { get; set; }  // % de la mémoire vidéo totale (null si le total est inconnu)
        public double? cpuCore { get; set; }  // % de charge du cœur CPU le plus chargé (indice limité CPU / GPU)
        public double? fps1 { get; set; }     // « 1 % low » : FPS du 99e centile des temps d'image (fenêtre de 2 s)
        public double? fpsMs { get; set; }    // temps d'image moyen (ms)
        public int? sacc { get; set; }        // saccades dans la fenêtre de 2 s (image > 2,5 × la médiane)
        public List<FanSample> fans { get; set; } = new List<FanSample>();
        public List<StorSample> stor { get; set; } = new List<StorSample>();
        public double? v12 { get; set; }      // rail +12 V (alimentation du CPU et de la carte graphique)
        public double? v5 { get; set; }
        public double? v33 { get; set; }
        public List<VoltSample> volts { get; set; } = new List<VoltSample>();
        public RebootInfo rb { get; set; }
        public double? bat { get; set; }      // % batterie (portables)
        public bool? ac { get; set; }         // sur secteur
        [JsonIgnore] public DateTime Time { get; set; }
        [JsonIgnore] public double TotalMB { get; set; }
        [JsonIgnore] public List<LeakInfo> Leaks { get; set; }
        [JsonIgnore] public bool GameActive { get; set; }
        /// <summary>Saccades comptées depuis le lancement (voir <see cref="FpsMeter.StutterTotal"/>) : sert au rapport de session.</summary>
        [JsonIgnore] public long StutterTotal { get; set; }
        /// <summary>Mémoire vidéo totale (Go) quand elle est connue : affichée telle quelle, et non recalculée depuis vram et vramPct arrondis.</summary>
        [JsonIgnore] public double? VramTotal { get; set; }

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
        public VoltSample Volt(string name) { foreach (var v in volts) if (v.n == name) return v; return null; }
    }
}
