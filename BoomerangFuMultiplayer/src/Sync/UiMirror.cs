using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BoomerangFuMultiplayer.Net;
using BoomerangFuMultiplayer.UI;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace BoomerangFuMultiplayer.Sync
{
    /// <summary>
    /// "Master" menus (main, match settings, pause, round end...) run on the host only. The host's
    /// event system accepts input from every slot (remote players can navigate like couch players);
    /// clients do not process menu input and instead mirror the host's selection, button presses
    /// and menu stack.
    /// </summary>
    public static class UiMirror
    {
        static readonly FieldInfo FMenuStack = AccessTools.Field(typeof(UIManager), "menuStack");
        static readonly FieldInfo FMenus = AccessTools.Field(typeof(UIManager), "menus");

        static readonly HashSet<UIMenuBase.MenuType> Reconcilable = new HashSet<UIMenuBase.MenuType>
        {
            UIMenuBase.MenuType.Main, UIMenuBase.MenuType.Options, UIMenuBase.MenuType.Lobby,
            UIMenuBase.MenuType.MatchSettings, UIMenuBase.MenuType.Credits, OnlineMenu.MenuId,
        };

        /// <summary>True when menu-input polling must return nothing on this machine.</summary>
        public static bool ShouldGateUi =>
            NetInput.UiContext > 0 && Session.IsClient && !OnlineMenu.IsCurrent;

        static string _lastSelectSent;
        static float _nextStackSend;
        static UIMenuBase.MenuType[] _hostStack;
        static float _mismatchSince = -1f;
        static float _roundEndLagSince = -1f;

        public static void Register(Session s)
        {
            s.On(Msg.UiSelect, OnSelect);
            s.On(Msg.UiPress, OnPress);
            s.On(Msg.MenuStack, OnMenuStack);
            UIManager.OnSelectionChanged += OnLocalSelectionChanged;
        }

        // ── Patches ──────────────────────────────────────────────────────────────

        public static void Patch(Harmony h)
        {
            var enter = new HarmonyMethod(AccessTools.Method(typeof(UiMirror), nameof(EnterUi)));
            var exit = new HarmonyMethod(AccessTools.Method(typeof(UiMirror), nameof(ExitUi)));
            foreach (var m in new[]
            {
                AccessTools.Method(typeof(Rewired.Integration.UnityUI.RewiredStandaloneInputModule), "Process"),
                AccessTools.Method(typeof(UIManager), "GetButtonDownFromEventSystem"),
                AccessTools.Method(typeof(UIManager), "GetAnyInputFromEventSystem"),
                AccessTools.Method(typeof(UIEventSystemPlayerSwapper), "Update"),
            })
            {
                if (m == null) { Plugin.Log.LogWarning("[UiMirror] UI input method not found"); continue; }
                h.Patch(m, prefix: enter, finalizer: exit);
            }

            h.Patch(AccessTools.Method(typeof(Button), "Press"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(UiMirror), nameof(ButtonPressPrefix))));
        }

        static readonly AccessTools.FieldRef<Rewired.Integration.UnityUI.RewiredStandaloneInputModule, bool> ModuleHasFocus =
            AccessTools.FieldRefAccess<Rewired.Integration.UnityUI.RewiredStandaloneInputModule, bool>("m_HasFocus");

        /// <summary>
        /// Online, menus also take input from the network: an unfocused window (host alt-tabbed,
        /// dedicated host) must keep processing it. Real devices stay muted by Rewired when unfocused.
        /// </summary>
        static void KeepUiFocused(UIManager ui)
        {
            try
            {
                var module = ui.eventSystem != null ? ui.eventSystem.currentInputModule as Rewired.Integration.UnityUI.RewiredStandaloneInputModule : null;
                if (module != null && !ModuleHasFocus(module)) ModuleHasFocus(module) = true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[UiMirror] focus: " + e.Message); }
        }

        static void EnterUi() => NetInput.UiContext++;
        static Exception ExitUi(Exception __exception) { NetInput.UiContext--; return __exception; }

        static bool ButtonPressPrefix(Button __instance)
        {
            if (!Session.Active || OnlineMenu.Owns(__instance)) return true;
            if (Session.IsHost)
            {
                var w = Protocol.Begin(Msg.UiPress);
                w.Put(PathOf(__instance.transform));
                Session.Instance.BroadcastAll(w, true);
                return true;
            }
            // Client: only presses replayed from the host go through.
            return Session.InHostScope;
        }

        // ── Host side ────────────────────────────────────────────────────────────

        static void OnLocalSelectionChanged(GameObject go)
        {
            if (!Session.IsHost || go == null || OnlineMenu.Owns(go)) return;
            string path = PathOf(go.transform);
            if (path == _lastSelectSent) return;
            _lastSelectSent = path;
            var w = Protocol.Begin(Msg.UiSelect);
            w.Put(path);
            Session.Instance.BroadcastAll(w, true);
        }

        public static void ResendState()
        {
            _lastSelectSent = null;
            _nextStackSend = 0f;
        }

        public static void Tick()
        {
            if (!Session.Active) { _hostStack = null; _mismatchSince = -1f; return; }
            var ui = Game.Get<UIManager>();
            if (ui == null) return;
            KeepUiFocused(ui);

            if (Session.IsHost)
            {
                if (Time.unscaledTime < _nextStackSend) return;
                _nextStackSend = Time.unscaledTime + 0.25f;
                var stack = Stack(ui);
                var w = Protocol.Begin(Msg.MenuStack);
                w.Put((byte)stack.Length);
                foreach (var t in stack) w.Put((int)t);
                Session.Instance.BroadcastAll(w, false);
                return;
            }
            Reconcile(ui);
        }

        // ── Client side ──────────────────────────────────────────────────────────

        static void OnSelect(int peer, NetDataReader r)
        {
            var go = Find(r.GetString());
            if (go == null || !go.activeInHierarchy) return;
            var ui = Game.Get<UIManager>();
            using (Session.HostScope()) ui?.SetSelectedGameObject(go);
        }

        static void OnPress(int peer, NetDataReader r)
        {
            string path = r.GetString();
            var go = Find(path);
            var btn = go != null ? go.GetComponent<Button>() : null;
            if (btn == null) { Plugin.Log.LogWarning("[UiMirror] pressed button not found: " + path); return; }
            if (!btn.gameObject.activeInHierarchy) return; // that menu is not open here (e.g. waiting to join)
            using (Session.HostScope())
            {
                try { btn.onClick.Invoke(); }
                catch (Exception e) { Plugin.Log.LogError("[UiMirror] replay click: " + e); }
            }
        }

        static void OnMenuStack(int peer, NetDataReader r)
        {
            int n = r.GetByte();
            var s = new UIMenuBase.MenuType[n];
            for (int i = 0; i < n; i++) s[i] = (UIMenuBase.MenuType)r.GetInt();
            if (_hostStack == null || !_hostStack.SequenceEqual(s))
                Plugin.Log.LogInfo("[UiMirror] host menus: " + string.Join(">", s));
            _hostStack = s;
        }

        static void Reconcile(UIManager ui)
        {
            var host = _hostStack;
            var gm = Game.Get<GameManager>();
            if (host == null || gm == null || gm.gameState != GameManager.GameState.Menu) { _mismatchSince = -1f; return; }
            var local = Stack(ui);

            // Still on the round-end card while the host has left it: its Continue press arrived before our
            // button existed (a late joiner's scoreboard animates more points). Continue by ourselves,
            // otherwise this client loads every following round late (or never reaches the match end).
            if (local.Length > 0 && local[local.Length - 1] == UIMenuBase.MenuType.RoundEnd && !host.Contains(UIMenuBase.MenuType.RoundEnd))
            {
                if (_roundEndLagSince < 0f) _roundEndLagSince = Time.unscaledTime;
                else if (Time.unscaledTime - _roundEndLagSince > 1.5f && ui.GetCurrentMenu() is UIMenuRoundEnd re
                         && re.continueButton != null && re.continueButton.interactable)
                {
                    _roundEndLagSince = -1f;
                    Plugin.Log.LogInfo("[UiMirror] the host left the round end: continuing");
                    using (Session.HostScope()) re.UIResponse_Continue();
                }
                return;
            }
            _roundEndLagSince = -1f;
            // The title screen may or may not sit at the bottom of a stack (UIManager.QuitMatch rebuilds the
            // stack from Main): ignore a leading Splash on both sides, it is never pushed or popped here.
            int lo = local.Length > 0 && local[0] == UIMenuBase.MenuType.Splash ? 1 : 0;
            int ho = host.Length > 0 && host[0] == UIMenuBase.MenuType.Splash ? 1 : 0;
            if (local.Skip(lo).SequenceEqual(host.Skip(ho))) { _mismatchSince = -1f; return; }

            // Only the part that differs must be made of menus we know how to push/pop.
            int common = 0;
            while (lo + common < local.Length && ho + common < host.Length && local[lo + common] == host[ho + common]) common++;
            if (!local.Skip(lo + common).All(Reconcilable.Contains) || !host.Skip(ho + common).All(Reconcilable.Contains))
            {
                LogSkip(local, host);
                _mismatchSince = -1f;
                return;
            }
            if (_mismatchSince < 0f) { _mismatchSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - _mismatchSince < 0.6f) return;
            _mismatchSince = -1f;
            Plugin.Log.LogInfo($"[UiMirror] menu stack {string.Join(">", local)} → {string.Join(">", host)}");
            using (Session.HostScope())
            {
                int keep = lo + common;
                for (int i = local.Length; i > keep; i--) ui.PopMenu(resumeLastMenu: i - 1 == keep && host.Length == ho + common);
                for (int i = ho + common; i < host.Length; i++)
                {
                    var menu = ui.GetMenu(host[i]);
                    if (menu == null) break;
                    ui.PushMenu(menu);
                    if (menu is UIMenuLobby lobby) lobby.ResetAllPlayers();
                }
            }
        }

        static string _lastSkip;
        static void LogSkip(UIMenuBase.MenuType[] local, UIMenuBase.MenuType[] host)
        {
            string s = string.Join(">", local) + " vs " + string.Join(">", host);
            if (s == _lastSkip) return;
            _lastSkip = s;
            Plugin.Log.LogInfo("[UiMirror] menu stacks differ, not reconcilable: " + s);
        }

        /// <summary>Menu stack, bottom first.</summary>
        public static UIMenuBase.MenuType[] Stack(UIManager ui)
        {
            var st = FMenuStack.GetValue(ui) as Stack<UIMenuBase>;
            if (st == null) return Array.Empty<UIMenuBase.MenuType>();
            return st.Reverse().Where(m => m != null).Select(m => m.MenuID).ToArray();
        }

        public static void RegisterMenu(UIManager ui, UIMenuBase menu)
        {
            var dict = FMenus.GetValue(ui) as Dictionary<UIMenuBase.MenuType, UIMenuBase>;
            if (dict != null) dict[menu.MenuID] = menu;
        }

        // ── Paths ────────────────────────────────────────────────────────────────

        public static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Add(t.GetSiblingIndex() + ":" + t.name);
            parts.Reverse();
            var sb = new StringBuilder();
            foreach (var p in parts) { if (sb.Length > 0) sb.Append('/'); sb.Append(p); }
            return sb.ToString();
        }

        public static GameObject Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.Split('/');
            Transform cur = null;
            foreach (var root in Roots())
            {
                if (Match(root.transform, parts[0])) { cur = root.transform; break; }
            }
            if (cur == null) return null;
            for (int i = 1; i < parts.Length && cur != null; i++)
            {
                Split(parts[i], out int idx, out string name);
                Transform next = null;
                if (idx >= 0 && idx < cur.childCount && cur.GetChild(idx).name == name) next = cur.GetChild(idx);
                else
                {
                    for (int c = 0; c < cur.childCount; c++)
                        if (cur.GetChild(c).name == name) { next = cur.GetChild(c); break; }
                }
                cur = next;
            }
            return cur != null ? cur.gameObject : null;
        }

        static bool Match(Transform t, string part)
        {
            Split(part, out _, out string name);
            return t.name == name;
        }

        static void Split(string part, out int idx, out string name)
        {
            int c = part.IndexOf(':');
            idx = c > 0 && int.TryParse(part.Substring(0, c), out int v) ? v : -1;
            name = c >= 0 ? part.Substring(c + 1) : part;
        }

        static IEnumerable<GameObject> Roots()
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!sc.isLoaded) continue;
                foreach (var g in sc.GetRootGameObjects()) yield return g;
            }
            if (Plugin.Instance != null)
            {
                var dd = Plugin.Instance.gameObject.scene;
                if (dd.IsValid())
                    foreach (var g in dd.GetRootGameObjects()) yield return g;
            }
        }
    }
}
