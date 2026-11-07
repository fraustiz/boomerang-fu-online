using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using Steamworks;
using UnityEngine;

namespace BoomerangFuMultiplayer.Net
{
    /// <summary>
    /// Internet play through Steam, no server and no port to open: Steam P2P packets (NAT
    /// traversal, Steam relay when needed). The host accepts its Steam friends directly; friends
    /// find it through rich presence ("Join game" in the friends list, in-game invites) or the
    /// mod's own friend list. A Steam lobby is also created when Steam allows it (overlay invite
    /// dialog), but nothing depends on it: some accounts are refused lobbies.
    /// </summary>
    public sealed class SteamTransport : ITransport
    {
        public const string LobbyKey = "bfump";
        public const string HostKey = "bfump_host";        // rich presence: protocol version while hosting
        public const string JoinArg = "+bfump_join";       // command line / connect string: +bfump_join <steamid64>
        const int Channel = 0;
        const float PeerTimeout = 12f;

        public bool IsHost { get; }
        public bool IsConnected => IsHost || _hostReady;
        public string Kind => "Steam";
        public CSteamID Lobby { get; private set; }

        public event Action<int, NetDataReader> Data;
        public event Action<int> PeerJoined;
        public event Action<int> PeerLeft;
        public event Action Connected;
        public event Action<string> Disconnected;

        public event Action LobbyReady;
        public event Action<string> Failed;

        readonly Dictionary<ulong, int> _idOf = new Dictionary<ulong, int>();
        readonly Dictionary<int, CSteamID> _steamOf = new Dictionary<int, CSteamID>();
        readonly Dictionary<int, float> _lastSeen = new Dictionary<int, float>();
        int _nextPeer = 1;
        CSteamID _host;
        float _hostLastSeen;
        bool _hostReady, _hostEverHeard;
        bool _closed;
        byte[] _buf = new byte[4096];

        readonly Callback<LobbyEnter_t> _cbEnter;
        readonly Callback<LobbyChatUpdate_t> _cbChat;
        readonly Callback<P2PSessionRequest_t> _cbP2PReq;
        readonly Callback<P2PSessionConnectFail_t> _cbP2PFail;
        readonly CallResult<LobbyCreated_t> _crCreated;

        SteamTransport(bool host)
        {
            IsHost = host;
            _cbEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            _cbChat = Callback<LobbyChatUpdate_t>.Create(OnLobbyChat);
            _cbP2PReq = Callback<P2PSessionRequest_t>.Create(OnP2PRequest);
            _cbP2PFail = Callback<P2PSessionConnectFail_t>.Create(OnP2PFail);
            _crCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        }

        public static bool Available
        {
            get
            {
                try { return SteamManager.Initialized; }
                catch { return false; }
            }
        }

        public static CSteamID Me => SteamUser.GetSteamID();

        // ── Start ────────────────────────────────────────────────────────────────

        public static SteamTransport Host()
        {
            var t = new SteamTransport(true);
            SteamNetworking.AllowP2PPacketRelay(true);
            // Friends can join right away through rich presence; the lobby is optional.
            SteamFriends.SetRichPresence(HostKey, Protocol.Version.ToString());
            SteamFriends.SetRichPresence("connect", JoinArg + " " + Me.m_SteamID);
            SteamFriends.SetRichPresence("status", "Héberge une partie en ligne");
            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, Protocol.MaxSlots);
            t._crCreated.Set(call);
            Plugin.Log.LogInfo($"[Steam] hosting as {Me.m_SteamID} (friends join by P2P), creating optional lobby…");
            return t;
        }

        /// <summary>Join a host directly by its SteamID (no lobby involved).</summary>
        public static SteamTransport JoinHost(CSteamID host)
        {
            var t = new SteamTransport(false);
            SteamNetworking.AllowP2PPacketRelay(true);
            t._host = host;
            t._hostReady = true;
            t._hostLastSeen = Time.unscaledTime;
            SteamFriends.SetRichPresence("status", "En partie en ligne");
            Plugin.Log.LogInfo($"[Steam] joining host {host.m_SteamID} by P2P");
            return t;
        }

        /// <summary>Join through a lobby id (overlay invites). Falls back to the owner's P2P when possible.</summary>
        public static SteamTransport Join(CSteamID lobby)
        {
            var t = new SteamTransport(false);
            SteamNetworking.AllowP2PPacketRelay(true);
            SteamMatchmaking.JoinLobby(lobby);
            Plugin.Log.LogInfo($"[Steam] joining lobby {lobby.m_SteamID}");
            return t;
        }

        /// <summary>Call once the session is listening, so the client says hello.</summary>
        public void Start()
        {
            if (!IsHost && _hostReady) Connected?.Invoke();
        }

        // ── Steam callbacks ──────────────────────────────────────────────────────

        void OnLobbyCreated(LobbyCreated_t r, bool ioFailure)
        {
            if (_closed) return;
            if (ioFailure || r.m_eResult != EResult.k_EResultOK)
            {
                // Not fatal: friends still join through P2P / rich presence.
                Plugin.Log.LogWarning($"[Steam] optional lobby not created: {r.m_eResult} (io {ioFailure}) — P2P hosting still active");
                LobbyReady?.Invoke();
                return;
            }
            Lobby = new CSteamID(r.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(Lobby, LobbyKey, Protocol.Version.ToString());
            SteamMatchmaking.SetLobbyData(Lobby, "host", Me.m_SteamID.ToString());
            SteamMatchmaking.SetLobbyData(Lobby, "name", SteamFriends.GetPersonaName());
            SteamMatchmaking.SetLobbyJoinable(Lobby, true);
            Plugin.Log.LogInfo($"[Steam] lobby {Lobby.m_SteamID} created");
            LobbyReady?.Invoke();
        }

        void OnLobbyEnter(LobbyEnter_t r)
        {
            if (_closed || IsHost || _hostReady) return;
            var resp = (EChatRoomEnterResponse)r.m_EChatRoomEnterResponse;
            var lobby = new CSteamID(r.m_ulSteamIDLobby);
            if (resp != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Plugin.Log.LogWarning($"[Steam] JoinLobby failed: {resp}");
                Failed?.Invoke(resp == EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist
                    ? "Ce salon n'existe plus"
                    : resp == EChatRoomEnterResponse.k_EChatRoomEnterResponseFull
                        ? "La partie est complète"
                        : "Steam refuse ce salon — rejoins plutôt ton ami depuis l'écran EN LIGNE ou son profil Steam");
                return;
            }
            Lobby = lobby;
            string ver = SteamMatchmaking.GetLobbyData(Lobby, LobbyKey);
            if (ver != Protocol.Version.ToString())
            {
                Failed?.Invoke(string.IsNullOrEmpty(ver)
                    ? "Ce salon n'utilise pas le mod multijoueur"
                    : $"Version du mod différente (hôte v{ver}, toi v{Protocol.Version})");
                return;
            }
            _host = SteamMatchmaking.GetLobbyOwner(Lobby);
            _hostReady = true;
            _hostLastSeen = Time.unscaledTime;
            SteamFriends.SetRichPresence("status", "En partie en ligne");
            Plugin.Log.LogInfo($"[Steam] entered lobby, host = {_host.m_SteamID}");
            LobbyReady?.Invoke();
            Connected?.Invoke();
        }

        void OnLobbyChat(LobbyChatUpdate_t r)
        {
            if (_closed || r.m_ulSteamIDLobby != Lobby.m_SteamID) return;
            var changed = new CSteamID(r.m_ulSteamIDUserChanged);
            const uint left = (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft
                                     | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
                                     | EChatMemberStateChange.k_EChatMemberStateChangeKicked
                                     | EChatMemberStateChange.k_EChatMemberStateChangeBanned);
            if ((r.m_rgfChatMemberStateChange & left) == 0) return;
            if (IsHost)
            {
                if (_idOf.TryGetValue(changed.m_SteamID, out int peer)) DropPeer(peer);
            }
            else if (changed == _host) LoseHost("L'hôte a quitté la partie");
        }

        void OnP2PRequest(P2PSessionRequest_t r)
        {
            if (_closed) return;
            var who = r.m_steamIDRemote;
            if (Allowed(who)) SteamNetworking.AcceptP2PSessionWithUser(who);
            else Plugin.Log.LogWarning($"[Steam] refused P2P from {who.m_SteamID} (not a friend / member)");
        }

        void OnP2PFail(P2PSessionConnectFail_t r)
        {
            if (_closed) return;
            Plugin.Log.LogWarning($"[Steam] P2P failure with {r.m_steamIDRemote.m_SteamID}: {(EP2PSessionError)r.m_eP2PSessionError}");
            if (IsHost)
            {
                if (_idOf.TryGetValue(r.m_steamIDRemote.m_SteamID, out int peer)) DropPeer(peer);
            }
            else if (r.m_steamIDRemote == _host)
            {
                var err = (EP2PSessionError)r.m_eP2PSessionError;
                LoseHost(_hostEverHeard ? "Connexion P2P perdue avec l'hôte"
                    : err == EP2PSessionError.k_EP2PSessionErrorNoRightsToApp
                        ? "Steam refuse : ce compte n'a pas de licence active du jeu (jeu emprunté ?) — passe par l'IP directe"
                        : "Impossible de joindre l'hôte par Steam");
            }
        }

        /// <summary>Host accepts its Steam friends and lobby members; a client only its host.</summary>
        bool Allowed(CSteamID who)
        {
            if (!IsHost) return who == _host;
            if (IsMember(who)) return true;
            try { return SteamFriends.GetFriendRelationship(who) == EFriendRelationship.k_EFriendRelationshipFriend; }
            catch { return false; }
        }

        bool IsMember(CSteamID who)
        {
            if (!Lobby.IsValid()) return false;
            int n = SteamMatchmaking.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i) == who) return true;
            return false;
        }

        void DropPeer(int peer)
        {
            if (!_steamOf.TryGetValue(peer, out var sid)) return;
            _steamOf.Remove(peer);
            _idOf.Remove(sid.m_SteamID);
            _lastSeen.Remove(peer);
            SteamNetworking.CloseP2PSessionWithUser(sid);
            PeerLeft?.Invoke(peer);
        }

        void LoseHost(string reason)
        {
            if (!_hostReady) return;
            _hostReady = false;
            Disconnected?.Invoke(reason);
        }

        // ── ITransport ───────────────────────────────────────────────────────────

        public void Poll()
        {
            if (_closed) return;
            float now = Time.unscaledTime;
            while (SteamNetworking.IsP2PPacketAvailable(out uint size, Channel))
            {
                if (size > _buf.Length) _buf = new byte[size];
                if (!SteamNetworking.ReadP2PPacket(_buf, size, out uint read, out CSteamID from, Channel)) break;
                int peer;
                if (IsHost)
                {
                    if (!_idOf.TryGetValue(from.m_SteamID, out peer))
                    {
                        if (!Allowed(from) || _steamOf.Count >= Protocol.MaxSlots - 1) continue;
                        peer = _nextPeer++;
                        _idOf[from.m_SteamID] = peer;
                        _steamOf[peer] = from;
                        Plugin.Log.LogInfo($"[Steam] P2P peer {peer} = {from.m_SteamID} ({SteamFriends.GetFriendPersonaName(from)})");
                        PeerJoined?.Invoke(peer);
                    }
                    _lastSeen[peer] = now;
                }
                else
                {
                    if (from != _host) continue;
                    peer = -1;
                    _hostLastSeen = now;
                    _hostEverHeard = true;
                }
                var copy = new byte[read];
                Buffer.BlockCopy(_buf, 0, copy, 0, (int)read);
                try { Data?.Invoke(peer, new NetDataReader(copy)); }
                catch (Exception e) { Plugin.Log.LogError("[Steam] receive handler: " + e); }
            }

            // No lobby to tell us when someone leaves: rely on traffic (inputs/menus flow constantly).
            if (IsHost)
            {
                List<int> dead = null;
                foreach (var kv in _lastSeen)
                    if (now - kv.Value > PeerTimeout) (dead ??= new List<int>()).Add(kv.Key);
                if (dead != null) foreach (int p in dead) { Plugin.Log.LogWarning($"[Steam] peer {p} timed out"); DropPeer(p); }
            }
            else if (_hostReady && _hostEverHeard && now - _hostLastSeen > PeerTimeout)
            {
                LoseHost("L'hôte ne répond plus");
            }
        }

        static EP2PSend Mode(bool reliable) =>
            reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay;

        public void SendToHost(NetDataWriter w, bool reliable)
        {
            if (_closed || !_host.IsValid()) return;
            SteamNetworking.SendP2PPacket(_host, w.Data, (uint)w.Length, Mode(reliable), Channel);
        }

        public void SendToPeer(int peer, NetDataWriter w, bool reliable)
        {
            if (_closed || !_steamOf.TryGetValue(peer, out var sid)) return;
            SteamNetworking.SendP2PPacket(sid, w.Data, (uint)w.Length, Mode(reliable), Channel);
        }

        public void Disconnect(int peer) => DropPeer(peer);

        public void Shutdown()
        {
            if (_closed) return;
            _closed = true;
            foreach (var sid in _steamOf.Values) SteamNetworking.CloseP2PSessionWithUser(sid);
            if (_host.IsValid()) SteamNetworking.CloseP2PSessionWithUser(_host);
            if (Lobby.IsValid()) SteamMatchmaking.LeaveLobby(Lobby);
            SteamFriends.ClearRichPresence();
            _cbEnter.Dispose(); _cbChat.Dispose(); _cbP2PReq.Dispose(); _cbP2PFail.Dispose(); _crCreated.Dispose();
            _steamOf.Clear(); _idOf.Clear(); _lastSeen.Clear();
        }

        public int PingMs(int peer) => -1;

        public string PeerName(int peer) =>
            _steamOf.TryGetValue(peer, out var sid) ? SteamFriends.GetFriendPersonaName(sid) : "?";

        /// <summary>Invite friends: Steam's invite dialog when a lobby exists, else the friends list
        /// ("Invite to game" there uses our rich presence connect string).</summary>
        public bool OpenInviteOverlay()
        {
            if (Lobby.IsValid()) { SteamFriends.ActivateGameOverlayInviteDialog(Lobby); return true; }
            SteamFriends.ActivateGameOverlay("Friends");
            return false;
        }

        /// <summary>Steam friends currently hosting a game with the mod (for the "join a friend" buttons).</summary>
        public static List<(string name, CSteamID host)> HostingFriends(int max)
        {
            var list = new List<(string, CSteamID)>();
            if (!Available) return list;
            var app = SteamUtils.GetAppID();
            int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < n && list.Count < max; i++)
            {
                var f = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (!SteamFriends.GetFriendGamePlayed(f, out FriendGameInfo_t info)) continue;
                if (info.m_gameID.AppID() != app) continue;
                SteamFriends.RequestFriendRichPresence(f);
                string hosting = SteamFriends.GetFriendRichPresence(f, HostKey);
                if (string.IsNullOrEmpty(hosting)) continue;
                list.Add((SteamFriends.GetFriendPersonaName(f), f));
            }
            return list;
        }

        /// <summary>Parse "+bfump_join 7656119…" (command line or rich presence connect string).</summary>
        public static bool TryParseJoin(string text, out CSteamID host)
        {
            host = CSteamID.Nil;
            if (string.IsNullOrEmpty(text)) return false;
            int i = text.IndexOf(JoinArg, StringComparison.Ordinal);
            if (i < 0) return false;
            var rest = text.Substring(i + JoinArg.Length).Trim().Split(' ');
            if (rest.Length == 0 || !ulong.TryParse(rest[0], out ulong id)) return false;
            host = new CSteamID(id);
            return host.IsValid();
        }
    }
}
