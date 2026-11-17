using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PerfMonitorLive.Metrics;
using WF = System.Windows.Forms;

namespace PerfMonitorLive.UI
{
    public class PctToWidth : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c) => Math.Max(0, Math.Min(100, System.Convert.ToDouble(v))) * 3;
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public partial class MainWindow : Window, System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
        int _cols = 3; double _gridHeight = double.NaN;
        public int Cols { get => _cols; set { if (_cols != value) { _cols = value; Raise(nameof(Cols)); } } }
        public double GridHeight { get => _gridHeight; set { if (!(double.IsNaN(_gridHeight) && double.IsNaN(value)) && _gridHeight != value) { _gridHeight = value; Raise(nameof(GridHeight)); } } }
        public double CardMinHeight => _app.Settings.Compact ? 200 : 250;
        public double ValueFontSize => _app.Settings.Compact ? 22 : 28;

        void LiveScroll_SizeChanged(object s, SizeChangedEventArgs e) { Relayout(); AdvisorRelayout(); }
        void Relayout()
        {
            double w = LiveScroll.ActualWidth - 14, h = LiveScroll.ActualHeight;
            if (w <= 0 || _cards.Count == 0) return;
            int maxCols = Math.Max(1, Math.Min(9, (int)Math.Floor(w / (_app.Settings.Compact ? 290 : 330))));
            double minRow = _app.Settings.Compact ? 205 : 262, maxRow = _app.Settings.Compact ? 360 : 440;
            int best = -1;
            for (int c = maxCols; c >= 1; c--)
            {
                int r = (int)Math.Ceiling(_cards.Count / (double)c);
                double cell = h / r;
                if (cell >= minRow && cell <= maxRow) { best = c; break; }
            }
            if (best > 0) { Cols = best; GridHeight = h; }
            else { Cols = maxCols; GridHeight = double.NaN; }
        }
        void Spark_SizeChanged(object s, SizeChangedEventArgs e)
        {
            if (s is FrameworkElement fe && fe.DataContext is MetricVm vm) vm.Resize(fe.ActualWidth, fe.ActualHeight);
        }

        readonly App _app;
        readonly ObservableCollection<MetricVm> _cards = new ObservableCollection<MetricVm>();
        readonly ObservableCollection<ProcVm> _procs = new ObservableCollection<ProcVm>();
        readonly Dictionary<string, MetricVm> _byKey = new Dictionary<string, MetricVm>();
        readonly SettingsVm _svm;
        bool _reallyClose;
        Sample _lastSample;
        readonly HashSet<string> _fanSeen = new HashSet<string>();
        bool _sawFps;

        public MainWindow(App app)
        {
            InitializeComponent();
            _app = app;
            // Cards/Procs.ItemsSource : branchés à l'affichage, débranchés au masquage (voir OnVisibleChanged)
            _svm = new SettingsVm(app.Settings, app.OnSettingsChanged);
            RefreshScreens();
            SettingsPanel.DataContext = _svm;
            BuildFixedCards();
            Closing += (s, e) => { if (!_reallyClose) { e.Cancel = true; Hide(); } };   // le ménage est fait par OnVisibleChanged
            IsVisibleChanged += (s, e) => OnVisibleChanged();
            Tabs.SelectionChanged += Tabs_SelectionChanged;
            Loaded += (s, e) => { ApplyBitmapCaches(VisualTreeHelper.GetDpi(this).DpiScaleX); UpdateLiveState(); };
            SourceInitialized += (s, e) => CenterOnPrimary();
            InitAdvisor();
            InitHistory();
            InitExtras();
            InitMachine();
            VersionText.Text = "v" + Updater.CurrentVersion;
        }

        public void ForceClose() { _reallyClose = true; Close(); }
        public SettingsVm SettingsVm => _svm;

        // ------------------------------------------------------------------ visibilité / économie
        /// <summary>Fenêtre réellement à l'écran (IsVisible reste vrai quand elle est réduite).</summary>
        bool WindowShown => IsVisible && WindowState != WindowState.Minimized;
        /// <summary>Onglet Live affiché : seules ses cartes sont recalculées et notifiées.</summary>
        bool LiveTabShown => WindowShown && ReferenceEquals(Tabs.SelectedItem, LiveTab);
        bool ProcsTabShown => WindowShown && ReferenceEquals(Tabs.SelectedItem, ProcsTab);
        bool _live;

        /// <summary>Cartes : rendu actif ou en sommeil (l'historique continue d'être alimenté) ; mascotte animée ou figée.</summary>
        void UpdateLiveState()
        {
            bool live = LiveTabShown;
            if (live != _live) { _live = live; foreach (var c in _cards) c.SetLive(live); }
            UpdateMascotAnim();
        }

        void OnVisibleChanged()
        {
            if (IsVisible)
            {
                UpdateLiveState();                      // reconstruit d'abord les cartes « sales »…
                bool replug = Cards.ItemsSource == null;
                if (replug) Cards.ItemsSource = _cards;   // …puis rebranche l'affichage
                if (Procs.ItemsSource == null) Procs.ItemsSource = _procs;
                Relayout();
                if (replug && _liveOffset > 0)
                {
                    // Position de défilement de l'onglet Live : rétablie une fois les conteneurs régénérés
                    double off = _liveOffset;
                    Dispatcher.BeginInvoke(new Action(() => { LiveScroll.ScrollToVerticalOffset(off); AdvisorRelayout(); }), DispatcherPriority.Loaded);
                }
                RefreshScreens(); UpdatePauseText(); UpdateProfilePill();
                RefreshShown();
                return;
            }
            if (_reallyClose) return;
            // Masquée (fermeture → zone de notification, ou App.ToggleMain) : on rend le maximum de mémoire.
            UpdateLiveState();
            History?.Release();
            CommitCardEdit();                                     // seuil/durée en cours de saisie : validé avant destruction du champ
            if (Cards.ItemsSource != null) _liveOffset = LiveScroll.VerticalOffset;   // avant que la liste vidée ne ramène l'offset à 0
            Cards.ItemsSource = null; Procs.ItemsSource = null;   // les VM et leur historique restent
            Dispatcher.BeginInvoke(new Action(TrimMemory), DispatcherPriority.ContextIdle);
        }

        double _liveOffset;

        /// <summary>Valide la saisie (liaison LostFocus) d'un champ d'une carte qui a encore le focus logique.</summary>
        void CommitCardEdit()
        {
            try
            {
                if (FocusManager.GetFocusedElement(this) is TextBox tb && Cards.IsAncestorOf(tb))
                    tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }
            catch (Exception ex) { Paths.Log("commit edit: " + ex.Message); }
        }

        /// <summary>Après masquage : GC compactant (LOH comprise) puis réduction du working set.</summary>
        void TrimMemory()
        {
            if (IsVisible || _reallyClose) return;
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
                SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
            }
            catch (Exception ex) { Paths.Log("trim: " + ex.Message); }
        }
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        void Tabs_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, Tabs)) return;   // SelectionChanged remonte aussi des ComboBox des onglets
            UpdateLiveState();
            if (ProcsTabShown && _lastSample != null) UpdateProcs(_lastSample);
        }

        /// <summary>Retour à l'écran (affichage, restauration) : en-tête et processus à jour sans attendre la mesure suivante.</summary>
        void RefreshShown()
        {
            if (_lastSample == null || !WindowShown) return;
            UpdateHeader(_lastSample);
            if (ProcsTabShown) UpdateProcs(_lastSample);
        }

        /// <summary>Effets statiques (ombres, flou) mis en cache bitmap, rendus à la résolution de l'écran pour rester nets.</summary>
        void ApplyBitmapCaches(double scale)
        {
            if (scale <= 0) scale = 1;
            HeaderIcon.CacheMode = new BitmapCache(scale) { SnapsToDevicePixels = true };
            Mascot.CacheMode = new BitmapCache(scale) { SnapsToDevicePixels = true };
            Bubble.CacheMode = new BitmapCache(scale) { SnapsToDevicePixels = true, EnableClearType = true };
        }
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            ApplyBitmapCaches(newDpi.DpiScaleX);
        }

        void CenterOnPrimary()
        {
            var wa = WF.Screen.PrimaryScreen.WorkingArea;
            var src = PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
            Left = (wa.Left + (wa.Width - Width * sx) / 2) / sx;
            Top = (wa.Top + (wa.Height - Height * sy) / 2) / sy;
        }

        void RefreshScreens()
        {
            var screens = WF.Screen.AllScreens;
            _svm.ScreenNames = screens.Select((s, i) => "Écran " + (i + 1) + (s.Primary ? " (principal)" : "") + " " + s.Bounds.Width + "×" + s.Bounds.Height).ToArray();
            _svm.RaiseAll();
            var target = _app.Toasts.TargetScreen();
            ScreensInfo.Text = screens.Length + " écran(s) détecté(s). Notifications actuellement envoyées sur : " + target.DeviceName.Replace(@"\\.\", "") + " (" + target.Bounds.Width + "×" + target.Bounds.Height + (target.Primary ? ", principal" : ", secondaire") + ").";
        }

        // ------------------------------------------------------------------ cartes
        static readonly string[] CardOrder = { "cpu", "memPct", "temp", "gpuTemp", "cpuMHz", "gpuMHz", "cpuW", "gpuW", "fps", "v12", "v5", "v33", "bat", "rx", "tx", "pageIn", "reboot", "disk.", "stor.", "fan:" };
        static int Rank(string key) { for (int i = 0; i < CardOrder.Length; i++) if (key == CardOrder[i] || key.StartsWith(CardOrder[i]) && CardOrder[i].EndsWith(".") || key.StartsWith(CardOrder[i]) && CardOrder[i].EndsWith(":")) return i; return CardOrder.Length; }
        MetricVm Card(string key, string title, string icon, Rule rule, double fixedMax = 0)
        {
            var vm = new MetricVm(key, title, icon, rule, _app.Settings.Save, fixedMax);
            vm.SetLive(_live);
            int r = Rank(key), pos = _cards.Count;
            for (int i = 0; i < _cards.Count; i++) if (Rank(_cards[i].Key) > r) { pos = i; break; }
            _cards.Insert(pos, vm); _byKey[key] = vm; Relayout(); return vm;
        }
        /// <summary>Cartes toujours présentes ; les autres (GPU, fréquences, conso, batterie, disques, ventilateurs) n'apparaissent que si la machine fournit la mesure.</summary>
        void BuildFixedCards()
        {
            var s = _app.Settings;
            Card("cpu", "Processeur", "🧠", s.Get("cpu"), 100);
            Card("memPct", "Mémoire vive", "🧮", s.Get("memPct"), 100);
            Card("temp", "Température CPU", "🌡️", s.Get("temp"));
            Card("rx", "Réseau — reçu", "⬇️", s.Get("rx"));
            Card("tx", "Réseau — envoyé", "⬆️", s.Get("tx"));
            Card("pageIn", "Pression mémoire (pages lues/s)", "📄", s.Get("pageIn"));
            Card("reboot", "Besoin de redémarrage (score)", "🔄", s.Get("reboot"), 100);
        }
        void EnsureDynamicCards(Sample smp)
        {
            var s = _app.Settings; var m = Inventory.Current;
            var gpu = m.MainGpu; string gpuName = gpu != null ? " " + gpu.Short : "";
            var hw = _app.Sampler.Hw;   // un GPU hybride (Optimus) ne répond que réveillé : la carte reste dès qu'il a répondu une fois
            if ((smp.gpu.HasValue || hw.SawGpuTemp) && !_byKey.ContainsKey("gpuTemp")) Card("gpuTemp", "Température GPU", "🎮", s.Get("gpuTemp"));
            if ((smp.cpuMHz.HasValue || hw.SawCpuClock) && !_byKey.ContainsKey("cpuMHz")) Card("cpuMHz", "Fréquence CPU", "⚡", null);
            if ((smp.gpuMHz.HasValue || hw.SawGpuClock) && !_byKey.ContainsKey("gpuMHz")) Card("gpuMHz", "Fréquence GPU", "⚡", null);
            if ((smp.cpuW.HasValue || hw.SawCpuPower) && !_byKey.ContainsKey("cpuW")) Card("cpuW", "Consommation CPU", "🔌", s.Get("cpuW"));
            if ((smp.gpuW.HasValue || hw.SawGpuPower) && !_byKey.ContainsKey("gpuW")) Card("gpuW", "Consommation GPU", "🔌", s.Get("gpuW"));
            // FPS : la carte n'apparait qu'une fois un jeu mesure, et reste ensuite (l'historique garde son interet hors jeu)
            if (smp.fps.HasValue) _sawFps = true;   // collant, comme hw.SawGpuTemp : survit à RebuildCards hors jeu
            if (_sawFps && !_byKey.ContainsKey("fps")) Card("fps", "Images par seconde", "🎞️", null);
            // Tensions : seuls les trois rails identifiés ont une carte. Les autres capteurs du Super I/O
            // (Vcore, AVCC, 3VSB, VIN…) restent enregistrés dans l'historique, mais encombreraient le tableau de bord.
            if (smp.v12.HasValue && !_byKey.ContainsKey("v12")) Card("v12", "Rail +12 V", "⚡", null);
            if (smp.v5.HasValue && !_byKey.ContainsKey("v5")) Card("v5", "Rail +5 V", "⚡", null);
            if (smp.v33.HasValue && !_byKey.ContainsKey("v33")) Card("v33", "Rail +3,3 V", "⚡", null);
            if (smp.bat.HasValue && !_byKey.ContainsKey("bat")) Card("bat", "Batterie", "🔋", null, 100);
            foreach (var d in smp.disks)
            {
                if (_byKey.ContainsKey("disk.pct:" + d.n)) continue;
                var info = m.DiskByPerfName(d.n);
                s.EnsureDisk(d.n, info?.Kind ?? DiskKind.Unknown);
                var label = DiskLabel(d.n, info); var icon = DiskIcon(info);
                Card("disk.pct:" + d.n, label + " — activité", icon, s.Get("disk.pct", d.n), 100);
                Card("disk.rw:" + d.n, label + " — débit", "🔁", s.Get("disk.rw", d.n));
                Card("disk.lat:" + d.n, label + " — latence", "⏱️", s.Get("disk.lat", d.n));
            }
            foreach (var st in smp.stor)
            {
                if (_byKey.ContainsKey("stor.health:" + st.n)) continue;
                var info = m.DiskByStorName(st.n);
                s.EnsureStorage(st.n, info?.Kind ?? DiskKind.Unknown);
                Card("stor.health:" + st.n, "Santé " + Short(st.n), "🩺", s.Get("stor.err", st.n));
            }
            foreach (var st in smp.stor)   // température SMART : seulement si le disque la fournit
                if (st.t > 0 && !_byKey.ContainsKey("stor.temp:" + st.n)) Card("stor.temp:" + st.n, "Température " + Short(st.n), "🌡️", s.Get("stor.temp", st.n));
            foreach (var f in smp.fans)
            {
                if (_byKey.ContainsKey("fan:" + f.n)) continue;
                if (f.rpm <= 0 && !_fanSeen.Contains(f.n)) continue;   // pas de carte pour les connecteurs vides
                _fanSeen.Add(f.n);
                s.EnsureFan(f.n);
                Card("fan:" + f.n, "Ventilateur " + f.n, "🌀", s.Get("fan", f.n));
            }
        }
        /// <summary>Recrée toutes les cartes (changement de profil : les règles sont d'autres objets).</summary>
        public void RebuildCards()
        {
            _cards.Clear(); _byKey.Clear();
            BuildFixedCards();
            if (_lastSample != null) { EnsureDynamicCards(_lastSample); OnSample(_lastSample); }
            Relayout();
        }
        static string DiskLabel(string n, DiskInfo info)
        {
            var parts = n.Split(' '); var letters = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "n°" + n;
            if (info == null) return "Disque " + letters;
            var kind = info.Kind == DiskKind.Nvme ? "NVMe" : info.Kind == DiskKind.Ssd ? "SSD" : info.Kind == DiskKind.Hdd ? "HDD" : info.Kind == DiskKind.Usb ? "USB" : "Disque";
            return kind + " " + letters;
        }
        static string DiskIcon(DiskInfo info) => info == null ? "💽" : info.Kind == DiskKind.Hdd ? "💽" : info.Kind == DiskKind.Usb ? "🔌" : "💾";
        static string Short(string model) => model.Length > 26 ? model.Substring(0, 25) + "…" : model;

        public void OnSample(Sample smp)
        {
            _lastSample = smp;
            EnsureDynamicCards(smp);
            AdvisorOnSample(smp);
            var active = _app.Alerts.ActiveRules;
            void Upd(string key, double? v, string text, string sub = "", string alertKey = null)
            {
                if (!_byKey.TryGetValue(key, out var vm)) return;
                vm.Push(v); vm.ValueText = text; vm.SubText = sub; vm.IsAlert = active.Contains(alertKey ?? key);
            }
            Upd("cpu", smp.cpu, Math.Round(smp.cpu) + " %", smp.procs.Count > 0 ? smp.procs[0].n + " " + smp.procs[0].cpu + " %" : "");
            Upd("memPct", smp.memPct, Math.Round(smp.memPct) + " %", (smp.memMB / 1024).ToString("0.0") + " / " + (smp.TotalMB / 1024).ToString("0.0") + " Go");
            string noTemp = _app.Sampler.Hw.MissingReason;
            var cpuVendor = Inventory.Current.Cpu.Vendor;
            Upd("temp", smp.temp, smp.temp.HasValue ? smp.temp.Value.ToString("0") + " °C" : "—", smp.temp.HasValue ? (cpuVendor == Vendor.Amd ? "Tctl/Tdie" : cpuVendor == Vendor.Intel ? "package" : "CPU") : noTemp);
            if (smp.bat.HasValue) Upd("bat", smp.bat, smp.bat.Value.ToString("0") + " %", smp.ac == true ? "sur secteur" : "sur batterie");
            string gpuOff = _app.Sampler.Hw.SawGpuTemp || _app.Sampler.Hw.SawGpuPower ? "GPU en veille (économie d'énergie)" : noTemp;
            Upd("gpuTemp", smp.gpu, smp.gpu.HasValue ? smp.gpu.Value.ToString("0") + " °C" : "—", smp.gpu.HasValue ? "cœur GPU" : gpuOff);
            Upd("cpuMHz", smp.cpuMHz, smp.cpuMHz.HasValue ? (smp.cpuMHz.Value / 1000).ToString("0.00") + " GHz" : "—", smp.cpuMHz.HasValue ? "cœur le plus rapide" : noTemp);
            Upd("gpuMHz", smp.gpuMHz, smp.gpuMHz.HasValue ? smp.gpuMHz.Value.ToString("0") + " MHz" : "—", smp.gpuMHz.HasValue ? "cœur GPU" : gpuOff);
            Upd("cpuW", smp.cpuW, smp.cpuW.HasValue ? smp.cpuW.Value.ToString("0") + " W" : "—", smp.cpuW.HasValue ? "package" : noTemp);
            Upd("gpuW", smp.gpuW, smp.gpuW.HasValue ? smp.gpuW.Value.ToString("0") + " W" : "—", smp.gpuW.HasValue ? "carte" : gpuOff);
            Upd("fps", smp.fps, smp.fps.HasValue ? smp.fps.Value.ToString("0") + " FPS" : "—", smp.fps.HasValue ? "jeu au premier plan" : "aucun jeu detecte");
            // Tolérance ATX : ±5 % sur chaque rail. Sortir de la fourchette basse sous charge est le signe recherché.
            Upd("v12", smp.v12, smp.v12.HasValue ? smp.v12.Value.ToString("0.00") + " V" : "—", smp.v12.HasValue ? "toléré 11,40 – 12,60 V" : noTemp);
            Upd("v5", smp.v5, smp.v5.HasValue ? smp.v5.Value.ToString("0.00") + " V" : "—", smp.v5.HasValue ? "toléré 4,75 – 5,25 V" : noTemp);
            Upd("v33", smp.v33, smp.v33.HasValue ? smp.v33.Value.ToString("0.00") + " V" : "—", smp.v33.HasValue ? "toléré 3,14 – 3,47 V" : noTemp);
            foreach (var f in smp.fans) Upd("fan:" + f.n, f.rpm, f.rpm + " tr/min", f.rpm == 0 ? "arrêté ou non lu" : "");
            foreach (var st in smp.stor)
            {
                if (st.t > 0) Upd("stor.temp:" + st.n, st.t, st.t.ToString("0") + " °C", st.tmax > 0 && st.tmax < 95 ? "max " + st.tmax.ToString("0") + " °C" : "SMART");
                var errRule = _app.Settings.Get("stor.err", st.n);
                double newErr = errRule?.Value(smp) ?? 0;
                string sub = (st.wear > 0 ? "usure " + st.wear.ToString("0") + " % · " : "") + (st.hours > 0 ? Math.Round(st.hours) + " h · " : "") + "erreurs L " + st.rerr.ToString("0") + " / É " + st.werr.ToString("0");
                var vm = _byKey["stor.health:" + st.n];
                vm.Push(newErr); vm.ValueText = st.health == 0 ? (newErr > 0 ? "Erreurs !" : "OK") : st.HealthText; vm.SubText = sub;
                vm.IsAlert = active.Contains("stor.err:" + st.n) || active.Contains("stor.health:" + st.n) || st.health != 0;
            }
            if (smp.rb != null)
            {
                var rb = smp.rb;
                string sub = "allumé depuis " + RebootCheck.FormatUptime(rb.up) + (rb.wu || rb.cbs ? " · MAJ en attente" : rb.pfr ? " · fichiers en attente" : "");
                Upd("reboot", rb.score, rb.Level, sub);
            }
            Upd("rx", smp.rx / 1024, FmtRate(smp.rx / 1024), "");
            Upd("tx", smp.tx / 1024, FmtRate(smp.tx / 1024), "");
            Upd("pageIn", smp.pageIn, smp.pageIn.ToString("0") + " /s", "");
            foreach (var d in smp.disks)
            {
                Upd("disk.pct:" + d.n, d.pct, d.pct + " %", "file " + d.q.ToString("0.0"));
                Upd("disk.rw:" + d.n, d.r + d.w, FmtRate(d.r + d.w), "L " + FmtRate(d.r) + " · É " + FmtRate(d.w));
                Upd("disk.lat:" + d.n, d.lat, (d.lat < 10 ? d.lat.ToString("0.0") : d.lat.ToString("0")) + " ms", "");
            }
            ExtrasOnSample(smp);
            MachineOnSample();
            if (!WindowShown) return;   // cachée ou réduite : en-tête et processus rafraîchis au retour (RefreshShown)
            if (ProcsTabShown) UpdateProcs(smp);
            UpdateHeader(smp);
        }

        void UpdateProcs(Sample smp)
        {
            for (int i = 0; i < smp.procs.Count; i++)
            {
                if (_procs.Count <= i) _procs.Add(new ProcVm());
                var p = smp.procs[i]; var pv = _procs[i];
                pv.Name = p.n; pv.Cpu = p.cpu; pv.Mem = p.mem; pv.Handles = p.h;
                var leak = _app.Sampler.Leaks.Get(p.n);
                pv.Delta = leak == null || leak.SpanMin < 5 ? "" : (leak.MemDelta1hMB >= 0 ? "+" : "−") + Math.Abs(leak.MemDelta1hMB).ToString("0") + " Mo / 1 h";
                pv.DeltaBrush = leak != null && leak.MemDelta1hMB >= 400 ? Palette.B(Palette.Bad) : Palette.B(Palette.Muted);
            }
            while (_procs.Count > smp.procs.Count) _procs.RemoveAt(_procs.Count - 1);
            var suspects = _app.Sampler.Leaks.Suspects().ToList();   // une seule énumération par mesure
            LeaksText.Text = suspects.Count > 0
                ? "Fuites probables : " + string.Join(" · ", suspects.Select(l => l.Name + " (+" + Math.Round(l.MemDelta1hMB) + " Mo/h)"))
                : "Aucune fuite mémoire détectée sur les 3 dernières heures.";
        }

        /// <summary>Barre de titre : heure de mise à jour et pastille d'état (pinceaux partagés : pas de réaffectation inutile).</summary>
        void UpdateHeader(Sample smp)
        {
            var active = _app.Alerts.ActiveRules;
            StatusText.Text = "Mis à jour à " + smp.Time.ToString("HH:mm:ss");
            if (active.Count > 0)
            {
                StatusPill.Background = Palette.B(Palette.Bad, 0x33); StatusPillText.Foreground = Palette.B(Palette.Bad);
                StatusPillText.Text = "⚠️  " + active.Count + (active.Count > 1 ? " alertes en cours" : " alerte en cours");
            }
            else if (_app.Settings.IsPaused || smp.GameActive)
            {
                StatusPill.Background = Palette.B(Palette.Warn, 0x33); StatusPillText.Foreground = Palette.B(Palette.Warn);
                StatusPillText.Text = smp.GameActive ? "🎮  Mode jeu — notifications réduites" : "🔕  Notifications en pause";
            }
            else
            {
                StatusPill.Background = Palette.B(Palette.Good, 0x2A); StatusPillText.Foreground = Palette.B(Palette.Good);
                StatusPillText.Text = "✅  Tout est normal";
            }
        }
        static string FmtRate(double mbs) => mbs >= 1 ? mbs.ToString("0.0") + " Mo/s" : (mbs * 1024).ToString("0") + " Ko/s";

        public void UpdateProfilePill()
        {
            ProfilePillText.Text = "👤 " + _app.Settings.ActiveProfile + (_app.Settings.ProfileAuto ? " (auto)" : "");
        }
        public void ShowTab(string header)
        {
            foreach (TabItem t in Tabs.Items) if ((t.Header as string ?? "").Contains(header)) { Tabs.SelectedItem = t; break; }
        }

        public void UpdatePauseText() => PauseBtn.Content = _app.Settings.IsPaused ? "🔔  Reprendre (pause jusqu'à " + _app.Settings.PausedUntil.Value.ToString("HH:mm") + ")" : "🔕  Pause 1 h";
        void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void Close_Click(object s, RoutedEventArgs e) => Close();
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            MaxBtn.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            BorderThickness = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            UpdateLiveState();   // reduite : cartes en sommeil, mascotte figee ; restauree : reprise
            RefreshShown();
        }
        void Pause_Click(object s, RoutedEventArgs e) { _app.TogglePause(); UpdatePauseText(); }
        void Test_Click(object s, RoutedEventArgs e) => _app.TestToast();
        void Report_Click(object s, RoutedEventArgs e) => _app.OpenReport();
        void Profile_Click(object s, RoutedEventArgs e)
        {
            var b = s as Button; if (b?.Tag == null) return;
            var name = (string)b.Tag;
            if (name == "Auto") { _app.Settings.ProfileAuto = true; _app.Settings.Save(); _app.ApplyAutoProfile(); }
            else { _app.Settings.ProfileAuto = false; _app.SwitchProfile(name); }
            UpdateProfilePill(); _svm.RaiseAll();
        }
    }
}
