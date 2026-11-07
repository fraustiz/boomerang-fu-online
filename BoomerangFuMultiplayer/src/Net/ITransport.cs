using System;
using LiteNetLib.Utils;

namespace BoomerangFuMultiplayer.Net
{
    /// <summary>
    /// Star topology seen from the session: the host talks to every client, clients only talk
    /// to the host. Peer ids are transport-local integers; the host itself is never a peer.
    /// </summary>
    public interface ITransport
    {
        bool IsHost { get; }
        /// <summary>Client: link with the host is up.</summary>
        bool IsConnected { get; }
        string Kind { get; }

        /// <summary>Pump the network and raise events on the Unity main thread.</summary>
        void Poll();

        void SendToHost(NetDataWriter w, bool reliable);
        void SendToPeer(int peer, NetDataWriter w, bool reliable);
        void Disconnect(int peer);
        void Shutdown();

        int PingMs(int peer);
        string PeerName(int peer);

        event Action<int, NetDataReader> Data;     // (peer, payload) — peer = -1 when from host
        event Action<int> PeerJoined;              // host side
        event Action<int> PeerLeft;                // host side
        event Action Connected;                    // client side
        event Action<string> Disconnected;         // client side (reason)
    }
}
