using System;
using System.Collections.Generic;
using System.Reflection;
using BoomerangFuMultiplayer.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using Rewired;
using UnityEngine;
using RPlayer = Rewired.Player;

namespace BoomerangFuMultiplayer.Sync
{
    /// <summary>
    /// Virtual controllers. During a session Rewired players 0..3 are the 4 online slots:
    ///  - the local slot reads this PC's physical input (always taken from Rewired player 0);
    ///  - other used slots read the input frames streamed by their owner;
    ///  - unused slots read nothing.
    /// Because the game reads all of its input through Rewired.Player, remote players behave
    /// exactly like couch players everywhere (lobby, menus, gameplay).
    /// </summary>
    public static class NetInput
    {
        // Rewired action ids (RewiredConsts.Action)
        static readonly int[] ButtonIds = { 2, 3, 4, 5, 6, 18, 19, 22, 23, 24, 25 }; // Dash Swipe Throw Back Start UISubmit UICancel UISelect UIRestore UITabLeft UITabRight
        static readonly int[] AxisIds = { 0, 1, 16, 17 };                          // Move H/V, UI H/V
        static readonly Dictionary<int, int> ButtonIndex = new Dictionary<int, int>();
        static readonly Dictionary<int, int> AxisIndex = new Dictionary<int, int>();
        static readonly Dictionary<string, int> ActionIds = new Dictionary<string, int>();

        [ThreadStatic] static int _bypass;
        /// <summary>&gt;0 while Unity UI / menu code polls input (see UiMirror gating).</summary>
        public static int UiContext;

        static readonly Pad[] Pads = new Pad[Protocol.MaxSlots];
        static readonly LocalSampler Sampler = new LocalSampler();

        static NetInput()
        {
            for (int i = 0; i < ButtonIds.Length; i++) ButtonIndex[ButtonIds[i]] = i;
            for (int i = 0; i < AxisIds.Length; i++) AxisIndex[AxisIds[i]] = i;
            for (int i = 0; i < Pads.Length; i++) Pads[i] = new Pad();
        }

        // ── Routing ──────────────────────────────────────────────────────────────

        enum Route { Original, Neutral, Local, Remote }

        static Route RouteOf(RPlayer p, out int slot)
        {
            slot = -1;
            if (_bypass > 0 || !Session.Active || p == null) return Route.Original;
            if (UiMirror.ShouldGateUi) return Route.Neutral; // client: menus are driven by the host
            slot = p.id;
            if (slot < 0 || slot >= Protocol.MaxSlots) return Route.Original; // system player etc.
            var s = Session.Instance;
            // The online screen (join / leave buttons) only answers to this PC's own player.
            if (UiContext > 0 && slot != s.LocalSlot && UI.OnlineMenu.IsCurrent) return Route.Neutral;
            if (slot == s.LocalSlot) return slot == 0 && !Autoplay ? Route.Original : Route.Local;
            return s.SlotUsed(slot) ? Route.Remote : Route.Neutral;
        }

        static int ActionId(object arg)
        {
            if (arg is int i) return i;
            if (arg is string name)
            {
                if (!ActionIds.TryGetValue(name, out int id))
                {
                    try { id = ReInput.mapping.GetActionId(name); } catch { id = -1; }
                    ActionIds[name] = id;
                }
                return id;
            }
            return -1;
        }

        static RPlayer Physical => ReInput.players.GetPlayer(0);

        enum BK { Held, Down, Up, Prev, NegHeld, NegDown }
        enum AK { Value, Prev }

        // ── Test autopilot: replaces this PC's physical input with a scripted pad ──
        public static bool Autoplay;
        static readonly Pad Auto = new Pad();

        public static int ButtonBit(int actionId) => ButtonIndex.TryGetValue(actionId, out int i) ? 1 << i : 0;

        /// <summary>Axes order: Move H, Move V, UI H, UI V.</summary>
        public static void AutoSet(ushort held, float moveX, float moveY, float uiX = 0f, float uiY = 0f)
        {
            Auto.SetLocal(held, new[] { moveX, moveY, uiX, uiY });
        }

        static bool LocalButton(int action, BK k)
        {
            if (Autoplay) return Auto.Button(action, k);
            var p = Physical;
            _bypass++;
            try
            {
                switch (k)
                {
                    case BK.Held: return p.GetButton(action);
                    case BK.Down: return p.GetButtonDown(action);
                    case BK.Up: return p.GetButtonUp(action);
                    case BK.Prev: return p.GetButtonPrev(action);
                    case BK.NegHeld: return p.GetNegativeButton(action);
                    case BK.NegDown: return p.GetNegativeButtonDown(action);
                }
                return false;
            }
            finally { _bypass--; }
        }

        static float LocalAxis(int action, AK k)
        {
            if (Autoplay) return Auto.Axis(action, k);
            var p = Physical;
            _bypass++;
            try { return k == AK.Prev ? p.GetAxisPrev(action) : p.GetAxis(action); }
            finally { _bypass--; }
        }

        static bool ButtonPrefix(RPlayer p, object arg, BK kind, ref bool result)
        {
            switch (RouteOf(p, out int slot))
            {
                case Route.Original: return true;
                case Route.Neutral: result = false; return false;
                case Route.Local: result = LocalButton(ActionId(arg), kind); return false;
                default: result = Pads[slot].Button(ActionId(arg), kind); return false;
            }
        }

        static bool AxisPrefix(RPlayer p, object arg, AK kind, ref float result)
        {
            switch (RouteOf(p, out int slot))
            {
                case Route.Original: return true;
                case Route.Neutral: result = 0f; return false;
                case Route.Local: result = LocalAxis(ActionId(arg), kind); return false;
                default: result = Pads[slot].Axis(ActionId(arg), kind); return false;
            }
        }

        static bool AnyPrefix(RPlayer p, int kind, ref bool result)
        {
            switch (RouteOf(p, out int slot))
            {
                case Route.Original: return true;
                case Route.Neutral: result = false; return false;
                case Route.Local:
                {
                    if (Autoplay) { result = Auto.Any(kind); return false; }
                    var ph = Physical;
                    _bypass++;
                    try { result = kind == 0 ? ph.GetAnyButton() : kind == 1 ? ph.GetAnyButtonDown() : ph.GetAnyButtonUp(); }
                    finally { _bypass--; }
                    return false;
                }
                default: result = Pads[slot].Any(kind); return false;
            }
        }

        // Harmony entry points (one per kind; __0 = first argument, int or string overload)
        static bool P_Held(RPlayer __instance, object __0, ref bool __result) => ButtonPrefix(__instance, __0, BK.Held, ref __result);
        static bool P_Down(RPlayer __instance, object __0, ref bool __result) => ButtonPrefix(__instance, __0, BK.Down, ref __result);
        static bool P_Up(RPlayer __instance, object __0, ref bool __result) => ButtonPrefix(__instance, __0, BK.Up, ref __result);
        static bool P_Prev(RPlayer __instance, object __0, ref bool __result) => ButtonPrefix(__instance, __0, BK.Prev, ref __result);
        static bool P_NegHeld(RPlayer __instance, object __0, ref bool __result) => ButtonPrefix(__instance, __0, BK.NegHeld, ref __result);
        static bool P_NegDown(RPlayer __instance, object __0, ref bool __result) => ButtonPrefix(__instance, __0, BK.NegDown, ref __result);
        static bool P_Axis(RPlayer __instance, object __0, ref float __result) => AxisPrefix(__instance, __0, AK.Value, ref __result);
        static bool P_AxisPrev(RPlayer __instance, object __0, ref float __result) => AxisPrefix(__instance, __0, AK.Prev, ref __result);
        static bool P_Any(RPlayer __instance, ref bool __result) => AnyPrefix(__instance, 0, ref __result);
        static bool P_AnyDown(RPlayer __instance, ref bool __result) => AnyPrefix(__instance, 1, ref __result);
        static bool P_AnyUp(RPlayer __instance, ref bool __result) => AnyPrefix(__instance, 2, ref __result);

        public static void Patch(Harmony h)
        {
            var t = typeof(RPlayer);
            void Both(string method, string prefix)
            {
                var pm = new HarmonyMethod(AccessTools.Method(typeof(NetInput), prefix));
                foreach (var arg in new[] { typeof(int), typeof(string) })
                {
                    var m = AccessTools.Method(t, method, new[] { arg });
                    if (m == null) { Plugin.Log.LogWarning($"[NetInput] Rewired.Player.{method}({arg.Name}) not found"); continue; }
                    h.Patch(m, prefix: pm);
                }
            }
            Both("GetButton", nameof(P_Held));
            Both("GetButtonDown", nameof(P_Down));
            Both("GetButtonUp", nameof(P_Up));
            Both("GetButtonPrev", nameof(P_Prev));
            Both("GetButtonShortPressDown", nameof(P_Up));
            Both("GetButtonRepeating", nameof(P_Down));
            Both("GetNegativeButton", nameof(P_NegHeld));
            Both("GetNegativeButtonDown", nameof(P_NegDown));
            Both("GetAxis", nameof(P_Axis));
            Both("GetAxisRaw", nameof(P_Axis));
            Both("GetAxisPrev", nameof(P_AxisPrev));
            Both("GetAxisRawPrev", nameof(P_AxisPrev));
            h.Patch(AccessTools.Method(t, "GetAnyButton", Type.EmptyTypes), prefix: new HarmonyMethod(AccessTools.Method(typeof(NetInput), nameof(P_Any))));
            h.Patch(AccessTools.Method(t, "GetAnyButtonDown", Type.EmptyTypes), prefix: new HarmonyMethod(AccessTools.Method(typeof(NetInput), nameof(P_AnyDown))));
            h.Patch(AccessTools.Method(t, "GetAnyButtonUp", Type.EmptyTypes), prefix: new HarmonyMethod(AccessTools.Method(typeof(NetInput), nameof(P_AnyUp))));
        }

        // ── Session hooks ────────────────────────────────────────────────────────

        public static void OnSessionStarted()
        {
            foreach (var p in Pads) p.Reset();
            Sampler.Reset();
            ClaimLocalControllers();
        }

        public static void OnSessionEnded()
        {
            foreach (var p in Pads) p.Reset();
        }

        public static void ClearSlot(int slot)
        {
            if (slot >= 0 && slot < Pads.Length) Pads[slot].Reset();
        }

        /// <summary>One local player per PC: give every local device to Rewired player 0.</summary>
        static void ClaimLocalControllers()
        {
            try
            {
                var p0 = Physical;
                foreach (var joy in ReInput.controllers.Joysticks)
                    if (!p0.controllers.ContainsController(joy)) p0.controllers.AddController(joy, true);
                p0.controllers.hasKeyboard = true;
                p0.controllers.maps.SetMapsEnabled(true, SettingsManager.Rewired_MapID_Keyboard_One);
                p0.controllers.maps.SetMapsEnabled(true, SettingsManager.Rewired_MapID_Keyboard_Two);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[NetInput] ClaimLocalControllers: " + e.Message); }
        }

        // ── Network ──────────────────────────────────────────────────────────────

        public static void Register(Session s) => s.On(Msg.Input, OnInput);

        static void OnInput(int peer, NetDataReader r)
        {
            var s = Session.Instance;
            int slot = r.GetByte();
            if (s.Role == Role.Host)
            {
                if (s.SlotOfPeer(peer) != slot) return; // a client may only drive its own slot
                int start = r.Position;
                Pads[slot].Read(r);
                // relay to the other clients
                var w = new NetDataWriter(true, 32);
                w.Put((byte)Msg.Input); w.Put((byte)slot);
                w.Put(r.RawData, start, r.Position - start);
                s.BroadcastAll(w, false, exceptPeer: peer);
            }
            else if (slot != s.LocalSlot && slot >= 0 && slot < Pads.Length)
            {
                Pads[slot].Read(r);
            }
        }

        /// <summary>Called every frame by the Plugin driver.</summary>
        public static void Tick()
        {
            if (!Session.Active) return;
            var s = Session.Instance;
            var w = Sampler.Sample((byte)s.LocalSlot);
            if (w == null) return;
            if (s.Role == Role.Host) s.BroadcastAll(w, false);
            else s.SendToHost(w, false);
        }

        // ── Local sampling ───────────────────────────────────────────────────────

        sealed class LocalSampler
        {
            ushort _seq;
            readonly byte[] _presses = new byte[ButtonIds.Length];
            int _lastFrame = -1;

            public void Reset() { _seq = 0; Array.Clear(_presses, 0, _presses.Length); }

            public NetDataWriter Sample(byte slot)
            {
                if (_lastFrame == Time.frameCount) return null;
                _lastFrame = Time.frameCount;
                if (!ReInput.isReady) return null;
                var p = Physical;
                ushort held = 0;
                _bypass++;
                try
                {
                    for (int i = 0; i < ButtonIds.Length; i++)
                    {
                        bool h = Autoplay ? Auto.Button(ButtonIds[i], BK.Held) : p.GetButton(ButtonIds[i]);
                        bool d = Autoplay ? Auto.Button(ButtonIds[i], BK.Down) : p.GetButtonDown(ButtonIds[i]);
                        if (h) held |= (ushort)(1 << i);
                        if (d) _presses[i]++;
                    }
                    var w = new NetDataWriter(true, 32);
                    w.Put((byte)Msg.Input);
                    w.Put(slot);
                    w.Put(++_seq);
                    w.Put(held);
                    w.Put(_presses);
                    for (int i = 0; i < AxisIds.Length; i++)
                    {
                        float v = Autoplay ? Auto.Axis(AxisIds[i], AK.Value) : p.GetAxis(AxisIds[i]);
                        w.Put((sbyte)Mathf.RoundToInt(Mathf.Clamp(v, -1f, 1f) * 127f));
                    }
                    return w;
                }
                finally { _bypass--; }
            }
        }

        // ── Remote pad ───────────────────────────────────────────────────────────

        sealed class Pad
        {
            bool _any;
            ushort _seq;
            ushort _latestHeld;
            readonly byte[] _latestPresses = new byte[ButtonIds.Length];
            readonly byte[] _consumed = new byte[ButtonIds.Length];
            readonly float[] _latestAxes = new float[AxisIds.Length];

            ushort _cur, _prev;
            readonly float[] _axes = new float[AxisIds.Length];
            readonly float[] _prevAxes = new float[AxisIds.Length];
            int _frame = -1;

            public void Reset()
            {
                _any = false; _seq = 0; _latestHeld = 0; _cur = _prev = 0; _frame = -1;
                Array.Clear(_latestPresses, 0, _latestPresses.Length);
                Array.Clear(_consumed, 0, _consumed.Length);
                Array.Clear(_latestAxes, 0, _latestAxes.Length);
                Array.Clear(_axes, 0, _axes.Length);
                Array.Clear(_prevAxes, 0, _prevAxes.Length);
            }

            public void Read(NetDataReader r)
            {
                ushort seq = r.GetUShort();
                ushort held = r.GetUShort();
                var presses = new byte[ButtonIds.Length];
                r.GetBytes(presses, presses.Length);
                var axes = new float[AxisIds.Length];
                for (int i = 0; i < axes.Length; i++) axes[i] = r.GetSByte() / 127f;

                if (_any && (short)(seq - _seq) <= 0) return; // old / duplicated frame
                if (!_any) Array.Copy(presses, _consumed, presses.Length); // no phantom presses on first frame
                _any = true;
                _seq = seq;
                _latestHeld = held;
                Array.Copy(presses, _latestPresses, presses.Length);
                Array.Copy(axes, _latestAxes, axes.Length);
            }

            /// <summary>Feed this pad directly (test autopilot) instead of from the network.</summary>
            public void SetLocal(ushort held, float[] axes)
            {
                if (!_any) { _any = true; Array.Copy(_latestPresses, _consumed, _consumed.Length); }
                for (int i = 0; i < ButtonIds.Length; i++)
                {
                    int bit = 1 << i;
                    if ((held & bit) != 0 && (_latestHeld & bit) == 0) _latestPresses[i]++;
                }
                _latestHeld = held;
                Array.Copy(axes, _latestAxes, Math.Min(axes.Length, _latestAxes.Length));
            }

            /// <summary>Advance once per Unity frame so Down/Up edges last exactly one frame.</summary>
            void Advance()
            {
                if (_frame == Time.frameCount) return;
                _frame = Time.frameCount;
                _prev = _cur;
                Array.Copy(_axes, _prevAxes, _axes.Length);
                ushort h = 0;
                for (int i = 0; i < ButtonIds.Length; i++)
                {
                    ushort bit = (ushort)(1 << i);
                    byte pending = (byte)(_latestPresses[i] - _consumed[i]);
                    if (pending > 0)
                    {
                        if ((_prev & bit) == 0) { h |= bit; _consumed[i]++; }
                        // else: release for one frame so the next press produces a Down edge
                    }
                    else if ((_latestHeld & bit) != 0) h |= bit;
                }
                _cur = h;
                Array.Copy(_latestAxes, _axes, _axes.Length);
            }

            bool Held(int idx, bool prev) => ((prev ? _prev : _cur) & (1 << idx)) != 0;

            public bool Button(int action, BK kind)
            {
                Advance();
                if (ButtonIndex.TryGetValue(action, out int b))
                {
                    switch (kind)
                    {
                        case BK.Held: return Held(b, false);
                        case BK.Down: return Held(b, false) && !Held(b, true);
                        case BK.Up: return !Held(b, false) && Held(b, true);
                        case BK.Prev: return Held(b, true);
                        default: return false;
                    }
                }
                if (AxisIndex.TryGetValue(action, out int a))
                {
                    float c = _axes[a], p = _prevAxes[a];
                    switch (kind)
                    {
                        case BK.Held: return c > 0.5f;
                        case BK.Down: return c > 0.5f && p <= 0.5f;
                        case BK.Up: return c <= 0.5f && p > 0.5f;
                        case BK.Prev: return p > 0.5f;
                        case BK.NegHeld: return c < -0.5f;
                        case BK.NegDown: return c < -0.5f && p >= -0.5f;
                    }
                }
                return false;
            }

            public float Axis(int action, AK kind)
            {
                Advance();
                if (AxisIndex.TryGetValue(action, out int a)) return kind == AK.Prev ? _prevAxes[a] : _axes[a];
                if (ButtonIndex.TryGetValue(action, out int b)) return Held(b, kind == AK.Prev) ? 1f : 0f;
                return 0f;
            }

            public bool Any(int kind)
            {
                Advance();
                switch (kind)
                {
                    case 0: return _cur != 0;
                    case 1: return (_cur & ~_prev) != 0;
                    default: return (_prev & ~_cur) != 0;
                }
            }
        }
    }
}
