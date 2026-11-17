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
            if (!Available) return Publish(default);
            int pid = 0;
            try { pid = TargetPid(); } catch { }
            var now = DateTime.UtcNow;

            if (pid <= 0)
            {
                _target = -1;
                if (_lastPid != 0) { _lastPid = 0; _window.Reset(-1); }
                if (_thread != null && now - _lastWanted > CloseAfter) StopSession();
                return Publish(default);
            }
            _lastWanted = now;
            // changement de jeu : on repart d'une fenêtre vide, attribuée au nouveau PID (une image de l'ancien PID
            // encore en vol est rejetée par la fenêtre elle-même)
            if (pid != _lastPid) { _lastPid = pid; _window.Reset(pid); }
            _target = pid;
            // session en cours d'ouverture (ou à rouvrir) : les horodatages ETW sont relatifs au début de la session,
            // la fenêtre sera donc vidée à la première lecture sur la session vivante (_lastPid = 0)
            if (!_live) { EnsureSession(now); _lastPid = 0; return Publish(default); }
            return Publish(_window.Stats(Environment.TickCount64, WindowMs, StaleMs));
        }

        /// <summary>Détail de la dernière lecture (thread de mesure), sur la même fenêtre de 2 s que le débit ; null quand
        /// <see cref="Read"/> renvoie null. <see cref="Low1"/> = « 1 % low » : FPS correspondant au 99e centile des temps
        /// d'image ; <see cref="FrameMs"/> = temps d'image moyen ; <see cref="Stutters"/> = saccades (images plus de
        /// <see cref="FrameWindow.StutterFactor"/> fois plus longues que la médiane) présentes dans la fenêtre.</summary>
        public double? Low1 { get; private set; }
        public double? FrameMs { get; private set; }
        public int? Stutters { get; private set; }
        /// <summary>Saccades comptées depuis le lancement, chacune une seule fois (jamais remis à zéro, tous jeux confondus) :
        /// la différence entre deux lectures donne les saccades de l'intervalle, même si des mesures ont été sautées.</summary>
        public long StutterTotal => _window.StutterTotal;
        /// <summary>Histogramme cumulé des temps d'image depuis le lancement (même principe que <see cref="StutterTotal"/>) :
        /// le rapport de session en tire le vrai « 1 % low » de la session.</summary>
        public FrameTimeHistogram FrameTimes => _window.FrameTimes;

        double? Publish(FrameStats st)
        {
            Low1 = st.Low1; FrameMs = st.FrameMs; Stutters = st.Stutters;
            return st.Fps;
        }

        public void Dispose()
        {
            _disposed = true;
            StopSession();
        }
    }

    /// <summary>Résultat d'une lecture de <see cref="FrameWindow"/> (structure : aucune allocation).
    /// Tous les champs sont null quand le débit l'est.</summary>
    internal readonly struct FrameStats
    {
        public readonly double? Fps, Low1, FrameMs; public readonly int? Stutters;
        public FrameStats(double fps, double low1, double frameMs, int stutters) { Fps = fps; Low1 = low1; FrameMs = frameMs; Stutters = stutters; }
    }

    /// <summary>Horodatages (ms) des dernières images d'un processus, dans un tampon circulaire préalloué.
    /// Écrit par le thread ETW, lu par le thread de mesure : un verrou court, aucune allocation après construction
    /// (le tampon de calcul des centiles est alloué une seule fois, à la première lecture qui trouve des images).
    /// Le verrou ne couvre que la copie des horodatages : tris et centiles se font hors verrou, sur le thread de mesure,
    /// une fois par mesure — rien n'est calculé image par image.</summary>
    internal sealed class FrameWindow
    {
        /// <summary>Une image est une saccade si son temps dépasse ce multiple de la médiane de la fenêtre.</summary>
        public const double StutterFactor = 2.5;
        readonly double[] _ts;
        readonly object _lock = new object();
        int _pid = -1, _head, _count;
        double _seenTs = double.NegativeInfinity, _seenAt;   // dernier horodatage d'image constaté par Stats, et quand (horloge du lecteur)
        double _stutterMark = double.NegativeInfinity;       // sous _lock : fin de la dernière image déjà examinée pour le total des saccades
        double[] _work;                                      // lecteur seul : [0, cap) horodatages triés, [cap, 2 cap) temps d'image triés
        long _stutterTotal;
        /// <summary>Au-delà de ce délai sans nouvelle image (horloge du lecteur), le jeu est considéré figé : le 1 % low publié
        /// est dégradé au temps d'image en cours. Doit dépasser la latence normale de livraison ETW (vidage ~1 fois/s) et la
        /// cadence de mesure, sinon un simple lot en retard passerait pour un gel.</summary>
        public const double StallMs = 1500;
        /// <summary>Plus long intervalle à cheval sur le début de la fenêtre encore examiné (gel, compilation de shader…) ;
        /// au-delà (écran de chargement sans image, jeu en pause), ce n'est plus une saccade.</summary>
        public const double MaxGapMs = 10_000;

        public FrameWindow(int capacity = 8192) { _ts = new double[capacity]; }

        /// <summary>Histogramme cumulé des temps d'image (chaque intervalle compté une seule fois, jamais remis à zéro) :
        /// sert au vrai « 1 % low » d'une session de jeu (différence entre la fin et le début de la session).</summary>
        public FrameTimeHistogram FrameTimes { get; } = new FrameTimeHistogram();

        /// <summary>Saccades comptées depuis la création, chacune une seule fois (voir <see cref="FpsMeter.StutterTotal"/>).</summary>
        public long StutterTotal => Interlocked.Read(ref _stutterTotal);

        /// <summary>Vide la fenêtre et n'accepte plus que les images de <paramref name="pid"/>.</summary>
        public void Reset(int pid)
        {
            lock (_lock) { _pid = pid; _head = 0; _count = 0; _seenTs = double.NegativeInfinity; _stutterMark = double.NegativeInfinity; }
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

        /// <summary>Débit seul (voir <see cref="Stats"/>).</summary>
        public double? Rate(double nowMs, double windowMs, double staleMs) => Stats(nowMs, windowMs, staleMs).Fps;

        /// <summary>Débit sur les <paramref name="windowMs"/> dernières millisecondes d'images, la fenêtre se terminant
        /// à la dernière image reçue (et non à l'instant présent, les images arrivant par lots).
        /// <paramref name="nowMs"/> est l'horloge monotone du lecteur : si aucune nouvelle image n'est apparue depuis
        /// plus de <paramref name="staleMs"/>, tout est null. Tout est null aussi avec moins de 2 images.
        /// Sur la même fenêtre : temps d'image moyen, « 1 % low » (FPS du 99e centile des temps d'image, rang le plus
        /// proche) et nombre de saccades (temps &gt; <see cref="StutterFactor"/> × médiane). Les saccades des images
        /// terminées depuis la lecture précédente s'ajoutent à <see cref="StutterTotal"/> et à <see cref="FrameTimes"/>.
        /// L'intervalle à cheval sur le début de la fenêtre (dernière image avant la fenêtre → première image de la
        /// fenêtre) est examiné lui aussi pour le centile et les saccades : sans lui, un gel de plus de ~1 s n'était
        /// jamais vu, la fenêtre se terminant à la dernière image reçue. Débit et temps moyen restent ceux de la fenêtre.
        /// Pendant un gel (aucune nouvelle image depuis plus de <see cref="StallMs"/>), le 1 % low est ramené au temps
        /// d'image en cours et le gel compte comme une saccade de la fenêtre (le total ne le compte qu'à la reprise).</summary>
        public FrameStats Stats(double nowMs, double windowMs, double staleMs)
        {
            int n = 0, cap = _ts.Length;
            double mark, pending;
            bool hasPrev = false;
            lock (_lock)
            {
                if (_count == 0) return default;
                double last = double.NegativeInfinity;
                for (int i = 0; i < _count; i++) { double t = _ts[i]; if (t > last) last = t; }
                if (last > _seenTs) { _seenTs = last; _seenAt = nowMs; }
                pending = nowMs - _seenAt;
                if (pending > staleMs) return default;   // plus d'image : jeu fermé, masqué, figé ou non mesurable

                double from = last - windowMs, prev = double.NegativeInfinity;
                _work ??= new double[cap * 2];
                for (int i = 0; i < _count; i++) { double t = _ts[i]; if (t >= from) _work[n++] = t; else if (t > prev) prev = t; }
                if (prev >= from - MaxGapMs) { _work[n++] = prev; hasPrev = true; }   // n ≤ _count ≤ cap : prev est l'une des images
                mark = _stutterMark;
            }

            var ts = _work.AsSpan(0, n);
            ts.Sort();   // tri en place : le tampon est circulaire et les lots ETW ne garantissent pas l'ordre
            int w0 = hasPrev ? 1 : 0;   // ts[w0..n) : images de la fenêtre proprement dite ; ts[0] = celle d'avant, si présente
            if (n - w0 < 2) return default;
            double span = ts[n - 1] - ts[w0];
            if (span < 100) return default;

            int m = n - 1, mw = n - 1 - w0;
            var dt = _work.AsSpan(cap, m);
            for (int i = 0; i < m; i++) dt[i] = ts[i + 1] - ts[i];
            dt.Sort();
            double median = (m & 1) == 1 ? dt[m / 2] : (dt[m / 2 - 1] + dt[m / 2]) / 2;
            double p99 = dt[Math.Max(0, (int)Math.Ceiling(0.99 * m) - 1)];
            double limit = median > 0 ? StutterFactor * median : double.PositiveInfinity;

            int stutters = 0;
            for (int i = 0; i < m; i++) if (ts[i + 1] - ts[i] > limit) stutters++;

            // Images terminées après la lecture précédente : pas encore comptées (total des saccades, histogramme de session).
            // Reset entre la copie et ici (changement de jeu) : la fenêtre copiée n'est plus d'actualité, rien n'est ajouté.
            bool current;
            lock (_lock) { current = _stutterMark.Equals(mark); if (current) _stutterMark = Math.Max(mark, ts[n - 1]); }
            if (current)
            {
                long fresh = 0;
                for (int i = m - 1; i >= 0 && ts[i + 1] > mark; i--)
                {
                    double d = ts[i + 1] - ts[i];
                    FrameTimes.Add(d);
                    if (d > limit) fresh++;
                }
                if (fresh > 0) Interlocked.Add(ref _stutterTotal, fresh);
            }

            double fps = Math.Round(mw * 1000.0 / span);
            double low1 = p99 > 0 ? Math.Round(1000.0 / p99) : fps;
            if (pending > StallMs && pending > limit)
            {   // gel en cours : l'image attendue dure déjà au moins « pending » (sous-estimé : la dernière image a été vue après coup)
                low1 = Math.Min(low1, Math.Round(1000.0 / pending));
                stutters++;
            }
            return new FrameStats(fps, low1, Math.Round(span / mw, 2), stutters);
        }
    }

    /// <summary>Histogramme cumulé des temps d'image, par cases de <see cref="BinMs"/> ms jusqu'à <see cref="MaxMs"/> ms,
    /// plus une case de débordement. Tableau préalloué, écrit par le thread de mesure (Interlocked), lu par le thread UI
    /// au début et à la fin d'une session de jeu : le centile de la session se lit sur la différence des deux copies.</summary>
    public sealed class FrameTimeHistogram
    {
        public const double BinMs = 0.1, MaxMs = 100;
        public const int Bins = (int)(MaxMs / BinMs) + 1;   // dernière case : au-delà de MaxMs
        readonly long[] _c = new long[Bins];

        public void Add(double ms)
        {
            if (!(ms >= 0)) return;
            int i = ms >= MaxMs ? Bins - 1 : (int)(ms / BinMs);
            Interlocked.Increment(ref _c[i]);
        }

        /// <summary>Copie les compteurs dans <paramref name="dst"/> (longueur <see cref="Bins"/>).</summary>
        public void CopyTo(long[] dst) { for (int i = 0; i < Bins; i++) dst[i] = Interlocked.Read(ref _c[i]); }

        /// <summary>Temps d'image (ms) du centile <paramref name="q"/> (rang le plus proche) des images comptées entre deux
        /// copies ; null si aucune. Centre de la case ; la case de débordement vaut <see cref="MaxMs"/>.</summary>
        public static double? Percentile(long[] end, long[] start, double q)
        {
            long total = 0;
            for (int i = 0; i < Bins; i++) total += end[i] - (start?[i] ?? 0);
            if (total <= 0) return null;
            long rank = Math.Max(1, (long)Math.Ceiling(q * total)), acc = 0;
            for (int i = 0; i < Bins; i++)
            {
                acc += end[i] - (start?[i] ?? 0);
                if (acc >= rank) return i == Bins - 1 ? MaxMs : (i + 0.5) * BinMs;
            }
            return MaxMs;
        }
    }
}
