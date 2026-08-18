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
        public string Notes;
        public bool IsNewer;
    }

    /// <summary>Vérification des mises à jour via l'API GitHub (dernière Release du dépôt). Aucune installation automatique : on ouvre la page.</summary>
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
                        if (name != null && name.Equals("PerfMonitorLive.exe", StringComparison.OrdinalIgnoreCase) && a.TryGetProperty("browser_download_url", out var d)) info.ExeUrl = d.GetString();
                    }
                info.IsNewer = Version.TryParse(Normalize(info.Version), out var remote) && Version.TryParse(Normalize(CurrentVersion), out var local) && remote > local;
                return info;
            }
        }
        static string Normalize(string v) { var parts = (v ?? "0").Split('-')[0].Split('.').ToList(); while (parts.Count < 3) parts.Add("0"); return string.Join(".", parts.Take(4)); }
    }
}
