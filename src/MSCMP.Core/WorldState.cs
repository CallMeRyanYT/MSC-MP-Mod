using System;

namespace MSCMP.Core
{
    // Value-only domain types: no Unity objects or game assemblies cross this boundary.
    public struct WorldVector
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public WorldVector(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static WorldVector Zero { get { return new WorldVector(0, 0, 0); } }
        public bool IsFinite { get { return Finite(X) && Finite(Y) && Finite(Z); } }
        public double LengthSquared { get { return (double)X * X + (double)Y * Y + (double)Z * Z; } }
        public bool Within(float bound) { return IsFinite && Math.Abs(X) <= bound && Math.Abs(Y) <= bound && Math.Abs(Z) <= bound; }
        public static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        public static double DistanceSquared(WorldVector a, WorldVector b)
        {
            double x = (double)a.X - b.X, y = (double)a.Y - b.Y, z = (double)a.Z - b.Z;
            return x * x + y * y + z * z;
        }
        public bool Same(WorldVector other) { return X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z); }
    }

    public struct WorldRotation
    {
        public readonly float X, Y, Z, W;
        public WorldRotation(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static WorldRotation Identity { get { return new WorldRotation(0, 0, 0, 1); } }
        public bool IsValid
        {
            get
            {
                double length = (double)X * X + (double)Y * Y + (double)Z * Z + (double)W * W;
                return WorldVector.Finite(X) && WorldVector.Finite(Y) && WorldVector.Finite(Z) && WorldVector.Finite(W) && Math.Abs(length - 1) < 0.002;
            }
        }
        public bool Same(WorldRotation other) { return X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && W.Equals(other.W); }
    }

    public enum EntityLifecycle { Free = 1, Held = 2, Consumed = 3, Destroyed = 4 }
    public enum CommandKind { Pickup = 1, Drop = 2, Throw = 3 }
    public enum CommitKind { Spawn = 1, Pickup = 2, Drop = 3, Throw = 4, Consume = 5, Destroy = 6, Connect = 7, Disconnect = 8, PlayerPose = 9, EntityMotion = 10, Ready = 11 }
    public enum CommandOutcome { Accepted, StaleWorld, StaleConnection, InvalidSequence, ObsoleteSequence, RequestConflict, NotReady, UnknownEntity, StaleRevision, StaleOwnership, Unavailable, HandOccupied, NotHolder, TooFar, InvalidValue, Unsupported }

    public sealed class AuthorityLimits
    {
        public readonly float PickupDistance;
        public readonly float ReleaseDistance;
        public readonly float ThrowSpeed;
        public readonly float WorldCoordinate;
        public readonly float PlayerPoseStep;
        public readonly int Players;
        public readonly int Entities;

        // Bounds are supplied by an inspected game adapter. They are not claims about vanilla MSC.
        public AuthorityLimits(float pickupDistance, float releaseDistance, float throwSpeed, float worldCoordinate, float playerPoseStep, int players, int entities)
        {
            if (!Positive(pickupDistance) || !Positive(releaseDistance) || !Positive(throwSpeed) || !Positive(worldCoordinate) || !Positive(playerPoseStep) || players < 1 || players > 64 || entities < 1 || entities > 100000)
                throw new ArgumentOutOfRangeException("limits");
            PickupDistance = pickupDistance; ReleaseDistance = releaseDistance; ThrowSpeed = throwSpeed;
            WorldCoordinate = worldCoordinate; PlayerPoseStep = playerPoseStep; Players = players; Entities = entities;
        }
        private static bool Positive(float value) { return WorldVector.Finite(value) && value > 0; }
    }

    public sealed class EntityState
    {
        public readonly ulong Generation;
        public readonly ulong Id;
        public readonly string AuthoredKey;
        public readonly string Archetype;
        public readonly EntityLifecycle Lifecycle;
        public readonly ulong Revision;
        public readonly ulong OwnershipEpoch;
        public readonly ulong MotionSequence;
        public readonly ulong HolderId;
        public readonly WorldVector Position;
        public readonly WorldVector Velocity;
        public readonly WorldRotation Rotation;
        public readonly WorldVector AngularVelocity;

        public EntityState(ulong generation, ulong id, string authoredKey, string archetype, EntityLifecycle lifecycle, ulong revision, ulong ownershipEpoch, ulong motionSequence, ulong holderId, WorldVector position, WorldVector velocity)
            : this(generation, id, authoredKey, archetype, lifecycle, revision, ownershipEpoch, motionSequence, holderId, position, velocity, WorldRotation.Identity, WorldVector.Zero) { }

        public EntityState(ulong generation, ulong id, string authoredKey, string archetype, EntityLifecycle lifecycle, ulong revision, ulong ownershipEpoch, ulong motionSequence, ulong holderId, WorldVector position, WorldVector velocity, WorldRotation rotation, WorldVector angularVelocity)
        {
            if (generation == 0 || id == 0 || revision == 0 || ownershipEpoch == 0 || !Enum.IsDefined(typeof(EntityLifecycle), lifecycle)) throw new ArgumentException("Invalid entity identity or lifecycle.");
            ValidateLabel(archetype, false); ValidateLabel(authoredKey, true);
            if (!position.IsFinite || !velocity.IsFinite || (lifecycle == EntityLifecycle.Held) != (holderId != 0)) throw new ArgumentException("Invalid entity state.");
            Generation = generation; Id = id; AuthoredKey = authoredKey ?? String.Empty; Archetype = archetype;
            if (!rotation.IsValid || !angularVelocity.IsFinite) throw new ArgumentException("Invalid body orientation or angular velocity.");
            Rotation = rotation; AngularVelocity = angularVelocity;
            Lifecycle = lifecycle; Revision = revision; OwnershipEpoch = ownershipEpoch; MotionSequence = motionSequence; HolderId = holderId; Position = position; Velocity = velocity;
        }
        internal static void ValidateLabel(string text, bool emptyAllowed)
        {
            if ((!emptyAllowed && String.IsNullOrEmpty(text)) || (text != null && text.Length > 256)) throw new ArgumentException("Identity labels must contain 1-256 characters.");
            if (text != null) for (int i = 0; i < text.Length; i++) if (Char.IsControl(text[i])) throw new ArgumentException("Control characters are not identity labels.");
        }
        internal EntityState Change(EntityLifecycle lifecycle, ulong holder, WorldVector position, WorldVector velocity)
        {
            return new EntityState(Generation, Id, AuthoredKey, Archetype, lifecycle, checked(Revision + 1), checked(OwnershipEpoch + 1), 0, holder, position, velocity, Rotation, WorldVector.Zero);
        }
        internal EntityState Move(ulong sequence, WorldVector position, WorldVector velocity)
        {
            return Move(sequence, position, velocity, Rotation, AngularVelocity);
        }
        internal EntityState Move(ulong sequence, WorldVector position, WorldVector velocity, WorldRotation rotation, WorldVector angularVelocity)
        { return new EntityState(Generation, Id, AuthoredKey, Archetype, Lifecycle, Revision, OwnershipEpoch, sequence, HolderId, position, velocity, rotation, angularVelocity); }
    }

    public sealed class PlayerState
    {
        public readonly ulong Id;
        public readonly ulong ConnectionGeneration;
        public readonly ulong Revision;
        public readonly ulong PoseSequence;
        public readonly ulong HandEntityId;
        public readonly bool Connected;
        public readonly bool Ready;
        public readonly WorldVector Position;

        public PlayerState(ulong id, ulong connectionGeneration, ulong revision, ulong poseSequence, ulong handEntityId, bool connected, bool ready, WorldVector position)
        {
            if (id == 0 || connectionGeneration == 0 || revision == 0 || !position.IsFinite || (!connected && (ready || handEntityId != 0))) throw new ArgumentException("Invalid player state.");
            Id = id; ConnectionGeneration = connectionGeneration; Revision = revision; PoseSequence = poseSequence; HandEntityId = handEntityId; Connected = connected; Ready = ready; Position = position;
        }
        internal PlayerState Change(ulong hand, bool connected, bool ready)
        {
            return new PlayerState(Id, ConnectionGeneration, checked(Revision + 1), PoseSequence, hand, connected, ready, Position);
        }
        internal PlayerState Move(ulong sequence, WorldVector position)
        {
            return new PlayerState(Id, ConnectionGeneration, Revision, sequence, HandEntityId, Connected, Ready, position);
        }
    }

    public sealed class AuthorityCommand
    {
        public readonly CommandKind Kind;
        public readonly ulong Generation;
        public readonly ulong PlayerId;
        public readonly ulong ConnectionGeneration;
        public readonly ulong Sequence;
        public readonly ulong EntityId;
        public readonly ulong ExpectedRevision;
        public readonly ulong ExpectedOwnershipEpoch;
        public readonly WorldVector ReleasePosition;
        public readonly WorldVector ReleaseVelocity;

        // PlayerId and ConnectionGeneration MUST come from the authenticated/bound connection, not packet claims.
        public AuthorityCommand(CommandKind kind, ulong generation, ulong playerId, ulong connectionGeneration, ulong sequence, ulong entityId, ulong expectedRevision, ulong expectedOwnershipEpoch, WorldVector releasePosition, WorldVector releaseVelocity)
        {
            Kind = kind; Generation = generation; PlayerId = playerId; ConnectionGeneration = connectionGeneration; Sequence = sequence;
            EntityId = entityId; ExpectedRevision = expectedRevision; ExpectedOwnershipEpoch = expectedOwnershipEpoch; ReleasePosition = releasePosition; ReleaseVelocity = releaseVelocity;
        }
        internal bool Same(AuthorityCommand other)
        {
            return Kind == other.Kind && Generation == other.Generation && PlayerId == other.PlayerId && ConnectionGeneration == other.ConnectionGeneration && Sequence == other.Sequence && EntityId == other.EntityId && ExpectedRevision == other.ExpectedRevision && ExpectedOwnershipEpoch == other.ExpectedOwnershipEpoch && ReleasePosition.Same(other.ReleasePosition) && ReleaseVelocity.Same(other.ReleaseVelocity);
        }
    }

    public sealed class CommitEvent
    {
        public readonly ulong Generation;
        public readonly ulong Sequence;
        public readonly CommitKind Kind;
        public readonly ulong RequestPlayerId;
        public readonly ulong RequestSequence;
        public readonly EntityState Entity;
        public readonly PlayerState Player;

        public CommitEvent(ulong generation, ulong sequence, CommitKind kind, ulong requestPlayerId, ulong requestSequence, EntityState entity, PlayerState player)
        {
            if (generation == 0 || sequence == 0 || !Enum.IsDefined(typeof(CommitKind), kind) || (entity == null && player == null) || (entity != null && entity.Generation != generation)) throw new ArgumentException("Invalid committed event.");
            Generation = generation; Sequence = sequence; Kind = kind; RequestPlayerId = requestPlayerId; RequestSequence = requestSequence; Entity = entity; Player = player;
        }
    }

    public sealed class CommandResult
    {
        public readonly CommandOutcome Outcome;
        public readonly ulong RequestSequence;
        public readonly ulong Watermark;
        public readonly EntityState Entity;
        public readonly PlayerState Player;
        public readonly CommitEvent Commit;
        public bool Accepted { get { return Outcome == CommandOutcome.Accepted; } }
        internal CommandResult(CommandOutcome outcome, ulong requestSequence, ulong watermark, EntityState entity, PlayerState player, CommitEvent commit)
        { Outcome = outcome; RequestSequence = requestSequence; Watermark = watermark; Entity = entity; Player = player; Commit = commit; }
    }

    public sealed class WorldSnapshot
    {
        public readonly ulong Generation;
        public readonly ulong SnapshotId;
        public readonly ulong Watermark;
        private readonly EntityState[] entities;
        private readonly PlayerState[] players;
        public EntityState[] Entities { get { return (EntityState[])entities.Clone(); } }
        public PlayerState[] Players { get { return (PlayerState[])players.Clone(); } }
        public WorldSnapshot(ulong generation, ulong snapshotId, ulong watermark, EntityState[] entities, PlayerState[] players)
        {
            if (generation == 0 || snapshotId == 0 || entities == null || players == null || entities.Length > 100000 || players.Length > 64) throw new ArgumentException("Invalid snapshot envelope.");
            Generation = generation; SnapshotId = snapshotId; Watermark = watermark;
            this.entities = (EntityState[])entities.Clone(); this.players = (PlayerState[])players.Clone();
        }
    }

    public enum CatchUpStatus { Success, InvalidTransfer, RestartRequired }
    public sealed class CatchUpBatch
    {
        public readonly CatchUpStatus Status;
        public readonly ulong SnapshotId;
        public readonly ulong FromWatermark;
        public readonly ulong ToWatermark;
        private readonly CommitEvent[] events;
        public CommitEvent[] Events { get { return (CommitEvent[])events.Clone(); } }
        internal CatchUpBatch(CatchUpStatus status, ulong snapshotId, ulong from, ulong to, CommitEvent[] events)
        { Status = status; SnapshotId = snapshotId; FromWatermark = from; ToWatermark = to; this.events = (CommitEvent[])events.Clone(); }
    }

    public enum ReadyStatus { Ready, MoreChanges, InvalidTransfer, RestartRequired }
    public sealed class ReadyGrant
    {
        public readonly ReadyStatus Status;
        public readonly ulong SnapshotId;
        public readonly CommitEvent Commit;
        internal ReadyGrant(ReadyStatus status, ulong snapshotId, CommitEvent commit) { Status = status; SnapshotId = snapshotId; Commit = commit; }
    }
}
