using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Moments;
using UnityEngine;

namespace BoomerangFuMultiplayer.Debugging
{
    /// <summary>
    /// Diagnostics for the round-end replay (the scoreboard plays the last seconds recorded by the
    /// camera's Moments.Recorder). Logs every state change of the recorder and the frame count shown
    /// at round end. Only with -bfump-diag / -bfump-autoplay.
    /// </summary>
    internal static class ReplayDiag
    {
        public static void Patch(Harmony h)
        {
            var post = new HarmonyMethod(AccessTools.Method(typeof(ReplayDiag), nameof(RecorderPostfix)));
            foreach (var name in new[] { "Record", "Pause", "FlushMemory" })
            {
                var m = AccessTools.Method(typeof(Recorder), name);
                if (m != null) h.Patch(m, postfix: post);
            }
            var play = AccessTools.Method(typeof(UIMenuRoundEnd), "PlayReplay");
            if (play != null) h.Patch(play, postfix: new HarmonyMethod(AccessTools.Method(typeof(ReplayDiag), nameof(PlayReplayPostfix))));
            var init = AccessTools.Method(typeof(UIMenuRoundEnd), nameof(UIMenuRoundEnd.Init));
            if (init != null) h.Patch(init, postfix: new HarmonyMethod(AccessTools.Method(typeof(ReplayDiag), nameof(RoundEndPostfix))));
        }

        static void RecorderPostfix(Recorder __instance, MethodBase __originalMethod)
        {
            if (!Plugin.Diag) return;
            var gm = Game.Get<GameManager>();
            Plugin.Log.LogInfo($"[Diag] recorder {__originalMethod.Name}: state {__instance.State}, frames {__instance.m_Frames?.Count ?? -1}, game {gm?.gameState} round {gm?.roundNumber}");
        }

        /// <summary>Saves the middle frame of the replay the scoreboard is about to play (replay-frame-r[round].png in the BepInEx folder).</summary>
        static void PlayReplayPostfix(UIMenuRoundEnd __instance)
        {
            if (!Plugin.Diag) return;
            try
            {
                var frames = Traverse.Create(__instance).Field("replayFrames").GetValue<List<RenderTexture>>();
                bool playing = Traverse.Create(__instance).Field("replayIsPlaying").GetValue<bool>();
                var gm = Game.Get<GameManager>();
                Plugin.Log.LogInfo($"[Diag] replay play: frames {frames?.Count ?? -1}, playing {playing}, round {gm?.roundNumber}");
                if (frames == null || frames.Count == 0) return;
                var rt = frames[frames.Count / 2];
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                string path = Path.Combine(BepInEx.Paths.BepInExRootPath, $"replay-frame-r{gm?.roundNumber}.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.Destroy(tex);
                Plugin.Log.LogInfo($"[Diag] replay frame saved: {path} ({rt.width}x{rt.height}, created {rt.IsCreated()})");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Diag] replay frame: " + e.Message); }
        }

        static void RoundEndPostfix()
        {
            if (!Plugin.Diag) return;
            var rec = Game.Get<Camera3D>()?.GetComponent<Recorder>();
            var gm = Game.Get<GameManager>();
            Plugin.Log.LogInfo($"[Diag] round end screen: recorder state {rec?.State}, frames {rec?.m_Frames?.Count ?? -1}, tutorial {Game.Get<LevelManager>()?.level?.isTutorialLevel}, round {gm?.roundNumber}");
        }
    }
}
