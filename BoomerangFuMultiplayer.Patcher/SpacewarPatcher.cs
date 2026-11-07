using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace BoomerangFuMultiplayer.Patcher
{
    /// <summary>
    /// Option "Steam.UseSpacewar" of the multiplayer mod. The game initialises Steam (SteamManager.Awake)
    /// before any BepInEx plugin loads, so the switch happens here, in the preloader:
    ///  - steam_appid.txt = 480 next to the exe, so SteamAPI.RestartAppIfNecessary(965680) never relaunches;
    ///  - SteamAppId / SteamGameId = 480 in the process environment, which wins over the 965680 that Steam
    ///    sets when it launches the game.
    /// Steam then sees the game as Spacewar (AppID 480): its lobbies, P2P relay and invites work for any
    /// account. Turning the option off removes the file again (only if it holds 480).
    /// </summary>
    public static class SpacewarPatcher
    {
        public const string SpacewarAppId = "480";
        static readonly ManualLogSource Log = Logger.CreateLogSource("BFUMP Spacewar");

        // BepInEx 5 patcher contract: nothing to rewrite, only Initialize() matters.
        public static IEnumerable<string> TargetDLLs { get; } = new string[0];
        public static void Patch(AssemblyDefinition assembly) { }

        public static void Initialize()
        {
            try
            {
                string cfg = Path.Combine(Paths.ConfigPath, "com.bfump.multiplayer.cfg");
                bool enabled = IsEnabled(File.Exists(cfg) ? File.ReadAllText(cfg) : null);
                string appIdFile = Path.Combine(Paths.GameRootPath, "steam_appid.txt");
                if (enabled)
                {
                    File.WriteAllText(appIdFile, SpacewarAppId);
                    Environment.SetEnvironmentVariable("SteamAppId", SpacewarAppId);
                    Environment.SetEnvironmentVariable("SteamGameId", SpacewarAppId);
                    Log.LogInfo("Steam: the game will present itself as Spacewar (AppID 480)");
                }
                else if (File.Exists(appIdFile) && File.ReadAllText(appIdFile).Trim() == SpacewarAppId)
                {
                    File.Delete(appIdFile);
                    Log.LogInfo("Steam: Spacewar disabled, steam_appid.txt removed");
                }
            }
            catch (Exception e) { Log.LogError("Spacewar switch failed: " + e.Message); }
        }

        /// <summary>Reads "UseSpacewar = true|false" from the plugin's config text; on by default.</summary>
        public static bool IsEnabled(string cfgText)
        {
            if (string.IsNullOrEmpty(cfgText)) return true;
            var m = Regex.Match(cfgText, @"^\s*UseSpacewar\s*=\s*(\w+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return !m.Success || !m.Groups[1].Value.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
    }
}
