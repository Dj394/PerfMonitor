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
