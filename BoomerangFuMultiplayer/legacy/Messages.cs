using LiteNetLib.Utils;
using UnityEngine;

namespace BoomerangFuMultiplayer
{
    public enum MsgType : byte
    {
        Assign = 1,  // host → client: your player slot index
        Input  = 2,  // client → host: button states each frame
        State  = 3,  // host → all:   full game state at 30 Hz
    }

    // Client input — 9 bytes on wire
    public struct InputMsg : INetSerializable
    {
        public float moveX, moveY;
        public bool  dash, attack, throwBtn, throwHeld, wave;

        public void Serialize(NetDataWriter w)
        {
            w.Put(moveX); w.Put(moveY);
            byte b = 0;
            if (dash)      b |= 1;
            if (attack)    b |= 2;
            if (throwBtn)  b |= 4;
            if (throwHeld) b |= 8;
            if (wave)      b |= 16;
            w.Put(b);
        }
        public void Deserialize(NetDataReader r)
        {
            moveX = r.GetFloat(); moveY = r.GetFloat();
            byte b = r.GetByte();
            dash      = (b & 1)  != 0;
            attack    = (b & 2)  != 0;
            throwBtn  = (b & 4)  != 0;
            throwHeld = (b & 8)  != 0;
            wave      = (b & 16) != 0;
        }
    }

    // Per-player snapshot — uses playerID for stable identity
    public struct PlayerSnapshot
    {
        public int   playerId;
        public float x, y;
        public float vx, vy;
        public float rotZ;
        public int   actorState;
        public bool  active;
    }

    // Per-disc snapshot
    public struct DiscSnapshot
    {
        public float x, y;
        public float vx, vy;
        public float rotZ;
        public int   movement;
    }

    public struct StateMsg : INetSerializable
    {
        // Game-level state
        public int   gameState;
        public int   roundNumber;
        public float roundTimer;
        public float matchTimer;

        public PlayerSnapshot[] players;
        public DiscSnapshot[]   discs;

        public void Serialize(NetDataWriter w)
        {
            w.Put(gameState); w.Put(roundNumber);
            w.Put(roundTimer); w.Put(matchTimer);

            int np = players?.Length ?? 0;
            w.Put((byte)np);
            for (int i = 0; i < np; i++)
            {
                var p = players[i];
                w.Put(p.playerId);
                w.Put(p.x);  w.Put(p.y);
                w.Put(p.vx); w.Put(p.vy);
                w.Put(p.rotZ);
                w.Put(p.actorState);
                w.Put(p.active);
            }

            int nd = discs?.Length ?? 0;
            w.Put((byte)nd);
            for (int i = 0; i < nd; i++)
            {
                var d = discs[i];
                w.Put(d.x);  w.Put(d.y);
                w.Put(d.vx); w.Put(d.vy);
                w.Put(d.rotZ);
                w.Put(d.movement);
            }
        }

        public void Deserialize(NetDataReader r)
        {
            gameState   = r.GetInt();  roundNumber = r.GetInt();
            roundTimer  = r.GetFloat(); matchTimer  = r.GetFloat();

            int np = r.GetByte();
            players = new PlayerSnapshot[np];
            for (int i = 0; i < np; i++)
            {
                players[i].playerId    = r.GetInt();
                players[i].x          = r.GetFloat();
                players[i].y          = r.GetFloat();
                players[i].vx         = r.GetFloat();
                players[i].vy         = r.GetFloat();
                players[i].rotZ       = r.GetFloat();
                players[i].actorState = r.GetInt();
                players[i].active     = r.GetBool();
            }

            int nd = r.GetByte();
            discs = new DiscSnapshot[nd];
            for (int i = 0; i < nd; i++)
            {
                discs[i].x        = r.GetFloat();
                discs[i].y        = r.GetFloat();
                discs[i].vx       = r.GetFloat();
                discs[i].vy       = r.GetFloat();
                discs[i].rotZ     = r.GetFloat();
                discs[i].movement = r.GetInt();
            }
        }
    }
}
