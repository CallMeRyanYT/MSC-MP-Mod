using System;
using System.Collections.Generic;
using System.Threading;
using MSCMP.Core;

internal static class AuthorityTests
{
    private delegate void TestAction();
    private static int assertions;
    private static readonly WorldVector Origin = WorldVector.Zero;
    private static readonly AuthorityLimits Limits = new AuthorityLimits(3, 2, 12, 10000, 10, 4, 1000);
    private static void Expect(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Reject(TestAction action, string message)
    {
        bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
        Expect(rejected, message);
    }
    private static AuthorityWorld World(int replay, int tail)
    {
        AuthorityWorld world = new AuthorityWorld(7, Limits, replay, tail);
        world.ConnectPlayer(1, 1, Origin, true); return world;
    }
    private static AuthorityCommand Command(AuthorityWorld world, CommandKind kind, ulong player, ulong sequence, EntityState entity, WorldVector position, WorldVector velocity)
    {
        return new AuthorityCommand(kind, world.Generation, player, world.GetPlayer(player).ConnectionGeneration, sequence, entity.Id, entity.Revision, entity.OwnershipEpoch, position, velocity);
    }
    private static AuthorityCommand Pick(AuthorityWorld world, ulong player, ulong sequence, EntityState entity) { return Command(world, CommandKind.Pickup, player, sequence, entity, Origin, Origin); }
    private static void Ready(AuthorityWorld world, ulong id, ulong connection)
    {
        WorldSnapshot snapshot = world.BeginJoin(id, connection);
        CatchUpBatch batch = world.ReadCatchUp(id, connection, snapshot.SnapshotId, snapshot.Watermark);
        Expect(batch.Status == CatchUpStatus.Success, "Empty join catch-up succeeds.");
        Expect(world.ConfirmReady(id, connection, snapshot.SnapshotId, batch.ToWatermark).Status == ReadyStatus.Ready, "Readiness follows the snapshot barrier.");
    }
    private static void CheckInvariants(AuthorityWorld world)
    {
        WorldSnapshot snapshot = world.CaptureSnapshot();
        Dictionary<ulong, EntityState> entities = new Dictionary<ulong, EntityState>();
        Dictionary<ulong, PlayerState> players = new Dictionary<ulong, PlayerState>();
        foreach (EntityState entity in snapshot.Entities) entities.Add(entity.Id, entity);
        foreach (PlayerState player in snapshot.Players) players.Add(player.Id, player);
        foreach (EntityState entity in entities.Values)
        {
            if (entity.Lifecycle == EntityLifecycle.Held) Expect(players.ContainsKey(entity.HolderId) && players[entity.HolderId].HandEntityId == entity.Id && players[entity.HolderId].Connected, "Held item and connected hand agree.");
            else Expect(entity.HolderId == 0, "Non-held entity has no holder.");
        }
        foreach (PlayerState player in players.Values) if (player.HandEntityId != 0) Expect(entities[player.HandEntityId].HolderId == player.Id && entities[player.HandEntityId].Lifecycle == EntityLifecycle.Held, "Every occupied hand refers back to its sole entity.");
    }

    private static void ConcurrentPickupAndReplay()
    {
        for (int iteration = 0; iteration < 16; iteration++)
        {
            AuthorityWorld world = World(2, 32);
            world.ConnectPlayer(2, 1, Origin, false); Ready(world, 2, 1);
            EntityState entity = world.RegisterAuthoredItem("verified-item", "test-box", Origin);
            AuthorityCommand first = Pick(world, 1, 1, entity), second = Pick(world, 2, 1, entity);
            ManualResetEvent start = new ManualResetEvent(false);
            CommandResult a = null, b = null;
            Thread one = new Thread(delegate() { start.WaitOne(); a = world.Process(first); });
            Thread two = new Thread(delegate() { start.WaitOne(); b = world.Process(second); });
            one.Start(); two.Start(); start.Set(); one.Join(); two.Join(); start.Close();
            Expect(a.Accepted != b.Accepted, "Serialized concurrent pickups have exactly one winner.");
            CommandResult winner = a.Accepted ? a : b;
            AuthorityCommand winningCommand = a.Accepted ? first : second;
            ulong mark = world.Watermark;
            Expect(Object.ReferenceEquals(world.Process(winningCommand), winner), "Lost grant acknowledgement replays the original result.");
            Expect(world.Watermark == mark && world.EntityCount == 1, "A repeated pickup has no second effect or entity.");
            CheckInvariants(world);
        }
        AuthorityWorld replayWorld = World(2, 64);
        EntityState item = replayWorld.SpawnItem("test-box", Origin);
        AuthorityCommand pickup = Pick(replayWorld, 1, 1, item);
        CommandResult accepted = replayWorld.Process(pickup);
        AuthorityCommand changedRequest = Command(replayWorld, CommandKind.Throw, 1, 1, accepted.Entity, Origin, new WorldVector(1, 0, 0));
        Expect(replayWorld.Process(changedRequest).Outcome == CommandOutcome.RequestConflict, "A request identity cannot be reused for a different action.");
        Expect(replayWorld.Process(Command(replayWorld, CommandKind.Drop, 1, 2, accepted.Entity, Origin, Origin)).Accepted, "Confirmed drop succeeds.");
        Expect(replayWorld.Process(Pick(replayWorld, 1, 3, replayWorld.GetEntity(item.Id))).Accepted, "Immediate pickup after release succeeds.");
        ulong current = replayWorld.Watermark;
        Expect(replayWorld.Process(pickup).Outcome == CommandOutcome.ObsoleteSequence, "Evicted requests are rejected by the retained sequence watermark.");
        Expect(replayWorld.Watermark == current, "An evicted replay cannot repeat an action.");
        AuthorityCommand wrap = new AuthorityCommand(CommandKind.Pickup, 7, 1, 1, UInt64.MaxValue, 999, 1, 1, Origin, Origin);
        Expect(replayWorld.Process(wrap).Outcome == CommandOutcome.UnknownEntity, "Highest non-wrapping sequence is handled.");
        Expect(replayWorld.Process(Pick(replayWorld, 1, 4, replayWorld.GetEntity(item.Id))).Outcome == CommandOutcome.ObsoleteSequence, "Sequence wrap does not bypass obsolete-request rejection.");
    }

    private static void ValidationAndLifecycle()
    {
        AuthorityWorld world = World(32, 64);
        world.ConnectPlayer(2, 1, Origin, false);
        EntityState box = world.SpawnItem("test-box", Origin), other = world.SpawnItem("test-box", Origin);
        Expect(world.Process(new AuthorityCommand(CommandKind.Pickup, 7, 1, 1, 0, box.Id, box.Revision, box.OwnershipEpoch, Origin, Origin)).Outcome == CommandOutcome.InvalidSequence, "Zero request sequence is rejected without entering replay history.");
        Expect(world.Process(Pick(world, 2, 1, box)).Outcome == CommandOutcome.NotReady, "Joining players cannot interact.");
        Ready(world, 2, 1);
        Expect(world.Process(Pick(world, 2, 1, box)).Outcome == CommandOutcome.NotReady, "A cached denial remains the same after readiness changes.");
        EntityState far = world.SpawnItem("test-box", new WorldVector(30, 0, 0));
        Expect(world.Process(Pick(world, 1, 1, far)).Outcome == CommandOutcome.TooFar, "Pickup validates authoritative distance.");
        Expect(!world.AcceptPlayerPose(7, 1, 1, 1, new WorldVector(Single.NaN, 0, 0)), "NaN movement is rejected.");
        Expect(!world.AcceptPlayerPose(7, 1, 1, 1, new WorldVector(Single.PositiveInfinity, 0, 0)), "Infinite movement is rejected.");
        Expect(!world.AcceptPlayerPose(7, 1, 1, 1, new WorldVector(11, 0, 0)), "Movement step bound is enforced.");
        Expect(world.AcceptPlayerPose(7, 1, 1, 1, new WorldVector(1, 0, 0)), "Finite bounded movement is accepted.");
        Expect(!world.AcceptPlayerPose(7, 1, 1, 1, Origin), "Obsolete player poses do not rewind the host.");
        Expect(world.UpdateFreeMotion(7, box.Id, box.OwnershipEpoch, 1, new WorldVector(1, 0, 0), Origin), "Host updates free-item motion.");
        Expect(world.GetEntity(box.Id).Revision == box.Revision, "Motion does not invalidate semantic pickup revision.");
        CommandResult pickup = world.Process(Pick(world, 1, 2, box));
        Expect(pickup.Accepted, "Expected semantic revision survives motion.");
        Expect(world.Process(Pick(world, 1, 3, other)).Outcome == CommandOutcome.HandOccupied, "One player cannot reserve a second item.");
        Expect(world.Process(Command(world, CommandKind.Drop, 2, 2, pickup.Entity, Origin, Origin)).Outcome == CommandOutcome.NotHolder, "Another player cannot release the holder's item.");
        Expect(world.Process(Command(world, CommandKind.Throw, 1, 4, pickup.Entity, Origin, new WorldVector(Single.NaN, 0, 0))).Outcome == CommandOutcome.InvalidValue, "Non-finite throw velocity is rejected.");
        Expect(world.Process(Command(world, CommandKind.Throw, 1, 5, pickup.Entity, Origin, new WorldVector(13, 0, 0))).Outcome == CommandOutcome.InvalidValue, "Excessive throw velocity is rejected.");
        Expect(world.Process(Command(world, CommandKind.Drop, 1, 6, pickup.Entity, Origin, new WorldVector(1, 0, 0))).Outcome == CommandOutcome.InvalidValue, "Drop cannot smuggle throw velocity.");
        Expect(world.Process(Command(world, CommandKind.Drop, 1, 7, pickup.Entity, new WorldVector(10, 0, 0), Origin)).Outcome == CommandOutcome.TooFar, "Release position is checked against approved player position.");
        Expect(world.Process(Command(world, CommandKind.Drop, 1, 8, pickup.Entity, new WorldVector(Single.PositiveInfinity, 0, 0), Origin)).Outcome == CommandOutcome.InvalidValue, "Non-finite release position is rejected.");
        CommandResult thrown = world.Process(Command(world, CommandKind.Throw, 1, 9, pickup.Entity, Origin, new WorldVector(12, 0, 0)));
        Expect(thrown.Accepted && thrown.Entity.OwnershipEpoch == pickup.Entity.OwnershipEpoch + 1 && thrown.Player.HandEntityId == 0, "Throw releases both references and advances ownership epoch atomically.");
        Expect(!world.UpdateFreeMotion(7, box.Id, box.OwnershipEpoch, 100, Origin, Origin), "Motion from a previous ownership epoch is rejected.");
        Expect(!world.UpdateFreeMotion(6, box.Id, thrown.Entity.OwnershipEpoch, 100, Origin, Origin), "Motion from a previous session is rejected.");
        CommandResult nextPickup = world.Process(Pick(world, 2, 3, thrown.Entity));
        Expect(nextPickup.Accepted, "Another player can retrieve the released entity immediately.");
        Expect(world.AcceptPlayerPose(7, 2, 1, 1, new WorldVector(2, 0, 0)), "Holder pose advances independently.");
        ulong beforeDisconnect = world.Watermark;
        Expect(world.DisconnectPlayer(2, 1), "Holder disconnect is accepted.");
        Expect(!world.DisconnectPlayer(2, 1) && world.Watermark == beforeDisconnect + 1, "Disconnect cleanup commits exactly once.");
        Expect(world.GetEntity(box.Id).Position.Same(new WorldVector(2, 0, 0)) && world.GetEntity(box.Id).Lifecycle == EntityLifecycle.Free, "Disconnect uses the latest host-approved release location.");
        world.ConnectPlayer(2, 2, Origin, false);
        Expect(world.Process(Pick(world, 2, 1, world.GetEntity(box.Id))).Outcome == CommandOutcome.NotReady, "Reconnect gets a fresh unready request scope.");
        Expect(world.Process(new AuthorityCommand(CommandKind.Pickup, 7, 2, 1, 100, box.Id, world.GetEntity(box.Id).Revision, world.GetEntity(box.Id).OwnershipEpoch, Origin, Origin)).Outcome == CommandOutcome.StaleConnection, "Packets from the replaced connection cannot affect its successor.");
        Expect(!world.DisconnectPlayer(2, 1) && world.GetPlayer(2).Connected, "Late old disconnect cannot remove a replacement connection.");
        Expect(world.Process(new AuthorityCommand(CommandKind.Pickup, 6, 1, 1, 10, box.Id, 1, 1, Origin, Origin)).Outcome == CommandOutcome.StaleWorld, "Old world commands are rejected.");
        Expect(world.RetireEntity(other.Id, other.Revision, true), "Host consumption commits once.");
        Expect(!world.RetireEntity(other.Id, other.Revision, true), "Duplicate retirement cannot repeat consumption.");
        Expect(world.Process(Pick(world, 1, 10, world.GetEntity(other.Id))).Outcome == CommandOutcome.Unavailable, "Consumed tombstones cannot be picked up.");
        EntityState fresh = world.SpawnItem("test-box", Origin);
        CommandResult held = world.Process(Pick(world, 1, 11, fresh));
        Expect(held.Accepted && !world.RetireEntity(fresh.Id, fresh.Revision, false), "Pickup wins over retirement carrying a stale semantic revision.");
        Expect(world.RetireEntity(fresh.Id, held.Entity.Revision, false) && world.GetPlayer(1).HandEntityId == 0, "Current host destruction clears a held relationship atomically.");
        EntityState replacement = world.SpawnItem("test-box", Origin);
        Expect(replacement.Id > fresh.Id && world.GetEntity(fresh.Id).Lifecycle == EntityLifecycle.Destroyed, "Destroyed IDs remain tombstones and are never recycled.");
        Expect(world.Process(new AuthorityCommand(CommandKind.Pickup, 7, 1, 1, 12, replacement.Id, replacement.Revision, 999, Origin, Origin)).Outcome == CommandOutcome.StaleOwnership, "A matching semantic revision does not excuse an incorrect ownership epoch.");
        Expect(world.Process(Command(world, (CommandKind)99, 1, 13, replacement, Origin, Origin)).Outcome == CommandOutcome.Unsupported, "Unknown command enums are denied.");
        Reject(delegate() { world.ConnectPlayer(2, 2, Origin, false); }, "A second connection cannot replace a still-active identity.");
        Reject(delegate() { world.ConnectPlayer(3, 1, Origin, true); }, "A second participating host cannot enter one authority world.");
        Reject(delegate() { world.SpawnItem("test-box", new WorldVector(Single.NaN, 0, 0)); }, "Non-finite host spawn positions fail before allocating identity.");
        CheckInvariants(world);
    }

    private static void Mapping()
    {
        AuthorityWorld world = World(8, 32);
        EntityState a = world.RegisterAuthoredItem("persistent-A", "same-name-box", Origin);
        EntityState b = world.RegisterAuthoredItem("persistent-B", "same-name-box", Origin);
        object localA = new object(), localB = new object();
        Dictionary<ulong, object> mapped = AuthoredMapping.Resolve(new EntityState[] { a, b }, new AuthoredCandidate[] { new AuthoredCandidate("persistent-B", "same-name-box", localB), new AuthoredCandidate("persistent-A", "same-name-box", localA) });
        Expect(Object.ReferenceEquals(mapped[a.Id], localA) && Object.ReferenceEquals(mapped[b.Id], localB), "Same-named objects resolve by explicit authored identity despite reversed discovery order.");
        Reject(delegate() { world.RegisterAuthoredItem("persistent-A", "same-name-box", Origin); }, "Duplicate host authored keys fail closed.");
        Reject(delegate() { AuthoredMapping.Resolve(new EntityState[] { a }, new AuthoredCandidate[] { new AuthoredCandidate("persistent-A", "same-name-box", localA), new AuthoredCandidate("persistent-A", "same-name-box", localB) }); }, "Ambiguous local authored keys fail closed.");
        Reject(delegate() { AuthoredMapping.Resolve(new EntityState[] { a, b }, new AuthoredCandidate[] { new AuthoredCandidate("persistent-A", "same-name-box", localA), new AuthoredCandidate("persistent-B", "same-name-box", localA) }); }, "Two identities cannot bind the same local object.");
        Reject(delegate() { AuthoredMapping.Resolve(new EntityState[] { a }, new AuthoredCandidate[0]); }, "Missing mappings do not guess nearest coordinates.");
        Reject(delegate() { AuthoredMapping.Resolve(new EntityState[] { a }, new AuthoredCandidate[] { new AuthoredCandidate("persistent-A", "wrong-type", localA) }); }, "Archetype mismatch fails mapping.");
    }

    private static void SnapshotCatchUpAndReadiness()
    {
        AuthorityWorld world = World(8, 32);
        EntityState box = world.RegisterAuthoredItem("item-A", "test-box", Origin);
        world.ConnectPlayer(2, 1, Origin, false);
        WorldSnapshot snapshot = world.BeginJoin(2, 1);
        EntityState[] exposed = snapshot.Entities; exposed[0] = null;
        Expect(snapshot.Entities[0] != null, "Snapshot arrays cannot be mutated by a caller.");
        ReplicaWorld replica = new ReplicaWorld(7);
        Expect(replica.LoadSnapshot(snapshot) == ApplyOutcome.Applied && !replica.Ready, "A baseline alone never enables interaction.");
        Expect(world.ConfirmReady(2, 1, snapshot.SnapshotId, snapshot.Watermark).Status == ReadyStatus.InvalidTransfer, "Readiness cannot skip the explicit catch-up barrier.");
        CommandResult held = world.Process(Pick(world, 1, 1, box));
        EntityState spawned = world.SpawnItem("test-box", Origin);
        world.RetireEntity(spawned.Id, spawned.Revision, true);
        Expect(snapshot.Entities.Length == 1 && snapshot.Entities[0].Lifecycle == EntityLifecycle.Free, "An in-flight snapshot is detached from subsequent pickup/spawn/consume mutations.");
        CatchUpBatch first = world.ReadCatchUp(2, 1, snapshot.SnapshotId, snapshot.Watermark);
        Expect(first.Status == CatchUpStatus.Success && first.Events.Length == 3, "Catch-up retains every committed event after the coherent watermark.");
        Expect(Object.ReferenceEquals(first, world.ReadCatchUp(2, 1, snapshot.SnapshotId, snapshot.Watermark)), "Lost catch-up response can be replayed without advancing over a gap.");
        Expect(replica.ApplyCatchUp(first) == ApplyOutcome.Applied, "Replica applies the complete ordered tail.");
        Expect(replica.GetEntity(box.Id).HolderId == 1 && replica.GetEntity(spawned.Id).Lifecycle == EntityLifecycle.Consumed, "A slow joining client converges on held and consumed state.");
        world.Process(Command(world, CommandKind.Drop, 1, 2, held.Entity, Origin, Origin));
        Expect(world.ConfirmReady(2, 1, snapshot.SnapshotId, first.ToWatermark).Status == ReadyStatus.MoreChanges, "A mutation during acknowledgement prevents premature readiness.");
        CatchUpBatch second = world.ReadCatchUp(2, 1, snapshot.SnapshotId, first.ToWatermark);
        Expect(replica.ApplyCatchUp(second) == ApplyOutcome.Applied, "Final barrier includes the intervening release.");
        ReadyGrant grant = world.ConfirmReady(2, 1, snapshot.SnapshotId, second.ToWatermark);
        Expect(grant.Status == ReadyStatus.Ready && replica.ApplyReady(grant, 2, 1), "Ready grant itself is an ordered committed transition.");
        ulong readyWatermark = world.Watermark;
        Expect(Object.ReferenceEquals(grant, world.ConfirmReady(2, 1, snapshot.SnapshotId, second.ToWatermark)) && world.Watermark == readyWatermark, "Lost readiness acknowledgement replays exactly one grant.");
        Expect(replica.ApplyReady(grant, 2, 1) && replica.Ready, "Repeated readiness grant is idempotent.");
        Expect(replica.GetEntity(box.Id).Lifecycle == EntityLifecycle.Free && replica.Watermark == world.Watermark, "Ready world exactly matches the host's final semantic state.");
        world.SpawnItem("test-box", Origin); world.SpawnItem("test-box", Origin);
        CommitEvent[] live = world.ReadEventsSince(readyWatermark).Events;
        Expect(replica.Apply(live[1]) == ApplyOutcome.Gap && !replica.Ready, "A missing live commit disables client readiness.");
        Expect(!replica.ApplyReady(grant, 2, 1), "An old Ready grant cannot hide a detected stream gap.");

        AuthorityWorld overflow = World(4, 2);
        overflow.ConnectPlayer(2, 1, Origin, false);
        WorldSnapshot original = overflow.BeginJoin(2, 1);
        overflow.SpawnItem("test-box", Origin); overflow.SpawnItem("test-box", Origin); overflow.SpawnItem("test-box", Origin);
        Expect(overflow.RetainedEventCount == 2, "Commit tail memory remains bounded.");
        Expect(overflow.ReadCatchUp(2, 1, original.SnapshotId, original.Watermark).Status == CatchUpStatus.RestartRequired, "Tail overflow requires a clean snapshot restart.");
        Expect(overflow.ConfirmReady(2, 1, original.SnapshotId, original.Watermark).Status == ReadyStatus.RestartRequired && !overflow.GetPlayer(2).Ready, "An overflowed transfer never becomes ready.");
        WorldSnapshot restarted = overflow.BeginJoin(2, 1);
        Expect(restarted.SnapshotId != original.SnapshotId && restarted.Entities.Length == 3, "Restart takes a new complete immutable baseline.");
        Expect(overflow.ReadCatchUp(2, 1, original.SnapshotId, original.Watermark).Status == CatchUpStatus.InvalidTransfer, "Cancelled snapshot packets cannot affect a replacement transfer.");
        CatchUpBatch clean = overflow.ReadCatchUp(2, 1, restarted.SnapshotId, restarted.Watermark);
        Expect(overflow.ConfirmReady(2, 1, restarted.SnapshotId, clean.ToWatermark).Status == ReadyStatus.Ready, "A clean restart can cross the readiness barrier.");
    }

    private static void ReplicaFreshnessAndTombstones()
    {
        AuthorityWorld world = World(8, 32);
        EntityState original = world.SpawnItem("test-box", Origin);
        ReplicaWorld replica = new ReplicaWorld(7);
        WorldSnapshot snapshot = world.CaptureSnapshot(); replica.LoadSnapshot(snapshot);
        CommandResult held = world.Process(Pick(world, 1, 1, original));
        Expect(replica.Apply(held.Commit) == ApplyOutcome.Applied, "Replica commits a coherent holder and hand together.");
        CommandResult drop = world.Process(Command(world, CommandKind.Drop, 1, 2, held.Entity, Origin, Origin));
        Expect(replica.Apply(drop.Commit) == ApplyOutcome.Applied, "Replica applies release.");
        Expect(replica.Apply(held.Commit) == ApplyOutcome.DuplicateOrStale && replica.GetEntity(original.Id).Lifecycle == EntityLifecycle.Free, "Delayed held state cannot reattach an item after release.");
        EntityState obsoleteMotion = new EntityState(7, original.Id, original.AuthoredKey, original.Archetype, EntityLifecycle.Free, drop.Entity.Revision, original.OwnershipEpoch, 999, 0, Origin, Origin);
        Expect(replica.Apply(new CommitEvent(7, replica.Watermark + 1, CommitKind.EntityMotion, 0, 0, obsoleteMotion, null)) == ApplyOutcome.InvalidState, "A fresh envelope cannot smuggle an old ownership epoch.");
        world.RetireEntity(original.Id, drop.Entity.Revision, false);
        CommitEvent tombstone = world.ReadEventsSince(replica.Watermark).Events[0];
        Expect(replica.Apply(tombstone) == ApplyOutcome.Applied, "Replica stores destruction as a tombstone.");
        EntityState resurrected = new EntityState(7, original.Id, original.AuthoredKey, original.Archetype, EntityLifecycle.Free, tombstone.Entity.Revision + 1, tombstone.Entity.OwnershipEpoch + 1, 0, 0, Origin, Origin);
        Expect(replica.Apply(new CommitEvent(7, replica.Watermark + 1, CommitKind.Spawn, 0, 0, resurrected, null)) == ApplyOutcome.InvalidState, "Tombstone identity cannot be reused by a late or forged spawn.");
        Expect(replica.LoadSnapshot(new WorldSnapshot(7, snapshot.SnapshotId + 100, replica.Watermark + 1, new EntityState[] { resurrected }, world.CaptureSnapshot().Players)) == ApplyOutcome.InvalidState, "A repair snapshot cannot resurrect retained tombstones.");
        Expect(replica.LoadSnapshot(snapshot) == ApplyOutcome.DuplicateOrStale, "An older snapshot cannot rewind current state.");
        Expect(replica.LoadSnapshot(new WorldSnapshot(7, 999, replica.Watermark + 1, new EntityState[0], world.CaptureSnapshot().Players)) == ApplyOutcome.InvalidState, "A repair snapshot cannot silently erase a known identity tombstone.");
        EntityState wrongGeneration = new EntityState(6, 1, "", "test-box", EntityLifecycle.Free, 1, 1, 0, 0, Origin, Origin);
        Expect(replica.Apply(new CommitEvent(6, replica.Watermark + 1, CommitKind.Spawn, 0, 0, wrongGeneration, null)) == ApplyOutcome.WrongGeneration, "Previous-session events do not mutate a current replica.");
        Reject(delegate() { new EntityState(7, 9, "", "test-box", (EntityLifecycle)99, 1, 1, 0, 0, Origin, Origin); }, "Unknown lifecycle enums are rejected.");
        Reject(delegate() { new EntityState(7, 9, "", "test-box", EntityLifecycle.Held, 1, 1, 0, 0, Origin, Origin); }, "A held record must have a holder.");
        ReplicaWorld broken = new ReplicaWorld(7);
        EntityState badHeld = new EntityState(7, 8, "", "test-box", EntityLifecycle.Held, 2, 2, 0, 3, Origin, Origin);
        Expect(broken.LoadSnapshot(new WorldSnapshot(7, 1, 0, new EntityState[] { badHeld }, new PlayerState[0])) == ApplyOutcome.InvalidState, "Snapshots with unresolved holder dependencies never load.");
        AuthorityWorld movement = World(8, 32);
        EntityState moving = movement.SpawnItem("test-box", Origin);
        ReplicaWorld movementReplica = new ReplicaWorld(7); movementReplica.LoadSnapshot(movement.CaptureSnapshot());
        EntityState counterfeitMotion = new EntityState(7, moving.Id, "", "test-box", EntityLifecycle.Free, 1, 1, 0, 0, new WorldVector(1, 0, 0), Origin);
        Expect(movementReplica.LoadSnapshot(new WorldSnapshot(7, 99, movement.Watermark + 1, new EntityState[] { counterfeitMotion }, movement.CaptureSnapshot().Players)) == ApplyOutcome.InvalidState, "A snapshot cannot change pose under an unchanged motion sequence.");
        PlayerState counterfeitPlayer = new PlayerState(1, 1, 1, 0, 0, true, true, new WorldVector(1, 0, 0));
        Expect(movementReplica.LoadSnapshot(new WorldSnapshot(7, 100, movement.Watermark + 1, new EntityState[] { moving }, new PlayerState[] { counterfeitPlayer })) == ApplyOutcome.InvalidState, "A snapshot cannot change player pose under an unchanged pose sequence.");
        EntityState partialHeld = new EntityState(7, moving.Id, "", "test-box", EntityLifecycle.Held, 2, 2, 0, 1, Origin, Origin);
        PlayerState staleHand = new PlayerState(1, 1, 2, 0, 0, true, true, Origin);
        Expect(movementReplica.Apply(new CommitEvent(7, movementReplica.Watermark + 1, CommitKind.Pickup, 1, 1, partialHeld, staleHand)) == ApplyOutcome.InvalidState && movementReplica.GetEntity(moving.Id).Lifecycle == EntityLifecycle.Free, "A partial holder/hand transaction is rejected without changing either record.");
    }

    private static void RepeatedContention()
    {
        AuthorityWorld world = World(4, 16);
        world.ConnectPlayer(2, 1, Origin, false); Ready(world, 2, 1);
        EntityState item = world.SpawnItem("test-box", Origin);
        ulong[] sequence = new ulong[] { 0, 0, 0 };
        Random random = new Random(28417);
        ReplicaWorld replica = new ReplicaWorld(7); replica.LoadSnapshot(world.CaptureSnapshot());
        for (int i = 0; i < 500; i++)
        {
            ulong player = (ulong)(1 + random.Next(2));
            EntityState before = world.GetEntity(item.Id);
            AuthorityCommand request = Command(world, before.Lifecycle == EntityLifecycle.Free ? CommandKind.Pickup : CommandKind.Drop, player, ++sequence[player], before, Origin, Origin);
            CommandResult result = world.Process(request);
            ulong committed = world.Watermark;
            Expect(Object.ReferenceEquals(world.Process(request), result) && world.Watermark == committed, "Seeded repeated request never duplicates a transaction.");
            if (result.Accepted) Expect(replica.Apply(result.Commit) == ApplyOutcome.Applied, "Replica follows repeated ownership transfers without gaps.");
            Expect(replica.GetEntity(item.Id).Revision == world.GetEntity(item.Id).Revision && replica.GetEntity(item.Id).HolderId == world.GetEntity(item.Id).HolderId, "Host and replica retain identical semantic state under contention.");
            CheckInvariants(world);
        }
    }

    private static void PlayerCorrections()
    {
        PlayerPoseHistory history = new PlayerPoseHistory(); WorldVector corrected;
        history.Sent(1, new WorldVector(100, 0, 0)); history.Sent(2, new WorldVector(101, 0, 0));
        Expect(history.Receive(1, false, Origin, new WorldVector(102, 0, 0), out corrected) && corrected.X == 2, "Rejected sample preserves two metres of subsequent walking.");
        Expect(!history.Receive(2, false, Origin, corrected, out corrected), "Old in-flight rejection cannot apply correction twice.");
        history.Sent(3, new WorldVector(2, 0, 0));
        Expect(!history.Receive(3, true, new WorldVector(2, 0, 0), new WorldVector(3, 0, 0), out corrected) && history.Count == 0, "Accepted pose acknowledges history without rubber-banding.");
        history.Sent(4, new WorldVector(2, 0, 0));
        Expect(!history.Receive(4, false, new WorldVector(Single.NaN, 0, 0), Origin, out corrected), "Invalid receipt cannot mutate history.");
        Expect(history.Receive(4, false, Origin, new WorldVector(100, 0, 0), out corrected) && corrected.Same(Origin), "Unbounded local delta snaps to accepted position.");
        history.Clear(); history.Sent(UInt32.MaxValue, Origin); history.Sent(0, Origin);
        Expect(!history.Receive(UInt32.MaxValue, true, Origin, Origin, out corrected), "Receipt before wrap accepted.");
        Expect(history.Receive(0, false, new WorldVector(1, 0, 0), Origin, out corrected) && corrected.X == 1, "Receipt wrap advances rather than becoming permanently stale.");
        history.Clear();
        for (uint i = 0; i < 200; i++) history.Sent(i, Origin);
        Expect(history.Count == 64 && !history.Receive(1, false, Origin, Origin, out corrected), "History stays bounded and ignores evicted samples.");
        AuthorityWorld world = World(8, 32); EntityState item = world.SpawnItem("test-box", Origin);
        Expect(!world.AcceptPlayerPose(7, 1, 1, 1, new WorldVector(1000, 0, 0)), "Host rejects overlong movement.");
        Expect(world.AcceptPlayerPose(7, 1, 1, 2, Origin), "Corrected owner can submit a legitimate pose.");
        Expect(world.Process(Command(world, CommandKind.Pickup, 1, 1, item, Origin, Origin)).Accepted, "Corrected owner can pick up an item at the host position.");
    }

    private static int Main()
    {
        try
        {
            PlayerCorrections(); ConcurrentPickupAndReplay(); ValidationAndLifecycle(); Mapping(); SnapshotCatchUpAndReadiness(); ReplicaFreshnessAndTombstones(); RepeatedContention();
            Console.WriteLine("PASS: " + assertions + " authority, identity, replay, lifecycle, and snapshot assertions. These are headless core tests, not in-game multiplayer evidence.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }
}
