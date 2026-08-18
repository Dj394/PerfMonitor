using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PerfMonitorLive.Alerts;

namespace PerfMonitorLive.UI
{
    public partial class ToastWindow : Window
    {
        public event Action<ToastWindow> Closed2;
        public event Action Clicked;
        readonly DispatcherTimer _timer = new DispatcherTimer();
        bool _closing;

        public ToastWindow(AlertInfo a, int seconds)
        {
            InitializeComponent();
            TitleText.Text = a.Title;
            DetailText.Text = a.Detail;
            var c = a.Severity == Severity.Critical ? Palette.Bad : a.Severity == Severity.Ok ? Palette.Good : a.Severity == Severity.Info ? Palette.Neutral : Palette.Warn;
            Dot.Background = Palette.B(c, 0x33);
            IconText.Text = a.Severity == Severity.Critical ? "🚨" : a.Severity == Severity.Ok ? "✅" : a.Severity == Severity.Info ? "💬" : "⚠️";
            Card.BorderBrush = Palette.B(c, a.Severity == Severity.Critical ? (byte)0xCC : (byte)0x66);
            if (a.Actions != null && a.Actions.Count > 0)
            {
                ActionsPanel.Visibility = Visibility.Visible;
                foreach (var act in a.Actions)
                {
                    var b = new Button { Content = act.Label, Margin = new Thickness(0, 0, 6, 4) };
                    if (act.Primary) b.Background = (Brush)TryFindResource("PrimaryBtnBrush") ?? b.Background;
                    var run = act.Run;
                    b.Click += (s, e) => { e.Handled = true; try { run?.Invoke(); } catch (Exception ex) { Paths.Log("action: " + ex.Message); } FadeClose(); };
                    ActionsPanel.Children.Add(b);
                }
            }
            CloseBtn.Click += (s, e) => { e.Handled = true; FadeClose(); };
            Card.MouseLeftButtonUp += (s, e) => { if (e.OriginalSource is Button || IsInside(e.OriginalSource as DependencyObject, ActionsPanel)) return; Clicked?.Invoke(); FadeClose(); };
            MouseEnter += (s, e) => _timer.Stop();
            MouseLeave += (s, e) => { if (!_closing) _timer.Start(); };
            if (seconds > 0)
            {
                _timer.Interval = TimeSpan.FromSeconds(seconds);
                _timer.Tick += (s, e) => FadeClose();
                _timer.Start();
            }
            Opacity = 0;
            Loaded += (s, e) => BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        static bool IsInside(DependencyObject d, DependencyObject parent)
        {
            while (d != null) { if (d == parent) return true; d = VisualTreeHelper.GetParent(d); }
            return false;
        }

        public void FadeClose()
        {
            if (_closing) return; _closing = true; _timer.Stop();
            var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
            anim.Completed += (s, e) => { try { Close(); } catch { } };
            BeginAnimation(OpacityProperty, anim);
        }
        protected override void OnClosed(EventArgs e) { base.OnClosed(e); Closed2?.Invoke(this); }
    }
}
