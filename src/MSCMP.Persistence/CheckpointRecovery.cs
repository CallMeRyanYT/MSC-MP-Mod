using System;
using System.Collections.Generic;
using System.IO;
using MSCMP.Core;

namespace MSCMP.Persistence
{
    public static class CheckpointRecovery
    {
        // The store reserves newGeneration durably before calling this. Standalone callers
        // must make the same uniqueness guarantee; simply reusing an old save's generation is unsafe.
        public static WorldSnapshot Rebase(WorldSnapshot saved, ulong newGeneration, CheckpointLimits limits)
        {
            SnapshotCodec.Validate(saved, limits);
            if (newGeneration <= saved.Generation) throw new InvalidDataException("Host recovery requires a strictly newer, durably reserved session generation.");
            Dictionary<ulong, PlayerState> previousPlayers = new Dictionary<ulong, PlayerState>();
            PlayerState[] players = saved.Players; EntityState[] entities = saved.Entities;
            for (int i = 0; i < players.Length; i++) previousPlayers.Add(players[i].Id, players[i]);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    EntityState old = entities[i]; bool held = old.Lifecycle == EntityLifecycle.Held;
                    entities[i] = new EntityState(newGeneration, old.Id, old.AuthoredKey, old.Archetype, held ? EntityLifecycle.Free : old.Lifecycle,
                        held ? checked(old.Revision + 1) : old.Revision, held ? checked(old.OwnershipEpoch + 1) : old.OwnershipEpoch, 0, 0,
                        held ? previousPlayers[old.HolderId].Position : old.Position, held ? WorldVector.Zero : old.Velocity);
                }
                for (int i = 0; i < players.Length; i++)
                {
                    PlayerState old = players[i];
                    players[i] = new PlayerState(old.Id, old.ConnectionGeneration, old.Connected ? checked(old.Revision + 1) : old.Revision, 0, 0, false, false, old.Position);
                }
            }
            catch (OverflowException error) { throw new InvalidDataException("Checkpoint revision space is exhausted; recovery cannot safely release ownership.", error); }
            WorldSnapshot rebased = new WorldSnapshot(newGeneration, 1, 0, entities, players);
            SnapshotCodec.Validate(rebased, limits);
            return rebased;
        }
    }
}
