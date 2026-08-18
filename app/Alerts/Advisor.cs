using System;
using System.Collections.Generic;
using System.Linq;
using PerfMonitorLive.Metrics;

namespace PerfMonitorLive.Alerts
{
    /// <summary>Un conseil : déclencheur (règle) ou astuce générale, ciblant éventuellement une carte.</summary>
    public class Tip
    {
        public string Id;            // stable, pour « ne plus montrer »
        public string Title;
        public string Text;
        public string TargetKey;     // clé de carte (cpu, memPct, temp, gpuTemp, disk.lat:<n>, stor.temp:<n>…) ou null
        public int Priority;         // plus grand = plus urgent
        public bool Triggered;       // vrai si issu d'une règle déclenchée par les mesures
        public TimeSpan Cooldown = TimeSpan.FromMinutes(30);
    }

    /// <summary>Moteur du conseiller : moyennes glissantes + règles + astuces générales tournantes.</summary>
    public class Advisor
    {
        class Series
        {
            readonly Queue<KeyValuePair<DateTime, double>> _q = new Queue<KeyValuePair<DateTime, double>>();
            public void Add(DateTime t, double v) { _q.Enqueue(new KeyValuePair<DateTime, double>(t, v)); while (_q.Count > 0 && (t - _q.Peek().Key).TotalMinutes > 15) _q.Dequeue(); }
            public double Avg(double minutes) { var now = _q.Count > 0 ? _q.Last().Key : DateTime.Now; var a = _q.Where(k => (now - k.Key).TotalMinutes <= minutes).Select(k => k.Value).ToList(); return a.Count == 0 ? double.NaN : a.Average(); }
            public double Max(double minutes) { var now = _q.Count > 0 ? _q.Last().Key : DateTime.Now; var a = _q.Where(k => (now - k.Key).TotalMinutes <= minutes).Select(k => k.Value).ToList(); return a.Count == 0 ? double.NaN : a.Max(); }
            public double Span => _q.Count < 2 ? 0 : (_q.Last().Key - _q.Peek().Key).TotalMinutes;
        }
        readonly Dictionary<string, Series> _s = new Dictionary<string, Series>();
        Series S(string k) { if (!_s.TryGetValue(k, out var s)) _s[k] = s = new Series(); return s; }
        Sample _last;
        readonly Dictionary<string, DateTime> _lastShown = new Dictionary<string, DateTime>();
        readonly HashSet<string> _dismissed;
        int _generalIdx;
        readonly Random _rnd = new Random();

        public Advisor(IEnumerable<string> dismissed) { _dismissed = new HashSet<string>(dismissed ?? new string[0]); }
        public void Dismiss(string id) => _dismissed.Add(id);
        public bool IsDismissed(string id) => _dismissed.Contains(id);

        public void OnSample(Sample s)
        {
            _last = s; var t = s.Time;
            S("cpu").Add(t, s.cpu); S("mem").Add(t, s.memPct); S("pageIn").Add(t, s.pageIn);
            if (s.temp.HasValue) S("temp").Add(t, s.temp.Value);
            if (s.gpu.HasValue) S("gpu").Add(t, s.gpu.Value);
            S("rx").Add(t, s.rx / 1024); S("tx").Add(t, s.tx / 1024);
            foreach (var d in s.disks) { S("dpct:" + d.n).Add(t, d.pct); S("dlat:" + d.n).Add(t, d.lat); S("dq:" + d.n).Add(t, d.q); }
            foreach (var st in s.stor) S("stor:" + st.n).Add(t, st.t);
        }

        static string Fmt(double v, string u) => double.IsNaN(v) ? "?" : Math.Round(v) + " " + u;
        static string DiskLabel(string n) { var p = n.Split(' '); return p.Length > 1 ? "disque " + string.Join(" ", p.Skip(1)) : "disque " + n; }

        // ------------------------------------------------------------ vocabulaire selon la machine détectée (Metrics.Inventory)
        static MachineInfo M => Inventory.Current;
        static bool Laptop => M.Laptop;
        static bool AmdCpu => M.Cpu.Vendor == Vendor.Amd;
        static bool IntelCpu => M.Cpu.Vendor == Vendor.Intel;
        static Vendor Gpu => M.GpuVendor;
        static string CpuName => M.Cpu.Name != "?" ? M.Cpu.Short : "processeur";
        static string GpuName => M.MainGpu != null ? M.MainGpu.Short : "carte graphique";
        static string RamGo => M.RamGB > 0 ? M.RamGBRound + " Go" : "ta RAM";
        /// <summary>Réglage n°1 pour faire baisser la température CPU, selon la plate-forme.</summary>
        static string CpuTune()
        {
            if (Laptop) return "Sur un portable : surélève-le (l'air entre par-dessous), dépoussière les grilles/ventilateurs, choisis le mode « Équilibré » ou « Silencieux » dans l'utilitaire du constructeur ou Paramètres › Système › Alimentation › Mode d'alimentation, et évite les surfaces molles (lit, canapé)." + (IntelCpu ? " Sur Intel, ThrottleStop ou l'utilitaire constructeur permettent de baisser les limites de puissance (PL1/PL2)." : AmdCpu ? " Sur Ryzen mobile, le mode « Économie » du constructeur baisse le TDP (moins de chauffe, presque autant de perfs)." : "");
            if (M.Cpu.IsRyzen) return "Réglage efficace : BIOS › PBO › Curve Optimizer négatif (-10 à -25 par cœur) ou mode Eco (65 W) : moins de chauffe pour quasi les mêmes perfs.";
            if (IntelCpu) return "Réglage efficace : BIOS › baisser les limites de puissance (PL1/PL2, ex. 125/180 W au lieu de « illimité »), ou léger undervolt (offset -50 à -80 mV) via Intel XTU ou le BIOS si autorisé. Vérifie aussi le montage du ventirad (pression sur les 4 points).";
            return "Réglage : baisser les limites de puissance ou un léger undervolt dans le BIOS, et vérifier le montage du ventirad.";
        }
        static string CpuIdleTune()
        {
            if (Laptop) return "Sur un portable, 55–75 °C au repos est fréquent (refroidissement compact). Ferme les programmes en arrière-plan qui tournent (onglet Processus), passe le mode d'alimentation en « Équilibré » et pense au dépoussiérage si ça grimpe avec le temps.";
            if (M.Cpu.IsRyzen) return "Sur un Ryzen, 60–75 °C au repos vient souvent du boost agressif (Tctl) : rien de dangereux, mais tu peux calmer ça.\nRéglages : plan d'alimentation Windows « Équilibré » (pas « Performances élevées » qui bloque le CPU en haut), BIOS › PBO Curve Optimizer négatif, ou « Global C-state Control » activé. Vérifie aussi que le ventirad tourne bien et que la courbe de ventilation démarre tôt.";
            if (IntelCpu) return "Sur Intel, un CPU au repos devrait être à 35–50 °C. Plus haut = programme actif en arrière-plan (onglet Processus), C-states désactivés dans le BIOS, ou ventirad mal fixé / pâte thermique sèche. Plan d'alimentation « Équilibré » recommandé.";
            return "Un CPU au repos devrait rester bien en dessous de 60 °C : vérifie les processus en arrière-plan, le plan d'alimentation « Équilibré » et le refroidissement.";
        }
        static string GpuTune()
        {
            switch (Gpu)
            {
                case Vendor.Amd: return "Dans AMD Adrenalin › Performance › Réglage : courbe de ventilateurs plus agressive, ou undervolt (baisse la tension GPU de ~50 mV) qui réduit chauffe et bruit. Vérifie la poussière et l'aération du boîtier.";
                case Vendor.Nvidia: return "Avec MSI Afterburner (ou l'app NVIDIA) : courbe de ventilateurs plus agressive, ou undervolt via la courbe tension/fréquence (ex. 0,9 V pour la fréquence de jeu) : -10 à -15 °C sans perte visible. Vérifie la poussière et l'aération du boîtier.";
                case Vendor.Intel: return "Dans Intel Graphics Software / Arc Control : courbe de ventilateurs et limite de puissance. Vérifie la poussière et l'aération du boîtier.";
            }
            return "Vérifie la ventilation de la carte (poussière, ventilateurs qui tournent) et l'aération du boîtier ; un undervolt via l'outil du constructeur réduit souvent la chauffe.";
        }
        static string SsdTool()
        {
            var models = string.Join(" ", M.Disks.Select(d => d.Model.ToLowerInvariant()));
            var tools = new List<string>();
            if (models.Contains("samsung")) tools.Add("Samsung Magician");
            if (models.Contains("kingston")) tools.Add("Kingston SSD Manager");
            if (models.Contains("crucial") || models.Contains("micron")) tools.Add("Crucial Storage Executive");
            if (models.Contains("wd") || models.Contains("western") || models.Contains("sandisk")) tools.Add("WD/SanDisk Dashboard");
            if (models.Contains("seagate")) tools.Add("SeaTools");
            if (models.Contains("transcend")) tools.Add("Transcend Scope");
            if (models.Contains("intel") || models.Contains("solidigm")) tools.Add("Solidigm Storage Tool");
            if (models.Contains("corsair")) tools.Add("Corsair SSD Toolbox");
            if (models.Contains("adata")) tools.Add("ADATA SSD ToolBox");
            if (models.Contains("lexar")) tools.Add("Lexar DiskMaster");
            return tools.Count > 0 ? string.Join(", ", tools.Distinct()) : "l'outil du fabricant ou CrystalDiskInfo";
        }
        static string DiskLabel(string n, DiskInfo info)
        {
            var p = n.Split(' '); var letters = p.Length > 1 ? string.Join(" ", p.Skip(1)) : "n°" + n;
            if (info == null) return "disque " + letters;
            return (info.Kind == DiskKind.Nvme ? "NVMe " : info.Kind == DiskKind.Ssd ? "SSD " : info.Kind == DiskKind.Hdd ? "disque dur " : info.Kind == DiskKind.Usb ? "disque USB " : "disque ") + letters;
        }

        /// <summary>Conseils déclenchés par les mesures actuelles (triés par priorité).</summary>
        public List<Tip> Triggered()
        {
            var tips = new List<Tip>();
            if (_last == null) return tips;
            var s = _last;
            double cpu5 = S("cpu").Avg(5), cpu1 = S("cpu").Avg(1), mem5 = S("mem").Avg(5), page2 = S("pageIn").Avg(2);
            double temp2 = S("temp").Avg(2), tempMax5 = S("temp").Max(5), gpu2 = S("gpu").Avg(2), span = S("cpu").Span;
            // seuils thermiques : portables et Intel récents tournent chaud par conception
            double hot = Laptop ? 93 : IntelCpu ? 92 : 85, spike = Laptop ? 98 : 95, idleHot = Laptop ? 75 : IntelCpu ? 60 : 68;

            // --- CPU
            if (span >= 3 && cpu5 >= 80)
                tips.Add(new Tip { Id = "cpu.sustained", Priority = 8, TargetKey = "cpu", Title = "Processeur très sollicité depuis 5 min",
                    Text = "CPU à " + Fmt(cpu5, "%") + " en moyenne. Regarde l'onglet Processus : si c'est un programme que tu n'utilises pas (indexation, antivirus, mise à jour, navigateur en arrière-plan), ferme-le ou planifie-le la nuit. Si c'est un jeu ou un rendu, c'est normal.\nAstuce : Gestionnaire des tâches › Démarrage pour empêcher les gourmands de se relancer." + TopProc(s) });
            if (span >= 2 && !double.IsNaN(temp2) && temp2 >= hot)
                tips.Add(new Tip { Id = "temp.hot", Priority = 10, TargetKey = "temp", Title = "Le CPU chauffe fort (" + Fmt(temp2, "°C") + ")",
                    Text = "Au-delà de " + hot + "–" + (hot + 5) + " °C, ton " + CpuName + " réduit ses fréquences (throttling). À vérifier : dépoussiérage " + (Laptop ? "du ventilateur et des grilles" : "du ventirad") + ", pâte thermique (>3 ans ?), courbe de ventilateurs" + (Laptop ? "" : " dans le BIOS, et un flux d'air correct dans le boîtier") + ".\n" + CpuTune() });
            else if (span >= 5 && !double.IsNaN(temp2) && temp2 >= idleHot && cpu5 < 15)
                tips.Add(new Tip { Id = "temp.idleHigh", Priority = 5, TargetKey = "temp", Title = "CPU un peu chaud au repos (" + Fmt(temp2, "°C") + " pour " + Fmt(cpu5, "%") + " de charge)",
                    Text = CpuIdleTune(), Cooldown = TimeSpan.FromHours(3) });
            if (span >= 2 && !double.IsNaN(tempMax5) && tempMax5 >= spike)
                tips.Add(new Tip { Id = "temp.spike90", Priority = 9, TargetKey = "temp", Title = "Pic à " + Fmt(tempMax5, "°C") + " ces 5 dernières minutes",
                    Text = "Des pointes à " + spike + " °C+ indiquent que le refroidissement suit tout juste. Priorité : nettoyer " + (Laptop ? "les grilles et le ventilateur" : "le radiateur et vérifier les ventilateurs") + ". Ensuite : " + CpuTune() });

            // --- Mémoire
            if (span >= 3 && mem5 >= 85)
                tips.Add(new Tip { Id = "mem.high", Priority = 8, TargetKey = "memPct", Title = "Mémoire vive presque pleine (" + Fmt(mem5, "%") + ")",
                    Text = "Quand la RAM sature, Windows utilise le disque (fichier d'échange) et tout ralentit. Ferme les onglets/navigateurs inutiles (chaque onglet compte), regarde l'onglet Processus.\nRéglage : Paramètres système avancés › Performances › Mémoire virtuelle : laisse « Gérer automatiquement » activé. " + (M.RamGB >= 24 ? "Avec " + RamGo + ", si ça sature souvent, c'est un logiciel qui fuit : redémarre-le." : M.RamGB > 0 && M.RamGB <= 8 ? "Avec " + RamGo + " seulement, c'est vite plein : limite les onglets, et passer à 16 Go est l'amélioration la plus rentable pour cette machine." : "Si ça sature souvent, identifie le programme responsable (onglet Processus) ; sinon, un peu de RAM en plus rendra la machine plus confortable.") });
            if (span >= 2 && page2 >= 1500)
                tips.Add(new Tip { Id = "mem.paging", Priority = 7, TargetKey = "pageIn", Title = "Beaucoup de lectures de pages mémoire",
                    Text = Math.Round(page2) + " pages/s lues depuis le disque : signe de pression mémoire (RAM insuffisante pour ce que tu fais) ou d'un gros chargement (jeu, VM). Si c'est constant, ferme des applications ou augmente la RAM" + (M.RamGB > 0 ? " (actuellement " + RamGo + ")" : "") + "." });

            // --- Disques (perf) : seuils selon le type (NVMe / SSD / HDD / USB)
            foreach (var d in s.disks)
            {
                double lat = S("dlat:" + d.n).Avg(3), pct = S("dpct:" + d.n).Avg(3), q = S("dq:" + d.n).Avg(3);
                var info = M.DiskByPerfName(d.n); var kind = info?.Kind ?? DiskKind.Unknown;
                var lbl = DiskLabel(d.n, info);
                double latThr = kind == DiskKind.Nvme ? 20 : kind == DiskKind.Ssd ? 30 : kind == DiskKind.Hdd || kind == DiskKind.Usb ? 100 : 40;
                if (span >= 3 && !double.IsNaN(lat) && lat >= latThr && pct >= 10)
                {
                    string why = kind == DiskKind.Hdd ? "Un disque dur répond normalement en 10–20 ms ; à " + Fmt(lat, "ms") + " soutenu il est saturé (trop d'accès en parallèle : indexation, antivirus, mise à jour), très fragmenté, ou en difficulté.\nÀ vérifier : « Défragmenter et optimiser les lecteurs » (les HDD se fragmentent), les processus qui écrivent (onglet Processus), et son état SMART. Si tu peux, remplace-le par un SSD : c'est le changement le plus spectaculaire pour un PC."
                        : kind == DiskKind.Usb ? "Un disque USB dépend du câble, du port (USB 3 bleu ?) et du boîtier : latence haute = port USB 2, câble fatigué ou disque en veille. Évite d'y faire tourner des programmes."
                        : "Un " + (kind == DiskKind.Nvme ? "NVMe" : "SSD") + " sain répond en moins de " + (kind == DiskKind.Nvme ? "1–2" : "2–5") + " ms. Une latence de " + Fmt(lat, "ms") + " soutenue signale un disque saturé, qui chauffe, ou en difficulté.\nÀ vérifier : température du SSD (carte juste à côté), « Optimiser les lecteurs » (TRIM planifié hebdo), firmware du SSD, et dans les options d'alimentation › PCI Express › « Gestion de l'alimentation de l'état de liaison » = Désactivé.\nSi la latence reste haute alors que le disque n'est pas très actif : surveille-le de près (données à sauvegarder).";
                    tips.Add(new Tip { Id = "disk.lat:" + d.n, Priority = 9, TargetKey = "disk.lat:" + d.n, Title = "Latence élevée sur le " + lbl + " (" + Fmt(lat, "ms") + ")", Text = why });
                }
                if (span >= 3 && !double.IsNaN(pct) && pct >= 80)
                    tips.Add(new Tip { Id = "disk.busy:" + d.n, Priority = 7, TargetKey = "disk.pct:" + d.n, Title = lbl + " occupé à " + Fmt(pct, "%") + " depuis 3 min",
                        Text = "Quelque chose lit ou écrit en continu : indexation Windows Search, analyse Defender, synchronisation OneDrive/Drive, mise à jour, ou un jeu qui s'installe.\nSi ça se répète sans raison : Paramètres › Confidentialité › Recherche Windows › exclure les gros dossiers ; Sécurité Windows › planifier l'analyse la nuit." + (kind == DiskKind.Hdd ? "\nSur un disque dur, 100 % d'activité rend tout le PC lent : c'est normal pour un HDD système, et la vraie solution est un SSD." : "") });
            }
            // --- Disques (température SMART)
            foreach (var st in s.stor)
            {
                double t = S("stor:" + st.n).Avg(3);
                var info = M.DiskByStorName(st.n); bool hdd = info != null && info.Kind == DiskKind.Hdd;
                double thr = hdd ? 50 : 60;
                if (!double.IsNaN(t) && t >= thr)
                    tips.Add(new Tip { Id = "stor.hot:" + st.n, Priority = 8, TargetKey = "stor.temp:" + st.n, Title = st.n + " chauffe (" + Fmt(t, "°C") + ")",
                        Text = hdd ? "Un disque dur au-delà de 50 °C vieillit plus vite (mécanique + lubrifiant). Améliore le flux d'air devant la cage disques (ventilateur d'entrée), espace les disques, et évite de le coller contre une source chaude."
                                   : "Au-delà de 60–70 °C un SSD ralentit pour se protéger, et sa durée de vie baisse. " + (Laptop ? "Sur un portable : surélève-le et dépoussière ; évite les gros transferts posés sur un lit/canapé." : "Ajoute/vérifie le dissipateur M.2 de la carte mère, améliore le flux d'air (un ventilateur de boîtier qui souffle sur la zone M.2), et évite de le coller sous la carte graphique si un autre slot existe.") });
            }
            // --- GPU
            double gpuHot = Laptop ? 87 : 85;
            if (span >= 2 && !double.IsNaN(gpu2) && gpu2 >= gpuHot)
                tips.Add(new Tip { Id = "gpu.hot", Priority = 8, TargetKey = "gpuTemp", Title = "Carte graphique chaude (" + Fmt(gpu2, "°C") + ")",
                    Text = "Sur " + (M.MainGpu != null ? "une " + GpuName : "cette carte") + ", cœur > " + gpuHot + " °C (ou hotspot > 100 °C) = ventilation insuffisante. " + GpuTune() });

            // --- Santé disques (SMART)
            foreach (var st in s.stor)
            {
                if (st.health != 0)
                    tips.Add(new Tip { Id = "stor.health:" + st.n, Priority = 10, TargetKey = "stor.health:" + st.n, Title = st.n + " : état " + st.HealthText.ToLower(),
                        Text = "Windows signale ce disque comme « " + st.HealthText + " ». Sauvegarde immédiatement ce qu'il contient vers un autre disque, puis vérifie-le (" + SsdTool() + "). Ne l'utilise plus pour des données uniques.", Cooldown = TimeSpan.FromHours(2) });
                double newErr = st.Errors - ErrBase(st);
                if (newErr > 0)
                    tips.Add(new Tip { Id = "stor.errors:" + st.n, Priority = 9, TargetKey = "stor.health:" + st.n, Title = st.n + " : " + newErr + " nouvelle(s) erreur(s) depuis le lancement",
                        Text = "Des erreurs de lecture/écriture qui augmentent = disque, câble/slot ou alimentation en cause. C'est le signal précoce classique avant qu'un disque lâche. Sauvegarde, puis contrôle avec " + SsdTool() + " ; si ça continue, remplace-le.", Cooldown = TimeSpan.FromHours(1) });
                if (st.wear >= 80)
                    tips.Add(new Tip { Id = "stor.wear:" + st.n, Priority = 6, TargetKey = "stor.health:" + st.n, Title = st.n + " usé à " + Math.Round(st.wear) + " %",
                        Text = "Le SSD approche de la fin de son endurance garantie (" + Math.Round(st.hours) + " h de fonctionnement). Il continuera un moment, mais planifie son remplacement et garde une sauvegarde à jour.", Cooldown = TimeSpan.FromHours(24) });
            }
            // --- Fuites mémoire / handles
            if (s.Leaks != null)
                foreach (var l in s.Leaks.Where(x => (x.SpanMin >= 50 && x.MemDelta1hMB >= 400) || (x.SpanMin >= 150 && x.MemDelta3hMB >= 1000) || (x.SpanMin >= 50 && x.HandlesDelta1h >= 5000)).Take(2))
                {
                    var why = l.MemDelta1hMB >= 400 ? "+" + Math.Round(l.MemDelta1hMB) + " Mo en 1 h" : l.MemDelta3hMB >= 1000 ? "+" + (l.MemDelta3hMB / 1024).ToString("0.0") + " Go en 3 h" : "+" + Math.Round(l.HandlesDelta1h) + " handles en 1 h";
                    tips.Add(new Tip { Id = "leak:" + l.Name, Priority = 6, TargetKey = "memPct", Title = l.Name + " grossit sans arrêt (" + why + ")",
                        Text = "Ce programme occupe " + Math.Round(l.MemNowMB) + " Mo et ne relâche pas ce qu'il prend : fuite mémoire probable (onglets, extension, plugin, ou bug). Ferme-le et relance-le avant que la RAM sature — c'est plus rapide qu'un redémarrage complet. S'il récidive tous les jours, cherche une mise à jour ou désactive ses extensions.", Cooldown = TimeSpan.FromHours(2) });
                }
            // --- Ventilateurs (seulement si la machine a déjà exposé des ventilateurs qui tournent)
            if (M.CapFans && s.fans != null && s.fans.Count > 0 && !double.IsNaN(temp2) && temp2 >= (Laptop ? 70 : 60) && s.fans.All(f => f.rpm == 0))
                tips.Add(new Tip { Id = "fan.stopped", Priority = 8, TargetKey = "temp", Title = "Aucun ventilateur ne tourne alors que le CPU chauffe",
                    Text = "Les capteurs renvoient 0 tr/min pour tous les ventilateurs avec un CPU à " + Fmt(temp2, "°C") + ". Vérifie le branchement des ventilateurs (CPU_FAN / CHA_FAN) et la courbe dans le BIOS. Si le capteur n'est simplement pas lu (0 en permanence même en charge), ignore ce message.", Cooldown = TimeSpan.FromHours(6) });
            // --- Batterie (portables)
            if (s.bat.HasValue && s.ac == false && s.bat.Value <= 15)
                tips.Add(new Tip { Id = "bat.low", Priority = 7, TargetKey = "bat", Title = "Batterie faible (" + s.bat.Value.ToString("0") + " %)",
                    Text = "Branche le chargeur : sous 10 % Windows passe en économie d'énergie forcée puis met en veille. Pour préserver la batterie sur la durée, évite de la laisser souvent sous 20 % ou en charge à 100 % en permanence (certains constructeurs proposent une limite à 80 %).", Cooldown = TimeSpan.FromHours(1) });
            if (s.bat.HasValue && s.ac == false && span >= 3 && cpu5 >= 50)
                tips.Add(new Tip { Id = "bat.cpu", Priority = 4, TargetKey = "cpu", Title = "Sur batterie avec un CPU très actif",
                    Text = "À " + Fmt(cpu5, "%") + " de CPU sur batterie, l'autonomie fond. Regarde l'onglet Processus ; s'il s'agit d'une tâche de fond (indexation, mise à jour, synchronisation), elle peut attendre le secteur. Le mode d'alimentation « Meilleure efficacité » (icône batterie) aide aussi.", Cooldown = TimeSpan.FromHours(2) });
            // --- Démarrage
            double slowBoot = M.SystemOnSsd ? 60000 : 120000;
            if (LastBoot != null && LastBoot.bootMs >= slowBoot)
                tips.Add(new Tip { Id = "boot.slow", Priority = 4, TargetKey = "reboot", Title = "Dernier démarrage lent : " + Math.Round(LastBoot.bootMs / 1000) + " s",
                    Text = (LastBoot.slow.Count > 0 ? "Ralentisseurs relevés par Windows : " + string.Join(", ", LastBoot.slow.Take(3).Select(x => x.n + " (" + Math.Round(x.ms / 1000) + " s)")) + ". " : "") + (M.SystemOnSsd ? "Un PC sur SSD démarre en 20–30 s. " : "Sur disque dur, 60–90 s est courant ; un SSD système diviserait ce temps par 3. ") + "Gestionnaire des tâches › Démarrage : désactive ce qui n'est pas indispensable, et vérifie que le démarrage rapide est activé (Options d'alimentation › Choisir l'action des boutons).", Cooldown = TimeSpan.FromHours(24) });

            // --- Redémarrage
            if (s.rb != null && s.rb.score >= 70)
                tips.Add(new Tip { Id = "reboot.needed", Priority = 7, TargetKey = "reboot", Title = "Un redémarrage ferait du bien (score " + s.rb.score + "/100)",
                    Text = "Pourquoi : " + string.Join(" ; ", s.rb.Reasons) + ".\nRedémarre (vraiment : « Redémarrer », pas « Arrêter » — avec le démarrage rapide de Windows, arrêter ne vide pas le noyau). Ça finalise les mises à jour, libère les handles/mémoire des programmes qui fuient et remet les pilotes à zéro.", Cooldown = TimeSpan.FromHours(4) });
            else if (s.rb != null && s.rb.score >= 30 && S("cpu").Span >= 5)
                tips.Add(new Tip { Id = "reboot.soon", Priority = 3, TargetKey = "reboot", Title = "Redémarrage conseillé prochainement",
                    Text = "Rien d'urgent, mais " + string.Join(" ; ", s.rb.Reasons) + ". Profite d'une pause pour redémarrer (menu Démarrer › Marche/Arrêt › Redémarrer).", Cooldown = TimeSpan.FromHours(6) });

            // --- Réseau
            double rx3 = S("rx").Avg(3);
            if (span >= 3 && rx3 >= 20)
                tips.Add(new Tip { Id = "net.dl", Priority = 4, TargetKey = "rx", Title = "Gros téléchargement en cours (" + Fmt(rx3, "Mo/s") + ")",
                    Text = "Si ce n'est pas toi : Windows Update, Steam/Epic, OneDrive ou une mise à jour de jeu. Paramètres › Windows Update › Options avancées › Optimisation de la distribution : limite la bande passante ou désactive le partage.", Cooldown = TimeSpan.FromHours(2) });

            foreach (var t in tips) t.Triggered = true;
            return tips.Where(t => !_dismissed.Contains(t.Id) && !OnCooldown(t)).OrderByDescending(t => t.Priority).ToList();
        }
        public Metrics.BootEntry LastBoot { get; set; }
        static readonly Dictionary<string, double> _errBase = new Dictionary<string, double>();
        static double ErrBase(StorSample st) { if (!_errBase.TryGetValue(st.n, out var b)) _errBase[st.n] = b = st.Errors; return b; }
        static string TopProc(Sample s) => s.procs != null && s.procs.Count > 0 ? "\nEn tête actuellement : " + s.procs[0].n + " (" + s.procs[0].cpu + " %)." : "";
        bool OnCooldown(Tip t) => _lastShown.TryGetValue(t.Id, out var when) && DateTime.Now - when < t.Cooldown;
        public void MarkShown(Tip t) => _lastShown[t.Id] = DateTime.Now;

        /// <summary>Astuce générale suivante (rotation, en évitant celles déjà écartées et celles qui ne concernent pas cette machine).</summary>
        public Tip NextGeneral()
        {
            var pool = General.Where(t => !_dismissed.Contains(t.Id) && Applies(t)).ToList();
            if (pool.Count == 0) return null;
            var tip = pool[_generalIdx % pool.Count]; _generalIdx++;
            return Mat(tip);
        }
        public Tip RandomGeneral()
        {
            var pool = General.Where(t => !_dismissed.Contains(t.Id) && Applies(t)).ToList();
            return pool.Count == 0 ? null : Mat(pool[_rnd.Next(pool.Count)]);
        }
        static Tip Mat(GeneralTip g)
        {
            string txt; try { txt = g.Build(); } catch (Exception ex) { txt = "(astuce indisponible : " + ex.Message + ")"; }
            return new Tip { Id = g.Id, TargetKey = g.TargetKey, Title = g.Title, Text = txt, Priority = g.Priority, Cooldown = g.Cooldown };
        }
        static bool Applies(GeneralTip t) => t.When == null || t.When();

        /// <summary>Astuce générale : texte construit à l'affichage (il dépend de la machine) + condition d'applicabilité.</summary>
        class GeneralTip : Tip { public Func<bool> When; public Func<string> Build; }
        static GeneralTip Gen(string id, string target, string title, Func<string> text, Func<bool> when = null) => new GeneralTip { Id = id, TargetKey = target, Title = title, When = when, Build = text };

        static readonly List<GeneralTip> General = new List<GeneralTip>
        {
            Gen("g.power", "cpu", "Plan d'alimentation : « Équilibré » suffit", () => (M.Cpu.IsRyzen ? "Sur les Ryzen, le plan « Équilibré » de Windows donne les mêmes performances que « Performances élevées » mais laisse le CPU se reposer (moins de chauffe, moins de bruit)." : "Le plan « Équilibré » de Windows laisse le CPU descendre en fréquence au repos sans brider les performances en charge (moins de chauffe, moins de bruit" + (Laptop ? ", plus d'autonomie" : "") + ").") + " Panneau de configuration › Options d'alimentation. Évite « Performances ultimes » hors benchmark."),
            Gen("g.docp", "memPct", "Ta RAM tourne-t-elle à sa vraie vitesse ?", () => "Par défaut la mémoire tourne souvent à sa vitesse de base (2133/2666 MHz en DDR4, 4800 en DDR5) au lieu de sa vitesse nominale. Dans le BIOS, active le profil " + (AmdCpu ? "DOCP / EXPO" : "XMP") + ". " + (M.RamMHz > 0 ? "Actuellement détectée à " + M.RamMHz + " MHz" + (M.RamType != null ? " (" + M.RamType + ")" : "") + "." : "") + (M.Cpu.IsRyzen && M.RamType == "DDR4" ? " Sur Ryzen 5000, 3600 MHz CL16–18 avec FCLK 1800 est le point idéal (+5 à 15 % dans les jeux)." : ""), () => !Laptop),
            Gen("g.dualchannel", "memPct", "Une seule barrette de RAM : simple canal", () => "Ta machine a une seule barrette (" + RamGo + "). En ajoutant une seconde barrette identique (même capacité, même vitesse), la mémoire passe en double canal : +10 à 20 % dans les jeux et les applis lourdes" + (!M.HasDedicatedGpu ? ", et bien plus pour le graphique intégré qui utilise la RAM" : "") + ". Vérifie qu'il reste un slot libre (onglet Machine › " + (M.RamModules.Count > 0 ? M.RamModules[0].Slot : "slot") + " occupé).", () => M.SingleChannel),
            Gen("g.pbo", "temp", "Curve Optimizer : moins chaud, aussi rapide", () => "Réglage n°1 pour un " + CpuName + " : BIOS › Advanced › AMD Overclocking › PBO › Curve Optimizer › All cores › Negative 15 (puis tester la stabilité, descendre à -20/-25 si stable). Résultat typique : -10 °C et boost plus soutenu.", () => M.Cpu.IsRyzen && !Laptop),
            Gen("g.intelpl", "temp", "Limites de puissance Intel : le réglage qui calme tout", () => "Beaucoup de cartes mères laissent le " + CpuName + " sans limite de puissance (« PL1/PL2 = 4096 W ») : il chauffe pour 2–3 % de perfs. BIOS › CPU Power Management : mets les limites Intel officielles de ton modèle (ex. 125 W / 253 W). Moins de chauffe, moins de bruit, quasi mêmes performances.", () => IntelCpu && !Laptop),
            Gen("g.trim", null, "TRIM hebdomadaire pour les SSD", () => "Tape « Défragmenter et optimiser les lecteurs » : vérifie que l'optimisation planifiée est activée (hebdomadaire) et que " + (M.Disks.Count(d => d.IsSsd) > 1 ? "tes SSD sont listés « OK »" : "ton SSD est listé « OK »") + ". Le TRIM garde les SSD rapides et prolonge leur vie." + (M.Disks.Any(d => d.Kind == DiskKind.Hdd) ? " Le disque dur, lui, a besoin d'une vraie défragmentation (même outil)." : ""), () => M.Disks.Any(d => d.IsSsd)),
            Gen("g.pcie", null, "Évite les micro-coupures des SSD NVMe", () => "Options d'alimentation › Modifier les paramètres avancés › PCI Express › Gestion de l'alimentation de l'état de liaison : « Désactivé »" + (Laptop ? " (sur secteur ; laisse « Économie » sur batterie)" : " (sur secteur)") + ". Ça supprime des latences disque et des saccades bizarres sur certains SSD.", () => M.Disks.Any(d => d.Kind == DiskKind.Nvme)),
            Gen("g.hdd2ssd", null, "Le meilleur upgrade possible : un SSD système", () => "Windows tourne sur un disque dur (" + (M.SystemDisk?.Model ?? "") + "). Cloner le système sur un SSD (même SATA à 30 €) divise le temps de démarrage par 3 et rend tout le PC réactif : c'est de très loin l'amélioration la plus rentable pour cette machine.", () => M.SystemDisk != null && M.SystemDisk.Kind == DiskKind.Hdd),
            Gen("g.startup", "cpu", "Démarrage allégé : à re-contrôler de temps en temps", () => "Les programmes réactivent volontiers leur lancement automatique après une mise à jour. Gestionnaire des tâches › Démarrage : garde seulement l'essentiel (antivirus, pilotes audio/souris, PerfMonitor 😉). Un démarrage sain " + (M.SystemOnSsd ? "sur SSD : 20–30 s bureau inclus." : "sur disque dur : 60–90 s ; en dessous d'une minute, c'est bien.")),
            Gen("g.gamemode", "gpuTemp", "Mode Jeu et HAGS", () => "Paramètres › Jeux › Mode Jeu : activé (Windows priorise le jeu). Paramètres › Système › Affichage › Graphiques › Planification GPU à accélération matérielle (HAGS) : activé avec " + (M.MainGpu != null ? "une " + GpuName : "une carte récente") + " sur des pilotes récents, ça réduit la latence.", () => M.HasDedicatedGpu),
            Gen("g.adrenalin", "gpuTemp", "Adrenalin : les deux réglages qui comptent", () => "AMD Software › Jeux › Graphiques : « Radeon Anti-Lag » ON pour la réactivité, et « Radeon Chill » pour plafonner les FPS au taux de ton écran (moins de chauffe et de bruit sans perte visible). Désactive « Instant Replay » si tu ne t'en sers pas : ça consomme en permanence.", () => Gpu == Vendor.Amd && M.HasDedicatedGpu),
            Gen("g.nvidia", "gpuTemp", "NVIDIA : les réglages qui comptent", () => "Panneau de configuration NVIDIA › Gérer les paramètres 3D : « Mode de gestion de l'alimentation » = Normal (pas « Performances maximales », qui garde la " + GpuName + " à fond au repos), « Fréquence d'images max » = taux de ton écran (moins de chauffe/bruit), et « Reflex » ON dans les jeux qui le proposent. Dans l'app NVIDIA, désactive « Instant Replay » si inutilisé.", () => Gpu == Vendor.Nvidia),
            Gen("g.igpu", "cpu", "Graphique intégré : la RAM fait office de VRAM", () => "Ta " + GpuName + " n'a pas de mémoire dédiée : elle prend sur la RAM. Plus tu as de RAM (et en double canal : deux barrettes identiques), plus elle est rapide. Pour jouer, baisse la résolution/qualité et active FSR/XeSS quand c'est proposé.", () => !M.HasDedicatedGpu && M.Gpus.Count > 0),
            Gen("g.pagefile", "pageIn", "Fichier d'échange : laisse Windows gérer", () => (M.RamGB >= 16 ? "Avec " + RamGo + " de RAM, inutile de le désactiver (certains jeux plantent sans)." : "Avec " + RamGo + " de RAM, il est indispensable.") + " Garde-le en « Taille gérée automatiquement » sur le disque système" + (M.Disks.Count(d => d.IsSsd) > 1 ? " ; si tu veux libérer le SSD système, tu peux le déplacer sur l'autre SSD" : "") + "."),
            Gen("g.backup", null, "Sauvegarde : le vrai réglage de performance", () => "Un disque qui lâche, c'est des jours perdus. Historique des fichiers (Paramètres › Sauvegarde) vers un autre disque, ou une copie régulière de tes dossiers critiques vers un disque externe/cloud." + (M.Disks.Count > 1 ? " Un second disque interne est pratique comme destination — mais pas comme seule copie." : " Avec un seul disque interne, un disque externe ou le cloud est indispensable.")),
            Gen("g.storage", null, "Assistant de stockage", () => "Paramètres › Système › Stockage › Assistant de stockage : supprime la corbeille et les fichiers temporaires automatiquement. Garde 15–20 % de libre sur " + (M.SystemOnSsd ? "le SSD système : en dessous, il ralentit (moins de cellules libres pour l'écriture)." : "le disque système.")),
            Gen("g.fans", "temp", "Courbe de ventilateurs : silence au repos, souffle en charge", () => "BIOS › Q-Fan / Smart Fan : ventilateurs boîtier à 30–40 % jusqu'à 60 °C CPU puis montée franche jusqu'à 100 % à 80 °C. Un boîtier avec 2 entrées devant + 1 sortie arrière suffit pour un " + CpuName + (M.HasDedicatedGpu ? " + " + GpuName : "") + ".", () => !Laptop),
            Gen("g.laptopcool", "temp", "Portable : les 3 gestes anti-chauffe", () => "1) Surélève l'arrière (support ou simple cale) : l'air entre par-dessous. 2) Dépoussière les grilles tous les 6 mois (bombe à air, portable éteint). 3) Sur secteur, mode « Équilibré » ; sur batterie, « Meilleure efficacité ». Et jamais sur un lit ou un canapé pendant un jeu.", () => Laptop),
            Gen("g.battery", "bat", "Batterie : la garder en forme", () => "Une batterie vieillit surtout par la chaleur et les 100 % permanents. Si ton constructeur propose une limite de charge (80 %) dans son utilitaire (MyASUS, Lenovo Vantage, Dell Power Manager, HP…), active-la quand tu es souvent branché. Vérifie sa santé : powercfg /batteryreport dans un terminal.", () => M.HasBattery),
            Gen("g.thresholds", "cpu", "Ajuste mes seuils à ton usage", () => "Les seuils sur chaque carte se modifient à la volée (valeur + durée). Ex : si tu joues souvent, monte le CPU à 95 % / 120 s pour éviter les fausses alertes ; garde les températures serrées (CPU " + (Laptop ? 95 : IntelCpu ? 95 : 85) + ", GPU 90, SSD 65" + (M.Disks.Any(d => d.Kind == DiskKind.Hdd) ? ", HDD 50" : "") + ")."),
            Gen("g.report", null, "Le rapport 24 h pour voir les tendances", () => "Le bouton « Rapport 24 h » ouvre des graphiques sur la journée : pratique pour repérer un programme qui s'emballe la nuit ou un disque qui chauffe pendant les sauvegardes. Ajoute un marqueur (.\\note.ps1 \"texte\") avant/après un réglage pour comparer."),
            Gen("g.machine", null, "L'onglet Machine connaît ton matériel", () => "PerfMonitor a détecté : " + CpuName + (M.MainGpu != null ? ", " + GpuName : "") + ", " + RamGo + " de RAM, " + M.Disks.Count + " disque(s). Les seuils par défaut, les cartes affichées et mes conseils s'y adaptent. Si tu changes un composant, clique « Re-scanner » dans l'onglet Machine.", () => M.Cpu.Name != "?"),
            Gen("g.updates", null, "Pilotes à jour, mais pas n'importe comment", () => (AmdCpu ? "Chipset AMD (site AMD, pas Windows Update) : le pilote chipset gère les C-states et le plan Ryzen. " : IntelCpu ? "Chipset/ME Intel : depuis le site du constructeur de la carte mère" + (Laptop ? " ou du portable" : "") + ". " : "") + "GPU : " + (Gpu == Vendor.Amd ? "Adrenalin « recommandé » plutôt que « optionnel » si tu veux la stabilité." : Gpu == Vendor.Nvidia ? "pilote « Game Ready » ou « Studio » depuis nvidia.com ; installation propre en cas de souci." : Gpu == Vendor.Intel ? "pilote graphique Intel depuis intel.com." : "pilote depuis le site du fabricant.") + " BIOS : mets à jour si une version plus récente corrige des soucis mémoire/USB" + (Laptop ? " (via l'utilitaire du constructeur)" : "") + "."),
        };
    }
}
