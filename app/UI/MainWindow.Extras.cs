using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PerfMonitorLive.Alerts;
using PerfMonitorLive.Metrics;

namespace PerfMonitorLive.UI
{
    /// <summary>Historique, Telegram, benchmark, jeux, widget, profils : gestion des onglets/paramètres.</summary>
    public partial class MainWindow
    {
        void InitHistory()
        {
            History.ReportOpener = () => { _app.OpenReport(); return null; };
            History.NoteAdded = t => _app.Toasts.Show(new AlertInfo { RuleId = "note", Severity = Severity.Info, Time = DateTime.Now, Title = "Marqueur ajouté", Detail = t }, true);
            RefreshHistoryMetrics();
        }
        void RefreshHistoryMetrics()
        {
            History.SetMetrics(_cards.Select(c => new HistoryView.MetricDef { Key = c.Key, Title = c.Title, Unit = c.Unit.Length > 0 ? c.Unit : (c.Key.StartsWith("cpuMHz") || c.Key.StartsWith("gpuMHz") ? "MHz" : ""), FixedMax = c.IsPercent ? 100 : 0 }));
        }

        void InitExtras()
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)) BenchDisk.Items.Add(d.Name.TrimEnd('\\'));
            if (BenchDisk.Items.Count > 0) BenchDisk.SelectedIndex = 0;
            FillBenchTable(null);
            _cards.CollectionChanged += (s, e) => RefreshHistoryMetrics();
            UpdateProfilePill();
        }
        void ExtrasOnSample(Sample smp) { }

        void ProfilePill_Click(object s, MouseButtonEventArgs e) => ProfilePopup.IsOpen = true;

        // ---------------- Telegram
        async void TgDetect_Click(object s, RoutedEventArgs e)
        {
            var token = _svm.TelegramToken;
            if (string.IsNullOrWhiteSpace(token)) { TgStatus.Text = "Colle d'abord le token du bot."; return; }
            TgStatus.Text = "Recherche du chat ID… (envoie un message au bot si ce n'est pas déjà fait)";
            var id = await TelegramSender.DetectChatIdAsync(token);
            if (id == null) { TgStatus.Text = "Aucun message trouvé : écris quelque chose à ton bot dans Telegram puis réessaie."; return; }
            _svm.TelegramChatId = id; TgStatus.Text = "Chat ID détecté : " + id + " ✔";
        }
        async void TgTest_Click(object s, RoutedEventArgs e)
        {
            TgStatus.Text = "Envoi…";
            var err = await TelegramSender.SendAsync(_app.Settings, "✅ PerfMonitor Live : test réussi. Les alertes critiques et le bilan quotidien arriveront ici.");
            TgStatus.Text = err == null ? "Message envoyé ✔ (regarde Telegram)" : "Échec : " + err;
        }
        void Digest_Click(object s, RoutedEventArgs e) => _app.ShowDigest(DateTime.Today.AddDays(-1), true);

        // ---------------- Jeux / widget
        void AddForeground_Click(object s, RoutedEventArgs e)
        {
            GameInfo.Text = "Passe sur le jeu/l'application dans les 5 secondes…";
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            t.Tick += (ss, ee) =>
            {
                t.Stop();
                var name = App.ForegroundProcessName();
                if (string.IsNullOrEmpty(name) || name.Equals("PerfMonitorLive", StringComparison.OrdinalIgnoreCase)) { GameInfo.Text = "Rien capturé (la fenêtre au premier plan était PerfMonitor)."; return; }
                if (!_app.Settings.Games.Contains(name, StringComparer.OrdinalIgnoreCase)) { _app.Settings.Games.Add(name); _app.Settings.Save(); }
                _svm.RaiseAll(); GameInfo.Text = "« " + name + " » ajouté à la liste des jeux.";
            };
            t.Start();
        }
        void WidgetReset_Click(object s, RoutedEventArgs e) => _app.ResetWidget();

        // ---------------- Benchmark
        CancellationTokenSource _benchCts;
        public class BenchRow { public string Name { get; set; } public string Now { get; set; } public string Prev { get; set; } public string Best { get; set; } public string Delta { get; set; } public Brush DeltaBrush { get; set; } }
        async void Bench_Click(object s, RoutedEventArgs e)
        {
            if (_benchCts != null) { _benchCts.Cancel(); return; }
            var disk = BenchDisk.SelectedItem as string ?? "C:";
            _benchCts = new CancellationTokenSource(); BenchBtn.Content = "⏹ Arrêter"; BenchStatus.Text = "Préparation…";
            try
            {
                var prog = new Progress<string>(m => BenchStatus.Text = m);
                var r = await Bench.RunAsync(disk, prog, _benchCts.Token);
                HistoryReader.AddNote("Benchmark : CPU " + r.cpuMBs + " Mo/s · RAM " + r.ramGBs + " Go/s · " + disk + " " + r.seqReadMBs + "/" + r.seqWriteMBs + " Mo/s");
                BenchStatus.Text = "Terminé ✔"; FillBenchTable(r);
            }
            catch (OperationCanceledException) { BenchStatus.Text = "Annulé."; }
            catch (Exception ex) { BenchStatus.Text = "Erreur : " + ex.Message; }
            finally { _benchCts = null; BenchBtn.Content = "▶ Lancer le benchmark"; }
        }
        void FillBenchTable(BenchResult current)
        {
            var all = Bench.Load();
            if (current == null) current = all.LastOrDefault();
            if (current == null) { BenchRows.ItemsSource = null; return; }
            var prev = all.Where(b => b.Time < current.Time).LastOrDefault();
            BenchRow Row(string name, Func<BenchResult, double> f, string unit)
            {
                double now = f(current), p = prev == null ? double.NaN : f(prev), best = all.Count == 0 ? now : all.Max(f);
                double delta = double.IsNaN(p) || p == 0 ? double.NaN : (now - p) / p * 100;
                return new BenchRow
                {
                    Name = name, Now = now.ToString("0.#") + " " + unit, Prev = double.IsNaN(p) ? "–" : p.ToString("0.#") + " " + unit, Best = best.ToString("0.#") + " " + unit,
                    Delta = double.IsNaN(delta) ? "" : (delta >= 0 ? "+" : "") + delta.ToString("0") + " % vs précédent",
                    DeltaBrush = double.IsNaN(delta) || Math.Abs(delta) < 3 ? Palette.B(Palette.Muted) : delta > 0 ? Palette.B(Palette.Good) : Palette.B(Palette.Bad)
                };
            }
            BenchRows.ItemsSource = new[]
            {
                Row("CPU multi-thread (SHA-256)", b => b.cpuMBs, "Mo/s"), Row("CPU mono-thread", b => b.cpu1MBs, "Mo/s"), Row("Mémoire (copie)", b => b.ramGBs, "Go/s"),
                Row("Disque " + current.disk + " écriture séq.", b => b.seqWriteMBs, "Mo/s"), Row("Disque " + current.disk + " lecture séq.", b => b.seqReadMBs, "Mo/s"), Row("Disque " + current.disk + " 4 Ko aléatoire", b => b.rnd4kIops, "IOPS"),
            };
            BenchStatus.Text = "Dernier : " + current.Time.ToString("dd/MM HH:mm") + (all.Count > 1 ? " · " + all.Count + " mesures" : "");
        }
    }
}
