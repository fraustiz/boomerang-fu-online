# Boomerang Fu Multiplayer — mod en ligne (v2)

Mod BepInEx 5 qui ajoute un mode **en ligne pair-à-pair** (jusqu'à 4 joueurs, un par PC), sans serveur.
Les menus sont ceux du jeu : un bouton **EN LIGNE** sous « Jouer » dans le menu principal ouvre un écran
construit à partir du menu pause du jeu (mêmes polices, boutons, navigation manette, retour).

## Installation (chaque joueur)
1. BepInEx 5.4.22 x64 dans le dossier du jeu.
2. Copier `BepInEx\plugins\BoomerangFuMultiplayer\` (BoomerangFuMultiplayer.dll + LiteNetLib.dll).
3. Tout le monde doit avoir **la même version du mod** (vérifiée à la connexion) et, idéalement, les mêmes DLC.

Compiler : `dotnet build -c Release` dans `ModSource\BoomerangFuMultiplayer\` (sortie directe dans `BepInEx\plugins`).

## Jouer
### Via Steam (recommandé)
- Hôte : EN LIGNE → **HÉBERGER UNE PARTIE** → **INVITER DES AMIS** (overlay Steam).
- Ami : accepter l'invitation Steam (le jeu se lance et rejoint seul), ou EN LIGNE → **REJOINDRE <nom>**.
- Pas de port à ouvrir : la connexion passe par le P2P de Steam (traversée NAT et relais Steam).

### Via IP directe (LAN, Hamachi, ZeroTier, redirection de port)
- Hôte : **HÉBERGER EN IP DIRECTE** (UDP 7777 par défaut) → **COPIER MON ADRESSE IP**, l'envoyer.
- Ami : copier l'adresse (`ip` ou `ip:port`) dans le presse-papiers → EN LIGNE → **REJOINDRE <adresse>**.
- Pare-feu de l'hôte : `New-NetFirewallRule -DisplayName "BFu Online" -Direction Inbound -Protocol UDP -LocalPort 7777 -Action Allow`

### Ensuite
- L'hôte choisit **JOUER** : tout le monde arrive dans l'écran de sélection des personnages, chacun rejoint
  avec sa manette/son clavier comme en local. L'hôte ajoute des bots, règle le match et lance.
- Les joueurs distants peuvent aussi naviguer dans les menus de l'hôte (comme une manette branchée chez lui).
- Pause : la pause de l'hôte (ou la touche Start d'un joueur) met tout le monde en pause.
- Client : **Échap deux fois** pour quitter la session. Le badge en haut à droite indique l'état et le ping.

## Comment ça marche
- **Entrées** : les joueurs Rewired 0..3 deviennent les 4 places en ligne. Chaque PC envoie les entrées de sa
  manette (≈60/s) ; chez les autres, elles alimentent la manette virtuelle de sa place. Le jeu lit tout via
  Rewired, donc les joueurs distants sont gérés partout comme des joueurs locaux.
- **Menus** : les menus « maîtres » tournent chez l'hôte ; les clients reproduisent sa sélection, ses clics et
  sa pile de menus. Le lobby est corrigé par l'hôte 5 fois/s.
- **Match** : départ commun (graine aléatoire, réglages, liste de niveaux, joueurs), mêmes graines à chaque
  manche ; l'hôte fait autorité sur les morts, les réapparitions, les fins de manche, les scores et les
  power-ups.
- **Clients passifs (v2.2)** : seul l'hôte simule le match. Chez les clients, la physique, les collisions,
  les ramassages, les coups et les actions locales des personnages et des boomerangs sont coupés. Ils
  affichent l'état de l'hôte (30 instantanés/s : position, vitesse, rotation, visée, dash, étourdissement,
  boomerangs en main, actions), interpolé environ 75 ms dans le passé. Les attaques et les lancers sont
  rejoués seulement pour l'animation. Les boomerangs ont un identifiant réseau : le client crée un
  « fantôme » pour chaque boomerang de l'hôte et retire tous les autres. Contrepartie : le personnage d'un
  client réagit avec le délai du réseau (ping + environ 0,1 s).
- **Fenêtre inactive** : en ligne, le module d'entrée des menus reste actif même si la fenêtre n'est pas au
  premier plan (sinon un hôte en arrière-plan ne peut plus valider « Continuer »).
- **Rejoindre en cours de partie** : on peut se connecter pendant un match. Comme avec le drop-in natif du
  jeu, le nouveau joueur remplace le bot de sa place à la fin de la manche en cours (niveau suivant, numéro
  de manche et scores repris de l'hôte). Sans bot à remplacer, il joue à la partie suivante.

## Tests (2026-11-06, deux PC reliés par Tailscale, IP directe)
- Validé de bout en bout avec le pilote automatique : connexion, lobby, réglages, lancement, tutoriel puis
  9 manches côté hôte. Côté client : morts et réapparitions synchronisées, fins de manche synchronisées,
  mêmes niveaux, aucune exception. Écart de position client/hôte le plus souvent < 1 unité, ping 1 à 5 ms.
- Voie Steam : la création de salon fonctionne sur le PC principal, mais le compte Steam du second PC est
  refusé par Steam (création `AccessDenied`, entrée `NotAllowed`), ce qui laisse penser à un compte limité.
  Jonction Steam non validée à ce jour. Le mod affiche alors un message qui renvoie vers l'IP directe.
- Second essai Steam (v2.2.0) avec un autre compte sur le second PC (`compte B`, jeu emprunté via la famille
  Steam), en lançant le jeu par l'exe puis par Steam (`steam.exe -applaunch 965680 <arguments>`, qui transmet
  bien les arguments) : échec `k_EP2PSessionErrorNoRightsToApp` et `JoinLobby NotAllowed`. Le journal de Steam
  du second PC indique `AppID 965680 failed to update ownership ticket (Access Denied)` : Steam ne donne pas
  de licence active à ce compte. L'hôte n'a pas ce problème. Le mod affiche maintenant un message explicite
  dans ce cas (« ce compte n'a pas de licence active du jeu… passe par l'IP directe »).
- Tailscale fonctionne très bien pour l'IP directe : l'hôte donne son adresse 100.x.y.z.

- Rejoindre en cours de partie validé (v10) : le second PC s'est connecté pendant le match de l'hôte
  (l'hôte et 2 bots), a remplacé un bot à la fin de manche, puis a joué 3 manches synchronisées avec le tableau des
  scores de l'hôte. Écart des boomerangs < 0,25 unité, aucune exception.

- Clients passifs validés (v2.2.0) : 5 manches (tutoriel compris) jouées par le second PC. Les 38 morts vues
  par le client sont identiques à celles de l'hôte (ordre, cause, tueur). Le nombre de boomerangs affichés
  est toujours égal à celui de l'hôte, retard d'affichage de 90 à 150 ms, aucune exception.

## Hôte sans écran sous Wine (essai du 2026-11-06, serveur maison)
- Le jeu Windows tourne dans Docker (Debian bookworm, Wine 8, Xvfb) sur le serveur maison (Ryzen 5 3600) :
  `~/bfu-server` (Dockerfile, entrypoint.sh, copie du jeu et du mod dans `game/`), image `bfu-server:test`.
- **Il faut `-batchmode -nographics`.** Avec un rendu logiciel (wined3d + llvmpipe), le jeu reste bloqué sur
  le logo du studio, même sans mod. En mode sans affichage, BepInEx et le mod se chargent normalement mais
  écrivent dans `BepInEx/LogOutput.log` et pas dans le journal d'Unity.
- Wine : `WINEDLLOVERRIDES="winhttp=n,b;mscoree=;mshtml="` (charger BepInEx, ne pas installer Mono/Gecko),
  `docker run --init` et Xvfb lancé à la main (`xvfb-run` reste bloqué en PID 1). Le jeu démarre sans
  Steam (`SteamAPI_Init` échoue, sans conséquence en IP directe).
- Le mod limite l'hôte sans écran à 60 images/s. Consommation : environ 16 % d'un cœur au repos et 30 %
  en partie, 620 à 660 Mo de mémoire.
- Validé : client (le PC principal) connecté à <IP locale du serveur>:7778, 3 manches, 20 morts identiques des deux
  côtés, aucune erreur. Reste à faire : un vrai mode « hôte dédié » (pas de joueur local, menus pilotés par
  un client), un déploiement durable et la redirection du port de la box.
- Effet de bord utile pour tous (v2.2.1) : le mod interrogeait les gestionnaires du jeu à chaque image
  pendant le chargement (`Singleton<T>.Instance` lance une recherche et écrit une erreur tant qu'ils
  n'existent pas). D'où l'écran rouge de 1 à 3 minutes au démarrage, qui dure maintenant environ 3 s.

## Steam en mode Spacewar (v2.3.0, 2026-11-07)
- Option `[Steam] UseSpacewar` (activée par défaut) : le patcher BepInEx `BoomerangFuMultiplayer.Patcher`
  (dans `BepInEx\patchers\BoomerangFuMultiplayer\`) s'exécute avant le jeu, écrit `steam_appid.txt` = 480
  et force `SteamAppId`/`SteamGameId` = 480 dans l'environnement du processus. Steam voit alors le jeu comme
  Spacewar : salons, P2P relayé et invitations marchent pour tous les comptes, même un jeu emprunté que Steam
  refusait (`NoRightsToApp`). Désactiver l'option supprime le fichier au lancement suivant.
- Contreparties : Steam affiche « Spacewar », pas de temps de jeu ni de succès pour Boomerang Fu, DLC non
  détectés. « Rejoindre » depuis la liste d'amis ne marche que si le jeu est déjà lancé.
- Validé à deux PC (compte `compte A` hôte, compte `compte B` client, lancement normal depuis Steam) :
  manches complètes avec 2 bots en « impossible » (`-bfump-bot-difficulty impossible`), morts identiques.
- Corrigés au passage :
  - une invitation reçue au lancement du jeu ouvrait l'écran EN LIGNE avant l'accueil, et le client ne suivait
    plus les menus de l'hôte ;
  - une pile de menus sans écran d'accueil en bas (après `QuitMatch`) n'était plus conciliable ;
  - un joueur qui se reconnecte en plein match sur sa place, où son personnage existe encore, reprend ce
    personnage (liste des joueurs envoyée depuis le match lui-même) ;
  - un client resté sur l'écran de fin de manche quand l'hôte l'a quitté continue seul ;
  - fond gris derrière les scores chez le client : son enregistreur de replay filmait encore la transition,
    il est désormais mis en pause à la réception de la fin de manche ;
  - la navigation des menus restait coupée après l'ouverture de l'overlay Steam ;
  - barre rouge au milieu de l'écran EN LIGNE (fond du menu pause recopié), bandeau du HUD illisible sur fond
    clair (contour sombre) ;
  - un patch Harmony en échec n'emporte plus les suivants (`MatchSync`).
- Le plan « serveurs dédiés » (`docs/superpowers/plans/2026-11-06-serveurs-dedies.md`) est mis de côté : le mainteneur
  préfère Spacewar s'il suffit.

## Obstacles mobiles et effets (v2.4.0, protocole 6, 2026-11-07)
- **Obstacles mobiles** : les murs, sols et portes qui bougent (`MovingObstacle*`, `MovingFloor*`,
  `MovingDoor`, `RotatingObstacleController`) avancent dans leur `FixedUpdate` avec des durées et des sens
  tirés au hasard, et parfois `Time.time`. Ils se décalaient donc entre l'hôte et les clients en quelques
  secondes. L'hôte ajoute maintenant leur position et leur rotation à chaque instantané. La liste est celle
  du niveau courant (`level.GetComponentsInChildren`, comme `Level.GetAllSwitchesAndMovingObstacles`),
  triée par chemin dans la scène et limitée à 64. Les clients ne les simulent plus et les placent comme les
  joueurs. Si le nombre diffère (liste faite trop tôt), le client refait sa liste une fois par seconde.
- **Effets** (`FxMirror`, `Msg.Fx`) : le code que les clients ne jouent pas jouait aussi des sons, des
  particules, des secousses de caméra et des animations : ricochets, rattrapages, désarmements, coups
  parés, dashs. L'hôte les relève pendant ce code-là et les envoie sans garantie de livraison, au plus 150
  par seconde. Les effets que les clients rejouent déjà eux-mêmes (morts, power-ups, chutes,
  réapparitions) ne sont pas envoyés.
- Validé à deux PC en Spacewar, avec 2 bots en « impossible » sur 5 manches :
  - même nombre d'obstacles mobiles des deux côtés (16 au premier niveau) ;
  - les 36 morts vues par le client sont identiques à celles de l'hôte ;
  - effets reçus (environ 330 sons, 45 particules, 7 secousses) ;
  - aucune erreur.
- Une recherche sur toute la scène comptait 64 obstacles au premier niveau (d'autres niveaux encore chargés).

## Limites connues
- Le personnage d'un client répond avec le délai du réseau (pas de prédiction locale) : jouable en
  dessous d'environ 80 ms de ping, de moins en moins confortable au-delà.
- Certains effets purement visuels des power-ups (traînée de feu, boomerang explosif…) ne sont pas encore
  reproduits sur les boomerangs fantômes des clients ; leur effet en jeu, calculé par l'hôte, est correct.
- Sans bot à remplacer, un joueur qui arrive en cours de match attend la partie suivante (pas de joueur
  supplémentaire ajouté en plein match).
- Pendant la manche où il entre, un joueur arrivé en retard peut charger le niveau quelques secondes
  après les autres.
- Un seul joueur local par PC.

## Config — `BepInEx\config\com.bfump.multiplayer.cfg`
- `Network.LastIP` : dernière adresse IP utilisée · `Network.Port` : port UDP (7777)
- `Debug.DumpUi` : écrit la hiérarchie des menus clonés dans le log

## Arguments de test
Lancer l'exe directement avec un `steam_appid.txt` (965680) à côté, pour garder les arguments, et le
supprimer après les tests.
- `-bfump-host` (héberge en IP directe), `-bfump-host-steam` (héberge un salon Steam)
- `-bfump-join <ip[:port]>`, `+connect_lobby <id>` (rejoint un salon Steam ; c'est aussi ce qu'utilisent les invitations),
  `+bfump_join <SteamID64 de l'hôte>` (rejoint l'hôte Steam par son compte)
- `-bfump-lobby` : l'hôte ouvre le lobby dès qu'un joueur arrive
- `-bfump-bots N` : l'hôte ouvre le lobby tout seul et ajoute N bots (test d'arrivée en cours de partie)
- `-bfump-wait-players N` : avec le pilote automatique, l'hôte attend N joueurs humains avant de lancer
- `-bfump-bot-difficulty easy|medium|hard|impossible` (ou 0..3) : niveau des bots de l'hôte
- `-bfump-add-joiners` : un joueur qui arrive en cours de match est ajouté même s'il n'y a pas de bot à remplacer
- `-bfump-autoplay` : pilote automatique (rejoindre, prêt, lancer, jouer au hasard), active les logs `[Diag]`
- `-bfump-diag` : logs `[Diag]` seuls (états, menus, morts, écarts de position)
- `-logFile <chemin>` (option Unity) : un log Unity par instance

L'ancien prototype (overlay IMGUI) est conservé dans `legacy/` (non compilé).
