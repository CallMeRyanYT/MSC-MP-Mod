using System;
using System.Collections.Generic;

namespace MSCMP.Core
{
    // Every mutation, snapshot capture, and join barrier shares this serialization boundary.
    // Adapters still call this from Unity's main thread; the lock also makes headless races testable.
    public sealed class AuthorityWorld
    {
        private sealed class ReplayEntry
        {
            internal readonly AuthorityCommand Command;
            internal readonly CommandResult Result;
            internal ReplayEntry(AuthorityCommand command, CommandResult result) { Command = command; Result = result; }
        }
        private sealed class Peer
        {
            internal PlayerState State;
            internal ulong HighestRequest;
            internal readonly Dictionary<ulong, ReplayEntry> Replay = new Dictionary<ulong, ReplayEntry>();
            internal readonly Queue<ulong> ReplayOrder = new Queue<ulong>();
            internal Peer(PlayerState state) { State = state; }
        }
        private sealed class Join
        {
            internal readonly ulong ConnectionGeneration;
            internal readonly ulong SnapshotId;
            internal ulong OfferedWatermark;
            internal bool CatchUpOffered;
            internal bool Failed;
            internal CatchUpBatch LastBatch;
            internal ReadyGrant Grant;
            internal Join(ulong connectionGeneration, WorldSnapshot snapshot) { ConnectionGeneration = connectionGeneration; SnapshotId = snapshot.SnapshotId; OfferedWatermark = snapshot.Watermark; }
        }

        private readonly object sync = new object();
        private readonly ulong generation;
        private readonly AuthorityLimits limits;
        private readonly int replayCapacity;
        private readonly int eventCapacity;
        private readonly Dictionary<ulong, EntityState> entities = new Dictionary<ulong, EntityState>();
        private readonly Dictionary<string, ulong> authoredKeys = new Dictionary<string, ulong>(StringComparer.Ordinal);
        private readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private readonly Dictionary<ulong, Join> joins = new Dictionary<ulong, Join>();
        private readonly Queue<CommitEvent> events = new Queue<CommitEvent>();
        private ulong nextEntityId = 1;
        private ulong nextSnapshotId = 1;
        private ulong watermark;
        private ulong hostPlayer;
        // A game adapter can restrict an otherwise valid command (seated players,
        // registered vehicle bodies, unsupported use state) before it commits.
        public Func<AuthorityCommand, bool> InteractionAllowed;

        public AuthorityWorld(ulong generation, AuthorityLimits limits, int replayCapacity, int eventCapacity)
        {
            if (generation == 0 || limits == null || replayCapacity < 1 || replayCapacity > 65536 || eventCapacity < 1 || eventCapacity > 100000) throw new ArgumentException("Invalid authority configuration.");
            this.generation = generation; this.limits = limits; this.replayCapacity = replayCapacity; this.eventCapacity = eventCapacity;
        }
        public ulong Generation { get { return generation; } }
        public ulong Watermark { get { lock (sync) { return watermark; } } }
        public int EntityCount { get { lock (sync) { return entities.Count; } } }
        public int RetainedEventCount { get { lock (sync) { return events.Count; } } }
        public EntityState GetEntity(ulong id) { lock (sync) { EntityState entity; return entities.TryGetValue(id, out entity) ? entity : null; } }
        public PlayerState GetPlayer(ulong id) { lock (sync) { Peer peer; return peers.TryGetValue(id, out peer) ? peer.State : null; } }

        // Only the local participating host starts Ready. Network peers always take BeginJoin.
        // Identity/token authentication and limits on connection attempts belong to the transport adapter.
        public PlayerState ConnectPlayer(ulong playerId, ulong connectionGeneration, WorldVector position, bool localHost)
        {
            lock (sync)
            {
                if (playerId == 0 || connectionGeneration == 0 || !position.Within(limits.WorldCoordinate)) throw new ArgumentException("Invalid player connection.");
                Peer previous;
                bool known = peers.TryGetValue(playerId, out previous);
                if (known && (previous.State.Connected || connectionGeneration <= previous.State.ConnectionGeneration)) throw new InvalidOperationException("Connection is active or obsolete; disconnect the old generation first.");
                // Disconnected identity tombstones are retained, preventing an old connection from becoming new again.
                if (!known && peers.Count >= limits.Players) throw new InvalidOperationException("Session identity capacity reached.");
                if (localHost && hostPlayer != 0 && hostPlayer != playerId) throw new InvalidOperationException("The world already has a participating host.");
                if (!localHost && hostPlayer == playerId) throw new InvalidOperationException("Host identity cannot reconnect as a remote peer.");
                PlayerState state = new PlayerState(playerId, connectionGeneration, known ? checked(previous.State.Revision + 1) : 1, 0, 0, true, localHost, position);
                Peer peer = new Peer(state);
                Commit(CommitKind.Connect, null, state, 0, 0);
                peers[playerId] = peer;
                joins.Remove(playerId);
                if (localHost) hostPlayer = playerId;
                return state;
            }
        }

        public EntityState RegisterAuthoredItem(string authoredKey, string archetype, WorldVector position)
        {
            EntityState.ValidateLabel(authoredKey, false);
            lock (sync)
            {
                if (authoredKeys.ContainsKey(authoredKey)) throw new InvalidOperationException("Ambiguous authored key: " + authoredKey);
                EntityState state = Spawn(archetype, authoredKey, position);
                authoredKeys.Add(authoredKey, state.Id);
                return state;
            }
        }

        // Install normalized checkpoint entities before connecting any new peers.
        // Old player identities, credentials, seats and request histories never return.
        public void RestoreEntities(WorldSnapshot snapshot)
        {
            lock (sync)
            {
                if (snapshot == null || snapshot.Generation != generation || entities.Count != 0 || peers.Count != 0 || watermark != 0)
                    throw new InvalidOperationException("Restore requires an empty world in the reserved generation.");
                EntityState[] restored = snapshot.Entities;
                if (restored.Length > limits.Entities) throw new FormatException("Restore capacity exceeded.");
                Dictionary<ulong, bool> ids = new Dictionary<ulong, bool>();
                Dictionary<string, bool> keys = new Dictionary<string, bool>(StringComparer.Ordinal);
                ulong next = 1;
                foreach (EntityState state in restored)
                {
                    if (state == null || state.Generation != generation || state.Id == UInt64.MaxValue || state.HolderId != 0
                        || state.Lifecycle != EntityLifecycle.Free || !state.Position.Within(limits.WorldCoordinate)
                        || String.IsNullOrEmpty(state.AuthoredKey) || ids.ContainsKey(state.Id) || keys.ContainsKey(state.AuthoredKey))
                        throw new FormatException("Unsupported or duplicate restored entity.");
                    ids.Add(state.Id, true); keys.Add(state.AuthoredKey, true); next = Math.Max(next, state.Id + 1);
                }
                foreach (EntityState state in restored)
                {
                    // A stopped world is safe to resume even if the save captured motion.
                    EntityState stopped = new EntityState(generation, state.Id, state.AuthoredKey, state.Archetype, EntityLifecycle.Free,
                        state.Revision, state.OwnershipEpoch, 0, 0, state.Position, WorldVector.Zero, state.Rotation, WorldVector.Zero);
                    entities.Add(state.Id, stopped); authoredKeys.Add(state.AuthoredKey, state.Id);
                    Commit(CommitKind.Spawn, stopped, null, 0, 0);
                }
                nextEntityId = next;
            }
        }

        // A verified host factory calls this only after successfully preparing a supported item.
        public EntityState SpawnItem(string archetype, WorldVector position)
        {
            lock (sync) { return Spawn(archetype, String.Empty, position); }
        }
        private EntityState Spawn(string archetype, string authoredKey, WorldVector position)
        {
            if (!position.Within(limits.WorldCoordinate)) throw new ArgumentException("Invalid spawn position.");
            if (entities.Count >= limits.Entities) throw new InvalidOperationException("Entity capacity reached; tombstones cannot be recycled.");
            if (nextEntityId == UInt64.MaxValue) throw new InvalidOperationException("Entity identity space exhausted.");
            EntityState state = new EntityState(generation, nextEntityId, authoredKey, archetype, EntityLifecycle.Free, 1, 1, 0, 0, position, WorldVector.Zero);
            Commit(CommitKind.Spawn, state, null, 0, 0);
            nextEntityId++;
            entities.Add(state.Id, state);
            return state;
        }

        public CommandResult Process(AuthorityCommand command)
        {
            if (command == null) throw new ArgumentNullException("command");
            lock (sync)
            {
                if (command.Generation != generation) return Result(command, CommandOutcome.StaleWorld, null, null, null);
                Peer peer;
                if (!peers.TryGetValue(command.PlayerId, out peer) || !peer.State.Connected || peer.State.ConnectionGeneration != command.ConnectionGeneration) return Result(command, CommandOutcome.StaleConnection, null, null, null);
                if (command.Sequence == 0) return Result(command, CommandOutcome.InvalidSequence, null, peer.State, null);
                ReplayEntry replay;
                if (peer.Replay.TryGetValue(command.Sequence, out replay)) return replay.Command.Same(command) ? replay.Result : Result(command, CommandOutcome.RequestConflict, null, peer.State, null);
                if (command.Sequence <= peer.HighestRequest) return Result(command, CommandOutcome.ObsoleteSequence, null, peer.State, null);
                peer.HighestRequest = command.Sequence;
                CommandResult result = Execute(command, peer);
                peer.Replay.Add(command.Sequence, new ReplayEntry(command, result));
                peer.ReplayOrder.Enqueue(command.Sequence);
                if (peer.ReplayOrder.Count > replayCapacity) peer.Replay.Remove(peer.ReplayOrder.Dequeue());
                return result;
            }
        }

        private CommandResult Execute(AuthorityCommand command, Peer peer)
        {
            if (!peer.State.Ready) return Result(command, CommandOutcome.NotReady, null, peer.State, null);
            if (InteractionAllowed != null && !InteractionAllowed(command)) return Result(command, CommandOutcome.Unsupported, null, peer.State, null);
            EntityState entity;
            if (!entities.TryGetValue(command.EntityId, out entity)) return Result(command, CommandOutcome.UnknownEntity, null, peer.State, null);
            if (command.ExpectedRevision != entity.Revision) return Result(command, CommandOutcome.StaleRevision, entity, peer.State, null);
            if (command.ExpectedOwnershipEpoch != entity.OwnershipEpoch) return Result(command, CommandOutcome.StaleOwnership, entity, peer.State, null);
            EntityState changed;
            PlayerState player;
            CommitKind kind;
            if (command.Kind == CommandKind.Pickup)
            {
                if (entity.Lifecycle != EntityLifecycle.Free) return Result(command, CommandOutcome.Unavailable, entity, peer.State, null);
                if (peer.State.HandEntityId != 0) return Result(command, CommandOutcome.HandOccupied, entity, peer.State, null);
                if (WorldVector.DistanceSquared(peer.State.Position, entity.Position) > (double)limits.PickupDistance * limits.PickupDistance) return Result(command, CommandOutcome.TooFar, entity, peer.State, null);
                changed = entity.Change(EntityLifecycle.Held, peer.State.Id, peer.State.Position, WorldVector.Zero);
                player = peer.State.Change(entity.Id, true, true); kind = CommitKind.Pickup;
            }
            else if (command.Kind == CommandKind.Drop || command.Kind == CommandKind.Throw)
            {
                if (entity.Lifecycle != EntityLifecycle.Held || entity.HolderId != peer.State.Id || peer.State.HandEntityId != entity.Id) return Result(command, CommandOutcome.NotHolder, entity, peer.State, null);
                if (!command.ReleasePosition.Within(limits.WorldCoordinate) || !command.ReleaseVelocity.IsFinite) return Result(command, CommandOutcome.InvalidValue, entity, peer.State, null);
                if (WorldVector.DistanceSquared(peer.State.Position, command.ReleasePosition) > (double)limits.ReleaseDistance * limits.ReleaseDistance) return Result(command, CommandOutcome.TooFar, entity, peer.State, null);
                if ((command.Kind == CommandKind.Drop && command.ReleaseVelocity.LengthSquared != 0) || command.ReleaseVelocity.LengthSquared > (double)limits.ThrowSpeed * limits.ThrowSpeed) return Result(command, CommandOutcome.InvalidValue, entity, peer.State, null);
                changed = entity.Change(EntityLifecycle.Free, 0, command.ReleasePosition, command.ReleaseVelocity);
                player = peer.State.Change(0, true, true); kind = command.Kind == CommandKind.Drop ? CommitKind.Drop : CommitKind.Throw;
            }
            else return Result(command, CommandOutcome.Unsupported, entity, peer.State, null);
            CommitEvent committed = Commit(kind, changed, player, peer.State.Id, command.Sequence);
            entities[entity.Id] = changed; peer.State = player;
            return Result(command, CommandOutcome.Accepted, changed, player, committed);
        }

        private CommandResult Result(AuthorityCommand command, CommandOutcome outcome, EntityState entity, PlayerState player, CommitEvent commit)
        { return new CommandResult(outcome, command.Sequence, watermark, entity, player, commit); }

        // Adapter supplies host-approved coordinates. Step bounds are a second validation layer;
        // game movement timing, terrain, teleport permissions, and corrections stay in that adapter.
        public bool AcceptPlayerPose(ulong worldGeneration, ulong playerId, ulong connectionGeneration, ulong poseSequence, WorldVector position)
        {
            lock (sync)
            {
                Peer peer;
                if (worldGeneration != generation || !peers.TryGetValue(playerId, out peer) || !peer.State.Connected || !peer.State.Ready || connectionGeneration != peer.State.ConnectionGeneration || poseSequence <= peer.State.PoseSequence || !position.Within(limits.WorldCoordinate)) return false;
                if (WorldVector.DistanceSquared(position, peer.State.Position) > (double)limits.PlayerPoseStep * limits.PlayerPoseStep) return false;
                PlayerState changed = peer.State.Move(poseSequence, position);
                Commit(CommitKind.PlayerPose, null, changed, 0, 0); peer.State = changed;
                return true;
            }
        }

        public bool UpdateFreeMotion(ulong worldGeneration, ulong entityId, ulong ownershipEpoch, ulong motionSequence, WorldVector position, WorldVector velocity)
        {
            EntityState entity = GetEntity(entityId);
            return entity != null && UpdateFreeMotion(worldGeneration, entityId, ownershipEpoch, motionSequence, position, velocity, entity.Rotation, entity.AngularVelocity);
        }

        public bool UpdateFreeMotion(ulong worldGeneration, ulong entityId, ulong ownershipEpoch, ulong motionSequence, WorldVector position, WorldVector velocity, WorldRotation rotation, WorldVector angularVelocity)
        {
            lock (sync)
            {
                EntityState entity;
                if (worldGeneration != generation || !entities.TryGetValue(entityId, out entity) || entity.Lifecycle != EntityLifecycle.Free || entity.OwnershipEpoch != ownershipEpoch || motionSequence <= entity.MotionSequence || !position.Within(limits.WorldCoordinate) || !velocity.IsFinite) return false;
                if (!rotation.IsValid || !angularVelocity.IsFinite) return false;
                EntityState changed = entity.Move(motionSequence, position, velocity, rotation, angularVelocity);
                Commit(CommitKind.EntityMotion, changed, null, 0, 0); entities[entityId] = changed;
                return true;
            }
        }

        // Host-only lifecycle operations. Requests from clients must use a supported transaction
        // adapter; these methods are never dispatched directly from a packet type.
        public bool RetireEntity(ulong entityId, ulong expectedRevision, bool consumed)
        {
            lock (sync)
            {
                EntityState entity;
                if (!entities.TryGetValue(entityId, out entity) || entity.Revision != expectedRevision || entity.Lifecycle == EntityLifecycle.Destroyed || entity.Lifecycle == EntityLifecycle.Consumed) return false;
                Peer peer = entity.HolderId == 0 ? null : peers[entity.HolderId];
                PlayerState player = peer == null ? null : peer.State.Change(0, true, peer.State.Ready);
                WorldVector position = peer == null ? entity.Position : peer.State.Position;
                EntityState changed = entity.Change(consumed ? EntityLifecycle.Consumed : EntityLifecycle.Destroyed, 0, position, WorldVector.Zero);
                Commit(consumed ? CommitKind.Consume : CommitKind.Destroy, changed, player, 0, 0);
                entities[entityId] = changed; if (peer != null) peer.State = player;
                return true;
            }
        }

        public bool DisconnectPlayer(ulong playerId, ulong connectionGeneration)
        {
            lock (sync)
            {
                Peer peer;
                if (!peers.TryGetValue(playerId, out peer) || !peer.State.Connected || connectionGeneration != peer.State.ConnectionGeneration) return false;
                EntityState release = null;
                if (peer.State.HandEntityId != 0)
                {
                    EntityState held = entities[peer.State.HandEntityId];
                    release = held.Change(EntityLifecycle.Free, 0, peer.State.Position, WorldVector.Zero);
                }
                PlayerState player = peer.State.Change(0, false, false);
                Commit(CommitKind.Disconnect, release, player, 0, 0);
                if (release != null) entities[release.Id] = release;
                peer.State = player; peer.Replay.Clear(); peer.ReplayOrder.Clear(); joins.Remove(playerId);
                return true;
            }
        }

        public WorldSnapshot CaptureSnapshot() { lock (sync) { return Capture(); } }
        private WorldSnapshot Capture()
        {
            if (nextSnapshotId == UInt64.MaxValue) throw new InvalidOperationException("Snapshot identity space exhausted.");
            EntityState[] entityArray = new EntityState[entities.Count]; entities.Values.CopyTo(entityArray, 0);
            PlayerState[] playerArray = new PlayerState[peers.Count]; int i = 0;
            foreach (Peer peer in peers.Values) playerArray[i++] = peer.State;
            Array.Sort(entityArray, delegate(EntityState a, EntityState b) { return a.Id.CompareTo(b.Id); });
            Array.Sort(playerArray, delegate(PlayerState a, PlayerState b) { return a.Id.CompareTo(b.Id); });
            return new WorldSnapshot(generation, nextSnapshotId++, watermark, entityArray, playerArray);
        }

        public WorldSnapshot BeginJoin(ulong playerId, ulong connectionGeneration)
        {
            lock (sync)
            {
                Peer peer;
                if (!peers.TryGetValue(playerId, out peer) || !peer.State.Connected || peer.State.Ready || peer.State.ConnectionGeneration != connectionGeneration) throw new InvalidOperationException("Only a connected, unready peer can begin a snapshot.");
                WorldSnapshot snapshot = Capture();
                joins[playerId] = new Join(connectionGeneration, snapshot);
                return snapshot;
            }
        }

        // Each call acknowledges the previously offered complete baseline/batch. No sequence
        // gaps are allowed; an expired tail forces a fresh BeginJoin instead of partial readiness.
        public CatchUpBatch ReadCatchUp(ulong playerId, ulong connectionGeneration, ulong snapshotId, ulong appliedWatermark)
        {
            lock (sync)
            {
                Join join;
                if (!TryJoin(playerId, connectionGeneration, snapshotId, out join)) return Batch(CatchUpStatus.InvalidTransfer, snapshotId, appliedWatermark, appliedWatermark, new CommitEvent[0]);
                if (join.Failed) return Batch(CatchUpStatus.RestartRequired, snapshotId, appliedWatermark, appliedWatermark, new CommitEvent[0]);
                if (join.Grant != null) return Batch(CatchUpStatus.InvalidTransfer, snapshotId, appliedWatermark, appliedWatermark, new CommitEvent[0]);
                if (join.LastBatch != null && appliedWatermark == join.LastBatch.FromWatermark && appliedWatermark != join.OfferedWatermark) return join.LastBatch;
                if (appliedWatermark != join.OfferedWatermark) return Batch(CatchUpStatus.InvalidTransfer, snapshotId, appliedWatermark, appliedWatermark, new CommitEvent[0]);
                CatchUpBatch batch = ReadTail(snapshotId, appliedWatermark);
                if (batch.Status != CatchUpStatus.Success) join.Failed = true;
                else { join.OfferedWatermark = batch.ToWatermark; join.CatchUpOffered = true; join.LastBatch = batch; }
                return batch;
            }
        }

        public ReadyGrant ConfirmReady(ulong playerId, ulong connectionGeneration, ulong snapshotId, ulong appliedWatermark)
        {
            lock (sync)
            {
                Join join;
                if (!TryJoin(playerId, connectionGeneration, snapshotId, out join)) return new ReadyGrant(ReadyStatus.InvalidTransfer, snapshotId, null);
                if (join.Failed) return new ReadyGrant(ReadyStatus.RestartRequired, snapshotId, null);
                if (!join.CatchUpOffered || appliedWatermark != join.OfferedWatermark) return new ReadyGrant(ReadyStatus.InvalidTransfer, snapshotId, null);
                if (join.Grant != null) return join.Grant;
                if (appliedWatermark != watermark)
                {
                    if (!TailAvailable(appliedWatermark)) { join.Failed = true; return new ReadyGrant(ReadyStatus.RestartRequired, snapshotId, null); }
                    return new ReadyGrant(ReadyStatus.MoreChanges, snapshotId, null);
                }
                Peer peer = peers[playerId]; PlayerState player = peer.State.Change(0, true, true);
                CommitEvent commit = Commit(CommitKind.Ready, null, player, 0, 0);
                peer.State = player; join.LastBatch = null; join.Grant = new ReadyGrant(ReadyStatus.Ready, snapshotId, commit);
                return join.Grant;
            }
        }

        public CatchUpBatch ReadEventsSince(ulong appliedWatermark)
        {
            lock (sync) { return ReadTail(0, appliedWatermark); }
        }
        private bool TryJoin(ulong playerId, ulong connectionGeneration, ulong snapshotId, out Join join)
        {
            Peer peer;
            if (joins.TryGetValue(playerId, out join) && join.ConnectionGeneration == connectionGeneration && join.SnapshotId == snapshotId && peers.TryGetValue(playerId, out peer) && peer.State.Connected && (!peer.State.Ready || join.Grant != null) && peer.State.ConnectionGeneration == connectionGeneration) return true;
            join = null; return false;
        }
        private CatchUpBatch ReadTail(ulong snapshotId, ulong from)
        {
            if (from > watermark) return Batch(CatchUpStatus.InvalidTransfer, snapshotId, from, from, new CommitEvent[0]);
            if (!TailAvailable(from)) return Batch(CatchUpStatus.RestartRequired, snapshotId, from, from, new CommitEvent[0]);
            List<CommitEvent> tail = new List<CommitEvent>();
            foreach (CommitEvent item in events) if (item.Sequence > from) tail.Add(item);
            return Batch(CatchUpStatus.Success, snapshotId, from, watermark, tail.ToArray());
        }
        private bool TailAvailable(ulong from) { return from == watermark || (events.Count > 0 && from >= events.Peek().Sequence - 1); }
        private static CatchUpBatch Batch(CatchUpStatus status, ulong snapshotId, ulong from, ulong to, CommitEvent[] items) { return new CatchUpBatch(status, snapshotId, from, to, items); }
        private CommitEvent Commit(CommitKind kind, EntityState entity, PlayerState player, ulong requestPlayer, ulong requestSequence)
        {
            if (watermark == UInt64.MaxValue) throw new InvalidOperationException("Commit space exhausted; create a new session.");
            CommitEvent commit = new CommitEvent(generation, watermark + 1, kind, requestPlayer, requestSequence, entity, player);
            events.Enqueue(commit); watermark++;
            if (events.Count > eventCapacity) events.Dequeue();
            return commit;
        }
    }
}
