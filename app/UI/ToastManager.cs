using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PerfMonitorLive.Alerts;
using WF = System.Windows.Forms;

namespace PerfMonitorLive.UI
{
    /// <summary>Affiche et empile les notifications sur l'écran choisi (par défaut : l'écran secondaire).</summary>
    public class ToastManager
    {
        readonly Settings _settings;
        readonly List<ToastWindow> _open = new List<ToastWindow>();
        public event Action ToastClicked;
        /// <summary>Vrai pendant une session de jeu : seules les alertes critiques passent.</summary>
        public Func<bool> GameActive { get; set; } = () => false;
        const int MaxToasts = 5;

        public ToastManager(Settings settings) { _settings = settings; }

        public WF.Screen TargetScreen()
        {
            var screens = WF.Screen.AllScreens;
            if (screens.Length == 0) return WF.Screen.PrimaryScreen;
            switch (_settings.ScreenMode)
            {
                case "Primary": return WF.Screen.PrimaryScreen ?? screens[0];
                case "Index":
                    if (_settings.ScreenIndex >= 0 && _settings.ScreenIndex < screens.Length) return screens[_settings.ScreenIndex];
                    break;
            }
            return screens.FirstOrDefault(s => !s.Primary) ?? WF.Screen.PrimaryScreen ?? screens[0];
        }

        /// <summary>Doit-on afficher cette alerte maintenant (pause, mode jeu, profil « critiques seulement ») ?</summary>
        public bool ShouldShow(AlertInfo a)
        {
            if (a.Severity == Severity.Info) return true;
            if (_settings.IsPaused) return false;
            if ((GameActive() || _settings.ActiveOnlyCritical) && a.Severity != Severity.Critical) return false;
            return true;
        }

        public void Show(AlertInfo a, bool force = false)
        {
            if (!force && !ShouldShow(a)) return;
            while (_open.Count >= MaxToasts) { var oldest = _open[0]; _open.RemoveAt(0); oldest.FadeClose(); }
            var w = new ToastWindow(a, a.Severity == Severity.Info && a.RuleId == "digest" ? 0 : _settings.ToastSec);
            w.Closed2 += t => { _open.Remove(t); Relayout(); };
            w.Clicked += () => ToastClicked?.Invoke();
            _open.Add(w);
            var helper = new WindowInteropHelper(w); helper.EnsureHandle();
            var scr = TargetScreen(); w.Left = scr.WorkingArea.Right - w.Width - 12; w.Top = scr.WorkingArea.Bottom - 120;
            w.Show();
            Relayout();
            w.Dispatcher.BeginInvoke(new Action(Relayout), DispatcherPriority.Loaded);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            t.Tick += (s, e) => { t.Stop(); Relayout(); };
            t.Start();
            if (_settings.Sound && a.Severity != Severity.Info)
            {
                try { if (a.Severity == Severity.Critical) System.Media.SystemSounds.Hand.Play(); else if (a.Severity == Severity.Ok) System.Media.SystemSounds.Asterisk.Play(); else System.Media.SystemSounds.Exclamation.Play(); } catch { }
            }
        }

        public void CloseAll() { foreach (var w in _open.ToArray()) w.FadeClose(); }

        void Relayout()
        {
            var scr = TargetScreen();
            var wa = scr.WorkingArea; var b = scr.Bounds;
            uint dpi = GetDpi(b.Left + b.Width / 2, b.Top + b.Height / 2);
            double scale = dpi / 96.0;
            int margin = (int)Math.Round(12 * scale), gap = (int)Math.Round(6 * scale);
            bool bottom = _settings.Corner.StartsWith("Bottom"), right = _settings.Corner.EndsWith("Right");
            int offset = 0;
            for (int i = _open.Count - 1; i >= 0; i--)
            {
                var w = _open[i];
                var h = new WindowInteropHelper(w).Handle; if (h == IntPtr.Zero) continue;
                double hDip = w.ActualHeight > 0 ? w.ActualHeight : 100;
                int wPx = (int)Math.Round(w.Width * scale), hPx = (int)Math.Round(hDip * scale);
                int x = right ? wa.Right - wPx - margin : wa.Left + margin;
                int y = bottom ? wa.Bottom - hPx - margin - offset : wa.Top + margin + offset;
                SetWindowPos(h, HWND_TOPMOST, x, y, wPx, hPx, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                offset += hPx + gap;
            }
        }

        static uint GetDpi(int x, int y)
        {
            try
            {
                var mon = MonitorFromPoint(new POINT { X = x, Y = y }, 2);
                if (GetDpiForMonitor(mon, 0, out uint dx, out uint dy) == 0 && dx > 0) return dx;
            }
            catch { }
            return 96;
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    }
}
