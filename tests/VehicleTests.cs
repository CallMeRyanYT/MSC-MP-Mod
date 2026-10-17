using System;
using MSCMP.Core;
using MSCMP.Sync;

internal static class VehicleTests
{
    private static int assertions;
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static PlayerState Player(ulong id, ulong connection, bool ready, ulong hand, float x)
    { return new PlayerState(id, connection, 1, 0, hand, true, ready, new WorldVector(x, 0, 0)); }
    private static void Seats()
    {
        VehicleAuthority vehicle = new VehicleAuthority(1, 6);
        PlayerState a = Player(1, 1, true, 0, 0), b = Player(2, 1, true, 0, 0);
        Check(vehicle.Request(Player(3, 1, false, 0, 0), 1, 1, 1, 0, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.NotReady, "Bootstrap cannot drive");
        Check(vehicle.Request(Player(3, 1, true, 7, 0), 1, 1, 1, 0, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.HoldingItem, "Holding blocks entry");
        Check(vehicle.Request(Player(4, 1, true, 0, 100), 1, 1, 1, 0, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.TooFar, "Remote entry rejected");
        Check(vehicle.Request(a, 1, 1, 1, 0, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.Accepted, "First claimant enters");
        Check(vehicle.Request(b, 1, 1, 1, 0, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.StaleRevision && vehicle.Driver == 1, "Racing stale entry cannot steal seat");
        ulong revision = vehicle.Revision;
        Check(vehicle.Request(a, 1, 1, 1, 0, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.Accepted && vehicle.Revision == revision, "Lost response replay has no second seat mutation");
        Check(vehicle.Request(a, 1, 1, 1, -1, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.StaleRequest, "Same request cannot change meaning");
        Check(vehicle.Request(b, 1, 2, revision, 0, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.Occupied, "Current epoch still exclusive");
        VehicleInput valid = new VehicleInput { Throttle = 0.8f, Gear = 2, StartEngine = true };
        Check(!vehicle.AcceptInput(2, revision, 1, valid, 100), "Non-driver input rejected");
        Check(!vehicle.AcceptInput(1, revision - 1, 1, valid, 100), "Old lease input rejected");
        VehicleInput invalid = valid; invalid.Steering = Single.NaN;
        Check(!vehicle.AcceptInput(1, revision, 1, invalid, 100), "NaN controls rejected");
        invalid = valid; invalid.Gear = 6; Check(!vehicle.AcceptInput(1, revision, 1, invalid, 100), "Gear contract enforced");
        Check(vehicle.AcceptInput(1, revision, 1, valid, 100), "Driver input accepted");
        Check(vehicle.ReadInput(200).StartEngine && vehicle.ReadInput(201).StartEngine, "Cranking stays active between network samples");
        Check(!vehicle.AcceptInput(1, revision, 1, valid, 200), "Duplicate input cannot extend its freshness deadline");
        Check(vehicle.ReadInput(601).Throttle == 0 && vehicle.ReadInput(601).Brake == 1 && !vehicle.ReadInput(601).StartEngine, "Silence brakes vehicle and releases the starter");
        Check(vehicle.Request(a, 1, 2, revision, -1, WorldVector.Zero, new WorldVector(4, 0, 0)) == SeatOutcome.Moving, "Unsafe moving exit blocked");
        Check(vehicle.Request(a, 1, 3, revision, -1, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.Accepted, "Stationary exit succeeds");
        Check(vehicle.Driver == 0 && vehicle.ReadInput(100).Throttle == 0, "Exit revokes controls");
        Check(vehicle.Request(b, 1, 3, vehicle.Revision, 0, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.Accepted, "Other player can take released seat");
        Check(vehicle.Disconnect(2) && !vehicle.Disconnect(2) && vehicle.Driver == 0, "Disconnect releases exactly once");
        Check(vehicle.Request(Player(2, 2, true, 0, 0), 1, 4, vehicle.Revision, 0, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.NotReady, "Old connection cannot act for resumed player");
    }
    private static void Passengers()
    {
        VehicleAuthority vehicle = new VehicleAuthority(2, 6);
        PlayerState driver = Player(1, 1, true, 0, 0), passenger = Player(2, 1, true, 0, 0), rival = Player(3, 1, true, 0, 0);
        Check(vehicle.Request(driver, 1, 1, 1, 0, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.Accepted, "Driver enters two-seat van");
        ulong revision = vehicle.Revision;
        Check(vehicle.Request(passenger, 1, 1, revision, 1, WorldVector.Zero, new WorldVector(4, 0, 0)) == SeatOutcome.Moving, "Cannot board moving van");
        Check(vehicle.Request(passenger, 1, 2, revision, 1, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.Accepted, "Passenger enters beside driver");
        Check(vehicle.Driver == 1 && vehicle.SeatOf(2) == 1 && vehicle.SeatOf(0) == -1, "Independent seat identities");
        Check(vehicle.Request(rival, 1, 1, revision, 1, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.StaleRevision, "Racing passenger request rejected");
        Check(vehicle.Request(rival, 1, 2, vehicle.Revision, 1, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.Occupied, "Passenger seat exclusive");
        VehicleInput gas = new VehicleInput { Throttle = 1, Gear = 2 };
        Check(!vehicle.AcceptInput(2, vehicle.Revision, 1, gas, 100), "Passenger cannot accelerate");
        Check(vehicle.AcceptInput(1, vehicle.Revision, 1, gas, 100), "Driver retains control with passenger");
        Check(vehicle.Request(passenger, 1, 3, vehicle.Revision, -1, WorldVector.Zero, new WorldVector(5, 0, 0)) == SeatOutcome.Moving, "Passenger moving exit rejected");
        Check(vehicle.Request(passenger, 1, 4, vehicle.Revision, -1, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.Accepted && vehicle.Driver == 1, "Passenger exit leaves driver seated");
        Check(vehicle.Request(passenger, 1, 5, vehicle.Revision, 1, WorldVector.Zero, WorldVector.Zero) == SeatOutcome.Accepted, "Passenger can reenter");
        Check(vehicle.Disconnect(1) && vehicle.Driver == 0 && vehicle.SeatOf(2) == 1 && vehicle.ReadInput(101).Brake == 1, "Driver disconnect brakes while passenger remains");
        Check(vehicle.Disconnect(2) && vehicle.SeatOf(2) == -1, "Passenger disconnect releases seat");
        VehicleMessage state = new VehicleMessage { Kind = VehicleMessageKind.State, Revision = 9, Driver = 1, Passenger = 2, Gear = 3 };
        VehicleMessage decoded = VehicleMessages.Decode(VehicleMessages.Encode(state));
        Check(decoded.Passenger == 2 && decoded.Driver == 1, "Passenger baseline roundtrip for late join");
        state.Passenger = 1; bool rejected = false;
        try { VehicleMessages.Decode(VehicleMessages.Encode(state)); } catch (FormatException) { rejected = true; }
        Check(rejected, "Duplicate occupants rejected");
        state.Kind = VehicleMessageKind.Request; state.Sequence = 1; state.Seat = 1;
        Check(VehicleMessages.Decode(VehicleMessages.Encode(state)).Seat == 1, "Passenger request accepted by codec");
    }
    private static void CodecAndOrientation()
    {
        VehicleMessage[] messages = {
            new VehicleMessage { Kind = VehicleMessageKind.State, Revision = 1, Driver = 2, Gear = 3 },
            new VehicleMessage { Kind = VehicleMessageKind.Request, Revision = 1, Sequence = 1, Seat = -1 },
            new VehicleMessage { Kind = VehicleMessageKind.Result, Revision = 2, Sequence = 1, Outcome = SeatOutcome.Accepted, Driver = 2 },
            new VehicleMessage { Kind = VehicleMessageKind.Input, Revision = 2, Sequence = 8, Input = new VehicleInput { Throttle = 1, Steering = -1, Gear = 2 } }
        };
        foreach (VehicleMessage m in messages)
        {
            byte[] bytes = VehicleMessages.Encode(m);
            VehicleMessage roundtrip = VehicleMessages.Decode(bytes);
            Check(roundtrip.Kind == m.Kind && roundtrip.Revision == m.Revision && roundtrip.Sequence == m.Sequence, "Vehicle wire roundtrip");
            for (int length = 0; length < bytes.Length; length++)
            {
                byte[] cut = new byte[length]; Array.Copy(bytes, cut, length); bool rejected = false;
                try { VehicleMessages.Decode(cut); } catch (System.IO.IOException) { rejected = true; } catch (FormatException) { rejected = true; }
                Check(rejected, "Truncated vehicle packet refused");
            }
        }
        WorldRotation turn = new WorldRotation(0, 1, 0, 0);
        EntityState body = new EntityState(1, 1, "van", "vehicle", EntityLifecycle.Free, 1, 1, 5, 0, WorldVector.Zero, WorldVector.Zero, turn, new WorldVector(0, 2, 0));
        WorldMessage snapshot = new WorldMessage { Kind = WorldMessageKind.Snapshot, Snapshot = new WorldSnapshot(1, 1, 0, new EntityState[] { body }, new PlayerState[0]) };
        EntityState decoded = WorldMessages.Decode(WorldMessages.Encode(snapshot), 1, 1).Snapshot.Entities[0];
        Check(decoded.Rotation.Same(turn) && decoded.AngularVelocity.Y == 2, "Body rotation and spin survive baseline");
        Check(!new WorldRotation(0, 0, 0, 0).IsValid && !new WorldRotation(Single.NaN, 0, 0, 1).IsValid, "Invalid orientation rejected");
    }
    public static int Main()
    {
        try { Seats(); Passengers(); CodecAndOrientation(); Console.WriteLine("PASS: " + assertions + " vehicle authority and wire assertions; game adapter unverified."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
