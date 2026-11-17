using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace PerfMonitorLive.UI
{
    public class Vm : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void Raise([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        /// <summary>N'assigne et ne notifie que si la valeur change (égalité de valeur, chaînes comprises ; pinceaux : même instance grâce au cache de <see cref="Palette"/>).</summary>
        protected bool Set<T>(ref T f, T v, [CallerMemberName] string n = null) { if (EqualityComparer<T>.Default.Equals(f, v)) return false; f = v; Raise(n); return true; }
    }

    /// <summary>Palette partagée (fenêtre + notifications).</summary>
    public static class Palette
    {
        public static readonly Color Good = Color.FromRgb(0x3D, 0xD6, 0x8C);     // vert
        public static readonly Color Warn = Color.FromRgb(0xF5, 0xB7, 0x3D);     // ambre
        public static readonly Color Bad = Color.FromRgb(0xFF, 0x5D, 0x6C);      // rouge corail
        public static readonly Color Neutral = Color.FromRgb(0x5A, 0xB4, 0xFF);  // bleu
        public static readonly Color Muted = Color.FromRgb(0x8C, 0x97, 0xA8);
        public static bool Light;
        public static Color Grid => Light ? Color.FromRgb(0xD9, 0xDF, 0xE8) : Color.FromRgb(0x27, 0x30, 0x3C);
        // Pinceaux gelés partagés, un par couleur ARGB : la même couleur renvoie la même instance,
        // donc Set() (comparaison par référence pour Brush) ne notifie plus à chaque mesure.
        // Le thème change Palette.Grid (autre couleur → autre pinceau) : la notification a bien lieu.
        static readonly ConcurrentDictionary<uint, SolidColorBrush> _brushes = new ConcurrentDictionary<uint, SolidColorBrush>();
        public static SolidColorBrush B(Color c) => B(c, c.A);
        public static SolidColorBrush B(Color c, byte a)
        {
            uint k = (uint)a << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B;
            if (_brushes.TryGetValue(k, out var b)) return b;
            b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze();
            return _brushes.GetOrAdd(k, b);
        }
    }

    /// <summary>Une carte métrique : valeur live, mini-graphique 5 min coloré selon la tendance, seuil éditable.</summary>
    public class MetricVm : Vm
    {
        public const int HistoryLen = 300; // 5 min à 1 s
        double W = 300, H = 58;
        public void Resize(double w, double h) { if (w < 20 || h < 10) return; if (Math.Abs(w - W) < 1 && Math.Abs(h - H) < 1) return; W = w; H = h; Raise(nameof(SparkW)); Refresh(); }
        public double SparkW => W;
        // Historique : tampon circulaire (plus de Queue + ToArray + LINQ à chaque mesure)
        readonly double[] _buf = new double[HistoryLen];
        int _head, _count;                       // _head = prochaine écriture
        readonly Point[] _pts = new Point[HistoryLen];   // points valides (réutilisés)
        readonly int[] _idx = new int[HistoryLen];       // index dans l'historique de chaque point
        double At(int i) => _buf[(_head - _count + i + HistoryLen) % HistoryLen];   // i : 0 = plus ancien
        static readonly Geometry EmptyGeom = MakeEmpty();
        static Geometry MakeEmpty() { var g = new StreamGeometry(); g.Freeze(); return g; }

        // Carte non affichée (fenêtre cachée/réduite, autre onglet) : l'historique continue d'être alimenté,
        // mais ni recalcul du rendu ni notification ; tout est rafraîchi d'un coup au retour (SetLive(true)).
        bool _quiet, _dirty;
        // Noms notifiés pendant le sommeil : seuls ceux-là sont renvoyés au réveil. Pas de notification globale
        // (string.Empty), qui rafraîchirait aussi Seuil/Durée et écraserait une saisie pas encore validée (LostFocus).
        readonly HashSet<string> _pending = new HashSet<string>();
        protected override void Raise([CallerMemberName] string n = null) { if (_quiet) { _dirty = true; if (!string.IsNullOrEmpty(n)) _pending.Add(n); return; } base.Raise(n); }
        public void SetLive(bool live)
        {
            if (live != _quiet) return;          // déjà dans cet état
            if (!live) { _quiet = true; return; }
            if (_dirty && _count > 0) Rebuild();   // encore silencieux : les propriétés modifiées sont mémorisées dans _pending
            _quiet = false;
            _dirty = false;
            if (_pending.Count == 0) return;
            var names = _pending.ToArray(); _pending.Clear();
            foreach (var n in names) base.Raise(n);
        }
        /// <summary>Recalcule le rendu (ex. changement de thème) ; différé si la carte n'est pas affichée.</summary>
        public void Invalidate() => Refresh();

        readonly Rule _rule; readonly Action _save;
        readonly double _fixedMax;
        /// <summary>false : un pourcentage élevé n'est pas un danger en soi (charge GPU à fond en jeu, VRAM remplie par le jeu) ;
        /// sans règle activée, la carte reste verte au lieu de la palette générique « ≥ 70 % ambre, ≥ 90 % rouge ».</summary>
        readonly bool _highIsBad;

        public MetricVm(string key, string title, string icon, Rule rule, Action save, double fixedMax = 0, bool highIsBad = true)
        { Key = key; Title = title; Icon = icon; _rule = rule; _save = save; _fixedMax = fixedMax; _highIsBad = highIsBad; }

        public string Key { get; }
        public string Title { get; }
        public string Icon { get; }
        public bool HasRule => _rule != null;
        public string Unit => _rule?.Unit ?? "";
        public bool IsPercent => _fixedMax == 100;

        string _value = "–", _sub = ""; bool _alert;
        public string ValueText { get => _value; set => Set(ref _value, value); }
        public string SubText { get => _sub; set => Set(ref _sub, value); }
        public bool IsAlert { get => _alert; set { if (Set(ref _alert, value)) Refresh(); } }

        // Couleur de niveau (vert / ambre / rouge selon la proximité du seuil)
        Brush _level = Palette.B(Palette.Neutral);
        public Brush LevelBrush { get => _level; set => Set(ref _level, value); }
        public Brush LevelSoft { get => _levelSoft; set => Set(ref _levelSoft, value); } Brush _levelSoft = Palette.B(Palette.Neutral, 0x33);
        public Brush CardBorder { get => _border; set => Set(ref _border, value); } Brush _border = Palette.B(Palette.Grid);

        // Tendance (delta sur 30 s)
        public string TrendText { get => _trend; set => Set(ref _trend, value); } string _trend = "";
        public Brush TrendBrush { get => _trendBrush; set => Set(ref _trendBrush, value); } Brush _trendBrush = Palette.B(Palette.Muted);
        public Brush TrendBg { get => _trendBg; set => Set(ref _trendBg, value); } Brush _trendBg = Brushes.Transparent;

        // Jauge (métriques en %) : découpe (Clip) arrondie d'une barre pleine largeur → rendu seul, pas d'invalidation de layout
        public Geometry GaugeClip { get => _gaugeClip; set => Set(ref _gaugeClip, value); } Geometry _gaugeClip = EmptyGeom;
        double _gaugeW = 0;
        public Visibility GaugeVisible => IsPercent ? Visibility.Visible : Visibility.Collapsed;

        // Géométries du mini-graphique
        public Geometry AreaGeom { get => _area; set => Set(ref _area, value); } Geometry _area = EmptyGeom;
        public Geometry FlatGeom { get => _flat; set => Set(ref _flat, value); } Geometry _flat = EmptyGeom;
        public Geometry UpGeom { get => _up; set => Set(ref _up, value); } Geometry _up = EmptyGeom;
        public Geometry DownGeom { get => _down; set => Set(ref _down, value); } Geometry _down = EmptyGeom;
        public double ThresholdY { get => _thrY; set => Set(ref _thrY, value); } double _thrY = -10;
        public Visibility ThresholdVisible => HasRule && Enabled ? Visibility.Visible : Visibility.Collapsed;

        public bool Enabled { get => _rule?.Enabled ?? false; set { if (_rule == null) return; _rule.Enabled = value; Raise(); Raise(nameof(ThresholdVisible)); _save(); Refresh(); } }
        public string Threshold
        {
            get => _rule == null ? "" : _rule.Threshold.ToString("0.#");
            set { if (_rule == null) return; if (double.TryParse(value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= 0) { _rule.Threshold = d; _save(); } Raise(); Refresh(); }
        }
        public string Sustain
        {
            get => _rule == null ? "" : _rule.SustainSec.ToString();
            set { if (_rule == null) return; if (int.TryParse(value, out var i) && i >= 0) { _rule.SustainSec = i; _save(); } Raise(); }
        }

        double? _last;
        void Refresh() { if (_count == 0) return; if (_quiet) _dirty = true; else Rebuild(); }

        /// <summary>Ajoute une valeur et recalcule couleurs, tendance et mini-graphique (recalcul différé si la carte n'est pas affichée).</summary>
        public void Push(double? v)
        {
            _last = v;
            _buf[_head] = v ?? double.NaN;
            _head = (_head + 1) % HistoryLen;
            if (_count < HistoryLen) _count++;
            if (_quiet) _dirty = true; else Rebuild();
        }

        void Rebuild()
        {
            int n = _count;
            // une passe : nombre de valeurs valides et maximum
            int valid = 0; double vmax = double.NegativeInfinity;
            for (int i = 0; i < n; i++) { double x = At(i); if (double.IsNaN(x)) continue; valid++; if (x > vmax) vmax = x; }
            double max = _fixedMax;
            if (max <= 0) { max = valid > 0 ? vmax : 0; if (_rule != null && _rule.Enabled) max = Math.Max(max, _rule.Threshold); max = max <= 0 ? 1 : max * 1.15; }

            // --- couleur de niveau
            Color level = Palette.Neutral;
            if (_last.HasValue && _rule != null && _rule.Enabled && _rule.Threshold > 0)
            {
                double ratio = _last.Value / _rule.Threshold;
                level = IsAlert || ratio >= 1 ? Palette.Bad : ratio >= 0.8 ? Palette.Warn : Palette.Good;
            }
            else if (_last.HasValue && IsPercent) level = IsAlert ? Palette.Bad : !_highIsBad ? Palette.Good : _last.Value >= 90 ? Palette.Bad : _last.Value >= 70 ? Palette.Warn : Palette.Good;
            LevelBrush = Palette.B(level); LevelSoft = Palette.B(level, 0x2E);
            CardBorder = IsAlert ? Palette.B(Palette.Bad, 0xAA) : Palette.B(Palette.Grid);
            double gw = IsPercent && _last.HasValue ? Math.Max(0, Math.Min(1, _last.Value / 100)) * W : 0;
            if (double.IsNaN(gw)) gw = 0;
            gw = Math.Round(gw);   // au pixel, comme l'arrondi de layout de l'ancienne Width : pas de nouvelle découpe pour un écart invisible
            if (gw != _gaugeW)
            {
                _gaugeW = gw;
                if (gw <= 0) GaugeClip = EmptyGeom;
                else { var clip = new RectangleGeometry(new Rect(0, 0, gw, 6), 3, 3); clip.Freeze(); GaugeClip = clip; }
            }

            // --- tendance sur 30 s
            if (valid >= 2 && _last.HasValue)
            {
                int back = Math.Min(30, n - 1);
                double prev = double.NaN; for (int i = n - 1 - back; i < n - 1 && double.IsNaN(prev); i++) if (i >= 0) prev = At(i);
                if (!double.IsNaN(prev))
                {
                    double d = _last.Value - prev, sig = max * 0.05; // variation notable = 5 % de l'échelle
                    if (Math.Abs(d) < sig || Math.Abs(d) < 0.05) { TrendText = "→ stable"; TrendBrush = Palette.B(Palette.Muted); TrendBg = Brushes.Transparent; }
                    else
                    {
                        string amount = _rule != null ? _rule.Format(Math.Abs(d)) : Math.Abs(d).ToString("0.#");
                        bool up = d > 0;
                        TrendText = (up ? "↑ +" : "↓ −") + amount + " / 30 s";
                        var c = up ? Palette.Bad : Palette.Good;
                        TrendBrush = Palette.B(c); TrendBg = Palette.B(c, 0x26);
                    }
                }
            }

            // --- géométries : aire + segments colorés selon la pente locale
            int m = 0; double step = W / (HistoryLen - 1);
            for (int i = 0; i < n; i++)
            {
                double v = At(i);
                if (double.IsNaN(v)) continue;
                _pts[m] = new Point(W - (n - 1 - i) * step, H - Math.Min(v, max) / max * (H - 4) - 2);
                _idx[m] = i; m++;
            }
            if (m >= 2)
            {
                var area = new StreamGeometry(); var flat = new StreamGeometry(); var up_ = new StreamGeometry(); var down = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new Point(_pts[0].X, H), true, true);
                    g.PolyLineTo(new ArraySegment<Point>(_pts, 0, m), false, false);
                    g.LineTo(new Point(_pts[m - 1].X, H), false, false);
                }
                double jump = max * (_fixedMax > 0 ? 0.12 : 0.30); // « brusque » = 12 % de l'échelle (métriques en %) ou 30 % (échelle dynamique) en ~5 s
                using (var gf = flat.Open()) using (var gu = up_.Open()) using (var gd = down.Open())
                {
                    // Les segments consécutifs de même couleur forment une seule figure (jointures rondes = mêmes pixels
                    // que des segments séparés à extrémités rondes).
                    int runColor = -1, runStart = 0;
                    for (int k = 1; k < m; k++)
                    {
                        int i = _idx[k]; double d = 0; int j = i - 5; if (j < 0) j = 0;
                        while (j < i && double.IsNaN(At(j))) j++;
                        if (j < i) d = At(i) - At(j);
                        int c = d >= jump ? 1 : d <= -jump ? 2 : 0;
                        if (c != runColor)
                        {
                            if (runColor >= 0) Figure(runColor == 1 ? gu : runColor == 2 ? gd : gf, runStart, k - 1);
                            runColor = c; runStart = k - 1;
                        }
                    }
                    if (runColor >= 0) Figure(runColor == 1 ? gu : runColor == 2 ? gd : gf, runStart, m - 1);
                }
                area.Freeze(); flat.Freeze(); up_.Freeze(); down.Freeze();
                AreaGeom = area; FlatGeom = flat; UpGeom = up_; DownGeom = down;
            }
            else { AreaGeom = EmptyGeom; FlatGeom = EmptyGeom; UpGeom = EmptyGeom; DownGeom = EmptyGeom; }
            ThresholdY = _rule != null && _rule.Enabled ? H - Math.Min(_rule.Threshold, max) / max * (H - 4) - 2 : -10;
        }

        /// <summary>Polyligne des points [from..to] (to &gt; from).</summary>
        void Figure(StreamGeometryContext g, int from, int to)
        {
            g.BeginFigure(_pts[from], false, false);
            g.PolyLineTo(new ArraySegment<Point>(_pts, from + 1, to - from), true, false);
        }
    }

    public class ProcVm : Vm
    {
        string _n, _delta = ""; double _cpu, _mem, _h; Brush _db = Palette.B(Palette.Muted);
        public string Name { get => _n; set => Set(ref _n, value); }
        public double Cpu { get => _cpu; set => Set(ref _cpu, value); }
        public double Mem { get => _mem; set => Set(ref _mem, value); }
        public double Handles { get => _h; set => Set(ref _h, value); }
        public string Delta { get => _delta; set => Set(ref _delta, value); }
        public Brush DeltaBrush { get => _db; set => Set(ref _db, value); }
    }

    /// <summary>Paramètres généraux exposés à la fenêtre (sauvegarde immédiate).</summary>
    public class SettingsVm : Vm
    {
        readonly Settings _s; readonly Action _changed;
        public SettingsVm(Settings s, Action changed) { _s = s; _changed = changed; }
        void Save() { _s.Save(); _changed?.Invoke(); }
        public Settings Model => _s;

        // notifications
        public string[] ScreenModes { get; } = { "Écran secondaire (par défaut)", "Écran principal", "Écran n°" };
        public int ScreenModeIndex
        {
            get => _s.ScreenMode == "Primary" ? 1 : _s.ScreenMode == "Index" ? 2 : 0;
            set { _s.ScreenMode = value == 1 ? "Primary" : value == 2 ? "Index" : "Secondary"; Save(); Raise(); Raise(nameof(ScreenIndexVisible)); }
        }
        public Visibility ScreenIndexVisible => _s.ScreenMode == "Index" ? Visibility.Visible : Visibility.Collapsed;
        public string[] ScreenNames { get; set; } = new string[0];
        public int ScreenIndex { get => _s.ScreenIndex; set { _s.ScreenIndex = value; Save(); Raise(); } }
        public string[] Corners { get; } = { "Bas droite", "Haut droite", "Bas gauche", "Haut gauche" };
        static readonly string[] CornerKeys = { "BottomRight", "TopRight", "BottomLeft", "TopLeft" };
        public int CornerIndex { get => Math.Max(0, Array.IndexOf(CornerKeys, _s.Corner)); set { _s.Corner = CornerKeys[Math.Max(0, Math.Min(3, value))]; Save(); Raise(); } }
        public string ToastSec { get => _s.ToastSec.ToString(); set { if (int.TryParse(value, out var i) && i >= 0) { _s.ToastSec = i; Save(); } Raise(); } }
        public string CooldownMin { get => (_s.CooldownSec / 60.0).ToString("0.#"); set { if (double.TryParse(value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= 0) { _s.CooldownSec = (int)(d * 60); Save(); } Raise(); } }
        public bool Sound { get => _s.Sound; set { _s.Sound = value; Save(); Raise(); } }
        public bool NotifyRecovery { get => _s.NotifyRecovery; set { _s.NotifyRecovery = value; Save(); Raise(); } }
        public bool StartWithWindows { get => _s.StartWithWindows; set { _s.StartWithWindows = value; Save(); Raise(); } }
        public bool ShowAdvisor { get => _s.ShowAdvisor; set { _s.ShowAdvisor = value; Save(); Raise(); } }
        public bool EcoAuto { get => _s.EcoAuto; set { _s.EcoAuto = value; Save(); Raise(); } }
        public bool UpdateAuto { get => _s.UpdateAuto; set { _s.UpdateAuto = value; Save(); Raise(); } }
        public bool UpdateAutoInstall { get => _s.UpdateAutoInstall; set { _s.UpdateAutoInstall = value; Save(); Raise(); } }
        public string VersionText => "Version installée : " + Updater.CurrentVersion;
        // profils
        public bool ProfileAuto { get => _s.ProfileAuto; set { _s.ProfileAuto = value; Save(); Raise(); } }
        public string NightStart { get => _s.NightStart.ToString(); set { if (int.TryParse(value, out var i) && i >= 0 && i <= 23) { _s.NightStart = i; Save(); } Raise(); } }
        public string NightEnd { get => _s.NightEnd.ToString(); set { if (int.TryParse(value, out var i) && i >= 0 && i <= 23) { _s.NightEnd = i; Save(); } Raise(); } }
        public string ActiveProfile => _s.ActiveProfile;
        // jeux / overlay / widget
        public bool GameAutoDetect { get => _s.GameAutoDetect; set { _s.GameAutoDetect = value; Save(); Raise(); } }
        public bool OverlayEnabled { get => _s.OverlayEnabled; set { _s.OverlayEnabled = value; Save(); Raise(); } }
        public bool WidgetEnabled { get => _s.WidgetEnabled; set { _s.WidgetEnabled = value; Save(); Raise(); } }
        public double WidgetOpacity { get => _s.WidgetOpacity; set { _s.WidgetOpacity = Math.Max(0.2, Math.Min(1, value)); Save(); Raise(); } }
        public string GamesText { get => string.Join(", ", _s.Games); set { _s.Games = (value ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().Replace(".exe", "")).Where(x => x.Length > 0).Distinct().ToList(); Save(); Raise(); } }
        // Telegram / résumé
        public string TelegramToken
        {
            get => string.IsNullOrEmpty(_s.TelegramTokenEnc) ? "" : Alerts.TelegramSender.Unprotect(_s.TelegramTokenEnc);
            set { _s.TelegramTokenEnc = string.IsNullOrWhiteSpace(value) ? null : Alerts.TelegramSender.Protect(value.Trim()); Save(); Raise(); }
        }
        public string TelegramChatId { get => _s.TelegramChatId ?? ""; set { _s.TelegramChatId = value?.Trim(); Save(); Raise(); } }
        public bool TelegramCritical { get => _s.TelegramCritical; set { _s.TelegramCritical = value; Save(); Raise(); } }
        public bool TelegramDigest { get => _s.TelegramDigest; set { _s.TelegramDigest = value; Save(); Raise(); } }
        public bool DigestEnabled { get => _s.DigestEnabled; set { _s.DigestEnabled = value; Save(); Raise(); } }
        // apparence
        public string[] Themes { get; } = { "Comme Windows", "Sombre", "Clair" };
        public int ThemeIndex { get => _s.Theme == "Dark" ? 1 : _s.Theme == "Light" ? 2 : 0; set { _s.Theme = value == 1 ? "Dark" : value == 2 ? "Light" : "Auto"; Save(); Raise(); } }
        public bool Compact { get => _s.Compact; set { _s.Compact = value; Save(); Raise(); } }
        public void RaiseAll() { foreach (var p in GetType().GetProperties()) Raise(p.Name); }
    }
}
