using MSCMP.Net;
using UnityEngine;

namespace MSCMP
{
    /// <summary>Everything other players need to draw you, sent ~20 times a second.</summary>
    internal struct PlayerSnapshot
    {
        public const byte FlagRunning = 1;
        public const byte FlagSwimming = 2;
        public const byte FlagSleeping = 4;
        public const byte FlagInMenu = 8;      // paused / in a menu
        public const byte FlagGrounded = 16;

        public float Time;          // sender's clock
        public uint Vehicle;        // 0 = on foot. Otherwise Position/Yaw/Head are local to that vehicle.
        public byte Seat;           // 1 = driver, 2 = passenger
        public Vector3 Position;    // feet
        public float Yaw;           // body yaw in degrees
        public float Pitch;         // camera pitch in degrees (+ = looking down)
        public Vector3 Head;        // camera position
        public Vector3 Velocity;    // world space, for animation speed
        public float Crouch;        // 0 standing .. 1 fully down
        public byte Flags;
        public uint Held;           // id of the object in hand, 0 = empty

        public bool Has(byte flag) { return (Flags & flag) != 0; }

        public void Write(NetWriter w)
        {
            w.Float(Time);
            w.UInt(Vehicle);
            w.Byte(Seat);
            w.Vector3(Position);
            w.Half(Mathf.DeltaAngle(0f, Yaw), 180f);
            w.Half(Mathf.DeltaAngle(0f, Pitch), 90f);
            w.Vector3(Head);
            w.Half(Velocity.x, 40f); w.Half(Velocity.y, 40f); w.Half(Velocity.z, 40f);
            w.Unit8(Crouch);
            w.Byte(Flags);
            w.UInt(Held);
        }

        public static PlayerSnapshot Read(NetReader r)
        {
            PlayerSnapshot s;
            s.Time = r.Float();
            s.Vehicle = r.UInt();
            s.Seat = r.Byte();
            s.Position = r.Vector3();
            s.Yaw = r.Half(180f);
            s.Pitch = r.Half(90f);
            s.Head = r.Vector3();
            s.Velocity = new Vector3(r.Half(40f), r.Half(40f), r.Half(40f));
            s.Crouch = r.Unit8();
            s.Flags = r.Byte();
            s.Held = r.UInt();
            return s;
        }
    }
}
