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
    /// pouvoir relire les dernières secondes avant le noir. D'où trois choix :
    /// <list type="bullet">
    /// <item>peu de colonnes, en CSV : lisible à l'œil et dans un tableur, sans outil ;</item>
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

        const string Header = "ts;fps;v12;v5;v33;cpuW;gpuW;cpuC;gpuC;gpuMHz;fanMax;cpuPct;jeu";
        static readonly byte[] HeaderBytes = Utf8.GetBytes(Header + "\n");

        // --- écriture : file bornée vidée par un thread dédié
        const int MaxQueue = 120;
        readonly Queue<(string path, byte[] data)> _queue = new Queue<(string path, byte[] data)>();
        readonly AutoResetEvent _signal = new AutoResetEvent(false);
        Thread _writer;
        bool _dropLogged;                        // thread de mesure
        bool _slowLogged;                        // thread d'écriture
        string _preparedFile;                    // thread d'écriture : fichier dont l'en-tête a été vérifié

        public void OnSample(Sample s)
        {
            if (!Enabled) return;
            if ((s.Time - _last).TotalSeconds < IntervalSec) return;
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
                Put(Game() ? '1' : '0'); Put('\n');
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
        /// avec la première ligne dans le même appel, comme avant.</summary>
        void WriterLoop()
        {
            while (true)
            {
                _signal.WaitOne();
                while (true)
                {
                    (string path, byte[] data) item;
                    lock (_queue) { if (_queue.Count == 0) break; item = _queue.Dequeue(); }
                    try
                    {
                        var data = item.data;
                        if (item.path != _preparedFile)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(item.path));
                            bool header = PrepareFile(item.path);
                            _preparedFile = item.path;
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
                    }
                    catch (Exception ex) { _preparedFile = null; Paths.Log("PowerLog: " + ex.Message); }   // en-tête revérifié (et dossier recréé) au prochain essai
                }
            }
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

        // --- formatage dans _c (mêmes valeurs et même culture invariante qu'avec ToString)
        void Grow() { Array.Resize(ref _c, _c.Length * 2); _bytes = new byte[_c.Length * 3]; }
        void Put(char ch) { if (_n == _c.Length) Grow(); _c[_n++] = ch; }
        void Num(double v) { int w; while (!v.TryFormat(_c.AsSpan(_n), out w, default, CultureInfo.InvariantCulture)) Grow(); _n += w; }
        void Num(int v) { int w; while (!v.TryFormat(_c.AsSpan(_n), out w, default, CultureInfo.InvariantCulture)) Grow(); _n += w; }
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
