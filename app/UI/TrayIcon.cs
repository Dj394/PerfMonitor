using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using WF = System.Windows.Forms;

namespace PerfMonitorLive.UI
{
    /// <summary>Icône de zone de notification redessinée avec le % CPU ; clic gauche = fenêtre live.</summary>
    public class TrayIcon : IDisposable
    {
        readonly WF.NotifyIcon _icon = new WF.NotifyIcon();
        IntPtr _hIcon = IntPtr.Zero;
        readonly WF.ToolStripMenuItem _pause, _startup, _widget, _profAuto, _profTravail, _profJeu, _profNuit;
        public event Action LeftClick, OpenClicked, HistoryClicked, ReportClicked, PauseToggled, StartupToggled, QuitClicked, TestClicked, WidgetToggled, DigestClicked;
        public event Action<string> ProfileSelected;   // "Auto" | "Travail" | "Jeu" | "Nuit"

        public TrayIcon()
        {
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Ouvrir PerfMonitor Live", null, (s, e) => OpenClicked?.Invoke());
            menu.Items.Add("Historique (graphiques dans l'appli)", null, (s, e) => HistoryClicked?.Invoke());
            menu.Items.Add("Rapport HTML 24 h", null, (s, e) => ReportClicked?.Invoke());
            menu.Items.Add("Bilan de la veille", null, (s, e) => DigestClicked?.Invoke());
            menu.Items.Add(new WF.ToolStripSeparator());
            var prof = new WF.ToolStripMenuItem("Profil de seuils");
            _profAuto = new WF.ToolStripMenuItem("Automatique (jeu / nuit / travail)", null, (s, e) => ProfileSelected?.Invoke("Auto"));
            _profTravail = new WF.ToolStripMenuItem("Travail", null, (s, e) => ProfileSelected?.Invoke("Travail"));
            _profJeu = new WF.ToolStripMenuItem("Jeu (critiques seulement)", null, (s, e) => ProfileSelected?.Invoke("Jeu"));
            _profNuit = new WF.ToolStripMenuItem("Nuit", null, (s, e) => ProfileSelected?.Invoke("Nuit"));
            prof.DropDownItems.AddRange(new WF.ToolStripItem[] { _profAuto, new WF.ToolStripSeparator(), _profTravail, _profJeu, _profNuit });
            menu.Items.Add(prof);
            _widget = new WF.ToolStripMenuItem("Widget flottant", null, (s, e) => WidgetToggled?.Invoke());
            menu.Items.Add(_widget);
            menu.Items.Add("Tester une notification", null, (s, e) => TestClicked?.Invoke());
            _pause = new WF.ToolStripMenuItem("Mettre les notifications en pause (1 h)", null, (s, e) => PauseToggled?.Invoke());
            menu.Items.Add(_pause);
            menu.Items.Add(new WF.ToolStripSeparator());
            _startup = new WF.ToolStripMenuItem("Démarrer avec Windows", null, (s, e) => StartupToggled?.Invoke());
            menu.Items.Add(_startup);
            menu.Items.Add("Quitter", null, (s, e) => QuitClicked?.Invoke());
            _icon.ContextMenuStrip = menu;
            _icon.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) LeftClick?.Invoke(); };
            _icon.Text = "PerfMonitor Live";
            Update(0, 0, false);
            _icon.Visible = true;
        }

        public void SetPaused(bool paused) => _pause.Text = paused ? "Reprendre les notifications" : "Mettre les notifications en pause (1 h)";
        public void SetStartup(bool on) => _startup.Checked = on;
        public void SetWidget(bool on) => _widget.Checked = on;
        public void SetProfile(string active, bool auto)
        {
            _profAuto.Checked = auto; _profTravail.Checked = !auto && active == "Travail"; _profJeu.Checked = !auto && active == "Jeu"; _profNuit.Checked = !auto && active == "Nuit";
            _profAuto.Text = auto ? "Automatique — actuellement « " + active + " »" : "Automatique (jeu / nuit / travail)";
        }

        public void Update(double cpu, double mem, bool alert)
        {
            var text = "PerfMonitor Live\nCPU " + Math.Round(cpu) + " %  ·  RAM " + Math.Round(mem) + " %";
            if (text.Length > 63) text = text.Substring(0, 63);
            _icon.Text = text;
            Color bg = alert ? Color.FromArgb(0xE5, 0x3E, 0x3E) : cpu >= 85 ? Color.FromArgb(0xE0, 0x7A, 0x1F) : cpu >= 60 ? Color.FromArgb(0xC9, 0xA2, 0x27) : Color.FromArgb(0x2E, 0x8B, 0x57);
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(bg)) g.FillEllipse(b, 1, 1, 30, 30);
                var s = Math.Round(cpu).ToString();
                using (var f = new Font("Segoe UI", s.Length > 2 ? 10 : 12, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(s, f, Brushes.White, new RectangleF(0, 1, 32, 30), sf);
                var h = bmp.GetHicon();
                var old = _icon.Icon; var oldH = _hIcon;
                _icon.Icon = Icon.FromHandle(h); _hIcon = h;
                old?.Dispose(); if (oldH != IntPtr.Zero) DestroyIcon(oldH);
            }
        }

        public void Balloon(string title, string text) { try { _icon.ShowBalloonTip(3000, title, text, WF.ToolTipIcon.Info); } catch { } }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        public void Dispose() { _icon.Visible = false; _icon.Dispose(); if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon); }
    }
}
