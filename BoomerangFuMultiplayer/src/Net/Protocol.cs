using LiteNetLib.Utils;

namespace BoomerangFuMultiplayer.Net
{
    /// <summary>Every packet starts with one of these bytes.</summary>
    public enum Msg : byte
    {
        Hello = 1,          // client -> host : protocol, mod version, player name
        Welcome = 2,        // host -> client : assigned slot
        Reject = 3,         // host -> client : reason
        Roster = 4,         // host -> all    : who is in which slot
        Input = 5,          // owner -> host -> others : one input frame for a slot
        UiSelect = 6,       // host -> clients : selected UI object path
        UiPress = 7,        // host -> clients : button pressed (path)
        MenuStack = 8,      // host -> clients : current menu stack (bottom -> top)
        LobbyState = 9,     // host -> clients : lobby players (character, ready, team...)
        MatchStart = 10,    // host -> clients : seed, settings, roster, playlist
        Snapshot = 11,      // host -> clients : authoritative player states
        Death = 12,         // host -> clients : a player died
        PowerupSpawn = 13,  // host -> clients
        PowerupCollect = 14,// host -> clients
        Pause = 15,         // host -> clients : pause driven by slot
        Bye = 16,           // any : leaving
        RoundEnd = 17,      // host -> clients : the round is over
        Respawn = 18,       // host -> clients : a player respawns
        TakeOver = 19,      // host -> clients : a late joiner replaces the bot of its slot
        LateJoin = 20,      // host -> late joiner : everything needed to enter the running match
        RoundReady = 21,    // client -> host : next round loaded, waiting for the start signal
        RoundStart = 22,    // host -> clients : start the round now
        AddPlayer = 23,     // host -> clients : a late joiner gets a new player in the running match
        Fx = 24,            // host -> clients : sound / particles / shake / animation from code clients don't run
    }

    public static class Protocol
    {
        public const int Version = 6;
        public const int MaxSlots = 4;

        public static NetDataWriter Begin(Msg type)
        {
            var w = new NetDataWriter(true, 64);
            w.Put((byte)type);
            return w;
        }
    }
}
