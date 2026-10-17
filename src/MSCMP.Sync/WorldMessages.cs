using System;
using System.IO;
using System.Text;
using MSCMP.Core;

namespace MSCMP.Sync
{
    public enum WorldMessageKind : byte { Snapshot = 1, SnapshotAck = 2, CatchUp = 3, ReadyRequest = 4, Ready = 5, Commit = 6, Command = 7, Result = 8, LiveBatch = 9, PoseReceipt = 10 }
    public sealed class WorldMessage
    {
        public WorldMessageKind Kind;
        public WorldSnapshot Snapshot;
        public CatchUpBatch CatchUp;
        public ReadyGrant Grant;
        public CommitEvent Commit;
        public CommitEvent[] Commits;
        public uint PoseSequence;
        public bool PoseAccepted;
        public WorldVector Position;
        public AuthorityCommand Command;
        public CommandOutcome Outcome;
        public ulong SnapshotId, Watermark, RequestSequence;
    }

    // Own protocol: explicit byte lengths/counts, strict UTF-8, no runtime serializers.
    // Connection identities in a command are injected by Decode, never read from its body.
    public static class WorldMessages
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public static byte[] Encode(WorldMessage message)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write((uint)0x32574D53); writer.Write((byte)message.Kind);
                switch (message.Kind)
                {
                    case WorldMessageKind.Snapshot:
                        WorldSnapshot s = message.Snapshot;
                        writer.Write(s.Generation); writer.Write(s.SnapshotId); writer.Write(s.Watermark);
                        EntityState[] entities = s.Entities; PlayerState[] players = s.Players;
                        if (entities.Length > 4096 || players.Length > 64) throw new FormatException("World transfer capacity exceeded.");
                        writer.Write(entities.Length); writer.Write(players.Length);
                        foreach (EntityState entity in entities) WriteEntity(writer, entity);
                        foreach (PlayerState player in players) WritePlayer(writer, player);
                        break;
                    case WorldMessageKind.SnapshotAck: case WorldMessageKind.ReadyRequest:
                        writer.Write(message.SnapshotId); writer.Write(message.Watermark); break;
                    case WorldMessageKind.CatchUp:
                        CatchUpBatch batch = message.CatchUp; writer.Write((byte)batch.Status);
                        writer.Write(batch.SnapshotId); writer.Write(batch.FromWatermark); writer.Write(batch.ToWatermark);
                        CommitEvent[] events = batch.Events;
                        if (events.Length > 4096) throw new FormatException("Catch-up capacity exceeded.");
                        writer.Write(events.Length); foreach (CommitEvent commit in events) WriteCommit(writer, commit); break;
                    case WorldMessageKind.Ready:
                        writer.Write((byte)message.Grant.Status); writer.Write(message.Grant.SnapshotId);
                        writer.Write(message.Grant.Commit != null); if (message.Grant.Commit != null) WriteCommit(writer, message.Grant.Commit); break;
                    case WorldMessageKind.Commit: WriteCommit(writer, message.Commit); break;
                    case WorldMessageKind.LiveBatch:
                        if (message.Commits == null || message.Commits.Length < 1 || message.Commits.Length > 16) throw new FormatException("Live batch bound.");
                        writer.Write(message.Commits.Length); foreach (CommitEvent entry in message.Commits) WriteCommit(writer, entry); break;
                    case WorldMessageKind.PoseReceipt:
                        writer.Write(message.PoseSequence); writer.Write(message.PoseAccepted); WriteVector(writer, message.Position); break;
                    case WorldMessageKind.Command:
                        AuthorityCommand command = message.Command;
                        writer.Write((byte)command.Kind); writer.Write(command.Generation); writer.Write(command.Sequence); writer.Write(command.EntityId);
                        writer.Write(command.ExpectedRevision); writer.Write(command.ExpectedOwnershipEpoch);
                        WriteVector(writer, command.ReleasePosition); WriteVector(writer, command.ReleaseVelocity); break;
                    case WorldMessageKind.Result:
                        writer.Write((byte)message.Outcome); writer.Write(message.RequestSequence); writer.Write(message.Watermark); break;
                    default: throw new FormatException("Unknown world message.");
                }
                if (stream.Length > ReliableBlobSender.MaximumBlob) throw new FormatException("World message exceeds transfer bound.");
                return stream.ToArray();
            }
        }
        public static WorldMessage Decode(byte[] bytes, ulong boundPlayer, ulong boundConnection)
        {
            if (bytes == null || bytes.Length < 5 || bytes.Length > ReliableBlobSender.MaximumBlob) throw new FormatException("Invalid world message length.");
            using (MemoryStream stream = new MemoryStream(bytes, false))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt32() != 0x32574D53) throw new FormatException("Unknown world message format.");
                WorldMessage message = new WorldMessage(); message.Kind = (WorldMessageKind)reader.ReadByte();
                switch (message.Kind)
                {
                    case WorldMessageKind.Snapshot:
                        ulong generation = reader.ReadUInt64(), snapshot = reader.ReadUInt64(), watermark = reader.ReadUInt64();
                        int entityCount = Count(reader, 4096), playerCount = Count(reader, 64);
                        EntityState[] entities = new EntityState[entityCount]; PlayerState[] players = new PlayerState[playerCount];
                        for (int i = 0; i < entities.Length; i++) entities[i] = ReadEntity(reader);
                        for (int i = 0; i < players.Length; i++) players[i] = ReadPlayer(reader);
                        message.Snapshot = new WorldSnapshot(generation, snapshot, watermark, entities, players); break;
                    case WorldMessageKind.SnapshotAck: case WorldMessageKind.ReadyRequest:
                        message.SnapshotId = reader.ReadUInt64(); message.Watermark = reader.ReadUInt64(); break;
                    case WorldMessageKind.CatchUp:
                        CatchUpStatus status = (CatchUpStatus)EnumByte(reader, typeof(CatchUpStatus));
                        ulong id = reader.ReadUInt64(), from = reader.ReadUInt64(), to = reader.ReadUInt64();
                        CommitEvent[] commits = new CommitEvent[Count(reader, 4096)];
                        for (int i = 0; i < commits.Length; i++) commits[i] = ReadCommit(reader);
                        message.CatchUp = new CatchUpBatch(status, id, from, to, commits); break;
                    case WorldMessageKind.Ready:
                        ReadyStatus ready = (ReadyStatus)EnumByte(reader, typeof(ReadyStatus));
                        ulong readyId = reader.ReadUInt64(); CommitEvent readyCommit = Boolean(reader) ? ReadCommit(reader) : null;
                        message.Grant = new ReadyGrant(ready, readyId, readyCommit); break;
                    case WorldMessageKind.Commit: message.Commit = ReadCommit(reader); break;
                    case WorldMessageKind.LiveBatch:
                        int liveCount = Count(reader, 16); if (liveCount == 0) throw new FormatException("Empty live batch.");
                        message.Commits = new CommitEvent[liveCount];
                        for (int i = 0; i < liveCount; i++) message.Commits[i] = ReadCommit(reader); break;
                    case WorldMessageKind.PoseReceipt:
                        message.PoseSequence = reader.ReadUInt32(); message.PoseAccepted = Boolean(reader); message.Position = ReadVector(reader); break;
                    case WorldMessageKind.Command:
                        CommandKind kind = (CommandKind)EnumByte(reader, typeof(CommandKind));
                        ulong world = reader.ReadUInt64(), sequence = reader.ReadUInt64(), entity = reader.ReadUInt64(), revision = reader.ReadUInt64(), ownership = reader.ReadUInt64();
                        message.Command = new AuthorityCommand(kind, world, boundPlayer, boundConnection, sequence, entity, revision, ownership, ReadVector(reader), ReadVector(reader)); break;
                    case WorldMessageKind.Result:
                        message.Outcome = (CommandOutcome)EnumByte(reader, typeof(CommandOutcome)); message.RequestSequence = reader.ReadUInt64(); message.Watermark = reader.ReadUInt64(); break;
                    default: throw new FormatException("Unknown world message kind.");
                }
                if (stream.Position != stream.Length) throw new FormatException("Trailing world message bytes.");
                return message;
            }
        }
        private static int Count(BinaryReader reader, int maximum)
        { int value = reader.ReadInt32(); if (value < 0 || value > maximum) throw new FormatException("Count outside bound."); return value; }
        private static int EnumByte(BinaryReader reader, Type type)
        { int value = reader.ReadByte(); if (!Enum.IsDefined(type, value)) throw new FormatException("Unknown enum value."); return value; }
        private static bool Boolean(BinaryReader reader)
        { byte value = reader.ReadByte(); if (value > 1) throw new FormatException("Invalid boolean."); return value == 1; }
        private static void WriteText(BinaryWriter writer, string value)
        { byte[] bytes = Utf8.GetBytes(value); if (bytes.Length > 1024) throw new FormatException("Identity text too long."); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
        private static string ReadText(BinaryReader reader)
        { int length = reader.ReadUInt16(); if (length > 1024 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new FormatException("Identity text too long."); return Utf8.GetString(reader.ReadBytes(length)); }
        private static void WriteVector(BinaryWriter writer, WorldVector vector)
        { if (!vector.IsFinite) throw new FormatException("Non-finite vector."); writer.Write(vector.X); writer.Write(vector.Y); writer.Write(vector.Z); }
        private static WorldVector ReadVector(BinaryReader reader)
        { WorldVector value = new WorldVector(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); if (!value.IsFinite) throw new FormatException("Non-finite vector."); return value; }
        private static void WriteEntity(BinaryWriter writer, EntityState e)
        {
            writer.Write(e.Generation); writer.Write(e.Id); WriteText(writer, e.AuthoredKey); WriteText(writer, e.Archetype);
            writer.Write((byte)e.Lifecycle); writer.Write(e.Revision); writer.Write(e.OwnershipEpoch); writer.Write(e.MotionSequence); writer.Write(e.HolderId);
            WriteVector(writer, e.Position); WriteVector(writer, e.Velocity);
            writer.Write(e.Rotation.X); writer.Write(e.Rotation.Y); writer.Write(e.Rotation.Z); writer.Write(e.Rotation.W); WriteVector(writer, e.AngularVelocity);
        }
        private static EntityState ReadEntity(BinaryReader reader)
        {
            ulong generation = reader.ReadUInt64(), id = reader.ReadUInt64(); string key = ReadText(reader), archetype = ReadText(reader);
            EntityLifecycle lifecycle = (EntityLifecycle)EnumByte(reader, typeof(EntityLifecycle));
            ulong revision = reader.ReadUInt64(), epoch = reader.ReadUInt64(), motion = reader.ReadUInt64(), holder = reader.ReadUInt64();
            WorldVector position = ReadVector(reader), velocity = ReadVector(reader);
            WorldRotation rotation = new WorldRotation(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            return new EntityState(generation, id, key, archetype, lifecycle, revision, epoch, motion, holder, position, velocity, rotation, ReadVector(reader));
        }
        private static void WritePlayer(BinaryWriter writer, PlayerState p)
        { writer.Write(p.Id); writer.Write(p.ConnectionGeneration); writer.Write(p.Revision); writer.Write(p.PoseSequence); writer.Write(p.HandEntityId); writer.Write(p.Connected); writer.Write(p.Ready); WriteVector(writer, p.Position); }
        private static PlayerState ReadPlayer(BinaryReader reader)
        { ulong id = reader.ReadUInt64(), generation = reader.ReadUInt64(), revision = reader.ReadUInt64(), pose = reader.ReadUInt64(), hand = reader.ReadUInt64(); bool connected = Boolean(reader), ready = Boolean(reader); return new PlayerState(id, generation, revision, pose, hand, connected, ready, ReadVector(reader)); }
        private static void WriteCommit(BinaryWriter writer, CommitEvent c)
        { writer.Write(c.Generation); writer.Write(c.Sequence); writer.Write((byte)c.Kind); writer.Write(c.RequestPlayerId); writer.Write(c.RequestSequence); writer.Write(c.Entity != null); if (c.Entity != null) WriteEntity(writer, c.Entity); writer.Write(c.Player != null); if (c.Player != null) WritePlayer(writer, c.Player); }
        private static CommitEvent ReadCommit(BinaryReader reader)
        { ulong generation = reader.ReadUInt64(), sequence = reader.ReadUInt64(); CommitKind kind = (CommitKind)EnumByte(reader, typeof(CommitKind)); ulong player = reader.ReadUInt64(), request = reader.ReadUInt64(); EntityState entity = Boolean(reader) ? ReadEntity(reader) : null; PlayerState state = Boolean(reader) ? ReadPlayer(reader) : null; return new CommitEvent(generation, sequence, kind, player, request, entity, state); }
    }
}
