using System;
using System.Globalization;
using System.IO;
using System.Linq;
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
    /// <item><see cref="File.AppendAllText(string,string,Encoding)"/> à chaque ligne, qui referme le fichier :
    /// un flux gardé ouvert avec son tampon ne survit pas à une coupure d'alimentation.</item>
    /// </list>
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
        bool _slowLogged;

        const string Header = "ts;fps;v12;v5;v33;cpuW;gpuW;cpuC;gpuC;gpuMHz;fanMax;cpuPct;jeu";

        public void OnSample(Sample s)
        {
            if (!Enabled) return;
            if ((s.Time - _last).TotalSeconds < IntervalSec) return;
            _last = s.Time;
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                var f = Path.Combine(Paths.DataDir, "power-" + s.Time.ToString("yyyy-MM-dd") + ".csv");
                var sb = new StringBuilder();
                if (f != _openFile) { _openFile = f; if (PrepareFile(f)) sb.Append(Header).Append('\n'); }

                int fanMax = s.fans != null && s.fans.Count > 0 ? s.fans.Max(x => x.rpm) : 0;
                sb.Append(s.Time.ToString("HH:mm:ss")).Append(';')
                  .Append(N(s.fps, 0)).Append(';')
                  .Append(N(s.v12, 3)).Append(';').Append(N(s.v5, 3)).Append(';').Append(N(s.v33, 3)).Append(';')
                  .Append(N(s.cpuW, 1)).Append(';').Append(N(s.gpuW, 1)).Append(';')
                  .Append(N(s.temp, 1)).Append(';').Append(N(s.gpu, 1)).Append(';')
                  .Append(N(s.gpuMHz, 0)).Append(';')
                  .Append(fanMax.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(Math.Round(s.cpu).ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(Game() ? "1" : "0").Append('\n');

                var t0 = DateTime.Now;
                File.AppendAllText(f, sb.ToString(), new UTF8Encoding(false));
                var ms = (DateTime.Now - t0).TotalMilliseconds;
                // l'écriture se fait sur le thread de mesure : si le disque la rend coûteuse, il faut le savoir
                if (ms > 500 && !_slowLogged) { _slowLogged = true; Paths.Log("PowerLog : écriture lente (" + ms.ToString("0") + " ms)"); }
            }
            catch (Exception ex) { Paths.Log("PowerLog: " + ex.Message); }
        }

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

        /// <summary>Valeur absente = cellule vide, pour ne pas la confondre avec un vrai zéro (un rail à 0 V, ça se remarque).</summary>
        static string N(double? v, int digits) => v.HasValue ? Math.Round(v.Value, digits).ToString(CultureInfo.InvariantCulture) : "";
    }
}
