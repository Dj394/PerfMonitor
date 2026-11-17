using System.Windows;
using System.Windows.Interop;

namespace PerfMonitorLive.UI
{
    /// <summary>Rendu logiciel pour les petites fenêtres (overlay, widget, notifications) : tant que la fenêtre principale
    /// n'a pas été ouverte, WPF n'a alors pas besoin de créer de périphérique Direct3D, dont l'initialisation par le pilote
    /// graphique coûte ~100 Mo de mémoire privée et ~1 600 handles (mesuré sur Radeon RX 7900 XT) — précisément pendant les jeux,
    /// moment où l'overlay apparaît. Ces fenêtres sont minuscules et rafraîchies au plus une fois par seconde : le rendu logiciel y est négligeable.</summary>
    public static class SoftwareRender
    {
        public static void Apply(Window w)
        {
            w.SourceInitialized += (s, e) =>
            {
                try { if (PresentationSource.FromVisual(w) is HwndSource src) src.CompositionTarget.RenderMode = RenderMode.SoftwareOnly; } catch { }
            };
        }
    }
}
