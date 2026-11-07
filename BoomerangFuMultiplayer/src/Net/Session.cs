using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using Steamworks;
using UnityEngine;

namespace BoomerangFuMultiplayer.Net
{
    public enum Role { None, Host, Client }

    /// <summary>
    /// One online session. Host owns slot 0 and gives slots 1..3 to clients. Each machine has
    /// exactly one local player, which becomes the lobby player / Rewired player of its slot.
    /// </summary>
    public sealed class Session : MonoBehaviour
    {
        public static Session Instance { get; private set; }

        public static bool Active => Instance != null && Instance.Role != Role.None && Instance.LocalSlot >= 0;
        public static bool IsHost => Instance != null && Instance.Role == Role.Host;
        public static bool IsClient => Instance != null && Instance.Role == Role.Client && Instance.LocalSlot >= 0;

        // Set while executing something the host decided (mirrored click, death event...).
        static int _hostScope;
        public static bool InHostScope => _hostScope > 0;
        public static HostScopeToken HostScope() { _hostScope++; return new HostScopeToken(); }
        public readonly struct HostScopeToken : IDisposable { public void Dispose() => _hostScope--; }

        public Role Role { get; private set; }
        public ITransport Transport { get; private set; }
        public int LocalSlot { get; private set; } = -1;
        public string Status { get; private set; } = "";
        public bool Connecting => Role == Role.Client && LocalSlot < 0;

        public sealed class SlotInfo
        {
            public bool Used;
            public string Name = "";
            public int Peer = -1;     // host side: transport peer id (-1 = host itself)
            public bool Pending;      // host side: joined during a match, not playing yet
        }
        public readonly SlotInfo[] Slots = new SlotInfo[Protocol.MaxSlots];

        public event Action Changed;                 // roster / status changed
        public event Action<string> Toast;           // short message for the HUD

        readonly Dictionary<Msg, Action<int, NetDataReader>> _handlers = new Dictionary<Msg, Action<int, NetDataReader>>();
        float _helloTimeout;

        void Awake()
        {
            Instance = this;
            for (int i = 0; i < Slots.Length; i++) Slots[i] = new SlotInfo();
            On(Msg.Hello, OnHello);
            On(Msg.Welcome, OnWelcome);
            On(Msg.Reject, OnReject);
            On(Msg.Roster, OnRoster);
            On(Msg.Bye, OnBye);
            Toast += m => Plugin.Log.LogInfo("[Toast] " + m);
        }

        public void On(Msg type, Action<int, NetDataReader> handler) => _handlers[type] = handler;

        public static string LocalName
        {
            get
            {
                try { if (SteamTransport.Available) return SteamFriends.GetPersonaName(); } catch { }
                return Environment.UserName;
            }
        }

        // ── Start / stop ─────────────────────────────────────────────────────────

        public void HostSteam()
        {
            if (!SteamTransport.Available) { Fail("Steam n'est pas disponible"); return; }
            var t = SteamTransport.Host();
            t.Failed += Fail;
            t.LobbyReady += () => SetStatus("Partie ouverte à tes amis Steam — invite-les");
            BeginHost(t);
            SetStatus("Partie ouverte à tes amis Steam — invite-les");
        }

        /// <summary>Join a Steam friend who hosts (by SteamID, no lobby needed).</summary>
        public void JoinSteamHost(CSteamID host)
        {
            if (!SteamTransport.Available) { Fail("Steam n'est pas disponible"); return; }
            var t = SteamTransport.JoinHost(host);
            t.Failed += Fail;
            BeginClient(t);
            SetStatus($"Connexion à {SteamFriends.GetFriendPersonaName(host)} via Steam…");
            t.Start();
        }

        public void HostDirect(int port)
        {
            try { BeginHost(DirectTransport.Host(port)); }
            catch (Exception e) { Fail(e.Message); return; }
            SetStatus($"Hébergement en IP directe (UDP {port}) — ouverture du port sur la box…");
            _upnpPort = port;
            _upnp = new UpnpPortMapper(port);
            _upnp.Start();
        }

        UpnpPortMapper _upnp;
        int _upnpPort;
        /// <summary>Address friends can use over the Internet ("ip:port"), once the router opened the port.</summary>
        public string PublicAddress { get; private set; }

        void PollUpnp()
        {
            if (_upnp == null || !_upnp.Done || Role != Role.Host || _upnpReported) return;
            _upnpReported = true;
            var o = _upnp.Result;
            if (o.Mapped && !string.IsNullOrEmpty(o.PublicIp))
            {
                PublicAddress = $"{o.PublicIp}:{_upnpPort}";
                Plugin.Log.LogInfo($"[UPnP] UDP {_upnpPort} opened on {o.Router} → {o.LocalIp}, public address {PublicAddress}");
                SetStatus($"Ouvert sur Internet : {PublicAddress} — donne cette adresse à tes amis");
                Toast?.Invoke("Port ouvert sur la box : tes amis peuvent rejoindre via Internet");
            }
            else
            {
                Plugin.Log.LogWarning($"[UPnP] port not opened: {o.Error ?? "public IP unknown"} (router {o.Router}, local {o.LocalIp})");
                SetStatus($"Hébergement en IP directe (UDP {_upnpPort}). Pour jouer via Internet, redirige le port UDP {_upnpPort} de ta box vers {o.LocalIp ?? "ce PC"} (UPnP : {o.Error ?? "?"})");
            }
        }
        bool _upnpReported;

        public void JoinSteam(CSteamID lobby)
        {
            if (!SteamTransport.Available) { Fail("Steam n'est pas disponible"); return; }
            var t = SteamTransport.Join(lobby);
            t.Failed += Fail;
            BeginClient(t);
            SetStatus("Connexion au salon Steam…");
        }

        public void JoinDirect(string ip, int port)
        {
            try { BeginClient(DirectTransport.Join(ip, port)); }
            catch (Exception e) { Fail(e.Message); return; }
            SetStatus($"Connexion à {ip}:{port}…");
        }

        void BeginHost(ITransport t)
        {
            Leave(silent: true);
            Transport = t;
            Role = Role.Host;
            LocalSlot = 0;
            Slots[0].Used = true; Slots[0].Name = LocalName; Slots[0].Peer = -1;
            t.Data += Dispatch;
            t.PeerJoined += p => Plugin.Log.LogInfo($"[Session] peer {p} connected, waiting for hello");
            t.PeerLeft += OnPeerLeft;
            SessionStarted();
        }

        void BeginClient(ITransport t)
        {
            Leave(silent: true);
            Transport = t;
            Role = Role.Client;
            LocalSlot = -1;
            _helloTimeout = Time.unscaledTime + 20f;
            t.Data += Dispatch;
            t.Connected += () =>
            {
                var w = Protocol.Begin(Msg.Hello);
                w.Put(Protocol.Version);
                w.Put(PluginInfo.Version);
                w.Put(LocalName);
                t.SendToHost(w, true);
                SetStatus("En attente de la réponse de l'hôte…");
            };
            t.Disconnected += reason =>
            {
                bool wasIn = LocalSlot >= 0;
                Leave(silent: true);
                Fail(wasIn ? "Déconnecté : " + reason : "Connexion impossible : " + reason);
            };
        }

        void SessionStarted()
        {
            Changed?.Invoke();
            Sync.NetInput.OnSessionStarted();
        }

        public void Leave(bool silent = false)
        {
            if (Role == Role.None) return;
            bool wasActive = Active;
            try
            {
                var w = Protocol.Begin(Msg.Bye);
                if (Role == Role.Host) BroadcastAll(w, true);
                else Transport?.SendToHost(w, true);
                Transport?.Poll();
            }
            catch { }
            Transport?.Shutdown();
            Transport = null;
            if (_upnp != null) { var m = _upnp; _upnp = null; m.Remove(); }
            _upnpReported = false;
            PublicAddress = null;
            Role = Role.None;
            LocalSlot = -1;
            foreach (var s in Slots) { s.Used = false; s.Name = ""; s.Peer = -1; s.Pending = false; }
            Sync.NetInput.OnSessionEnded();
            Sync.MatchSync.OnSessionEnded(wasActive);
            if (!silent) SetStatus("Session terminée");
            Changed?.Invoke();
        }

        void Fail(string message)
        {
            Plugin.Log.LogWarning("[Session] " + message);
            if (Role != Role.None && !Active) Leave(silent: true);
            SetStatus(message);
            Toast?.Invoke(message);
        }

        void SetStatus(string s)
        {
            Status = s;
            Plugin.Log.LogInfo("[Status] " + s);
            Changed?.Invoke();
        }
        public void Notify(string s) => Toast?.Invoke(s);

        void Update()
        {
            Transport?.Poll();
            PollUpnp();
            if (Connecting && Time.unscaledTime > _helloTimeout)
            {
                Leave(silent: true);
                Fail("L'hôte ne répond pas");
            }
        }

        void OnApplicationQuit() => Leave(silent: true);

        // ── Messaging ────────────────────────────────────────────────────────────

        void Dispatch(int peer, NetDataReader r)
        {
            if (r.AvailableBytes < 1) return;
            var type = (Msg)r.GetByte();
            if (Role == Role.Host && type != Msg.Hello && type != Msg.Bye && SlotOfPeer(peer) < 0) return;
            if (_handlers.TryGetValue(type, out var h)) h(peer, r);
        }

        public int SlotOfPeer(int peer)
        {
            for (int i = 1; i < Slots.Length; i++)
                if (Slots[i].Used && Slots[i].Peer == peer) return i;
            return -1;
        }

        /// <summary>Host: send to every connected client (optionally skipping one peer).</summary>
        public void BroadcastAll(NetDataWriter w, bool reliable, int exceptPeer = int.MinValue)
        {
            if (Role != Role.Host || Transport == null) return;
            for (int i = 1; i < Slots.Length; i++)
                if (Slots[i].Used && Slots[i].Peer != exceptPeer) Transport.SendToPeer(Slots[i].Peer, w, reliable);
        }

        public void SendToHost(NetDataWriter w, bool reliable)
        {
            if (Role == Role.Client) Transport?.SendToHost(w, reliable);
        }

        public bool SlotUsed(int slot) => slot >= 0 && slot < Slots.Length && Slots[slot].Used;
        public int PlayerCount { get { int n = 0; foreach (var s in Slots) if (s.Used) n++; return n; } }

        public int PingToHost => Role == Role.Client ? Transport?.PingMs(-1) ?? -1 : -1;

        // ── Handshake ────────────────────────────────────────────────────────────

        void OnHello(int peer, NetDataReader r)
        {
            if (Role != Role.Host) return;
            int proto = r.GetInt();
            string ver = r.GetString();
            string name = r.GetString(64);

            string reject = null;
            if (proto != Protocol.Version) reject = $"Version du mod différente (hôte v{Protocol.Version}, toi v{proto})";

            int slot = -1;
            if (reject == null)
            {
                for (int i = 1; i < Slots.Length; i++)
                {
                    if (Slots[i].Used) continue;
                    // test option: during a match, prefer a slot with no player (exercises "add a player")
                    if (Plugin.ForceAddJoiners && Sync.MatchSync.MatchInProgress && Sync.MatchSync.SlotHasPlayer(i)) continue;
                    slot = i; break;
                }
                if (slot < 0) for (int i = 1; i < Slots.Length; i++) if (!Slots[i].Used) { slot = i; break; }
                if (slot < 0) reject = "La partie est complète (4 joueurs)";
            }

            if (reject != null)
            {
                var rw = Protocol.Begin(Msg.Reject);
                rw.Put(reject);
                Transport.SendToPeer(peer, rw, true);
                Plugin.Log.LogInfo($"[Session] rejected {name}: {reject}");
                return;
            }

            Slots[slot].Used = true;
            Slots[slot].Name = string.IsNullOrEmpty(name) ? Transport.PeerName(peer) : name;
            Slots[slot].Peer = peer;
            Slots[slot].Pending = Sync.MatchSync.MatchInProgress;

            var w = Protocol.Begin(Msg.Welcome);
            w.Put((byte)slot);
            w.Put(Slots[slot].Pending ? Sync.MatchSync.LateJoinNote(slot) : "");
            Transport.SendToPeer(peer, w, true);
            BroadcastRoster();
            Plugin.Log.LogInfo($"[Session] {Slots[slot].Name} (mod {ver}) → slot {slot}");
            Toast?.Invoke($"{Slots[slot].Name} a rejoint la partie");
            Sync.MatchSync.OnPeerJoined(peer);
            Changed?.Invoke();
        }

        void OnWelcome(int peer, NetDataReader r)
        {
            if (Role != Role.Client) return;
            LocalSlot = r.GetByte();
            string note = r.AvailableBytes > 0 ? r.GetString() : "";
            SetStatus($"Connecté — tu es le joueur {LocalSlot + 1}" + (note.Length > 0 ? "\n" + note : ""));
            Toast?.Invoke("Connecté à la partie !");
            if (note.Length > 0) Toast?.Invoke(note);
            SessionStarted();
        }

        void OnReject(int peer, NetDataReader r)
        {
            string reason = r.GetString();
            Leave(silent: true);
            Fail(reason);
        }

        void OnRoster(int peer, NetDataReader r)
        {
            if (Role != Role.Client) return;
            for (int i = 0; i < Slots.Length; i++)
            {
                Slots[i].Used = r.GetBool();
                Slots[i].Name = r.GetString();
            }
            Changed?.Invoke();
        }

        void OnBye(int peer, NetDataReader r)
        {
            if (Role == Role.Host)
            {
                int slot = SlotOfPeer(peer);
                Transport.Disconnect(peer);
                if (slot >= 0) RemoveSlot(slot);
            }
            else
            {
                Leave(silent: true);
                Fail("L'hôte a fermé la session");
            }
        }

        void OnPeerLeft(int peer)
        {
            int slot = SlotOfPeer(peer);
            if (slot >= 0) RemoveSlot(slot);
        }

        void RemoveSlot(int slot)
        {
            string name = Slots[slot].Name;
            Slots[slot].Used = false; Slots[slot].Name = ""; Slots[slot].Peer = -1; Slots[slot].Pending = false;
            Sync.NetInput.ClearSlot(slot);
            BroadcastRoster();
            Toast?.Invoke($"{name} a quitté la partie");
            Changed?.Invoke();
        }

        void BroadcastRoster()
        {
            var w = Protocol.Begin(Msg.Roster);
            foreach (var s in Slots) { w.Put(s.Used); w.Put(s.Name ?? ""); }
            BroadcastAll(w, true);
        }
    }
}
