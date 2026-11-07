using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BoomerangFuMultiplayer
{
    public sealed class GameSync : MonoBehaviour
    {
        public static GameSync Instance { get; private set; }

        static readonly FieldInfo FGMPlayers    = AccessTools.Field(typeof(GameManager), "players");
        static readonly FieldInfo FGMGameState  = AccessTools.Field(typeof(GameManager), "gameState");
        static readonly FieldInfo FGMRound      = AccessTools.Field(typeof(GameManager), "roundNumber");
        static readonly FieldInfo FGMRoundTimer = AccessTools.Field(typeof(GameManager), "roundTimer");
        static readonly FieldInfo FGMMatchTimer = AccessTools.Field(typeof(GameManager), "matchTimer");

        // Disc/Projectile fields (accessed by name — classes discovered from Assembly-CSharp)
        static readonly FieldInfo FDiscVelocity = AccessTools.Field(typeof(Disc), "velocity");
        static readonly FieldInfo FDiscMovement = AccessTools.Field(typeof(Disc), "movement");

        private GameManager      _gm;
        private List<Player>     _players;
        private List<Rigidbody2D>_rbs = new List<Rigidbody2D>();
        private Coroutine        _broadcast;

        void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void OnEnable()  => SceneManager.sceneLoaded += OnScene;
        void OnDisable() => SceneManager.sceneLoaded -= OnScene;

        void OnScene(Scene s, LoadSceneMode _)
        {
            _gm = null; _players = null; _rbs.Clear();
            InputPatch.ClearCache();
        }

        public void StartBroadcasting()
        {
            if (_broadcast != null) StopCoroutine(_broadcast);
            _broadcast = StartCoroutine(BroadcastLoop());
        }

        // ─── HOST: broadcast at 30 Hz ─────────────────────────────────────────

        IEnumerator BroadcastLoop()
        {
            var wait = new WaitForSecondsRealtime(1f / 30f);
            while (NetLayer.Instance?.IsHost == true)
            {
                SendState();
                yield return wait;
            }
            _broadcast = null;
        }

        void SendState()
        {
            if (!RefreshPlayers()) return;

            var msg = new StateMsg();

            // Game state
            if (_gm != null)
            {
                msg.gameState   = (int)FGMGameState.GetValue(_gm);
                msg.roundNumber = (int)FGMRound.GetValue(_gm);
                msg.roundTimer  = (float)FGMRoundTimer.GetValue(_gm);
                msg.matchTimer  = (float)FGMMatchTimer.GetValue(_gm);
            }

            // Players — indexed by playerID for stable identity
            msg.players = new PlayerSnapshot[_players.Count];
            for (int i = 0; i < _players.Count; i++)
            {
                var p = _players[i];
                if (p == null) continue;
                var rb = i < _rbs.Count ? _rbs[i] : null;
                msg.players[i] = new PlayerSnapshot
                {
                    playerId   = ((Actor)p).playerID,
                    x          = p.transform.position.x,
                    y          = p.transform.position.y,
                    vx         = rb != null ? rb.velocity.x : 0f,
                    vy         = rb != null ? rb.velocity.y : 0f,
                    rotZ       = p.transform.eulerAngles.z,
                    actorState = (int)((Actor)p).actorState,
                    active     = p.gameObject.activeSelf,
                };
            }

            // Discs — sorted by instance ID for deterministic order
            var discObjs = FindObjectsOfType<Disc>();
            Array.Sort(discObjs, (a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            msg.discs = new DiscSnapshot[discObjs.Length];
            for (int i = 0; i < discObjs.Length; i++)
            {
                var d = discObjs[i];
                var v = (Vector2)(FDiscVelocity?.GetValue(d) ?? Vector2.zero);
                msg.discs[i] = new DiscSnapshot
                {
                    x        = d.transform.position.x,
                    y        = d.transform.position.y,
                    vx       = v.x,
                    vy       = v.y,
                    rotZ     = d.transform.eulerAngles.z,
                    movement = (int)(FDiscMovement?.GetValue(d) ?? 0),
                };
            }

            NetLayer.Instance.BroadcastState(msg);

            // Disable AI for connected client slots (re-checked each broadcast tick)
            DisableAIForClients();
        }

        void DisableAIForClients()
        {
            var net = NetLayer.Instance;
            if (net == null || !net.IsHost || _players == null) return;
            for (int i = 0; i < _players.Count; i++)
            {
                var p = _players[i];
                if (p == null) continue;
                int pid = ((Actor)p).playerID;
                if (pid >= 1 && pid <= 3 && net.IsSlotConnected(pid))
                {
                    var ai = p.GetComponent<PlayerAI>();
                    if (ai != null && ai.enabled) ai.enabled = false;
                }
            }
        }

        // ─── CLIENT: apply received state in FixedUpdate ──────────────────────

        void FixedUpdate()
        {
            var net = NetLayer.Instance;
            if (net == null || !net.IsClient) return;

            var state = net.LatestState;
            if (state == null) return;

            var msg = state.Value;
            ApplyGameState(msg);
            ApplyPlayerStates(msg);
            ApplyDiscStates(msg);
        }

        void ApplyGameState(StateMsg msg)
        {
            if (!RefreshPlayers()) return;
            if (_gm == null) return;
            FGMRoundTimer.SetValue(_gm, msg.roundTimer);
            FGMMatchTimer.SetValue(_gm, msg.matchTimer);
            // GameState and roundNumber intentionally not overridden — let local game manage transitions
        }

        void ApplyPlayerStates(StateMsg msg)
        {
            if (msg.players == null || !RefreshPlayers()) return;
            foreach (var snap in msg.players)
            {
                // Find player by playerID for stable matching
                for (int i = 0; i < _players.Count; i++)
                {
                    var p = _players[i];
                    if (p == null) continue;
                    if (((Actor)p).playerID != snap.playerId) continue;

                    var rb = i < _rbs.Count ? _rbs[i] : null;
                    var pos = new Vector2(snap.x, snap.y);
                    if (rb != null)
                    {
                        rb.MovePosition(pos);
                        rb.velocity = new Vector2(snap.vx, snap.vy);
                    }
                    else
                    {
                        p.transform.position = new Vector3(pos.x, pos.y, p.transform.position.z);
                    }
                    var e = p.transform.eulerAngles;
                    p.transform.eulerAngles = new Vector3(e.x, e.y, snap.rotZ);
                    break;
                }
            }
        }

        void ApplyDiscStates(StateMsg msg)
        {
            if (msg.discs == null || msg.discs.Length == 0) return;
            var discObjs = FindObjectsOfType<Disc>();
            Array.Sort(discObjs, (a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            int count = Math.Min(discObjs.Length, msg.discs.Length);
            for (int i = 0; i < count; i++)
            {
                var d = discObjs[i];
                var snap = msg.discs[i];
                d.transform.position = new Vector3(snap.x, snap.y, d.transform.position.z);
                FDiscVelocity?.SetValue(d, new Vector2(snap.vx, snap.vy));
                FDiscMovement?.SetValue(d, snap.movement);
                var e = d.transform.eulerAngles;
                d.transform.eulerAngles = new Vector3(e.x, e.y, snap.rotZ);
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        bool RefreshPlayers()
        {
            if (_gm == null) _gm = FindObjectOfType<GameManager>();
            if (_gm == null) return false;

            var list = FGMPlayers?.GetValue(_gm) as List<Player>;
            if (list == null || list.Count == 0) return false;

            if (list != _players)
            {
                _players = list;
                _rbs.Clear();
                foreach (var p in _players)
                    _rbs.Add(p != null ? p.GetComponent<Rigidbody2D>() : null);
            }
            return true;
        }
    }
}
