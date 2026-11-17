using System;
using System.IO;

namespace PerfMonitorLive
{
    public static class Paths
    {
        public static readonly string BaseDir = FindBaseDir();
        public static string DataDir => Path.Combine(BaseDir, "data");
        public static string SettingsFile => Path.Combine(BaseDir, "settings.json");
        public static string ReportScript => Path.Combine(BaseDir, "report.ps1");
        public static string LogFile => Path.Combine(DataDir, "live.log");
        /// <summary>Banc de mesure (variable d'environnement PERFMON_BENCH) : instance isolée qui ne réveille pas l'instance installée,
        /// ne partage ni sa session ETW ni sa tâche planifiée et ne cherche pas de mise à jour.</summary>
        public static readonly bool Bench = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PERFMON_BENCH"));
        public static string InstanceSuffix => Bench ? "-Bench" : "";

        static string FindBaseDir()
        {
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
