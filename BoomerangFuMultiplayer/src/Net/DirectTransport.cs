using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;

namespace BoomerangFuMultiplayer.Net
{
    /// <summary>UDP transport over a plain IP (LAN, Hamachi/ZeroTier, port forward, localhost).</summary>
    public sealed class DirectTransport : ITransport
    {
        const string Key = "BFuMP-v" + "2";

        readonly EventBasedNetListener _listener = new EventBasedNetListener();
        readonly NetManager _net;
        readonly Dictionary<int, NetPeer> _peers = new Dictionary<int, NetPeer>();
        readonly Dictionary<int, int> _latency = new Dictionary<int, int>();
        NetPeer _server;

        public bool IsHost { get; }
        public bool IsConnected => IsHost || _server != null;
        public string Kind => "IP";

        public event Action<int, NetDataReader> Data;
        public event Action<int> PeerJoined;
        public event Action<int> PeerLeft;
        public event Action Connected;
        public event Action<string> Disconnected;

        DirectTransport(bool host)
        {
            IsHost = host;
            _net = new NetManager(_listener) { DisconnectTimeout = 8000, AutoRecycle = false, UnsyncedEvents = false };

            _listener.ConnectionRequestEvent += req =>
            {
                if (!IsHost || _peers.Count >= Protocol.MaxSlots - 1) { req.Reject(); return; }
                req.AcceptIfKey(Key);
            };
            _listener.PeerConnectedEvent += peer =>
            {
                if (IsHost) { _peers[peer.Id] = peer; PeerJoined?.Invoke(peer.Id); }
                else { _server = peer; Connected?.Invoke(); }
            };
            _listener.PeerDisconnectedEvent += (peer, info) =>
            {
                if (IsHost)
                {
                    if (_peers.Remove(peer.Id)) PeerLeft?.Invoke(peer.Id);
                }
                else
                {
                    _server = null;
                    Disconnected?.Invoke(info.Reason.ToString());
                }
            };
            _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                try { Data?.Invoke(IsHost ? peer.Id : -1, reader); }
                catch (Exception e) { Plugin.Log.LogError("[Direct] receive handler: " + e); }
                finally { reader.Recycle(); }
            };
            _listener.NetworkLatencyUpdateEvent += (peer, latency) => _latency[IsHost ? peer.Id : -1] = latency;
        }

        public static DirectTransport Host(int port)
        {
            var t = new DirectTransport(true);
            if (!t._net.Start(port)) throw new Exception($"Impossible d'ouvrir le port UDP {port}");
            Plugin.Log.LogInfo($"[Direct] hosting on UDP {port}");
            return t;
        }

        public static DirectTransport Join(string ip, int port)
        {
            var t = new DirectTransport(false);
            t._net.Start();
            t._net.Connect(ip, port, Key);
            Plugin.Log.LogInfo($"[Direct] connecting to {ip}:{port}");
            return t;
        }

        public void Poll() => _net.PollEvents();

        static DeliveryMethod Method(bool reliable) => reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced;

        public void SendToHost(NetDataWriter w, bool reliable) => _server?.Send(w, Method(reliable));

        public void SendToPeer(int peer, NetDataWriter w, bool reliable)
        {
            if (_peers.TryGetValue(peer, out var p)) p.Send(w, Method(reliable));
        }

        public void Disconnect(int peer)
        {
            if (_peers.TryGetValue(peer, out var p)) _net.DisconnectPeer(p);
        }

        public void Shutdown()
        {
            try { _net.Stop(true); } catch { }
            _peers.Clear();
            _server = null;
        }

        public int PingMs(int peer) => _latency.TryGetValue(peer, out var l) ? l : -1;

        public string PeerName(int peer) =>
            _peers.TryGetValue(peer, out var p) ? p.ToString() : "?";
    }
}
