using System;
using System.Collections.Generic;
using PerfMonitorLive.Metrics;
using Xunit;

namespace PerfMonitorLive.Tests
{
    /// <summary>Revue « fps » : débit calculé sur les horodatages des images, null sans image récente,
    /// PID mesuré limité au jeu suivi quand il est au premier plan.</summary>
    public class ReviewfpsTests
    {
        const double W = 2000, Stale = 3000;

        static void Frames(FrameWindow w, int pid, double fromMs, double toMs, double fps)
        {
            double step = 1000.0 / fps;
            for (double t = fromMs; t < toMs - 1e-9; t += step) w.Add(pid, t);
        }

        [Fact]
        public void Debit_stable_malgre_la_livraison_par_lots()
        {
            // 144 images/s livrées par lots d'une seconde ; la lecture tombe tantôt avant, tantôt après un lot :
            // le débit ne dépend que des horodatages, jamais ~0 puis le double.
            var w = new FrameWindow(); w.Reset(1234);
            Frames(w, 1234, 0, 1000, 144);
            Assert.InRange(w.Rate(1000, W, Stale).Value, 143, 145);
            Frames(w, 1234, 1000, 2000, 144);
            var a = w.Rate(2100, W, Stale);
            var b = w.Rate(2900, W, Stale);   // aucun lot arrivé entre les deux lectures
            Frames(w, 1234, 2000, 4000, 144); // deux lots d'un coup
            var c = w.Rate(3100, W, Stale);
            foreach (var v in new[] { a, b, c }) { Assert.NotNull(v); Assert.InRange(v.Value, 143, 145); }
        }

        [Fact]
        public void Aucune_image_donne_null_et_jamais_zero()
        {
            var w = new FrameWindow(); w.Reset(42);
            Assert.Null(w.Rate(0, W, Stale));
            Assert.Null(w.Rate(5000, W, Stale));
            w.Add(42, 10);                           // une seule image : pas de débit
            Assert.Null(w.Rate(6000, W, Stale));
        }

        [Fact]
        public void Plus_d_image_depuis_3_s_donne_null()
        {
            var w = new FrameWindow(); w.Reset(7);
            Frames(w, 7, 0, 2000, 60);
            Assert.InRange(w.Rate(10_000, W, Stale).Value, 59, 61);
            Assert.NotNull(w.Rate(12_500, W, Stale));   // délai de livraison toléré
            Assert.Null(w.Rate(13_500, W, Stale));      // jeu fermé, masqué ou figé : « — », pas « 0 FPS »
            Frames(w, 7, 2000, 3000, 60);              // les images reprennent
            Assert.NotNull(w.Rate(14_000, W, Stale));
        }

        [Fact]
        public void Images_d_un_autre_pid_ignorees_et_reset_vide_la_fenetre()
        {
            var w = new FrameWindow(); w.Reset(1);
            Frames(w, 2, 0, 1000, 100);
            Assert.Null(w.Rate(1000, W, Stale));
            Frames(w, 1, 0, 1000, 100);
            Assert.NotNull(w.Rate(1000, W, Stale));
            w.Reset(3);
            Assert.Null(w.Rate(1100, W, Stale));
        }

        [Fact]
        public void Debit_suit_un_changement_de_cadence()
        {
            var w = new FrameWindow(); w.Reset(5);
            Frames(w, 5, 0, 3000, 200);
            Frames(w, 5, 3000, 6000, 50);   // la fenêtre de 2 s ne garde que la nouvelle cadence
            Assert.InRange(w.Rate(6000, W, Stale).Value, 49, 51);
        }

        [Fact]
        public void Pid_mesure_uniquement_pour_le_jeu_suivi()
        {
            Assert.Equal(100, GameDetector.ForegroundGamePid(false, null, "League of Legends", 100));     // avant la session
            Assert.Equal(100, GameDetector.ForegroundGamePid(true, "League of Legends", "league of legends", 100));
            Assert.Equal(200, GameDetector.ForegroundGamePid(true, "League of Legends", "League of Legends", 200)); // relancé
            Assert.Equal(0, GameDetector.ForegroundGamePid(true, "League of Legends", "mpv", 300));      // autre plein écran
            Assert.Equal(0, GameDetector.ForegroundGamePid(true, "League of Legends", null, 100));       // plus au premier plan
        }

        static (GameDetector d, List<string> ev) Detector()
        {
            var ev = new List<string>();
            var d = new GameDetector(new Settings()) { Log = _ => { }, Save = _ => { } };
            d.Started += g => ev.Add("start " + g);
            d.Ended += s => ev.Add("end " + s.game);
            return (d, ev);
        }

        [Fact]
        public void Autre_jeu_au_premier_plan_termine_la_session_et_demarre_la_sienne()
        {
            var (d, ev) = Detector();
            var t = new DateTime(2026, 1, 1, 20, 0, 0);
            for (int s = 0; s <= 10; s += 2) d.Step("GameA", 10, t.AddSeconds(s), null);
            Assert.Equal(new[] { "start GameA" }, ev);
            for (int s = 12; s < 18; s += 2) { d.Step("GameB", 20, t.AddSeconds(s), null); Assert.Equal(0, d.Pid); }
            Assert.Equal("GameA", d.Game);                   // moins de 8 s : la session A tient
            d.Step("GameB", 20, t.AddSeconds(20), null);
            Assert.Equal(new[] { "start GameA", "end GameA", "start GameB" }, ev);
            Assert.True(d.Active); Assert.Equal("GameB", d.Game); Assert.Equal(20, d.Pid);
        }

        [Fact]
        public void Alt_tab_bref_et_relance_sous_autre_pid_gardent_la_session()
        {
            var (d, ev) = Detector();
            var t = new DateTime(2026, 1, 1, 20, 0, 0);
            for (int s = 0; s <= 10; s += 2) d.Step("GameA", 10, t.AddSeconds(s), null);
            for (int s = 12; s <= 30; s += 2) { d.Step(null, 0, t.AddSeconds(s), null); Assert.Equal(0, d.Pid); }   // bureau / navigateur
            d.Step("GameA", 11, t.AddSeconds(32), null);     // relancé sous un autre PID
            Assert.Equal(11, d.Pid);
            d.Step("mpv", 30, t.AddSeconds(34), null);       // plein écran bref (< 8 s)
            d.Step("GameA", 11, t.AddSeconds(38), null);
            Assert.Equal(new[] { "start GameA" }, ev);
            d.Step(null, 0, t.AddSeconds(68), null);         // 30 s sans le jeu : fin
            Assert.Equal(new[] { "start GameA", "end GameA" }, ev);
            d.Step("GameA", 12, t.AddSeconds(70), null);     // retour : nouvelle attente de 8 s
            Assert.False(d.Active);
        }
    }
}
