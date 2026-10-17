using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MSCMP.Core;

namespace MSCMP.Persistence
{
    public sealed class CheckpointLimits
    {
        public readonly int MaxBytes;
        public readonly int MaxEntities;
        public readonly int MaxPlayers;
        public readonly int MaxStringBytes;
        public static CheckpointLimits Default { get { return new CheckpointLimits(16 * 1024 * 1024, 100000, 64, 1024); } }
        public CheckpointLimits(int maxBytes, int maxEntities, int maxPlayers, int maxStringBytes)
        {
            if (maxBytes < 128 || maxBytes > 64 * 1024 * 1024 || maxEntities < 1 || maxEntities > 100000 || maxPlayers < 1 || maxPlayers > 64 || maxStringBytes < 1 || maxStringBytes > 1024) throw new ArgumentOutOfRangeException("limits");
            MaxBytes = maxBytes; MaxEntities = maxEntities; MaxPlayers = maxPlayers; MaxStringBytes = maxStringBytes;
        }
    }

    // A reusable wire/file codec for one coherent domain snapshot. It does not perform
    // network chunking, authenticate senders, or capture vanilla game saves.
    public static class SnapshotCodec
    {
        public const ushort Version = 1;
        public const int HeaderBytes = 12;
        public const int HashBytes = 32;
        private const uint Magic = 0x5043534d; // ASCII MSCP in little-endian order.
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        private sealed class BoundedMemoryStream : MemoryStream
        {
            private readonly int maximum;
            internal BoundedMemoryStream(int maximum) : base(Math.Min(maximum, 4096)) { this.maximum = maximum; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count < 0 || Position > maximum - (long)count) throw new FormatException("Checkpoint exceeds its byte budget.");
                base.Write(buffer, offset, count);
            }
            public override void WriteByte(byte value)
            {
                if (Position >= maximum) throw new FormatException("Checkpoint exceeds its byte budget.");
                base.WriteByte(value);
            }
            public override void SetLength(long value)
            {
                if (value < 0 || value > maximum) throw new FormatException("Checkpoint exceeds its byte budget.");
                base.SetLength(value);
            }
        }

        public static byte[] Encode(WorldSnapshot snapshot, CheckpointLimits limits)
        {
            if (limits == null) throw new ArgumentNullException("limits");
            Validate(snapshot, limits);
            EntityState[] entities = snapshot.Entities; PlayerState[] players = snapshot.Players;
            Array.Sort(entities, delegate(EntityState a, EntityState b) { return a.Id.CompareTo(b.Id); });
            Array.Sort(players, delegate(PlayerState a, PlayerState b) { return a.Id.CompareTo(b.Id); });
            using (BoundedMemoryStream stream = new BoundedMemoryStream(limits.MaxBytes - HashBytes))
            using (BinaryWriter writer = new BinaryWriter(stream, Utf8))
            {
                writer.Write(Magic); writer.Write(Version); writer.Write((ushort)0); writer.Write((uint)0);
                writer.Write(snapshot.Generation); writer.Write(snapshot.SnapshotId); writer.Write(snapshot.Watermark);
                writer.Write((uint)entities.Length); writer.Write((uint)players.Length);
                for (int i = 0; i < entities.Length; i++)
                {
                    EntityState entity = entities[i];
                    writer.Write(entity.Id); writer.Write(entity.Revision); writer.Write(entity.OwnershipEpoch); writer.Write(entity.MotionSequence);
                    writer.Write((byte)entity.Lifecycle); writer.Write(entity.HolderId);
                    WriteText(writer, entity.AuthoredKey, limits); WriteText(writer, entity.Archetype, limits);
                    WriteVector(writer, entity.Position); WriteVector(writer, entity.Velocity);
                }
                for (int i = 0; i < players.Length; i++)
                {
                    PlayerState player = players[i];
                    writer.Write(player.Id); writer.Write(player.ConnectionGeneration); writer.Write(player.Revision); writer.Write(player.PoseSequence); writer.Write(player.HandEntityId);
                    writer.Write((byte)(player.Connected ? 1 : 0)); writer.Write((byte)(player.Ready ? 1 : 0)); WriteVector(writer, player.Position);
                }
                writer.Flush();
                long end = stream.Length;
                stream.Position = 8; writer.Write(checked((uint)(end - HeaderBytes))); writer.Flush();
                byte[] body = stream.ToArray(), digest = Hash(body, body.Length);
                byte[] result = new byte[body.Length + HashBytes];
                Buffer.BlockCopy(body, 0, result, 0, body.Length); Buffer.BlockCopy(digest, 0, result, body.Length, HashBytes);
                return result;
            }
        }

        public static WorldSnapshot Decode(byte[] bytes, CheckpointLimits limits)
        {
            if (limits == null) throw new ArgumentNullException("limits");
            if (bytes == null || bytes.Length < HeaderBytes + 32 + HashBytes || bytes.Length > limits.MaxBytes) throw new FormatException("Invalid checkpoint byte length.");
            try
            {
                using (MemoryStream stream = new MemoryStream(bytes, false))
                using (BinaryReader reader = new BinaryReader(stream, Utf8))
                {
                    if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != Version || reader.ReadUInt16() != 0) throw new FormatException("Unsupported checkpoint magic, schema version, or flags.");
                    uint payloadLength = reader.ReadUInt32();
                    int bodyLength = bytes.Length - HashBytes;
                    if (payloadLength != (uint)(bodyLength - HeaderBytes)) throw new FormatException("Checkpoint payload length does not match its envelope.");
                    if (!HashMatches(bytes, bodyLength)) throw new FormatException("Checkpoint integrity checksum failed.");
                    ulong generation = reader.ReadUInt64(), snapshotId = reader.ReadUInt64(), watermark = reader.ReadUInt64();
                    uint entityCount = reader.ReadUInt32(), playerCount = reader.ReadUInt32();
                    if (entityCount > limits.MaxEntities || playerCount > limits.MaxPlayers) throw new FormatException("Checkpoint record count exceeds configured limits.");
                    // Minimum fixed bytes are checked before allocating attacker-controlled arrays.
                    if ((long)entityCount * 69 + (long)playerCount * 54 > bodyLength - stream.Position) throw new FormatException("Checkpoint record counts cannot fit its payload.");
                    EntityState[] entities = new EntityState[(int)entityCount];
                    PlayerState[] players = new PlayerState[(int)playerCount];
                    for (int i = 0; i < entities.Length; i++)
                    {
                        ulong id = reader.ReadUInt64(), revision = reader.ReadUInt64(), epoch = reader.ReadUInt64(), motion = reader.ReadUInt64();
                        EntityLifecycle lifecycle = (EntityLifecycle)reader.ReadByte(); ulong holder = reader.ReadUInt64();
                        string key = ReadText(reader, limits, bodyLength), archetype = ReadText(reader, limits, bodyLength);
                        WorldVector position = ReadVector(reader), velocity = ReadVector(reader);
                        entities[i] = new EntityState(generation, id, key, archetype, lifecycle, revision, epoch, motion, holder, position, velocity);
                    }
                    for (int i = 0; i < players.Length; i++)
                    {
                        ulong id = reader.ReadUInt64(), connection = reader.ReadUInt64(), revision = reader.ReadUInt64(), pose = reader.ReadUInt64(), hand = reader.ReadUInt64();
                        bool connected = ReadBoolean(reader), ready = ReadBoolean(reader); WorldVector position = ReadVector(reader);
                        players[i] = new PlayerState(id, connection, revision, pose, hand, connected, ready, position);
                    }
                    if (stream.Position != bodyLength) throw new FormatException("Checkpoint contains truncated records or trailing payload bytes.");
                    WorldSnapshot snapshot = new WorldSnapshot(generation, snapshotId, watermark, entities, players);
                    Validate(snapshot, limits);
                    return snapshot;
                }
            }
            catch (FormatException) { throw; }
            catch (EndOfStreamException error) { throw new FormatException("Checkpoint is truncated.", error); }
            catch (ArgumentException error) { throw new FormatException("Checkpoint contains invalid values or text.", error); }
            catch (OverflowException error) { throw new FormatException("Checkpoint numeric bounds exceeded.", error); }
        }

        public static void Validate(WorldSnapshot snapshot, CheckpointLimits limits)
        {
            if (snapshot == null || limits == null) throw new FormatException("A checkpoint snapshot and limits are required.");
            EntityState[] entities = snapshot.Entities; PlayerState[] players = snapshot.Players;
            if (entities.Length > limits.MaxEntities || players.Length > limits.MaxPlayers) throw new FormatException("Checkpoint record count exceeds configured limits.");
            if (new ReplicaWorld(snapshot.Generation).LoadSnapshot(snapshot) != ApplyOutcome.Applied) throw new FormatException("Checkpoint entity/player graph is incoherent.");
            for (int i = 0; i < entities.Length; i++)
            {
                CheckText(entities[i].AuthoredKey, limits); CheckText(entities[i].Archetype, limits);
            }
        }
        private static void CheckText(string text, CheckpointLimits limits)
        {
            try { if (Utf8.GetByteCount(text) > limits.MaxStringBytes) throw new FormatException("Checkpoint string exceeds its UTF-8 byte limit."); }
            catch (EncoderFallbackException error) { throw new FormatException("Checkpoint string is not valid Unicode.", error); }
        }
        private static void WriteText(BinaryWriter writer, string value, CheckpointLimits limits)
        {
            byte[] bytes = Utf8.GetBytes(value);
            if (bytes.Length > limits.MaxStringBytes) throw new FormatException("Checkpoint string exceeds its UTF-8 byte limit.");
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        private static string ReadText(BinaryReader reader, CheckpointLimits limits, int bodyLength)
        {
            ushort length = reader.ReadUInt16();
            if (length > limits.MaxStringBytes || reader.BaseStream.Position + length > bodyLength) throw new FormatException("Invalid checkpoint string length.");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new FormatException("Truncated checkpoint text.");
            return Utf8.GetString(bytes);
        }
        private static bool ReadBoolean(BinaryReader reader)
        {
            byte value = reader.ReadByte();
            if (value > 1) throw new FormatException("Invalid checkpoint Boolean encoding.");
            return value != 0;
        }
        private static void WriteVector(BinaryWriter writer, WorldVector vector) { writer.Write(vector.X); writer.Write(vector.Y); writer.Write(vector.Z); }
        private static WorldVector ReadVector(BinaryReader reader) { return new WorldVector(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); }
        internal static byte[] Hash(byte[] bytes, int count)
        {
            using (SHA256 hash = new SHA256Managed()) { return hash.ComputeHash(bytes, 0, count); }
        }
        internal static bool HashMatches(byte[] bytes, int bodyLength)
        {
            if (bodyLength < 0 || bytes.Length != bodyLength + HashBytes) return false;
            byte[] expected = Hash(bytes, bodyLength); int difference = 0;
            for (int i = 0; i < HashBytes; i++) difference |= bytes[bodyLength + i] ^ expected[i];
            return difference == 0;
        }
    }
}
