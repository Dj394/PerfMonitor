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
        /// <summary>PID du jeu au premier plan, 0 si aucun. Sert a rattacher les images comptees par <see cref="FpsMeter"/>
        /// au bon processus : le nom ne suffit pas, ETW ne connait que les PID.
        /// Vaut 0 des que le jeu n'est plus au premier plan (alt-tab, jeu ferme), meme si la session reste active
        /// pendant son hysteresis de 30 s, et pendant une session, 0 si une autre application plein ecran passe devant.</summary>
        public int Pid { get; private set; }
        DateTime _seenSince = DateTime.MinValue, _lastSeen = DateTime.MinValue, _start;
        string _candidate; int _fgPid;
        // stats de session
        int _n; double _cpuSum, _cpuMax, _tempMax, _gpuMax, _memMax;
        int _nGpu, _nGpuBound, _nCpuBound; double _gpuLoadSum;   // charge GPU et indice limité CPU / GPU
        int _fpsN, _lowN; double _fpsSum, _lowSum; long _saccBase = -1, _saccLast;
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
                    string name;
                    if (pid != 0 && (int)pid == _namePid) name = _nameOfPid;   // même fenêtre qu'au tick précédent : pas de nouvelle requête
                    else
                    {
                        name = ProcName(pid);
                        if (name != null) { _namePid = (int)pid; _nameOfPid = name; }   // un échec n'est pas mémorisé : on réessaie au prochain tick
                        else _namePid = -1;
                    }
                    if (name != null && !Exclude.Contains(name))
                    {
                        _fgPid = (int)pid;
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
            return Step(found, _fgPid, DateTime.Now, latest);
        }

        /// <summary>Logique de session, sans Win32 : <paramref name="found"/> = jeu au premier plan (null si aucun).
        /// Un autre jeu au premier plan pendant une session ne la prolonge pas ; s'il y reste 8 s, la session en cours
        /// se termine (rapport emis) et la sienne commence. Sans jeu au premier plan, la session dure encore 30 s.</summary>
        internal bool Step(string found, int fgPid, DateTime now, Sample latest)
        {
            if (found != null)
            {
                if (!string.Equals(found, _candidate, StringComparison.OrdinalIgnoreCase)) { _candidate = found; _seenSince = now; }
                bool same = Active && string.Equals(found, Game, StringComparison.OrdinalIgnoreCase);
                if (same || !Active) _lastSeen = now;   // un autre jeu ne maintient pas la session en cours
                Pid = ForegroundGamePid(Active, Game, found, fgPid);
                if (Active && !same && (now - _seenSince).TotalSeconds >= 8)
                {
                    End(_lastSeen > _start ? _lastSeen : now);   // la session A s'arrete a sa derniere apparition
                    Pid = ForegroundGamePid(false, null, found, fgPid);
                }
                if (!Active && (now - _seenSince).TotalSeconds >= 8) Begin(found, now);
                else if (Active && !same && (now - _lastSeen).TotalSeconds >= 30) End(now);
            }
            else
            {
                Pid = 0;   // plus de jeu au premier plan : on ne mesure plus (sinon faux « 0 FPS » sur un PID absent ou masque)
                if (Active && (now - _lastSeen).TotalSeconds >= 30) End(now);
            }
            if (Active && latest != null)
            {
                _n++; _cpuSum += latest.cpu; _cpuMax = Math.Max(_cpuMax, latest.cpu);
                if (latest.temp.HasValue) _tempMax = Math.Max(_tempMax, latest.temp.Value);
                if (latest.gpu.HasValue) _gpuMax = Math.Max(_gpuMax, latest.gpu.Value);
                _memMax = Math.Max(_memMax, latest.memPct);
                if (latest.gpuLoad.HasValue)
                {
                    _nGpu++; _gpuLoadSum += latest.gpuLoad.Value;
                    int b = Bottleneck.Classify(latest.gpuLoad, latest.cpuCore, latest.cpu);
                    if (b == Bottleneck.Gpu) _nGpuBound++; else if (b == Bottleneck.Cpu) _nCpuBound++;
                }
                if (latest.fps.HasValue) { _fpsN++; _fpsSum += latest.fps.Value; }
                if (latest.fps1.HasValue) { _lowN++; _lowSum += latest.fps1.Value; }
                // total monotone : la différence fin - début compte chaque saccade une fois, même entre deux ticks
                if (_saccBase < 0) _saccBase = latest.StutterTotal;
                _saccLast = Math.Max(_saccLast, latest.StutterTotal);
            }
            return Active;
        }

        void Begin(string game, DateTime now)
        {
            Active = true; Game = game; _start = now; _n = 0; _cpuSum = _cpuMax = _tempMax = _gpuMax = _memMax = 0; _nGpu = _nGpuBound = _nCpuBound = 0; _gpuLoadSum = 0;
            _fpsN = _lowN = 0; _fpsSum = _lowSum = 0; _saccBase = -1; _saccLast = 0;
            Log("Session jeu : " + game);
            Started?.Invoke(game);
        }
        void End(DateTime now)
        {
            Active = false;
            var sess = new GameSession { ts = _start.ToString("yyyy-MM-ddTHH:mm:ss"), game = Game, min = Math.Round((now - _start).TotalMinutes, 1), cpuAvg = _n > 0 ? Math.Round(_cpuSum / _n) : 0, cpuMax = _cpuMax, tempMax = _tempMax, gpuMax = _gpuMax, memMax = Math.Round(_memMax),
                gpuLoadAvg = _nGpu > 0 ? Math.Round(_gpuLoadSum / _nGpu) : (double?)null, bound = Bottleneck.Verdict(_nGpuBound, _nCpuBound, _nGpu),
                fpsAvg = _fpsN > 0 ? Math.Round(_fpsSum / _fpsN) : null, fps1 = _lowN > 0 ? Math.Round(_lowSum / _lowN) : null,
                sacc = _fpsN > 0 && _saccBase >= 0 ? Math.Max(0, _saccLast - _saccBase) : null };
            try { Save(sess); } catch { }
            Game = null; _candidate = null; Pid = 0;
            Ended?.Invoke(sess);
        }

        /// <summary>Journal et persistance des sessions ; remplaçables par les tests (qui n'ecrivent pas dans data/).</summary>
        internal Action<string> Log = Paths.Log;
        internal Action<GameSession> Save = sess =>
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.AppendAllText(Path.Combine(Paths.DataDir, "sessions.jsonl"), JsonSerializer.Serialize(sess) + "\n", new UTF8Encoding(false));
        };
        public void ForceEnd() { if (Active) End(DateTime.Now); }

        /// <summary>PID a mesurer pour l'application <paramref name="found"/> au premier plan. Hors session : le sien
        /// (la session demarrera dessus). En session : seulement s'il s'agit du jeu suivi (meme nom, eventuellement
        /// relance sous un autre PID) ; une autre application plein ecran ne detourne pas la mesure.</summary>
        internal static int ForegroundGamePid(bool active, string game, string found, int fgPid)
        {
            if (found == null || fgPid <= 0) return 0;
            if (active && !string.Equals(found, game, StringComparison.OrdinalIgnoreCase)) return 0;
            return fgPid;
        }

        int _namePid = -1; string _nameOfPid;
        readonly char[] _pathBuf = new char[1024];

        /// <summary>Nom du processus sans extension, comme <see cref="Process.ProcessName"/>, mais via OpenProcess + QueryFullProcessImageName :
        /// <c>Process.GetProcessById(..).ProcessName</c> énumère tous les processus du système à chaque appel.</summary>
        string ProcName(uint pid)
        {
            if (pid == 0) return null;
            IntPtr hp = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (hp != IntPtr.Zero)
            {
                try
                {
                    uint len = (uint)_pathBuf.Length;
                    if (QueryFullProcessImageNameW(hp, 0, _pathBuf, ref len) && len > 0)
                    {
                        int start = 0;
                        for (int i = (int)len - 1; i >= 0; i--) if (_pathBuf[i] == '\\' || _pathBuf[i] == '/') { start = i + 1; break; }
                        int n = (int)len - start;
                        if (n > 4 && string.Compare(new string(_pathBuf, (int)len - 4, 4), ".exe", StringComparison.OrdinalIgnoreCase) == 0) n -= 4;
                        if (n > 0) return new string(_pathBuf, start, n);
                    }
                }
                finally { CloseHandle(hp); }
            }
            try { using (var p = Process.GetProcessById((int)pid)) return p.ProcessName; } catch { return null; }   // repli (accès refusé, chemin trop long...)
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, [Out] char[] buf, ref uint size);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    }
}
