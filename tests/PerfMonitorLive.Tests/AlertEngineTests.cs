using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PerfMonitorLive.Alerts;
using PerfMonitorLive.Metrics;
using Xunit;

namespace PerfMonitorLive.Tests
{
    public class AlertEngineTests
    {
        static readonly DateTime T0 = new DateTime(2026, 3, 5, 12, 0, 0);

        static (AlertEngine engine, List<AlertInfo> alerts, Settings settings) Make(double threshold = 90, int sustain = 30, int cooldown = 300)
        {
            TestEnvironment.Reset();
            var settings = new Settings { CooldownSec = cooldown };
            settings.Rules.Add(new Rule { Metric = "cpu", Threshold = threshold, SustainSec = sustain, Enabled = true });
            var engine = new AlertEngine(settings);
            var alerts = new List<AlertInfo>();
            engine.Alert += alerts.Add;
            return (engine, alerts, settings);
        }

        static Sample Cpu(int sec, double cpu) => new Sample { Time = T0.AddSeconds(sec), cpu = cpu };

        [Fact]
        public void Ne_se_declenche_qu_apres_la_duree_de_maintien()
        {
            var (e, alerts, _) = Make();
            for (int s = 0; s < 30; s++) e.OnSample(Cpu(s, 92));   // 0..29 s : 29 s de dépassement seulement
            Assert.Empty(alerts);
            Assert.DoesNotContain("cpu", e.ActiveRules);

            e.OnSample(Cpu(30, 92));                                // 30 s : durée atteinte
            var a = Assert.Single(alerts);
            Assert.Equal("cpu", a.RuleId);
            Assert.Equal(Severity.Warning, a.Severity);
            Assert.Contains("CPU", a.Title);
            Assert.Contains("92 %", a.Title);
            Assert.Contains("30 s", a.Detail);
            Assert.Contains("cpu", e.ActiveRules);
        }

        [Fact]
        public void Un_retour_sous_le_seuil_remet_le_compteur_a_zero()
        {
            var (e, alerts, _) = Make();
            for (int s = 0; s < 20; s++) e.OnSample(Cpu(s, 95));
            e.OnSample(Cpu(20, 10));                                // retour à la normale : le maintien repart de zéro
            for (int s = 21; s < 50; s++) e.OnSample(Cpu(s, 95));   // 29 s de dépassement (21..49)
            Assert.Empty(alerts);
            e.OnSample(Cpu(51, 95));                                // 30 s depuis 21
            Assert.Single(alerts);
        }

        [Fact]
        public void Pas_de_second_declenchement_tant_que_l_alerte_est_active_puis_retour_a_la_normale()
        {
            var (e, alerts, _) = Make();
            for (int s = 0; s <= 100; s++) e.OnSample(Cpu(s, 92));
            Assert.Single(alerts);                                  // une seule alerte malgré 100 s de dépassement
            e.OnSample(Cpu(101, 5));
            Assert.Equal(2, alerts.Count);
            Assert.Equal(Severity.Ok, alerts[1].Severity);
            Assert.Contains("retour à la normale", alerts[1].Title);
            Assert.DoesNotContain("cpu", e.ActiveRules);
        }

        [Fact]
        public void Cooldown_empeche_un_redeclenchement_immediat()
        {
            var (e, alerts, _) = Make(cooldown: 300);
            for (int s = 0; s <= 30; s++) e.OnSample(Cpu(s, 92));   // alerte à 30 s
            e.OnSample(Cpu(31, 5));                                  // retour
            alerts.Clear();
            for (int s = 32; s <= 100; s++) e.OnSample(Cpu(s, 92));  // à nouveau soutenu, mais 70 s < cooldown de 300 s
            Assert.DoesNotContain(alerts, a => a.Severity == Severity.Warning || a.Severity == Severity.Critical);
            for (int s = 101; s <= 340; s++) e.OnSample(Cpu(s, 92));   // 340 - 30 > 300 s
            Assert.Contains(alerts, a => a.Severity == Severity.Warning || a.Severity == Severity.Critical);
        }

        [Fact]
        public void Sans_maintien_se_declenche_immediatement()
        {
            var (e, alerts, _) = Make(sustain: 0);
            e.OnSample(Cpu(0, 91));
            Assert.Single(alerts);
        }

        [Fact]
        public void Valeur_tres_au_dessus_du_seuil_est_critique()
        {
            var (e, alerts, _) = Make(sustain: 0, threshold: 90);
            e.OnSample(Cpu(0, 99));   // >= 97 % pour une unité en %
            Assert.Equal(Severity.Critical, alerts.Single().Severity);
        }

        [Fact]
        public void Regle_desactivee_ou_valeur_absente_ne_declenche_rien()
        {
            var (e, alerts, settings) = Make(sustain: 0);
            settings.Rules[0].Enabled = false;
            e.OnSample(Cpu(0, 99));
            Assert.Empty(alerts);

            settings.Rules[0].Enabled = true;
            settings.Rules.Add(new Rule { Metric = "temp", Threshold = 80, SustainSec = 0, Enabled = true });
            var s = Cpu(1, 10); s.temp = null;                      // température indisponible
            e.OnSample(s);
            Assert.Empty(alerts);
        }

        [Fact]
        public void L_alerte_est_journalisee_dans_le_dossier_temporaire()
        {
            var (e, alerts, _) = Make(sustain: 0);
            e.OnSample(Cpu(0, 95));
            var file = Path.Combine(Paths.DataDir, "alerts.jsonl");
            Assert.True(File.Exists(file));
            Assert.StartsWith(TestEnvironment.BaseDir, file, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"rule\":\"cpu\"", File.ReadAllText(file));
        }
    }
}
