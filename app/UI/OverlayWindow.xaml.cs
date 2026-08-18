using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PerfMonitorLive.Metrics;
using WF = System.Windows.Forms;

namespace PerfMonitorLive.UI
{
    /// <summary>Bandeau compact en haut de l'écran cible pendant une session de jeu.</summary>
    public partial class OverlayWindow : Window
    {
        readonly Func<WF.Screen> _screen;
        public OverlayWindow(Func<WF.Screen> screen)
        {
            InitializeComponent();
            _screen = screen;
            SourceInitialized += (s, e) =>
            {
                // fenêtre « outil » transparente aux clics : ne gêne jamais
                var h = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            };
            Loaded += (s, e) => Place();
            SizeChanged += (s, e) => Place();
        }

        public void SetGame(string g) => GameText.Text = "🎮 " + g;

        public void Update(Sample s)
        {
            CpuText.Text = "CPU " + Math.Round(s.cpu) + " %";
            CpuText.Foreground = Palette.B(s.cpu >= 90 ? Palette.Bad : s.cpu >= 70 ? Palette.Warn : Palette.Good);
            TempText.Text = s.temp.HasValue ? "🌡 " + s.temp.Value.ToString("0") + " °C" : "";
            TempText.Foreground = Palette.B(s.temp >= 85 ? Palette.Bad : s.temp >= 75 ? Palette.Warn : Palette.Good);
            GpuText.Text = s.gpu.HasValue ? "GPU " + s.gpu.Value.ToString("0") + " °C" + (s.gpuMHz.HasValue ? " · " + s.gpuMHz.Value.ToString("0") + " MHz" : "") : "";
            GpuText.Foreground = Palette.B(s.gpu >= 90 ? Palette.Bad : s.gpu >= 80 ? Palette.Warn : Palette.Good);
            MemText.Text = "RAM " + Math.Round(s.memPct) + " %";
            MemText.Foreground = Palette.B(s.memPct >= 90 ? Palette.Bad : s.memPct >= 80 ? Palette.Warn : Palette.Good);
            var d = s.disks.OrderByDescending(x => x.pct).FirstOrDefault();
            DiskText.Text = d != null ? "💽 " + d.n.Split(' ').Last() + " " + d.pct + " %" : "";
            ClockText.Text = s.Time.ToString("HH:mm");
        }

        public void Place()
        {
            try
            {
                var scr = _screen(); var b = scr.Bounds;
                var h = new WindowInteropHelper(this).Handle; if (h == IntPtr.Zero) return;
                uint dpi = 96; try { var mon = MonitorFromPoint(new POINT { X = b.Left + b.Width / 2, Y = b.Top + 5 }, 2); GetDpiForMonitor(mon, 0, out dpi, out _); } catch { }
                double scale = dpi / 96.0;
                int w = (int)Math.Round(ActualWidth * scale), hh = (int)Math.Round(ActualHeight * scale);
                SetWindowPos(h, new IntPtr(-1), b.Left + (b.Width - w) / 2, b.Top, w, hh, 0x0010 | 0x0040);
            }
            catch { }
        }

        const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
