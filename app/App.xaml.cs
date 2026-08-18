using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PerfMonitorLive.Alerts;
using PerfMonitorLive.Metrics;
using PerfMonitorLive.UI;

namespace PerfMonitorLive
{
    public partial class App : Application
    {
        public Settings Settings { get; private set; }
        public AlertEngine Alerts { get; private set; }
        public ToastManager Toasts { get; private set; }
        public Sampler Sampler => _sampler;
        public GameDetector Games { get; private set; }
        Sampler _sampler; HistoryWriter _history; TrayIcon _tray; MainWindow _main; Mutex _mutex;
        OverlayWindow _overlay; WidgetWindow _widget;
        readonly DispatcherTimer _slow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        DateTime _lastHourly = DateTime.MinValue;
        Sample _last;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "PerfMonitorLive";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            _mutex = new Mutex(true, "PerfMonitorLive-SingleInstance", out bool first);
            var wake = new EventWaitHandle(false, EventResetMode.AutoReset, "PerfMonitorLive-Show");
            if (!first) { wake.Set(); Shutdown(); return; }
            bool trayOnly = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
            bool here = e.Args.Any(a => a.Equals("--here", StringComparison.OrdinalIgnoreCase));
            if (!trayOnly && !here && !Hardware.IsElevated && TaskExists())
            {
                _mutex.ReleaseMutex(); _mutex.Dispose(); _mutex = null;
                RunTask();
                for (int i = 0; i < 40; i++) { Thread.Sleep(250); if (Mutex.TryOpenExisting("PerfMonitorLive-SingleInstance", out var m)) { m.Dispose(); break; } }
                Thread.Sleep(300); wake.Set(); Shutdown(); return;
            }
            new Thread(() => { while (true) { wake.WaitOne(); Dispatcher.BeginInvoke(new Action(ShowMain)); } }) { IsBackground = true, Name = "WakeListener" }.Start();
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Paths.Log("Unhandled: " + a.ExceptionObject);
            DispatcherUnhandledException += (s, a) => { Paths.Log("UI: " + a.Exception); a.Handled = true; };

            Settings = Settings.Load();
            ApplyTheme();
            SystemEvents.UserPreferenceChanged += (s, a) => { if (a.Category == UserPreferenceCategory.General) Dispatcher.BeginInvoke(new Action(ApplyTheme)); };
            Alerts = new AlertEngine(Settings);
            Toasts = new ToastManager(Settings);
            Games = new GameDetector(Settings);
            Toasts.GameActive = () => Games.Active;
            _history = new HistoryWriter();
            _tray = new TrayIcon();
            _main = new MainWindow(this);

            Alerts.Alert += a => Dispatcher.BeginInvoke(new Action(() => OnAlert(a)));
            Toasts.ToastClicked += ShowMain;
            _tray.LeftClick += ToggleMain;
            _tray.OpenClicked += ShowMain;
            _tray.HistoryClicked += () => { ShowMain(); _main.ShowTab("Historique"); };
            _tray.ReportClicked += OpenReport;
            _tray.DigestClicked += () => ShowDigest(DateTime.Today.AddDays(-1), true);
            _tray.TestClicked += TestToast;
            _tray.WidgetToggled += () => { Settings.WidgetEnabled = !Settings.WidgetEnabled; Settings.Save(); OnSettingsChanged(); };
            _tray.ProfileSelected += name => { if (name == "Auto") { Settings.ProfileAuto = true; Settings.Save(); ApplyAutoProfile(); } else { Settings.ProfileAuto = false; SwitchProfile(name); } };
            _tray.PauseToggled += () => { TogglePause(); _main.UpdatePauseText(); };
            _tray.StartupToggled += () => { Settings.StartWithWindows = !Settings.StartWithWindows; Settings.Save(); OnSettingsChanged(); };
            _tray.QuitClicked += Quit;
            Games.Started += g => Dispatcher.BeginInvoke(new Action(() => OnGameStarted(g)));
            Games.Ended += sess => Dispatcher.BeginInvoke(new Action(() => OnGameEnded(sess)));
            ApplyStartup();

            _sampler = new Sampler();
            _sampler.SampleReady += s => Dispatcher.BeginInvoke(new Action(() => OnSample(s)));
            _sampler.Start();
            _slow.Tick += (s, a) => SlowTick();
            _slow.Start();

            // inventaire matériel (WMI, ~1-2 s) puis rapport de démarrage (journal Windows), en tâche de fond
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Inventory.Scan(); } catch (Exception ex) { Paths.Log("inventaire: " + ex.Message); }
                try { BootReport.Refresh(); var boots = BootReport.Load(); var last = boots.LastOrDefault(); Dispatcher.BeginInvoke(new Action(() => _main.SetLastBoot(last))); }
                catch (Exception ex) { Paths.Log("boot: " + ex.Message); }
            });

            if (!trayOnly) ShowMain();
            OnSettingsChanged();
            if (e.Args.Any(a => a.Equals("--digest", StringComparison.OrdinalIgnoreCase))) ShowDigest(DateTime.Today.AddDays(-1), true);
            Paths.Log("Démarrage (" + (trayOnly ? "tray" : "fenêtre") + ")" + (Hardware.IsElevated ? " admin" : ""));
        }

        // ---------------------------------------------------------------- boucle
        int _tick;
        void OnSample(Sample s)
        {
            _last = s;
            s.GameActive = Games.Active;
            Alerts.OnSample(s);
            _history.OnSample(s);
            _main.OnSample(s);
            if (_tick < 600 && Inventory.UpdateCapabilities(s, Hardware.IsElevated)) _main.RefreshMachine();
            _overlay?.Update(s);
            if (_widget != null && _widget.IsVisible) _widget.Update(s);
            if (++_tick % 2 == 0) _tray.Update(s.cpu, s.memPct, Alerts.ActiveRules.Count > 0);
        }
        /// <summary>Après un scan matériel : seuils par défaut adaptés (première installation) et cartes reconstruites.</summary>
        public void OnInventory(MachineInfo m)
        {
            if (Settings.ApplyMachine(m)) { Paths.Log("Seuils par défaut adaptés au matériel (" + m.KindText + ", CPU " + m.Cpu.Vendor + ", GPU " + m.GpuVendor + ")"); _main.RebuildCards(); OnSettingsChanged(); }
        }
        DateTime? _mainHiddenSince; bool _eco;
        /// <summary>Mode économie : 2 s entre deux mesures si l'option est active et (sur batterie, ou rien de visible depuis > 5 min).</summary>
        void UpdateEco()
        {
            bool visible = (_main != null && _main.IsVisible && _main.WindowState != WindowState.Minimized) || (_widget != null && _widget.IsVisible) || (_overlay != null && _overlay.IsVisible) || Games.Active;
            if (visible) _mainHiddenSince = null; else if (_mainHiddenSince == null) _mainHiddenSince = DateTime.Now;
            bool onBattery = false;
            try { onBattery = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline; } catch { }
            bool eco = Settings.EcoAuto && !visible && (onBattery || (_mainHiddenSince.HasValue && (DateTime.Now - _mainHiddenSince.Value).TotalMinutes >= 5));
            if (eco != _eco)
            {
                _eco = eco; _sampler.IntervalMs = eco ? 2000 : 1000;
                Paths.Log(eco ? "Mode économie : mesure toutes les 2 s" + (onBattery ? " (batterie)" : " (fenêtre fermée)") : "Mode normal : mesure toutes les 1 s");
            }
        }
        public bool EcoActive => _eco;

        // ---------------------------------------------------------------- installation automatique
        UpdateInfo _pendingUpdate; bool _installing; string _downloadedExe;
        DispatcherTimer _installTimer;
        /// <summary>Télécharge la mise à jour en attente, puis prévient (20 s, reportable) et remplace l'exe. Diffère tant qu'un jeu est actif.</summary>
        public async void InstallUpdate()
        {
            var info = _pendingUpdate; if (info == null || _installing) return;
            _installing = true;
            try
            {
                if (_downloadedExe == null || !System.IO.File.Exists(_downloadedExe))
                {
                    _main.SetUpdateStatus("Téléchargement de la " + info.Version + "…", null);
                    var prog = new Progress<double>(p => _main.SetUpdateStatus("Téléchargement de la " + info.Version + "… " + p.ToString("0") + " %", null));
                    _downloadedExe = await Updater.DownloadAsync(info, prog);
                    Paths.Log("MAJ " + info.Version + " téléchargée : " + _downloadedExe);
                }
                if (Games.Active) { _main.SetUpdateStatus("Mise à jour " + info.Version + " téléchargée : installation à la fin du jeu.", null); _installing = false; ScheduleInstallRetry(); return; }
                int left = 20;
                var a = new AlertInfo { RuleId = "update", Time = DateTime.Now, Severity = Severity.Info, Title = "Mise à jour " + info.Version + " prête", Detail = "PerfMonitor va se relancer dans 20 s pour installer la nouvelle version (réglages et historique conservés)." };
                bool later = false;
                a.Actions.Add(new ToastAction { Label = "Maintenant", Primary = true, Run = () => { _installTimer?.Stop(); ApplyUpdate(); } });
                a.Actions.Add(new ToastAction { Label = "Plus tard", Run = () => { later = true; _installTimer?.Stop(); _installing = false; _main.SetUpdateStatus("Mise à jour " + info.Version + " téléchargée : elle s'installera à la prochaine vérification (ou bouton « Installer maintenant »).", null); ScheduleInstallRetry(); } });
                Toasts.Show(a, true);
                _main.SetUpdateStatus("Mise à jour " + info.Version + " téléchargée : relance dans 20 s…", null);
                _installTimer?.Stop();
                _installTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _installTimer.Tick += (s, e) => { if (later) { _installTimer.Stop(); return; } if (--left <= 0) { _installTimer.Stop(); ApplyUpdate(); } else _main.SetUpdateStatus("Mise à jour " + info.Version + " téléchargée : relance dans " + left + " s…", null); };
                _installTimer.Start();
            }
            catch (Exception ex)
            {
                Paths.Log("MAJ install: " + ex.Message);
                _main.SetUpdateStatus("Installation impossible : " + ex.Message + " — tu peux télécharger à la main.", info.Url);
                _installing = false;
            }
        }
        DispatcherTimer _installRetry;
        void ScheduleInstallRetry()
        {   // nouvel essai dans 1 h (ou dès la prochaine vérification manuelle)
            _installRetry?.Stop();
            _installRetry = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            _installRetry.Tick += (s, e) => { _installRetry.Stop(); if (_pendingUpdate != null && !Games.Active) InstallUpdate(); else if (_pendingUpdate != null) ScheduleInstallRetry(); };
            _installRetry.Start();
        }
        void ApplyUpdate()
        {
            try
            {
                Settings.Save();
                Paths.Log("MAJ : remplacement de l'exe et relance (" + _pendingUpdate?.Version + ")");
                Updater.LaunchReplace(_downloadedExe, TaskExists());
                Quit();
            }
            catch (Exception ex) { Paths.Log("MAJ apply: " + ex.Message); _main.SetUpdateStatus("Relance impossible : " + ex.Message, _pendingUpdate?.Url); _installing = false; }
        }

        // ---------------------------------------------------------------- mises à jour
        bool _updateChecking;
        void MaybeCheckUpdate()
        {
            if (!Settings.UpdateAuto || _updateChecking) return;
            if (Settings.LastUpdateCheck.HasValue && (DateTime.Now - Settings.LastUpdateCheck.Value).TotalHours < 24) return;
            CheckUpdate(false);
        }
        /// <summary>Vérifie la dernière Release GitHub ; notifie si plus récente. manual = déclenché par l'utilisateur (résultat affiché même si à jour).</summary>
        public async void CheckUpdate(bool manual)
        {
            if (_updateChecking) return; _updateChecking = true;
            _main.SetUpdateStatus("Vérification…", null);
            try
            {
                var info = await Updater.CheckAsync();
                Settings.LastUpdateCheck = DateTime.Now; Settings.LastUpdateVersion = info.Version; Settings.Save();
                if (info.IsNewer)
                {
                    _pendingUpdate = info;
                    _main.SetUpdateStatus("Nouvelle version disponible : " + info.Version + " (installée : " + Updater.CurrentVersion + ").", info.Url);
                    _main.SetUpdateInstallable(true);
                    if (Settings.UpdateAutoInstall) InstallUpdate();
                    else
                    {
                        var a = new AlertInfo { RuleId = "update", Time = DateTime.Now, Severity = Severity.Info, Title = "PerfMonitor " + info.Version + " est disponible", Detail = "Tu utilises la " + Updater.CurrentVersion + ". Réglages et historique sont conservés lors de la mise à jour." };
                        a.Actions.Add(new ToastAction { Label = "Installer", Primary = true, Run = InstallUpdate });
                        Toasts.Show(a, true);
                    }
                }
                else { _pendingUpdate = null; _main.SetUpdateInstallable(false); _main.SetUpdateStatus("À jour (" + Updater.CurrentVersion + "). Vérifié le " + DateTime.Now.ToString("dd/MM à HH:mm") + ".", null); }
            }
            catch (Exception ex)
            {
                Paths.Log("MAJ: " + ex.Message);
                _main.SetUpdateStatus(manual ? "Vérification impossible (" + (ex.Message.Length > 80 ? ex.Message.Substring(0, 80) : ex.Message) + "). Le dépôt existe-t-il déjà et as-tu accès à Internet ?" : "", manual ? Updater.ReleasesUrl : null);
                if (!manual) { Settings.LastUpdateCheck = DateTime.Now; Settings.Save(); }   // pas de retentative en boucle
            }
            finally { _updateChecking = false; }
        }
        void SlowTick()
        {
            try
            {
                UpdateEco();
                Games.Tick(_last);
                if (Settings.ProfileAuto) ApplyAutoProfile();
                if ((DateTime.Now - _lastHourly).TotalMinutes >= 10) { _lastHourly = DateTime.Now; MaybeDigest(); MaybeCheckUpdate(); }
            }
            catch (Exception ex) { Paths.Log("slow: " + ex.Message); }
        }

        // ---------------------------------------------------------------- alertes → toasts / actions / Telegram
        void OnAlert(AlertInfo a)
        {
            AddActions(a);
            Toasts.Show(a);
            if (a.Severity == Severity.Critical && Settings.TelegramCritical && TelegramSender.Configured(Settings))
                TelegramSender.Fire(Settings, "🚨 " + a.Title + "\n" + a.Detail);
        }
        static readonly string[] Protected = { "System", "Registry", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer", "smss", "fontdrvhost", "PerfMonitorLive", "MsMpEng", "audiodg" };
        void AddActions(AlertInfo a)
        {
            var m = a.RuleId.Split(':')[0];
            if (a.Severity == Severity.Ok || a.Severity == Severity.Info) return;
            if (m == "cpu" || m == "memPct" || m == "pageIn")
            {
                a.Actions.Add(new ToastAction { Label = "Voir les processus", Run = () => { ShowMain(); _main.ShowTab("Processus"); } });
                if (!string.IsNullOrEmpty(a.Proc) && !Protected.Contains(a.Proc, StringComparer.OrdinalIgnoreCase))
                {
                    var name = a.Proc;
                    a.Actions.Add(new ToastAction { Label = "Terminer " + name, Run = () => KillProcess(name) });
                }
            }
            else if (m == "disk.lat" || m == "disk.pct" || m == "disk.rw")
                a.Actions.Add(new ToastAction { Label = "Optimiser les lecteurs", Run = () => Start("dfrgui.exe", "") });
            else if (m == "reboot")
                a.Actions.Add(new ToastAction { Label = "Redémarrer maintenant", Primary = true, Run = RebootNow });
            else if (m == "temp" || m == "gpuTemp" || m == "stor.temp")
                a.Actions.Add(new ToastAction { Label = "Voir les conseils", Run = () => { ShowMain(); _main.ShowTab("Live"); _main.AdvisorAsk(); } });
            else if (m == "stor.err" || m == "stor.health")
                a.Actions.Add(new ToastAction { Label = "Ouvrir la gestion des disques", Run = () => Start("diskmgmt.msc", "") });
        }
        void KillProcess(string name)
        {
            var r = MessageBox.Show("Terminer tous les processus « " + name + " » ? Les données non enregistrées de ce programme seront perdues.", "PerfMonitor Live", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            int n = 0;
            foreach (var p in Process.GetProcessesByName(name)) { try { p.Kill(); n++; } catch (Exception ex) { Paths.Log("kill: " + ex.Message); } }
            Toasts.Show(new AlertInfo { RuleId = "kill", Severity = Severity.Info, Time = DateTime.Now, Title = n > 0 ? name + " terminé (" + n + ")" : "Impossible de terminer " + name, Detail = n > 0 ? "La charge devrait redescendre dans quelques secondes." : "Droits insuffisants ou processus déjà fermé." }, true);
        }
        void RebootNow()
        {
            var r = MessageBox.Show("Redémarrer le PC dans 60 secondes ? Enregistre ton travail. Tu pourras annuler depuis la notification.", "PerfMonitor Live", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            Start("shutdown.exe", "/r /t 60 /c \"PerfMonitor Live : redémarrage demandé\"");
            var a = new AlertInfo { RuleId = "reboot.go", Severity = Severity.Info, Time = DateTime.Now, Title = "Redémarrage dans 60 s", Detail = "Enregistre ton travail. Clique sur « Annuler » pour tout arrêter." };
            a.Actions.Add(new ToastAction { Label = "Annuler le redémarrage", Primary = true, Run = () => Start("shutdown.exe", "/a") });
            Toasts.Show(a, true);
        }
        static void Start(string file, string args) { try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); } catch (Exception ex) { Paths.Log("start " + file + ": " + ex.Message); } }

        // ---------------------------------------------------------------- jeu / overlay / widget
        void OnGameStarted(string game)
        {
            Paths.Log("Jeu détecté : " + game);
            if (Settings.OverlayEnabled)
            {
                try { _overlay = new OverlayWindow(() => Toasts.TargetScreen()); _overlay.SetGame(game); _overlay.Show(); if (_last != null) _overlay.Update(_last); }
                catch (Exception ex) { Paths.Log("overlay: " + ex.Message); }
            }
            if (Settings.ProfileAuto) ApplyAutoProfile();
        }
        void OnGameEnded(GameSession sess)
        {
            try { _overlay?.Close(); } catch { } _overlay = null;
            if (Settings.ProfileAuto) ApplyAutoProfile();
            Toasts.Show(new AlertInfo
            {
                RuleId = "session", Severity = Severity.Info, Time = DateTime.Now,
                Title = "Session " + sess.game + " : " + Math.Round(sess.min) + " min",
                Detail = "CPU " + sess.cpuAvg + " % moy / " + sess.cpuMax + " % max · CPU " + sess.tempMax + " °C max · GPU " + sess.gpuMax + " °C max · RAM " + sess.memMax + " % max"
            }, true);
        }
        public void ResetWidget() { EnsureWidget(); _widget?.ResetPosition(); }
        void EnsureWidget()
        {
            if (!Settings.WidgetEnabled) { if (_widget != null) _widget.Hide(); return; }
            if (_widget == null)
            {
                _widget = new WidgetWindow(Settings, () => Toasts.TargetScreen(), ShowMain);
                _widget.HiddenByUser += () => { _tray.SetWidget(false); _main.SettingsVm.RaiseAll(); };
            }
            _widget.ApplyOpacity();
            if (!_widget.IsVisible) _widget.Show();
            if (_last != null) _widget.Update(_last);
        }
        public static string ForegroundProcessName()
        {
            try { var h = GetForegroundWindow(); GetWindowThreadProcessId(h, out uint pid); using (var p = Process.GetProcessById((int)pid)) return p.ProcessName; } catch { return null; }
        }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        // ---------------------------------------------------------------- profils
        public void SwitchProfile(string name)
        {
            if (name == Settings.ActiveProfile) { _tray.SetProfile(name, Settings.ProfileAuto); return; }
            Settings.SwitchProfile(name);
            _main.RebuildCards(); _main.UpdateProfilePill();
            _tray.SetProfile(name, Settings.ProfileAuto);
        }
        public void ApplyAutoProfile()
        {
            var want = Settings.AutoProfile(Games.Active, DateTime.Now);
            if (want != Settings.ActiveProfile) SwitchProfile(want); else _tray.SetProfile(want, Settings.ProfileAuto);
            _main.UpdateProfilePill();
        }

        // ---------------------------------------------------------------- bilan quotidien
        void MaybeDigest()
        {
            if (!Settings.DigestEnabled) return;
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            if (Settings.LastDigestDate == today || DateTime.Now.Hour < 7) return;
            Settings.LastDigestDate = today; Settings.Save();
            ShowDigest(DateTime.Today.AddDays(-1), false);
        }
        public void ShowDigest(DateTime day, bool manual)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var (title, body) = DailyDigest.Build(day);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (title == null) { if (manual) Toasts.Show(new AlertInfo { RuleId = "digest", Severity = Severity.Info, Time = DateTime.Now, Title = "Pas assez de données pour le " + day.ToString("dd/MM"), Detail = "L'historique de ce jour est vide ou trop court." }, true); return; }
                    var a = new AlertInfo { RuleId = "digest", Severity = Severity.Info, Time = DateTime.Now, Title = "📋 " + title, Detail = body };
                    a.Actions.Add(new ToastAction { Label = "Voir l'historique", Run = () => { ShowMain(); _main.ShowTab("Historique"); } });
                    Toasts.Show(a, true);
                    if (Settings.TelegramDigest && TelegramSender.Configured(Settings)) TelegramSender.Fire(Settings, "📋 " + title + "\n" + body);
                }));
            });
        }

        // ---------------------------------------------------------------- thème
        public void ApplyTheme()
        {
            bool light = Settings.Theme == "Light" || (Settings.Theme == "Auto" && WindowsUsesLightTheme());
            var uri = new Uri(light ? "UI/Themes/Light.xaml" : "UI/Themes/Dark.xaml", UriKind.Relative);
            var dict = new ResourceDictionary { Source = uri };
            Resources.MergedDictionaries.Clear(); Resources.MergedDictionaries.Add(dict);
            Palette.Light = light;
        }
        static bool WindowsUsesLightTheme()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) return k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 0)) == 1; } catch { return false; }
        }

        // ---------------------------------------------------------------- réglages / démarrage
        public void OnSettingsChanged()
        {
            ApplyStartup(); _tray.SetPaused(Settings.IsPaused); _tray.SetWidget(Settings.WidgetEnabled); _tray.SetProfile(Settings.ActiveProfile, Settings.ProfileAuto);
            ApplyTheme(); EnsureWidget();
            _main?.SettingsChangedHook();
        }

        const string TaskName = "PerfMonitorLive";
        static bool TaskExists()
        {
            try { var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/query /tn " + TaskName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }); p.WaitForExit(5000); return p.ExitCode == 0; }
            catch { return false; }
        }
        static int Schtasks(string args)
        {
            try { var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }); p.WaitForExit(15000); return p.ExitCode; }
            catch (Exception ex) { Paths.Log("schtasks: " + ex.Message); return -1; }
        }
        static void RunTask() => Schtasks("/run /tn " + TaskName);
        void ApplyStartup()
        {
            try
            {
                if (Hardware.IsElevated)
                {
                    if (Settings.StartWithWindows) Schtasks("/create /f /tn " + TaskName + " /sc onlogon /rl highest /it /tr \"\\\"" + Environment.ProcessPath + "\\\" --tray\"");
                    else if (TaskExists()) Schtasks("/delete /f /tn " + TaskName);
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
                }
                else if (!TaskExists())
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    {
                        if (Settings.StartWithWindows) k.SetValue(RunName, "\"" + Environment.ProcessPath + "\" --tray");
                        else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
                    }
                }
            }
            catch (Exception ex) { Paths.Log("Startup: " + ex.Message); }
            _tray.SetStartup(Settings.StartWithWindows);
        }

        public void TogglePause()
        {
            Settings.PausedUntil = Settings.IsPaused ? (DateTime?)null : DateTime.Now.AddHours(1);
            Settings.Save(); _tray.SetPaused(Settings.IsPaused);
            if (Settings.IsPaused) Toasts.CloseAll();
        }
        public void TestToast()
        {
            var scr = Toasts.TargetScreen();
            var a = new AlertInfo { RuleId = "test", Time = DateTime.Now, Severity = Severity.Info, Title = "Notification de test", Detail = "Affichée sur " + scr.DeviceName.Replace(@"\\.\", "") + " (" + scr.Bounds.Width + "×" + scr.Bounds.Height + "). Cliquer ouvre la fenêtre." };
            a.Actions.Add(new ToastAction { Label = "Exemple d'action", Run = () => { } });
            Toasts.Show(a, true);
        }
        public void OpenReport()
        {
            try { Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + Paths.ReportScript + "\" -Hours 24") { WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false, CreateNoWindow = true }); }
            catch (Exception ex) { Paths.Log("Report: " + ex.Message); }
        }
        void ShowMain()
        {
            if (_eco) { _eco = false; _sampler.IntervalMs = 1000; }
            ShowMainCore();
            if (!Settings.WelcomeShown) Dispatcher.BeginInvoke(new Action(() => _main.ShowWelcome()), DispatcherPriority.ApplicationIdle);
        }
        void ShowMainCore() { _main.Show(); if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal; _main.Activate(); }
        void ToggleMain() { if (_main.IsVisible && _main.WindowState != WindowState.Minimized) _main.Hide(); else ShowMain(); }
        void Quit()
        {
            Games.ForceEnd();
            _slow.Stop(); _sampler?.Dispose(); Toasts?.CloseAll(); try { _overlay?.Close(); _widget?.Close(); } catch { } _tray?.Dispose(); _main?.ForceClose();
            Shutdown();
        }
        protected override void OnExit(ExitEventArgs e) { _tray?.Dispose(); _mutex?.Dispose(); base.OnExit(e); }
    }
}
