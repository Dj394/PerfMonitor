using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace PerfMonitorLive
{
    public class UpdateInfo
    {
        public string Version;      // ex. "3.1.0"
        public string Url;          // page de la release
        public string ExeUrl;       // asset PerfMonitorLive.exe s'il existe
        public string PortableUrl;  // asset PerfMonitorLive-portable.exe
        public string Notes;
        public bool IsNewer;
    }

    /// <summary>Mises à jour via l'API GitHub (dernière Release du dépôt) : vérification, téléchargement de l'exe, remplacement + relance.</summary>
    public static class Updater
    {
        public const string Repo = "Dj394/PerfMonitor";
        public static string ReleasesUrl => "https://github.com/" + Repo + "/releases";
        static readonly HttpClient Http = MakeClient();
        static HttpClient MakeClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PerfMonitorLive", CurrentVersion));
            c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return c;
        }

        public static string CurrentVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
                return v.Build > 0 ? v.Major + "." + v.Minor + "." + v.Build : v.Major + "." + v.Minor;
            }
        }

        public static async Task<UpdateInfo> CheckAsync()
        {
            var json = await Http.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases/latest").ConfigureAwait(false);
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (string.IsNullOrEmpty(tag)) throw new Exception("réponse GitHub sans tag");
                var info = new UpdateInfo { Version = tag.TrimStart('v', 'V'), Url = root.TryGetProperty("html_url", out var u) ? u.GetString() : ReleasesUrl, Notes = root.TryGetProperty("body", out var b) ? b.GetString() : null };
                if (root.TryGetProperty("assets", out var assets))
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() : "";
                        if (name == null || !a.TryGetProperty("browser_download_url", out var d)) continue;
                        if (name.Equals("PerfMonitorLive.exe", StringComparison.OrdinalIgnoreCase)) info.ExeUrl = d.GetString();
                        else if (name.Equals("PerfMonitorLive-portable.exe", StringComparison.OrdinalIgnoreCase)) info.PortableUrl = d.GetString();
                    }
                info.IsNewer = Version.TryParse(Normalize(info.Version), out var remote) && Version.TryParse(Normalize(CurrentVersion), out var local) && remote > local;
                return info;
            }
        }
        /// <summary>L'exe en cours est-il la variante « portable » (runtime inclus, > 50 Mo) ?</summary>
        public static bool IsPortableBuild { get { try { return new System.IO.FileInfo(ExePath).Length > 50L * 1024 * 1024; } catch { return false; } } }
        public static string ExePath => Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;

        /// <summary>Télécharge le bon exe dans un fichier temporaire (retourne son chemin). Lève une exception si l'asset manque ou le fichier est vide.</summary>
        public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress = null)
        {
            var url = IsPortableBuild ? (info.PortableUrl ?? info.ExeUrl) : (info.ExeUrl ?? info.PortableUrl);
            if (string.IsNullOrEmpty(url)) throw new Exception("la release ne contient pas d'exe");
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PerfMonitorLive-" + info.Version + ".exe");
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = new System.IO.FileStream(tmp, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None, 1 << 16))
                {
                    var buf = new byte[1 << 16]; int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0) { await dst.WriteAsync(buf, 0, n).ConfigureAwait(false); done += n; if (total > 0) progress?.Report(done * 100.0 / total); }
                }
            }
            var len = new System.IO.FileInfo(tmp).Length;
            if (len < 1024 * 1024) throw new Exception("téléchargement incomplet (" + len + " octets)");
            return tmp;
        }

        /// <summary>Lance le remplacement : un script PowerShell attend la fin de ce processus, copie le nouvel exe par-dessus l'ancien (l'ancien est gardé en .old), puis relance (tâche planifiée si elle existe, sinon l'exe).</summary>
        public static void LaunchReplace(string newExe, bool useTask)
        {
            var target = ExePath;
            var script = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PerfMonitorLive-update.ps1");
            var ps = @"
param($procId, $src, $dst, $useTask)
$log = Join-Path (Split-Path $dst) 'data\live.log'
function L($m) { try { Add-Content $log ((Get-Date -Format s) + ' MAJ: ' + $m) } catch {} }
for ($i = 0; $i -lt 60; $i++) { if (-not (Get-Process -Id $procId -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 500 }
Start-Sleep -Milliseconds 500
$ok = $false
for ($i = 0; $i -lt 20; $i++) {
  try { Copy-Item $dst ($dst + '.old') -Force -ErrorAction SilentlyContinue; Copy-Item $src $dst -Force -ErrorAction Stop; $ok = $true; break } catch { Start-Sleep -Milliseconds 500 }
}
if ($ok) { L ('exe remplace par ' + $src); Remove-Item $src -Force -ErrorAction SilentlyContinue } else { L 'echec du remplacement, ancien exe conserve' }
if ($useTask -eq 'True') { schtasks /run /tn PerfMonitorLive | Out-Null } else { Start-Process $dst -ArgumentList '--here --tray' }
";
            System.IO.File.WriteAllText(script, ps, new System.Text.UTF8Encoding(true));
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\" " + Environment.ProcessId + " \"" + newExe + "\" \"" + target + "\" " + useTask) { UseShellExecute = false, CreateNoWindow = true };
            System.Diagnostics.Process.Start(psi);
        }
        static string Normalize(string v) { var parts = (v ?? "0").Split('-')[0].Split('.').ToList(); while (parts.Count < 3) parts.Add("0"); return string.Join(".", parts.Take(4)); }
    }
}
