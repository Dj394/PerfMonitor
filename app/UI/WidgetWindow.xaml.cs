using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PerfMonitorLive.Metrics;
using WF = System.Windows.Forms;

namespace PerfMonitorLive.UI
{
    /// <summary>Mini widget « toujours visible » : CPU · temp · GPU · RAM · disque.</summary>
    public partial class WidgetWindow : Window
    {
        readonly Settings _s; readonly Func<WF.Screen> _screen; readonly Action _open;
        public WidgetWindow(Settings s, Func<WF.Screen> screen, Action open)
        {
            InitializeComponent();
            _s = s; _screen = screen; _open = open;
            Opacity = s.WidgetOpacity;
            SourceInitialized += (sender, e) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
                if (double.IsNaN(_s.WidgetX) || double.IsNaN(_s.WidgetY)) ResetPosition(); else PlacePx((int)_s.WidgetX, (int)_s.WidgetY);
            };
            Card.MouseLeftButtonDown += (sender, e) => { if (e.ClickCount == 2) { _open?.Invoke(); return; } try { DragMove(); } catch { } SavePosition(); };
            MouseEnter += (sender, e) => Opacity = 1;
            MouseLeave += (sender, e) => Opacity = _s.WidgetOpacity;
        }

        public void ApplyOpacity() => Opacity = _s.WidgetOpacity;

        public void Update(Sample smp)
        {
            T1.Text = "CPU " + Math.Round(smp.cpu) + " %"; T1.Foreground = Palette.B(smp.cpu >= 90 ? Palette.Bad : smp.cpu >= 70 ? Palette.Warn : Palette.Good);
            T2.Text = smp.temp.HasValue ? smp.temp.Value.ToString("0") + " °C" : "—"; T2.Foreground = Palette.B(smp.temp >= 85 ? Palette.Bad : smp.temp >= 75 ? Palette.Warn : Palette.Good);
            T3.Text = smp.gpu.HasValue ? "GPU " + smp.gpu.Value.ToString("0") + " °C" : "GPU —"; T3.Foreground = Palette.B(smp.gpu >= 90 ? Palette.Bad : smp.gpu >= 80 ? Palette.Warn : Palette.Good);
            T4.Text = "RAM " + Math.Round(smp.memPct) + " %"; T4.Foreground = Palette.B(smp.memPct >= 90 ? Palette.Bad : smp.memPct >= 80 ? Palette.Warn : Palette.Good);
            var d = smp.disks.OrderByDescending(x => x.pct).FirstOrDefault();
            T5.Text = d != null ? "💽 " + d.n.Split(' ').Last() + " " + d.pct + " %" : ""; T5.Foreground = Palette.B(d != null && d.pct >= 90 ? Palette.Bad : Palette.Muted);
        }

        void PlacePx(int x, int y)
        {
            var h = new WindowInteropHelper(this).Handle; if (h == IntPtr.Zero) return;
            SetWindowPos(h, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010 | 0x0040);
        }
        public void ResetPosition()
        {
            var b = _screen().WorkingArea;
            uint dpi = 96; try { var mon = MonitorFromPoint(new POINT { X = b.Left + b.Width / 2, Y = b.Top + 5 }, 2); GetDpiForMonitor(mon, 0, out dpi, out _); } catch { }
            int w = (int)Math.Round((ActualWidth > 0 ? ActualWidth : 360) * dpi / 96.0);
            PlacePx(b.Right - w - 12, b.Top + 12);
            SavePosition();
        }
        void SavePosition()
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle; if (h == IntPtr.Zero) return;
                if (GetWindowRect(h, out var r)) { _s.WidgetX = r.L; _s.WidgetY = r.T; _s.Save(); }
            }
            catch { }
        }
        void Open_Click(object s, RoutedEventArgs e) => _open?.Invoke();
        void Reset_Click(object s, RoutedEventArgs e) => ResetPosition();
        void Hide_Click(object s, RoutedEventArgs e) { _s.WidgetEnabled = false; _s.Save(); Hide(); HiddenByUser?.Invoke(); }
        public event Action HiddenByUser;

        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
