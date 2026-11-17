using System;
using System.IO;

namespace PerfMonitorLive
{
    public static class Paths
    {
        /// <summary>Banc de mesure (variable d'environnement PERFMON_BENCH) : instance isolée qui ne réveille pas l'instance installée,
        /// ne partage ni sa session ETW ni sa tâche planifiée, ne cherche pas de mise à jour et, lancée depuis un dossier
        /// d'installation, garde ses propres réglages et données (voir <see cref="StateDir"/>).</summary>
        public static readonly bool Bench = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PERFMON_BENCH"));
        public static string InstanceSuffix => Bench ? "-Bench" : "";
        public static readonly string BaseDir = FindBaseDir();
        /// <summary>Dossier des réglages et des données : BaseDir, sauf pour le banc lancé depuis un dossier d'installation.</summary>
        public static readonly string StateDir = ResolveStateDir(BaseDir, Bench, !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PERFMON_BASEDIR")));
        public static string DataDir => Path.Combine(StateDir, "data");
        public static string SettingsFile => Path.Combine(StateDir, "settings.json");
        public static string ReportScript => Path.Combine(BaseDir, "report.ps1");
        public static string LogFile => Path.Combine(DataDir, "live.log");

        /// <summary>Le banc tourne en parallèle de l'instance installée (mutex suffixé « -Bench ») : s'il résout le même
        /// dossier qu'elle (dossier contenant report.ps1, trouvé en remontant depuis app\bin ou app\publish, ou exe du dépôt),
        /// il isole settings.json et data\ dans BaseDir\bench pour ne pas écraser ses réglages, doubler l'historique,
        /// le journal alimentation, les alertes ni lui voler la notification d'arrêt brutal. Un banc dans un dossier
        /// isolé (sans report.ps1) et la surcharge PERFMON_BASEDIR (tests, choix explicite) gardent BaseDir tel quel.</summary>
        internal static string ResolveStateDir(string baseDir, bool bench, bool forcedBaseDir)
        {
            if (!bench || forcedBaseDir) return baseDir;
            try { if (!File.Exists(Path.Combine(baseDir, "report.ps1"))) return baseDir; } catch { return baseDir; }
            var dir = Path.Combine(baseDir, "bench");
            // Créé dès la résolution : au premier lancement du banc, Settings.Save et Log écriraient sinon dans un dossier absent.
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        /// <summary>report.ps1 lit toujours $PSScriptRoot\data (pas de paramètre de dossier) : il ne cible les bonnes
        /// données que si le dossier d'état est BaseDir. Faux pour le banc isolé dans BaseDirench, qui lirait sinon
        /// les vraies données de l'instance installée.</summary>
        public static bool ReportAvailable => ReportTargetsState(BaseDir, StateDir);
        internal static bool ReportTargetsState(string baseDir, string stateDir) =>
            string.Equals(Path.GetFullPath(baseDir).TrimEnd('\\', '/'), Path.GetFullPath(stateDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        static string FindBaseDir()
        {
            // Surcharge (tests automatisés) : évite d'écrire dans le data\ et le settings.json réels du dépôt.
            var forced = Environment.GetEnvironmentVariable("PERFMON_BASEDIR");
            if (!string.IsNullOrEmpty(forced)) return forced;
            var dir = AppContext.BaseDirectory;
            var d = new DirectoryInfo(dir);
            for (int i = 0; i < 6 && d != null; i++)
            {
                if (File.Exists(Path.Combine(d.FullName, "report.ps1"))) return d.FullName;
                d = d.Parent;
            }
            return dir;
        }

        public static void Log(string msg)
        {
            try { Directory.CreateDirectory(DataDir); File.AppendAllText(LogFile, DateTime.Now.ToString("s") + " " + msg + Environment.NewLine); } catch { }
        }
    }
}
