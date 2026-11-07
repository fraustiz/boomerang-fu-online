using System;
using System.Collections;
using System.Reflection;
using BoomerangFuMultiplayer.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace BoomerangFuMultiplayer.Sync
{
    /// <summary>
    /// Lobby: every machine runs the character-select screen with the same (mirrored) inputs, so
    /// it mostly stays in sync on its own; the host still sends its lobby state a few times per
    /// second and clients fix whatever drifted (random picks, bots, timing).
    /// </summary>
    public static class LobbySync
    {
        static readonly FieldInfo FInputDevice = AccessTools.Field(typeof(UILobbyPlayer), "inputDevice");
        static readonly MethodInfo MSelectGridCell = AccessTools.Method(typeof(UILobbyPlayer), "SelectGridCell");
        static readonly MethodInfo MSelectAltGridCell = AccessTools.Method(typeof(UILobbyPlayer), "SelectAltGridCell");

        public struct PlayerState
        {
            public byte Ready, Type, Team;
            public sbyte Cell;
            public int Character, Hat;
        }

        static PlayerState[] _hostState;
        static float[] _mismatchSince = new float[8];
        static float _nextSend;

        public static void Register(Session s) => s.On(Msg.LobbyState, OnLobbyState);

        public static void Patch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(UILobbyPlayer), nameof(UILobbyPlayer.AssignInputDevice)),
                prefix: new HarmonyMethod(typeof(LobbySync), nameof(AssignInputDevicePrefix)));
            h.Patch(AccessTools.Method(typeof(UIMenuLobby), nameof(UIMenuLobby.UIResponse_Continue)),
                prefix: new HarmonyMethod(typeof(LobbySync), nameof(ContinuePrefix)));
            h.Patch(AccessTools.Method(typeof(UIMenuLobby), nameof(UIMenuLobby.UIResponse_Close)),
                prefix: new HarmonyMethod(typeof(LobbySync), nameof(HostOnlyPrefix)));
            h.Patch(AccessTools.Method(typeof(UIMenuLobby), nameof(UIMenuLobby.AddBot)),
                prefix: new HarmonyMethod(typeof(LobbySync), nameof(HostOnlyPrefix)));
            h.Patch(AccessTools.Method(typeof(UIMenuLobby), nameof(UIMenuLobby.RemoveBot)),
                prefix: new HarmonyMethod(typeof(LobbySync), nameof(HostOnlyPrefix)));
        }

        /// <summary>
        /// Online slots have no physical device on this PC. Treat used slots as "a gamepad is
        /// plugged in" and unused ones as empty, without touching Rewired's real controllers.
        /// </summary>
        static bool AssignInputDevicePrefix(UILobbyPlayer __instance)
        {
            if (!Session.Active) return true;
            int slot = __instance.playerID;
            bool used = Session.Instance.SlotUsed(slot);
            FInputDevice.SetValue(__instance, used ? UILobbyPlayer.InputDevice.Joystick : UILobbyPlayer.InputDevice.None);
            if (__instance.playerType != PlayerType.Human) return false;
            if (!used)
            {
                if (__instance.readyState != UILobbyPlayer.ReadyState.Disabled)
                {
                    __instance.SetReadyState(UILobbyPlayer.ReadyState.Disabled, skipAnimation: true);
                    MSelectGridCell.Invoke(__instance, new object[] { null });
                }
            }
            else if (__instance.readyState == UILobbyPlayer.ReadyState.Disabled)
            {
                __instance.SetReadyState(UILobbyPlayer.ReadyState.Available);
            }
            return false;
        }

        static bool ContinuePrefix(ref IEnumerator __result)
        {
            if (Session.IsClient && !Session.InHostScope) { __result = Empty(); return false; }
            return true;
        }

        static bool HostOnlyPrefix() => !(Session.IsClient && !Session.InHostScope);

        static IEnumerator Empty() { yield break; }

        /// <summary>Re-evaluate every lobby slot (call when the roster changes).</summary>
        public static void RefreshDevices()
        {
            var lobby = Lobby;
            if (lobby == null) return;
            foreach (var lp in lobby.uiPlayers) if (lp != null) lp.AssignInputDevice();
            lobby.UpdateBotSliders();
        }

        static UIMenuLobby Lobby
        {
            get
            {
                var ui = Game.Get<UIManager>();
                if (ui == null) return null;
                try { return ui.GetMenu(UIMenuBase.MenuType.Lobby) as UIMenuLobby; } catch { return null; }
            }
        }

        // ── Host ─────────────────────────────────────────────────────────────────

        public static void Tick()
        {
            if (!Session.Active) { _hostState = null; return; }
            var lobby = Lobby;
            var ui = Game.Get<UIManager>();
            if (lobby == null || ui == null) return;
            var cur = ui.GetCurrentMenu();
            bool inLobby = cur == lobby || (cur != null && cur.MenuID == UIMenuBase.MenuType.MatchSettings);

            if (Session.IsHost)
            {
                if (!inLobby || Time.unscaledTime < _nextSend) return;
                _nextSend = Time.unscaledTime + 0.2f;
                var w = Protocol.Begin(Msg.LobbyState);
                Write(w, Capture(lobby));
                Session.Instance.BroadcastAll(w, false);
            }
            else if (inLobby && _hostState != null)
            {
                Apply(lobby, _hostState, force: false);
            }
        }

        public static PlayerState[] Capture(UIMenuLobby lobby)
        {
            var arr = new PlayerState[lobby.uiPlayers.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                var lp = lobby.uiPlayers[i];
                arr[i] = new PlayerState
                {
                    Ready = (byte)lp.readyState,
                    Type = (byte)lp.playerType,
                    Team = (byte)lp.team,
                    Cell = (sbyte)(lp.currentGridCell != null ? lp.currentGridCell.index : -1),
                    Character = lp.character != null ? (int)lp.character.characterType : -1,
                    Hat = lp.character != null ? (int)lp.character.equippedHat : -1,
                };
            }
            return arr;
        }

        public static void Write(NetDataWriter w, PlayerState[] s)
        {
            w.Put((byte)s.Length);
            foreach (var p in s)
            {
                w.Put(p.Ready); w.Put(p.Type); w.Put(p.Team); w.Put(p.Cell); w.Put(p.Character); w.Put(p.Hat);
            }
        }

        public static PlayerState[] Read(NetDataReader r)
        {
            var s = new PlayerState[r.GetByte()];
            for (int i = 0; i < s.Length; i++)
            {
                s[i].Ready = r.GetByte(); s[i].Type = r.GetByte(); s[i].Team = r.GetByte();
                s[i].Cell = r.GetSByte(); s[i].Character = r.GetInt(); s[i].Hat = r.GetInt();
            }
            return s;
        }

        // ── Client ───────────────────────────────────────────────────────────────

        static void OnLobbyState(int peer, NetDataReader r)
        {
            if (Session.IsClient) _hostState = Read(r);
        }

        /// <summary>Force one lobby slot into the given state (used to add a player mid-match).</summary>
        public static void ForceSlot(UIMenuLobby lobby, int slot, PlayerState s)
        {
            if (lobby == null || slot < 0 || slot >= lobby.uiPlayers.Length) return;
            using (Session.HostScope())
            {
                try { Fix(lobby, lobby.uiPlayers[slot], s); }
                catch (Exception e) { Plugin.Log.LogWarning($"[LobbySync] force slot {slot}: {e.Message}"); }
            }
        }

        public static void ApplyNow(PlayerState[] state)
        {
            var lobby = Lobby;
            if (lobby != null && state != null) Apply(lobby, state, force: true);
        }

        static void Apply(UIMenuLobby lobby, PlayerState[] state, bool force)
        {
            int n = Math.Min(state.Length, lobby.uiPlayers.Length);
            if (_mismatchSince.Length < n) _mismatchSince = new float[n];
            using (Session.HostScope())
            {
                for (int i = 0; i < n; i++)
                {
                    var lp = lobby.uiPlayers[i];
                    if (lp == null) continue;
                    if (lp.rewiredPlayer == null) lp.rewiredPlayer = Rewired.ReInput.players.GetPlayer(lp.playerID);
                    if (Same(lp, state[i])) { _mismatchSince[i] = 0f; continue; }
                    if (!force)
                    {
                        // let mirrored input catch up first: only fix lasting differences
                        if (_mismatchSince[i] <= 0f) { _mismatchSince[i] = Time.unscaledTime; continue; }
                        if (Time.unscaledTime - _mismatchSince[i] < 0.35f) continue;
                    }
                    _mismatchSince[i] = 0f;
                    try { Fix(lobby, lp, state[i]); }
                    catch (Exception e) { Plugin.Log.LogWarning($"[LobbySync] slot {i}: {e.Message}"); }
                }
            }
        }

        static bool Same(UILobbyPlayer lp, PlayerState s)
        {
            if ((byte)lp.readyState != s.Ready) return false;
            if (s.Ready == (byte)UILobbyPlayer.ReadyState.Disabled) return true;
            if ((byte)lp.playerType != s.Type || (byte)lp.team != s.Team) return false;
            int cell = lp.currentGridCell != null ? lp.currentGridCell.index : -1;
            if (cell != s.Cell) return false;
            if (lp.character != null && s.Character >= 0)
            {
                if ((int)lp.character.characterType != s.Character) return false;
                if ((int)lp.character.equippedHat != s.Hat) return false;
            }
            return true;
        }

        static void Fix(UIMenuLobby lobby, UILobbyPlayer lp, PlayerState s)
        {
            // A client that never opened the lobby (joined during a match) has no Rewired player
            // bound to the slot yet; CreatePlayer would then fall back to Rewired player 0.
            if (lp.rewiredPlayer == null) lp.rewiredPlayer = Rewired.ReInput.players.GetPlayer(lp.playerID);
            var ready = (UILobbyPlayer.ReadyState)s.Ready;
            if (ready == UILobbyPlayer.ReadyState.Disabled)
            {
                if (lp.readyState != ready) lp.SetReadyState(ready, skipAnimation: true);
                return;
            }

            UICharacterGridCell cell = s.Cell >= 0 && s.Cell < lobby.characterGridCells.Length ? lobby.characterGridCells[s.Cell] : null;

            if (lp.readyState == UILobbyPlayer.ReadyState.Disabled) lp.SetReadyState(UILobbyPlayer.ReadyState.Available, true);

            if (s.Type == (byte)PlayerType.Bot && lp.playerType != PlayerType.Bot) lp.SetBot(cell);
            else if (s.Type == (byte)PlayerType.Human && lp.playerType == PlayerType.Bot) lp.Reset();

            if (cell != null && lp.currentGridCell != cell) MSelectGridCell.Invoke(lp, new object[] { cell });
            if (cell != null && lp.character != null && s.Character >= 0 && (int)lp.character.characterType != s.Character)
                MSelectAltGridCell.Invoke(lp, new object[] { cell });

            if (lp.character != null && s.Hat >= 0 && (int)lp.character.equippedHat != s.Hat)
            {
                lp.character.equippedHat = (HatType)s.Hat;
                try { lp.character.SetHat(); } catch { }
            }
            if (lp.team != s.Team) lp.SetTeam(s.Team);
            if (lp.readyState != ready)
            {
                if (ready == UILobbyPlayer.ReadyState.Ready && lp.readyState == UILobbyPlayer.ReadyState.Available)
                    lp.SetReadyState(UILobbyPlayer.ReadyState.Selecting, true);
                lp.SetReadyState(ready, true);
            }
        }
    }
}
