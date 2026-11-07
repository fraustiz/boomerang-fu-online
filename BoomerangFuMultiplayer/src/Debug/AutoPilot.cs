using System.Collections.Generic;
using BoomerangFuMultiplayer.Net;
using BoomerangFuMultiplayer.Sync;
using UnityEngine;

namespace BoomerangFuMultiplayer.Debugging
{
    /// <summary>
    /// Test-only scripted controller for the local player (-bfump-autoplay): joins and readies up
    /// in the lobby, the host launches the match, then everyone wanders and attacks at random.
    /// Also writes [Diag] lines (menus, game states) used to check sync between two PCs.
    /// </summary>
    public static class AutoPilot
    {
        public static bool Enabled;
        /// <summary>Host: number of bots to add in the lobby (-bfump-bots N).</summary>
        public static int WantBots;
        /// <summary>Host: do not launch before this many PCs are connected (-bfump-wait-players N).</summary>
        public static int WaitPlayers;
        static float _nextBot;

        static int CountBots(UIMenuLobby lobby)
        {
            int n = 0;
            foreach (var lp in lobby.uiPlayers)
                if (lp != null && lp.playerType == PlayerType.Bot && lp.readyState != UILobbyPlayer.ReadyState.Disabled) n++;
            return n;
        }

        const int Dash = 2, Swipe = 3, Throw = 4, UISubmit = 18;
        static readonly Dictionary<int, float> ReleaseAt = new Dictionary<int, float>();
        static float _nextTap, _nextWander, _menuSince, _allReadySince = -1f;
        static Vector2 _move;
        static bool _fightCalled;
        static UIMenuBase _lastMenu, _diagMenu;
        static GameManager.GameState _lastState = (GameManager.GameState)(-1);
        static int _lastRound = -1;

        static void Tap(int id, float duration)
        {
            if (Held(id)) return;
            ReleaseAt[id] = Time.unscaledTime + duration;
        }

        static bool Held(int id) => ReleaseAt.TryGetValue(id, out float t) && Time.unscaledTime < t;

        /// <summary>Host: bot level forced for tests (-bfump-bot-difficulty), -1 = game setting.</summary>
        public static int BotDifficulty = -1;

        public static void Tick()
        {
            DiagTransitions();
            if (!Enabled) return;
            NetInput.Autoplay = Session.Active;
            if (!Session.Active) return;
            if (BotDifficulty >= 0 && Session.IsHost)
            {
                var sm = Game.Get<SettingsManager>();
                if (sm != null && (int)sm.botDifficulty != BotDifficulty)
                {
                    sm.botDifficulty = (SettingsManager.BotDifficulty)BotDifficulty;
                    Plugin.Log.LogInfo($"[Diag] autopilot: bot difficulty {sm.botDifficulty}");
                }
            }

            var ui = Game.Get<UIManager>();
            var gm = Game.Get<GameManager>();
            if (ui == null || gm == null) return;
            var cur = ui.GetCurrentMenu();
            if (cur != _lastMenu) { _lastMenu = cur; _menuSince = Time.unscaledTime; }
            float now = Time.unscaledTime;
            bool forceHoldDash = false;
            Vector2 move = Vector2.zero;

            if (cur is UIMenuLobby lobby)
            {
                int slot = Session.Instance.LocalSlot;
                var lp = slot >= 0 && slot < lobby.uiPlayers.Length ? lobby.uiPlayers[slot] : null;
                if (Session.IsHost && WantBots > 0 && Session.Instance.PlayerCount >= WaitPlayers
                    && now - _menuSince > 1.5f && now > _nextBot && CountBots(lobby) < WantBots)
                {
                    _nextBot = now + 0.6f;
                    lobby.AddBot();
                    Plugin.Log.LogInfo($"[Diag] autopilot: bot added ({CountBots(lobby)}/{WantBots})");
                }
                if (lp != null && now - _menuSince > 2f)
                {
                    if ((lp.readyState == UILobbyPlayer.ReadyState.Available || lp.readyState == UILobbyPlayer.ReadyState.Selecting) && now > _nextTap)
                    {
                        Tap(Dash, 0.15f);
                        _nextTap = now + 1.5f;
                    }
                    else if (lp.readyState == UILobbyPlayer.ReadyState.Ready && Session.IsHost && AllReady(lobby))
                    {
                        if (_allReadySince < 0f) _allReadySince = now;
                        float t = now - _allReadySince;
                        if (t > 1f && t < 1.15f) Tap(Dash, 0.15f);       // Down edge unblocks the charge
                        else if (t > 1.6f && t < 6f) forceHoldDash = true; // hold to fill "continue"
                    }
                    else _allReadySince = -1f;
                }
            }
            else if (cur is UIMenuMatchSettings ms && Session.IsHost && !_fightCalled && now - _menuSince > 3f)
            {
                _fightCalled = true;
                Plugin.Log.LogInfo("[Diag] autopilot: host launches the match");
                ms.UIResponse_Fight(null);
            }
            else if (Session.IsHost && cur != null && now - _menuSince > 2.5f && now > _nextTap
                     && (cur.MenuID == UIMenuBase.MenuType.Instructions || cur.MenuID == UIMenuBase.MenuType.RoundEnd))
            {
                // host validates the selected button (Continue); clients follow through the click mirror
                Tap(UISubmit, 0.12f);
                _nextTap = now + 3f;
                Plugin.Log.LogInfo($"[Diag] autopilot: submit on {cur.MenuID}");
            }
            else if (cur == null && gm.gameState == GameManager.GameState.Playing)
            {
                if (now > _nextWander)
                {
                    _nextWander = now + Random.Range(0.4f, 0.9f);
                    _move = Random.insideUnitCircle.normalized;
                    float r = Random.value;
                    if (r < 0.15f) Tap(Swipe, 0.1f);
                    else if (r < 0.27f) Tap(Dash, 0.1f);
                    else if (r < 0.37f) Tap(Throw, Random.Range(0.3f, 0.7f));
                }
                move = _move;
            }
            if (gm.gameState == GameManager.GameState.Menu && cur is UIMenuLobby == false && !(cur is UIMenuMatchSettings)) _fightCalled = false;

            ushort held = 0;
            foreach (var kv in ReleaseAt)
                if (now < kv.Value) held |= (ushort)NetInput.ButtonBit(kv.Key);
            if (forceHoldDash) held |= (ushort)NetInput.ButtonBit(Dash);
            NetInput.AutoSet(held, move.x, move.y);
        }

        static bool AllReady(UIMenuLobby lobby)
        {
            int ready = 0;
            foreach (var lp in lobby.uiPlayers)
                if (lp != null && lp.playerType == PlayerType.Human && lp.readyState == UILobbyPlayer.ReadyState.Ready) ready++;
            if (WantBots > 0 && CountBots(lobby) < WantBots) return false;
            if (Session.Instance.PlayerCount < WaitPlayers) return false;
            return ready >= Session.Instance.PlayerCount;
        }

        static void DiagTransitions()
        {
            if (!Plugin.Diag) return;
            var gm = Game.Get<GameManager>();
            var ui = Game.Get<UIManager>();
            if (gm == null || ui == null) return;
            if (gm.gameState != _lastState || gm.roundNumber != _lastRound)
            {
                _lastState = gm.gameState; _lastRound = gm.roundNumber;
                Plugin.Log.LogInfo($"[Diag] state {gm.gameState} round {gm.roundNumber} players {gm.players.Count}");
            }
            var menu = ui.GetCurrentMenu();
            if (menu != _diagMenu)
            {
                _diagMenu = menu;
                Plugin.Log.LogInfo("[Diag] menu " + (menu != null ? menu.MenuID.ToString() : "none") + " | stack " + string.Join(">", UiMirror.Stack(ui)));
            }
        }
    }
}
