using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PerfMonitorLive.Alerts;
using WF = System.Windows.Forms;

namespace PerfMonitorLive.Metrics
{
    /// <summary>Détecte une application plein écran (jeu) au premier plan, ou un processus de la liste utilisateur ; suit la session.</summary>
    public class GameDetector
    {
        readonly Settings _s;
        public bool Active { get; private set; }
        public string Game { get; private set; }
        DateTime _seenSince = DateTime.MinValue, _lastSeen = DateTime.MinValue, _start;
        string _candidate;
        // stats de session
        int _n; double _cpuSum, _cpuMax, _tempMax, _gpuMax, _memMax;
        public event Action<string> Started;
        public event Action<GameSession> Ended;
        static readonly HashSet<string> Exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "explorer", "dwm", "PerfMonitorLive", "brave", "chrome", "msedge", "firefox", "opera", "vivaldi", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost", "LockApp", "ApplicationFrameHost", "SystemSettings", "Taskmgr", "mstsc", "vlc", "wmplayer", "Video.UI", "Photos", "mspaint", "notepad", "Code", "devenv", "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "Teams", "ms-teams", "Discord", "Spotify", "OverwolfBrowser", "Overwolf", "steamwebhelper", "EpicWebHelper", "TextInputHost", "MicrosoftEdgeWebView2", "msedgewebview2" };

        public GameDetector(Settings s) { _s = s; }

        /// <summary>À appeler ~toutes les 2 s (thread UI). Renvoie true si un jeu est actif.</summary>
        public bool Tick(Sample latest)
        {
            string found = null;
            try
            {
                var h = GetForegroundWindow();
                if (h != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    string name = null;
                    try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; } catch { }
                    if (name != null && !Exclude.Contains(name))
                    {
                        if (_s.Games.Any(g => g.Equals(name, StringComparison.OrdinalIgnoreCase))) found = name;
                        else if (_s.GameAutoDetect && GetWindowRect(h, out var r))
                        {
                            foreach (var scr in WF.Screen.AllScreens)
                            {
                                var b = scr.Bounds;
                                if (Math.Abs(r.L - b.Left) <= 2 && Math.Abs(r.T - b.Top) <= 2 && Math.Abs((r.R - r.L) - b.Width) <= 2 && Math.Abs((r.B - r.T) - b.Height) <= 2) { found = name; break; }
                            }
                        }
                    }
                }
            }
            catch { }
            var now = DateTime.Now;
            if (found != null)
            {
                if (found != _candidate) { _candidate = found; _seenSince = now; }
                _lastSeen = now;
                if (!Active && (now - _seenSince).TotalSeconds >= 8) Begin(found, now);
            }
            else if (Active && (now - _lastSeen).TotalSeconds >= 30) End(now);
            if (Active && latest != null)
            {
                _n++; _cpuSum += latest.cpu; _cpuMax = Math.Max(_cpuMax, latest.cpu);
                if (latest.temp.HasValue) _tempMax = Math.Max(_tempMax, latest.temp.Value);
                if (latest.gpu.HasValue) _gpuMax = Math.Max(_gpuMax, latest.gpu.Value);
                _memMax = Math.Max(_memMax, latest.memPct);
            }
            return Active;
        }

        void Begin(string game, DateTime now)
        {
            Active = true; Game = game; _start = now; _n = 0; _cpuSum = _cpuMax = _tempMax = _gpuMax = _memMax = 0;
            Paths.Log("Session jeu : " + game);
            Started?.Invoke(game);
        }
        void End(DateTime now)
        {
            Active = false;
            var sess = new GameSession { ts = _start.ToString("yyyy-MM-ddTHH:mm:ss"), game = Game, min = Math.Round((now - _start).TotalMinutes, 1), cpuAvg = _n > 0 ? Math.Round(_cpuSum / _n) : 0, cpuMax = _cpuMax, tempMax = _tempMax, gpuMax = _gpuMax, memMax = Math.Round(_memMax) };
            try { Directory.CreateDirectory(Paths.DataDir); File.AppendAllText(Path.Combine(Paths.DataDir, "sessions.jsonl"), JsonSerializer.Serialize(sess) + "\n", new UTF8Encoding(false)); } catch { }
            Game = null; _candidate = null;
            Ended?.Invoke(sess);
        }
        public void ForceEnd() { if (Active) End(DateTime.Now); }

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    }
}
