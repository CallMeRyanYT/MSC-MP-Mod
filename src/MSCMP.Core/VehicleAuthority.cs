using System;
using System.Collections.Generic;

namespace MSCMP.Core
{
    public struct VehicleInput
    {
        public float Throttle, Brake, Steering, Handbrake, Clutch;
        public int Gear;
        public bool StartEngine;
        public bool Valid(int gears)
        {
            return Unit(Throttle) && Unit(Brake) && Unit(Handbrake) && Unit(Clutch) && WorldVector.Finite(Steering)
                && Steering >= -1 && Steering <= 1 && Gear >= 0 && Gear < gears;
        }
        private static bool Unit(float value) { return WorldVector.Finite(value) && value >= 0 && value <= 1; }
        public static VehicleInput Stopped { get { return new VehicleInput { Brake = 1, Handbrake = 1, Clutch = 1, Gear = 1 }; } }
    }

    public enum SeatOutcome : byte { Accepted, NotReady, StaleRequest, StaleRevision, Occupied, HoldingItem, TooFar, NotOccupant, Moving, InvalidSeat }

    // One vehicle authority per verified body. All calls are serialized on the game thread.
    // Connection generations and request IDs are supplied by the accepted transport envelope.
    public sealed class VehicleAuthority
    {
        private sealed class Replay
        {
            internal ulong Connection, Request, ExpectedRevision;
            internal int Seat;
            internal SeatOutcome Outcome;
        }
        private readonly Dictionary<ulong, Replay> requests = new Dictionary<ulong, Replay>();
        private readonly ulong[] occupants;
        private readonly int gears;
        private ulong revision = 1, inputSequence;
        private long lastInput;
        private VehicleInput input = VehicleInput.Stopped;
        public ulong Revision { get { return revision; } }
        public ulong Driver { get { return occupants[0]; } }
        public ulong[] Occupants { get { return (ulong[])occupants.Clone(); } }
        public VehicleAuthority(int seats, int gearCount)
        {
            if (seats < 1 || seats > 8 || gearCount < 2 || gearCount > 16) throw new ArgumentException("Invalid vehicle contract.");
            occupants = new ulong[seats]; gears = gearCount;
        }
        public int SeatOf(ulong player)
        { for (int i = 0; i < occupants.Length; i++) if (occupants[i] == player && player != 0) return i; return -1; }
        public SeatOutcome Request(PlayerState player, ulong connection, ulong request, ulong expectedRevision, int seat, WorldVector vehiclePosition, WorldVector vehicleVelocity)
        {
            if (player == null || !player.Connected || !player.Ready || player.ConnectionGeneration != connection || request == 0) return SeatOutcome.NotReady;
            Replay previous;
            if (requests.TryGetValue(player.Id, out previous))
            {
                if (connection < previous.Connection || (connection == previous.Connection && request < previous.Request)) return SeatOutcome.StaleRequest;
                if (connection == previous.Connection && request == previous.Request)
                    return expectedRevision == previous.ExpectedRevision && seat == previous.Seat ? previous.Outcome : SeatOutcome.StaleRequest;
            }
            else if (requests.Count >= 64) return SeatOutcome.StaleRequest;
            SeatOutcome result = Execute(player, expectedRevision, seat, vehiclePosition, vehicleVelocity);
            requests[player.Id] = new Replay { Connection = connection, Request = request, ExpectedRevision = expectedRevision, Seat = seat, Outcome = result };
            return result;
        }
        private SeatOutcome Execute(PlayerState player, ulong expectedRevision, int seat, WorldVector position, WorldVector velocity)
        {
            if (expectedRevision != revision) return SeatOutcome.StaleRevision;
            if (seat < -1 || seat >= occupants.Length) return SeatOutcome.InvalidSeat;
            int existing = SeatOf(player.Id);
            if (seat == -1)
            {
                if (existing < 0) return SeatOutcome.NotOccupant;
                if (!velocity.IsFinite || velocity.LengthSquared > 9) return SeatOutcome.Moving;
                occupants[existing] = 0;
            }
            else
            {
                if (existing >= 0 || occupants[seat] != 0) return SeatOutcome.Occupied;
                if (player.HandEntityId != 0) return SeatOutcome.HoldingItem;
                if (!position.IsFinite || WorldVector.DistanceSquared(player.Position, position) > 36) return SeatOutcome.TooFar;
                occupants[seat] = player.Id;
            }
            Advance(); return SeatOutcome.Accepted;
        }
        public bool Disconnect(ulong player)
        {
            int seat = SeatOf(player); if (seat < 0) return false;
            occupants[seat] = 0; Advance(); return true;
        }
        private void Advance() { revision = checked(revision + 1); inputSequence = 0; input = VehicleInput.Stopped; lastInput = 0; }
        public bool AcceptInput(ulong player, ulong seatRevision, ulong sequence, VehicleInput value, long now)
        {
            if (player == 0 || player != Driver || seatRevision != revision || sequence <= inputSequence || now < 0 || !value.Valid(gears)) return false;
            inputSequence = sequence; input = value; lastInput = now; return true;
        }
        public VehicleInput ReadInput(long now)
        {
            if (Driver == 0 || inputSequence == 0 || now < lastInput || now - lastInput > 500) return VehicleInput.Stopped;
            // Cranking is a held control, just like throttle. Keep it active between
            // input samples; the stale-input deadline above releases it safely.
            return input;
        }
    }
}
