using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PerfMonitorLive.Alerts;
using PerfMonitorLive.Metrics;

namespace PerfMonitorLive.UI
{
    /// <summary>Onglet Historique : courbes agrégées, marqueurs, comparaison, démarrages, sessions, alertes.</summary>
    public partial class HistoryView : UserControl
    {
        public class MetricDef { public string Key, Title, Unit; public double FixedMax; }
        readonly List<MetricDef> _defs = new List<MetricDef>();
        readonly HashSet<string> _picked = new HashSet<string> { "cpu", "memPct", "temp", "gpuTemp" };
        List<Sample> _samples = new List<Sample>();
        List<Note> _notes = new List<Note>();
        DateTime _from, _to, _viewFrom, _viewTo;
        double _hours = 24;
        bool _loading;
        readonly List<HistoryChart> _charts = new List<HistoryChart>();
        public Func<string> ReportOpener { get; set; }
        public Action<string> NoteAdded { get; set; }

        public HistoryView() { InitializeComponent(); IsVisibleChanged += (s, e) => { if (IsVisible && _samples.Count == 0) _ = LoadAsync(); }; }

        /// <summary>Déclare les métriques disponibles (mêmes clés que les cartes).</summary>
        public void SetMetrics(IEnumerable<MetricDef> defs)
        {
            foreach (var d in defs) if (!_defs.Any(x => x.Key == d.Key)) _defs.Add(d);
            MetricPicks.Children.Clear();
            foreach (var d in _defs)
            {
                var cb = new CheckBox { Content = d.Title, IsChecked = _picked.Contains(d.Key), Margin = new Thickness(0, 0, 12, 4), Tag = d.Key, Foreground = (Brush)FindResource("FgBrush") };
                cb.Checked += (s, e) => { _picked.Add(d.Key); BuildCharts(); };
                cb.Unchecked += (s, e) => { _picked.Remove(d.Key); BuildCharts(); };
                MetricPicks.Children.Add(cb);
            }
        }

        void Range_Click(object s, RoutedEventArgs e) { _hours = double.Parse((string)((Button)s).Tag, CultureInfo.InvariantCulture); _ = LoadAsync(); }
        void Refresh_Click(object s, RoutedEventArgs e) => _ = LoadAsync();
        void Report_Click(object s, RoutedEventArgs e) => ReportOpener?.Invoke();
        void Mark_Click(object s, RoutedEventArgs e)
        {
            var text = PromptWindow.Ask(Window.GetWindow(this), "Marquer un réglage", "Décris le réglage que tu viens de faire (ex : « Activé DOCP 3600 », « Curve Optimizer -15 »). Un trait vertical apparaîtra sur les graphiques.");
            if (string.IsNullOrWhiteSpace(text)) return;
            HistoryReader.AddNote(text.Trim()); NoteAdded?.Invoke(text.Trim());
            _ = LoadAsync();
        }

        public async Task LoadAsync()
        {
            if (_loading) return; _loading = true;
            try
            {
                _to = DateTime.Now; _from = _to.AddHours(-_hours);
                RangeText.Text = "chargement…";
                var from = _from; var to = _to;
                var samples = await Task.Run(() => HistoryReader.Load(from, to));
                _samples = samples; _notes = HistoryReader.LoadNotes();
                _viewFrom = _from; _viewTo = _to;
                RangeText.Text = _samples.Count + " échantillons · " + _from.ToString("dd/MM HH:mm") + " → " + _to.ToString("dd/MM HH:mm");
                BuildCharts(); FillMarkers(); FillBoots(); FillSessions(); FillAlerts();
            }
            finally { _loading = false; }
        }

        void BuildCharts()
        {
            Charts.Children.Clear(); _charts.Clear();
            foreach (var d in _defs.Where(x => _picked.Contains(x.Key)))
            {
                var c = new HistoryChart { Def = d, Height = 170, Margin = new Thickness(0, 0, 0, 10) };
                c.SetData(_samples, _notes, _viewFrom, _viewTo);
                c.ViewChanged += (f, t) => { _viewFrom = f; _viewTo = t; foreach (var o in _charts) if (o != c) o.SetView(f, t); };
                _charts.Add(c); Charts.Children.Add(c);
            }
        }

        // ---------------- comparaison
        void FillMarkers()
        {
            MarkerCombo.Items.Clear();
            foreach (var n in _notes.Where(n => n.Time >= _from.AddHours(-2) && n.Time <= _to).OrderByDescending(n => n.Time)) MarkerCombo.Items.Add(n.Time.ToString("dd/MM HH:mm") + " — " + n.text);
            if (MarkerCombo.Items.Count > 0) MarkerCombo.SelectedIndex = 0;
            CompareHint.Visibility = MarkerCombo.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        public class CmpRow { public string Name { get; set; } public string Before { get; set; } public string After { get; set; } public string Delta { get; set; } public Brush DeltaBrush { get; set; } }
        void Compare_Changed(object s, RoutedEventArgs e)
        {
            var rows = new List<CmpRow>();
            if (MarkerCombo.SelectedIndex >= 0)
            {
                var note = _notes.Where(n => n.Time >= _from.AddHours(-2) && n.Time <= _to).OrderByDescending(n => n.Time).ElementAt(MarkerCombo.SelectedIndex);
                int.TryParse(WindowBox.Text, out int win); if (win <= 0) win = 30;
                var before = _samples.Where(x => x.Time >= note.Time.AddMinutes(-win) && x.Time < note.Time).ToList();
                var after = _samples.Where(x => x.Time > note.Time && x.Time <= note.Time.AddMinutes(win)).ToList();
                foreach (var d in _defs.Where(x => _picked.Contains(x.Key)))
                {
                    var b = HistoryReader.Stats(before, d.Key); var a = HistoryReader.Stats(after, d.Key);
                    if (b.n == 0 || a.n == 0) continue;
                    double delta = b.avg == 0 ? 0 : (a.avg - b.avg) / Math.Abs(b.avg) * 100;
                    bool lowerIsBetter = d.Key != "cpuMHz" && d.Key != "gpuMHz" && !d.Key.StartsWith("fan");
                    rows.Add(new CmpRow
                    {
                        Name = d.Title,
                        Before = "moy " + F(b.avg, d.Unit) + " · p95 " + F(b.p95, d.Unit) + " · max " + F(b.max, d.Unit),
                        After = "moy " + F(a.avg, d.Unit) + " · p95 " + F(a.p95, d.Unit) + " · max " + F(a.max, d.Unit),
                        Delta = (delta >= 0 ? "+" : "") + delta.ToString("0") + " % (moyenne)",
                        DeltaBrush = Math.Abs(delta) < 3 ? Palette.B(Palette.Muted) : (delta < 0) == lowerIsBetter ? Palette.B(Palette.Good) : Palette.B(Palette.Bad)
                    });
                }
            }
            CompareRows.ItemsSource = rows;
        }
        static string F(double v, string u) => double.IsNaN(v) ? "–" : (v < 10 ? v.ToString("0.0") : v.ToString("0")) + " " + u;

        // ---------------- démarrages
        public class BootRow { public string When { get; set; } public string Dur { get; set; } public string Slow { get; set; } public Brush Brush { get; set; } }
        void FillBoots()
        {
            var boots = BootReport.Load();
            var recent = boots.Where(b => b.Time >= DateTime.Now.AddDays(-30)).ToList();
            if (recent.Count == 0) { BootSummary.Text = "Aucun démarrage enregistré (journal « Diagnostics-Performance » vide ou inaccessible)."; BootRows.ItemsSource = null; BootBars.SetValues(new List<(string, double, Color)>()); return; }
            var med = recent.Select(b => b.bootMs).OrderBy(x => x).ElementAt(recent.Count / 2) / 1000;
            var last = recent.Last();
            BootSummary.Text = recent.Count + " démarrages sur 30 jours · médiane " + med.ToString("0") + " s · dernier " + (last.bootMs / 1000).ToString("0") + " s (" + last.Time.ToString("dd/MM HH:mm") + ")";
            BootBars.SetValues(recent.TakeLast(30).Select(b => (b.Time.ToString("dd/MM"), b.bootMs / 1000, b.bootMs / 1000 > 60 ? Palette.Bad : b.bootMs / 1000 > 40 ? Palette.Warn : Palette.Good)).ToList());
            BootRows.ItemsSource = recent.OrderByDescending(b => b.Time).Take(8).Select(b => new BootRow
            {
                When = b.Time.ToString("ddd dd/MM HH:mm"),
                Dur = (b.bootMs / 1000).ToString("0") + " s",
                Brush = Palette.B(b.bootMs / 1000 > 60 ? Palette.Bad : b.bootMs / 1000 > 40 ? Palette.Warn : Palette.Good),
                Slow = b.slow.Count == 0 ? "—" : string.Join(", ", b.slow.Take(4).Select(x => x.n + " " + (x.ms / 1000).ToString("0.#") + " s"))
            }).ToList();
        }
        void FillSessions()
        {
            var sess = DailyDigest.LoadSessions(_from, _to).OrderByDescending(x => x.Time).ToList();
            SessionRows.ItemsSource = sess.Select(x => x.Time.ToString("dd/MM HH:mm") + " · " + x.game + " · " + Math.Round(x.min) + " min · CPU " + x.cpuAvg + " % moy / " + x.cpuMax + " % max · CPU " + x.tempMax + " °C · GPU " + x.gpuMax + " °C · RAM " + x.memMax + " %").ToList();
            SessionsEmpty.Visibility = sess.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        void FillAlerts()
        {
            var al = HistoryReader.LoadAlerts(_from, _to).OrderByDescending(a => a.Time).Take(30).ToList();
            AlertRows.ItemsSource = al.Select(a => a.Time.ToString("dd/MM HH:mm") + " · " + (a.sev == "Critical" ? "🚨" : a.sev == "Ok" ? "✅" : a.sev == "Info" ? "💬" : "⚠️") + " " + a.title).ToList();
            AlertsEmpty.Visibility = al.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Graphique d'une métrique : aire + moyenne + max, marqueurs, zoom/pan, curseur.</summary>
    public class HistoryChart : FrameworkElement
    {
        public HistoryView.MetricDef Def;
        List<Bucket> _b = new List<Bucket>(); List<Note> _notes = new List<Note>(); List<Sample> _samples = new List<Sample>();
        DateTime _f, _t; Point? _hover; bool _drag; Point _dragStart; DateTime _dragF, _dragT;
        public event Action<DateTime, DateTime> ViewChanged;
        static readonly Typeface Face = new Typeface("Segoe UI");
        const double L = 52, R = 12, T = 26, B = 22;

        public HistoryChart() { ClipToBounds = true; Cursor = Cursors.Cross; }
        public void SetData(List<Sample> samples, List<Note> notes, DateTime f, DateTime t) { _samples = samples; _notes = notes; SetView(f, t); }
        public void SetView(DateTime f, DateTime t)
        {
            _f = f; _t = t;
            int step = HistoryReader.StepFor(t - f);
            _b = HistoryReader.Aggregate(_samples.Where(s => s.Time >= f && s.Time <= t).ToList(), Def.Key, step);
            InvalidateVisual();
        }

        Brush Res(string k) => (Brush)(TryFindResource(k) ?? Brushes.Gray);
        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight; if (W < 60 || H < 40) return;
            dc.DrawRoundedRectangle(Res("PanelBrush"), new Pen(Res("GridBrush"), 1), new Rect(0.5, 0.5, W - 1, H - 1), 12, 12);
            double w = W - L - R, h = H - T - B;
            var fg = Res("FgBrush"); var muted = Res("MutedBrush");
            dc.DrawText(Text(Def.Title, 12.5, fg, FontWeights.SemiBold), new Point(14, 6));
            if (_b.Count == 0) { dc.DrawText(Text("Aucune donnée sur la plage", 12, muted), new Point(L, T + h / 2)); return; }
            double max = Def.FixedMax > 0 ? Def.FixedMax : Math.Max(1, _b.Max(x => x.Max) * 1.1);
            double span = (_t - _f).TotalSeconds; if (span <= 0) span = 1;
            double X(DateTime d) => L + (d - _f).TotalSeconds / span * w;
            double Y(double v) => T + h - Math.Min(v, max) / max * h;
            var gridPen = new Pen(Res("GridBrush"), 1);
            for (int i = 0; i <= 4; i++)
            {
                double y = T + h * i / 4; dc.DrawLine(gridPen, new Point(L, y), new Point(W - R, y));
                var ft = Text(FmtV(max * (1 - i / 4.0), Def.Unit), 10.5, muted); dc.DrawText(ft, new Point(L - 6 - ft.Width, y - 7));
            }
            int nT = Math.Max(2, (int)(w / 120));
            for (int i = 0; i <= nT; i++)
            {
                var d = _f.AddSeconds(span * i / nT);
                var ft = Text(span > 86400 * 2 ? d.ToString("dd/MM HH:mm") : d.ToString("HH:mm"), 10.5, muted);
                dc.DrawText(ft, new Point(Math.Min(W - R - ft.Width, Math.Max(L, X(d) - ft.Width / 2)), H - B + 4));
            }
            // aire moyenne + ligne max
            var accent = Palette.Neutral;
            var area = new StreamGeometry(); var line = new StreamGeometry(); var maxLine = new StreamGeometry();
            using (var ga = area.Open()) using (var gl = line.Open()) using (var gm = maxLine.Open())
            {
                bool first = true;
                for (int i = 0; i < _b.Count; i++)
                {
                    var p = new Point(X(_b[i].T), Y(_b[i].Avg)); var pm = new Point(p.X, Y(_b[i].Max));
                    if (first) { ga.BeginFigure(new Point(p.X, T + h), true, true); ga.LineTo(p, false, false); gl.BeginFigure(p, false, false); gm.BeginFigure(pm, false, false); first = false; }
                    else
                    {
                        // trou de données > 3 pas : on coupe
                        if ((_b[i].T - _b[i - 1].T).TotalSeconds > HistoryReader.StepFor(_t - _f) * 4) { ga.LineTo(new Point(X(_b[i - 1].T), T + h), false, false); ga.LineTo(new Point(p.X, T + h), false, false); gl.BeginFigure(p, false, false); gm.BeginFigure(pm, false, false); }
                        else { gl.LineTo(p, true, true); gm.LineTo(pm, true, true); }
                        ga.LineTo(p, false, false);
                    }
                }
                ga.LineTo(new Point(X(_b[_b.Count - 1].T), T + h), false, false);
            }
            dc.PushClip(new RectangleGeometry(new Rect(L, T, w, h)));
            dc.DrawGeometry(Palette.B(accent, 0x2A), null, area);
            dc.DrawGeometry(null, new Pen(Palette.B(Palette.Bad, 0x70), 1), maxLine);
            dc.DrawGeometry(null, new Pen(Palette.B(accent), 1.6) { LineJoin = PenLineJoin.Round }, line);
            // marqueurs
            var mpen = new Pen(Palette.B(Palette.Warn), 1) { DashStyle = DashStyles.Dash };
            foreach (var n in _notes.Where(n => n.Time >= _f && n.Time <= _t))
            {
                double x = X(n.Time); dc.DrawLine(mpen, new Point(x, T), new Point(x, T + h));
                dc.DrawText(Text(n.text.Length > 24 ? n.text.Substring(0, 23) + "…" : n.text, 10, Palette.B(Palette.Warn)), new Point(x + 3, T + 2));
            }
            // curseur
            if (_hover.HasValue && _hover.Value.X >= L && _hover.Value.X <= W - R)
            {
                var d = _f.AddSeconds((_hover.Value.X - L) / w * span);
                var nb = _b.OrderBy(x => Math.Abs((x.T - d).TotalSeconds)).First();
                double x = X(nb.T);
                dc.DrawLine(new Pen(muted, 1) { DashStyle = DashStyles.Dot }, new Point(x, T), new Point(x, T + h));
                dc.DrawEllipse(Palette.B(accent), null, new Point(x, Y(nb.Avg)), 3.5, 3.5);
                var txt = Text(nb.T.ToString("dd/MM HH:mm") + "  moy " + FmtV(nb.Avg, Def.Unit) + "  max " + FmtV(nb.Max, Def.Unit), 11, fg, FontWeights.SemiBold);
                double bx = Math.Min(W - R - txt.Width - 12, x + 8);
                dc.DrawRoundedRectangle(Res("Panel2Brush"), null, new Rect(bx, T + 4, txt.Width + 12, txt.Height + 6), 6, 6);
                dc.DrawText(txt, new Point(bx + 6, T + 7));
            }
            dc.Pop();
            var lg = Text("moyenne", 10, Palette.B(accent)); dc.DrawText(lg, new Point(W - R - 120, 8));
            dc.DrawText(Text("max", 10, Palette.B(Palette.Bad, 0xB0)), new Point(W - R - 40, 8));
        }
        static string FmtV(double v, string u) => u == "%" ? Math.Round(v) + " %" : (v < 10 ? v.ToString("0.0") : v.ToString("0")) + " " + u;
        FormattedText Text(string s, double size, Brush b, FontWeight? w = null) => new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, w ?? FontWeights.Normal, FontStretches.Normal), size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (_drag && e.LeftButton == MouseButtonState.Pressed)
            {
                double w = ActualWidth - L - R; var span = (_dragT - _dragF).TotalSeconds;
                double dx = (p.X - _dragStart.X) / w * span;
                _f = _dragF.AddSeconds(-dx); _t = _dragT.AddSeconds(-dx);
                SetView(_f, _t); ViewChanged?.Invoke(_f, _t);
            }
            _hover = p; InvalidateVisual();
        }
        protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; _drag = false; InvalidateVisual(); }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { _drag = true; _dragStart = e.GetPosition(this); _dragF = _f; _dragT = _t; CaptureMouse(); }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { _drag = false; ReleaseMouseCapture(); }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var p = e.GetPosition(this); double w = ActualWidth - L - R; var span = (_t - _f).TotalSeconds;
            double frac = Math.Max(0, Math.Min(1, (p.X - L) / w));
            double factor = e.Delta > 0 ? 0.8 : 1.25;
            double ns = Math.Max(300, span * factor);
            var pivot = _f.AddSeconds(span * frac);
            _f = pivot.AddSeconds(-ns * frac); _t = pivot.AddSeconds(ns * (1 - frac));
            SetView(_f, _t); ViewChanged?.Invoke(_f, _t); e.Handled = true;
        }
    }

    /// <summary>Petit graphique en barres (temps de démarrage par jour).</summary>
    public class BarStrip : FrameworkElement
    {
        List<(string label, double v, Color c)> _v = new List<(string, double, Color)>();
        public void SetValues(List<(string, double, Color)> v) { _v = v; InvalidateVisual(); }
        protected override void OnRender(DrawingContext dc)
        {
            if (_v.Count == 0 || ActualWidth < 20) return;
            double max = Math.Max(1, _v.Max(x => x.v)); double bw = Math.Min(40, (ActualWidth - 10) / _v.Count); double h = ActualHeight - 18;
            var muted = (Brush)(TryFindResource("MutedBrush") ?? Brushes.Gray);
            for (int i = 0; i < _v.Count; i++)
            {
                double bh = _v[i].v / max * h; double x = i * bw + 4;
                dc.DrawRoundedRectangle(Palette.B(_v[i].c), null, new Rect(x, h - bh, Math.Max(2, bw - 6), bh), 3, 3);
                var t = new FormattedText(_v[i].v.ToString("0"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9.5, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(t, new Point(x + (bw - 6 - t.Width) / 2, h - bh - 13));
                if (bw >= 30 || i % 3 == 0) { var l = new FormattedText(_v[i].label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip); dc.DrawText(l, new Point(x, h + 3)); }
            }
        }
    }

    /// <summary>Boîte de saisie simple, dans le style de l'appli.</summary>
    public class PromptWindow : Window
    {
        public static string Ask(Window owner, string title, string label)
        {
            var w = new PromptWindow { Title = title, Owner = owner, Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = (Brush)Application.Current.FindResource("PanelBrush"), Foreground = (Brush)Application.Current.FindResource("FgBrush"), FontFamily = new FontFamily("Segoe UI") };
            var tb = new TextBox { Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(6, 4, 6, 4), Background = (Brush)Application.Current.FindResource("InputBgBrush"), Foreground = w.Foreground, BorderBrush = (Brush)Application.Current.FindResource("InputBorderBrush"), CaretBrush = w.Foreground };
            var ok = new Button { Content = "OK", Padding = new Thickness(16, 5, 16, 5), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Annuler", Padding = new Thickness(16, 5, 16, 5), IsCancel = true };
            string result = null;
            ok.Click += (s, e) => { result = tb.Text; w.DialogResult = true; };
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(tb);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; btns.Children.Add(ok); btns.Children.Add(cancel);
            panel.Children.Add(btns);
            w.Content = panel; tb.Focus();
            return w.ShowDialog() == true ? result : null;
        }
    }
}
