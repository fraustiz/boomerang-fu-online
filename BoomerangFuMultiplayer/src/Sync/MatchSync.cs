using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BoomerangFuMultiplayer.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using Newtonsoft.Json;
using Rewired;
using UnityEngine;

namespace BoomerangFuMultiplayer.Sync
{
    /// <summary>
    /// Host-authoritative match. Every machine simulates the match with the same inputs and
    /// the same random seeds (good local prediction), and the host has the final word on what
    /// matters: deaths, power-ups, positions, pause and match start.
    /// </summary>
    public static class MatchSync
    {
        static readonly FieldInfo FPlaylist = AccessTools.Field(typeof(LevelManager), "matchPlaylist");
        static readonly FieldInfo FPowerupsThisRound = AccessTools.Field(typeof(LevelManager), "powerupsSpawnedThisRound");
        static readonly FieldInfo FKillerType = AccessTools.Field(typeof(Actor), "killerType");
        static readonly MethodInfo MCollectPowerup = AccessTools.Method(typeof(Player), "CollectPowerup");

        public static int MatchSeed;
        static List<string> _pendingPlaylist;
        static bool _runningMatchStart;
        static bool _clientWaiting;
        static StartData _received;
        static float _receivedAt;
        static bool _applyingDeath, _applyingPowerup;
        static PowerupType? _forcedPower;
        static PowerupType _lastCollected;
        static float _nextSnapshot, _nextDiag;

        public static bool CanAcceptNewPlayers
        {
            get
            {
                var gm = Game.Get<GameManager>();
                return gm == null || gm.gameState == GameManager.GameState.Menu;
            }
        }

        /// <summary>A match exists on this machine (players spawned), including round-end screens.</summary>
        public static bool MatchInProgress
        {
            get
            {
                var gm = Game.Get<GameManager>();
                return gm != null && gm.players.Count > 0;
            }
        }

        static GameManager GM => Game.Get<GameManager>();
        static UIManager UI => Game.Get<UIManager>();

        public static void Register(Session s)
        {
            s.On(Msg.TakeOver, OnTakeOver);
            s.On(Msg.AddPlayer, OnAddPlayer);
            s.On(Msg.LateJoin, OnLateJoin);
            s.On(Msg.MatchStart, OnMatchStart);
            s.On(Msg.Snapshot, OnSnapshot);
            s.On(Msg.Death, OnDeath);
            s.On(Msg.PowerupSpawn, OnPowerupSpawn);
            s.On(Msg.PowerupCollect, OnPowerupCollect);
            s.On(Msg.Pause, OnPause);
            s.On(Msg.RoundEnd, OnRoundEnd);
            s.On(Msg.Respawn, OnRespawn);
        }

        public static void Patch(Harmony h)
        {
            HarmonyMethod M(string name) => new HarmonyMethod(typeof(MatchSync), name);
            // one failing patch must not drop the others (they used to share a single try/catch)
            void Do(int index, Action patch)
            {
                try { patch(); }
                catch (Exception e) { Plugin.Log.LogError($"[MatchSync] patch #{index} failed: {e.Message}"); }
            }
            Do(1, () => h.Patch(AccessTools.Method(typeof(UIManager), nameof(UIManager.WaitForMatchStartThenClose)), prefix: M(nameof(WaitForMatchPrefix))));
            Do(2, () => h.Patch(AccessTools.Method(typeof(LevelManager), nameof(LevelManager.BuildMatchPlaylist)), postfix: M(nameof(BuildPlaylistPostfix))));
            Do(3, () => h.Patch(AccessTools.Method(typeof(LevelManager), nameof(LevelManager.SelectRandomPower)), prefix: M(nameof(SelectRandomPowerPrefix))));
            Do(4, () => h.Patch(AccessTools.Method(typeof(GameManager), "EndRound"), prefix: M(nameof(EndRoundPrefix)), postfix: M(nameof(EndRoundPostfix))));
            Do(5, () => h.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.RespawnPlayer)), prefix: M(nameof(RespawnPrefix)), postfix: M(nameof(RespawnPostfix))));
            Do(6, () => h.Patch(AccessTools.Method(typeof(GameManager), "PrepareRound"), prefix: M(nameof(PrepareRoundPrefix))));
            Do(7, () => h.Patch(AccessTools.Method(typeof(Player), nameof(Player.Die)), prefix: M(nameof(DiePrefix)), postfix: M(nameof(DiePostfix))));
            Do(8, () => h.Patch(AccessTools.Method(typeof(LevelManager), "AttemptToSpawnPowerup"), prefix: M(nameof(SpawnPowerupPrefix)), postfix: M(nameof(SpawnPowerupPostfix))));
            Do(9, () => h.Patch(MCollectPowerup, prefix: M(nameof(CollectPrefix)), postfix: M(nameof(CollectPostfix))));
            Do(10, () => h.Patch(AccessTools.Method(typeof(Powerup), nameof(Powerup.GetCollected)), postfix: M(nameof(GetCollectedPostfix))));
            Do(11, () => h.Patch(AccessTools.Method(typeof(UIManager), nameof(UIManager.Pause)), prefix: M(nameof(PausePrefix))));
            Do(12, () => h.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.OnGameOverlayActivated)), prefix: M(nameof(OverlayPrefix))));
            Do(100, () => h.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.OnGameOverlayClosed)), postfix: M(nameof(OverlayClosedPostfix))));
            Do(13, () => h.Patch(AccessTools.Method(typeof(PlayerAI), "UpdateBehaviour"), prefix: M(nameof(AIPrefix))));
            Do(14, () => h.Patch(AccessTools.Method(typeof(PlayerAI), nameof(PlayerAI.Init)), postfix: M(nameof(AIInitPostfix))));
            Do(15, () => h.Patch(AccessTools.Method(typeof(PlayerInput), "Update"), prefix: M(nameof(PlayerInputPrefix))));
            Do(16, () => h.Patch(AccessTools.Method(typeof(Player), nameof(Player.ThrowDisc)), prefix: M(nameof(ThrowDiscPrefix)), postfix: M(nameof(ThrowDiscPostfix))));
            Do(17, () => h.Patch(AccessTools.Method(typeof(Disc), nameof(Disc.Init), new[] { typeof(bool), typeof(bool), typeof(bool) }), postfix: M(nameof(DiscInitPostfix))));
            // passive clients: no local simulation, collisions or pick-ups (the host decides everything)
            Do(18, () => h.Patch(AccessTools.DeclaredMethod(typeof(Player), "FixedUpdate"), prefix: M(nameof(PlayerFixedPrefix))));
            var blocked = new HashSet<string>
            {
                "CollisionEnter2D", "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D",
                "OnDamageColliderTriggered", "OnDamageColliderStay", "OnMeleeColliderTriggered",
                "OnDisarmColliderTriggered", "OnShieldColliderTriggered", "OnShieldColliderStay",
                "OnPickupColliderTriggered", "OnPickupColliderStay",
                "CooldownStun", // stun state comes from the host
            };
            foreach (var t in new[] { typeof(Actor), typeof(Player), typeof(Projectile), typeof(Disc) })
                foreach (var m in AccessTools.GetDeclaredMethods(t))
                    if (!m.IsAbstract && (blocked.Contains(m.Name) || (m.Name == "FixedUpdate" && typeof(Projectile).IsAssignableFrom(t))))
                        Do(19, () => h.Patch(m, prefix: M(nameof(PassiveBlock))));
            foreach (var t in typeof(MovingObstacle).Assembly.GetTypes())
            {
                if (!typeof(MovingObstacle).IsAssignableFrom(t) && !typeof(MovingFloor).IsAssignableFrom(t)
                    && !typeof(MovingDoor).IsAssignableFrom(t) && t != typeof(RotatingObstacleController)) continue;
                var fu = AccessTools.GetDeclaredMethods(t).FirstOrDefault(m => m.Name == "FixedUpdate"); // inherited ones are patched on the base
                if (fu != null && !fu.IsAbstract) Do(200, () => h.Patch(fu, prefix: M(nameof(MoverFixedPrefix))));
            }
            foreach (var name in new[] { nameof(Player.ThrowDisc), nameof(Player.Attack), nameof(Player.Dash) })
            {
                var m = AccessTools.Method(typeof(Player), name, Type.EmptyTypes);
                if (m != null && m.ReturnType == typeof(bool)) h.Patch(m, postfix: M(nameof(BotActionPostfix)));
                else Plugin.Log.LogWarning($"[MatchSync] Player.{name}() not patchable for bot replication");
            }
        }

        // ── Match start ──────────────────────────────────────────────────────────

        sealed class StartData
        {
            public int Seed;
            public bool Rematch;
            public string Settings;
            public LobbySync.PlayerState[] Roster;
            public List<string> Playlist;
            public LateData Late;   // set when joining a match that is already running
        }

        sealed class LateData
        {
            public int MatchIndex, Round;
            public Scores Scores;
            public List<int> Order = new List<int>();   // host's player order (by playerID)
        }

        // ── Scores (host is authoritative, sent with every round end) ────────────

        sealed class Scores
        {
            public List<(int id, MatchScore s)> Rows = new List<(int, MatchScore)>();
            public List<int> Winners = new List<int>();
            public bool TieBreaker;
        }

        static void WriteScores(NetDataWriter w, GameManager gm)
        {
            w.Put((byte)gm.matchScores.Count);
            foreach (var kv in gm.matchScores)
            {
                w.Put(kv.Key);
                w.Put(kv.Value.Points); w.Put(kv.Value.Suicides); w.Put(kv.Value.TeamKills);
                w.Put(kv.Value.Total); w.Put(kv.Value.Rank);
            }
            w.Put((byte)gm.matchWinners.Count);
            foreach (int id in gm.matchWinners) w.Put(id);
            w.Put(Game.Get<SettingsManager>().tieBreaker);
        }

        static Scores ReadScores(NetDataReader r)
        {
            var s = new Scores();
            int n = r.GetByte();
            for (int i = 0; i < n; i++)
            {
                int id = r.GetInt();
                s.Rows.Add((id, new MatchScore
                {
                    Points = r.GetInt(), Suicides = r.GetInt(), TeamKills = r.GetInt(), Total = r.GetInt(), Rank = r.GetInt(),
                }));
            }
            int nw = r.GetByte();
            for (int i = 0; i < nw; i++) s.Winners.Add(r.GetInt());
            s.TieBreaker = r.GetBool();
            return s;
        }

        static void ApplyScores(GameManager gm, Scores s)
        {
            if (s == null) return;
            gm.matchScores.Clear();
            foreach (var (id, score) in s.Rows) gm.matchScores[id] = score;
            gm.matchWinners.Clear();
            gm.matchWinners.AddRange(s.Winners);
            Game.Get<SettingsManager>().tieBreaker = s.TieBreaker;
        }

        sealed class SettingsDto
        {
            public string MatchSettings;
            public int MatchType, MatchScoreGoal, NumBots, NumPlayers, BotDifficulty;
            public bool TeamMatch, TieBreaker, LimitSeekAttempts;
            public float GoalTimeStart, RoundDuration;
        }

        static string CaptureSettings()
        {
            var s = Game.Get<SettingsManager>();
            return JsonConvert.SerializeObject(new SettingsDto
            {
                MatchSettings = JsonConvert.SerializeObject(s.MatchSettings),
                MatchType = (int)s.matchType,
                MatchScoreGoal = s.matchScoreGoal,
                NumBots = s.numBots,
                NumPlayers = s.numPlayers,
                BotDifficulty = (int)s.botDifficulty,
                TeamMatch = s.teamMatch,
                TieBreaker = s.tieBreaker,
                LimitSeekAttempts = s.limitSeekAttempts,
                GoalTimeStart = s.goalTimeStart,
                RoundDuration = s.roundDuration,
            });
        }

        static void ApplySettings(string json)
        {
            var s = Game.Get<SettingsManager>();
            var d = JsonConvert.DeserializeObject<SettingsDto>(json);
            JsonConvert.PopulateObject(d.MatchSettings, s.MatchSettings);
            s.matchType = (SettingsManager.MatchType)d.MatchType;
            s.matchScoreGoal = d.MatchScoreGoal;
            s.numBots = d.NumBots;
            s.numPlayers = d.NumPlayers;
            s.botDifficulty = (SettingsManager.BotDifficulty)d.BotDifficulty;
            s.teamMatch = d.TeamMatch;
            s.tieBreaker = d.TieBreaker;
            s.limitSeekAttempts = d.LimitSeekAttempts;
            s.goalTimeStart = d.GoalTimeStart;
            s.roundDuration = d.RoundDuration;
        }

        static bool WaitForMatchPrefix(UIManager __instance, UIButton button, bool rematch, ref IEnumerator __result)
        {
            if (!Session.Active || _runningMatchStart) return true;

            if (Session.IsHost)
            {
                MatchSeed = new System.Random().Next(1, int.MaxValue);
                var data = new StartData { Seed = MatchSeed, Rematch = rematch, Settings = CaptureSettings() };
                var lobby = __instance.GetMenu(UIMenuBase.MenuType.Lobby) as UIMenuLobby;
                data.Roster = lobby != null ? LobbySync.Capture(lobby) : new LobbySync.PlayerState[0];
                if (!rematch)
                {
                    // Build the playlist now (seeded) so it can be shipped to clients.
                    _pendingPlaylist = null;
                    UnityEngine.Random.InitState(MatchSeed);
                    var lm = Game.Get<LevelManager>();
                    lm.BuildMatchPlaylist(false);
                    data.Playlist = ((IEnumerable<LevelAsset>)FPlaylist.GetValue(lm)).Select(l => l.name).ToList();
                    _pendingPlaylist = data.Playlist;
                }
                var w = Protocol.Begin(Msg.MatchStart);
                w.Put(data.Seed); w.Put(data.Rematch); w.Put(data.Settings);
                LobbySync.Write(w, data.Roster);
                w.Put(data.Playlist?.Count ?? -1);
                if (data.Playlist != null) foreach (var n in data.Playlist) w.Put(n);
                BroadcastToPlaying(w, true);
                Plugin.Log.LogInfo($"[MatchSync] match start sent (seed {MatchSeed}, rematch {rematch})");
                if (Plugin.Diag) Plugin.Log.LogInfo($"[Diag] playlist: {(data.Playlist != null ? string.Join(",", data.Playlist) : "(rematch)")}");
                return true;
            }

            // Client: never start on our own, wait for the host's data.
            __result = ClientWait(__instance, button, rematch);
            return false;
        }

        static IEnumerator ClientWait(UIManager ui, UIButton button, bool rematch)
        {
            _clientWaiting = true;
            float until = Time.unscaledTime + 20f;
            while (_received == null && Time.unscaledTime < until && Session.IsClient) yield return null;
            _clientWaiting = false;
            if (_received == null)
            {
                Session.Instance?.Notify("L'hôte n'a pas lancé la partie");
                ui.eventSystem.sendNavigationEvents = true;
                yield break;
            }
            yield return ui.StartCoroutine(RunReceived(ui, button));
        }

        static IEnumerator RunReceived(UIManager ui, UIButton button)
        {
            var d = _received;
            _received = null;
            try
            {
                ApplySettings(d.Settings);
                if (!d.Rematch) LobbySync.ApplyNow(d.Roster);
                MatchSeed = d.Seed;
                _pendingPlaylist = d.Rematch ? null : d.Playlist;
                _pendingLate = d.Late;
            }
            catch (Exception e) { Plugin.Log.LogError("[MatchSync] apply start data: " + e); }

            IEnumerator run;
            _runningMatchStart = true;
            try { run = ui.WaitForMatchStartThenClose(button, d.Rematch); }
            finally { _runningMatchStart = false; }
            yield return ui.StartCoroutine(run);
        }

        static void OnMatchStart(int peer, NetDataReader r)
        {
            if (!Session.IsClient) return;
            var d = new StartData { Seed = r.GetInt(), Rematch = r.GetBool(), Settings = r.GetString() };
            d.Roster = LobbySync.Read(r);
            int n = r.GetInt();
            if (n >= 0) { d.Playlist = new List<string>(n); for (int i = 0; i < n; i++) d.Playlist.Add(r.GetString()); }
            if (d.Rematch && !MatchInProgress)
            {
                // still waiting to enter (joined during the previous match): wait for the lobby
                Plugin.Log.LogInfo("[MatchSync] rematch ignored: not in the match yet");
                return;
            }
            _received = d;
            _receivedAt = Time.unscaledTime;
            if (Plugin.Diag) Plugin.Log.LogInfo($"[Diag] playlist: {(d.Playlist != null ? string.Join(",", d.Playlist) : "(rematch)")}");
            Plugin.Log.LogInfo($"[MatchSync] match start received (seed {d.Seed}, rematch {d.Rematch})");
        }

        /// <summary>
        /// Let the game build its playlist (it also resets the level index and the power-up pool),
        /// then replace the level order with the host's.
        /// </summary>
        static void BuildPlaylistPostfix(LevelManager __instance)
        {
            if (!Session.Active || _pendingPlaylist == null) return;
            var mapped = new List<LevelAsset>();
            foreach (var name in _pendingPlaylist)
            {
                var asset = __instance.levelAssets.FirstOrDefault(a => a != null && a.name == name);
                if (asset != null) mapped.Add(asset);
                else Plugin.Log.LogWarning($"[MatchSync] level '{name}' missing here (DLC ?)");
            }
            _pendingPlaylist = null;
            if (mapped.Count > 0)
            {
                var list = (List<LevelAsset>)FPlaylist.GetValue(__instance);
                list.Clear();
                list.AddRange(mapped);
            }

            // Joining a running match: resume at the host's next level, round number and scores.
            // (StartMatch has already run InitMatchScores, which reset them.)
            var late = _pendingLate;
            _pendingLate = null;
            var gm = GM;
            if (late != null && gm != null)
            {
                __instance.matchIndex = late.MatchIndex;
                gm.roundNumber = late.Round;
                ApplyScores(gm, late.Scores);
                if (late.Order.Count == gm.players.Count)
                {
                    // same order as the host (players added mid-match are at the end there)
                    var sorted = gm.players.OrderBy(p => { int i = late.Order.IndexOf(p.playerID); return i < 0 ? 99 : i; }).ToList();
                    gm.players.Clear();
                    gm.players.AddRange(sorted);
                }
                if (Plugin.Diag) Plugin.Log.LogInfo($"[Diag] late join applied: level index {late.MatchIndex}, round {late.Round}, scores {late.Scores.Rows.Count}");
            }
        }

        static LateData _pendingLate;

        static bool SelectRandomPowerPrefix(ref PowerupType __result)
        {
            if (!_forcedPower.HasValue) return true;
            __result = _forcedPower.Value;
            return false;
        }

        // ── Round end / respawn (host decides) ───────────────────────────────────

        static bool EndRoundPrefix(GameManager __instance, bool transitionWipe)
        {
            if (Session.IsClient) return Session.InHostScope;
            if (Session.IsHost)
            {
                // Scores as they are before EndRound: the client then runs the same EndRound on them.
                var w = Protocol.Begin(Msg.RoundEnd);
                w.Put(__instance.roundNumber);
                w.Put(transitionWipe);
                WriteScores(w, __instance);
                BroadcastToPlaying(w, true);
                if (Plugin.Diag) Plugin.Log.LogInfo($"[Diag] round end sent (round {__instance.roundNumber}, winners {__instance.matchWinners.Count})");
            }
            return true;
        }

        static void EndRoundPostfix(GameManager __instance)
        {
            if (Session.IsHost) ProcessLateJoiners(__instance);
        }

        static readonly MethodInfo MEndRound = AccessTools.Method(typeof(GameManager), "EndRound");

        static void OnRoundEnd(int peer, NetDataReader r)
        {
            int round = r.GetInt();
            bool wipe = r.GetBool();
            var scores = ReadScores(r);
            var gm = GM;
            if (gm == null) return;
            if (Plugin.Diag) Plugin.Log.LogInfo($"[Diag] round end received (host round {round}, local round {gm.roundNumber}, state {gm.gameState}, winners {scores.Winners.Count})");
            if (gm.gameState == GameManager.GameState.Menu || gm.gameState == GameManager.GameState.Loading) return;
            // The scoreboard plays the last seconds filmed by the camera. The host stops filming right after
            // the last kill; a client may still be filming when the round end arrives, and would then keep
            // only the transition (a plain grey background behind the scores).
            try { gm.PauseRecording(); } catch (Exception e) { Plugin.Log.LogWarning("[MatchSync] pause replay: " + e.Message); }
            ApplyScores(gm, scores);
            using (Session.HostScope())
            {
                try { MEndRound.Invoke(gm, new object[] { wipe }); }
                catch (Exception e) { Plugin.Log.LogError("[MatchSync] apply round end: " + e); }
            }
        }

        /// <summary>Host: send to every client already playing (not to late joiners still waiting).</summary>
        internal static void BroadcastToPlaying(NetDataWriter w, bool reliable)
        {
            var s = Session.Instance;
            for (int i = 1; i < s.Slots.Length; i++)
                if (s.Slots[i].Used && !s.Slots[i].Pending) s.Transport.SendToPeer(s.Slots[i].Peer, w, reliable);
        }

        // ── Joining a running match ──────────────────────────────────────────────
        // Like the game's own drop-in: the newcomer replaces the bot of its slot at the next
        // round end. Without a bot to replace, the newcomer plays from the next match.

        static readonly MethodInfo MTakeOverAI = AccessTools.Method(typeof(GameManager), "TakeOverAI");

        public static bool SlotHasPlayer(int slot) => GM != null && GM.players.Any(p => p != null && p.playerID == slot);

        static Player BotInSlot(GameManager gm, int slot) =>
            gm.players.FirstOrDefault(p => p != null && p.playerID == slot && p.playerType == PlayerType.Bot && p.GetComponent<PlayerAI>() != null);

        static readonly MethodInfo MCreatePlayer = AccessTools.Method(typeof(GameManager), "CreatePlayer");

        /// <summary>Hide-and-seek rotates the seeker over a fixed player list: newcomers wait for the next match.</summary>
        static bool CanAddPlayers => Game.Get<SettingsManager>().matchType != SettingsManager.MatchType.HideAndSeek;

        public static string LateJoinNote(int slot)
        {
            var gm = GM;
            if (gm != null && BotInSlot(gm, slot) != null && !Plugin.ForceAddJoiners)
                return "Partie en cours : tu remplaces un bot à la fin de la manche.";
            if (CanAddPlayers)
                return "Partie en cours : tu entres dans le match à la fin de la manche.";
            return "Partie en cours (cache-cache) : tu joueras à la prochaine partie.";
        }

        static void ProcessLateJoiners(GameManager gm)
        {
            var s = Session.Instance;
            if (gm.matchWinners.Count > 0) return; // match is ending: join the next one from the lobby
            for (int slot = 1; slot < s.Slots.Length; slot++)
            {
                var info = s.Slots[slot];
                if (!info.Used || !info.Pending) continue;

                if (BotInSlot(gm, slot) != null && !Plugin.ForceAddJoiners)
                {
                    TakeOverLocally(gm, slot);
                    var t = Protocol.Begin(Msg.TakeOver);
                    t.Put((byte)slot);
                    BroadcastToPlaying(t, true);
                    s.Notify($"{info.Name} entre dans la partie à la place d'un bot");
                }
                else if (gm.players.Any(p => p != null && p.playerID == slot && p.playerType == PlayerType.Human))
                {
                    // the same slot reconnects while its character is still in the match (it dropped out
                    // mid-match): nothing to create, its new inputs drive that character again
                    s.Notify($"{info.Name} revient dans la partie");
                }
                else if (CanAddPlayers && gm.players.All(p => p == null || p.playerID != slot))
                {
                    var state = PickNewPlayer(gm, slot);
                    if (state == null || !AddPlayerLocally(gm, slot, state.Value)) continue;
                    var a = Protocol.Begin(Msg.AddPlayer);
                    a.Put((byte)slot);
                    LobbySync.Write(a, new[] { state.Value });
                    BroadcastToPlaying(a, true);
                    s.Notify($"{info.Name} entre dans la partie");
                }
                else continue;

                SendLateJoin(gm, info);
                info.Pending = false;
                RoundSync.LateLoadingUntil = Time.unscaledTime + 30f;
            }
        }

        static readonly FieldInfo FCellCharacter = AccessTools.Field(typeof(UICharacterGridCell), "character");

        /// <summary>
        /// The lobby screen may no longer describe the match (a player who dropped out mid-match has his lobby
        /// slot reset, yet his character is still playing): the late joiner gets the match's real players.
        /// </summary>
        static void RosterFromMatch(GameManager gm, UIMenuLobby lobby, LobbySync.PlayerState[] roster)
        {
            foreach (var p in gm.players)
            {
                if (p == null || p.character == null || p.playerID < 0 || p.playerID >= roster.Length) continue;
                var type = p.character.characterType;
                ref var st = ref roster[p.playerID];
                st.Type = (byte)p.playerType;
                st.Ready = (byte)UILobbyPlayer.ReadyState.Ready;
                st.Character = (int)type;
                st.Hat = (int)p.character.equippedHat;
                if (lobby != null)
                {
                    var cell = lobby.characterGridCells.FirstOrDefault(c => c != null &&
                        ((FCellCharacter?.GetValue(c) as Character)?.characterType == type || (c.altCharacter != null && c.altCharacter.characterType == type)));
                    if (cell != null) st.Cell = (sbyte)cell.index;
                }
            }
        }

        static void SendLateJoin(GameManager gm, Session.SlotInfo info)
        {
            var lm = Game.Get<LevelManager>();
            var lobby = UI?.GetMenu(UIMenuBase.MenuType.Lobby) as UIMenuLobby;
            var w = Protocol.Begin(Msg.LateJoin);
            w.Put(MatchSeed);
            w.Put(CaptureSettings());
            var roster = lobby != null ? LobbySync.Capture(lobby) : new LobbySync.PlayerState[0];
            RosterFromMatch(gm, lobby, roster);
            LobbySync.Write(w, roster);
            var playlist = ((IEnumerable<LevelAsset>)FPlaylist.GetValue(lm)).Select(l => l.name).ToList();
            w.Put(playlist.Count);
            foreach (var n in playlist) w.Put(n);
            w.Put(lm.matchIndex);
            w.Put(gm.roundNumber);
            WriteScores(w, gm);
            // player order matters (spawn points, HUD bars): send the host's
            var order = gm.players.Where(p => p != null).Select(p => p.playerID).ToList();
            w.Put((byte)order.Count);
            foreach (int id in order) w.Put((byte)id);
            Session.Instance.Transport.SendToPeer(info.Peer, w, true);
            Plugin.Log.LogInfo($"[MatchSync] late join sent to {info.Name} (level index {lm.matchIndex}, round {gm.roundNumber}, players {order.Count})");
        }

        /// <summary>Host: choose a free character (and team) for a newcomer.</summary>
        static LobbySync.PlayerState? PickNewPlayer(GameManager gm, int slot)
        {
            var lobby = UI?.GetMenu(UIMenuBase.MenuType.Lobby) as UIMenuLobby;
            if (lobby == null) return null;
            var cell = lobby.GetRandomAvailableGridCell();
            if (cell == null) { Plugin.Log.LogWarning("[MatchSync] no free character for the newcomer"); return null; }
            byte team = 0;
            if (Game.Get<SettingsManager>().teamMatch)
            {
                int t1 = gm.players.Count(p => p != null && p.team == 1), t2 = gm.players.Count(p => p != null && p.team == 2);
                team = (byte)(t1 <= t2 ? 1 : 2);
            }
            return new LobbySync.PlayerState
            {
                Ready = (byte)UILobbyPlayer.ReadyState.Ready,
                Type = (byte)PlayerType.Human,
                Team = team,
                Cell = (sbyte)cell.index,
                Character = cell.currentCharacter != null ? (int)cell.currentCharacter.characterType : -1,
                Hat = -1,
            };
        }

        /// <summary>Create a player for <paramref name="slot"/> in the running match (all machines do the same).</summary>
        static bool AddPlayerLocally(GameManager gm, int slot, LobbySync.PlayerState state)
        {
            var lobby = UI?.GetMenu(UIMenuBase.MenuType.Lobby) as UIMenuLobby;
            if (lobby == null || slot >= lobby.uiPlayers.Length) return false;
            LobbySync.ForceSlot(lobby, slot, state);
            var lp = lobby.uiPlayers[slot];
            if (lp.readyState != UILobbyPlayer.ReadyState.Ready || lp.character == null)
            {
                Plugin.Log.LogWarning($"[MatchSync] could not prepare a player for slot {slot}");
                return false;
            }
            try { MCreatePlayer.Invoke(gm, new object[] { lp }); }
            catch (Exception e) { Plugin.Log.LogError("[MatchSync] create player: " + e); return false; }
            var settings = Game.Get<SettingsManager>();
            if (!settings.teamMatch && !gm.matchScores.ContainsKey(slot)) gm.matchScores[slot] = new MatchScore();
            settings.numPlayers++;
            Plugin.Log.LogInfo($"[MatchSync] player added for slot {slot} ({lp.character.characterType}), {gm.players.Count} players");
            return true;
        }

        static void OnAddPlayer(int peer, NetDataReader r)
        {
            int slot = r.GetByte();
            var states = LobbySync.Read(r);
            var gm = GM;
            if (gm == null || states.Length == 0 || !MatchInProgress) return;
            if (gm.players.Any(p => p != null && p.playerID == slot)) return;
            AddPlayerLocally(gm, slot, states[0]);
        }

        static void TakeOverLocally(GameManager gm, int slot)
        {
            var bot = BotInSlot(gm, slot);
            if (bot == null) return;
            var lobby = UI?.GetMenu(UIMenuBase.MenuType.Lobby) as UIMenuLobby;
            if (lobby != null && slot < lobby.uiPlayers.Length) lobby.uiPlayers[slot].playerType = PlayerType.Human;
            var routine = (IEnumerator)MTakeOverAI.Invoke(gm, new object[] { bot.GetComponent<PlayerAI>(), ReInput.players.GetPlayer(slot) });
            gm.StartCoroutine(routine);
        }

        static void OnTakeOver(int peer, NetDataReader r)
        {
            int slot = r.GetByte();
            var gm = GM;
            if (gm == null) return;
            if (Plugin.Diag) Plugin.Log.LogInfo($"[Diag] take over: slot {slot}");
            TakeOverLocally(gm, slot);
        }

        static void OnLateJoin(int peer, NetDataReader r)
        {
            if (!Session.IsClient) return;
            var d = new StartData { Seed = r.GetInt(), Rematch = false, Settings = r.GetString() };
            d.Roster = LobbySync.Read(r);
            int n = r.GetInt();
            d.Playlist = new List<string>(n);
            for (int i = 0; i < n; i++) d.Playlist.Add(r.GetString());
            d.Late = new LateData { MatchIndex = r.GetInt(), Round = r.GetInt(), Scores = ReadScores(r) };
            int no = r.GetByte();
            for (int i = 0; i < no; i++) d.Late.Order.Add(r.GetByte());
            _received = d;
            _receivedAt = Time.unscaledTime;
            Session.Instance.Notify("C'est parti, tu entres dans la partie !");
            Plugin.Log.LogInfo($"[MatchSync] late join received (level index {d.Late.MatchIndex}, round {d.Late.Round})");
        }

        static int _hostDieDepth;
        static readonly List<(int id, float delay)> _deferredRespawns = new List<(int, float)>();

        static bool RespawnPrefix() => !(Session.IsClient && !Session.InHostScope);

        static void RespawnPostfix(Player candidate, float delay)
        {
            if (!Session.IsHost || candidate == null) return;
            if (_hostDieDepth > 0) { _deferredRespawns.Add((candidate.playerID, delay)); return; }
            SendRespawn(candidate.playerID, delay);
        }

        static void SendRespawn(int id, float delay)
        {
            var w = Protocol.Begin(Msg.Respawn);
            w.Put((byte)id);
            w.Put(delay);
            BroadcastToPlaying(w, true);
            if (Plugin.Diag) Plugin.Log.LogInfo($"[Diag] respawn sent: player {id}");
        }

        static void OnRespawn(int peer, NetDataReader r)
        {
            int id = r.GetByte();
            float delay = r.GetFloat();
            var gm = GM;
            var p = gm?.players.FirstOrDefault(x => x != null && x.playerID == id);
            if (p == null) return;
            using (Session.HostScope()) gm.RespawnPlayer(p, delay);
        }

        static void PrepareRoundPrefix(GameManager __instance)
        {
            if (Session.Active) UnityEngine.Random.InitState(MatchSeed ^ ((__instance.roundNumber + 1) * 7919));
        }

        // ── Deaths ───────────────────────────────────────────────────────────────

        static bool DiePrefix(Player __instance, out bool __state)
        {
            // "not dead yet" (Alive or Falling): a fall ends with Die(fall) while already Falling
            __state = __instance.actorState != Actor.ActorState.Dead;
            if (Session.IsHost) _hostDieDepth++;
            return !(Session.IsClient && !_applyingDeath);
        }

        static void DiePostfix(Player __instance, Actor.DeathType deathType, bool __state)
        {
            if (!Session.IsHost) return;
            _hostDieDepth = Math.Max(0, _hostDieDepth - 1);
            try { SendDeath(__instance, deathType, __state); }
            finally
            {
                // respawns decided inside Die are sent after the death itself
                if (_hostDieDepth == 0 && _deferredRespawns.Count > 0)
                {
                    foreach (var (id, delay) in _deferredRespawns) SendRespawn(id, delay);
                    _deferredRespawns.Clear();
                }
            }
        }

        static void SendDeath(Player __instance, Actor.DeathType deathType, bool __state)
        {
            if (!__state || __instance.actorState != Actor.ActorState.Dead) return;
            var killer = __instance.killer as Player;
            var w = Protocol.Begin(Msg.Death);
            w.Put((byte)__instance.playerID);
            w.Put((int)deathType);
            w.Put(killer != null ? killer.playerID : -1);
            w.Put(FKillerType != null ? Convert.ToInt32(FKillerType.GetValue(__instance)) : 0);
            BroadcastToPlaying(w, true);
            if (Plugin.Diag) Plugin.Log.LogInfo($"[Diag] death sent: player {__instance.playerID} ({deathType}) killer {(killer != null ? killer.playerID : -1)} round {GM?.roundNumber}");
        }

        static void OnDeath(int peer, NetDataReader r)
        {
            int id = r.GetByte();
            var type = (Actor.DeathType)r.GetInt();
            int killerId = r.GetInt();
            var killerType = (Actor.KillerType)r.GetInt();
            var gm = GM;
            if (gm == null) return;
            var victim = gm.players.FirstOrDefault(p => p != null && p.playerID == id);
            if (Plugin.Diag) Plugin.Log.LogInfo($"[Diag] death received: player {id} ({type}) killer {killerId}, local state {(victim != null ? victim.actorState.ToString() : "missing")}, game {gm.gameState} round {gm.roundNumber}");
            if (victim == null || victim.actorState == Actor.ActorState.Dead) return;
            var killer = gm.players.FirstOrDefault(p => p != null && p.playerID == killerId);
            _applyingDeath = true;
            try
            {
                if (killer != null) victim.SetKiller(killer, killerType, 1f);
                victim.Die(type);
            }
            catch (Exception e) { Plugin.Log.LogError("[MatchSync] apply death: " + e); }
            finally { _applyingDeath = false; }
        }

        // ── Power-ups ────────────────────────────────────────────────────────────

        static bool SpawnPowerupPrefix(out int __state)
        {
            __state = GM != null ? GM.spawnedPowerups.Count : 0;
            return !Session.IsClient;
        }

        static void SpawnPowerupPostfix(int __state)
        {
            var gm = GM;
            if (!Session.IsHost || gm == null || gm.spawnedPowerups.Count <= __state) return;
            var go = gm.spawnedPowerups[gm.spawnedPowerups.Count - 1];
            if (go == null) return;
            var p = go.transform.position;
            var w = Protocol.Begin(Msg.PowerupSpawn);
            w.Put(p.x); w.Put(p.y); w.Put(p.z);
            BroadcastToPlaying(w, true);
        }

        static void OnPowerupSpawn(int peer, NetDataReader r)
        {
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            var lm = Game.Get<LevelManager>();
            var gm = GM;
            if (lm == null || gm == null || lm.powerupPrefab == null || !MatchInProgress) return;
            var go = UnityEngine.Object.Instantiate(lm.powerupPrefab, pos, Quaternion.identity);
            var pu = go.GetComponent<Powerup>();
            if (pu != null && pu.interpolatedTransform != null) pu.interpolatedTransform.ForgetPreviousTransforms();
            gm.spawnedPowerups.Add(go);
            if (FPowerupsThisRound != null) FPowerupsThisRound.SetValue(lm, (int)FPowerupsThisRound.GetValue(lm) + 1);
        }

        static bool CollectPrefix(Player __instance, Powerup powerup, out int __state)
        {
            __state = GM != null && powerup != null ? GM.spawnedPowerups.IndexOf(powerup.gameObject) : -1;
            return !(Session.IsClient && !_applyingPowerup);
        }

        static void CollectPostfix(Player __instance, Powerup powerup, int __state)
        {
            if (!Session.IsHost) return;
            var pos = powerup != null ? powerup.transform.position : __instance.transform.position;
            var w = Protocol.Begin(Msg.PowerupCollect);
            w.Put((byte)__instance.playerID);
            w.Put(__state);
            w.Put((int)_lastCollected);
            w.Put(pos.x); w.Put(pos.y);
            BroadcastToPlaying(w, true);
        }

        static void GetCollectedPostfix(ref PowerupType __result)
        {
            if (_forcedPower.HasValue) __result = _forcedPower.Value;
            _lastCollected = __result;
        }

        static void OnPowerupCollect(int peer, NetDataReader r)
        {
            int id = r.GetByte();
            int index = r.GetInt();
            var type = (PowerupType)r.GetInt();
            var pos = new Vector2(r.GetFloat(), r.GetFloat());
            var gm = GM;
            if (gm == null) return;
            var player = gm.players.FirstOrDefault(p => p != null && p.playerID == id);
            if (player == null) return;

            Powerup box = null;
            if (index >= 0 && index < gm.spawnedPowerups.Count && gm.spawnedPowerups[index] != null)
                box = gm.spawnedPowerups[index].GetComponent<Powerup>();
            if (box == null)
            {
                float best = 6f;
                foreach (var go in gm.spawnedPowerups)
                {
                    if (go == null) continue;
                    float d = Vector2.Distance(go.transform.position, pos);
                    if (d < best && go.GetComponent<Powerup>() is Powerup c) { best = d; box = c; }
                }
            }

            _applyingPowerup = true;
            _forcedPower = type;
            try
            {
                if (box != null) MCollectPowerup.Invoke(player, new object[] { box });
                else player.StartPowerup(type);
            }
            catch (Exception e) { Plugin.Log.LogError("[MatchSync] apply powerup: " + e); }
            finally { _applyingPowerup = false; _forcedPower = null; }
        }

        // ── Pause ────────────────────────────────────────────────────────────────

        static bool PausePrefix(Rewired.Player drivingPlayer)
        {
            if (!Session.Active) return true;
            if (Session.IsClient) return Session.InHostScope;
            var w = Protocol.Begin(Msg.Pause);
            w.Put(drivingPlayer != null ? drivingPlayer.id : -1);
            BroadcastToPlaying(w, true);
            return true;
        }

        /// <summary>Online: opening the Steam overlay (invites, chat) must not pause everybody.</summary>
        static bool OverlayPrefix()
        {
            if (!Session.Active) return true;
            // the menus must not move behind the overlay (they work again once it closes, below)
            var ui = UI;
            if (ui != null && ui.eventSystem != null) ui.eventSystem.sendNavigationEvents = false;
            return false;
        }

        static void OverlayClosedPostfix()
        {
            var ui = UI;
            if (ui != null && ui.eventSystem != null && !ui.eventSystem.sendNavigationEvents) ui.eventSystem.sendNavigationEvents = true;
        }

        static void OnPause(int peer, NetDataReader r)
        {
            int slot = r.GetInt();
            var gm = GM;
            if (gm == null || gm.gameState == GameManager.GameState.Paused || gm.gameState == GameManager.GameState.Menu) return;
            using (Session.HostScope())
                UI?.Pause(slot >= 0 && slot < Protocol.MaxSlots ? ReInput.players.GetPlayer(slot) : null);
        }

        // ── Passive clients ──────────────────────────────────────────────────────
        // The host is the only simulation. Clients send their inputs (NetInput) and display the
        // host's state: players and boomerangs are puppets (no local physics, collisions, hits or
        // actions), positions are interpolated between host snapshots, actions are replayed only
        // for their visuals. Nothing can disagree with the host any more.

        /// <summary>This machine is a client inside a running match.</summary>
        public static bool PassiveClient => Session.IsClient && MatchInProgress;

        static bool _replaying;               // running game code on purpose (visual replay, puppet spawn)
        static bool AIPrefix() => !Session.IsClient;

        static void AIInitPostfix(PlayerAI __instance)
        {
            if (!Session.IsClient) return;
            __instance.StopAllCoroutines();
            __instance.enabled = false;
        }

        static void ThrowDiscPostfix(Player __instance, bool __result)
        {
            if (Plugin.Diag && Session.IsHost && __result)
                Plugin.Log.LogInfo($"[Diag] throw: player {__instance.playerID}{(__instance.playerType == PlayerType.Bot ? " (bot)" : "")} discsLeft={__instance.HasDiscs} round {GM?.roundNumber}");
        }

        /// <summary>Client: inputs never trigger actions locally (they go to the host).</summary>
        static bool PlayerInputPrefix(PlayerInput __instance)
        {
            if (!PassiveClient) return true;
            var ai = __instance.player != null ? __instance.player.GetComponent<PlayerAI>() : null;
            if (ai != null && ai.enabled) { ai.StopAllCoroutines(); ai.enabled = false; }
            return false;
        }

        /// <summary>Client: skip local simulation / collision handling of puppets.</summary>
        static bool PassiveBlock() => !PassiveClient || _replaying;

        /// <summary>Client: no local movement, except bodies sliding once dead (purely visual).</summary>
        static bool PlayerFixedPrefix(Player __instance) =>
            !PassiveClient || _replaying || __instance.actorState == Actor.ActorState.Dead;

        /// <summary>Client: boomerangs only appear when the host says so.</summary>
        static bool ThrowDiscPrefix() => !PassiveClient || _replaying;

        // Host: one-shot actions seen since the last snapshot (1 dash, 2 attack, 4 throw)
        static readonly byte[] ActionLatch = new byte[8];

        static void BotActionPostfix(Player __instance, bool __result, MethodBase __originalMethod)
        {
            if (!Session.IsHost || !__result) return;
            int id = __instance.playerID;
            if (id < 0 || id >= ActionLatch.Length) return;
            ActionLatch[id] |= __originalMethod.Name == nameof(Player.Dash) ? (byte)1
                : __originalMethod.Name == nameof(Player.Attack) ? (byte)2 : (byte)4;
        }

        // Host: network ids of boomerangs (a pooled instance gets a new id every Init)
        static readonly Dictionary<Disc, int> DiscIds = new Dictionary<Disc, int>();
        static int _nextDiscId = 1;

        static void DiscInitPostfix(Disc __instance)
        {
            if (Session.IsHost) DiscIds[__instance] = _nextDiscId++;
        }

        static int DiscId(Disc d)
        {
            if (!DiscIds.TryGetValue(d, out int id)) { id = _nextDiscId++; DiscIds[d] = id; }
            return id;
        }

        static readonly MethodInfo MStartFall = AccessTools.Method(typeof(Actor), "StartFall");

        // ── Snapshots ────────────────────────────────────────────────────────────

        struct PlayerSnap
        {
            public int Id; public byte State, Flags, Discs, Actions;   // Flags: 1 aiming, 2 dashing, 4 stunned
            public Vector2 Pos, Vel; public Quaternion Rot;
        }

        struct DiscSnap
        {
            public int NetId; public byte Owner, Movement, Kind;       // Kind: 1 golden
            public Vector2 Pos, Vel;
        }

        sealed class SnapshotData
        {
            public int Round; public byte GameState; public float RoundTimer, HostTime, ReceivedAt;
            public PlayerSnap[] Players;
            public DiscSnap[] Discs;
            public MoverSnap[] Movers;
        }

        struct MoverSnap { public Vector3 Pos; public Quaternion Rot; }

        // ── Moving obstacles (walls, floors, doors) ──────────────────────────────
        // They advance by themselves on every machine, with random durations and directions and,
        // for some, Time.time: host and client drift apart within seconds. The host now sends their
        // transforms with every snapshot; clients stop simulating them and place them like players.

        sealed class Mover { public Transform T; public string Key; }
        static List<Mover> _movers;
        static int _moversLevel;
        static readonly FieldInfo FObstacleT = AccessTools.Field(typeof(MovingObstacle), "obstacleTransform");
        static readonly FieldInfo FFloorT = AccessTools.Field(typeof(MovingFloor), "tr");
        static readonly FieldInfo FDoorT = AccessTools.Field(typeof(MovingDoor), "obstacleTransform");
        const int MaxMovers = 64;

        /// <summary>Moving obstacles of the current level, in an order both machines agree on (scene path).</summary>
        static List<Mover> Movers()
        {
            var lm = Game.Get<LevelManager>();
            int level = lm != null && lm.level != null ? lm.level.GetInstanceID() : 0;
            if (_movers != null && level == _moversLevel && _movers.TrueForAll(m => m.T != null)) return _movers;
            var list = new List<Mover>();
            void Add(Component c, FieldInfo f)
            {
                var t = (f?.GetValue(c) as Transform) ?? c.transform;
                list.Add(new Mover { T = t, Key = c.GetType().Name + ":" + ScenePath(c.transform) });
            }
            // the level's own active obstacles, as Level.GetAllSwitchesAndMovingObstacles lists them
            // (a scene-wide search also catches other loaded levels during transitions)
            if (lm != null && lm.level != null)
            {
                var root = lm.level.gameObject;
                foreach (var c in root.GetComponentsInChildren<MovingObstacle>()) Add(c, FObstacleT);
                foreach (var c in root.GetComponentsInChildren<MovingFloor>()) Add(c, FFloorT);
                foreach (var c in root.GetComponentsInChildren<MovingDoor>()) Add(c, FDoorT);
            }
            list.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            if (list.Count > MaxMovers) list.RemoveRange(MaxMovers, list.Count - MaxMovers);
            _movers = list;
            _moversLevel = level;
            if (Plugin.Diag) Plugin.Log.LogInfo($"[Diag] moving obstacles: {list.Count} in level {lm?.level?.name}");
            return list;
        }

        static string ScenePath(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Add(t.name + "#" + t.GetSiblingIndex());
            parts.Reverse();
            return string.Join("/", parts);
        }

        static void RenderMovers(SnapshotData a, SnapshotData b, float k)
        {
            if (b.Movers == null || b.Movers.Length == 0) return;
            var movers = Movers();
            if (movers.Count != b.Movers.Length)
            {
                if (Plugin.Diag && Time.unscaledTime > _nextMoverWarn)
                {
                    _nextMoverWarn = Time.unscaledTime + 5f;
                    Plugin.Log.LogWarning($"[Diag] moving obstacles differ: host {b.Movers.Length}, here {movers.Count}");
                }
                if (Time.unscaledTime > _nextMoverRescan) { _nextMoverRescan = Time.unscaledTime + 1f; _movers = null; } // built too early: list again
                return;
            }
            bool blend = a.Movers != null && a.Movers.Length == b.Movers.Length;
            for (int i = 0; i < movers.Count; i++)
            {
                var t = movers[i].T;
                if (t == null) continue;
                var mb = b.Movers[i];
                var ma = blend ? a.Movers[i] : mb;
                t.position = Vector3.Lerp(ma.Pos, mb.Pos, k);
                t.rotation = Quaternion.Slerp(ma.Rot, mb.Rot, k);
            }
        }
        static float _nextMoverWarn, _nextMoverRescan;

        /// <summary>Client: moving obstacles only move where the host says.</summary>
        static bool MoverFixedPrefix() => !PassiveClient;

        const int MaxDiscs = 40;
        const float InterpDelay = 0.075f;       // render slightly in the past: always two snapshots to blend
        static readonly List<SnapshotData> _snaps = new List<SnapshotData>();
        static float _clockOffset = float.NaN;  // client time - host time (smallest seen delay)
        static readonly Dictionary<int, Disc> _puppets = new Dictionary<int, Disc>();
        static readonly HashSet<Disc> _puppetSet = new HashSet<Disc>();
        static float _nextPassiveDiag;

        static bool InMatch(GameManager gm) =>
            gm != null && (gm.gameState == GameManager.GameState.Playing
                           || gm.gameState == GameManager.GameState.TransitionIn
                           || gm.gameState == GameManager.GameState.TransitionOut);

        public static void Tick()
        {
            if (Session.IsClient)
            {
                // Must run in Update: FixedUpdate does not tick while menus pause time (timeScale 0).
                if (_received != null && !_clientWaiting && Time.unscaledTime - _receivedAt > 0.3f)
                {
                    // The host started (or restarted) a match but no mirrored click started it here.
                    var ui = UI;
                    if (ui != null)
                    {
                        if (Plugin.Diag) Plugin.Log.LogInfo("[Diag] starting match from host data (no local waiter)");
                        ui.eventSystem.sendNavigationEvents = false;
                        if (!_received.Rematch) Game.Get<AudioManager>()?.StopMenuMusic();
                        ui.StartCoroutine(RunReceived(ui, null));
                    }
                }
                return;
            }
            if (!Session.IsHost) return;
            var gm = GM;
            if (!MatchInProgress)
            {
                // back in the menus: late joiners waiting for the next match are now regular players
                foreach (var slot in Session.Instance.Slots) slot.Pending = false;
                DiscIds.Clear();
            }
            if (!InMatch(gm) || Time.unscaledTime < _nextSnapshot) return;
            _nextSnapshot = Time.unscaledTime + 1f / 30f;

            var w = Protocol.Begin(Msg.Snapshot);
            w.Put(gm.roundNumber); w.Put((byte)gm.gameState); w.Put(gm.roundTimer); w.Put(Time.unscaledTime);
            var list = gm.players.Where(p => p != null).ToList();
            w.Put((byte)list.Count);
            foreach (var p in list)
            {
                var t = p.transform;
                byte flags = 0;
                if (p.IsAiming) flags |= 1;
                if (p.IsDashing) flags |= 2;
                if (p.IsStunned) flags |= 4;
                byte actions = 0;
                if (p.playerID >= 0 && p.playerID < ActionLatch.Length) { actions = ActionLatch[p.playerID]; ActionLatch[p.playerID] = 0; }
                w.Put((byte)p.playerID);
                w.Put((byte)p.actorState);
                w.Put(flags);
                w.Put((byte)Mathf.Clamp(p.HasDiscs, 0, 255));
                w.Put(actions);
                w.Put(t.position.x); w.Put(t.position.y);
                w.Put(p.velocity.x); w.Put(p.velocity.y);
                var q = t.rotation;
                w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w);
            }

            var discs = gm.activeDiscs.Where(d => d != null && d.isActiveAndEnabled).Take(MaxDiscs).ToList();
            w.Put((byte)discs.Count);
            foreach (var d in discs)
            {
                var owner = d.DiscOwner;
                w.Put(DiscId(d));
                w.Put((byte)(owner != null ? owner.playerID : 255));
                w.Put((byte)d.movement);
                w.Put((byte)(d.IsGoldenDisc ? 1 : 0));
                var p = d.transform.position;
                w.Put(p.x); w.Put(p.y);
                w.Put(d.velocity.x); w.Put(d.velocity.y);
            }
            var movers = Movers();
            w.Put((byte)movers.Count);
            foreach (var m in movers)
            {
                var pos = m.T != null ? m.T.position : Vector3.zero;
                var rot = m.T != null ? m.T.rotation : Quaternion.identity;
                w.Put(pos.x); w.Put(pos.y); w.Put(pos.z);
                w.Put(rot.x); w.Put(rot.y); w.Put(rot.z); w.Put(rot.w);
            }
            BroadcastToPlaying(w, false);
        }

        static void OnSnapshot(int peer, NetDataReader r)
        {
            if (!Session.IsClient) return;
            var s = new SnapshotData
            {
                Round = r.GetInt(), GameState = r.GetByte(), RoundTimer = r.GetFloat(), HostTime = r.GetFloat(),
                ReceivedAt = Time.unscaledTime,
            };
            s.Players = new PlayerSnap[r.GetByte()];
            for (int i = 0; i < s.Players.Length; i++)
            {
                ref var p = ref s.Players[i];
                p.Id = r.GetByte(); p.State = r.GetByte(); p.Flags = r.GetByte(); p.Discs = r.GetByte(); p.Actions = r.GetByte();
                p.Pos = new Vector2(r.GetFloat(), r.GetFloat());
                p.Vel = new Vector2(r.GetFloat(), r.GetFloat());
                p.Rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
            }
            s.Discs = new DiscSnap[r.GetByte()];
            for (int i = 0; i < s.Discs.Length; i++)
            {
                ref var d = ref s.Discs[i];
                d.NetId = r.GetInt(); d.Owner = r.GetByte(); d.Movement = r.GetByte(); d.Kind = r.GetByte();
                d.Pos = new Vector2(r.GetFloat(), r.GetFloat());
                d.Vel = new Vector2(r.GetFloat(), r.GetFloat());
            }
            s.Movers = new MoverSnap[r.AvailableBytes > 0 ? r.GetByte() : 0];
            for (int i = 0; i < s.Movers.Length; i++)
            {
                s.Movers[i].Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                s.Movers[i].Rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
            }

            if (_snaps.Count > 0 && s.HostTime <= _snaps[_snaps.Count - 1].HostTime) return; // late / duplicated
            // clock: keep the smallest observed delay, let it rise slowly (drift, path changes)
            float sample = s.ReceivedAt - s.HostTime;
            if (float.IsNaN(_clockOffset) || sample < _clockOffset) _clockOffset = sample;
            else _clockOffset += (sample - _clockOffset) * 0.01f;
            _snaps.Add(s);
            while (_snaps.Count > 12) _snaps.RemoveAt(0);

            ApplyDiscreteState(s);
        }

        /// <summary>Client, on receipt: one-shot actions and states (not interpolated).</summary>
        static void ApplyDiscreteState(SnapshotData s)
        {
            var gm = GM;
            if (!PassiveClient || gm == null || s.Round != gm.roundNumber) return;
            foreach (var snap in s.Players)
            {
                var p = gm.players.FirstOrDefault(x => x != null && x.playerID == snap.Id);
                if (p == null) continue;
                try
                {
                    if (p.actorState == Actor.ActorState.Alive)
                    {
                        if (p.IsAiming != ((snap.Flags & 1) != 0)) p.IsAiming = (snap.Flags & 1) != 0;
                        if (p.IsDashing != ((snap.Flags & 2) != 0)) p.IsDashing = (snap.Flags & 2) != 0;
                        if (p.IsStunned != ((snap.Flags & 4) != 0)) p.IsStunned = (snap.Flags & 4) != 0;
                        if (p.HasDiscs != snap.Discs) p.HasDiscs = snap.Discs;
                        if (snap.State == (byte)Actor.ActorState.Falling && MStartFall != null)
                        {
                            _replaying = true;
                            try { MStartFall.Invoke(p, null); } finally { _replaying = false; }
                        }
                        // visuals of the host's actions (collisions are disabled here: no effect on the game)
                        if ((snap.Actions & 2) != 0)
                        {
                            _replaying = true;
                            bool ok;
                            try { ok = p.Attack(); } finally { _replaying = false; p.isLunging = false; }
                            if (!ok && p.character != null) p.character.anim.SetTrigger("AttackRight");
                        }
                        if ((snap.Actions & 4) != 0 && p.character != null) p.character.anim.SetTrigger("ThrowRight");
                    }
                }
                catch (Exception e) { if (Plugin.Diag) Plugin.Log.LogWarning($"[MatchSync] state of player {snap.Id}: {e.Message}"); }
            }
            gm.roundTimer = s.RoundTimer;
        }

        /// <summary>Client, every frame after the game's Update: place the puppets.</summary>
        public static void LateTick()
        {
            if (!PassiveClient) { if (_puppets.Count > 0 || _snaps.Count > 0) ResetPassive(); return; }
            var gm = GM;
            if (gm == null || _snaps.Count == 0 || float.IsNaN(_clockOffset)) return;
            if (!InMatch(gm) && gm.gameState != GameManager.GameState.Paused) return;

            // find the two snapshots around the render time
            float renderT = Time.unscaledTime - _clockOffset - InterpDelay;
            SnapshotData a = _snaps[0], b = _snaps[0];
            for (int i = 0; i < _snaps.Count; i++)
            {
                if (_snaps[i].HostTime <= renderT) a = _snaps[i];
                if (_snaps[i].HostTime >= renderT) { b = _snaps[i]; break; }
                b = _snaps[i];
            }
            if (b.Round != gm.roundNumber) return;
            if (a.Round != b.Round) a = b;
            float span = b.HostTime - a.HostTime;
            float k = span > 0.0001f ? Mathf.Clamp01((renderT - a.HostTime) / span) : 1f;
            float extra = renderT > b.HostTime ? Mathf.Min(renderT - b.HostTime, 0.1f) : 0f; // short extrapolation

            foreach (var pb in b.Players)
            {
                var p = gm.players.FirstOrDefault(x => x != null && x.playerID == pb.Id);
                if (p == null) continue;
                if (p.interpolatedTransform != null && p.interpolatedTransform.enabled) p.interpolatedTransform.enabled = false;
                if (pb.State == (byte)Actor.ActorState.Dead || p.actorState == Actor.ActorState.Dead) continue; // let death animations play
                var pa = pb;
                foreach (var x in a.Players) if (x.Id == pb.Id) { pa = x; break; }
                Vector2 pos = Vector2.Lerp(pa.Pos, pb.Pos, k) + pb.Vel * extra;
                var t = p.transform;
                t.position = new Vector3(pos.x, pos.y, t.position.z);
                t.rotation = Quaternion.Slerp(pa.Rot, pb.Rot, k);
                p.velocity = Vector2.Lerp(pa.Vel, pb.Vel, k);
                if (p.character != null && p.character.anim != null && p.character.anim.isActiveAndEnabled && !p.IsDashing && !p.IsStunned)
                {
                    float speed = Mathf.Min(p.velocity.magnitude / 11f, 2f);
                    p.character.anim.SetFloat("MoveSpeed", speed <= 0.25f ? 0f : speed);
                }
            }

            RenderDiscs(gm, a, b, k, extra);
            RenderMovers(a, b, k);

            if (Plugin.Diag && Time.unscaledTime > _nextPassiveDiag)
            {
                _nextPassiveDiag = Time.unscaledTime + 3f;
                Plugin.Log.LogInfo($"[Diag] passive round {gm.roundNumber}: delay {(Time.unscaledTime - _clockOffset - b.HostTime) * 1000f + InterpDelay * 1000f:0}ms behind host, discs host {b.Discs.Length} puppets {_puppets.Count}, players {b.Players.Length}");
            }
        }

        static void RenderDiscs(GameManager gm, SnapshotData a, SnapshotData b, float k, float extra)
        {
            var seen = new HashSet<int>();
            foreach (var db in b.Discs)
            {
                seen.Add(db.NetId);
                if (!_puppets.TryGetValue(db.NetId, out var d) || d == null || !d.isActiveAndEnabled)
                {
                    d = SpawnPuppet(gm, db);
                    if (d == null) continue;
                    _puppets[db.NetId] = d;
                    _puppetSet.Add(d);
                }
                var da = db;
                foreach (var x in a.Discs) if (x.NetId == db.NetId) { da = x; break; }
                Vector2 pos = Vector2.Lerp(da.Pos, db.Pos, k) + db.Vel * extra;
                var t = d.transform;
                t.position = new Vector3(pos.x, pos.y, t.position.z);
                d.velocity = db.Vel;
                d.movement = (Projectile.Movement)db.Movement;
            }

            // boomerangs the host no longer has (picked up, destroyed), and local ones it never had
            List<int> gone = null;
            foreach (var kv in _puppets) if (!seen.Contains(kv.Key)) (gone ??= new List<int>()).Add(kv.Key);
            if (gone != null) foreach (int id in gone) { RemovePuppet(_puppets[id]); _puppets.Remove(id); }
            foreach (var d in gm.activeDiscs.ToList())
                if (d != null && !_puppetSet.Contains(d)) RemovePuppet(d);
        }

        static Disc SpawnPuppet(GameManager gm, DiscSnap s)
        {
            try
            {
                _replaying = true;
                var owner = gm.players.FirstOrDefault(p => p != null && p.playerID == s.Owner);
                if (owner == null && s.Owner != 255) return null; // its game code needs the owner (Disc.Update / CalculateReturn)
                var d = gm.discPrefab.Spawn(new Vector3(s.Pos.x, s.Pos.y, -0.75f), Quaternion.identity);
                d.DiscOwner = owner;
                d.throwDirection = s.Vel.sqrMagnitude > 0.01f ? s.Vel.normalized : Vector2.up;
                d.aimPower = 1f;
                gm.activeDiscs.Add(d);
                var movement = (Projectile.Movement)s.Movement;
                d.Init(isDropped: movement == Projectile.Movement.Dropped, isMiniDisc: false, isGoldenDisc: (s.Kind & 1) != 0);
                if (d.interpolatedTransform != null) d.interpolatedTransform.enabled = false;
                d.movement = movement;
                return d;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[MatchSync] puppet boomerang: " + e.Message); return null; }
            finally { _replaying = false; }
        }

        static void RemovePuppet(Disc d)
        {
            if (d == null) return;
            _puppetSet.Remove(d);
            if (d.interpolatedTransform != null) d.interpolatedTransform.enabled = true; // pooled: may be reused as a real one
            if (!d.isActiveAndEnabled) { GM?.activeDiscs.Remove(d); return; }
            _replaying = true;
            try { d.GetPickedUp(vanish: true); }
            catch (Exception e) { Plugin.Log.LogWarning("[MatchSync] remove boomerang: " + e.Message); GM?.activeDiscs.Remove(d); }
            finally { _replaying = false; }
        }

        static void ResetPassive()
        {
            _snaps.Clear();
            _puppets.Clear();
            _puppetSet.Clear();
            _clockOffset = float.NaN;
        }

        // ── Session lifecycle ────────────────────────────────────────────────────

        public static void OnPeerJoined(int peer)
        {
            UiMirror.ResendState();
            LobbySync.RefreshDevices();
        }

        public static void OnSessionEnded(bool wasActive)
        {
            _received = null; ResetPassive(); DiscIds.Clear(); _pendingPlaylist = null; _pendingLate = null;
            RoundSync.Reset();
            var gm = GM;
            var ui = UI;
            if (wasActive && gm != null && ui != null && gm.gameState != GameManager.GameState.Menu)
            {
                try { ui.QuitMatch(); } catch (Exception e) { Plugin.Log.LogWarning("[MatchSync] quit: " + e.Message); }
            }
            LobbySync.RefreshDevices();
        }
    }
}
