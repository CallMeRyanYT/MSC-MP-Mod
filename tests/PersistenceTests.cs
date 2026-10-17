using System;
using System.IO;
using System.Security.Cryptography;
using MSCMP.Core;
using MSCMP.Persistence;

internal static class PersistenceTests
{
    private delegate void TestAction();
    private static int assertions;
    private static readonly WorldVector Origin = WorldVector.Zero;
    private static readonly CheckpointLimits Limits = CheckpointLimits.Default;
    private static void Expect(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Reject(TestAction action, string message)
    {
        bool rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; } catch (IOException) { rejected = true; } catch (ArgumentException) { rejected = true; }
        Expect(rejected, message);
    }
    private static AuthorityWorld World()
    {
        AuthorityWorld world = new AuthorityWorld(7, new AuthorityLimits(3, 2, 12, 10000, 10, 4, 100), 8, 32);
        world.ConnectPlayer(1, 1, Origin, true); return world;
    }
    private static AuthorityCommand Pickup(AuthorityWorld world, EntityState entity)
    {
        return new AuthorityCommand(CommandKind.Pickup, world.Generation, 1, 1, 1, entity.Id, entity.Revision, entity.OwnershipEpoch, Origin, Origin);
    }
    private static byte[] Copy(byte[] bytes) { return (byte[])bytes.Clone(); }
    private static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    private static void Rehash(byte[] bytes)
    {
        using (SHA256 hash = new SHA256Managed())
        {
            byte[] digest = hash.ComputeHash(bytes, 0, bytes.Length - 32); Buffer.BlockCopy(digest, 0, bytes, bytes.Length - 32, 32);
        }
    }
    private static void Set32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8); bytes[offset + 2] = (byte)(value >> 16); bytes[offset + 3] = (byte)(value >> 24);
    }
    private static void Set64(byte[] bytes, int offset, ulong value)
    {
        for (int i = 0; i < 8; i++) bytes[offset + i] = (byte)(value >> (i * 8));
    }
    private static int PlayerStart(byte[] encoded)
    {
        using (BinaryReader reader = new BinaryReader(new MemoryStream(encoded, false)))
        {
            reader.BaseStream.Position = 36; uint count = reader.ReadUInt32(); reader.ReadUInt32();
            for (uint i = 0; i < count; i++)
            {
                reader.BaseStream.Position += 41;
                ushort key = reader.ReadUInt16(); reader.BaseStream.Position += key;
                ushort type = reader.ReadUInt16(); reader.BaseStream.Position += type + 24;
            }
            return (int)reader.BaseStream.Position;
        }
    }
    private static void CodecRoundTripAndBounds()
    {
        AuthorityWorld world = World();
        EntityState item = world.RegisterAuthoredItem("author-1", "test-çöp", new WorldVector(1, 2, 3));
        world.UpdateFreeMotion(7, item.Id, 1, 14, new WorldVector(2, 3, 4), new WorldVector(-1, 0, 1));
        EntityState dead = world.SpawnItem("test-box", Origin); world.RetireEntity(dead.Id, dead.Revision, false);
        WorldSnapshot snapshot = world.CaptureSnapshot(); byte[] encoded = SnapshotCodec.Encode(snapshot, Limits);
        WorldSnapshot decoded = SnapshotCodec.Decode(encoded, Limits);
        Expect(decoded.Generation == 7 && decoded.SnapshotId == snapshot.SnapshotId && decoded.Watermark == snapshot.Watermark, "Checkpoint envelope round-trips generation, snapshot ID, and committed watermark.");
        Expect(decoded.Entities.Length == 2 && decoded.Players.Length == 1, "Checkpoint records round-trip without filtering tombstones.");
        Expect(decoded.Entities[0].AuthoredKey == "author-1" && decoded.Entities[0].Archetype == "test-çöp" && decoded.Entities[0].Position.Same(new WorldVector(2, 3, 4)) && decoded.Entities[0].Velocity.Same(new WorldVector(-1, 0, 1)) && decoded.Entities[0].MotionSequence == 14, "Explicit UTF-8, identity, vector, and sequence fields round-trip.");
        Expect(decoded.Entities[1].Lifecycle == EntityLifecycle.Destroyed, "Lifecycle tombstones survive serialization.");
        Expect(Same(encoded, SnapshotCodec.Encode(decoded, Limits)), "Encoding a decoded checkpoint is canonical and repeatable.");
        WorldSnapshot shuffled = new WorldSnapshot(snapshot.Generation, snapshot.SnapshotId, snapshot.Watermark, new EntityState[] { snapshot.Entities[1], snapshot.Entities[0] }, snapshot.Players);
        Expect(Same(encoded, SnapshotCodec.Encode(shuffled, Limits)), "Discovery order does not affect canonical checkpoint bytes.");
        Expect(Same(encoded, SnapshotCodec.Encode(snapshot, new CheckpointLimits(encoded.Length, 2, 1, 1024))), "The exact configured byte/count boundary remains usable.");
        for (int length = 0; length < encoded.Length; length++)
        {
            byte[] truncated = new byte[length]; Buffer.BlockCopy(encoded, 0, truncated, 0, length);
            Reject(delegate() { SnapshotCodec.Decode(truncated, Limits); }, "Every truncation point is rejected.");
        }
        for (int offset = 0; offset < encoded.Length; offset++)
        {
            byte[] corrupt = Copy(encoded); corrupt[offset] ^= 1;
            Reject(delegate() { SnapshotCodec.Decode(corrupt, Limits); }, "Every single-byte corruption is rejected by schema or checksum.");
        }
        byte[] trailing = new byte[encoded.Length + 1]; Buffer.BlockCopy(encoded, 0, trailing, 0, encoded.Length);
        Reject(delegate() { SnapshotCodec.Decode(trailing, Limits); }, "Trailing checkpoint bytes are rejected.");
        Reject(delegate() { SnapshotCodec.Encode(snapshot, new CheckpointLimits(128, 100, 4, 1024)); }, "Encoding cannot exceed the configured byte budget.");
        Reject(delegate() { SnapshotCodec.Decode(encoded, new CheckpointLimits(128, 100, 4, 1024)); }, "Decoding rejects oversized buffers before parsing records.");
        Reject(delegate() { SnapshotCodec.Encode(snapshot, new CheckpointLimits(4096, 1, 4, 1024)); }, "Entity count is bounded on encode.");
        Reject(delegate() { SnapshotCodec.Decode(encoded, new CheckpointLimits(4096, 1, 4, 1024)); }, "Entity count is bounded on decode.");
        Reject(delegate() { SnapshotCodec.Encode(snapshot, new CheckpointLimits(4096, 100, 4, 4)); }, "String bounds count UTF-8 bytes rather than characters.");
        byte[] enormousCount = Copy(encoded); Set32(enormousCount, 36, UInt32.MaxValue); Rehash(enormousCount);
        Reject(delegate() { SnapshotCodec.Decode(enormousCount, Limits); }, "A valid checksum cannot bypass entity allocation limits.");
        byte[] impossibleCount = Copy(encoded); Set32(impossibleCount, 36, 100); Rehash(impossibleCount);
        Reject(delegate() { SnapshotCodec.Decode(impossibleCount, Limits); }, "Counts must fit remaining payload bytes even when below the configured maximum.");
        byte[] invalidPlayers = Copy(encoded); Set32(invalidPlayers, 40, 65); Rehash(invalidPlayers);
        Reject(delegate() { SnapshotCodec.Decode(invalidPlayers, Limits); }, "A valid checksum cannot bypass player allocation limits.");
        byte[] badVersion = Copy(encoded); badVersion[4] = 2; Rehash(badVersion);
        Reject(delegate() { SnapshotCodec.Decode(badVersion, Limits); }, "Unsupported checkpoint schema is rejected explicitly.");
        byte[] badFlags = Copy(encoded); badFlags[6] = 1; Rehash(badFlags);
        Reject(delegate() { SnapshotCodec.Decode(badFlags, Limits); }, "Unknown flags cannot silently change interpretation.");
        byte[] badLength = Copy(encoded); Set32(badLength, 8, UInt32.MaxValue); Rehash(badLength);
        Reject(delegate() { SnapshotCodec.Decode(badLength, Limits); }, "A valid checksum cannot conceal a mismatched declared payload length.");
        byte[] invalidEnum = Copy(encoded); invalidEnum[44 + 32] = 99; Rehash(invalidEnum);
        Reject(delegate() { SnapshotCodec.Decode(invalidEnum, Limits); }, "Unknown lifecycle enum is rejected even with a valid checksum.");
        byte[] invalidBoolean = Copy(encoded); invalidBoolean[PlayerStart(encoded) + 40] = 2; Rehash(invalidBoolean);
        Reject(delegate() { SnapshotCodec.Decode(invalidBoolean, Limits); }, "Boolean fields accept exactly zero or one.");
        byte[] invalidUtf8 = Copy(encoded); invalidUtf8[87] = 0xff; Rehash(invalidUtf8);
        Reject(delegate() { SnapshotCodec.Decode(invalidUtf8, Limits); }, "Malformed UTF-8 cannot be silently replaced during identity decoding.");
        byte[] invalidStringLength = Copy(encoded); invalidStringLength[85] = 0xff; invalidStringLength[86] = 0xff; Rehash(invalidStringLength);
        Reject(delegate() { SnapshotCodec.Decode(invalidStringLength, Limits); }, "String allocation respects its explicit byte bound.");
        byte[] zeroGeneration = Copy(encoded); Set64(zeroGeneration, 12, 0); Rehash(zeroGeneration);
        Reject(delegate() { SnapshotCodec.Decode(zeroGeneration, Limits); }, "Zero generation fails domain validation.");
        byte[] nonFinite = Copy(encoded); Set32(nonFinite, PlayerStart(encoded) + 42, 0x7fc00000); Rehash(nonFinite);
        Reject(delegate() { SnapshotCodec.Decode(nonFinite, Limits); }, "Non-finite physics values fail domain validation.");
        byte[] decodedOrphan = Copy(encoded); Set64(decodedOrphan, PlayerStart(encoded) + 32, 999); Rehash(decodedOrphan);
        Reject(delegate() { SnapshotCodec.Decode(decodedOrphan, Limits); }, "Decoded hand dependencies are validated even with a valid checksum.");
        byte[] decodedDuplicate = Copy(encoded);
        int secondEntityOffset;
        using (BinaryReader reader = new BinaryReader(new MemoryStream(decodedDuplicate, false)))
        {
            reader.BaseStream.Position = 85; ushort keyLength = reader.ReadUInt16(); reader.BaseStream.Position += keyLength;
            ushort typeLength = reader.ReadUInt16(); reader.BaseStream.Position += typeLength + 24; secondEntityOffset = (int)reader.BaseStream.Position;
        }
        Set64(decodedDuplicate, secondEntityOffset, item.Id); Rehash(decodedDuplicate);
        Reject(delegate() { SnapshotCodec.Decode(decodedDuplicate, Limits); }, "Decoded duplicate identity records fail graph validation.");
        WorldSnapshot duplicate = new WorldSnapshot(7, 1, 1, new EntityState[] { item, item }, snapshot.Players);
        Reject(delegate() { SnapshotCodec.Encode(duplicate, Limits); }, "Duplicate entity IDs cannot form a checkpoint.");
        WorldSnapshot duplicatePlayers = new WorldSnapshot(7, 1, 1, new EntityState[0], new PlayerState[] { snapshot.Players[0], snapshot.Players[0] });
        Reject(delegate() { SnapshotCodec.Encode(duplicatePlayers, Limits); }, "Duplicate player IDs cannot form a checkpoint.");
        PlayerState orphan = new PlayerState(1, 1, 2, 0, 999, true, true, Origin);
        Reject(delegate() { SnapshotCodec.Encode(new WorldSnapshot(7, 1, 1, new EntityState[0], new PlayerState[] { orphan }), Limits); }, "Orphaned hand relationships are rejected before persistence.");
        EntityState dangling = new EntityState(7, 1, "", "box", EntityLifecycle.Held, 2, 2, 0, 999, Origin, Origin);
        Reject(delegate() { SnapshotCodec.Encode(new WorldSnapshot(7, 1, 1, new EntityState[] { dangling }, snapshot.Players), Limits); }, "An unavailable holder cannot be persisted as coherent state.");
        EntityState badText = new EntityState(7, 1, "", "\ud800", EntityLifecycle.Free, 1, 1, 0, 0, Origin, Origin);
        Reject(delegate() { SnapshotCodec.Encode(new WorldSnapshot(7, 1, 1, new EntityState[] { badText }, snapshot.Players), Limits); }, "Unpaired Unicode surrogates are rejected on encode.");
        world.ConnectPlayer(2, 1, Origin, false); WorldSnapshot twoPlayers = world.CaptureSnapshot();
        Reject(delegate() { SnapshotCodec.Encode(twoPlayers, new CheckpointLimits(4096, 2, 1, 1024)); }, "Player count is bounded on encode.");
        byte[] twoEncoded = SnapshotCodec.Encode(twoPlayers, Limits);
        Reject(delegate() { SnapshotCodec.Decode(twoEncoded, new CheckpointLimits(4096, 2, 1, 1024)); }, "Player count is bounded on decode.");
        Set64(twoEncoded, PlayerStart(twoEncoded) + 54, 1); Rehash(twoEncoded);
        Reject(delegate() { SnapshotCodec.Decode(twoEncoded, Limits); }, "Decoded duplicate player identity records fail graph validation.");
    }

    private static void StoreAtomicityAndRecovery(string root)
    {
        string directory = Path.Combine(root, "atomic"); CheckpointStore store = new CheckpointStore(directory, Limits);
        string unrelated = Path.Combine(root, "unrelated-save-sentinel.txt"); File.WriteAllText(unrelated, "untouched");
        AuthorityWorld world = World(); EntityState item = world.RegisterAuthoredItem("persistent-item", "test-box", Origin);
        WorldSnapshot first = world.CaptureSnapshot(); store.Save(first);
        Expect(store.ReadCheckpoint().Source == CheckpointSource.Current && store.ReadCheckpoint().Snapshot.Watermark == first.Watermark, "Initial checkpoint publishes successfully.");
        Expect(!File.Exists(store.PreviousPath) && File.Exists(store.GenerationPath), "First publication reserves its generation without inventing a previous checkpoint.");
        world.Process(Pickup(world, item)); world.AcceptPlayerPose(7, 1, 1, 1, new WorldVector(2, 0, 0));
        WorldSnapshot held = world.CaptureSnapshot(); byte[] firstBytes = File.ReadAllBytes(store.CurrentPath); store.Save(held);
        Expect(Same(firstBytes, File.ReadAllBytes(store.PreviousPath)), "Atomic overwrite preserves exactly the previous coherent checkpoint.");
        Expect(store.ReadCheckpoint().Snapshot.Entities[0].Lifecycle == EntityLifecycle.Held, "Saved checkpoint captures a coherent held relationship.");
        string abandoned = Path.Combine(directory, ".publish-abandoned.tmp"); File.WriteAllBytes(abandoned, new byte[] { 1, 2, 3 });
        Expect(store.ReadCheckpoint().Snapshot.Watermark == held.Watermark && File.Exists(abandoned), "Interrupted temporary files are ignored and are not blindly deleted.");
        Reject(delegate() { store.Save(first); }, "Older same-generation watermark cannot overwrite a newer checkpoint.");
        WorldSnapshot restored = store.RecoverForHost();
        Expect(restored.Generation == 8 && restored.Watermark == 0 && restored.SnapshotId == 1, "Host recovery starts a fresh generation and commit stream.");
        Expect(restored.Entities[0].Lifecycle == EntityLifecycle.Free && restored.Entities[0].HolderId == 0 && restored.Players[0].HandEntityId == 0 && !restored.Players[0].Connected && !restored.Players[0].Ready, "Recovered connections and hand ownership are never live.");
        Expect(restored.Entities[0].Position.Same(new WorldVector(2, 0, 0)) && restored.Entities[0].Velocity.Same(Origin), "Held item recovers at the latest accepted holder position with a zero release velocity.");
        Expect(restored.Entities[0].Revision == held.Entities[0].Revision + 1 && restored.Entities[0].OwnershipEpoch == held.Entities[0].OwnershipEpoch + 1, "Recovery normalizes ownership with one semantic release.");
        Expect(store.ReadCheckpoint().Snapshot.Generation == restored.Generation, "Recovered generation is committed before it is returned to the caller.");
        WorldSnapshot again = store.RecoverForHost();
        Expect(again.Generation == 9 && again.Entities[0].Revision == restored.Entities[0].Revision && again.Entities[0].OwnershipEpoch == restored.Entities[0].OwnershipEpoch, "Repeated recovery allocates another generation without releasing the same item twice.");
        Reject(delegate() { store.Save(held); }, "Old live sessions cannot overwrite a newly reserved recovery generation.");
        Expect(File.ReadAllText(unrelated) == "untouched", "The store leaves unrelated files untouched.");

        // Simulate interruption after the durable generation reservation but before checkpoint publication.
        byte[] lease = File.ReadAllBytes(store.GenerationPath); Set64(lease, 8, 15); Rehash(lease); File.WriteAllBytes(store.GenerationPath, lease);
        WorldSnapshot afterInterruptedReservation = store.RecoverForHost();
        Expect(afterInterruptedReservation.Generation == 16, "Recovery skips generations reserved by interrupted earlier recovery attempts.");
        byte[] validBackup = File.ReadAllBytes(store.PreviousPath);
        File.WriteAllBytes(store.CurrentPath, new byte[] { 9, 8, 7 });
        CheckpointReadResult fallback = store.ReadCheckpoint();
        Expect(fallback.Source == CheckpointSource.Previous && fallback.Snapshot.Generation == 9 && fallback.RecoveryReason.Length != 0, "Corrupt current checkpoint selects the previous coherent generation with diagnostics.");
        Reject(delegate() { store.Save(fallback.Snapshot); }, "Ordinary saving cannot replace the good backup with corrupt current bytes.");
        WorldSnapshot fallbackRecovery = store.RecoverForHost();
        Expect(fallbackRecovery.Generation == 17 && Same(validBackup, File.ReadAllBytes(store.PreviousPath)), "Recovery from backup preserves that backup and allocates a generation newer than the corrupt current one.");
        Expect(store.ReadCheckpoint().Source == CheckpointSource.Current, "Backup recovery publishes a valid current checkpoint atomically.");
        byte[] corruptLease = File.ReadAllBytes(store.GenerationPath); corruptLease[8] ^= 1; File.WriteAllBytes(store.GenerationPath, corruptLease);
        byte[] beforeFailure = File.ReadAllBytes(store.CurrentPath);
        Reject(delegate() { store.RecoverForHost(); }, "Corrupt generation lease fails closed rather than reusing an old session generation.");
        Expect(Same(beforeFailure, File.ReadAllBytes(store.CurrentPath)), "Failed lease validation leaves current checkpoint intact.");
    }

    private static void FailureAndBounds(string root)
    {
        AuthorityWorld world = World(); EntityState item = world.SpawnItem("test-box", Origin); WorldSnapshot snapshot = world.CaptureSnapshot();
        string invalidDirectory = Path.Combine(root, "invalid-never-created"); CheckpointStore invalidStore = new CheckpointStore(invalidDirectory, Limits);
        WorldSnapshot broken = new WorldSnapshot(7, 1, 0, new EntityState[] { item, item }, snapshot.Players);
        Reject(delegate() { invalidStore.Save(broken); }, "Invalid graph is rejected before filesystem mutation.");
        Expect(!Directory.Exists(invalidDirectory), "An invalid checkpoint cannot even create its store directory.");
        CheckpointStore empty = new CheckpointStore(Path.Combine(root, "empty"), Limits);
        Reject(delegate() { empty.ReadCheckpoint(); }, "A missing checkpoint produces a clear failure.");
        Reject(delegate() { CheckpointRecovery.Rebase(snapshot, 7, Limits); }, "Pure recovery helper also rejects reused generation.");
        Reject(delegate() { CheckpointRecovery.Rebase(snapshot, 6, Limits); }, "Pure recovery helper rejects older generation.");
        WorldSnapshot rebased = CheckpointRecovery.Rebase(snapshot, 8, Limits);
        Expect(rebased.Entities[0].Revision == snapshot.Entities[0].Revision && !rebased.Players[0].Connected, "Free-item recovery preserves semantic revision while clearing connections.");
        string missingLeaseDirectory = Path.Combine(root, "missing-lease"); Directory.CreateDirectory(missingLeaseDirectory);
        File.WriteAllBytes(Path.Combine(missingLeaseDirectory, CheckpointStore.CurrentFileName), SnapshotCodec.Encode(snapshot, Limits));
        CheckpointStore missingLease = new CheckpointStore(missingLeaseDirectory, Limits);
        Reject(delegate() { missingLease.RecoverForHost(); }, "A checkpoint copied without its generation lease cannot silently restart a live generation.");
        string lockedDirectory = Path.Combine(root, "locked"); CheckpointStore locked = new CheckpointStore(lockedDirectory, Limits); locked.Save(snapshot);
        byte[] beforeLock = File.ReadAllBytes(locked.CurrentPath);
        using (FileStream heldLock = new FileStream(Path.Combine(lockedDirectory, "checkpoint.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { Reject(delegate() { new CheckpointStore(lockedDirectory, Limits).Save(snapshot); }, "A second writer cannot interleave checkpoint publication."); }
        Expect(Same(beforeLock, File.ReadAllBytes(locked.CurrentPath)), "Writer contention does not alter the existing checkpoint.");
        string oversizedDirectory = Path.Combine(root, "oversized"); Directory.CreateDirectory(oversizedDirectory);
        File.WriteAllBytes(Path.Combine(oversizedDirectory, CheckpointStore.CurrentFileName), new byte[129]);
        Reject(delegate() { new CheckpointStore(oversizedDirectory, new CheckpointLimits(128, 1, 1, 16)).ReadCheckpoint(); }, "Store file length is bounded before allocating a read buffer.");
        string interruptedDirectory = Path.Combine(root, "only-temp"); Directory.CreateDirectory(interruptedDirectory);
        File.WriteAllBytes(Path.Combine(interruptedDirectory, ".publish-partial.tmp"), SnapshotCodec.Encode(snapshot, Limits));
        Reject(delegate() { new CheckpointStore(interruptedDirectory, Limits).ReadCheckpoint(); }, "A complete-looking temporary file is not mistaken for a committed checkpoint.");
    }

    private static int Main()
    {
        try
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "persistence-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            CodecRoundTripAndBounds(); StoreAtomicityAndRecovery(root); FailureAndBounds(root);
            Console.WriteLine("PASS: " + assertions + " bounded checkpoint, atomic publication, corruption, and recovery assertions. Tests touched generated checkpoint fixtures only; vanilla save isolation remains unverified.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }
}
