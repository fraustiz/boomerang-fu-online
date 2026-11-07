using System;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace BoomerangFuMultiplayer
{
    // Singleton MonoBehaviour wrapping LiteNetLib.
    // PollEvents() runs on Unity's main thread — no cross-thread locking needed.
    public sealed class NetLayer : MonoBehaviour, INetEventListener
    {
        public static NetLayer Instance { get; private set; }

        const string KEY = "BFuMP";

        private NetManager    _net;
        private NetDataWriter _w = new NetDataWriter();

        public bool IsHost      { get; private set; }
        public bool IsClient    { get; private set; }
        public bool IsConnected => IsHost || (IsClient && _server != null);

        // Host: one peer per client slot (slots 1–3; slot 0 = local host player)
        private NetPeer[] _slots    = new NetPeer[4];
        private int       _nextSlot = 1;

        // Client: connection to host
        private NetPeer _server;

        // Player slot this client owns in the host's game (set by Assign message)
        public byte LocalSlot { get; private set; }

        // Input received from each client — host reads these in InputPatch
        public InputMsg[] RemoteInput { get; } = new InputMsg[4];
        public bool[]     HasInput    { get; } = new bool[4];

        // Latest full state received from host — GameSync reads this on clients
        public StateMsg? LatestState { get; private set; }

        public int Latency { get; private set; }

        // All events fire on Unity main thread (via PollEvents in Update)
        public event Action<int> OnClientJoined;
        public event Action<int> OnClientLeft;
        public event Action      OnJoinedHost;
        public event Action      OnLeftHost;

        void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // Drive LiteNetLib on main thread
        void Update() => _net?.PollEvents();

        public bool IsSlotConnected(int slot) =>
            slot >= 1 && slot <= 3 && _slots[slot] != null;

        // ─── Public API ────────────────────────────────────────────────────────

        public void StartHost(int port = 7777)
        {
            Shutdown();
            _net = new NetManager(this);
            _net.Start(port);
            IsHost    = true;
            _nextSlot = 1;
            Plugin.Log.LogInfo($"[NetLayer] Hosting on :{port}");
        }

        public void StartClient(string ip, int port = 7777)
        {
            Shutdown();
            _net = new NetManager(this);
            _net.Start();
            _net.Connect(ip, port, KEY);
            IsClient = true;
            Plugin.Log.LogInfo($"[NetLayer] Connecting → {ip}:{port}");
        }

        public void Shutdown()
        {
            if (_net == null) return;
            _net.Stop();
            _net      = null;
            IsHost    = IsClient = false;
            _server   = null;
            Array.Clear(_slots, 0, _slots.Length);
            _nextSlot = 1;
            Array.Clear(HasInput, 0, HasInput.Length);
            LatestState = null;
        }

        // Client → host: unreliable, latest-wins (called every Update by InputPatch)
        public void SendInput(InputMsg msg)
        {
            if (!IsClient || _server == null) return;
            _w.Reset();
            _w.Put((byte)MsgType.Input);
            msg.Serialize(_w);
            _server.Send(_w, DeliveryMethod.Unreliable);
        }

        // Host → all clients: unreliable state broadcast at 30 Hz (called by GameSync)
        public void BroadcastState(StateMsg msg)
        {
            if (!IsHost) return;
            _w.Reset();
            _w.Put((byte)MsgType.State);
            msg.Serialize(_w);
            foreach (var peer in _slots)
                peer?.Send(_w, DeliveryMethod.Unreliable);
        }

        // ─── INetEventListener ─────────────────────────────────────────────────

        public void OnConnectionRequest(ConnectionRequest req)
        {
            if (!IsHost || _nextSlot > 3) { req.Reject(); return; }
            req.AcceptIfKey(KEY);
        }

        public void OnPeerConnected(NetPeer peer)
        {
            if (IsHost)
            {
                int slot = _nextSlot++;
                peer.Tag = slot;
                if (slot <= 3) _slots[slot] = peer;

                // Tell client which player slot it owns
                _w.Reset();
                _w.Put((byte)MsgType.Assign);
                _w.Put((byte)slot);
                peer.Send(_w, DeliveryMethod.ReliableOrdered);

                Plugin.Log.LogInfo($"[NetLayer] Client connected → slot {slot}");
                OnClientJoined?.Invoke(slot);
            }
            else
            {
                _server = peer;
                Plugin.Log.LogInfo("[NetLayer] Connected to host");
            }
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (IsHost)
            {
                int slot = peer.Tag is int s ? s : 0;
                if (slot >= 1 && slot <= 3) { _slots[slot] = null; HasInput[slot] = false; }
                Plugin.Log.LogInfo($"[NetLayer] Slot {slot} left: {info.Reason}");
                OnClientLeft?.Invoke(slot);
            }
            else
            {
                _server = null;
                Plugin.Log.LogInfo($"[NetLayer] Disconnected: {info.Reason}");
                OnLeftHost?.Invoke();
            }
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte ch, DeliveryMethod dm)
        {
            var type = (MsgType)reader.GetByte();

            if (type == MsgType.Assign && IsClient)
            {
                LocalSlot = reader.GetByte();
                Plugin.Log.LogInfo($"[NetLayer] Assigned slot {LocalSlot}");
                OnJoinedHost?.Invoke();
            }
            else if (type == MsgType.Input && IsHost)
            {
                int slot = peer.Tag is int s ? s : -1;
                if (slot >= 1 && slot <= 3)
                {
                    var m = new InputMsg();
                    m.Deserialize(reader);
                    RemoteInput[slot] = m;
                    HasInput[slot]    = true;
                }
            }
            else if (type == MsgType.State && IsClient)
            {
                var m = new StateMsg();
                m.Deserialize(reader);
                LatestState = m;
            }

            reader.Recycle();
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) => Latency = latency;
        public void OnNetworkError(IPEndPoint ep, SocketError err) =>
            Plugin.Log.LogError($"[NetLayer] Socket error: {err}");
        public void OnNetworkReceiveUnconnected(IPEndPoint ep, NetPacketReader r, UnconnectedMessageType t) { }
    }
}
