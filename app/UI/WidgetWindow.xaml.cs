using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PerfMonitorLive.Metrics;
using WF = System.Windows.Forms;

namespace PerfMonitorLive.UI
{
    /// <summary>Mini widget « toujours visible » : CPU · temp · GPU · RAM · disque.</summary>
    public partial class WidgetWindow : Window
    {
        readonly Settings _s; readonly Func<WF.Screen> _screen; readonly Action _open;
        /// <summary>Ombre mise en cache bitmap à la résolution de l'écran (sinon floue à 125/150 %), comme dans MainWindow.</summary>
        void ApplyBitmapCache(double scale)
        {
            if (scale <= 0) scale = 1;
            ShadowLayer.CacheMode = new BitmapCache(scale) { SnapsToDevicePixels = true };
        }
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            ApplyBitmapCache(newDpi.DpiScaleX);
        }

        public WidgetWindow(Settings s, Func<WF.Screen> screen, Action open)
        {
            InitializeComponent();
            SoftwareRender.Apply(this);
            Loaded += (_, _) => ApplyBitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX);
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
            // Texte et pinceau ne sont réaffectés que si la valeur affichée change (pinceaux gelés partagés de Palette : comparés par référence)
            double cr = Math.Round(smp.cpu);
            if (cr != _cpuR) { _cpuR = cr; T1.Text = "CPU " + cr + " %"; }
            LiveText.SetBrush(T1, Palette.B(smp.cpu >= 90 ? Palette.Bad : smp.cpu >= 70 ? Palette.Warn : Palette.Good), ref _b1);

            long k = LiveText.Key(smp.temp);
            if (k != _tempK) { _tempK = k; T2.Text = k != LiveText.None ? smp.temp.Value.ToString("0") + " °C" : "—"; }
            LiveText.SetBrush(T2, Palette.B(smp.temp >= 85 ? Palette.Bad : smp.temp >= 75 ? Palette.Warn : Palette.Good), ref _b2);

            k = LiveText.Key(smp.gpu);
            if (k != _gpuK) { _gpuK = k; T3.Text = k != LiveText.None ? "GPU " + smp.gpu.Value.ToString("0") + " °C" : "GPU —"; }
            LiveText.SetBrush(T3, Palette.B(smp.gpu >= 90 ? Palette.Bad : smp.gpu >= 80 ? Palette.Warn : Palette.Good), ref _b3);

            double mr = Math.Round(smp.memPct);
            if (mr != _memR) { _memR = mr; T4.Text = "RAM " + mr + " %"; }
            LiveText.SetBrush(T4, Palette.B(smp.memPct >= 90 ? Palette.Bad : smp.memPct >= 80 ? Palette.Warn : Palette.Good), ref _b4);

            // en jeu, les images par seconde priment sur l'activite disque : c'est ce qu'on regarde a ce moment-la
            if (smp.fps.HasValue)
            {
                long f = LiveText.Key(smp.fps);
                if (_t5Mode != 1 || f != _fpsK) { _t5Mode = 1; _fpsK = f; T5.Text = smp.fps.Value.ToString("0") + " FPS"; }
                LiveText.SetBrush(T5, Palette.B(smp.fps < 30 ? Palette.Bad : smp.fps < 60 ? Palette.Warn : Palette.Good), ref _b5);
            }
            else
            {
                DiskSample d = null;   // disque le plus occupé (le premier en cas d'égalité, comme OrderByDescending)
                foreach (var x in smp.disks) if (d == null || x.pct > d.pct) d = x;
                int p = d != null ? d.pct : int.MinValue; string n = d?.n;
                if (_t5Mode != 2 || p != _diskP || n != _diskN) { _t5Mode = 2; _diskP = p; _diskN = n; T5.Text = d != null ? "💽 " + LiveText.LastToken(d.n) + " " + d.pct + " %" : ""; }
                LiveText.SetBrush(T5, Palette.B(d != null && d.pct >= 90 ? Palette.Bad : Palette.Muted), ref _b5);
            }
        }
        double _cpuR = double.NaN, _memR = double.NaN;
        long _tempK = long.MaxValue, _gpuK = long.MaxValue, _fpsK = long.MaxValue;
        int _t5Mode, _diskP; string _diskN;
        Brush _b1, _b2, _b3, _b4, _b5;

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
