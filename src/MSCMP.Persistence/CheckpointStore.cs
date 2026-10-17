using System;
using System.IO;
using MSCMP.Core;

namespace MSCMP.Persistence
{
    public enum CheckpointSource { Current, Previous }
    public sealed class CheckpointReadResult
    {
        public readonly WorldSnapshot Snapshot;
        public readonly CheckpointSource Source;
        public readonly string RecoveryReason;
        internal CheckpointReadResult(WorldSnapshot snapshot, CheckpointSource source, string reason) { Snapshot = snapshot; Source = source; RecoveryReason = reason; }
    }

    // Stores only MSCMP's own domain checkpoint in an explicitly supplied dedicated directory.
    // Nothing here discovers, redirects, reads, or writes any vanilla MSC save file.
    public sealed class CheckpointStore
    {
        public const string CurrentFileName = "world.mscp";
        public const string PreviousFileName = "world.previous.mscp";
        public const string GenerationFileName = "session-generation.mscg";
        private const uint GenerationMagic = 0x4743534d; // MSCG
        private readonly object sync = new object();
        private readonly string directory;
        private readonly CheckpointLimits limits;
        public string DirectoryPath { get { return directory; } }
        public string CurrentPath { get { return Path.Combine(directory, CurrentFileName); } }
        public string PreviousPath { get { return Path.Combine(directory, PreviousFileName); } }
        public string GenerationPath { get { return Path.Combine(directory, GenerationFileName); } }

        public CheckpointStore(string directory, CheckpointLimits limits)
        {
            if (String.IsNullOrEmpty(directory) || limits == null) throw new ArgumentException("A dedicated checkpoint directory and limits are required.");
            this.directory = Path.GetFullPath(directory); this.limits = limits;
        }

        public void Save(WorldSnapshot snapshot)
        {
            // Full validation precedes any filesystem mutation.
            byte[] encoded = SnapshotCodec.Encode(snapshot, limits);
            lock (sync)
            {
                Directory.CreateDirectory(directory);
                using (FileStream writerLock = OpenWriterLock())
                {
                    WorldSnapshot previous = null;
                    if (File.Exists(CurrentPath)) previous = ReadSnapshot(CurrentPath);
                    else if (File.Exists(PreviousPath)) throw new FormatException("Current checkpoint is missing; use host recovery to preserve the previous coherent checkpoint.");
                    if (previous != null && (snapshot.Generation < previous.Generation || (snapshot.Generation == previous.Generation && snapshot.Watermark < previous.Watermark))) throw new FormatException("A checkpoint cannot overwrite a newer committed generation.");
                    ulong reserved = ReadGeneration(previous != null);
                    if (snapshot.Generation < reserved) throw new FormatException("Checkpoint uses an obsolete reserved session generation.");
                    if (snapshot.Generation > reserved) WriteGeneration(snapshot.Generation);
                    Publish(CurrentPath, File.Exists(CurrentPath) ? PreviousPath : null, encoded);
                }
            }
        }

        // Diagnostic reading preserves the checkpoint exactly as recorded. It is not a live
        // session restore. Runtime startup must use RecoverForHost to reserve a new generation.
        public CheckpointReadResult ReadCheckpoint()
        {
            lock (sync) { return ReadBest(); }
        }

        public WorldSnapshot RecoverForHost()
        {
            lock (sync)
            {
                if (!Directory.Exists(directory)) throw new FileNotFoundException("No checkpoint directory exists.");
                using (FileStream writerLock = OpenWriterLock())
                {
                    CheckpointReadResult saved = ReadBest();
                    // Never fall back to an older generation lease: that can reuse a generation
                    // consumed by a previous recovery whose checkpoint publication was interrupted.
                    ulong reserved = ReadGeneration(true);
                    if (reserved < saved.Snapshot.Generation) throw new FormatException("Generation lease is older than the checkpoint; recovery is unsafe.");
                    if (reserved == UInt64.MaxValue) throw new FormatException("Session generation space exhausted.");
                    ulong next = reserved + 1;
                    WorldSnapshot rebased = CheckpointRecovery.Rebase(saved.Snapshot, next, limits);
                    byte[] encoded = SnapshotCodec.Encode(rebased, limits);
                    // Reserve first, publish second, return last. A crash can burn a generation
                    // but cannot hand the same generation to a later successful recovery.
                    WriteGeneration(next);
                    if (saved.Source == CheckpointSource.Current) Publish(CurrentPath, PreviousPath, encoded);
                    else Publish(CurrentPath, null, encoded); // Preserve the valid previous file; never back up corrupt current bytes over it.
                    return rebased;
                }
            }
        }

        private FileStream OpenWriterLock()
        {
            // Cooperating stores/processes serialize publication. A crash releases this OS lock;
            // the empty lock file may remain and is intentionally not deleted/recreated.
            return new FileStream(Path.Combine(directory, "checkpoint.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        private CheckpointReadResult ReadBest()
        {
            string reason;
            WorldSnapshot current;
            if (TryRead(CurrentPath, out current, out reason)) return new CheckpointReadResult(current, CheckpointSource.Current, String.Empty);
            string previousReason;
            WorldSnapshot previous;
            if (TryRead(PreviousPath, out previous, out previousReason)) return new CheckpointReadResult(previous, CheckpointSource.Previous, reason);
            throw new FormatException("No coherent checkpoint is available. Current: " + reason + " Previous: " + previousReason);
        }
        private bool TryRead(string path, out WorldSnapshot snapshot, out string reason)
        {
            snapshot = null;
            if (!File.Exists(path)) { reason = "missing"; return false; }
            try { snapshot = ReadSnapshot(path); reason = String.Empty; return true; }
            catch (FormatException error) { reason = error.Message; return false; }
            catch (IOException error) { reason = error.Message; return false; }
            catch (UnauthorizedAccessException error) { reason = error.Message; return false; }
        }
        private WorldSnapshot ReadSnapshot(string path) { return SnapshotCodec.Decode(ReadBounded(path, limits.MaxBytes), limits); }
        private static byte[] ReadBounded(string path, int maximum)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (stream.Length < 0 || stream.Length > maximum) throw new FormatException("Checkpoint file exceeds its byte limit.");
                byte[] bytes = new byte[(int)stream.Length]; int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw new FormatException("Checkpoint file was truncated while reading.");
                    offset += count;
                }
                if (stream.ReadByte() != -1) throw new FormatException("Checkpoint file changed while reading.");
                return bytes;
            }
        }
        private ulong ReadGeneration(bool required)
        {
            if (!File.Exists(GenerationPath))
            {
                if (required) throw new FormatException("The durable session-generation lease is missing; refusing to reuse a possible live generation.");
                return 0;
            }
            byte[] bytes = ReadBounded(GenerationPath, 48);
            if (bytes.Length != 48 || !SnapshotCodec.HashMatches(bytes, 16)) throw new FormatException("Invalid session-generation lease integrity.");
            using (BinaryReader reader = new BinaryReader(new MemoryStream(bytes, false)))
            {
                if (reader.ReadUInt32() != GenerationMagic || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 0) throw new FormatException("Unsupported session-generation lease format.");
                ulong generation = reader.ReadUInt64();
                if (generation == 0) throw new FormatException("Invalid zero session generation.");
                return generation;
            }
        }
        private void WriteGeneration(ulong generation)
        {
            byte[] body;
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(GenerationMagic); writer.Write((ushort)1); writer.Write((ushort)0); writer.Write(generation); writer.Flush(); body = stream.ToArray();
            }
            byte[] bytes = new byte[48], digest = SnapshotCodec.Hash(body, body.Length);
            Buffer.BlockCopy(body, 0, bytes, 0, body.Length); Buffer.BlockCopy(digest, 0, bytes, body.Length, digest.Length);
            Publish(GenerationPath, null, bytes);
        }
        private void Publish(string destination, string backup, byte[] bytes)
        {
            string temporary = Path.Combine(directory, ".publish-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // Unity's shipped Mono profile lacks the FileOptions constructor.
                // Flush buffers before atomic replacement; this is not a power-loss durability guarantee.
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(); }
                // Same-directory atomic publication; deliberately no delete-then-move fallback.
                // File.Replace support is required of the selected local filesystem.
                if (File.Exists(destination)) File.Replace(temporary, destination, backup);
                else File.Move(temporary, destination);
            }
            finally
            {
                // Only this invocation's exact temporary filename can be cleaned up.
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
