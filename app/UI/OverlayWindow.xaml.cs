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

        public OverlayWindow(Func<WF.Screen> screen)
        {
            InitializeComponent();
            SoftwareRender.Apply(this);
            Loaded += (_, _) => ApplyBitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX);
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
            // FPS : vide tant que le compteur n'a pas deux points de mesure sur le meme jeu (premiere seconde)
            // Texte et pinceau ne sont réaffectés que si la valeur affichée change (les pinceaux de Palette sont gelés et partagés,
            // donc comparés par référence) : un TextBlock inchangé ne déclenche ni mesure ni rendu.
            long k = LiveText.Key(s.fps);
            if (k != _fpsK) { _fpsK = k; FpsText.Text = k == LiveText.None ? "" : s.fps.Value.ToString("0") + " FPS"; }
            LiveText.SetBrush(FpsText, Palette.B(!s.fps.HasValue ? Palette.Good : s.fps < 30 ? Palette.Bad : s.fps < 60 ? Palette.Warn : Palette.Good), ref _fpsB);

            double cr = Math.Round(s.cpu);
            if (cr != _cpuR) { _cpuR = cr; CpuText.Text = "CPU " + cr + " %"; }
            LiveText.SetBrush(CpuText, Palette.B(s.cpu >= 90 ? Palette.Bad : s.cpu >= 70 ? Palette.Warn : Palette.Good), ref _cpuB);

            k = LiveText.Key(s.temp);
            if (k != _tempK) { _tempK = k; TempText.Text = k == LiveText.None ? "" : "🌡 " + s.temp.Value.ToString("0") + " °C"; }
            LiveText.SetBrush(TempText, Palette.B(s.temp >= 85 ? Palette.Bad : s.temp >= 75 ? Palette.Warn : Palette.Good), ref _tempB);

            k = LiveText.Key(s.gpu); long k2 = LiveText.Key(s.gpuMHz), k3 = LiveText.Key(s.gpuLoad);
            if (k != _gpuK || k2 != _gpuMHzK || k3 != _gpuLoadK)
            {
                _gpuK = k; _gpuMHzK = k2; _gpuLoadK = k3;
                // charge GPU en tête : c'est elle qui dit si la carte graphique limite les images par seconde
                string load = k3 == LiveText.None ? "" : k3 + " %";
                GpuText.Text = k == LiveText.None
                    ? (load.Length > 0 ? "GPU " + load : "")
                    : "GPU " + (load.Length > 0 ? load + " · " : "") + s.gpu.Value.ToString("0") + " °C" + (k2 != LiveText.None ? " · " + s.gpuMHz.Value.ToString("0") + " MHz" : "");
            }
            LiveText.SetBrush(GpuText, Palette.B(s.gpu >= 90 ? Palette.Bad : s.gpu >= 80 ? Palette.Warn : Palette.Good), ref _gpuB);

            double mr = Math.Round(s.memPct);
            if (mr != _memR) { _memR = mr; MemText.Text = "RAM " + mr + " %"; }
            LiveText.SetBrush(MemText, Palette.B(s.memPct >= 90 ? Palette.Bad : s.memPct >= 80 ? Palette.Warn : Palette.Good), ref _memB);

            DiskSample d = null;   // disque le plus occupé (le premier en cas d'égalité, comme OrderByDescending)
            foreach (var x in s.disks) if (d == null || x.pct > d.pct) d = x;
            if (d == null) { if (_diskN != null || _diskP != int.MinValue) { _diskN = null; _diskP = int.MinValue; DiskText.Text = ""; } }
            else if (_diskP != d.pct || _diskN != d.n) { _diskN = d.n; _diskP = d.pct; DiskText.Text = "💽 " + LiveText.LastToken(d.n) + " " + d.pct + " %"; }

            int hm = s.Time.Hour * 60 + s.Time.Minute;
            if (hm != _clockK) { _clockK = hm; ClockText.Text = s.Time.ToString("HH:mm"); }
        }
        long _fpsK = long.MaxValue, _tempK = long.MaxValue, _gpuK = long.MaxValue, _gpuMHzK = long.MaxValue, _gpuLoadK = long.MaxValue;
        double _cpuR = double.NaN, _memR = double.NaN;
        Brush _fpsB, _cpuB, _tempB, _gpuB, _memB;
        string _diskN; int _diskP = int.MaxValue; int _clockK = -1;

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

namespace PerfMonitorLive.UI
{
    /// <summary>Aides communes à l'overlay et au widget : ne toucher un TextBlock que lorsque l'affichage change.</summary>
    internal static class LiveText
    {
        public const long None = long.MinValue;
        /// <summary>Valeur arrondie à l'unité (celle que « ToString("0") » affiche), ou <see cref="None"/> si absente.</summary>
        public static long Key(double? v) => v.HasValue && double.IsFinite(v.Value) ? (long)Math.Round(v.Value, MidpointRounding.AwayFromZero) : None;
        public static void SetBrush(System.Windows.Controls.TextBlock t, Brush b, ref Brush last)
        {
            if (ReferenceEquals(last, b)) return;
            last = b; t.Foreground = b;
        }
        /// <summary>Dernier mot du nom (« 0 C: » → « C: »), comme <c>n.Split(' ').Last()</c> sans tableau.</summary>
        public static string LastToken(string n) { int i = n.LastIndexOf(' '); return i < 0 ? n : n.Substring(i + 1); }
    }
}
