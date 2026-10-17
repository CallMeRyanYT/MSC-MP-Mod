using System;
using System.Collections.Generic;

namespace MSCMP.Core
{
    public enum ApplyOutcome { Applied, DuplicateOrStale, WrongGeneration, Gap, InvalidState }

    // State application is atomic and presentation-free. The game adapter changes its objects
    // only after an Applied result. A timeout never implies that a pickup was rejected.
    public sealed class ReplicaWorld
    {
        private readonly ulong generation;
        private Dictionary<ulong, EntityState> entities = new Dictionary<ulong, EntityState>();
        private Dictionary<ulong, PlayerState> players = new Dictionary<ulong, PlayerState>();
        private ulong watermark;
        private ulong snapshotId;
        private bool loaded;
        private bool ready;
        private bool requiresSnapshot;
        private ulong localPlayer;
        public ReplicaWorld(ulong generation) { if (generation == 0) throw new ArgumentOutOfRangeException("generation"); this.generation = generation; }
        public ulong Watermark { get { return watermark; } }
        public bool Ready { get { return ready; } }
        public EntityState GetEntity(ulong id) { EntityState state; return entities.TryGetValue(id, out state) ? state : null; }
        public PlayerState GetPlayer(ulong id) { PlayerState state; return players.TryGetValue(id, out state) ? state : null; }

        public ApplyOutcome LoadSnapshot(WorldSnapshot snapshot)
        {
            if (snapshot == null) return ApplyOutcome.InvalidState;
            if (snapshot.Generation != generation) return ApplyOutcome.WrongGeneration;
            if (loaded && snapshot.Watermark < watermark) return ApplyOutcome.DuplicateOrStale;
            Dictionary<ulong, EntityState> newEntities = new Dictionary<ulong, EntityState>();
            Dictionary<ulong, PlayerState> newPlayers = new Dictionary<ulong, PlayerState>();
            Dictionary<string, bool> keys = new Dictionary<string, bool>(StringComparer.Ordinal);
            EntityState[] entityArray = snapshot.Entities; PlayerState[] playerArray = snapshot.Players;
            for (int i = 0; i < entityArray.Length; i++)
            {
                EntityState entity = entityArray[i];
                if (entity == null || entity.Generation != generation || newEntities.ContainsKey(entity.Id)) return ApplyOutcome.InvalidState;
                if (entity.AuthoredKey.Length != 0)
                {
                    if (keys.ContainsKey(entity.AuthoredKey)) return ApplyOutcome.InvalidState;
                    keys.Add(entity.AuthoredKey, true);
                }
                EntityState old;
                if (entities.TryGetValue(entity.Id, out old) && !EntityNotOlder(old, entity)) return ApplyOutcome.InvalidState;
                newEntities.Add(entity.Id, entity);
            }
            for (int i = 0; i < playerArray.Length; i++)
            {
                PlayerState player = playerArray[i];
                if (player == null || newPlayers.ContainsKey(player.Id)) return ApplyOutcome.InvalidState;
                PlayerState old;
                if (players.TryGetValue(player.Id, out old) && !PlayerNotOlder(old, player)) return ApplyOutcome.InvalidState;
                newPlayers.Add(player.Id, player);
            }
            // Authority retains identity tombstones for the session. Omitting one could allow resurrection.
            foreach (ulong id in entities.Keys) if (!newEntities.ContainsKey(id)) return ApplyOutcome.InvalidState;
            foreach (ulong id in players.Keys) if (!newPlayers.ContainsKey(id)) return ApplyOutcome.InvalidState;
            foreach (EntityState entity in newEntities.Values) if (!EntityLink(entity, newPlayers)) return ApplyOutcome.InvalidState;
            foreach (PlayerState player in newPlayers.Values) if (!PlayerLink(player, newEntities)) return ApplyOutcome.InvalidState;
            entities = newEntities; players = newPlayers; watermark = snapshot.Watermark; snapshotId = snapshot.SnapshotId; loaded = true; ready = false; requiresSnapshot = false;
            return ApplyOutcome.Applied;
        }

        public ApplyOutcome Apply(CommitEvent commit)
        {
            if (commit == null || !loaded) return ApplyOutcome.InvalidState;
            if (commit.Generation != generation) return ApplyOutcome.WrongGeneration;
            if (commit.Sequence <= watermark) return ApplyOutcome.DuplicateOrStale;
            if (watermark == UInt64.MaxValue || commit.Sequence != watermark + 1) { ready = false; requiresSnapshot = true; return ApplyOutcome.Gap; }
            EntityState oldEntity = commit.Entity == null ? null : GetEntity(commit.Entity.Id);
            PlayerState oldPlayer = commit.Player == null ? null : GetPlayer(commit.Player.Id);
            if (!ValidShape(commit) || !ValidEntityTransition(commit, oldEntity) || !ValidPlayerTransition(commit, oldPlayer)) return ApplyOutcome.InvalidState;
            if (commit.Entity != null && !EntityLink(commit.Entity, commit.Player)) return ApplyOutcome.InvalidState;
            if (commit.Player != null && !PlayerLink(commit.Player, commit.Entity)) return ApplyOutcome.InvalidState;
            // Check both sides of any relationship being removed, not only its replacement.
            if (oldEntity != null && oldEntity.HolderId != 0)
            {
                PlayerState holder = commit.Player != null && commit.Player.Id == oldEntity.HolderId ? commit.Player : GetPlayer(oldEntity.HolderId);
                if (holder == null || !PlayerLink(holder, commit.Entity)) return ApplyOutcome.InvalidState;
            }
            if (oldPlayer != null && oldPlayer.HandEntityId != 0)
            {
                EntityState held = commit.Entity != null && commit.Entity.Id == oldPlayer.HandEntityId ? commit.Entity : GetEntity(oldPlayer.HandEntityId);
                if (held == null || !EntityLink(held, commit.Player)) return ApplyOutcome.InvalidState;
            }
            if (commit.Entity != null) entities[commit.Entity.Id] = commit.Entity;
            if (commit.Player != null) players[commit.Player.Id] = commit.Player;
            if (commit.Player != null && commit.Player.Id == localPlayer && !commit.Player.Connected) ready = false;
            watermark = commit.Sequence;
            return ApplyOutcome.Applied;
        }

        public ApplyOutcome ApplyCatchUp(CatchUpBatch batch)
        {
            if (batch == null || batch.Status != CatchUpStatus.Success || batch.SnapshotId != snapshotId || batch.FromWatermark != watermark || batch.ToWatermark < batch.FromWatermark) { ready = false; requiresSnapshot = true; return ApplyOutcome.InvalidState; }
            CommitEvent[] items = batch.Events;
            // Validate the full envelope before applying any prefix.
            if (batch.ToWatermark - batch.FromWatermark != (ulong)items.Length) { ready = false; requiresSnapshot = true; return ApplyOutcome.InvalidState; }
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i] == null || items[i].Generation != generation || items[i].Sequence != batch.FromWatermark + (ulong)i + 1) { ready = false; requiresSnapshot = true; return ApplyOutcome.InvalidState; }
            }
            for (int i = 0; i < items.Length; i++)
            {
                ApplyOutcome outcome = Apply(items[i]);
                if (outcome != ApplyOutcome.Applied) { ready = false; requiresSnapshot = true; return outcome; }
            }
            return ApplyOutcome.Applied;
        }

        public bool ApplyReady(ReadyGrant grant, ulong localPlayerId, ulong connectionGeneration)
        {
            if (requiresSnapshot || grant == null || grant.Status != ReadyStatus.Ready || grant.SnapshotId != snapshotId || grant.Commit == null || grant.Commit.Kind != CommitKind.Ready || grant.Commit.Player == null || grant.Commit.Player.Id != localPlayerId || grant.Commit.Player.ConnectionGeneration != connectionGeneration) return false;
            ApplyOutcome result = Apply(grant.Commit);
            PlayerState player = GetPlayer(localPlayerId);
            if (result == ApplyOutcome.Applied || (result == ApplyOutcome.DuplicateOrStale && player != null && player.ConnectionGeneration == connectionGeneration && player.Connected && player.Ready && watermark >= grant.Commit.Sequence))
            { ready = true; localPlayer = localPlayerId; return true; }
            return false;
        }

        private bool EntityLink(EntityState entity, PlayerState changedPlayer)
        {
            if (entity.Lifecycle != EntityLifecycle.Held) return entity.HolderId == 0;
            PlayerState holder = changedPlayer != null && changedPlayer.Id == entity.HolderId ? changedPlayer : GetPlayer(entity.HolderId);
            return holder != null && holder.Connected && holder.Ready && holder.HandEntityId == entity.Id;
        }
        private bool PlayerLink(PlayerState player, EntityState changedEntity)
        {
            if (player.HandEntityId == 0) return true;
            EntityState entity = changedEntity != null && changedEntity.Id == player.HandEntityId ? changedEntity : GetEntity(player.HandEntityId);
            return player.Connected && player.Ready && entity != null && entity.Lifecycle == EntityLifecycle.Held && entity.HolderId == player.Id;
        }
        private static bool EntityLink(EntityState entity, Dictionary<ulong, PlayerState> target)
        {
            if (entity.Lifecycle != EntityLifecycle.Held) return entity.HolderId == 0;
            PlayerState player; return target.TryGetValue(entity.HolderId, out player) && player.Connected && player.Ready && player.HandEntityId == entity.Id;
        }
        private static bool PlayerLink(PlayerState player, Dictionary<ulong, EntityState> target)
        {
            if (player.HandEntityId == 0) return true;
            EntityState entity; return player.Connected && player.Ready && target.TryGetValue(player.HandEntityId, out entity) && entity.Lifecycle == EntityLifecycle.Held && entity.HolderId == player.Id;
        }

        private static bool ValidShape(CommitEvent commit)
        {
            switch (commit.Kind)
            {
                case CommitKind.Spawn: case CommitKind.EntityMotion: return commit.Entity != null && commit.Player == null;
                case CommitKind.Pickup: case CommitKind.Drop: case CommitKind.Throw: return commit.Entity != null && commit.Player != null;
                case CommitKind.Consume: case CommitKind.Destroy: return commit.Entity != null;
                case CommitKind.Connect: case CommitKind.Ready: case CommitKind.PlayerPose: return commit.Entity == null && commit.Player != null;
                case CommitKind.Disconnect: return commit.Player != null;
                default: return false;
            }
        }
        private static bool ValidEntityTransition(CommitEvent commit, EntityState old)
        {
            EntityState current = commit.Entity;
            if (current == null) return true;
            if (commit.Kind == CommitKind.Spawn) return old == null && current.Lifecycle == EntityLifecycle.Free && current.Revision == 1 && current.OwnershipEpoch == 1 && current.MotionSequence == 0;
            if (old == null || !EntityNotOlder(old, current) || old.Lifecycle == EntityLifecycle.Consumed || old.Lifecycle == EntityLifecycle.Destroyed) return false;
            if (commit.Kind == CommitKind.EntityMotion) return old.Lifecycle == EntityLifecycle.Free && SameSemantic(old, current) && current.MotionSequence > old.MotionSequence;
            if (old.Revision == UInt64.MaxValue || old.OwnershipEpoch == UInt64.MaxValue || current.Revision != old.Revision + 1 || current.OwnershipEpoch != old.OwnershipEpoch + 1 || current.MotionSequence != 0) return false;
            switch (commit.Kind)
            {
                case CommitKind.Pickup: return old.Lifecycle == EntityLifecycle.Free && current.Lifecycle == EntityLifecycle.Held && commit.Player.Id == current.HolderId;
                case CommitKind.Drop: case CommitKind.Throw: case CommitKind.Disconnect: return old.Lifecycle == EntityLifecycle.Held && current.Lifecycle == EntityLifecycle.Free && commit.Player.Id == old.HolderId;
                case CommitKind.Consume: return current.Lifecycle == EntityLifecycle.Consumed && (old.HolderId == 0 ? commit.Player == null : commit.Player != null && commit.Player.Id == old.HolderId);
                case CommitKind.Destroy: return current.Lifecycle == EntityLifecycle.Destroyed && (old.HolderId == 0 ? commit.Player == null : commit.Player != null && commit.Player.Id == old.HolderId);
                default: return false;
            }
        }
        private static bool ValidPlayerTransition(CommitEvent commit, PlayerState old)
        {
            PlayerState current = commit.Player;
            if (current == null) return true;
            if (commit.Kind == CommitKind.Connect)
                return current.Connected && current.HandEntityId == 0 && current.PoseSequence == 0 && (old == null ? current.Revision == 1 : !old.Connected && old.Revision != UInt64.MaxValue && current.Revision == old.Revision + 1 && current.ConnectionGeneration > old.ConnectionGeneration);
            if (old == null || !old.Connected || current.ConnectionGeneration != old.ConnectionGeneration || !PlayerNotOlder(old, current)) return false;
            if (commit.Kind == CommitKind.PlayerPose) return old.Ready && current.Ready == old.Ready && current.Connected && current.Revision == old.Revision && current.HandEntityId == old.HandEntityId && current.PoseSequence > old.PoseSequence;
            if (old.Revision == UInt64.MaxValue || current.Revision != old.Revision + 1 || current.PoseSequence != old.PoseSequence || !current.Position.Same(old.Position)) return false;
            if (commit.Kind == CommitKind.Disconnect) return !current.Connected && !current.Ready && current.HandEntityId == 0 && (old.HandEntityId == 0 ? commit.Entity == null : commit.Entity != null && commit.Entity.Id == old.HandEntityId);
            if (!current.Connected) return false;
            if (commit.Kind == CommitKind.Ready) return !old.Ready && current.Ready && current.HandEntityId == 0 && old.HandEntityId == 0;
            if (!old.Ready || !current.Ready) return false;
            if (commit.Kind == CommitKind.Pickup) return old.HandEntityId == 0 && current.HandEntityId == commit.Entity.Id;
            return (commit.Kind == CommitKind.Drop || commit.Kind == CommitKind.Throw || commit.Kind == CommitKind.Consume || commit.Kind == CommitKind.Destroy) && old.HandEntityId == commit.Entity.Id && current.HandEntityId == 0;
        }
        private static bool EntityNotOlder(EntityState old, EntityState current)
        {
            if (old.Id != current.Id || old.Generation != current.Generation || old.AuthoredKey != current.AuthoredKey || old.Archetype != current.Archetype || current.Revision < old.Revision || current.OwnershipEpoch < old.OwnershipEpoch) return false;
            if ((old.Lifecycle == EntityLifecycle.Consumed || old.Lifecycle == EntityLifecycle.Destroyed) && (!SameSemantic(old, current) || current.MotionSequence != old.MotionSequence || !current.Position.Same(old.Position) || !current.Velocity.Same(old.Velocity) || !current.Rotation.Same(old.Rotation) || !current.AngularVelocity.Same(old.AngularVelocity))) return false;
            if (current.Revision == old.Revision && (!SameSemantic(old, current) || current.MotionSequence < old.MotionSequence)) return false;
            if (current.Revision == old.Revision && current.MotionSequence == old.MotionSequence && (!current.Position.Same(old.Position) || !current.Velocity.Same(old.Velocity) || !current.Rotation.Same(old.Rotation) || !current.AngularVelocity.Same(old.AngularVelocity))) return false;
            if (current.Revision > old.Revision && current.OwnershipEpoch <= old.OwnershipEpoch) return false;
            return true;
        }
        private static bool SameSemantic(EntityState a, EntityState b) { return a.Revision == b.Revision && a.OwnershipEpoch == b.OwnershipEpoch && a.Lifecycle == b.Lifecycle && a.HolderId == b.HolderId; }
        private static bool PlayerNotOlder(PlayerState old, PlayerState current)
        {
            if (current.Revision < old.Revision || current.ConnectionGeneration < old.ConnectionGeneration) return false;
            if (current.ConnectionGeneration == old.ConnectionGeneration && ((!old.Connected && current.Connected) || current.PoseSequence < old.PoseSequence)) return false;
            if (current.ConnectionGeneration == old.ConnectionGeneration && current.PoseSequence == old.PoseSequence && !current.Position.Same(old.Position)) return false;
            if (current.Revision == old.Revision && (current.HandEntityId != old.HandEntityId || current.Connected != old.Connected || current.Ready != old.Ready || current.ConnectionGeneration != old.ConnectionGeneration)) return false;
            return true;
        }
    }
}
