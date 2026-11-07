# Serveurs dédiés et liste des serveurs — conception

Date : 2026-11-06. Projet : mod BepInEx « Boomerang Fu Online » (v2.2.1, protocole 5).
Source : `C:\Program Files (x86)\Steam\steamapps\common\Boomerang Fu\ModSource\BoomerangFuMultiplayer\` (pas de git).
Doc existante : `SETUP.md` (architecture du mod, essai de l'hôte sous Wine).

## 1. Intention

Aujourd'hui, pour jouer en ligne, un joueur héberge la partie sur son PC. Ses amis doivent ensuite le rejoindre
par Steam (bloqué pour les comptes qui empruntent le jeu) ou par une adresse IP (UPnP ou port à ouvrir, adresse à
copier). Depuis la v2.2, l'hôte décide de tout : il joue sans délai, alors que les autres subissent le ping plus
environ 0,1 s.

Le mainteneur veut que n'importe quel joueur qui a le mod puisse :

1. **voir la liste des serveurs** dans les menus du jeu ;
2. **en créer un**, ce qui lui donne les **droits d'hôte** de ce serveur ;
3. **attendre les autres** dans un salon et **les exclure** s'il le souhaite ;
4. **ne pas pouvoir lancer tant qu'il est seul** ;
5. **lancer**, ce qui fait passer tout le monde sur l'**écran de choix des persos du jeu**. La suite (réglages,
   partie) se déroule comme aujourd'hui.

Les parties tournent sur le **serveur maison** du mainteneur (choix validé), pas sur le PC des joueurs. Tout le monde a
ainsi le même délai, il n'y a rien à configurer chez les joueurs, et la partie survit au départ de son créateur.

**Exigence du mainteneur : utiliser les menus du jeu.** Les nouveaux écrans reprennent le cadre, les boutons et la
police des menus du jeu, exactement comme l'écran EN LIGNE actuel (classe `UI/OnlineMenu.cs` : cadre cloné du menu
pause, boutons clonés du bouton « Jouer » du menu principal). Pas d'IMGUI, pas d'interface web.

## 2. Parcours du joueur (maquettes validées par le mainteneur)

1. **SERVEURS** (nouvel écran, ouvert par un nouveau bouton SERVEURS en tête de l'écran EN LIGNE) :
   - titre « SERVEURS », ligne d'info « N SERVEURS EN LIGNE / CHOISIS-EN UN, OU CRÉE LE TIEN » ;
   - un bouton par serveur : « PARTIE DE <pseudo du créateur> · 2/4 ». Un serveur complet est grisé et ne se
     rejoint pas ; un serveur en partie affiche « EN PARTIE » ;
   - puis CRÉER UN SERVEUR, ACTUALISER, RETOUR.
2. **SALON** (nouvel écran, même cadre), vu par l'hôte :
   - « PARTIE DE <pseudo> — TU ES L'HÔTE », « EN ATTENTE DE JOUEURS · n/4 » ;
   - la liste des joueurs : l'hôte en ligne de texte, chaque invité sous forme de bouton « EXCLURE <pseudo> » ;
   - LANCER (grisé avec « IL FAUT AU MOINS 2 JOUEURS » tant qu'il est seul), QUITTER LE SALON.
3. **SALON vu par un invité** : même écran, sans EXCLURE ni LANCER, avec « EN ATTENTE DE L'HÔTE… » et QUITTER LE
   SALON.
4. **Après LANCER** : tout le monde passe sur l'écran de choix des persos du jeu (menu `Lobby`), tel quel. L'hôte y
   a les droits qu'a l'hôte d'aujourd'hui (continuer vers les réglages, ajouter ou retirer des bots, lancer le
   match). Le match se déroule ensuite comme en v2.2 (clients passifs).
5. **Fin de match** : l'hôte choisit entre revanche et retour aux persos, comme aujourd'hui. Un « retour au menu
   principal » choisi par l'hôte ramène tout le monde au SALON au lieu de fermer le serveur.

Le badge du HUD (`UI/OnlineHud.cs`) affiche « EN LIGNE n/4 · HÔTE » chez l'hôte du serveur, « JOUEUR n » chez les
autres.

## 3. Objectifs, non-objectifs, anti-objectifs

**Objectifs**
- Liste des serveurs et création en 2 clics depuis le jeu, sans IP ni port à saisir.
- Jusqu'à 4 joueurs par serveur, tous distants (aucun joueur sur la machine serveur).
- Droits d'hôte : exclure, lancer (à partir de 2 joueurs), piloter les menus du jeu (persos, réglages, match).
- Les droits passent au joueur suivant quand l'hôte part (ordre d'arrivée).
- Une partie s'éteint d'elle-même quand elle est vide ; au plus **3 parties en même temps**.

**Non-objectifs (pour cette version)**
- Héberger sur son propre PC via la liste (l'écran EN LIGNE garde ses options actuelles, inchangées).
- Prédiction locale côté client, mot de passe de serveur, chat, classement.
- Faire passer la liste par VPS : tout est sur le serveur maison.

**Anti-objectifs (échec même si « ça marche »)**
- Une interface qui ne ressemble pas aux menus du jeu.
- Saturer la mémoire du serveur maison (déjà sous pression : voir § 5) ou gêner ses autres services.
- Un serveur fantôme : listé mais injoignable, ou qui tourne vide indéfiniment.
- Exposer autre chose que l'API HTTPS de la liste et les ports UDP des parties.

## 4. Architecture décidée

### 4.1 Composants

1. **Service « serveurs »** (validé par le mainteneur : sur le serveur maison, pas sur VPS) : un seul conteneur Docker déployé par Dokploy sur le serveur maison
   (`https://dokploy.example.org`). Il contient :
   - le programme d'orchestration, qui expose une petite API HTTP (liste, création) et lance ou arrête les parties ;
   - Wine (Debian bookworm, Wine 8), avec la recette de l'essai (`~/bfu-server/Dockerfile` et `entrypoint.sh` sur
     le serveur maison) ;
   - les parties : jusqu'à 3 processus du jeu (`Boomerang Fu.exe` sous Wine, sans écran), lancés et surveillés par
     l'orchestrateur **dans ce même conteneur**. Pas d'accès au socket Docker.
2. **Mod, côté instance** : nouveau « mode dédié » (`-bfump-dedicated`) qui héberge en IP directe sans joueur
   local, avec un hôte délégué (le joueur qui a les droits).
3. **Mod, côté joueur** : écrans SERVEURS et SALON, client de l'API.

### 4.2 Réseau
- API : `https://bfu.example.org` (wildcard `*.example.org` → serveur maison, Traefik de Dokploy, TLS
  Let's Encrypt). Seule l'API passe par Traefik.
- Parties : UDP **7780, 7781, 7782**, une par partie, publiés par le conteneur sur l'hôte. **Le mainteneur ouvre une fois
  ces ports sur la box** (redirection UDP 7780-7782 → <IP locale du serveur>). Le port 7777 reste celui des PC des
  joueurs qui hébergent eux-mêmes (UPnP).
- Adresse annoncée dans la liste : l'IP publique de la box. Si la requête vient de cette même IP publique ou d'une
  adresse privée (un joueur chez le mainteneur), l'API renvoie l'adresse locale <IP locale du serveur>, pour ne pas dépendre du
  « NAT loopback » de la box.

### 4.3 Cycle de vie d'une partie
1. CRÉER UN SERVEUR : le jeu du joueur envoie `POST /servers` avec son pseudo et la version du protocole.
2. L'orchestrateur refuse si 3 parties tournent déjà (message « Tous les serveurs sont occupés »), si la mémoire
   disponible de la machine (`MemAvailable` de `/proc/meminfo`) est sous 1,5 Go (même message), ou si la version
   ne correspond pas. Sinon il choisit un port libre et génère un **jeton d'hôte**. Il lance l'instance avec
   `-batchmode -nographics -bfump-dedicated -bfump-port <port> -bfump-owner <jeton> -bfump-name "<pseudo>"`, puis
   répond `{id, address, port, token}`.
3. Le jeu du créateur se connecte en IP directe à `address:port` et présente le jeton dans son `Hello`. L'instance
   lui donne les droits d'hôte. Le serveur n'apparaît dans la liste qu'une fois son créateur connecté.
4. L'instance écrit son état toutes les 2 s (fichier JSON dans un dossier partagé avec l'orchestrateur) :
   phase (`salon`, `persos`, `partie`), joueurs (pseudos, ordre), pseudo de l'hôte actuel, n/4, version.
5. L'instance signale qu'elle est prête (menus chargés, hébergement ouvert) dans son fichier d'état ; le jeu du
   créateur réessaie de se connecter pendant ce démarrage. L'orchestrateur arrête l'instance si elle n'est pas prête
   après 180 s, si son fichier d'état n'est plus mis à jour depuis 10 s (instance figée), si le créateur ne s'est
   pas connecté 60 s après qu'elle est prête, si elle est vide depuis 60 s,
   si un joueur seul y attend en SALON depuis 10 minutes (message « Serveur fermé pour inactivité »), ou si son
   processus s'arrête (et il libère le port). Il redémarre proprement en cas de plantage, sans relance
   automatique d'une partie morte : elle disparaît de la liste.

### 4.4 Droits d'hôte (dans l'instance)
- L'hôte est un client : le porteur du jeton, puis, s'il part, le joueur connecté depuis le plus longtemps. Toast :
  « <pseudo> est maintenant l'hôte ».
- **Ses entrées de menu pilotent les menus de l'instance**, comme les entrées du joueur local pilotent les menus de
  l'hôte aujourd'hui. Les entrées de menu des autres joueurs sont ignorées, sauf là où le jeu lit chaque joueur
  séparément (choix des persos). Sur les PC clients, l'affichage continue de suivre les menus de l'instance
  (`UiMirror`).
- Les actions réservées aujourd'hui à l'hôte (`LobbySync.HostOnlyPrefix` : continuer, fermer, ajouter ou retirer un
  bot ; lancement du match ; pause) sont réservées à l'hôte délégué.
- **Exclure** : l'instance déconnecte le joueur avec le message « Tu as été exclu de la partie » et refuse son
  retour (même pseudo ou même adresse) jusqu'à l'arrêt de l'instance.
- **Lancer** n'est possible qu'à partir de 2 joueurs connectés ; l'instance le vérifie (pas seulement l'écran).
- **Déblocages** : l'instance n'a ni Steam ni DLC. Sur le serveur, tout ce qui se gagne en jouant (persos et
  chapeaux) est disponible pour tous. Les persos des DLC payants restent réservés aux joueurs qui possèdent le pack
  (chaque joueur l'indique à la connexion). Le serveur ne gagne aucune progression.
- **Messages aux joueurs** : quand l'orchestrateur ferme une partie, il demande d'abord à l'instance de prévenir les
  joueurs avec la raison (« Serveur fermé pour inactivité »). Le changement d'hôte s'affiche chez les joueurs.
- **S'il ne reste qu'un joueur** : pendant le choix des persos ou les réglages, retour automatique au SALON ; pendant
  un match, le match continue (les bots éventuels restent) jusqu'à sa fin, puis retour au SALON. « Tout le monde
  est parti » veut dire : plus aucun joueur connecté, hôte compris (l'instance s'arrête alors après 60 s).

### 4.5 Mode dédié du mod (instance)
- Aucun joueur local : les 4 places en ligne sont distantes. Aujourd'hui, l'hôte occupe la place 0
  (`Session.LocalSlot = 0`, `Session.Active` exige `LocalSlot >= 0`). Le mode dédié doit permettre une session
  hôte sans place locale, avec la place 0 attribuable à un client.
- Pas de pilote automatique, pas d'UPnP, pas de Steam.
- Démarre directement en phase SALON après le chargement des menus. Retour en SALON en fin de partie si l'hôte
  choisit le menu principal, ou si tout le monde est parti.
- Limite à 60 images/s déjà en place en mode sans écran (`Plugin.Awake`, `Application.isBatchMode`).

### 4.6 API HTTP (forme indicative)
- `GET /servers?protocol=<n>` → `[{id, name, players, max, phase, address, port, protocol}]`.
- `POST /servers` `{name, protocol}` → `{id, address, port, token}` ou une erreur lisible en français.
- Limite : une création par adresse IP par minute, pseudo tronqué à 24 caractères et nettoyé. API publique, sans
  compte (le mod n'est distribué qu'aux amis du mainteneur). Un mot de passe intégré au mod ne protégerait rien, puisqu'il
  se lirait dans la DLL : la protection repose sur le plafond de parties, la garde mémoire et la limite par IP.

## 5. Contraintes connues (essai du 2026-11-06)

- Serveur maison : x86_64, Ryzen 5 3600 (12 threads), 15 Go de mémoire, Docker 29, Dokploy. Pendant l'essai :
  environ 370 Mo libres et 4,2 Go de swap utilisés sur 8 Go. Une instance consomme de 620 à 660 Mo et de 16 à
  30 % d'un cœur. Limiter le conteneur (par exemple 3 Go de mémoire, 2 cœurs) et vérifier la marge avant de
  passer à 3 parties.
- Le jeu **doit** tourner en `-batchmode -nographics` : avec un rendu logiciel (wined3d + llvmpipe), il reste figé
  sur le logo du studio, même sans mod.
- Sous Wine : `WINEDLLOVERRIDES="winhttp=n,b;mscoree=;mshtml="`, `docker run --init`. Xvfb était lancé à la main
  (`xvfb-run` reste bloqué en PID 1). Il n'est peut-être pas nécessaire en mode sans écran.
- En mode sans écran, le journal du mod est `BepInEx/LogOutput.log` et non celui d'Unity.
- Le jeu démarre sans Steam (`SteamAPI_Init` échoue, sans conséquence en IP directe).
- `boot.config` contient `single-instance=` : le jeu refuse une deuxième instance sur la même machine. Hypothèse à
  vérifier en premier : un préfixe Wine par instance (donc un `wineserver` par instance) suffit à les isoler.
- Plusieurs instances ne doivent pas écrire dans les mêmes fichiers (`BepInEx/LogOutput.log`, `BepInEx/cache`,
  journal Unity, sauvegarde dans le préfixe). Prévoir un dossier par instance qui pointe vers les fichiers lourds
  du jeu, partagés en lecture seule.
- Les fichiers du jeu viennent de l'installation Steam du mainteneur. Ils restent sur le serveur maison
  (`/home/<utilisateur>/bfu-server/game`, monté en volume) et ne vont ni dans une image publiée ni dans un registre.
- Protocole réseau : passer à la version 6. L'API et la liste filtrent par version.

## 6. Ce qui est laissé au constructeur

- Langage et forme de l'orchestrateur (petit service .NET, Python ou Node dans l'image Wine), tant qu'il reste
  léger et sans accès au socket Docker.
- Format exact du fichier d'état, de l'API et des messages réseau (nouveaux types `Msg` pour exclure, transférer
  l'hôte, état du salon).
- Façon de router les entrées de menu de l'hôte délégué dans `NetInput` et `UiMirror`.
- Détails visuels des écrans, dans le style imposé (cadre et boutons clonés du jeu).
- Journaux, rotation, surveillance (Uptime Kuma existe sur VPS si besoin).

## 7. Validation attendue

- Deux PC (le PC principal et PC2, voir la mémoire `two-pc-test-setup`) : le premier crée un serveur
  depuis la liste, le second le voit et le rejoint. LANCER est grisé à 1 joueur et actif à 2. Exclusion vérifiée
  (pas de retour possible). Choix des persos, réglages, 3 manches avec morts identiques, retour au SALON.
- Départ de l'hôte en salon et en partie : l'autre joueur récupère les droits.
- Serveur vide : disparu de la liste et processus arrêté en moins de 2 minutes.
- Quatrième création refusée proprement quand 3 parties tournent. Mémoire du serveur maison relevée pendant ce
  test.
- Joueur chez le mainteneur et joueur à l'extérieur (Internet) sur le même serveur.

## 8. Choix faits par Claude (à relire par le mainteneur)

- Adresse de l'API : `bfu.example.org` (le service sur le serveur maison est validé par le mainteneur).
- Au plus 3 parties en même temps, et aucune nouvelle partie si la machine a moins de 1,5 Go de mémoire
  disponible ; arrêt après 60 s à vide, ou après 10 minutes avec un seul joueur en SALON.
- Un joueur resté seul repasse au SALON (sauf en plein match, qui se termine d'abord).
- Ports UDP 7780 à 7782, ouverts à la main sur la box.
- Droits transmis au joueur connecté depuis le plus longtemps quand l'hôte part.
- Un joueur exclu ne peut pas revenir tant que la partie tourne.
- Un serveur en partie reste joignable (arrivée en cours de match comme aujourd'hui) ; un serveur complet est grisé.
- API publique sans mot de passe.
- Déblocages : tout ce qui se gagne en jouant est disponible pour tous sur le serveur ; les persos des DLC payants
  restent réservés à leurs propriétaires (décision prise après les revues du plan).
- L'écran EN LIGNE garde ses options actuelles ; SERVEURS s'ajoute en tête.
