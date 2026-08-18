using System;
using System.Collections.Generic;
using System.Management;
using Microsoft.Win32;

namespace PerfMonitorLive.Metrics
{
    public class RebootInfo
    {
        public int score { get; set; }          // 0-100
        public double up { get; set; }          // heures depuis le démarrage
        public bool wu { get; set; }            // reboot Windows Update en attente
        public bool cbs { get; set; }           // reboot composants (CBS) en attente
        public bool pfr { get; set; }           // PendingFileRenameOperations
        public double commit { get; set; }      // % de la mémoire validée
        public double hnd { get; set; }         // handles (milliers)
        public double pool { get; set; }        // pool non paginé (Mo)
        public int procs { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public List<string> Reasons { get; set; } = new List<string>();
        [System.Text.Json.Serialization.JsonIgnore] public string Level => score >= 70 ? "Recommandé" : score >= 30 ? "Conseillé bientôt" : "Pas nécessaire";
    }

    /// <summary>Faisceau d'indices « le PC a besoin d'un redémarrage » (recalculé toutes les 60 s).</summary>
    public class RebootCheck
    {
        RebootInfo _last; DateTime _time = DateTime.MinValue;
        static readonly DateTime Boot = GetBoot();

        static DateTime GetBoot()
        {
            try { return DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64); } catch { return DateTime.Now; }
        }

        public RebootInfo Get(DateTime now)
        {
            if (_last != null && (now - _time).TotalSeconds < 60) return _last;
            _time = now;
            var r = new RebootInfo { up = Math.Round((now - Boot).TotalHours, 1) };
            try
            {
                r.wu = Exists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
                r.cbs = Exists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
                    r.pfr = k?.GetValue("PendingFileRenameOperations") is string[] arr && arr.Length > 0;
            }
            catch (Exception ex) { Paths.Log("Reboot reg: " + ex.Message); }
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT CommittedBytes,CommitLimit,PoolNonpagedBytes FROM Win32_PerfFormattedData_PerfOS_Memory"))
                    foreach (ManagementObject o in s.Get())
                    {
                        double c = Convert.ToDouble(o["CommittedBytes"]), l = Convert.ToDouble(o["CommitLimit"]);
                        r.commit = l > 0 ? Math.Round(c * 100 / l, 1) : 0;
                        r.pool = Math.Round(Convert.ToDouble(o["PoolNonpagedBytes"]) / 1048576.0);
                    }
                using (var s = new ManagementObjectSearcher("SELECT HandleCount FROM Win32_PerfFormattedData_PerfProc_Process WHERE Name='_Total'"))
                    foreach (ManagementObject o in s.Get()) r.hnd = Math.Round(Convert.ToDouble(o["HandleCount"]) / 1000.0, 1);
                using (var s = new ManagementObjectSearcher("SELECT NumberOfProcesses FROM Win32_OperatingSystem"))
                    foreach (ManagementObject o in s.Get()) r.procs = Convert.ToInt32(o["NumberOfProcesses"]);
            }
            catch (Exception ex) { Paths.Log("Reboot wmi: " + ex.Message); }

            int score = 0;
            if (r.wu || r.cbs) { score += 50; r.Reasons.Add(r.wu ? "une mise à jour Windows attend le redémarrage" : "des composants Windows attendent le redémarrage"); }
            if (r.pfr) { score += 15; r.Reasons.Add("des fichiers (pilote/installation) seront remplacés au prochain démarrage"); }
            if (r.up >= 168) { score += 40; r.Reasons.Add("allumé depuis " + Math.Round(r.up / 24) + " jours"); }
            else if (r.up >= 72) { score += 20; r.Reasons.Add("allumé depuis " + Math.Round(r.up / 24) + " jours"); }
            else if (r.up >= 24) { score += 10; }
            if (r.commit >= 85) { score += 30; r.Reasons.Add("mémoire validée à " + Math.Round(r.commit) + " % (fuite probable)"); }
            else if (r.commit >= 70) { score += 15; r.Reasons.Add("mémoire validée à " + Math.Round(r.commit) + " %"); }
            if (r.hnd >= 400) { score += 30; r.Reasons.Add(Math.Round(r.hnd) + " k handles ouverts (fuite probable)"); }
            else if (r.hnd >= 250) { score += 15; r.Reasons.Add(Math.Round(r.hnd) + " k handles ouverts"); }
            if (r.pool >= 3072) { score += 30; r.Reasons.Add("pool non paginé à " + Math.Round(r.pool / 1024.0, 1) + " Go (pilote qui fuit)"); }
            else if (r.pool >= 2048) { score += 15; r.Reasons.Add("pool non paginé à " + Math.Round(r.pool / 1024.0, 1) + " Go"); }
            r.score = Math.Min(100, score);
            _last = r; return r;
        }

        static bool Exists(string path) { using (var k = Registry.LocalMachine.OpenSubKey(path)) return k != null; }
        public static string FormatUptime(double h) => h < 24 ? Math.Floor(h) + " h " + Math.Round((h - Math.Floor(h)) * 60) + " min" : Math.Floor(h / 24) + " j " + Math.Round(h % 24) + " h";
    }
}

