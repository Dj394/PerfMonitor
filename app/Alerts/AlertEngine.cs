using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Text.Json;
using PerfMonitorLive.Metrics;

namespace PerfMonitorLive.Alerts
{
    public enum Severity { Warning, Critical, Ok, Info }

    public class ToastAction { public string Label; public Action Run; public bool Primary; }
    public class AlertInfo
    {
        public string RuleId; public string Title; public string Detail; public Severity Severity; public DateTime Time;
        public string Proc; public int ProcPid;                 // processus en tête (alertes CPU/RAM)
        public List<ToastAction> Actions = new List<ToastAction>();
    }

    /// <summary>Évalue les règles à chaque échantillon : durée soutenue, cooldown, retour à la normale.</summary>
    public class AlertEngine
    {
        class State { public DateTime? AboveSince; public DateTime LastFired = DateTime.MinValue; public bool Active; public double Peak; }
        readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        readonly Settings _settings;
        public event Action<AlertInfo> Alert;
        /// <summary>Règles actuellement en dépassement (pour colorer les cartes).</summary>
        public HashSet<string> ActiveRules { get; } = new HashSet<string>();

        public AlertEngine(Settings settings) { _settings = settings; }

        public void OnSample(Sample s)
        {
            var now = s.Time;
            foreach (var rule in _settings.Rules.ToArray())
            {
                if (!_states.TryGetValue(rule.Id, out var st)) _states[rule.Id] = st = new State();
                var v = rule.Value(s);
                if (!rule.Enabled || v == null)
                {
                    if (st.Active) { st.Active = false; ActiveRules.Remove(rule.Id); }
                    st.AboveSince = null; continue;
                }
                double val = v.Value;
                if (val >= rule.Threshold)
                {
                    if (st.AboveSince == null) { st.AboveSince = now; st.Peak = val; }
                    st.Peak = Math.Max(st.Peak, val);
                    var held = (now - st.AboveSince.Value).TotalSeconds;
                    if (held >= rule.SustainSec)
                    {
                        ActiveRules.Add(rule.Id);
                        if (!st.Active && (now - st.LastFired).TotalSeconds >= _settings.CooldownSec)
                        {
                            st.Active = true; st.LastFired = now;
                            var sev = val >= rule.Threshold * 1.1 || rule.Unit == "%" && val >= 97 ? Severity.Critical : Severity.Warning;
                            var top = (rule.Metric == "cpu" || rule.Metric == "memPct") && s.procs != null && s.procs.Count > 0
                                ? (rule.Metric == "cpu" ? s.procs[0] : s.procs.OrderByDescending(p => p.mem).First()) : null;
                            Fire(new AlertInfo
                            {
                                RuleId = rule.Id, Time = now, Severity = sev,
                                Title = rule.Label + " : " + rule.Format(val),
                                Detail = "Au-dessus de " + rule.Format(rule.Threshold) + " depuis " + FormatDur(held) + TopProc(rule, s),
                                Proc = top?.n, ProcPid = top?.Pid ?? 0
                            });
                        }
                    }
                }
                else
                {
                    if (st.Active)
                    {
                        st.Active = false; ActiveRules.Remove(rule.Id);
                        if (_settings.NotifyRecovery)
                            Fire(new AlertInfo
                            {
                                RuleId = rule.Id, Time = now, Severity = Severity.Ok,
                                Title = rule.Label + " : retour à la normale (" + rule.Format(val) + ")",
                                Detail = "Pic " + rule.Format(st.Peak) + " · durée " + FormatDur((now - st.AboveSince.Value).TotalSeconds)
                            });
                    }
                    ActiveRules.Remove(rule.Id);
                    st.AboveSince = null;
                }
            }
        }

        static string TopProc(Rule rule, Sample s)
        {
            if (rule.Metric != "cpu" || s.procs == null || s.procs.Count == 0) return "";
            var p = s.procs[0]; return "\nProcessus principal : " + p.n + " (" + p.cpu + " %)";
        }
        static string FormatDur(double sec) => sec < 90 ? Math.Round(sec) + " s" : Math.Round(sec / 60) + " min";

        void Fire(AlertInfo a)
        {
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                var line = JsonSerializer.Serialize(new { ts = a.Time.ToString("yyyy-MM-ddTHH:mm:ss"), rule = a.RuleId, sev = a.Severity.ToString(), title = a.Title, detail = a.Detail });
                File.AppendAllText(Path.Combine(Paths.DataDir, "alerts.jsonl"), line + "\n", new UTF8Encoding(false));
            }
            catch { }
            Alert?.Invoke(a);
        }
    }
}
