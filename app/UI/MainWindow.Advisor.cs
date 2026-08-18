using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PerfMonitorLive.Alerts;

namespace PerfMonitorLive.UI
{
    /// <summary>Le conseiller volant : mascotte + bulle, se déplace vers la carte concernée.</summary>
    public partial class MainWindow
    {
        Advisor _advisor;
        readonly DispatcherTimer _advTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        DateTime _advLastTip = DateTime.MinValue, _advBubbleShown = DateTime.MinValue;
        Tip _advCurrent;
        readonly Random _advRnd = new Random();
        static readonly TimeSpan GeneralEvery = TimeSpan.FromMinutes(3), BubbleAutoHide = TimeSpan.FromSeconds(45);

        void InitAdvisor()
        {
            _advisor = new Advisor(_app.Settings.AdvisorDismissed);
            _advTimer.Tick += (s, e) => AdvisorTick();
            _advTimer.Start();
            // flottement permanent de la mascotte
            var bob = new DoubleAnimation(-4, 4, TimeSpan.FromSeconds(1.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            MascotBob.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, bob);
            Loaded += (s, e) => { ApplyAdvisorVisibility(); MoveMascot(RandomSpot(), false); };
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase)))
            {   // démo : un conseil 6 s après l'ouverture
                var once = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                once.Tick += (s, e) => { once.Stop(); Mascot_Click(null, null); }; once.Start();
            }
            _svm.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(SettingsVm.ShowAdvisor) || e.PropertyName == null) ApplyAdvisorVisibility(); };
        }

        void ApplyAdvisorVisibility()
        {
            bool on = _app.Settings.ShowAdvisor;
            Mascot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) Bubble.Visibility = Visibility.Collapsed;
        }

        void AdvisorOnSample(Metrics.Sample s) => _advisor?.OnSample(s);
        public void SetLastBoot(Metrics.BootEntry b) { if (_advisor != null) _advisor.LastBoot = b; }
        public void AdvisorAsk() => Mascot_Click(null, null);
        /// <summary>Après modification des paramètres (thème, compact, conseiller, profil).</summary>
        public void SettingsChangedHook()
        {
            ApplyAdvisorVisibility(); UpdateProfilePill();
            Raise(nameof(CardMinHeight)); Raise(nameof(ValueFontSize)); Relayout();
        }

        /// <summary>Après un changement de taille (plein écran…) : ramène la mascotte près de la carte du conseil affiché.</summary>
        void AdvisorRelayout()
        {
            if (_advCurrent == null || Bubble.Visibility != Visibility.Visible) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var target = _advCurrent.TargetKey != null ? CardRect(_advCurrent.TargetKey) : (Rect?)null;
                var spot = target.HasValue ? new Point(target.Value.Right - 70, target.Value.Top + 6) : new Point(Canvas.GetLeft(Mascot), Canvas.GetTop(Mascot));
                MoveMascot(spot, true, () => PlaceBubble(spot)); PlaceBubble(spot);
            }), DispatcherPriority.Loaded);
        }

        void AdvisorTick()
        {
            if (!_app.Settings.ShowAdvisor || !IsVisible || WindowState == WindowState.Minimized) return;
            if (Bubble.Visibility == Visibility.Visible && DateTime.Now - _advBubbleShown > BubbleAutoHide && (_advCurrent == null || !_advCurrent.Triggered))
                HideBubble();
            var trig = _advisor.Triggered();
            if (trig.Count > 0)
            {
                var t = trig[0];
                if (_advCurrent == null || _advCurrent.Id != t.Id || Bubble.Visibility != Visibility.Visible) ShowTip(t);
                return;
            }
            if (Bubble.Visibility != Visibility.Visible && DateTime.Now - _advLastTip > GeneralEvery)
            {
                var g = _advisor.NextGeneral();
                if (g != null) ShowTip(g);
                else if (_advRnd.Next(4) == 0) MoveMascot(RandomSpot(), true); // rien à dire : il se promène un peu
            }
            else if (Bubble.Visibility != Visibility.Visible && _advRnd.Next(6) == 0) MoveMascot(RandomSpot(), true);
        }

        void ShowTip(Tip t)
        {
            _advCurrent = t; _advisor.MarkShown(t); _advLastTip = _advBubbleShown = DateTime.Now;
            BubbleTitle.Text = t.Title; BubbleText.Text = t.Text;
            BubbleKind.Text = t.Triggered || t.Priority >= 5 ? "⚠️ observé maintenant" : "💡 astuce";
            Bubble.BorderBrush = t.Priority >= 8 ? Palette.B(Palette.Bad, 0xCC) : t.Priority >= 5 ? Palette.B(Palette.Warn, 0xCC) : Palette.B(Palette.Neutral, 0xAA);
            var target = t.TargetKey != null ? CardRect(t.TargetKey) : (Rect?)null;
            var spot = target.HasValue ? new Point(target.Value.Right - 70, target.Value.Top + 6) : RandomSpot();
            Bubble.Opacity = 0; Bubble.Visibility = Visibility.Visible;
            Bubble.UpdateLayout(); PlaceBubble(spot);
            MoveMascot(spot, true, () => PlaceBubble(spot));
            Bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)) { BeginTime = TimeSpan.FromMilliseconds(700) });
            // les yeux regardent vers la bulle
            LookAt(spot);
        }

        void HideBubble()
        {
            var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
            a.Completed += (s, e) => { Bubble.Visibility = Visibility.Collapsed; Bubble.BeginAnimation(OpacityProperty, null); };
            Bubble.BeginAnimation(OpacityProperty, a);
        }

        /// <summary>Rectangle d'une carte (clé) dans le repère de l'overlay ; null si non visible.</summary>
        Rect? CardRect(string key)
        {
            if (!_byKey.TryGetValue(key, out var vm)) return null;
            var cont = Cards.ItemContainerGenerator.ContainerFromItem(vm) as FrameworkElement;
            if (cont == null || !cont.IsVisible) return null;
            try
            {
                var tl = cont.TransformToVisual(AdvisorLayer).Transform(new Point(0, 0));
                var r = new Rect(tl, new Size(cont.ActualWidth, cont.ActualHeight));
                var view = new Rect(0, 0, AdvisorLayer.ActualWidth, AdvisorLayer.ActualHeight);
                if (!r.IntersectsWith(view)) { cont.BringIntoView(); return null; }
                return r;
            }
            catch { return null; }
        }

        Point RandomSpot()
        {
            double w = Math.Max(120, AdvisorLayer.ActualWidth), h = Math.Max(120, AdvisorLayer.ActualHeight);
            return new Point(20 + _advRnd.NextDouble() * (w - 100), 20 + _advRnd.NextDouble() * (h - 100));
        }

        void MoveMascot(Point to, bool animate, Action done = null)
        {
            double w = Math.Max(120, AdvisorLayer.ActualWidth), h = Math.Max(120, AdvisorLayer.ActualHeight);
            to.X = Math.Max(4, Math.Min(w - 62, to.X)); to.Y = Math.Max(4, Math.Min(h - 62, to.Y));
            if (!animate) { Canvas.SetLeft(Mascot, to.X); Canvas.SetTop(Mascot, to.Y); done?.Invoke(); return; }
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var dur = TimeSpan.FromMilliseconds(900);
            var ax = new DoubleAnimation(to.X, dur) { EasingFunction = ease };
            var ay = new DoubleAnimation(to.Y, dur) { EasingFunction = ease };
            ax.Completed += (s, e) =>
            {
                Canvas.SetLeft(Mascot, to.X); Canvas.SetTop(Mascot, to.Y);
                Mascot.BeginAnimation(Canvas.LeftProperty, null); Mascot.BeginAnimation(Canvas.TopProperty, null);
                done?.Invoke();
            };
            Mascot.BeginAnimation(Canvas.LeftProperty, ax); Mascot.BeginAnimation(Canvas.TopProperty, ay);
        }

        void PlaceBubble(Point mascot)
        {
            double w = AdvisorLayer.ActualWidth, h = AdvisorLayer.ActualHeight;
            Bubble.Measure(new Size(330, double.PositiveInfinity));
            double bw = 330, bh = Math.Max(120, Bubble.DesiredSize.Height);
            // sous la mascotte (sur la zone de courbe de la carte, laisse le titre et la valeur lisibles), sinon au-dessus
            double x = mascot.X + 58 - bw; if (x < 8) x = 8; if (x + bw > w - 8) x = Math.Max(8, w - bw - 8);
            double y = mascot.Y + 66; if (y + bh > h - 8) y = mascot.Y - bh - 10; if (y < 8) y = Math.Max(8, h - bh - 8);
            Canvas.SetLeft(Bubble, x); Canvas.SetTop(Bubble, y);
        }

        void LookAt(Point p)
        {
            double dx = Bubble.Visibility == Visibility.Visible && Canvas.GetLeft(Bubble) < p.X ? -2 : 2;
            PupilL.Margin = new Thickness(17 + dx, 21, 0, 0); PupilR.Margin = new Thickness(36 + dx, 21, 0, 0);
        }

        // --- interactions
        void Mascot_Click(object s, MouseButtonEventArgs e)
        {
            var trig = _advisor.Triggered();
            var t = trig.FirstOrDefault() ?? _advisor.NextGeneral();
            if (t != null) ShowTip(t);
        }
        void AdvNext_Click(object s, RoutedEventArgs e)
        {
            var trig = _advisor.Triggered().Where(t => _advCurrent == null || t.Id != _advCurrent.Id).ToList();
            var t = trig.FirstOrDefault() ?? _advisor.NextGeneral();
            if (t != null) ShowTip(t); else HideBubble();
        }
        void AdvOk_Click(object s, RoutedEventArgs e) => HideBubble();
        void AdvDismiss_Click(object s, RoutedEventArgs e)
        {
            if (_advCurrent != null)
            {
                _advisor.Dismiss(_advCurrent.Id);
                if (!_app.Settings.AdvisorDismissed.Contains(_advCurrent.Id)) { _app.Settings.AdvisorDismissed.Add(_advCurrent.Id); _app.Settings.Save(); }
            }
            HideBubble();
        }
        void AdvHide_Click(object s, RoutedEventArgs e) { _svm.ShowAdvisor = false; }
        void AdvReset_Click(object s, RoutedEventArgs e)
        {
            _app.Settings.AdvisorDismissed.Clear(); _app.Settings.Save();
            _advisor = new Advisor(_app.Settings.AdvisorDismissed);
            _svm.ShowAdvisor = true;
        }
    }
}
