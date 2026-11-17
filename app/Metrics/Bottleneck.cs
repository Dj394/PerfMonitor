namespace PerfMonitorLive.Metrics
{
    /// <summary>Indice « limité par le GPU / par le CPU » d'une session de jeu. Heuristique volontairement simple, et présentée
    /// comme un indice, pas comme un verdict :
    /// <list type="bullet">
    /// <item>charge GPU ≥ 95 % → la carte graphique travaille à fond : c'est elle qui fixe les images par seconde ;</item>
    /// <item>charge GPU &lt; 80 % alors qu'un cœur CPU est saturé (≥ 90 %, ou le CPU entier à défaut de mesure par cœur)
    /// → la carte attend le processeur : limité par le CPU, probablement (un jeu dont le fil principal sature un cœur) ;</item>
    /// <item>entre les deux, ou GPU bas sans cœur saturé (images plafonnées par V-Sync / limiteur, menu, chargement)
    /// → aucun des deux n'est le goulot.</item>
    /// </list>
    /// Calcul sur des valeurs déjà mesurées, sans allocation : appelé à chaque pas de <see cref="GameDetector"/>.</summary>
    public static class Bottleneck
    {
        public const double GpuBoundPct = 95, GpuIdlePct = 80, CoreSaturatedPct = 90;
        public const int None = 0, Gpu = 1, Cpu = 2;

        /// <summary>Classe une mesure : <see cref="Gpu"/>, <see cref="Cpu"/> ou <see cref="None"/> (indéterminé, ou pas de charge GPU).
        /// <paramref name="coreMax"/> = charge du cœur logique le plus chargé (null si inconnue : on retombe sur la charge totale,
        /// qui ne peut être saturée sans qu'un cœur le soit).</summary>
        public static int Classify(double? gpuLoad, double? coreMax, double cpuTotal)
        {
            if (!gpuLoad.HasValue) return None;
            if (gpuLoad.Value >= GpuBoundPct) return Gpu;
            if (gpuLoad.Value < GpuIdlePct && (coreMax ?? cpuTotal) >= CoreSaturatedPct) return Cpu;
            return None;
        }

        /// <summary>Indice de session à partir des décomptes : « gpu » ou « cpu » si la moitié au moins des mesures avec charge GPU
        /// vont dans ce sens, « » (aucun goulot net) sinon, null si la charge GPU n'a jamais été mesurée.</summary>
        public static string Verdict(int nGpu, int nCpu, int nMeasured)
        {
            if (nMeasured <= 0) return null;
            if (nGpu * 2 >= nMeasured) return "gpu";
            if (nCpu * 2 >= nMeasured) return "cpu";
            return "";
        }

        /// <summary>Texte affiché (rapport de session, historique).</summary>
        public static string Text(string verdict) =>
            verdict == "gpu" ? "limité par le GPU" :
            verdict == "cpu" ? "limité par le CPU (probable)" :
            verdict == "" ? "ni CPU ni GPU à fond" : null;
    }
}
