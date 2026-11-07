using HarmonyLib;
using System.Reflection;
using UnityEngine;

namespace BoomerangFuMultiplayer
{
    // POSTFIX on PlayerInput.Update — runs AFTER Rewired reads AND after PlayerAI sets input.
    // Our network input always overwrites. Also resets inactivity counters so the game never
    // treats a remote player as idle/disconnected.
    [HarmonyPatch(typeof(PlayerInput), "Update")]
    internal static class InputPatch
    {
        // No cache needed — playerID is a stable identity on the Actor base class.
        public static void ClearCache() { } // kept so GameSync.OnScene compiles

        static void Postfix(PlayerInput __instance)
        {
            var net = NetLayer.Instance;
            if (net == null) return;

            var player = __instance.player;
            if ((Object)(object)player == (Object)null) return;

            int pid = ((Actor)player).playerID;

            if (net.IsHost && pid >= 1 && pid <= 3 && net.HasInput[pid])
            {
                ApplyRemoteInput(__instance, net.RemoteInput[pid]);
            }
            else if (net.IsClient && pid == net.LocalSlot)
            {
                // Capture Rewired input and send to host
                net.SendInput(new InputMsg
                {
                    moveX     = __instance.inputMove.x,
                    moveY     = __instance.inputMove.y,
                    dash      = __instance.inputDash,
                    attack    = __instance.inputAttack,
                    throwBtn  = __instance.inputThrow,
                    throwHeld = __instance.inputThrowHeld,
                    wave      = __instance.inputWave,
                });
            }
        }

        static void ApplyRemoteInput(PlayerInput pi, InputMsg inp)
        {
            pi.inputMove                    = new Vector2(inp.moveX, inp.moveY);
            pi.inputDash                    = inp.dash;
            pi.inputAttack                  = inp.attack;
            pi.inputThrow                   = inp.throwBtn;
            pi.inputThrowHeld               = inp.throwHeld;
            pi.inputWave                    = inp.wave;
            pi.inputIsEnabled               = true;   // prevents idle-player AI takeover
            pi.roundsSinceLastInputReceived = 0;
            pi.timeSinceLastInputReceived   = 0f;
        }
    }

    // Blocks PlayerAI from overwriting input for network-controlled slots.
    // Uses playerID via Actor base class (same approach as BoomerangFuOnline by Codex).
    [HarmonyPatch(typeof(PlayerAI), "UpdateBehaviour")]
    internal static class AIBlockPatch
    {
        static readonly FieldInfo FAIPlayer =
            AccessTools.Field(typeof(PlayerAI), "player");

        static bool Prefix(PlayerAI __instance)
        {
            var net = NetLayer.Instance;
            if (net == null || !net.IsHost) return true;

            var player = FAIPlayer.GetValue(__instance) as Player;
            if ((Object)(object)player == (Object)null) return true;

            int pid = ((Actor)player).playerID;
            // Block AI if a client owns this slot and has sent at least one frame
            return !(pid >= 1 && pid <= 3 && net.HasInput[pid]);
        }
    }
}
