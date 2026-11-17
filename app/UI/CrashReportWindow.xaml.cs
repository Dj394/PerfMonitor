using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PerfMonitorLive.Metrics;

namespace PerfMonitorLive.UI
{
    /// <summary>Détail d'un arrêt brutal : indices, mini-courbes et tableau de la dernière minute du journal alimentation,
    /// bouton « Copier » (texte pour un forum ou le support). Le journal est relu hors thread UI à l'ouverture.</summary>
    public partial class CrashReportWindow : Window
    {
        readonly CrashEntry _e;
        CrashAnalysis _a;

        public CrashReportWindow(CrashEntry e)
        {
            InitializeComponent();
            SoftwareRender.Apply(this);
            _e = e;
            Header.MouseLeftButtonDown += (s, a) => { if (a.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
            KeyDown += (s, a) => { if (a.Key == Key.Escape) Close(); };
            var c = e.Critical ? Palette.Bad : Palette.Warn;
            Dot.Background = Palette.B(c, 0x33);
            FillHeader();
            TableBox.Text = "Lecture du journal alimentation…";
            Task.Run(() => CrashReport.Analyze(e)).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.IsFaulted) { Paths.Log("CrashReportWindow: " + t.Exception?.GetBaseException().Message); TableBox.Text = "Lecture impossible : " + t.Exception?.GetBaseException().Message; return; }
                _a = t.Result; Fill();
            })));
        }

        void FillHeader()
        {
            TitleText.Text = "Arrêt brutal du " + _e.When.ToString("dd/MM/yyyy") + " vers " + _e.When.ToString("HH:mm:ss");
            var sb = new StringBuilder("Windows : " + _e.source + " · redémarrage à " + _e.BootTime.ToString("HH:mm") + (_e.BootTime.Date != _e.When.Date ? " le " + _e.BootTime.ToString("dd/MM") : ""));
            if (_e.LastLog.HasValue) sb.Append(" · dernière mesure à " + _e.LastLog.Value.ToString("HH:mm:ss"));
            else if (_e.WinTime.HasValue) sb.Append(" · heure d'arrêt notée par Windows");
            SubText.Text = sb.ToString();
        }

        void Fill()
        {
            FillHeader();
            FindingsPanel.Children.Clear();
            foreach (var f in _a.Findings.OrderByDescending(x => x.Level)) FindingsPanel.Children.Add(FindingRow(f));
            if (_a.HasData)
            {
                var sb = new StringBuilder();
                sb.AppendLine(CrashReport.TableHeader());
                for (int i = _a.Rows.Count - 1; i >= 0; i--) sb.AppendLine(CrashReport.TableRow(_a.Rows[i]));   // le plus récent en haut
                TableBox.Text = sb.ToString().TrimEnd();
                BuildCharts();
                var span = (_a.Rows[_a.Rows.Count - 1].Time - _a.Rows[0].Time).TotalSeconds;
                ChartsCaption.Text = "Les " + Math.Round(span + 1) + " dernières secondes avant l'arrêt (pointillé rouge = dernière mesure enregistrée)";
                FileText.Text = string.Join(", ", _a.Files.Select(System.IO.Path.GetFileName));
                FileText.ToolTip = string.Join("\n", _a.Files);
            }
            else
            {
                TableBox.Text = "Aucune ligne du journal alimentation pour la session interrompue.";
                ChartsGrid.Visibility = Visibility.Collapsed; ChartsCaption.Visibility = Visibility.Collapsed;
                OpenBtn.Content = "Ouvrir le dossier data";
            }
            CopyBtn.IsEnabled = true;
        }

        UIElement FindingRow(CrashFinding f)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var col = f.Level >= 2 ? Palette.Bad : f.Level == 1 ? Palette.Warn : Palette.Neutral;
            var dot = new Ellipse { Width = 8, Height = 8, Fill = Palette.B(col), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) };
            var tb = new TextBlock { Text = f.Text, TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            Grid.SetColumn(tb, 1);
            g.Children.Add(dot); g.Children.Add(tb);
            return g;
        }

        // ------------------------------------------------------------------ mini-courbes
        static readonly Color Violet = Color.FromRgb(0xB0, 0x8C, 0xFF);
        void BuildCharts()
        {
            var rows = _a.Rows;
            // tensions en écart au nominal (%) : les trois rails sur une même échelle, bande de tolérance ATX ±5 %
            var rails = new List<(string n, Color c, Func<PowerRow, double?> v, double nom)>();
            void Rail(string n, Color c, Func<PowerRow, double?> v, double nom)
            {
                var vals = rows.Select(v).Where(x => x.HasValue && x > 0).Select(x => x.Value).ToList();
                if (vals.Count > 0 && Math.Abs(vals.OrderBy(x => x).ElementAt(vals.Count / 2) - nom) / nom <= 0.30) rails.Add((n, c, v, nom));
            }
            Rail("12 V", Palette.Neutral, r => r.V12, 12); Rail("5 V", Palette.Warn, r => r.V5, 5); Rail("3,3 V", Palette.Good, r => r.V33, 3.3);
            int slot = 0;
            if (rails.Count > 0)
                AddChart(slot++, "Tensions (écart au nominal, ±5 % = tolérance ATX)",
                    rails.Select(r => (r.n + " " + Last(rows, r.v, "0.00") + " V", r.c, (Func<PowerRow, double?>)(x => r.v(x).HasValue && r.v(x) > 0 ? (r.v(x) / r.nom - 1) * 100 : null))).ToList(), -8, 8, band: 5, unit: "%");
            if (rows.Any(r => r.CpuC.HasValue || r.GpuC.HasValue))
                AddChart(slot++, "Températures (°C)", new List<(string, Color, Func<PowerRow, double?>)> { ("CPU " + Last(rows, r => r.CpuC, "0") + " °C", Palette.Neutral, r => r.CpuC), ("GPU " + Last(rows, r => r.GpuC, "0") + " °C", Palette.Warn, r => r.GpuC) }, double.NaN, double.NaN, unit: "°C");
            if (rows.Any(r => r.CpuW.HasValue || r.GpuW.HasValue))
                AddChart(slot++, "Consommation (W)", new List<(string, Color, Func<PowerRow, double?>)> { ("CPU " + Last(rows, r => r.CpuW, "0") + " W", Palette.Neutral, r => r.CpuW), ("GPU " + Last(rows, r => r.GpuW, "0") + " W", Palette.Warn, r => r.GpuW) }, 0, double.NaN, unit: "W");
            if (rows.Any(r => r.Fps > 0))
                AddChart(slot++, "Images par seconde", new List<(string, Color, Func<PowerRow, double?>)> { ("FPS " + Last(rows, r => r.Fps, "0"), Violet, r => r.Fps) }, 0, double.NaN, unit: "");
            if (slot < 4 && rows.Any(r => r.CpuPct.HasValue))
                AddChart(slot++, "Charge CPU (%)", new List<(string, Color, Func<PowerRow, double?>)> { ("CPU " + Last(rows, r => r.CpuPct, "0") + " %", Palette.Neutral, r => r.CpuPct) }, 0, 100, unit: "%");
            if (slot == 0) { ChartsGrid.Visibility = Visibility.Collapsed; ChartsCaption.Visibility = Visibility.Collapsed; }
        }

        static string Last(List<PowerRow> rows, Func<PowerRow, double?> v, string fmt)
        {
            for (int i = rows.Count - 1; i >= 0; i--) { var x = v(rows[i]); if (x.HasValue) return x.Value.ToString(fmt, System.Globalization.CultureInfo.GetCultureInfo("fr-FR")); }
            return "—";
        }

        void AddChart(int slot, string title, List<(string label, Color color, Func<PowerRow, double?> v)> series, double yMin, double yMax, double band = 0, string unit = "")
        {
            if (slot >= 4) return;
            var box = new Border { Style = (Style)FindResource("ChartBox") };
            var sp = new StackPanel();
            var t = new TextBlock { Text = title, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
            t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            sp.Children.Add(t);
            var legend = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
            foreach (var s in series)
            {
                var l = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
                l.Children.Add(new Rectangle { Width = 10, Height = 3, Fill = Palette.B(s.color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
                var lt = new TextBlock { Text = s.label, FontSize = 12 };
                lt.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
                l.Children.Add(lt);
                legend.Children.Add(l);
            }
            sp.Children.Add(legend);
            var canvas = new Canvas { Height = 64, ClipToBounds = true };
            sp.Children.Add(canvas);
            box.Child = sp;
            Grid.SetColumn(box, slot % 2); Grid.SetRow(box, slot / 2);
            ChartsGrid.Children.Add(box);
            var rows = _a.Rows;
            Action<Canvas> draw = cv => Draw(cv, rows, series, yMin, yMax, band, unit);
            canvas.SizeChanged += (s, a) => draw(canvas);
        }

        static void Draw(Canvas cv, List<PowerRow> rows, List<(string label, Color color, Func<PowerRow, double?> v)> series, double yMin, double yMax, double band, string unit)
        {
            cv.Children.Clear();
            double w = cv.ActualWidth, h = cv.ActualHeight;
            if (w < 10 || h < 10 || rows.Count == 0) return;
            var vals = series.SelectMany(s => rows.Select(s.v)).Where(x => x.HasValue).Select(x => x.Value).ToList();
            if (vals.Count == 0) return;
            double lo = double.IsNaN(yMin) ? vals.Min() : Math.Min(yMin, vals.Min()), hi = double.IsNaN(yMax) ? vals.Max() : Math.Max(yMax, vals.Max());
            if (hi - lo < 1e-6) { hi += 1; lo -= 1; }
            double pad = (hi - lo) * 0.08; if (double.IsNaN(yMin)) lo -= pad; if (double.IsNaN(yMax)) hi += pad;
            DateTime t0 = rows[0].Time, t1 = rows[rows.Count - 1].Time;
            double span = Math.Max(1, (t1 - t0).TotalSeconds);
            double X(DateTime t) => rows.Count == 1 ? w - 2 : (t - t0).TotalSeconds / span * (w - 4) + 1;
            double Y(double v) => h - 1 - (v - lo) / (hi - lo) * (h - 2);
            var grid = (Brush)cv.TryFindResource("GridBrush") ?? Brushes.Gray;
            var muted = (Brush)cv.TryFindResource("MutedBrush") ?? Brushes.Gray;
            if (band > 0)
            {
                var r = new Rectangle { Width = w, Height = Math.Max(0, Y(-band) - Y(band)), Fill = Palette.B(Palette.Good, 0x1E) };
                Canvas.SetTop(r, Y(band)); cv.Children.Add(r);
                cv.Children.Add(new Line { X1 = 0, X2 = w, Y1 = Y(0), Y2 = Y(0), Stroke = grid, StrokeThickness = 1 });
            }
            foreach (var s in series)
            {
                var pl = new Polyline { Stroke = Palette.B(s.color), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
                foreach (var r in rows)
                {
                    var v = s.v(r);
                    if (!v.HasValue) { if (pl.Points.Count > 0) { cv.Children.Add(pl); pl = new Polyline { Stroke = Palette.B(s.color), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round }; } continue; }
                    pl.Points.Add(new Point(X(r.Time), Y(v.Value)));
                }
                if (pl.Points.Count == 1) pl.Points.Add(new Point(pl.Points[0].X + 1, pl.Points[0].Y));
                if (pl.Points.Count > 0) cv.Children.Add(pl);
            }
            // repère de fin : la dernière mesure, instant approximatif de l'arrêt
            cv.Children.Add(new Line { X1 = w - 1, X2 = w - 1, Y1 = 0, Y2 = h, Stroke = Palette.B(Palette.Bad, 0xAA), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2 } });
            var hiT = new TextBlock { Text = Fmt(hi, unit), FontSize = 10, Foreground = muted };
            var loT = new TextBlock { Text = Fmt(lo, unit), FontSize = 10, Foreground = muted };
            Canvas.SetLeft(hiT, 2); Canvas.SetTop(hiT, 0); Canvas.SetLeft(loT, 2); Canvas.SetTop(loT, h - 14);
            cv.Children.Add(hiT); cv.Children.Add(loT);
        }
        static string Fmt(double v, string unit) => (Math.Abs(v) >= 100 ? Math.Round(v).ToString("0") : v.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))) + (unit.Length > 0 ? " " + unit : "");

        // ------------------------------------------------------------------ boutons
        void Copy_Click(object s, RoutedEventArgs e)
        {
            if (_a == null) return;
            string machine = null;
            try
            {
                var m = Inventory.Current;
                if (m != null && m.Cpu != null && m.Cpu.Name != "?")
                    machine = m.Cpu.Short + (m.MainGpu != null ? " · " + m.MainGpu.Short : "") + " · " + m.RamText + (string.IsNullOrEmpty(m.Board) ? "" : " · " + ((m.BoardMaker ?? "") + " " + m.Board).Trim()) + " · " + m.KindText;
            }
            catch { }
            var text = CrashReport.BuildText(_e, _a, machine);
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text); CopyBtn.Content = "Copié ✓"; break; }
                catch (Exception ex) { if (i == 4) { Paths.Log("presse-papiers: " + ex.Message); CopyBtn.Content = "Presse-papiers occupé"; } else System.Threading.Thread.Sleep(50); }
            }
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (o, a) => { t.Stop(); CopyBtn.Content = "Copier"; };
            t.Start();
        }

        void Open_Click(object s, RoutedEventArgs e)
        {
            try
            {
                var f = _a?.Files.FirstOrDefault();
                if (f != null && System.IO.File.Exists(f)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + f + "\"") { UseShellExecute = true });
                else Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Paths.DataDir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Paths.Log("ouvrir journal: " + ex.Message); }
        }

        void Close_Click(object s, RoutedEventArgs e) => Close();
    }
}
