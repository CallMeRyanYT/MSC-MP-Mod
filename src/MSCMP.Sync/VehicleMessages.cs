using System;
using System.IO;
using MSCMP.Core;

namespace MSCMP.Sync
{
    public enum VehicleMessageKind : byte { State = 1, Request = 2, Result = 3, Input = 4 }
    public sealed class VehicleMessage
    {
        public VehicleMessageKind Kind;
        public ulong Revision, Sequence, Driver, Passenger;
        public int Seat;
        public int Gear;
        public SeatOutcome Outcome;
        public VehicleInput Input;
    }
    public static class VehicleMessages
    {
        public static bool Matches(byte[] bytes)
        { return bytes != null && bytes.Length >= 4 && bytes[0] == 0x53 && bytes[1] == 0x4d && bytes[2] == 0x56 && bytes[3] == 0x32; }
        public static byte[] Encode(VehicleMessage m)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(stream))
            {
                w.Write((uint)0x32564d53); w.Write((byte)m.Kind); w.Write(m.Revision); w.Write(m.Sequence);
                switch (m.Kind)
                {
                    case VehicleMessageKind.State: w.Write(m.Driver); w.Write(m.Passenger); w.Write(m.Gear); break;
                    case VehicleMessageKind.Request: w.Write(m.Seat); break;
                    case VehicleMessageKind.Result: w.Write((byte)m.Outcome); w.Write(m.Driver); w.Write(m.Passenger); break;
                    case VehicleMessageKind.Input:
                        VehicleInput i = m.Input;
                        w.Write(i.Throttle); w.Write(i.Brake); w.Write(i.Steering); w.Write(i.Handbrake); w.Write(i.Clutch); w.Write(i.Gear); w.Write(i.StartEngine); break;
                    default: throw new FormatException("Unknown vehicle message.");
                }
                return stream.ToArray();
            }
        }
        public static VehicleMessage Decode(byte[] bytes)
        {
            if (!Matches(bytes) || bytes.Length < 21 || bytes.Length > 64) throw new FormatException("Invalid vehicle message.");
            using (MemoryStream stream = new MemoryStream(bytes, false))
            using (BinaryReader r = new BinaryReader(stream))
            {
                r.ReadUInt32(); VehicleMessage m = new VehicleMessage { Kind = (VehicleMessageKind)r.ReadByte(), Revision = r.ReadUInt64(), Sequence = r.ReadUInt64() };
                if (m.Revision == 0) throw new FormatException("Invalid vehicle revision.");
                switch (m.Kind)
                {
                    case VehicleMessageKind.State: m.Driver = r.ReadUInt64(); m.Passenger = r.ReadUInt64(); m.Gear = r.ReadInt32(); if (m.Gear < 0 || m.Gear > 15) throw new FormatException("Invalid gear."); break;
                    case VehicleMessageKind.Request: m.Seat = r.ReadInt32(); if (m.Seat < -1 || m.Seat > 1 || m.Sequence == 0) throw new FormatException("Invalid seat request."); break;
                    case VehicleMessageKind.Result:
                        m.Outcome = (SeatOutcome)r.ReadByte(); m.Driver = r.ReadUInt64(); m.Passenger = r.ReadUInt64();
                        if (!Enum.IsDefined(typeof(SeatOutcome), m.Outcome) || m.Sequence == 0) throw new FormatException("Invalid seat result."); break;
                    case VehicleMessageKind.Input:
                        m.Input = new VehicleInput { Throttle = r.ReadSingle(), Brake = r.ReadSingle(), Steering = r.ReadSingle(), Handbrake = r.ReadSingle(), Clutch = r.ReadSingle(), Gear = r.ReadInt32() };
                        byte start = r.ReadByte(); m.Input.StartEngine = start == 1;
                        if (start > 1 || m.Sequence == 0 || !m.Input.Valid(16)) throw new FormatException("Invalid driving input."); break;
                    default: throw new FormatException("Unknown vehicle message kind.");
                }
                if (m.Driver != 0 && m.Driver == m.Passenger) throw new FormatException("Duplicate vehicle occupant.");
                if (stream.Position != stream.Length) throw new FormatException("Trailing vehicle bytes.");
                return m;
            }
        }
    }
}
