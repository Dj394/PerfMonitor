using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace PerfMonitorLive.Metrics
{
    /// <summary>Journal dédié à l'alimentation : une ligne par seconde dans <c>data\power-yyyy-MM-dd.csv</c>.
    ///
    /// Volontairement séparé de <see cref="HistoryWriter"/>, qui écrit tout l'échantillon en JSON toutes les 5 s.
    /// Ce journal-ci vise un cas précis : une coupure brutale où la machine perd le courant sans rien écrire —
    /// pas de BSOD, pas de vidage mémoire, aucune trace dans les journaux Windows. Ce qu'il faut alors, c'est
    /// pouvoir relire les dernières secondes avant le noir. D'où quatre choix :
    /// <list type="bullet">
    /// <item>peu de colonnes, en CSV (dont, pendant un jeu, FPS, 1 % low, temps d'image moyen et saccades) : lisible à l'œil et dans un tableur, sans outil ;</item>
    /// <item>format « CSV à la française », fixe quelle que soit la culture : séparateur « ; » ET virgule décimale
    /// (12,035). Un Excel en français l'ouvre d'un double-clic avec des nombres (et non du texte ou des dates comme
    /// avec 12.035 ou 3.3), et le format reste déterministe pour les autres outils (pandas : <c>sep=';', decimal=','</c>).
    /// La virgule ne peut pas être confondue avec le séparateur « ; » ; aucun séparateur de milliers n'est écrit.
    /// Les anciens fichiers (point décimal, première colonne « ts ») restent lisibles par <see cref="CrashReport"/>,
    /// qui accepte les deux ; le changement d'en-tête (« heure ») fait mettre de côté un fichier du jour à l'ancien format
    /// plutôt que d'y mélanger deux notations ;</item>
    /// <item>cadence à la seconde, contre 5 s pour l'historique ;</item>
    /// <item>ouverture en ajout, écriture, FlushFileBuffers puis fermeture à chaque ligne (voir <c>AppendDurable</c>) :
    /// un flux gardé ouvert avec son tampon ne survit pas à une coupure d'alimentation.</item>
    /// </list>
    /// Le thread de mesure ne fait que formater la ligne et l'enfiler : l'écriture (et son FlushFileBuffers, lent sur un
    /// disque chargé) se fait sur un thread dédié, dans l'ordre d'arrivée. File bornée à <see cref="MaxQueue"/> lignes :
    /// si le disque ne suit plus, les plus anciennes sont jetées (journalisé une fois).
    ///
    /// Les tensions viennent du Super I/O de la carte mère, relu toutes les 5 s (voir <see cref="Hardware.MbIntervalSec"/>) :
    /// elles se répètent donc d'une ligne à l'autre, alors que consommations, températures et images par seconde
    /// bougent à chaque seconde. C'est assez : un rail qui s'affaisse le fait sur des minutes. En revanche aucune
    /// mesure logicielle ne verra un pic transitoire de quelques microsecondes — cela demande un oscilloscope.</summary>
    public class PowerLog
    {
        public bool Enabled = true;
        public int IntervalSec = 1;
        /// <summary>Partie en cours. Fourni par l'appelant : ce journal écrit depuis le thread de mesure,
        /// où <see cref="Sample.GameActive"/> n'est pas encore renseigné (il l'est plus tard, côté interface).</summary>
        public Func<bool> GameActive = () => false;

        DateTime _last = DateTime.MinValue;
        string _openFile;
        DateTime _day = DateTime.MinValue;       // jour du fichier courant : le chemin n'est recalculé qu'au changement de jour
        char[] _c = new char[1024]; int _n;      // ligne en cours de formatage (tampon réutilisé)
        byte[] _bytes = new byte[1024 * 3];
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>Colonnes. Les trois dernières (1 % low, temps d'image moyen en ms, saccades sur 2 s) ont été ajoutées
        /// en fin de ligne : les colonnes existantes gardent leur position, et <see cref="CrashReport"/> lit par nom
        /// d'en-tête (une colonne qu'il ne connaît pas est ignorée).</summary>
        internal const string Header = "heure;fps;v12;v5;v33;cpuW;gpuW;cpuC;gpuC;gpuMHz;fanMax;cpuPct;jeu;fps1;fpsMs;sacc";
        static readonly byte[] HeaderBytes = Utf8.GetBytes(Header + "\n");

        // --- écriture : file bornée vidée par un thread dédié
        const int MaxQueue = 120;
        readonly Queue<(string path, byte[] data)> _queue = new Queue<(string path, byte[] data)>();
        readonly AutoResetEvent _signal = new AutoResetEvent(false);
        Thread _writer;
        bool _dropLogged;                        // thread de mesure
        bool _slowLogged;                        // thread d'écriture
        readonly HashSet<string> _prepared = new HashSet<string>();   // thread d'écriture : fichiers dont l'en-tête a été vérifié
        readonly List<(string path, byte[] data)> _held = new List<(string path, byte[] data)>();   // thread d'écriture : lignes refusées, dans l'ordre
        long _nextRetry;                         // thread d'écriture : échéance du prochain essai des lignes de côté (Stopwatch)
        bool _okSinceErr;                        // thread d'écriture : au moins une écriture réussie depuis le dernier échec
        bool _heldDropLogged;                    // thread d'écriture
        bool _errLogged;                         // thread d'écriture : échec d'écriture déjà journalisé (réarmé au premier succès, une fois plus aucune ligne de côté)
        int _failedLines;                        // thread d'écriture : nombre d'essais en échec depuis le dernier succès
        /// <summary>Attente avant de réessayer les lignes refusées (fichier verrouillé par un tableur, disque plein…).</summary>
        internal int RetryMs = 1000;
        /// <summary>Virgule décimale, le reste comme la culture invariante (signe « - », pas de séparateur de milliers).</summary>
        static readonly NumberFormatInfo CsvNumbers = NumberFormatInfo.ReadOnly(new NumberFormatInfo { NumberDecimalSeparator = "," });

        public void OnSample(Sample s)
        {
            if (!Enabled) return;
            // Écart négatif = l'horloge a reculé (passage à l'heure d'hiver, correction NTP ou manuelle) : on écrit tout de
            // suite et on repart de la nouvelle heure, sinon le journal resterait muet le temps du recul (1 h en octobre).
            var gap = (s.Time - _last).TotalSeconds;
            if (gap >= 0 && gap < IntervalSec) return;
            _last = s.Time;
            try
            {
                _n = 0;
                if (_openFile == null || s.Time.Date != _day)
                {
                    _day = s.Time.Date;
                    _openFile = Path.Combine(Paths.DataDir, "power-" + s.Time.ToString("yyyy-MM-dd") + ".csv");
                }

                int fanMax = 0;
                if (s.fans != null && s.fans.Count > 0) { fanMax = s.fans[0].rpm; for (int i = 1; i < s.fans.Count; i++) if (s.fans[i].rpm > fanMax) fanMax = s.fans[i].rpm; }
                int w; while (!s.Time.TryFormat(_c.AsSpan(_n), out w, "HH:mm:ss", null)) Grow(); _n += w;   // culture courante, comme avant
                Put(';'); N(s.fps, 0); Put(';');
                N(s.v12, 3); Put(';'); N(s.v5, 3); Put(';'); N(s.v33, 3); Put(';');
                N(s.cpuW, 1); Put(';'); N(s.gpuW, 1); Put(';');
                N(s.temp, 1); Put(';'); N(s.gpu, 1); Put(';');
                N(s.gpuMHz, 0); Put(';');
                Num(fanMax); Put(';');
                Num(Math.Round(s.cpu)); Put(';');
                Put(Game() ? '1' : '0'); Put(';');
                N(s.fps1, 0); Put(';'); N(s.fpsMs, 2); Put(';');
                if (s.sacc.HasValue) Num(s.sacc.Value);
                Put('\n');
                int nb = Utf8.GetBytes(_c, 0, _n, _bytes, 0);
                Enqueue(_openFile, _bytes.AsSpan(0, nb).ToArray());
            }
            catch (Exception ex) { _openFile = null; Paths.Log("PowerLog: " + ex.Message); }   // chemin recalculé au prochain essai
        }

        /// <summary>Thread de mesure : enfile la ligne (les plus anciennes sont jetées si la file est pleine) et réveille l'écrivain.</summary>
        void Enqueue(string path, byte[] data)
        {
            bool dropped = false;
            lock (_queue)
            {
                while (_queue.Count >= MaxQueue) { _queue.Dequeue(); dropped = true; }
                _queue.Enqueue((path, data));
                if (_writer == null) { _writer = new Thread(WriterLoop) { IsBackground = true, Name = "PowerLogWriter" }; _writer.Start(); }
            }
            _signal.Set();
            if (dropped && !_dropLogged) { _dropLogged = true; Paths.Log("PowerLog : le disque ne suit pas, lignes les plus anciennes jetées (file pleine, " + MaxQueue + " lignes)"); }
        }

        /// <summary>Thread d'écriture : vide la file dans l'ordre, une ligne = ouverture/écriture/flush/fermeture.
        /// L'en-tête est vérifié au premier passage sur chaque fichier (nouveau jour, ou après une erreur) et écrit
        /// avec la première ligne dans le même appel, comme avant.
        ///
        /// Ligne refusée (fichier verrouillé par un tableur, disque plein…) : elle est mise de côté dans <c>_held</c>,
        /// suivie de toutes les lignes suivantes du MÊME fichier (ordre conservé), et réessayée toutes les <see cref="RetryMs"/>.
        /// Les lignes d'un autre fichier passent tout de suite : power-J.csv ouvert dans Excel à minuit ne retarde pas
        /// power-J+1.csv.</summary>
        void WriterLoop()
        {
            while (true)
            {
                if (_held.Count == 0) _signal.WaitOne();
                else
                {
                    // Lignes de côté : réveil à l'échéance du nouvel essai, ou plus tôt si une ligne arrive.
                    long wait = (_nextRetry - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                    if (wait > 0) _signal.WaitOne((int)Math.Min(wait, int.MaxValue));
                    if (Stopwatch.GetTimestamp() >= _nextRetry) RetryHeld();
                }
                while (true)
                {
                    (string path, byte[] data) item;
                    lock (_queue) { if (_queue.Count == 0) break; item = _queue.Dequeue(); }
                    // Fichier déjà en échec : la ligne attend derrière les précédentes du même fichier (ordre conservé).
                    if (IsHeld(item.path) || !TryWrite(item)) Hold(item);
                }
                // « Rétabli » seulement quand plus rien n'attend : une écriture réussie dans power-J+1 pendant que power-J
                // reste verrouillé ne réarme pas le journal (sinon une erreur journalisée à chaque essai).
                if (_errLogged && _okSinceErr && _held.Count == 0)
                {
                    Paths.Log("PowerLog : écriture rétablie après " + _failedLines + " essai(s) en échec");
                    _errLogged = false; _failedLines = 0;
                }
            }
        }

        /// <summary>Thread d'écriture : écrit une ligne. Faux si l'erreur est passagère (ligne à réessayer) ;
        /// une autre erreur fait abandonner la ligne. Une seule erreur journalisée par série.</summary>
        bool TryWrite((string path, byte[] data) item)
        {
            try
            {
                var data = item.data;
                if (!_prepared.Contains(item.path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.path));
                    bool header = PrepareFile(item.path);
                    if (_prepared.Count >= 4) _prepared.Clear();
                    _prepared.Add(item.path);
                    if (header)
                    {
                        var d = new byte[HeaderBytes.Length + data.Length];
                        HeaderBytes.CopyTo(d, 0); data.CopyTo(d, HeaderBytes.Length); data = d;
                    }
                }
                long t0 = Stopwatch.GetTimestamp();
                AppendDurable(item.path, data, data.Length);
                var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (ms > 500 && !_slowLogged) { _slowLogged = true; Paths.Log("PowerLog : écriture lente (" + ms.ToString("0") + " ms)"); }
                _okSinceErr = true;
                return true;
            }
            catch (Exception ex)
            {
                _prepared.Remove(item.path);   // en-tête revérifié (et dossier recréé) au prochain essai
                _failedLines++;
                _okSinceErr = false;
                if (!_errLogged) { _errLogged = true; Paths.Log("PowerLog: " + ex.Message + " (lignes gardées de côté, nouvel essai toutes les " + RetryMs / 1000.0 + " s)"); }
                return !(ex is IOException || ex is UnauthorizedAccessException);
            }
        }

        bool IsHeld(string path) { foreach (var h in _held) if (h.path == path) return true; return false; }

        /// <summary>Thread d'écriture : met une ligne de côté, à la suite des précédentes. Bornée à <see cref="MaxQueue"/> :
        /// au-delà, les plus anciennes sont jetées (journalisé une fois).</summary>
        void Hold((string path, byte[] data) item)
        {
            if (_held.Count == 0) _nextRetry = Stopwatch.GetTimestamp() + RetryMs * Stopwatch.Frequency / 1000;
            _held.Add(item);
            if (_held.Count > MaxQueue)
            {
                _held.RemoveAt(0);
                if (!_heldDropLogged) { _heldDropLogged = true; Paths.Log("PowerLog : fichier inaccessible trop longtemps, lignes les plus anciennes jetées (" + MaxQueue + " lignes gardées)"); }
            }
        }

        /// <summary>Thread d'écriture : réessaie les lignes mises de côté, dans l'ordre. Dès qu'un fichier refuse encore
        /// une ligne, ses lignes suivantes restent de côté sans essai (ordre conservé) ; celles d'un autre fichier sont tentées.</summary>
        void RetryHeld()
        {
            var lines = _held.ToArray();
            _held.Clear();
            List<string> failed = null;
            foreach (var item in lines)
            {
                if (failed != null && failed.Contains(item.path)) { _held.Add(item); continue; }
                if (TryWrite(item)) continue;
                (failed ??= new List<string>()).Add(item.path);
                _held.Add(item);
            }
            _nextRetry = Stopwatch.GetTimestamp() + RetryMs * Stopwatch.Frequency / 1000;
        }

        /// <summary>Ajoute la ligne en fin de fichier puis referme : ouverture en ajout, écriture, FlushFileBuffers, fermeture.
        /// Le flush est ce qui fait survivre la ligne à une coupure d'alimentation (un simple Write reste dans le cache système).</summary>
        static void AppendDurable(string path, byte[] data, int count)
        {
            SafeFileHandle fh;
            try { fh = File.OpenHandle(path, FileMode.Append, FileAccess.Write, FileShare.Read); }
            catch (DirectoryNotFoundException) { Directory.CreateDirectory(Path.GetDirectoryName(path)); fh = File.OpenHandle(path, FileMode.Append, FileAccess.Write, FileShare.Read); }
            using (fh)
            {
                RandomAccess.Write(fh, new ReadOnlySpan<byte>(data, 0, count), RandomAccess.GetLength(fh));
                RandomAccess.FlushToDisk(fh);
            }
        }

        // --- formatage dans _c (mêmes valeurs qu'avec ToString invariant, mais virgule décimale : voir l'en-tête de classe)
        void Grow() { Array.Resize(ref _c, _c.Length * 2); _bytes = new byte[_c.Length * 3]; }
        void Put(char ch) { if (_n == _c.Length) Grow(); _c[_n++] = ch; }
        void Num(double v) { int w; while (!v.TryFormat(_c.AsSpan(_n), out w, default, CsvNumbers)) Grow(); _n += w; }
        void Num(int v) { int w; while (!v.TryFormat(_c.AsSpan(_n), out w, default, CsvNumbers)) Grow(); _n += w; }
        /// <summary>Valeur absente = cellule vide, pour ne pas la confondre avec un vrai zéro (un rail à 0 V, ça se remarque).</summary>
        void N(double? v, int digits) { if (v.HasValue) Num(Math.Round(v.Value, digits)); }

        /// <summary>Prépare le fichier du jour et dit s'il faut y écrire l'en-tête. Si un fichier existe déjà avec
        /// d'autres colonnes (une version précédente en avait moins), il est mis de côté plutôt que complété :
        /// mélanger deux jeux de colonnes dans un même CSV le rendrait illisible, et on ne jette pas des mesures.</summary>
        static bool PrepareFile(string f)
        {
            if (!File.Exists(f)) return true;
            string first = null;
            try { using (var sr = new StreamReader(f)) first = sr.ReadLine(); } catch { return false; }
            if (first == Header) return false;
            var bak = Path.Combine(Path.GetDirectoryName(f), Path.GetFileNameWithoutExtension(f) + "." + DateTime.Now.ToString("HHmmss") + ".csv");
            try { File.Move(f, bak); Paths.Log("PowerLog : colonnes modifiées, ancien fichier conservé sous " + Path.GetFileName(bak)); return true; }
            catch { return false; }
        }

        bool Game() { try { return GameActive(); } catch { return false; } }
    }
}
