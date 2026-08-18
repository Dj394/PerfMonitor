using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PerfMonitorLive.Alerts
{
    /// <summary>Envoi de messages Telegram (bot) ; le token est chiffré DPAPI (utilisateur) dans settings.json.</summary>
    public static class TelegramSender
    {
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public static string Protect(string plain)
        {
            try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser)); } catch { return null; }
        }
        public static string Unprotect(string enc)
        {
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser)); } catch { return ""; }
        }

        public static bool Configured(Settings s) => !string.IsNullOrEmpty(s.TelegramTokenEnc) && !string.IsNullOrWhiteSpace(s.TelegramChatId);

        /// <summary>Envoie un message ; renvoie null si OK, sinon le message d'erreur.</summary>
        public static async Task<string> SendAsync(Settings s, string text)
        {
            if (!Configured(s)) return "Telegram non configuré (token / chat ID).";
            var token = Unprotect(s.TelegramTokenEnc);
            if (string.IsNullOrEmpty(token)) return "Token illisible (re-saisir).";
            try
            {
                var payload = JsonSerializer.Serialize(new { chat_id = s.TelegramChatId, text = text, disable_web_page_preview = true });
                using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                using (var resp = await Http.PostAsync("https://api.telegram.org/bot" + token + "/sendMessage", content))
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    if (resp.IsSuccessStatusCode) return null;
                    Paths.Log("Telegram: " + body);
                    try { using (var doc = JsonDocument.Parse(body)) return doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : body; } catch { return body; }
                }
            }
            catch (Exception ex) { Paths.Log("Telegram: " + ex.Message); return ex.Message; }
        }

        /// <summary>Détecte le chat ID à partir des derniers messages reçus par le bot (l'utilisateur doit lui avoir écrit).</summary>
        public static async Task<string> DetectChatIdAsync(string token)
        {
            try
            {
                var body = await Http.GetStringAsync("https://api.telegram.org/bot" + token + "/getUpdates");
                using (var doc = JsonDocument.Parse(body))
                {
                    if (!doc.RootElement.TryGetProperty("result", out var arr)) return null;
                    string id = null;
                    foreach (var u in arr.EnumerateArray())
                        if (u.TryGetProperty("message", out var m) && m.TryGetProperty("chat", out var c) && c.TryGetProperty("id", out var i)) id = i.GetRawText();
                    return id;
                }
            }
            catch (Exception ex) { Paths.Log("Telegram getUpdates: " + ex.Message); return null; }
        }
        public static void Fire(Settings s, string text) { _ = Task.Run(async () => { var err = await SendAsync(s, text); if (err != null) Paths.Log("Telegram KO: " + err); }); }
    }
}
