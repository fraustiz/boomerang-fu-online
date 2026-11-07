using BoomerangFuMultiplayer.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace BoomerangFuMultiplayer.Sync
{
    /// <summary>
    /// Synchronised round start. Every machine loads the next level on its own; clients then tell
    /// the host they are ready and wait for its signal, and the host waits for everyone (with a
    /// timeout) before starting. Nobody starts early, and a slow or late-joining PC is not left
    /// behind.
    /// </summary>
    public static class RoundSync
    {
        const float RegularTimeout = 6f;
        const float LateJoinTimeout = 25f;

        static bool _releasing;

        // client
        static bool _held, _heldRematch;
        static int _heldRound;
        static int _hostStartedRound = -1, _hostStartedSeed;

        // host
        static bool _hostHeld, _hostHeldRematch, _toasted;
        static float _holdSince, _holdUntil;
        static readonly int[] ReadyRound = new int[Protocol.MaxSlots];
        static readonly int[] ReadySeed = new int[Protocol.MaxSlots];
        /// <summary>Host: a late joiner is loading the match until this time (longer wait).</summary>
        public static float LateLoadingUntil;

        static GameManager GM => Game.Get<GameManager>();

        public static void Register(Session s)
        {
            s.On(Msg.RoundReady, OnRoundReady);
            s.On(Msg.RoundStart, OnRoundStart);
        }

        public static void Patch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.StartRound)),
                prefix: new HarmonyMethod(typeof(RoundSync), nameof(StartRoundPrefix)),
                postfix: new HarmonyMethod(typeof(RoundSync), nameof(StartRoundPostfix)));
        }

        static bool StartRoundPrefix(GameManager __instance, bool rematch, out bool __state)
        {
            __state = true;
            if (!Session.Active || _releasing) return true;
            int round = __instance.roundNumber;
            int seed = MatchSync.MatchSeed;

            if (Session.IsClient)
            {
                if (_hostStartedSeed == seed && _hostStartedRound >= round) return true; // host already started
                _held = true; _heldRound = round; _heldRematch = rematch;
                var w = Protocol.Begin(Msg.RoundReady);
                w.Put(seed); w.Put(round);
                Session.Instance.SendToHost(w, true);
                if (Plugin.Diag) Plugin.Log.LogInfo($"[Diag] round {round} loaded, waiting for the host");
                __state = false;
                return false;
            }

            if (Session.IsHost)
            {
                if (AllReady(round)) return true;
                _hostHeld = true; _hostHeldRematch = rematch; _toasted = false;
                _holdSince = Time.unscaledTime;
                _holdUntil = _holdSince + (Time.unscaledTime < LateLoadingUntil ? LateJoinTimeout : RegularTimeout);
                if (Plugin.Diag) Plugin.Log.LogInfo($"[Diag] round {round}: waiting for players to load");
                __state = false;
                return false;
            }
            return true;
        }

        static void StartRoundPostfix(GameManager __instance, bool __state)
        {
            if (!__state || !Session.IsHost) return;
            var w = Protocol.Begin(Msg.RoundStart);
            w.Put(MatchSync.MatchSeed); w.Put(__instance.roundNumber);
            MatchSync.BroadcastToPlaying(w, true);
            if (Plugin.Diag) Plugin.Log.LogInfo($"[Diag] round {__instance.roundNumber} start sent");
        }

        static bool AllReady(int round)
        {
            var s = Session.Instance;
            for (int i = 1; i < s.Slots.Length; i++)
            {
                if (!s.Slots[i].Used || s.Slots[i].Pending) continue;
                if (ReadySeed[i] != MatchSync.MatchSeed || ReadyRound[i] < round) return false;
            }
            return true;
        }

        static void OnRoundReady(int peer, NetDataReader r)
        {
            int seed = r.GetInt(), round = r.GetInt();
            int slot = Session.Instance.SlotOfPeer(peer);
            if (slot < 0) return;
            ReadySeed[slot] = seed;
            ReadyRound[slot] = round;
        }

        static void OnRoundStart(int peer, NetDataReader r)
        {
            _hostStartedSeed = r.GetInt();
            _hostStartedRound = r.GetInt();
        }

        public static void Tick()
        {
            var gm = GM;
            if (gm == null) return;

            if (Session.IsClient && _held && _hostStartedSeed == MatchSync.MatchSeed && _hostStartedRound >= _heldRound)
            {
                _held = false;
                Release(gm, _heldRematch);
            }

            if (Session.IsHost && _hostHeld)
            {
                bool ready = AllReady(gm.roundNumber);
                if (ready || Time.unscaledTime > _holdUntil)
                {
                    _hostHeld = false;
                    if (!ready) Plugin.Log.LogWarning("[RoundSync] some players did not finish loading in time, starting anyway");
                    if (Plugin.Diag) Plugin.Log.LogInfo($"[Diag] round {gm.roundNumber} released after {Time.unscaledTime - _holdSince:0.0}s (all ready: {ready})");
                    Release(gm, _hostHeldRematch);
                }
                else if (!_toasted && Time.unscaledTime - _holdSince > 1.5f)
                {
                    _toasted = true;
                    Session.Instance.Notify("En attente des autres joueurs…");
                }
            }
        }

        static void Release(GameManager gm, bool rematch)
        {
            _releasing = true;
            try { gm.StartRound(rematch); }
            finally { _releasing = false; }
        }

        public static void Reset()
        {
            _held = _hostHeld = false;
            _hostStartedRound = -1;
            for (int i = 0; i < ReadyRound.Length; i++) { ReadyRound[i] = -1; ReadySeed[i] = 0; }
        }
    }
}
