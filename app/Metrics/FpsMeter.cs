using System;
using System.Collections.Concurrent;
using System.Threading;
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
    /// </list></summary>
    public class FpsMeter : IDisposable
    {
        /// <summary>PID dont on veut le débit d'images — le jeu au premier plan. 0 = personne, la mesure renvoie null.</summary>
        public Func<int> TargetPid = () => 0;

        public bool Available { get; private set; }
        public string Status { get; private set; } = "non démarré";

        static readonly Guid DxgiProvider = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        const string SessionName = "PerfMonitorLive-FPS";

        TraceEventSession _session;
        Thread _thread;
        volatile bool _run = true;
        readonly ConcurrentDictionary<int, long> _frames = new ConcurrentDictionary<int, long>();

        int _lastPid; long _lastCount; DateTime _lastTime; double? _last;

        public void Start()
        {
            if (TraceEventSession.IsElevated() != true) { Status = "droits administrateur requis"; return; }
            _thread = new Thread(Loop) { IsBackground = true, Name = "FpsMeter" };
            _thread.Start();
        }

        void Loop()
        {
            try
            {
                // Une session ETW du même nom peut subsister après un arrêt brutal (et la machine en connaît :
                // c'est tout le sujet de l'enquête sur l'alimentation). TraceEventSession la reprend et
                // StopOnDispose garantit qu'on ne laisse rien derrière nous.
                _session = new TraceEventSession(SessionName) { StopOnDispose = true };
                _session.Source.Dynamic.All += ev =>
                {
                    if (ev.ProviderGuid != DxgiProvider) return;
                    if (ev.OpcodeName != "Stop") return;
                    var task = ev.TaskName;
                    if (task == null || task.IndexOf("Present", StringComparison.OrdinalIgnoreCase) < 0) return;
                    _frames.AddOrUpdate(ev.ProcessID, 1L, (k, v) => v + 1);
                };
                _session.EnableProvider(DxgiProvider);
                Available = true; Status = "ok";
                Paths.Log("FPS : session ETW DXGI ouverte");
                _session.Source.Process();   // bloque jusqu'à l'arrêt de la session
            }
            catch (Exception ex)
            {
                Available = false; Status = "indisponible : " + ex.Message;
                Paths.Log("FPS: " + ex.GetType().Name + " — " + ex.Message);
            }
            if (_run) Paths.Log("FPS : session ETW terminée");
        }

        /// <summary>Débit d'images depuis l'appel précédent. À appeler une seule fois par mesure, à cadence régulière.
        /// Renvoie null tant qu'il n'y a pas deux points de mesure sur le même processus.</summary>
        public double? Read()
        {
            if (!Available) return null;
            int pid = 0;
            try { pid = TargetPid(); } catch { }
            if (pid <= 0) { _lastPid = 0; _last = null; return null; }

            _frames.TryGetValue(pid, out long count);
            var now = DateTime.UtcNow;

            // changement de jeu, ou compteur reparti de zéro : on repose la référence sans rien annoncer
            if (pid != _lastPid || count < _lastCount)
            {
                _lastPid = pid; _lastCount = count; _lastTime = now; _last = null;
                return null;
            }
            var dt = (now - _lastTime).TotalSeconds;
            if (dt < 0.3) return _last;   // trop rapproché : le bruit dépasserait la mesure

            _last = Math.Round((count - _lastCount) / dt);
            _lastCount = count; _lastTime = now;

            // Le dictionnaire ne grandit qu'avec le nombre de processus qui dessinent ; au-delà d'un seuil
            // manifestement anormal on repart de zéro plutôt que de fuir en mémoire.
            if (_frames.Count > 512) { _frames.Clear(); _lastCount = 0; }
            return _last;
        }

        public void Dispose()
        {
            _run = false;
            try { _session?.Dispose(); } catch { }
        }
    }
}
