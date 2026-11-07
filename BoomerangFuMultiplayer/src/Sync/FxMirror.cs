using System;
using System.Collections.Generic;
using System.Reflection;
using BoomerangFuMultiplayer.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace BoomerangFuMultiplayer.Sync
{
    /// <summary>
    /// Passive clients do not run the simulation code (FixedUpdate of players and boomerangs, collision
    /// handlers, dashes): the host decides. That code also plays sounds, particles, camera shakes and
    /// character animations (ricochets, catching a boomerang, disarms, melee blocks, dash dust…), which
    /// were lost on clients. The host records those effects while it runs that code and sends them;
    /// clients play them. Effects the clients already replay themselves (deaths, power-up pick-ups,
    /// falls) are not sent.
    /// </summary>
    public static class FxMirror
    {
        enum Kind : byte { Audio, Pfx, Shake, Anim }

        static int _simDepth;      // host: inside code that clients do not run
        static int _suppressDepth; // host: inside code that clients replay by other means
        static int _audioDepth;    // host: nested audio helpers forward only the outermost call
        static bool _applying;     // client: playing a received effect
        static float _windowStart;
        static int _windowCount;
        const int MaxPerSecond = 150;

        static bool Recording => _simDepth > 0 && _suppressDepth == 0 && !_applying && Session.IsHost && Session.Instance != null;

        // ── Patches ──────────────────────────────────────────────────────────────

        public static void Patch(Harmony h)
        {
            HarmonyMethod M(string name) => new HarmonyMethod(typeof(FxMirror), name);
            void Do(string what, Action a)
            {
                try { a(); } catch (Exception e) { Plugin.Log.LogError($"[FxMirror] patch {what} failed: {e.Message}"); }
            }

            // context: the same code the passive clients skip, plus dashes (input-driven on the host)
            var sim = new HashSet<string>
            {
                "CollisionEnter2D", "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D",
                "OnDamageColliderTriggered", "OnDamageColliderStay", "OnMeleeColliderTriggered",
                "OnDisarmColliderTriggered", "OnShieldColliderTriggered", "OnShieldColliderStay",
                "OnPickupColliderTriggered", "OnPickupColliderStay",
            };
            var methods = new List<MethodBase>();
            foreach (var t in new[] { typeof(Actor), typeof(Player), typeof(Projectile), typeof(Disc) })
                foreach (var m in AccessTools.GetDeclaredMethods(t))
                    if (!m.IsAbstract && (sim.Contains(m.Name) || (m.Name == "FixedUpdate" && t != typeof(Actor))))
                        methods.Add(m);
            var dash = AccessTools.Method(typeof(Player), nameof(Player.Dash), Type.EmptyTypes);
            if (dash != null) methods.Add(dash);
            foreach (var m in methods)
                Do(m.DeclaringType.Name + "." + m.Name, () => h.Patch(m, prefix: M(nameof(SimEnter)), finalizer: M(nameof(SimExit))));

            // replayed on clients by their own messages: don't send their effects twice
            foreach (var m in new MethodBase[]
            {
                AccessTools.Method(typeof(Player), nameof(Player.Die)),
                AccessTools.Method(typeof(Player), "CollectPowerup"),
                AccessTools.Method(typeof(Actor), "StartFall"),
                AccessTools.Method(typeof(GameManager), nameof(GameManager.RespawnPlayer)),
            })
                if (m != null) Do(m.Name, () => h.Patch(m, prefix: M(nameof(SuppressEnter)), finalizer: M(nameof(SuppressExit))));

            // the effects themselves
            var am = typeof(AudioManager);
            Do("audio1", () => h.Patch(AccessTools.Method(am, nameof(AudioManager.PlayOneShotAt)), prefix: M(nameof(AudioAt)), finalizer: M(nameof(AudioExit))));
            Do("audio2", () => h.Patch(AccessTools.Method(am, nameof(AudioManager.PlayOneShot), new[] { typeof(string) }), prefix: M(nameof(Audio2D)), finalizer: M(nameof(AudioExit))));
            Do("audio3", () => h.Patch(AccessTools.Method(am, nameof(AudioManager.PlayOneShotAttached)), prefix: M(nameof(AudioAttached)), finalizer: M(nameof(AudioExit))));
            Do("audio4", () => h.Patch(AccessTools.Method(am, nameof(AudioManager.PlayOneShotAndSetParameter)), prefix: M(nameof(AudioParam)), finalizer: M(nameof(AudioExit))));
            Do("audio5", () => h.Patch(AccessTools.Method(am, nameof(AudioManager.PlayOneShotAndSetParameterAt)), prefix: M(nameof(AudioParamAt)), finalizer: M(nameof(AudioExit))));
            Do("pfx1", () => h.Patch(AccessTools.Method(typeof(ParticleManager), "SpawnPFX"), prefix: M(nameof(PfxPrefix))));
            Do("pfx2", () => h.Patch(AccessTools.Method(typeof(ParticleManager), "SpawnPFXWithColor"), prefix: M(nameof(PfxColorPrefix))));
            Do("shake", () => h.Patch(AccessTools.Method(typeof(Camera3D), nameof(Camera3D.Shake)), prefix: M(nameof(ShakePrefix))));
            Do("anim", () => h.Patch(AccessTools.Method(typeof(Animator), nameof(Animator.SetTrigger), new[] { typeof(string) }), prefix: M(nameof(AnimPrefix))));
        }

        public static void Register(Session s) => s.On(Msg.Fx, OnFx);

        static void SimEnter() { if (Session.IsHost) _simDepth++; }
        static Exception SimExit(Exception __exception) { if (Session.IsHost && _simDepth > 0) _simDepth--; return __exception; }
        static void SuppressEnter() { if (Session.IsHost) _suppressDepth++; }
        static Exception SuppressExit(Exception __exception) { if (Session.IsHost && _suppressDepth > 0) _suppressDepth--; return __exception; }
        static Exception AudioExit(Exception __exception) { if (_audioDepth > 0) _audioDepth--; return __exception; }

        // ── Host: capture ────────────────────────────────────────────────────────

        static void AudioAt(string eventName, Vector3 pos) => SendAudio(eventName, pos, true, null, 0f);
        static void Audio2D(string eventName) => SendAudio(eventName, Vector3.zero, false, null, 0f);
        static void AudioAttached(string eventName, GameObject go) =>
            SendAudio(eventName, go != null ? go.transform.position : Vector3.zero, go != null, null, 0f);
        static void AudioParam(string eventName, string parameterName, float value, Transform tf) =>
            SendAudio(eventName, tf != null ? tf.position : Vector3.zero, tf != null, parameterName, value);
        static void AudioParamAt(string eventName, string parameterName, float value, Vector3 position) =>
            SendAudio(eventName, position, true, parameterName, value);

        static void SendAudio(string name, Vector3 pos, bool hasPos, string param, float value)
        {
            bool outer = _audioDepth == 0;
            _audioDepth++;
            if (!outer || !Recording || string.IsNullOrEmpty(name) || !Budget()) return;
            var w = Begin(Kind.Audio);
            w.Put(name);
            w.Put(hasPos);
            if (hasPos) { w.Put(pos.x); w.Put(pos.y); w.Put(pos.z); }
            w.Put(param ?? "");
            w.Put(value);
            Send(w);
        }

        static void PfxPrefix(PFXType type, Vector3 position, Quaternion rotation) => SendPfx(type, position, rotation, null);
        static void PfxColorPrefix(PFXType type, Vector3 position, Color color, Quaternion rotation) => SendPfx(type, position, rotation, color);

        static void SendPfx(PFXType type, Vector3 pos, Quaternion rot, Color? color)
        {
            if (!Recording || !Budget()) return;
            var w = Begin(Kind.Pfx);
            w.Put((int)type);
            w.Put(pos.x); w.Put(pos.y); w.Put(pos.z);
            w.Put(rot.x); w.Put(rot.y); w.Put(rot.z); w.Put(rot.w);
            w.Put(color.HasValue);
            if (color.HasValue) { var c = color.Value; w.Put(c.r); w.Put(c.g); w.Put(c.b); w.Put(c.a); }
            Send(w);
        }

        static void ShakePrefix(float intensity, float duration)
        {
            if (!Recording || !Budget()) return;
            var w = Begin(Kind.Shake);
            w.Put(intensity); w.Put(duration);
            Send(w);
        }

        static void AnimPrefix(Animator __instance, string name)
        {
            if (!Recording || __instance == null || string.IsNullOrEmpty(name)) return;
            var gm = Game.Get<GameManager>();
            if (gm == null) return;
            foreach (var p in gm.players)
            {
                if (p == null || p.character == null || p.character.anim != __instance) continue;
                if (!Budget()) return;
                var w = Begin(Kind.Anim);
                w.Put((byte)p.playerID);
                w.Put(name);
                Send(w);
                return;
            }
        }

        static bool Budget()
        {
            float now = Time.unscaledTime;
            if (now - _windowStart > 1f) { _windowStart = now; _windowCount = 0; }
            return ++_windowCount <= MaxPerSecond;
        }

        static NetDataWriter Begin(Kind k)
        {
            var w = Protocol.Begin(Msg.Fx);
            w.Put((byte)k);
            return w;
        }

        static void Send(NetDataWriter w) => MatchSync.BroadcastToPlaying(w, false);

        // ── Client: play ─────────────────────────────────────────────────────────

        static readonly int[] _received = new int[4];
        static float _nextReport;

        static void OnFx(int peer, NetDataReader r)
        {
            if (!Session.IsClient) return;
            var kind = (Kind)r.GetByte();
            if ((int)kind < _received.Length) _received[(int)kind]++;
            if (Plugin.Diag && Time.unscaledTime > _nextReport)
            {
                _nextReport = Time.unscaledTime + 10f;
                Plugin.Log.LogInfo($"[Diag] fx received: audio {_received[0]}, pfx {_received[1]}, shake {_received[2]}, anim {_received[3]}");
            }
            _applying = true;
            try
            {
                switch (kind)
                {
                    case Kind.Audio:
                    {
                        string name = r.GetString();
                        bool hasPos = r.GetBool();
                        var pos = hasPos ? new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()) : Vector3.zero;
                        string param = r.GetString();
                        float value = r.GetFloat();
                        var am = Game.Get<AudioManager>();
                        if (am == null) return;
                        if (param.Length > 0 && hasPos) am.PlayOneShotAndSetParameterAt(name, param, value, pos);
                        else if (param.Length > 0) am.PlayOneShotAndSetParameter(name, param, value);
                        else if (hasPos) am.PlayOneShotAt(name, pos);
                        else am.PlayOneShot(name);
                        break;
                    }
                    case Kind.Pfx:
                    {
                        var type = (PFXType)r.GetInt();
                        var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                        var rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
                        bool hasColor = r.GetBool();
                        var pm = Game.Get<ParticleManager>();
                        if (pm == null) return;
                        if (hasColor) pm.PlayPFX(type, pos, new Color(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat()), null, rot);
                        else pm.PlayPFX(type, pos, null, rot);
                        break;
                    }
                    case Kind.Shake:
                    {
                        float intensity = r.GetFloat(), duration = r.GetFloat();
                        Game.Get<Camera3D>()?.Shake(intensity, duration);
                        break;
                    }
                    case Kind.Anim:
                    {
                        int id = r.GetByte();
                        string trigger = r.GetString();
                        var gm = Game.Get<GameManager>();
                        if (gm == null) return;
                        foreach (var p in gm.players)
                            if (p != null && p.playerID == id && p.character != null && p.character.anim != null && p.character.anim.isActiveAndEnabled)
                            { p.character.anim.SetTrigger(trigger); break; }
                        break;
                    }
                }
            }
            catch (Exception e) { if (Plugin.Diag) Plugin.Log.LogWarning("[FxMirror] " + e.Message); }
            finally { _applying = false; }
        }
    }
}
