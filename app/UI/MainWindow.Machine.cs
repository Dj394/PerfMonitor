using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PerfMonitorLive.Metrics;

namespace PerfMonitorLive.UI
{
    /// <summary>Onglet « Machine » : inventaire matériel + capteurs disponibles.</summary>
    public partial class MainWindow
    {
        bool _scanning;

        void InitMachine()
        {
            Inventory.Scanned += m => Dispatcher.BeginInvoke(new Action(() => { RenderMachine(m); _app.OnInventory(m); }));
            RenderMachine(Inventory.Current);
        }

        public void RefreshMachine() => RenderMachine(Inventory.Current);

        // ------------------------------------------------------------ guide de bienvenue
        WelcomeWindow _welcome;
        public void ShowWelcome()
        {
            if (_welcome != null) return;
            try
            {
                var scr = _app.Toasts.TargetScreen();
                bool multi = System.Windows.Forms.Screen.AllScreens.Length > 1;
                string scrName = scr == null || !multi ? null : scr.Primary ? "l'écran principal" : "l'écran secondaire";
                var c = _app.Settings.Corner ?? "BottomRight";
                string corner = c == "TopRight" ? "en haut à droite" : c == "BottomLeft" ? "en bas à gauche" : c == "TopLeft" ? "en haut à gauche" : "en bas à droite";
                _welcome = new WelcomeWindow(Inventory.Current, corner + (scrName != null ? " de " + scrName : "")) { Owner = this };
                _welcome.ShowAgain.IsChecked = false;
                _welcome.ShowDialog();
                _app.Settings.WelcomeShown = !_welcome.ShowNextTime; _app.Settings.Save();
                if (_welcome.GoToMachine) ShowTab("Machine");
            }
            catch (Exception ex) { Paths.Log("welcome: " + ex.Message); }
            finally { _welcome = null; }
        }
        void Welcome_Click(object s, RoutedEventArgs e) => ShowWelcome();

        // ------------------------------------------------------------ mises à jour
        string _updateUrl;
        public void SetUpdateStatus(string text, string url)
        {
            UpdateText.Text = text ?? ""; _updateUrl = url;
            UpdateOpenBtn.Visibility = string.IsNullOrEmpty(url) ? Visibility.Collapsed : Visibility.Visible;
            UpdateBtn.IsEnabled = text != "Vérification…";
        }
        void Update_Click(object s, RoutedEventArgs e) => _app.CheckUpdate(true);
        void UpdateOpen_Click(object s, RoutedEventArgs e) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_updateUrl ?? Updater.ReleasesUrl) { UseShellExecute = true }); } catch { } }
        DateTime _selfCostShown = DateTime.MinValue;
        /// <summary>Met à jour la ligne « coût de PerfMonitor » de l'onglet Machine (toutes les 30 s, si l'onglet est visible).</summary>
        void MachineOnSample()
        {
            var smp = _app.Sampler; if (smp.SelfMemMB <= 0) return;
            SelfCost = smp.SelfCpuPct.ToString("0.0") + " % CPU · " + smp.SelfMemMB.ToString("0") + " Mo · " + (_app.EcoActive ? "mode économie (2 s)" : "mesure toutes les " + (smp.IntervalMs / 1000.0).ToString("0.#") + " s");
            if (IsVisible && (DateTime.Now - _selfCostShown).TotalSeconds >= 30 && MachinePanel.IsVisible) { _selfCostShown = DateTime.Now; RenderMachine(Inventory.Current); }
        }
        void MachineScan_Click(object s, RoutedEventArgs e)
        {
            if (_scanning) return;
            _scanning = true; MachineScanBtn.IsEnabled = false; MachineScanBtn.Content = "⏳  Scan…";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Inventory.Scan(); } catch (Exception ex) { Paths.Log("scan: " + ex.Message); }
                Dispatcher.BeginInvoke(new Action(() => { _scanning = false; MachineScanBtn.IsEnabled = true; MachineScanBtn.Content = "🔍  Re-scanner"; }));
            });
        }
        void MachineCopy_Click(object s, RoutedEventArgs e)
        {
            try { Clipboard.SetText(MachineText(Inventory.Current)); MachineCopyBtn.Content = "✅  Copié"; }
            catch { }
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (a, b) => { t.Stop(); MachineCopyBtn.Content = "📋  Copier"; }; t.Start();
        }

        /// <summary>Sections (titre → lignes label/valeur).</summary>
        static List<Tuple<string, List<Tuple<string, string>>>> MachineSections(MachineInfo m)
        {
            var secs = new List<Tuple<string, List<Tuple<string, string>>>>();
            List<Tuple<string, string>> Sec(string title) { var l = new List<Tuple<string, string>>(); secs.Add(Tuple.Create(title, l)); return l; }
            void Add(List<Tuple<string, string>> l, string k, string v) { if (!string.IsNullOrWhiteSpace(v)) l.Add(Tuple.Create(k, v)); }
            bool scanned = m != null && m.Cpu != null && m.Cpu.Name != "?";
            if (!scanned) { var l = Sec("🖥️  Machine"); Add(l, "État", "scan en cours…"); return secs; }

            var sys = Sec("🖥️  Système");
            Add(sys, "Nom", m.Host);
            Add(sys, "Type", m.KindText + (m.Chassis != null ? " (" + m.Chassis + ")" : ""));
            Add(sys, "Constructeur", string.Join(" ", new[] { m.Maker, m.Model }.Where(x => !string.IsNullOrWhiteSpace(x) && x != "System manufacturer" && x != "System Product Name" && x != "To Be Filled By O.E.M.")));
            Add(sys, "Windows", m.Os + (m.OsBuild != null ? " (" + m.OsBuild + ")" : ""));
            Add(sys, "Batterie", m.HasBattery ? "présente" : null);
            Add(sys, "Écrans", m.Screens.Count == 0 ? null : string.Join(" · ", m.Screens.Select(s => s.W + "×" + s.H + (s.Primary ? " (principal)" : ""))));
            Add(sys, "Réseau", m.Nets.Count == 0 ? null : string.Join(" · ", m.Nets.Select(n => (n.Wireless ? "Wi-Fi " : "") + n.Name + (n.SpeedMbps > 0 ? " " + (n.SpeedMbps >= 1000 ? (n.SpeedMbps / 1000).ToString("0.#") + " Gb/s" : n.SpeedMbps + " Mb/s") : ""))));

            var cpu = Sec("🧠  Processeur");
            Add(cpu, "Modèle", m.Cpu.Short);
            Add(cpu, "Fabricant", VendorName(m.Cpu.Vendor));
            Add(cpu, "Cœurs / threads", m.Cpu.Cores + " / " + m.Cpu.Threads);
            Add(cpu, "Fréquence de base", m.Cpu.MaxMHz > 0 ? (m.Cpu.MaxMHz / 1000.0).ToString("0.00") + " GHz" : null);
            Add(cpu, "Carte mère", string.Join(" ", new[] { m.BoardMaker, m.Board }.Where(x => !string.IsNullOrWhiteSpace(x))));
            Add(cpu, "BIOS", m.Bios + (m.BiosDate != null ? " (" + m.BiosDate + ")" : ""));

            var gpu = Sec("🎮  Carte graphique");
            if (m.Gpus.Count == 0) Add(gpu, "GPU", "aucun détecté");
            foreach (var g in m.Gpus)
            {
                Add(gpu, g.Integrated ? "Intégré" : "Dédié", g.Short + (g.VramGB > 0 ? " · " + g.VramGB.ToString("0.#") + " Go" : ""));
                Add(gpu, "  pilote", g.Driver);
            }

            var ram = Sec("🧮  Mémoire vive");
            Add(ram, "Total", m.RamText);
            foreach (var mod in m.RamModules) Add(ram, mod.Slot ?? "Barrette", mod.SizeGB.ToString("0.#") + " Go" + (mod.Type != null ? " " + mod.Type : "") + (mod.ConfiguredMHz > 0 ? " · " + mod.ConfiguredMHz + " MHz" + (mod.SpeedMHz > mod.ConfiguredMHz ? " (max " + mod.SpeedMHz + ")" : "") : mod.SpeedMHz > 0 ? " · " + mod.SpeedMHz + " MHz" : "") + (!string.IsNullOrWhiteSpace(mod.Maker) && mod.Maker != "Unknown" ? " · " + mod.Maker : ""));
            if (m.RamModules.Count > 0 && m.RamModules.Any(x => x.SpeedMHz > x.ConfiguredMHz && x.ConfiguredMHz > 0)) Add(ram, "⚠️", "La RAM tourne moins vite que sa vitesse nominale : profil XMP/DOCP/EXPO probablement inactif dans le BIOS.");

            var dsk = Sec("💾  Stockage");
            if (m.Disks.Count == 0) Add(dsk, "Disque", "aucun détecté");
            foreach (var d in m.Disks) Add(dsk, (d.Letters.Count > 0 ? string.Join(" ", d.Letters) : "Disque " + d.Index) + (d.System ? " (système)" : ""), d.Model + " · " + d.KindText + (d.Bus != null && d.Bus != "?" && d.KindText.IndexOf(d.Bus, StringComparison.OrdinalIgnoreCase) < 0 ? " " + d.Bus : "") + " · " + (d.SizeGB >= 1000 ? (d.SizeGB / 1000).ToString("0.#") + " To" : d.SizeGB + " Go"));

            var cap = Sec("📡  Capteurs disponibles");
            Add(cap, "Droits admin", m.Elevated ? "oui (capteurs matériels actifs)" : "non — températures/ventilateurs indisponibles (lancer via la tâche planifiée)");
            Add(cap, "Température CPU", m.CapCpuTemp ? "✅" : "❌");
            Add(cap, "Température GPU", m.CapGpuTemp ? "✅" : m.Gpus.Count == 0 ? "— (pas de GPU)" : "❌");
            Add(cap, "Ventilateurs", m.CapFans ? "✅" : "❌ (aucun capteur lu — courant sur portable / carte mère non prise en charge)");
            Add(cap, "SMART / temp. disques", m.CapStorTemp ? "✅" : "❌");
            Add(cap, "Consommation (W)", m.CapPower ? "✅" : "❌");
            Add(cap, "Fréquences", m.CapClocks ? "✅" : "❌");
            Add(cap, "Coût de PerfMonitor", SelfCost);
            Add(cap, "Version", Updater.CurrentVersion);
            Add(cap, "Dernier scan", m.ScannedAt == default(DateTime) ? null : m.ScannedAt.ToString("dd/MM/yyyy HH:mm") + " (" + m.ScanMs + " ms)");
            return secs;
        }
        static string SelfCost;
        public static string VendorName(Vendor v) => v == Vendor.Amd ? "AMD" : v == Vendor.Intel ? "Intel" : v == Vendor.Nvidia ? "NVIDIA" : v == Vendor.Qualcomm ? "Qualcomm" : "—";

        void RenderMachine(MachineInfo m)
        {
            if (MachinePanel == null) return;
            MachineTitle.Text = m != null && m.Cpu != null && m.Cpu.Name != "?" ? "🖥️  " + m.Host + " — " + m.KindText : "Ma machine";
            var items = new List<UIElement>();
            foreach (var sec in MachineSections(m))
            {
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = sec.Item1, Style = (Style)FindResource("SectionTitle") });
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int row = 0;
                foreach (var kv in sec.Item2)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var k = new TextBlock { Text = kv.Item1, Margin = new Thickness(0, 3, 8, 3), TextWrapping = TextWrapping.Wrap };
                    var v = new TextBlock { Text = kv.Item2, Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap };
                    k.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                    v.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
                    Grid.SetRow(k, row); Grid.SetRow(v, row); Grid.SetColumn(v, 1);
                    grid.Children.Add(k); grid.Children.Add(v); row++;
                }
                sp.Children.Add(grid);
                var border = new Border { Style = (Style)FindResource("Section"), Child = sp, Margin = new Thickness(0, 0, 12, 12), VerticalAlignment = VerticalAlignment.Top };
                items.Add(border);
            }
            MachinePanel.ItemsSource = items;
        }

        static string MachineText(MachineInfo m)
        {
            var sb = new StringBuilder();
            sb.AppendLine("PerfMonitor — inventaire " + (m?.Host ?? "") + " (" + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + ")");
            foreach (var sec in MachineSections(m))
            {
                sb.AppendLine(); sb.AppendLine(sec.Item1);
                foreach (var kv in sec.Item2) sb.AppendLine("  " + kv.Item1.Trim() + " : " + kv.Item2);
            }
            return sb.ToString();
        }
    }
}
