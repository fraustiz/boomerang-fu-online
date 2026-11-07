# Serveurs dédiés et liste des serveurs — plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Depuis les menus du jeu, lister les serveurs, en créer un sur le serveur maison avec les droits d'hôte, attendre et exclure des joueurs dans un salon, puis LANCER vers l'écran de choix des persos.

**Architecture:** Un conteneur unique sur le serveur maison fait tourner un orchestrateur Python (API HTTPS derrière Traefik) qui lance jusqu'à 3 instances du jeu sous Wine, sans écran. Le mod gagne un mode « hôte dédié » sans joueur local, où un client (l'hôte délégué) pilote les menus. Côté joueur, l'écran EN LIGNE gagne deux pages : SERVEURS et SALON.

**Tech Stack:** C# net48 / BepInEx 5 / HarmonyX / LiteNetLib 1.1 / Newtonsoft.Json (mod) ; xunit sur net48 (tests du mod) ; Python 3.11, bibliothèque standard seule, unittest (orchestrateur) ; Docker, Wine 8 (Debian bookworm), Dokploy et Traefik du serveur maison.

**Spec:** `docs/superpowers/specs/2026-11-06-serveurs-dedies-design.md` (à lire avant toute tâche). Contexte du mod : `SETUP.md`. Code décompilé du jeu, pour vérifier un comportement : `<dossier de travail>\dec\acs\`.

Racine de tous les chemins : `C:\Program Files (x86)\Steam\steamapps\common\Boomerang Fu\ModSource\BoomerangFuMultiplayer\` (abrégée `MOD\`). Build du mod : `dotnet build -c Release` dans `MOD\` (la DLL sort dans `BepInEx\plugins\BoomerangFuMultiplayer\` du jeu). Serveur maison : `ssh <utilisateur>@<serveur>` depuis le PC principal. **Pas de git dans ce dossier, donc pas d'étape de commit** (règle : ne committer que sur demande du mainteneur).

## Global Constraints

- **Public : des amis du mainteneur.** Les protections contre les abus (limite par IP, nettoyage du pseudo, bannissement) restent simples ; ne pas en ajouter d'autres. Le plan a été relu six fois : les points restants se règlent pendant l'implémentation.

- Protocole réseau **6** (`Protocol.Version`), version du mod **2.3.0** (`PluginInfo.Version`).
- API : `https://bfu.example.org` ; `GET /servers?protocol=<n>`, `POST /servers` `{name, protocol}`, `GET /health`.
- Parties : UDP **7780, 7781, 7782**, une par partie ; au plus **3** parties ; refus si `MemAvailable` de la machine < **1,5 Go** (mesuré pendant l'essai : environ 6,2 Go disponibles) **ou** si la marge du conteneur (`/sys/fs/cgroup/memory.max` − (`memory.current` − `inactive_file` de `memory.stat`), cgroup v2 ; le cache des fichiers du jeu ne compte pas) < **800 Mo**.
- Arrêt d'une instance : créateur absent **60 s** après que l'instance est prête, vide depuis **60 s**, un seul joueur en SALON depuis **10 min**, statut non mis à jour depuis **30 s** une fois l'instance prête (instance figée), ou processus terminé.
- Une création par adresse IP par **minute** ; pseudo nettoyé et tronqué à **24** caractères.
- Instance : `-batchmode -nographics`, `WINEDLLOVERRIDES="winhttp=n,b;mscoree=;mshtml="`, `LANG=C.UTF-8`, répertoire de travail = dossier de l'instance, conteneur avec `init: true`, pas d'accès au socket Docker, fichiers du jeu montés depuis `/home/<utilisateur>/bfu-server/game` (jamais dans une image publiée).
- **Utiliser les menus du jeu** : nouvelles pages dans `UI/OnlineMenu.cs` (même cadre, mêmes boutons clonés, même police). Pas d'IMGUI ni de web.
- **Déblocages sur le serveur** : tout ce qui se gagne en jouant (persos et chapeaux) est disponible pour tous ; les persos des **DLC payants** (`GameManager.dlcOneCharacters`, `dlcTwoCharacters`, `dlcThreeCharacters` ; packs `DownloadableContentID.FreshFlavorsPack`, `JustDessertsPack`, `StreetFoodPack`) restent réservés aux joueurs qui possèdent le pack. Le serveur ne gagne aucune progression (pas d'écran de déblocage en fin de match).
- Délais et limite par IP réglables pour les tests par variables d'environnement de l'orchestrateur, en secondes : `BFU_OWNER_TIMEOUT` (60), `BFU_EMPTY_TIMEOUT` (60), `BFU_ALONE_TIMEOUT` (600), `BFU_STALE_AFTER` (30), `BFU_RATE_LIMIT` (60, 0 = désactivée).
- Textes exacts (majuscules comme ici) : `SERVEURS` · `{n} SERVEURS EN LIGNE` · `CHOISIS-EN UN, OU CRÉE LE TIEN` · `PARTIE DE {pseudo} · {n}/4` (suffixes ` · COMPLET`, ` · EN PARTIE`) · `CRÉER UN SERVEUR` · `ACTUALISER` · `RETOUR` · `SALON` · `PARTIE DE {pseudo} — TU ES L'HÔTE` · `EN ATTENTE DE JOUEURS · {n}/4` · `EXCLURE {pseudo}` · `LANCER` · `IL FAUT AU MOINS 2 JOUEURS` · `QUITTER LE SALON` · `EN ATTENTE DE L'HÔTE…` · `LISTE INDISPONIBLE` · `DÉMARRAGE DU SERVEUR…`.
- Messages exacts : « Tu as été exclu de la partie » · « Tous les serveurs sont occupés » · « Serveur fermé pour inactivité » · « {pseudo} est maintenant l'hôte » · « Version du mod différente (serveur v6) » · « Attends une minute avant de créer un autre serveur » · « Le serveur démarre, réessaie dans une minute » (IP publique pas encore connue).

## Review Focus

- API injoignable ou lente (serveur maison éteint, DNS) : la page SERVEURS affiche `LISTE INDISPONIBLE` en moins de 5 s, ACTUALISER réessaie ; jamais de blocage → tests en Tâche 6.
- L'hôte délégué quitte (en SALON, au choix des persos, en match) : les droits passent au suivant ; s'il ne reste qu'un joueur hors match, retour au SALON → tests en Tâche 4, vérification en Tâche 7.
- Le créateur ne se connecte jamais (jeu fermé juste après CRÉER) : l'instance s'arrête 60 s après être prête et n'apparaît jamais dans la liste → test en Tâche 1.
- Joueur chez le mainteneur (adresse privée ou même IP publique que le serveur) : la liste et la création lui donnent <IP locale du serveur> → test en Tâche 1, IP réellement vue par l'API relevée en Tâche 3.
- Joueur exclu qui revient par la liste : refusé avec « Tu as été exclu de la partie » → test en Tâche 4, vérification en Tâche 7.

---

### Task 0: Vérifier les hypothèses risquées (essai jetable)

**Files:** aucun dans `MOD\`. Sur le serveur maison : image d'essai `bfu-server:test` et dossier `~/bfu-server` existants.

- [ ] **Step 1: Run two instances** (la DLL actuelle lit son port dans `BepInEx\config\com.bfump.multiplayer.cfg`, clé `Port` de la section `[Network]` : mettre 7780 dans la première copie de `BepInEx` et 7781 dans la seconde ; arguments `-batchmode -nographics -bfump-host`) dans deux conteneurs `bfu-server:test` avec deux volumes de préfixe distincts, puis dans **un seul** conteneur avec deux `WINEPREFIX` différents (deux processus lancés à la main), ports 7780 et 7781. Expected : les deux `BepInEx/LogOutput.log` (dossiers BepInEx séparés : copier `game/BepInEx` dans deux dossiers et lancer depuis deux dossiers d'instance faits de liens symboliques) contiennent `menus ready` puis `[Direct] hosting on UDP 7780` pour l'une et `7781` pour l'autre, et les deux processus tournent encore 60 s plus tard. Si le second refuse de démarrer (`single-instance`), le noter et prévoir dans la Tâche 2 un conteneur par partie lancé autrement (dans ce cas, s'arrêter et revoir le plan avec le mainteneur).
- [ ] **Step 2: Measure** le temps entre le lancement et `menus ready`, avec un préfixe neuf puis avec un préfixe déjà créé. Expected : deux durées notées dans `SETUP.md` (elles fixent les délais du client à la Tâche 6).
- [ ] **Step 3: Check the real conditions** : relancer une instance **sans Xvfb** (`DISPLAY` vide, `wine` lancé directement) et avec le jeu monté en lecture seule (`-v ~/bfu-server/game:/game:ro`, BepInEx copié ailleurs). Expected : `menus ready` dans les deux cas ; sinon noter ce qui manque (Xvfb, droits d'écriture) pour la Tâche 3.
- [ ] **Step 4: Clean up** : arrêter et supprimer les conteneurs d'essai.

### Task 1: Registre de l'orchestrateur (logique pure, Python)

**Files:**
- Create: `MOD\server\bfu_servers\__init__.py`, `MOD\server\bfu_servers\registry.py`
- Test: `MOD\server\tests\test_registry.py`

**Interfaces:**
- Produces (`registry.py`) :
  - `class CreateError(Exception)` : `.message: str` (texte français affiché tel quel par le jeu).
  - `class Instance` (protocole fourni par la Tâche 2) : `alive() -> bool`, `status() -> tuple[dict | None, float]` (statut et âge du fichier en secondes), `stop(reason: str) -> None`.
  - `class Registry(ports=(7780, 7781, 7782), protocol=6, clock=time.monotonic, mem_available=<callable -> int octets>, container_headroom=<callable -> int octets>, launch=<callable(id, port, token, name) -> Instance>, public_ip=<callable -> str | None>, lan_ip="<IP locale du serveur>")`. Toutes les méthodes publiques sont protégées par un même `threading.Lock`.
  - `public_ip` est une fonction **qui ne bloque jamais** : elle renvoie une valeur en cache. `__main__` la rafraîchit dans un thread à part (toutes les 10 min, délai réseau de 5 s), hors du verrou du registre.
  - `Registry.create(name: str, protocol: int, client_ip: str) -> dict` → `{"id", "address", "port", "token"}`, rendu **immédiatement** (l'instance démarre en arrière-plan) ; lève `CreateError`.
  - `Registry.list(protocol: int, client_ip: str) -> list[dict]`, chaque entrée `{"id", "name", "players", "max", "phase", "address", "port", "protocol"}`.
  - `Registry.tick() -> list[str]` (ids arrêtés), appelée toutes les secondes. Elle décide sous le verrou, retire les instances de la table, puis appelle leur `stop(reason)` **hors du verrou** (dans un thread), pour ne jamais bloquer `list()` ni `create()`.
  - Délais lus dans le constructeur (`owner_timeout=60, empty_timeout=60, alone_timeout=600, rate_limit=60, boot_timeout=180, stale_after=30`) ; `__main__` les prend dans les variables `BFU_*` des Global Constraints.
  - Format du statut (écrit par le mod, Tâche 5) : `{"protocol": 6, "ready": bool, "phase": "salon"|"persos"|"partie", "players": [str], "leader": str|null, "max": 4}`.
  - Adresse « maison » : `client_ip` privée (RFC 1918 : 10/8, 172.16/12, 192.168/16) ou égale à `public_ip()` → `lan_ip`.

- [ ] **Step 1: Write the failing tests** (`unittest`, horloge et lanceur factices) :
  - `test_create_returns_port_and_token` : premier `create("Joueur1", 6, "1.2.3.4")` → `port == 7780`, `len(token) >= 16`, `address == "<IP publique>"` (`public_ip` factice).
  - `test_concurrent_creates_get_distinct_ports` : 3 threads appellent `create` en même temps (3 IP) → ports `{7780, 7781, 7782}`.
  - `test_fourth_create_refused` : 3 créations puis une 4e → `CreateError("Tous les serveurs sont occupés")`.
  - `test_low_memory_refused` : `mem_available` → `1_400_000_000` → même message ; `container_headroom` → `700_000_000` → même message.
  - `test_public_ip_unknown_refused` : `public_ip()` renvoie `None` → `"Le serveur démarre, réessaie dans une minute"`.
  - `test_wrong_protocol_refused` : `create(..., 5, ...)` → `"Version du mod différente (serveur v6)"`.
  - `test_rate_limit_per_ip` : même IP à 30 s d'écart → `"Attends une minute avant de créer un autre serveur"` ; à 61 s, acceptée. Avec `rate_limit=0`, deux créations immédiates acceptées.
  - `test_stop_runs_outside_lock` : un `stop()` factice qui dort 3 s ; pendant ce temps, `list()` répond en moins de 0,1 s.
  - `test_port_reserved_while_stopping` : 3 parties ouvertes, l'une s'arrête avec un `stop()` lent (3 s) ; une création pendant ces 3 s → `"Tous les serveurs sont occupés"` ; après le retour de `stop()`, la création obtient le port libéré. Un port reste réservé (état « en arrêt ») jusqu'au retour de `stop()`.
  - `test_name_sanitized` : `"<b>Jean\n" + "x"*40` → nom sans `<`, `>`, saut de ligne, longueur ≤ 24.
  - `test_hidden_until_owner_joined` : statut `None`, `ready == False` ou `players == []` → `list()` vide ; `ready` et 1 joueur → 1 entrée `players == 1, max == 4, phase == "salon"`.
  - `test_owner_timer_starts_when_ready` : instance pas prête pendant 80 s → pas d'arrêt ; prête sans joueur pendant 61 s → arrêt avec la raison `"Serveur fermé pour inactivité"`, port de nouveau libre une fois `stop()` revenu. Instance jamais prête après 180 s → arrêt.
  - `test_empty_for_60s_stops` : 2 joueurs puis 0 → arrêt à 60 s, pas à 59 s.
  - `test_alone_in_salon_10_min_stops` : 1 joueur en `salon` pendant 600 s → arrêt avec `"Serveur fermé pour inactivité"` ; 1 joueur en `partie` pendant 700 s → pas d'arrêt.
  - `test_stale_status_is_dead` : instance déjà vue `ready` dont le statut a 31 s (et pas à 29 s) → retirée de `list()` et arrêtée. Une instance qui n'a encore jamais écrit de statut n'est pas concernée par cette règle (seul le délai de démarrage de 180 s s'applique).
  - `test_dead_process_removed` : `alive() == False` → retiré au `tick()`.
  - `test_home_player_gets_lan_address` : `client_ip` = `public_ip()`, `"<IP locale du PC>"` ou `"10.0.0.5"` → `address == "<IP locale du serveur>"` dans `create` et `list`.
  - `test_list_filters_protocol` : `list(5, ip)` → vide.
- [ ] **Step 2: Run** `python -m unittest discover -s tests -v` (dans `MOD\server\`, pour que `bfu_servers` soit importable). Expected : FAIL (module absent).
- [ ] **Step 3: Implement `Registry`**. Jeton `secrets.token_hex(16)` (jamais de `-` en tête : Unity lit les options n'importe où sur la ligne de commande), id `secrets.token_hex(4)`. Pseudo : garder lettres, chiffres, espace et `-_.'`, retirer les `-` en tête, réduire les espaces, tronquer à 24, `"JOUEUR"` si vide. Délai maximal de démarrage : 180 s.
- [ ] **Step 4: Run** la même commande. Expected : tous les tests PASS.

### Task 2: API HTTP et lanceur d'instances (Python)

**Files:**
- Create: `MOD\server\bfu_servers\api.py`, `MOD\server\bfu_servers\launcher.py`, `MOD\server\bfu_servers\__main__.py`
- Test: `MOD\server\tests\test_api.py`, `MOD\server\tests\test_launcher.py`

**Interfaces:**
- Consumes : `Registry`, `CreateError` (Tâche 1).
- Produces :
  - `api.make_server(registry, host="0.0.0.0", port=8080) -> ThreadingHTTPServer`. Routes : `GET /health` → 200 `{"ok": true, "running": n}` ; `GET /servers?protocol=n` → 200 liste ; `POST /servers` (JSON `{name, protocol}`, ≤ 2 Ko) → 200 `{id,address,port,token}` ou 409 `{"error": message}` ; 400 si JSON invalide. IP du client, dans cet ordre : `CF-Connecting-IP` **seulement si** la variable `BFU_TRUST_CLOUDFLARE=1` (à poser si la Tâche 3 montre que le domaine passe par Cloudflare), puis `X-Real-Ip` (posé par Traefik), **dernier** élément de `X-Forwarded-For`, adresse de la socket. Chaque requête journalise l'IP retenue et les en-têtes d'origine.
  - `launcher.prepare_prefixes(ports, root="/srv/bfu")` : crée `root/prefix/<port>` (`$BFU_WINE wineboot -i`) s'il manque. Appelée **au démarrage du conteneur**, jamais pendant une requête.
  - `launcher.WineInstance(id, port, token, name, game_dir="/game", root="/srv/bfu")` implémente `Instance` : dossier `root/instances/<id>/` avec liens symboliques vers `Boomerang Fu.exe`, `UnityPlayer.dll`, `UnityCrashHandler64.exe`, `Boomerang Fu_Data`, `MonoBleedingEdge`, `winhttp.dll`, `doorstop_config.ini` et `steam_appid.txt` (lier seulement ceux qui existent dans `game_dir`), et une **copie** de `BepInEx/{core,config,plugins,patchers}`. À l'arrêt, `unity.log` (s'il existe : en mode sans écran il peut manquer) et `BepInEx/LogOutput.log` sont déplacés dans `root/logs/<id>-unity.log` et `root/logs/<id>-bepinex.log` (seuls les 20 plus récents sont gardés) avant la suppression du dossier. `prepare_prefixes` supprime aussi les dossiers `root/instances/*` restés d'un démarrage précédent. Lancement avec `cwd` = ce dossier, `WINEPREFIX=root/prefix/<port>`, `LANG=C.UTF-8`, nouveau groupe de processus : `$BFU_WINE "Boomerang Fu.exe" -batchmode -nographics -logFile <Z:\…\unity.log> -bfump-dedicated -bfump-port <port> -bfump-owner <token> -bfump-name <name> -bfump-status <Z:\…\status.json> -bfump-stopfile <Z:\…\stop.request>`. `stop(reason)` écrit `reason` dans `stop.request`, attend jusqu'à **6 s** la fin du processus, puis tue le groupe et lance `$BFU_WINESERVER -k` sur ce préfixe, et supprime le dossier.
  - `python -m bfu_servers` : `prepare_prefixes`, puis `Registry` (`mem_available` lit `MemAvailable` dans `/proc/meminfo` ; `container_headroom` lit `/sys/fs/cgroup/memory.max`, `memory.current` et la ligne `inactive_file` de `memory.stat` (fonction pure `headroom(max_text, current_text, stat_text) -> int`, testée : `max` 3 Gio, `current` 2,6 Gio dont `inactive_file` 0,9 Gio → 1,3 Gio de marge), ou renvoie une valeur très grande si `memory.max` vaut `max` ou n'existe pas ; `public_ip` : variable `BFU_PUBLIC_IP`, sinon `https://api.ipify.org` mis en cache 10 min), API et `tick()` toutes les secondes.
  - Variables : `BFU_WINE` (défaut `wine`) et `BFU_WINESERVER` (défaut `wineserver`).

- [ ] **Step 1: Write the failing tests** :
  - `test_api.py` (serveur sur port éphémère + `urllib`, registre factice) : `GET /health` → 200 ; `POST` refusé → 409 et `{"error": "Tous les serveurs sont occupés"}` ; `X-Real-Ip: <IP publique>` → le registre reçoit `"<IP publique>"` ; `X-Forwarded-For: 6.6.6.6, <IP publique>` sans `X-Real-Ip` → `"<IP publique>"` ; `CF-Connecting-IP: 5.5.5.5` est ignoré par défaut et prime sur les deux avec `BFU_TRUST_CLOUDFLARE=1` ; corps invalide → 400.
  - `test_launcher.py` (`game_dir` et `root` temporaires ; `BFU_WINE` et `BFU_WINESERVER` pointent vers de faux scripts Python (créés par le test dans un dossier temporaire, avec `chmod +x` ; ou `BFU_WINE` découpé par `shlex` pour accepter `python3 faux_wine.py`) qui notent leur ligne de commande, leur `cwd` et `LANG`, puis dorment) : liens et copie de `BepInEx` présents ; commande avec `-bfump-port 7781` et `-batchmode` ; `cwd` = dossier d'instance ; `LANG == "C.UTF-8"` ; `stop("x")` écrit `stop.request`, termine le processus et supprime le dossier ; `prepare_prefixes` appelle `wineboot -i` une seule fois par préfixe.
- [ ] **Step 2: Run** `python -m unittest discover -s tests -v` (dans `MOD\server\`). Expected : FAIL sur les nouveaux tests.
- [ ] **Step 3: Implement** `api.py`, `launcher.py`, `__main__.py`.
- [ ] **Step 4: Run** la même commande. Expected : PASS (sous Windows, `test_launcher.py` est sauté par `@unittest.skipIf(os.name == "nt", …)` ; il tourne dans l'image à la Tâche 3).

### Task 3: Image, compose et premier déploiement (serveur maison)

**Files:**
- Create: `MOD\server\Dockerfile`, `MOD\server\entrypoint.sh`, `MOD\server\compose.yaml`
- Server : `/home/<utilisateur>/bfu-server/docker/` (copie de `MOD\server\`), jeu déjà dans `/home/<utilisateur>/bfu-server/game/`

**Interfaces:**
- Consumes : `python -m bfu_servers` (Tâche 2).
- Produces : image locale `bfu-servers:latest`, service `https://bfu.example.org`, conteneur `bfu-servers`.

- [ ] **Step 1: Write** le `Dockerfile`, en partant de la recette d'essai qui fonctionne (`~/bfu-server/Dockerfile` et `entrypoint.sh` sur le serveur maison) : base `debian:bookworm-slim`, paquets `wine wine64 python3 ca-certificates procps`, variables `WINEARCH=win64 WINEDEBUG=-all WINEDLLOVERRIDES="winhttp=n,b;mscoree=;mshtml=" LANG=C.UTF-8`, `COPY bfu_servers /app/bfu_servers`, `COPY tests /app/tests` et `COPY entrypoint.sh /entrypoint.sh`, `ENTRYPOINT ["sh", "/entrypoint.sh"]` (fichiers copiés depuis Windows : pas de bit exécutable ; fins de ligne LF obligatoires dans `entrypoint.sh`), qui fait `cd /app && exec python3 -m bfu_servers` (`exec` pour que l'arrêt du conteneur atteigne l'orchestrateur). Ajouter `xvfb` et `Xvfb :99` seulement si la Tâche 0 a montré que le jeu en a besoin.
- [ ] **Step 2: Build over SSH** : copier `MOD\server\` vers `<utilisateur>@<serveur>:/home/<utilisateur>/bfu-server/docker/`, puis `docker build -t bfu-servers:latest /home/<utilisateur>/bfu-server/docker`. Lancer les tests dans l'image : `docker run --rm --entrypoint python3 -w /app bfu-servers:latest -m unittest discover -s tests -v`. Expected : tous PASS, `test_launcher.py` compris.
- [ ] **Step 3: Write** `compose.yaml` : service `bfu-servers`, `container_name: bfu-servers` (sinon Dokploy préfixe le nom et les commandes `docker logs bfu-servers` des étapes suivantes échouent), **`image: bfu-servers:latest`** (pas de `build:` : Dokploy ne voit pas `/home/<utilisateur>`), `init: true`, `restart: unless-stopped`, `mem_limit: 3g`, `cpus: 2`, ports `7780-7782:7780-7782/udp`, volumes `/home/<utilisateur>/bfu-server/game:/game:ro` et `bfu-data:/srv/bfu`, réseau `dokploy-network`, labels Traefik vers le port 8080 pour `bfu.example.org` avec TLS Let's Encrypt (reprendre la forme des labels d'un compose existant du Dokploy maison).
- [ ] **Step 4: Deploy** : supprimer l'ancien conteneur d'essai (`docker rm -f bfu-test`), créer le compose dans le Dokploy maison via son API (`compose.create`, `compose.update` avec le contenu, `compose.deploy`), avec la clé `~/.config/<dokploy>/maison.key` du serveur maison. Ne jamais afficher la clé.
- [ ] **Step 5: Verify** :
  - `curl -s https://bfu.example.org/health` → `{"ok": true, "running": 0}`.
  - Une requête depuis le PC principal, puis une depuis PC2 (autre réseau, par Internet et non par Tailscale) : noter l'IP du client et les en-têtes journalisés par l'API (`docker logs bfu-servers`). Expected : le PC principal est reconnu « maison » (adresse privée ou IP publique de la box), PC2 apparaît avec sa propre IP publique et n'est **pas** reconnu « maison ». Sinon, corriger l'ordre des en-têtes ou la règle de la Tâche 1, et leurs tests.
  - `POST /servers` → port 7780, un processus `Boomerang Fu.exe` dans le conteneur, et le `LogOutput.log` de l'instance contient `menus ready`. Deux POST (deux IP, par exemple une via Tailscale avec `curl --resolve`) → deux instances simultanées.
  - Sans client (le mod ne lit pas encore `-bfump-dedicated`, donc pas de `ready`) : l'instance est arrêtée au bout de 180 s et `running` revient à 0.

### Task 4: Protocole 6, transport, session sans joueur local et règles du serveur (mod)

**Files:**
- Create: `MOD\src\Dedicated\ServerRules.cs` (pur, sans Unity), `MOD\tests\BoomerangFuMultiplayer.Tests\BoomerangFuMultiplayer.Tests.csproj`, `MOD\tests\BoomerangFuMultiplayer.Tests\ServerRulesTests.cs`
- Modify: `MOD\BoomerangFuMultiplayer.csproj` (`<Compile Remove="tests\**" />`, `<Compile Remove="server\**" />`), `MOD\src\Net\Protocol.cs`, `MOD\src\Net\ITransport.cs`, `MOD\src\Net\DirectTransport.cs`, `MOD\src\Net\SteamTransport.cs`, `MOD\src\Net\Session.cs`, `MOD\src\Sync\NetInput.cs`, `MOD\src\Sync\MatchSync.cs`, `MOD\src\Sync\RoundSync.cs`, `MOD\src\PluginInfo.cs`

**Interfaces:**
- Produces :
  - `Protocol.Version = 6` ; `Msg.Kick = 24` (client → hôte : `byte targetSlot`), `Msg.Launch = 25` (client → hôte, vide), `Msg.SalonState = 26` (hôte → clients : `string serverName`, `sbyte leaderSlot`, `byte phase` : 0 salon, 1 persos, 2 partie). `Msg.Bye` porte désormais une raison facultative (`string`, vide par défaut), affichée par le client.
  - `Hello` (client → hôte) se termine par `string token` puis `byte ownedDlc` : bit 0 `FreshFlavorsPack`, bit 1 `JustDessertsPack`, bit 2 `StreetFoodPack`, d'après `GameManager.IsDLCUnlocked` sur le PC du joueur.
  - `Msg.Notice = 28` (hôte → un client : `string`, message à afficher en toast ; utilisé à la Tâche 5).
  - `Msg.ToSalon = 27` (hôte → clients, vide) : si un match tourne encore chez lui (`MatchSync.MatchInProgress`), le client le quitte comme l'hôte (`UIManager.QuitMatch` sous `Session.HostScope()`) ; dans tous les cas il suit ensuite la pile de menus de l'hôte jusqu'à l'écran EN LIGNE (un second `QuitMatch` retirerait l'écran EN LIGNE déjà remis).
  - L'hôte dédié envoie `Msg.SalonState` à **chaque** changement et aussi au nouveau client juste après son `Welcome`.
  - `ITransport.PeerAddress(int peer) -> string` : IP seule, sans port (`DirectTransport` : `peer.EndPoint.Address` ; `SteamTransport` : le SteamID).
  - `DirectTransport.Host(int port, int maxPeers = Protocol.MaxSlots - 1)` : l'hôte dédié passe `Protocol.MaxSlots` (fichier actuel : limite codée en dur, ligne 36).
  - `enum ServerPhase { Salon, Persos, Partie }` ; `enum AfterChange { None, BackToSalon }`.
  - `sealed class ServerRules(string ownerToken)` : `void OnJoin(int slot, string name, string address, string token)`, `void OnLeave(int slot)`, `int LeaderSlot { get; }` (−1 si personne), `bool IsBanned(string name, string address)`, `bool TryKick(int bySlot, int targetSlot)` (ajoute le bannissement), `bool CanLaunch(int bySlot, int playerCount)`, `static AfterChange Evaluate(ServerPhase phase, int players, bool matchJustEnded)`.
  - Côté client (dans `Session`, pour que les Tâches 5 à 7 s'en servent) : `Session.Salon` (dernier `SalonState` reçu : `ServerName`, `LeaderSlot`, `Phase`, ou `null` hors serveur dédié), `Session.IsLeader` (`Salon != null && Salon.LeaderSlot == LocalSlot`), événement `SalonChanged` ; `Msg.Notice` → toast et ligne `[Online] notice: <texte>` ; `Msg.ToSalon` traité comme décrit ci-dessus ; raison de `Msg.Bye` → toast. Toast « {pseudo} est maintenant l'hôte » quand `LeaderSlot` change.
  - `Session.HostDedicated(int port, string ownerToken)` : hôte sans place locale (`LocalSlot == -1`, les 4 places attribuables, place 0 comprise) ; `Session.IsDedicatedHost` ; `Session.Active` vrai pour cet hôte ; `Session.Rules` (`ServerRules`, non nul seulement pour l'hôte dédié) ; `Session.OwnedDlc(int slot) -> byte`.
  - `Session.JoinDirect(string ip, int port, string token = null)`.
  - `Session.Kick(int slot, string reason)` (hôte) : envoie `Msg.Reject` avec la raison (le client quitte déjà en le recevant, `Session.OnReject`), puis force la déconnexion de ce pair **1 s plus tard** (pas avant, sinon LiteNetLib jette le message), seulement si la place contient encore ce même pair (LiteNetLib réutilise les numéros de pair).

- [ ] **Step 1: Create** le projet de tests : net48, `<LangVersion>9</LangVersion>` (comme le mod), `xunit` 2.4.2, `xunit.runner.visualstudio` 2.4.5, `Microsoft.NET.Test.Sdk` 17.8.0, `<Compile Include="..\..\src\Dedicated\ServerRules.cs" />`, référence `Newtonsoft.Json` vers `..\..\..\..\Boomerang Fu_Data\Managed\Newtonsoft.Json.dll` (celle du jeu, utilisée par les Tâches 5 et 6).
- [ ] **Step 2: Write the failing tests** (`ServerRulesTests.cs`) :
  - `Owner_is_leader` : `OnJoin(1,"A","1.1.1.1","tok")` puis `OnJoin(2,"B","2.2.2.2","")` avec `ownerToken "tok"` → `LeaderSlot == 1`.
  - `Leader_passes_to_oldest_when_owner_leaves` : A (jeton), B, puis C ; `OnLeave(1)` → `LeaderSlot == 2`.
  - `Without_owner_first_joiner_leads` : B puis C sans jeton → `LeaderSlot` = place de B ; A arrive ensuite avec le jeton → `LeaderSlot` = place de A.
  - `Only_leader_can_kick_and_not_himself` : `TryKick(2,1) == false`, `TryKick(1,1) == false`, `TryKick(1,2) == true`.
  - `Kicked_player_is_banned_by_name_or_address` : après `TryKick(1,2)` → `IsBanned("B","9.9.9.9")` et `IsBanned("Z","2.2.2.2")` vrais.
  - `Launch_needs_leader_and_two_players` : `CanLaunch(1,1) == false`, `CanLaunch(2,2) == false`, `CanLaunch(1,2) == true`.
  - `Lone_player_rules` : `Evaluate(Persos,1,false) == BackToSalon` ; `Evaluate(Partie,1,false) == None` ; `Evaluate(Partie,1,true) == BackToSalon` ; `Evaluate(Salon,1,false) == None` ; `Evaluate(Persos,2,false) == None`.
- [ ] **Step 3: Run** `dotnet test tests\BoomerangFuMultiplayer.Tests` (dans `MOD\`). Expected : FAIL (types absents).
- [ ] **Step 4: Implement** `ServerRules`, puis le transport (`PeerAddress`, limite de pairs) et `Protocol`/`Session` :
  - Boucles qui supposent « la place 0 est l'hôte local », à faire partir de `FirstRemoteSlot` (0 pour l'hôte dédié, 1 sinon) : `Session.cs` lignes 258, 267, 296, 303 ; `MatchSync.cs` lignes 420 et 454 (`ProcessLateJoiners`) ; `RoundSync.cs` ligne 93. (`UiMirror.cs:259` parcourt un chemin d'objet, pas des places : ne pas y toucher.)
  - `OnHello` vérifie d'abord la version (un client v5 envoie un `Hello` plus court : il doit recevoir le refus « Version du mod différente », pas une exception), puis lit le jeton et `ownedDlc`, journalise `[Session] <pseudo> dlc=<octet>`, refuse les bannis avec « Tu as été exclu de la partie », appelle `Rules.OnJoin(slot, name, PeerAddress(peer), token)`. `RemoveSlot` appelle `Rules.OnLeave`.
  - `PluginInfo.Version = "2.3.0"`. `NetInput.Tick` : pas d'échantillonnage local si `LocalSlot < 0`.
- [ ] **Step 5: Run** `dotnet test tests\BoomerangFuMultiplayer.Tests` puis `dotnet build -c Release`. Expected : tests PASS, build sans erreur ni avertissement. Puis vérifier sur un seul PC que le mode actuel n'a pas changé : PC principal en hôte avec `-bfump-host -bfump-autoplay -bfump-bots 1 -bfump-wait-players 1` → une manche jouée (`state Playing round 1` dans le journal).

### Task 5: Hôte dédié dans le mod (instance)

**Files:**
- Create: `MOD\src\Dedicated\DedicatedConfig.cs`, `MOD\src\Dedicated\StatusFile.cs` (pur), `MOD\src\Sync\MenuStackDiff.cs` (pur), `MOD\src\Dedicated\DedicatedHost.cs` (Unity), `MOD\tests\BoomerangFuMultiplayer.Tests\StatusFileTests.cs`, `MOD\tests\BoomerangFuMultiplayer.Tests\MenuStackDiffTests.cs`
- Modify: `MOD\src\Plugin.cs` (arguments, appel de `DedicatedHost.Tick`, option de test `-bfump-fake-dlc <octet>` qui remplace l'octet `ownedDlc` envoyé), `MOD\src\Sync\NetInput.cs` (`RouteOf`), `MOD\src\Sync\LobbySync.cs`, `MOD\src\Sync\MatchSync.cs` (`PausePrefix`, listes de déblocage), `MOD\src\Sync\UiMirror.cs` (`Reconcile`), `MOD\src\Debug\AutoPilot.cs` (modes de test `-bfump-test-leader` et `-bfump-test-nonleader`, Step 6), `MOD\src\UI\OnlineMenu.cs` (`StartLobby` rendue `internal`)

**Interfaces:**
- Consumes : `ServerRules`, `Session.HostDedicated`, `Session.Kick`, `Session.OwnedDlc`, les nouveaux `Msg` (Tâche 4).
- Produces :
  - `DedicatedConfig.Parse(string[] args) -> DedicatedConfig` (`Enabled`, `Port`, `OwnerToken`, `Name`, `StatusPath`, `StopPath`) ; `DedicatedConfig.Current`.
  - `StatusFile.Serialize(bool ready, ServerPhase phase, IList<string> players, string leader) -> string` (format de la Tâche 1) ; `StatusFile.WriteAtomic(string path, string json)` : fichier `.tmp` puis `File.Replace` (ou `File.Move` si la cible n'existe pas encore).
  - `DedicatedHost.Active`, `DedicatedHost.LeaderSlot`, `DedicatedHost.Phase` ; `DedicatedHost.Tick()` appelé par `Plugin.Update`.
- Comportement de l'instance :
  - **Démarrage** : une fois les menus prêts, passer l'écran d'accueil (`UIMenuSplash.UIResponse_Begin`, comme `Plugin.cs` lignes 137-138), attendre que le menu courant soit `Main`, ouvrir l'écran EN LIGNE (`OnlineMenu.Open()`, pile `Splash>Main>42`), lancer `Session.HostDedicated(Port, OwnerToken)` (sans UPnP ni pilote automatique), puis seulement écrire `"ready": true`.
  - **Toutes les 2 s** : écrire le statut ; envoyer `Msg.SalonState` à chaque changement (et après chaque `Welcome`, Tâche 4). Phase : `Partie` si `MatchSync.MatchInProgress`, `Persos` si le menu courant est `Lobby` ou `MatchSettings`, sinon `Salon`.
  - **À chaque image** : si `stop.request` existe, envoyer `Msg.Bye` avec la raison qu'il contient à tous les clients, attendre 1 s, quitter (`Application.Quit`). Vider `GameManager.nextCharactersToUnlock` et `nextHatsToUnlock` (le serveur ne gagne aucune progression, donc jamais d'écran `Unlocked` qui attendrait une touche).
  - **Joueur resté seul** : `ServerRules.Evaluate(phase, playerCount, matchJustEnded)`, où `matchJustEnded` est vrai dès que le menu courant est `MatchEnd` (`UIMenuBase.MenuType.MatchEnd = 9`). `BackToSalon` :
    - en match ou sur l'écran de fin de match : `UIManager.QuitMatch()` (il vide la pile et ne remet que `Main`), puis `OnlineMenu.Open()` une seule fois, et `Msg.ToSalon` aux clients ;
    - au choix des persos ou aux réglages : dépiler (`UIManager.PopMenu`) jusqu'à l'écran EN LIGNE.
    - Même retour quand le menu courant devient `Main` hors match (l'hôte délégué a choisi « menu principal ») : `OnlineMenu.Open()` et `Msg.ToSalon`. Jamais deux écrans EN LIGNE dans la pile.
  - `Msg.Launch` de l'hôte délégué : accepté seulement en phase `Salon` et si `CanLaunch` est vrai, puis `OnlineMenu.StartLobby()` (méthode rendue `internal`). `Msg.Kick` : `TryKick` puis `Session.Kick(slot, "Tu as été exclu de la partie")`.
  - **Seul l'hôte délégué pilote** : `NetInput.RouteOf` renvoie `Neutral` pour les entrées de menu (`UiContext > 0`) des places autres que `LeaderSlot`. Dans le choix des persos, les actions « continuer » (charge de `UIMenuLobby.ReadyCharge` par n'importe quel joueur prêt, `UILobbyPlayer.cs` 718-728) et « retour » (`OnBackButtonPressed`, 526-528) lisent chaque manette hors `UiContext` : retenir le `playerID` du `UILobbyPlayer` en cours dans un préfixe et un finalizer sur `UILobbyPlayer.Update`, et n'accepter ces deux actions que si ce `playerID == LeaderSlot` (sinon journaliser `[Dedicated] ignored continue from slot <n>`, une fois par maintien). **Le même filtre s'applique sur les PC des joueurs** (leader du dernier `SalonState`), sinon la jauge d'un non-chef se remplit localement et fige sa navigation (`UIMenuLobby.cs` 105-116). `MatchSync.PausePrefix` n'accepte la pause que si `drivingPlayer.id == LeaderSlot`. Sur l'instance, `NetInput` renvoie aussi « relâché » pour les actions `Start` (6) et `UISelect` (22) des places autres que `LeaderSlot`, **partout** et pas seulement en `UiContext` (`UISelect` ouvre l'écran Instructions en plein match, ce qui met tout le monde en pause, `RewiredController.cs` 56-63), et pour `UIRestore` (23) **seulement quand le menu courant est `MatchSettings`** (bascule des équipes de bots, `UILobbyPlayer.cs` 485-488) : au choix des persos, `UIRestore` sert à chaque joueur à prendre un perso alternatif (`UILobbyPlayer.cs` 572). Quand ce filtre jette un appui (front montant), il journalise `[Dedicated] ignored pause from slot <n>` (Start) ou `[Dedicated] ignored instructions from slot <n>` (UISelect).
  - **Sur les PC des joueurs** (toute session, mode actuel compris) : `NetInput` renvoie « relâché » pour `Start` et `UISelect` de toutes les places hors `UiContext`, car c'est l'hôte qui décide de la pause et de l'écran Instructions (reproduits par `Msg.Pause` et la pile de menus). Sinon `RewiredController` ouvre l'écran Instructions et met le jeu en pause chez ce joueur seul, qui reste figé (`UIMenuInstructions.cs` 40). L'échantillonnage envoyé à l'hôte n'est pas touché (`LocalSampler` lit le périphérique directement).
  - Quand `Msg.Launch` est accepté, remettre à zéro les manettes virtuelles des clients (`NetInput.ClearSlot` pour chaque place) : les appuis faits pendant le SALON, ignorés par l'écran EN LIGNE, ne doivent pas être rejoués dans le choix des persos (Dash = rejoindre).
  - **Déblocages** (hôte dédié) :
    - Remplissage : dans un postfix de `SettingsManager.OnUnlockedItemsLoaded` (appelé à **chaque** chargement de `save.data`, y compris celui de `UIMenuSplash.Setup`, `SettingsManager.cs` ~1088), ajouter à `SettingsManager.UnlockedItems` tous les persos hors DLC et tous les chapeaux qu'ils peuvent porter (`GameManager.UnlockHatForCharacter(..., saveData: false)`). Toutes les fonctions de chapeaux du jeu lisent alors des données complètes. **Ne pas bloquer les sauvegardes** : `UIMenuMatchEnd` attend le rappel de `SaveUnlockedItems` pour afficher REVANCHE et QUITTER (`UIMenuMatchEnd.cs` 143-149) ; la sauvegarde du serveur, dans son propre préfixe, est sans importance.
    - DLC : patcher `GameManager.IsDLCUnlocked(pack)` sur l'hôte dédié. Un « joueur courant » est posé pendant l'`Update` d'un `UILobbyPlayer` (son `playerID`) et pendant l'ajout d'un joueur en cours de match par `MatchSync` (`AddPlayerLocally` / `PickNewPlayer`, `MatchSync.cs` 508-529 : la place du nouveau venu). S'il y en a un, le patch renvoie vrai si `Session.OwnedDlc(joueur courant)` contient ce pack ; sinon (grille, bots), vrai si au moins un joueur connecté le possède. Quand il renvoie faux pour un joueur courant, il journalise une fois par couple (place, pack) `[Dedicated] DLC character refused for slot <n>`. Ce patch couvre les persos (`IsCharacterUnlocked`) et les chapeaux de DLC (`CanCharacterWearHat`, `GameManager.cs` 1182).
    - À chaque changement de joueurs : `GameManager.UnlockDLCItems()`, puis remettre toutes les cases de la grille à `Unlocked` et relancer `CheckLockedState` (elle ne fait que verrouiller, `UICharacterGridCell.cs` 122-138).
    - Sur les PC des joueurs, rien ne change dans leur sauvegarde ; une case peut y paraître verrouillée, l'affichage suit celui du serveur (`LobbySync`).
  - **Changement d'hôte délégué** : appeler `UIManager.eventSystemPlayerSwapper.ForceSpecificPlayerToMasterPlayer(<joueur Rewired du nouveau chef>)`. En pause, le jeu ne redonne jamais les commandes du menu à un autre joueur (`UIEventSystemPlayerSwapper.cs` 29-39) : sans cet appel, la partie resterait en pause si le chef part.
  - **Refus de LANCER** : l'hôte journalise `[Dedicated] launch refused` et répond par `Msg.Notice` avec `IL FAUT AU MOINS 2 JOUEURS`.
  - **Retour au SALON pendant le lancement d'un match** : ignorer `BackToSalon` tant que le match se charge (`GameManager.gameState == Loading`, ou départ de match en cours dans `MatchSync`) ; réévaluer une fois en jeu.
  - **Écran de déblocage chez les joueurs** : pendant toute session en ligne (hôte comme clients), vider aussi `nextCharactersToUnlock` et `nextHatsToUnlock` à chaque image. Le jeu a déjà enregistré le déblocage dans la sauvegarde avant de remplir ces listes (`GameManager.cs` 1427-1431 et 1522-1523) : seul l'écran disparaît, qui bloquait le joueur hors de la revanche.
  - **Piles de menus** : `UIManager.QuitMatch` laisse une pile sans `Splash` en bas. Extraire la comparaison des piles de `UiMirror.Reconcile` dans une fonction pure `MenuStackDiff.Plan(int[] host, int[] local) -> (int pops, int[] pushes)?` (`null` si non conciliable) qui **ignore un `Splash` (12) en tête de chacune des deux piles**, et l'utiliser dans `Reconcile`.

- [ ] **Step 1: Write the failing tests** (`StatusFileTests.cs` et `MenuStackDiffTests.cs` ; lier `StatusFile.cs`, `DedicatedConfig.cs` et `MenuStackDiff.cs` dans le projet de tests). Ces fichiers restent purs : ni `Protocol.Version` (LiteNetLib) ni `OnlineMenu.MenuId` (Unity) ; le protocole (6) et les menus conciliables (dont 42) sont des constantes locales ou des paramètres. `MenuStackDiff` : hôte `[12,1,42]` et joueur `[1,42]` → `(0, [])` ; hôte `[1,42]` et joueur `[12,1,3]` → `(1, [42])` ; hôte `[12,1,42,3]` et joueur `[1,42]` → `(0, [3])` ; une pile contenant un menu non conciliable au-delà de la partie commune (par exemple `9`, fin de match) → `null`. `Serialize(true, Persos, ["A","B"], "A")` → JSON dont `ready == true`, `phase == "persos"`, `players == ["A","B"]`, `leader == "A"`, `max == 4`, `protocol == 6` ; `WriteAtomic` appelé **deux fois** sur le même chemin → le fichier contient le second JSON, sans exception ; `DedicatedConfig.Parse(["-bfump-dedicated","-bfump-port","7781","-bfump-owner","tok","-bfump-name","Jean Luc","-bfump-status","Z:\\s.json","-bfump-stopfile","Z:\\stop"])` → champs attendus ; sans `-bfump-dedicated` → `Enabled == false`.
- [ ] **Step 2: Run** `dotnet test tests\BoomerangFuMultiplayer.Tests`. Expected : FAIL.
- [ ] **Step 3: Implement** `DedicatedConfig`, `StatusFile`, `DedicatedHost` et les modifications de `Plugin`, `NetInput`, `LobbySync` et `MatchSync`.
- [ ] **Step 4: Run** les tests, puis `dotnet build -c Release`. Expected : PASS, build propre.
- [ ] **Step 5: Verify on the server** : copier la DLL dans `/home/<utilisateur>/bfu-server/game/BepInEx/plugins/BoomerangFuMultiplayer/`, redémarrer le conteneur, `POST /servers`. Le `LogOutput.log` de l'instance contient `[Direct] hosting on UDP 7780` et pas `[UPnP]`, et `status.json` passe à `"ready": true`.
- [ ] **Step 6: Verify the delegated host early** (avant toute interface). Redéployer avec `BFU_OWNER_TIMEOUT=900` le temps du test, `POST /servers` (curl). Le PC principal rejoint par `steam.exe -applaunch 965680 -bfump-join <IP locale du serveur>:7780` (sans jeton : premier arrivé, donc hôte délégué) ; PC2 (agent distant « agent distant ») par l'adresse Tailscale du serveur maison, `<IP Tailscale du serveur>:7780` (les ports de la box ne sont ouverts qu'à la Tâche 7). Copier d'abord la nouvelle DLL sur PC2 (`tailscale file cp <dll> PC2:`, puis l'agent distant la place dans `BepInEx\plugins\BoomerangFuMultiplayer\`). Options de test du pilote automatique (ajoutées dans cette tâche) : `-bfump-test-leader` (en tant qu'hôte délégué : envoie `Msg.Launch` une fois seul, puis de nouveau à 2 joueurs ; au choix des persos, rejoint, se met prêt et, quand 2 joueurs sont prêts, maintient l'action « commencer ») et `-bfump-test-nonleader` (rejoint, se met prêt, maintient « commencer » 3 s). Ordre imposé : lancer le PC principal (`-bfump-autoplay -bfump-test-leader -bfump-join <IP locale du serveur>:7780`), attendre `[Dedicated] launch refused` dans le journal de l'instance, **puis** lancer PC2 (`-bfump-autoplay -bfump-test-nonleader -bfump-join <IP Tailscale du serveur>:7780`). Au choix des persos, le non-chef maintient « commencer » 3 s dès que 2 joueurs sont prêts ; le chef attend 8 s après ce moment avant de maintenir à son tour. Expected :
  - pile alignée sur `Splash>Main>42` ; premier `Msg.Launch` refusé (`[Dedicated] launch refused` dans le `LogOutput.log` de l'instance, toast `IL FAUT AU MOINS 2 JOUEURS` chez le PC principal), second accepté : les deux passent au choix des persos ;
  - l'instance journalise `[Dedicated] ignored continue from slot <n>` pour PC2, puis les réglages s'ouvrent quand le PC principal maintient « commencer ».
  - La vérification de la pause (refusée aux non-chefs) se fait en match, à la Tâche 7.
  - Le mod journalise l'écart maximal entre deux écritures du statut pendant un départ de match (`[Dedicated] longest status gap <n> s`) : il doit rester sous `BFU_STALE_AFTER` (30 s).
  - Remettre ensuite les délais par défaut.

### Task 6: Pages SERVEURS et SALON (mod, côté joueur)

**Files:**
- Create: `MOD\src\Dedicated\ServerDirectory.cs` (requêtes `UnityWebRequest`, délai **5 s**), `MOD\src\Dedicated\ServerDirectoryParsing.cs` (pur), `MOD\tests\BoomerangFuMultiplayer.Tests\ServerDirectoryParsingTests.cs`
- Modify: `MOD\src\UI\OnlineMenu.cs` (pages), `MOD\src\UI\OnlineHud.cs` (badge HÔTE pour l'hôte délégué, toast du changement d'hôte), `MOD\src\Plugin.cs` (option `Network.ServerDirectory`, défaut `https://bfu.example.org`), `MOD\BoomerangFuMultiplayer.csproj` (référence `UnityEngine.UnityWebRequestModule`)

**Interfaces:**
- Consumes : `Msg.SalonState`, `Msg.Kick`, `Msg.Launch`, raison de `Msg.Bye`, `Session.JoinDirect(ip, port, token)` (Tâche 4).
- Produces :
  - `sealed class ServerEntry { string Id, Name, Phase, Address; int Players, Max, Port, Protocol; }`.
  - `ServerDirectoryParsing.ParseList(string json) -> List<ServerEntry>` (`null` si JSON invalide) ; `ParseCreate(string json, out string error) -> (string address, int port, string token)?` ; `ListLabel(ServerEntry e) -> string`.
  - `ServerDirectory.Fetch(Action<List<ServerEntry>, string> done)` ; `ServerDirectory.Create(string name, Action<(string address, int port, string token)?, string> done)` (erreur = message français de l'API, ou `LISTE INDISPONIBLE`).
  - `OnlineMenu` : `enum Page { Online, Servers, Salon }`, bouton `SERVEURS` en tête de la page EN LIGNE. Page SALON quand le joueur est connecté à un hôte dédié (un `SalonState` reçu) et que la phase vaut `Salon`.
- Règles : libellés exacts des Global Constraints. Entrée grisée et non cliquable si `Players >= Max`. EXCLURE et LANCER sont des boutons locaux de `OnlineMenu` (pas reproduits par `UiMirror`) qui envoient `Msg.Kick` et `Msg.Launch`. Sur la page SALON, la touche retour (`UICancel`) fait QUITTER LE SALON (sinon `Back()` fermerait la page localement et la synchro des menus la rouvrirait). LANCER est grisé à moins de 2 joueurs. CRÉER : `Create(Session.LocalName)` (corps JSON envoyé avec `UploadHandlerRaw` et `Content-Type: application/json`, car `UnityWebRequest.Post(url, string)` encoderait le corps en formulaire), puis la page affiche `DÉMARRAGE DU SERVEUR…` et appelle `JoinDirect(address, port, token)`. Chaque échec de connexion (LiteNetLib abandonne au bout d'environ 5 s) relance une tentative, sans afficher le toast « Connexion impossible », pendant **90 s** au plus (ajuster d'après la mesure de la Tâche 0) ; jamais deux tentatives en même temps. Quand le leader de `SalonState` change, toast « {pseudo} est maintenant l'hôte » ; la raison d'un `Bye` (par exemple « Serveur fermé pour inactivité ») s'affiche en toast.

- [ ] **Step 1: Write the failing tests** (lier `ServerDirectoryParsing.cs`) : `ParseList` sur un tableau de 2 serveurs → `Count == 2`, `Name == "Joueur1"`, `Port == 7781` ; sur `""`, `"<html>"` ou `"{"` → `null` ; `ParseCreate("{\"error\":\"Tous les serveurs sont occupés\"}", out e)` → `null` et `e == "Tous les serveurs sont occupés"` ; `ListLabel` d'une entrée `Name "X", Players 4, Max 4` → `"PARTIE DE X · 4/4 · COMPLET"`, avec `Phase "partie", Players 2` → `"PARTIE DE X · 2/4 · EN PARTIE"`.
- [ ] **Step 2: Run** `dotnet test tests\BoomerangFuMultiplayer.Tests`. Expected : FAIL.
- [ ] **Step 3: Implement** le client, les pages, le badge et les toasts.
- [ ] **Step 4: Run** les tests et le build. Expected : PASS, build propre.
- [ ] **Step 5: Verify in game** (PC principal, lancé par `steam.exe -applaunch 965680`) : EN LIGNE > SERVEURS affiche `0 SERVEURS EN LIGNE`. Avec `Network.ServerDirectory = https://10.255.255.1` (adresse qui ne répond pas) : `LISTE INDISPONIBLE` en 5 s au plus, puis ACTUALISER réessaie. Avec l'adresse normale : CRÉER UN SERVEUR → `DÉMARRAGE DU SERVEUR…` puis SALON avec `EN ATTENTE DE JOUEURS · 1/4` et LANCER grisé. Capture d'écran comparée aux maquettes de la spec, supprimée ensuite si d'autres fenêtres y apparaissent.

### Task 7: Pilote automatique, validation de bout en bout et livraison

**Files:**
- Modify: `MOD\src\Debug\AutoPilot.cs`, `MOD\src\Plugin.cs` (argument `-bfump-servers create|join`)
- Modify: `MOD\SETUP.md`, paquet `scratchpad\pkg` (LISEZMOI) et `MOD\dist\BoomerangFuOnline-v2.3.0.zip`

**Interfaces:**
- Consumes : tout ce qui précède.
- Produces : pilote automatique client.
  - Sur ses propres pages (SERVEURS, SALON), il appelle directement les actions de `OnlineMenu` (la manette virtuelle ne s'active qu'une fois en session) : `create` → CRÉER puis LANCER dès 2 joueurs ; `join` → rejoindre le premier serveur non complet.
  - Pour les menus de l'instance (choix des persos, réglages, Instructions, fin de manche, fin de match), il agit **uniquement par la manette virtuelle** (`NetInput.AutoSet` : `Dash` pour rejoindre, maintien de l'action de début pour continuer, `UISubmit` pour valider), sans appeler les méthodes des menus. Les conditions `Session.IsHost` des lignes 81, 91 et 97 deviennent « je suis l'hôte délégué » (`Session.IsLeader`) en mode serveur. La ligne 67 (ajout de bots par appel direct) reste réservée à l'hôte classique : en mode serveur, le pilote n'ajoute pas de bots. L'appel direct à `UIResponse_Fight` est remplacé par la navigation à la manette. Les réglages s'ouvrent sur la page du mode de jeu (`UIMenuMatchSettings.cs` 30) : valider d'abord le mode sélectionné (`UISubmit`), puis, **à l'exécution**, repérer le bouton dont `onClick.GetPersistentMethodName(i) == "UIResponse_Fight"` (le lien est dans les données de la scène, pas dans le code) et y naviguer (`UI Vertical`) avant `UISubmit`. Le pilote qui tourne sur un client lit la sélection reproduite par `UiMirror` pour savoir où il en est.
  - Écran de fin de match (`MatchEnd`) : en mode serveur, l'hôte délégué choisit le bouton du menu principal, pour exercer le retour au SALON ; en mode hôte classique (`-bfump-host`), le pilote de l'hôte valide la revanche (bouton sélectionné par défaut).
  - En mode `create`, il appuie une fois sur LANCER **avec un seul joueur** et journalise `[Diag] autopilot: launch refused alone` à la réception du `Msg.Notice`, puis attend le deuxième joueur.
  - EXCLURE et le perso de DLC (Step 4) : **opérateur le mainteneur**, au clavier sur le PC principal ; le pilote automatique ne les fait pas.
  - `-bfump-test-nonleader` (Tâche 5) appuie en plus une fois sur Start et une fois sur `UISelect` pendant la première manche jouée.

- [ ] **Step 1: Implement** le pilote automatique.
- [ ] **Step 2: Le mainteneur ouvre les ports** UDP 7780 à 7782 de la box vers <IP locale du serveur> (à lui demander, avec la marche à suivre dans l'interface de la box).
- [ ] **Step 3: Run the two-PC test** : copier la DLL finale sur PC2 (Taildrop) et sur le serveur maison. PC principal `-bfump-autoplay -bfump-servers create` ; PC2 (agent distant « agent distant », voir la mémoire `two-pc-test-setup`) `-bfump-autoplay -bfump-servers join`, par Internet. Expected : LANCER refusé à 1 joueur et accepté à 2, choix des persos, réglages, 3 manches avec des morts identiques dans les deux journaux, puis retour au SALON.
- [ ] **Step 4: Run the failure cases** : un joueur rejoint un serveur **après** son premier match (pile du serveur sans `Splash`), et un joueur qui vient de finir un match ailleurs rejoint un serveur neuf : tous deux suivent les menus jusqu'au match ; exclusion (message « Tu as été exclu de la partie » et retour impossible par la liste) ; départ de l'hôte **en SALON**, **au choix des persos** et **en match** (l'autre joueur reçoit les droits ; seul hors match, il revient au SALON ; en match, après la fin) ; serveur vide (disparu de la liste et processus arrêté en moins de 2 min) ; un joueur seul 10 min en SALON (toast « Serveur fermé pour inactivité ») ; 4e création refusée (« Tous les serveurs sont occupés » : redéployer avec `BFU_RATE_LIMIT=0` et `BFU_OWNER_TIMEOUT=900`, ouvrir 3 serveurs par `curl -X POST`, puis tenter CRÉER depuis le jeu ; remettre les valeurs par défaut ensuite) ; le PC principal (réseau du mainteneur) se connecte par <IP locale du serveur> ; en match, PC2 (`-bfump-test-nonleader`, qui appuie aussi une fois sur Start et une fois sur `UISelect` pendant une manche) ne peut ni mettre en pause ni ouvrir l'écran Instructions (`[Dedicated] ignored pause from slot <n>`) ; DLC (**opérateur le mainteneur**, sur son propre perso au PC principal) : avec `-bfump-fake-dlc 0`, un perso de DLC lui est refusé (`[Dedicated] DLC character refused for slot <n>`), avec `-bfump-fake-dlc 7` il est accepté. Relever `free -m` sur le serveur maison et `docker stats --no-stream bfu-servers` avec 3 parties ouvertes.
- [ ] **Step 5: Re-test the existing online mode** (non-régression des Tâches 4 et 5) : PC principal `-bfump-host -bfump-autoplay -bfump-bots 1 -bfump-wait-players 2`, PC2 `-bfump-join <IP Tailscale du PC principal> -bfump-autoplay`. Expected : 3 manches, morts identiques des deux côtés, fin de match puis revanche sans écran de déblocage bloquant.
- [ ] **Step 6: Livrer** : `SETUP.md` (architecture, déploiement, dépannage, durées de démarrage), LISEZMOI du zip (nouvelle section SERVEURS), zip `BoomerangFuOnline-v2.3.0.zip`, coffre Obsidian (note du projet, note Serveur maison, journal du jour), suppression des fichiers temporaires de test et de tout `steam_appid.txt` sur les deux PC.
