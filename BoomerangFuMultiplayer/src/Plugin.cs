using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BoomerangFuMultiplayer.Net;
using BoomerangFuMultiplayer.Sync;
using BoomerangFuMultiplayer.UI;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace BoomerangFuMultiplayer
{
    [BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance { get; private set; }
        internal static ManualLogSource Log;

        public static ConfigEntry<string> CfgLastIP;
        public static ConfigEntry<int> CfgPort;
        public static ConfigEntry<bool> CfgDumpUi;
        public static ConfigEntry<bool> CfgRunInBackground;
        public static ConfigEntry<bool> CfgUseSpacewar;
        /// <summary>Extra [Diag] log lines (sync measurements), enabled by -bfump-diag / -bfump-autoplay.</summary>
        public static bool Diag;
        /// <summary>Test: late joiners always get a new player instead of replacing a bot (-bfump-add-joiners).</summary>
        public static bool ForceAddJoiners;

        Harmony _harmony;
        Callback<GameLobbyJoinRequested_t> _joinRequested;
        Callback<GameRichPresenceJoinRequested_t> _richJoin;
        ulong _pendingLobby, _pendingHost;
        bool _menusReady;

        void Awake()
        {
            Instance = this;
            Log = Logger;

            CfgLastIP = Config.Bind("Network", "LastIP", "127.0.0.1", "Dernière adresse utilisée pour « Rejoindre » en IP directe (ip ou ip:port)");
            CfgPort = Config.Bind("Network", "Port", 7777, "Port UDP de l'hébergement en IP directe");
            CfgDumpUi = Config.Bind("Debug", "DumpUi", false, "Écrit la hiérarchie des menus clonés dans le log (diagnostic)");
            // Read by the preloader patcher (BoomerangFuMultiplayer.Patcher) before Steam starts; bound here so it
            // appears in the config file with its description.
            CfgUseSpacewar = Config.Bind("Steam", "UseSpacewar", true,
                "Se présenter à Steam comme Spacewar (AppID 480) : invitations et parties Steam entre amis, même avec un jeu emprunté. " +
                "Steam affiche alors « Spacewar » ; le temps de jeu, les succès et les DLC de Boomerang Fu ne sont pas reconnus. " +
                "Redémarrer le jeu après un changement.");
            CfgRunInBackground = Config.Bind("General", "RunInBackground", true,
                "Le jeu continue de tourner quand sa fenêtre n'est pas active (sinon il se fige dès que la souris sort du jeu ou qu'on change de fenêtre)");
            Log.LogInfo($"[Plugin] runInBackground was {Application.runInBackground}, now {CfgRunInBackground.Value}");
            Application.runInBackground = CfgRunInBackground.Value;
            // Startup: the game loads its main scene in the background behind the studio logo; give that
            // load the full per-frame budget until the menus exist (restored in SetupAllMenusPostfix).
            _startupLoadingPriority = Application.backgroundLoadingPriority;
            Application.backgroundLoadingPriority = ThreadPriority.High;
            if (Application.isBatchMode)
            {
                // headless host (-batchmode -nographics): nothing limits the frame rate otherwise
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
                Log.LogInfo("[Plugin] headless mode: 60 fps cap");
            }

            _harmony = new Harmony(PluginInfo.GUID);
            TryPatch("NetInput", () => NetInput.Patch(_harmony));
            TryPatch("UiMirror", () => UiMirror.Patch(_harmony));
            TryPatch("LobbySync", () => LobbySync.Patch(_harmony));
            TryPatch("MatchSync", () => MatchSync.Patch(_harmony));
            TryPatch("RoundSync", () => RoundSync.Patch(_harmony));
            TryPatch("ReplayDiag", () => Debugging.ReplayDiag.Patch(_harmony));
            TryPatch("Menus", () => _harmony.Patch(AccessTools.Method(typeof(UIManager), "SetupAllMenus"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(SetupAllMenusPostfix))));
            TryPatch("QuitTrace", () =>
            {
                foreach (var m in typeof(Application).GetMethods())
                    if (m.Name == "Quit") _harmony.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), nameof(QuitPrefix)));
            });

            var session = gameObject.AddComponent<Session>();
            NetInput.Register(session);
            UiMirror.Register(session);
            LobbySync.Register(session);
            MatchSync.Register(session);
            RoundSync.Register(session);
            session.Changed += LobbySync.RefreshDevices;
            gameObject.AddComponent<OnlineHud>();

            ReadCommandLine();
            Log.LogInfo($"[{PluginInfo.Name}] v{PluginInfo.Version} ready");
        }

        void TryPatch(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Log.LogError($"[Plugin] patching {what} failed: {e}"); }
        }

        /// <summary>
        /// A Steam join (invite, "Join game", +connect_lobby / +bfump_join at launch) must wait until the
        /// game's own menus are up: opening the online screen before the splash leaves the stack as
        /// "42>Splash", which can never follow the host's "Splash>Main>42…".
        /// </summary>
        static bool PendingJoinMenuReady()
        {
            var ui = Game.Get<UIManager>();
            var cur = ui != null ? ui.GetCurrentMenu() : null;
            if (cur == null) return false;                                  // still loading
            if (cur is UIMenuSplash splash) { splash.UIResponse_Begin(); return false; } // the player asked to join: skip "press any key"
            return true;
        }

        static ThreadPriority _startupLoadingPriority = ThreadPriority.Normal;
        float _nextStartupLog;
        int _startupFrames;

        static void SetupAllMenusPostfix(UIManager __instance)
        {
            OnlineMenu.Build(__instance);
            if (Instance != null) Instance._menusReady = true;
            Application.backgroundLoadingPriority = _startupLoadingPriority;
            Log.LogInfo($"[Plugin] menus ready after {Time.realtimeSinceStartup:0.0}s");
        }

        // Test automation: -bfump-host (host in direct IP) / -bfump-join <ip[:port]>, -bfump-lobby (host opens the lobby once someone joined)
        string _autoJoin;
        bool _autoHost, _autoHostSteam, _autoLobby, _autoDone, _autoLobbyDone;
        float _autoAt;

        void ReadCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "+connect_lobby" && i + 1 < args.Length && ulong.TryParse(args[i + 1], out ulong id)) _pendingLobby = id;
                if (args[i] == SteamTransport.JoinArg && i + 1 < args.Length && ulong.TryParse(args[i + 1], out ulong hostId)) _pendingHost = hostId;
                if (args[i] == "-bfump-host") _autoHost = true;
                if (args[i] == "-bfump-host-steam") { _autoHost = true; _autoHostSteam = true; }
                if (args[i] == "-bfump-lobby") _autoLobby = true;
                if (args[i] == "-bfump-join" && i + 1 < args.Length) _autoJoin = args[i + 1];
                if (args[i] == "-bfump-autoplay") { Debugging.AutoPilot.Enabled = true; Diag = true; }
                if (args[i] == "-bfump-diag") Diag = true;
                if (args[i] == "-bfump-add-joiners") ForceAddJoiners = true;
                if (args[i] == "-bfump-wait-players" && i + 1 < args.Length && int.TryParse(args[i + 1], out int wp))
                    Debugging.AutoPilot.WaitPlayers = wp;
                if (args[i] == "-bfump-bots" && i + 1 < args.Length && int.TryParse(args[i + 1], out int bots))
                    Debugging.AutoPilot.WantBots = bots;
                // test: bot level on the host (only the host runs bots): easy|medium|hard|impossible or 0..3
                if (args[i] == "-bfump-bot-difficulty" && i + 1 < args.Length)
                {
                    string v = args[i + 1].ToLowerInvariant();
                    int lvl = v == "easy" ? 0 : v == "medium" ? 1 : v == "hard" ? 2 : v == "impossible" ? 3 : int.TryParse(v, out int n) ? n : -1;
                    if (lvl >= 0 && lvl <= 3) Debugging.AutoPilot.BotDifficulty = lvl;
                }
            }
        }

        void AutoTest()
        {
            if (!_menusReady || (!_autoHost && _autoJoin == null)) return;
            var ui = Game.Get<UIManager>();
            var cur = ui != null ? ui.GetCurrentMenu() : null;
            if (!_autoDone)
            {
                if (cur is UIMenuSplash splash && Time.unscaledTime > 6f) { splash.UIResponse_Begin(); return; }
                if (cur == null || cur.MenuID != UIMenuBase.MenuType.Main) return;
                if (_autoAt == 0f) { _autoAt = Time.unscaledTime + 4f; return; }
                if (Time.unscaledTime < _autoAt) return;
                _autoDone = true;
                OnlineMenu.Open();
                if (_autoHostSteam) Session.Instance.HostSteam();
                else if (_autoHost) Session.Instance.HostDirect(CfgPort.Value);
                else
                {
                    string host = _autoJoin; int port = CfgPort.Value;
                    int c = host.LastIndexOf(':');
                    if (c > 0 && int.TryParse(host.Substring(c + 1), out int p)) { port = p; host = host.Substring(0, c); }
                    Session.Instance.JoinDirect(host, port);
                }
                return;
            }
            bool startAlone = Debugging.AutoPilot.WantBots > 0 && Session.Active; // host plays with bots right away
            if (_autoHost && (_autoLobby || startAlone) && !_autoLobbyDone && (Session.Instance.PlayerCount > 1 || startAlone) && OnlineMenu.IsCurrent)
            {
                _autoLobbyDone = true;
                var lobby = ui.GetMenu(UIMenuBase.MenuType.Lobby) as UIMenuLobby;
                ui.PushMenu(lobby);
                lobby.ResetAllPlayers();
            }
        }

        void Update()
        {
            // keep it on even if something resets it (cheap property)
            if (CfgRunInBackground.Value && !Application.runInBackground) Application.runInBackground = true;

            if (!_menusReady && Time.realtimeSinceStartup > _nextStartupLog)
            {
                // startup diagnostics: is the frame loop alive, and is the game scene loaded yet?
                int frames = Time.frameCount;
                if (_nextStartupLog > 0f)
                    Log.LogInfo($"[Plugin] startup {Time.realtimeSinceStartup:0}s: {(frames - _startupFrames) / 5f:0.0} fps, GameScene loaded {UnityEngine.SceneManagement.SceneManager.GetSceneByName("GameScene").isLoaded}, scenes {UnityEngine.SceneManagement.SceneManager.sceneCount}");
                _startupFrames = frames;
                _nextStartupLog = Time.realtimeSinceStartup + 5f;
            }

            if (_joinRequested == null && SteamTransport.Available)
            {
                try { Log.LogInfo($"[Steam] running as AppID {SteamUtils.GetAppID().m_AppId}"); } catch { }
                _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(r => _pendingLobby = r.m_steamIDLobby.m_SteamID);
                // "Join game" in the Steam friends list / "Invite to game" while the game is running
                _richJoin = Callback<GameRichPresenceJoinRequested_t>.Create(r =>
                {
                    if (SteamTransport.TryParseJoin(r.m_rgchConnect, out var host)) _pendingHost = host.m_SteamID;
                });
            }

            if ((_pendingLobby != 0 || _pendingHost != 0) && _menusReady && MatchSync.CanAcceptNewPlayers && Session.Instance != null
                && PendingJoinMenuReady())
            {
                OnlineMenu.Open();
                if (_pendingHost != 0) Session.Instance.JoinSteamHost(new CSteamID(_pendingHost));
                else Session.Instance.JoinSteam(new CSteamID(_pendingLobby));
                _pendingHost = 0;
                _pendingLobby = 0;
            }

            try
            {
                AutoTest();
                Debugging.AutoPilot.Tick();
                NetInput.Tick();
                UiMirror.Tick();
                LobbySync.Tick();
                MatchSync.Tick();
                RoundSync.Tick();
            }
            catch (Exception e) { Log.LogError("[Plugin] tick: " + e); }
        }

        void LateUpdate()
        {
            // after the game's own Update: puppets of a passive client are placed last
            try { MatchSync.LateTick(); }
            catch (Exception e) { Log.LogError("[Plugin] late tick: " + e); }
        }

        static void QuitPrefix()
        {
            Log.LogWarning("[Plugin] Application.Quit called from:\n" + Environment.StackTrace);
        }

        void OnApplicationQuit()
        {
            if (Diag) Log.LogInfo("[Diag] application quitting");
        }

        void OnDestroy()
        {
            Session.Instance?.Leave(silent: true);
            _harmony?.UnpatchSelf();
        }
    }
}
