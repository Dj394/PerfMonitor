# PerfMonitor

Supervision temps réel d'un PC Windows (CPU, RAM, disques, températures, ventilateurs, GPU, réseau, batterie) avec alertes, historique, détecteur de fuites mémoire et un **conseiller** qui explique quoi régler. S'adapte automatiquement à la machine sur laquelle il tourne (fixe/portable, AMD/Intel, NVIDIA/AMD/Intel, NVMe/SSD/HDD).

## Installation rapide (utilisateur)
1. Prérequis : Windows 10/11 x64 et le **[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)** (« Run desktop apps » x64) — ou prendre la variante *Portable* de la Release, qui l'inclut.
2. Télécharger la dernière **Release** (`PerfMonitorLive.exe`) et la placer dans un dossier de votre choix avec `install-live.ps1`, `report.ps1`, `template.html`, `note.ps1` (ou cloner ce dépôt et lancer `.uild.ps1` — nécessite le SDK .NET 10).
3. Clic droit sur `install-live.ps1` › *Exécuter avec PowerShell* → une invite UAC (l'appli tourne en administrateur pour lire les températures). Ça crée la tâche planifiée « PerfMonitorLive » (démarrage à l'ouverture de session) et les raccourcis Bureau / menu Démarrer.
4. Au premier lancement : SmartScreen peut afficher « Windows a protégé votre PC » (exe non signé) → *Informations complémentaires* › *Exécuter quand même*. L'onglet **Machine** montre ce qui a été détecté et quels capteurs sont disponibles.

Désinstaller : Quitter (menu de l'icône), puis en PowerShell admin `Unregister-ScheduledTask PerfMonitorLive -Confirm:$false`, supprimer les raccourcis et le dossier. Rien n'est écrit ailleurs que dans le dossier de l'application (`data\`, `settings.json`).

Limites connues : certains antivirus d'entreprise bloquent le pilote de LibreHardwareMonitor (accès capteurs) → l'application fonctionne, sans températures/ventilateurs. Testé sur PC fixe AMD ; les autres configurations sont gérées mais moins éprouvées — signalez les soucis avec le contenu de l'onglet Machine (bouton **Copier**) et `data\live.log`.

## PerfMonitorLive.exe — supervision temps réel + alertes (recommandé)
Application C# WPF (.NET 10), un seul exe. Lancée automatiquement à l'ouverture de session par la **tâche planifiée `PerfMonitorLive`** (mode administrateur — nécessaire pour lire les capteurs), réduite dans la zone de notification. Raccourci **« PerfMonitor Live »** sur le Bureau et dans le menu Démarrer (réveille l'instance en cours ou la lance via la tâche, sans invite UAC).

### Onglets
- **Live** — une carte par métrique : CPU, RAM, températures CPU/GPU, fréquences CPU/GPU, consommation CPU/GPU, réseau, pression mémoire, score de redémarrage, par disque (activité / débit / latence), par disque physique (**santé SMART** : état Windows, erreurs L/É depuis le lancement, usure, heures ; **température**), **ventilateurs** (RPM carte mère + GPU). Valeur colorée selon le niveau, jauge, mini-graphique 5 min (rouge = montée brusque, vert = descente), badge de tendance, **seuil réglable sur la carte**. La grille s'adapte à la fenêtre (plein écran = cartes plus grandes). Le **conseiller** (mascotte) se déplace vers la carte concernée et donne des conseils (règles hors ligne + astuces BIOS/Windows/Adrenalin).
- **Historique** — courbes agrégées 1 h / 6 h / 24 h / 7 j des métriques choisies (molette = zoom, glisser = déplacer, survol = valeurs), **marqueurs de réglage** (bouton « Marquer un réglage » ou `note.ps1`), **comparaison avant / après** un marqueur (moyenne, p95, max, Δ %), **démarrages Windows** (temps de boot par jour + programmes lents, source : journal Diagnostics-Performance — attention : avec le « démarrage rapide » Windows, un arrêt/allumage n'est pas un vrai démarrage et n'est pas journalisé), **sessions de jeu**, **alertes**. Bouton Rapport HTML conservé.
- **Machine** — inventaire matériel détecté au démarrage (`data\inventory.json`, ~0,3 s, sans droits admin) : système (fixe/portable, batterie, écrans, réseau), processeur + carte mère + BIOS, carte(s) graphique(s) dédiée/intégrée + VRAM + pilote, RAM (barrettes, type, vitesse, simple/double canal, XMP inactif), stockage (modèle, NVMe/SSD/HDD/USB, taille, lettres, disque système), capteurs réellement disponibles (températures, ventilateurs, SMART, conso, fréquences). Boutons **Re-scanner** (après un changement de composant) et **Copier**.
- **Processus** — top 6 live (CPU, RAM, handles) + colonne **Δ 1 h** du détecteur de fuites (suivi mémoire/handles par processus sur 3 h) ; les fuites probables sont listées et le conseiller prévient.
- **Paramètres** — notifications (écran, coin, durée, son, cooldown), **profils de seuils** (Travail / Jeu / Nuit, automatique selon jeu détecté et heures de nuit), **jeux / overlay / widget**, **Telegram + bilan quotidien**, **apparence** (thème comme Windows / sombre / clair, mode compact), **benchmark**, démarrage auto et conseiller.

### Portabilité : l'application s'adapte à la machine
- **Cartes** : CPU, RAM, température CPU, réseau, pression mémoire et score de redémarrage sont toujours là ; **température/fréquence/consommation GPU, fréquence/conso CPU, batterie, disques, SMART, ventilateurs n'apparaissent que si la machine fournit la mesure** (pas de carte GPU vide sur un PC sans capteur, carte Batterie sur portable, disques nommés « NVMe C: », « SSD D: », « HDD E: », « USB F: »).
- **Seuils par défaut** (première installation, `MachineTuned` dans `settings.json`) : température CPU 85 °C (Ryzen fixe) / 95 °C (Intel ou portable), latence disque 20 ms NVMe / 30 ms SSD / 100 ms HDD-USB, température disque 65 °C SSD / 50 °C HDD, consommations selon CPU/GPU. Une installation existante garde ses seuils.
- **Conseiller** : les conseils et astuces sont rédigés d'après l'inventaire (Ryzen → PBO/Curve Optimizer, Intel → limites PL1/PL2/undervolt, portable → surélévation/dépoussiérage/mode d'alimentation, GPU AMD → Adrenalin, NVIDIA → Afterburner/panneau NVIDIA, Intel Arc, GPU intégré, HDD → défragmentation/passer au SSD, RAM réelle, simple canal, batterie, outil SSD du fabricant détecté…) ; les astuces qui ne concernent pas la machine ne sont jamais proposées.
- Installer sur une autre machine : copier le dossier (exe + `install-live.ps1` + `report.ps1`/`template.html`), lancer `.\install-live.ps1` (une invite UAC). Prérequis : Windows 10/11 x64 + **.NET 10 Desktop Runtime** — ou compiler un exe autonome avec `.uild.ps1 -Portable` (runtime inclus, ~150 Mo).

### Empreinte
Mesurée sur un Ryzen 5800X : **≈ 0,1 % CPU et ~60 Mo de RAM** réduit dans la zone de notification (≈ 1 % / 170 Mo fenêtre ouverte, à cause du rendu des graphiques). Processus lus par l'API .NET toutes les 5 s, capteurs carte mère toutes les 5 s, SMART toutes les 30 s. **Mode économie automatique** (Paramètres, désactivable) : 1 mesure toutes les 2 s sur batterie ou fenêtre fermée depuis > 5 min — l'historique 5 s, les alertes et les rapports ne changent pas. La ligne « Coût de PerfMonitor » de l'onglet Machine affiche la consommation en direct.

### Fonctions transverses
- **Notifications** sur l'écran choisi (par défaut le secondaire), empilées, avec **boutons d'action** : Voir les processus / Terminer <processus> (CPU, RAM), Optimiser les lecteurs (disque), Redémarrer maintenant (score) avec annulation, Voir les conseils (températures), Gestion des disques (SMART).
- **Mode jeu** : application plein écran (ou processus de la liste « Jeux ») → notifications réduites aux critiques, profil « Jeu » si automatique, **bandeau overlay** en haut de l'écran des notifications (CPU / temp / GPU / RAM / disque), et à la fin **rapport de session** (toast + `data\sessions.jsonl` + onglet Historique).
- **Widget flottant** (menu tray ou Paramètres) : CPU · temp · GPU · RAM · disque, déplaçable, opacité réglable, position mémorisée ; double-clic = ouvrir.
- **Bilan quotidien** : première session après 7 h → toast persistant (résumé de la veille : CPU/RAM/temp max, disques, alertes, score reboot, sessions de jeu) ; menu tray « Bilan de la veille » pour le voir à la demande.
- **Telegram** : bot via @BotFather, token + chat ID dans Paramètres (bouton « Détecter mon chat ID » après avoir écrit au bot) ; alertes critiques et/ou bilan quotidien. Token chiffré (DPAPI utilisateur).
- **Benchmark** (Paramètres) : CPU multi/mono-thread (SHA-256), RAM (copie), disque (1 Go séquentiel + 4 Ko aléatoire, fichier temporaire supprimé) ; tableau cette fois / précédente / meilleure + Δ %, marqueur automatique dans l'historique (`data\bench.jsonl`).
- **Historique** écrit toutes les 5 s dans `data\perf-AAAA-MM-JJ.jsonl` (+ `alerts.jsonl`, `sessions.jsonl`, `boot.jsonl`, `bench.jsonl`, `notes.jsonl`). Réglages dans `settings.json`.

### Maintenance
- Recompiler après modification du code (`app\`) : `.\build.ps1` (arrête la tâche, publie, copie l'exe). Relancer : `Start-ScheduledTask PerfMonitorLive`.
- (Ré)installer tâche + raccourcis (une invite UAC) : `.\install-live.ps1`.
- Arguments de test : `--here` (ne pas déléguer à la tâche élevée), `--demo` (un conseil après 6 s), `--digest` (bilan de la veille au lancement), `--tray` (démarrage réduit).
- Désinstaller : Quitter via le menu, `Unregister-ScheduledTask PerfMonitorLive -Confirm:$false` (admin), supprimer les raccourcis et le dossier.

## Graphiques HTML (ancien rapport, toujours disponible)
- `Rapport 24h.cmd` / `Rapport 7 jours.cmd`, ou `.\report.ps1 -Hours 6`. Marqueur : `.\note.ps1 "texte"`.

## Ancien collecteur PowerShell (remplacé, conservé en secours)
`collect.ps1` + `install-task.ps1`. Ne pas lancer en même temps que l'appli (doublons dans l'historique).

## Licence
MIT — voir `LICENSE`. Utilise [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL 2.0).
