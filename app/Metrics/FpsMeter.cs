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
    /// <item>La lecture est un débit : <see cref="Read"/> divise le nombre d'images accumulées par le temps
    /// écoulé depuis l'appel précédent. Il faut donc l'appeler à cadence régulière, et une seule fois par mesure.</item>
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
        long _frames;                    // images du PID cible depuis l'ouverture (Interlocked)
        int _failures; DateTime _retryAt = DateTime.MinValue;

        // état de lecture (thread de mesure uniquement)
        int _lastPid; long _lastCount; DateTime _lastTime; double? _last;
        DateTime _lastWanted;

        public void Start()
        {
            if (TraceEventSession.IsElevated() != true) { Status = "droits administrateur requis"; return; }
            Available = true; Status = "en attente d'un jeu";
        }

        /// <summary>Rappel ETW : appelé pour chaque événement remis. Ni allocation ni chaîne — uniquement des entiers.</summary>
        void OnEvent(TraceEvent ev)
        {
            if (ev.ProcessID != _target) return;
            int id = (int)ev.ID;
            if (id != PresentStop && id != PresentMpoStop) return;
            if (ev.ProviderGuid != DxgiProvider) return;
            Interlocked.Increment(ref _frames);
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

        /// <summary>Débit d'images depuis l'appel précédent. À appeler une seule fois par mesure, à cadence régulière.
        /// Renvoie null tant qu'il n'y a pas deux points de mesure sur le même processus.</summary>
        public double? Read()
        {
            if (!Available) return null;
            int pid = 0;
            try { pid = TargetPid(); } catch { }
            var now = DateTime.UtcNow;

            if (pid <= 0)
            {
                _target = -1; _lastPid = 0; _last = null;
                if (_thread != null && now - _lastWanted > CloseAfter) StopSession();
                return null;
            }
            _lastWanted = now;
            _target = pid;
            if (!_live) { EnsureSession(now); _lastPid = 0; _last = null; return null; }   // session en cours d'ouverture

            long count = Interlocked.Read(ref _frames);
            // changement de jeu, session rouverte ou compteur reparti de zéro : on repose la référence sans rien annoncer
            if (pid != _lastPid || count < _lastCount)
            {
                _lastPid = pid; _lastCount = count; _lastTime = now; _last = null;
                return null;
            }
            var dt = (now - _lastTime).TotalSeconds;
            if (dt < 0.3) return _last;   // trop rapproché : le bruit dépasserait la mesure

            _last = Math.Round((count - _lastCount) / dt);
            _lastCount = count; _lastTime = now;
            return _last;
        }

        public void Dispose()
        {
            _disposed = true;
            StopSession();
        }
    }
}
