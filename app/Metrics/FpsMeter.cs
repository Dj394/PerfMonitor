using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace PerfMonitorLive.Metrics
{
    /// <summary>Images par seconde, mesurées <b>depuis l'extérieur du jeu</b>.
    ///
    /// Aucun overlay n'est injecté dans le jeu et aucun logiciel tiers n'est requis : on écoute la trace
    /// système ETW du fournisseur <c>Microsoft-Windows-DXGI</c> et on compte les événements <c>Present/Stop</c>,
    /// c'est-à-dire les images effectivement remises à l'affichage. C'est le principe de PresentMon (Intel/Microsoft).
    ///
    /// Conséquences à connaître :
    /// <list type="bullet">
    /// <item>Il faut les droits administrateur : une session ETW temps réel n'est pas ouvrable autrement.
    /// PerfMonitor tourne déjà élevé (tâche planifiée) pour les capteurs, donc c'est acquis.</item>
    /// <item>Seules les applications passant par DXGI sont vues, ce qui couvre Direct3D 10 à 12 —
    /// donc Apex et League. Un jeu purement Vulkan ou OpenGL ne compterait pas.</item>
    /// <item>Le débit est calculé à partir de l'<b>horodatage</b> des images (fenêtre glissante de 2 s), et non de
    /// leur heure d'arrivée : une session ETW temps réel livre les événements par lots (vidage des tampons ~1 fois/s),
    /// ce qui donnait alternativement ~0 puis le double quand la mesure et le vidage se décalaient.</item>
    /// <item>Sans image récente du PID cible (jeu D3D9/Vulkan/OpenGL, jeu masqué ou fermé…), la mesure vaut null
    /// (« — ») : jamais un 0 inventé.</item>
    /// <item>La session ETW n'est ouverte que pendant un jeu (PID cible non nul) et refermée ~10 s après :
    /// hors jeu, aucun thread, aucun tampon noyau, aucun événement à filtrer.</item>
    /// </list></summary>
    public class FpsMeter : IDisposable
    {
        /// <summary>PID dont on veut le débit d'images — le jeu au premier plan. 0 = personne, la mesure renvoie null.</summary>
        public Func<int> TargetPid = () => 0;

        public bool Available { get; private set; }
        public string Status { get; private set; } = "non démarré";

        static readonly Guid DxgiProvider = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        static readonly string SessionName = "PerfMonitorLive-FPS" + Paths.InstanceSuffix;

        // Manifeste Microsoft-Windows-DXGI (wevtutil gp Microsoft-Windows-DXGI /ge /gm, identique aux en-têtes de PresentMon) :
        // 42/43 = Present Start/Stop (tâche 9), 55/56 = PresentMultiplaneOverlay Start/Stop (tâche 14),
        // tous niveau 0 (toujours journalisés), mot-clé 0x2 « Events » + bit de canal Analytic 0x8000000000000000.
        // Une image remise = un Stop : seuls 43 et 56 sont demandés au noyau.
        const int PresentStop = 43, PresentMpoStop = 56;
        const ulong DxgiKeywords = 0x8000000000000002UL;
        /// <summary>Délai avant de refermer la session quand le jeu disparaît (alt-tab, écran de chargement…).</summary>
        static readonly TimeSpan CloseAfter = TimeSpan.FromSeconds(10);
        /// <summary>Après un échec d'ouverture, on ne réessaie pas avant ce délai ; au-delà de 3 échecs on abandonne.</summary>
        static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

        readonly object _gate = new object();
        TraceEventSession _session;      // sous _gate
        Thread _thread;                  // sous _gate : non null tant qu'une session est ouverte ou en cours d'ouverture/fermeture
        bool _stopRequested;             // sous _gate
        volatile bool _live;             // fournisseur activé, les images arrivent
        volatile bool _disposed;
        volatile int _target = -1;       // PID compté par le rappel ETW (-1 = personne)
        readonly FrameWindow _window = new FrameWindow();   // horodatages des dernières images du PID cible
        int _failures; DateTime _retryAt = DateTime.MinValue;

        // état de lecture (thread de mesure uniquement)
        int _lastPid;
        DateTime _lastWanted;
        /// <summary>Largeur de la fenêtre glissante, et délai sans nouvelle image au-delà duquel on ne publie plus rien.
        /// Le délai couvre le vidage des tampons ETW (~1 s) et la cadence de mesure.</summary>
        const double WindowMs = 2000, StaleMs = 3000;

        public void Start()
        {
            if (TraceEventSession.IsElevated() != true) { Status = "droits administrateur requis"; return; }
            Available = true; Status = "en attente d'un jeu";
        }

        /// <summary>Rappel ETW : appelé pour chaque événement remis. Ni allocation ni chaîne — uniquement des nombres.</summary>
        void OnEvent(TraceEvent ev)
        {
            int pid = ev.ProcessID;
            if (pid != _target) return;
            int id = (int)ev.ID;
            if (id != PresentStop && id != PresentMpoStop) return;
            if (ev.ProviderGuid != DxgiProvider) return;
            _window.Add(pid, ev.TimeStampRelativeMSec);   // horodatage de l'image (QPC de l'événement), pas l'heure de livraison
        }

        void EnsureSession(DateTime now)
        {
            lock (_gate)
            {
                if (_disposed || _thread != null || now < _retryAt) return;
                _stopRequested = false;
                _thread = new Thread(Loop) { IsBackground = true, Name = "FpsMeter" };
                _thread.Start();
            }
        }

        void StopSession()
        {
            TraceEventSession s;
            lock (_gate)
            {
                if (_thread == null || _stopRequested) return;
                _stopRequested = true;
                s = _session;
            }
            _live = false;
            try { s?.Dispose(); } catch { }   // StopOnDispose : Process() rend la main, le thread se termine
        }

        void Loop()
        {
            TraceEventSession s = null;
            bool opened = false;
            try
            {
                // Une session ETW du même nom peut subsister après un arrêt brutal : TraceEventSession (option Create par défaut)
                // l'arrête et en recrée une, et StopOnDispose garantit qu'on ne laisse rien derrière nous.
                // 4 Mo suffisent largement (quelques centaines d'événements par seconde une fois filtrés) ; le défaut est 64 Mo.
                s = new TraceEventSession(SessionName) { StopOnDispose = true, BufferSizeMB = 4 };
                lock (_gate) { if (_disposed || _stopRequested) return; }
                s.Source.AllEvents += OnEvent;   // aucun parseur enregistré : pas de décodage de manifeste, pas de nom de tâche
                try
                {
                    var opts = new TraceEventProviderOptions { EventIDsToEnable = new List<int> { PresentStop, PresentMpoStop } };
                    s.EnableProvider(DxgiProvider, TraceEventLevel.Informational, DxgiKeywords, opts);
                }
                catch (Exception fx)
                {   // filtre par identifiant refusé (très vieux Windows) : sans filtre noyau, le rappel trie de toute façon par ID
                    Paths.Log("FPS : filtre d'événements refusé (" + fx.Message + "), activation sans filtre");
                    s.EnableProvider(DxgiProvider, TraceEventLevel.Informational, DxgiKeywords);
                }
                // La session ETW réelle n'existe qu'après EnableProvider : elle n'est publiée (donc fermable par StopSession)
                // qu'ici, sous le verrou. Arrêt demandé entretemps → on ne l'ouvre pas, le finally la Dispose (StopOnDispose).
                lock (_gate)
                {
                    if (_disposed || _stopRequested) return;
                    _session = s;
                }
                opened = true; _failures = 0;
                _live = true; Status = "ok";
                Paths.Log("FPS : session ETW DXGI ouverte");
                s.Source.Process();   // bloque jusqu'à l'arrêt de la session
            }
            catch (Exception ex)
            {
                bool stopping; lock (_gate) stopping = _stopRequested || _disposed;
                if (!stopping)
                {
                    Status = "indisponible : " + ex.Message;
                    Paths.Log("FPS: " + ex.GetType().Name + " — " + ex.Message);
                    if (!opened)
                    {
                        _retryAt = DateTime.UtcNow + RetryAfter;
                        if (++_failures >= 3) { Available = false; Paths.Log("FPS : abandon après 3 échecs d'ouverture"); }
                    }
                }
            }
            finally
            {
                _live = false;
                lock (_gate)
                {
                    // session arrêtée de l'extérieur (ou Process() sorti seul) : pas de réouverture en rafale
                    if (opened && !_stopRequested && !_disposed) _retryAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                    if (_session == s) _session = null; _thread = null;
                }
                try { s?.Dispose(); } catch { }
                if (opened && !_disposed) Paths.Log("FPS : session ETW fermée");
            }
        }

        /// <summary>Images par seconde du PID cible, sur les 2 dernières secondes d'images (horodatages ETW).
        /// Renvoie null sans cible, pendant l'ouverture de la session, et tant qu'aucune image récente n'a été vue
        /// pour ce PID (moins de 2 images, ou plus de 3 s sans nouvelle image). Lecture sans effet de bord sur la mesure.</summary>
        public double? Read()
        {
            if (!Available) return null;
            int pid = 0;
            try { pid = TargetPid(); } catch { }
            var now = DateTime.UtcNow;

            if (pid <= 0)
            {
                _target = -1;
                if (_lastPid != 0) { _lastPid = 0; _window.Reset(-1); }
                if (_thread != null && now - _lastWanted > CloseAfter) StopSession();
                return null;
            }
            _lastWanted = now;
            // changement de jeu : on repart d'une fenêtre vide, attribuée au nouveau PID (une image de l'ancien PID
            // encore en vol est rejetée par la fenêtre elle-même)
            if (pid != _lastPid) { _lastPid = pid; _window.Reset(pid); }
            _target = pid;
            // session en cours d'ouverture (ou à rouvrir) : les horodatages ETW sont relatifs au début de la session,
            // la fenêtre sera donc vidée à la première lecture sur la session vivante (_lastPid = 0)
            if (!_live) { EnsureSession(now); _lastPid = 0; return null; }
            return _window.Rate(Environment.TickCount64, WindowMs, StaleMs);
        }

        public void Dispose()
        {
            _disposed = true;
            StopSession();
        }
    }

    /// <summary>Horodatages (ms) des dernières images d'un processus, dans un tampon circulaire préalloué.
    /// Écrit par le thread ETW, lu par le thread de mesure : un verrou court, aucune allocation après construction.</summary>
    internal sealed class FrameWindow
    {
        readonly double[] _ts;
        readonly object _lock = new object();
        int _pid = -1, _head, _count;
        double _seenTs = double.NegativeInfinity, _seenAt;   // dernier horodatage d'image constaté par Rate, et quand (horloge du lecteur)

        public FrameWindow(int capacity = 8192) { _ts = new double[capacity]; }

        /// <summary>Vide la fenêtre et n'accepte plus que les images de <paramref name="pid"/>.</summary>
        public void Reset(int pid)
        {
            lock (_lock) { _pid = pid; _head = 0; _count = 0; _seenTs = double.NegativeInfinity; }
        }

        public void Add(int pid, double tsMs)
        {
            lock (_lock)
            {
                if (pid != _pid) return;
                _ts[_head] = tsMs;
                _head = (_head + 1) % _ts.Length;
                if (_count < _ts.Length) _count++;
            }
        }

        /// <summary>Débit sur les <paramref name="windowMs"/> dernières millisecondes d'images, la fenêtre se terminant
        /// à la dernière image reçue (et non à l'instant présent, les images arrivant par lots).
        /// <paramref name="nowMs"/> est l'horloge monotone du lecteur : si aucune nouvelle image n'est apparue depuis
        /// plus de <paramref name="staleMs"/>, renvoie null. Renvoie aussi null avec moins de 2 images.</summary>
        public double? Rate(double nowMs, double windowMs, double staleMs)
        {
            lock (_lock)
            {
                if (_count == 0) return null;
                double last = double.NegativeInfinity;
                for (int i = 0; i < _count; i++) { double t = _ts[i]; if (t > last) last = t; }
                if (last > _seenTs) { _seenTs = last; _seenAt = nowMs; }
                if (nowMs - _seenAt > staleMs) return null;   // plus d'image : jeu fermé, masqué, figé ou non mesurable

                double from = last - windowMs, first = last; int n = 0;
                for (int i = 0; i < _count; i++) { double t = _ts[i]; if (t >= from) { n++; if (t < first) first = t; } }
                double span = last - first;
                if (n < 2 || span < 100) return null;
                return Math.Round((n - 1) * 1000.0 / span);
            }
        }
    }
}
