using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using PerfMonitorLive.Metrics;

namespace PerfMonitorLive.UI
{
    /// <summary>Petit guide affiché à la première ouverture de la fenêtre : 5 points, pas plus.</summary>
    public partial class WelcomeWindow : Window
    {
        public bool GoToMachine { get; private set; }
        public bool ShowNextTime => ShowAgain.IsChecked == true;

        public WelcomeWindow(MachineInfo m, string alertScreen)
        {
            InitializeComponent();
            bool scanned = m != null && m.Cpu != null && m.Cpu.Name != "?";
            Sub.Text = scanned ? "Détecté : " + m.Cpu.Short + (m.MainGpu != null ? " · " + m.MainGpu.Short : "") + " · " + m.RamText + " · " + m.Disks.Count + " disque(s)" + (m.Laptop ? " · portable" : "") : "Analyse de la machine en cours…";
            Point("⚡", "Onglet Live", "une carte par mesure (processeur, mémoire, températures, disques…). Vert = normal, ambre = ça monte, rouge = seuil dépassé. Le seuil se règle directement sur la carte.");
            Point("🔔", "Alertes", "quand un seuil est dépassé assez longtemps, une notification apparaît " + (string.IsNullOrEmpty(alertScreen) ? "en bas à droite" : alertScreen) + " avec des boutons d'action. Cliquer dessus ouvre la fenêtre. Réglable dans Paramètres.");
            Point("🧭", "Le conseiller", "la petite mascotte se déplace vers la carte concernée et explique quoi faire (BIOS, Windows, pilotes) — adapté à cette machine. Clique dessus pour un conseil, « ne plus montrer » pour l'écarter.");
            Point("📈", "Historique et Processus", "courbes sur 1 h à 7 jours, démarrages Windows, sessions de jeu ; top des processus et détection des programmes qui fuient.");
            Point("🖥️", "Machine et Paramètres", "ce qui a été détecté et quels capteurs sont disponibles ; profils Travail / Jeu / Nuit, thème, widget, Telegram. Fermer la fenêtre ne quitte pas : PerfMonitor reste dans la zone de notification (icône avec le % CPU).");
        }

        void Point(string icon, string title, string text)
        {
            var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var i = new TextBlock { Text = icon, FontSize = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -1, 0, 0) };
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
            tb.Inlines.Add(new Run(title + " — ") { FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run(text));
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            Grid.SetColumn(tb, 1);
            g.Children.Add(i); g.Children.Add(tb);
            Points.Children.Add(g);
        }

        void Ok_Click(object s, RoutedEventArgs e) { DialogResult = true; Close(); }
        void Machine_Click(object s, RoutedEventArgs e) { GoToMachine = true; DialogResult = true; Close(); }
    }
}
