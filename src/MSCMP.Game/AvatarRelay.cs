using System;
using System.Collections.Generic;
using System.IO;

namespace MSCMP.Integration
{
    internal enum AvatarRelayKind : byte { Pose = 1, Remove = 2, Appearance = 3 }

    internal sealed class AvatarRelayMessage
    {
        internal AvatarRelayKind Kind;
        internal ulong PlayerId;
        internal ulong ConnectionGeneration;
        internal uint Sequence;
        internal float X, Y, Z, Yaw;
        internal CharacterStyle Style;
    }

    internal static class AvatarRelayCodec
    {
        private const uint Magic = 0x3256414d; // MAV2, little endian. Appearance baseline required.

        internal static byte[] Encode(AvatarRelayMessage message)
        {
            if (!Valid(message)) throw new ArgumentException("Invalid avatar relay.");
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(Magic); writer.Write((byte)2); writer.Write((byte)message.Kind);
                writer.Write(message.PlayerId); writer.Write(message.ConnectionGeneration); writer.Write(message.Sequence);
                if (message.Kind == AvatarRelayKind.Pose)
                { writer.Write(message.X); writer.Write(message.Y); writer.Write(message.Z); writer.Write(message.Yaw); }
                else if (message.Kind == AvatarRelayKind.Appearance) writer.Write((byte)message.Style);
                return stream.ToArray();
            }
        }

        internal static bool TryDecode(byte[] bytes, out AvatarRelayMessage message)
        {
            message = null;
            if (bytes == null || (bytes.Length != 26 && bytes.Length != 27 && bytes.Length != 42)) return false;
            try
            {
                using (MemoryStream stream = new MemoryStream(bytes, false))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (reader.ReadUInt32() != Magic || reader.ReadByte() != 2) return false;
                    AvatarRelayMessage value = new AvatarRelayMessage();
                    value.Kind = (AvatarRelayKind)reader.ReadByte(); value.PlayerId = reader.ReadUInt64();
                    value.ConnectionGeneration = reader.ReadUInt64(); value.Sequence = reader.ReadUInt32();
                    if (value.Kind == AvatarRelayKind.Pose)
                    { value.X = reader.ReadSingle(); value.Y = reader.ReadSingle(); value.Z = reader.ReadSingle(); value.Yaw = reader.ReadSingle(); }
                    else if (value.Kind == AvatarRelayKind.Appearance) value.Style = (CharacterStyle)reader.ReadByte();
                    if (stream.Position != stream.Length || !Valid(value)) return false;
                    message = value;
                    return true;
                }
            }
            catch (EndOfStreamException) { return false; }
            catch (IOException) { return false; }
        }

        private static bool Valid(AvatarRelayMessage value)
        {
            if (value == null || value.PlayerId == 0 || value.PlayerId > Int32.MaxValue || value.ConnectionGeneration == 0) return false;
            if (value.Kind == AvatarRelayKind.Remove) return true;
            if (value.Kind == AvatarRelayKind.Appearance) return IsStyle(value.Style);
            return value.Kind == AvatarRelayKind.Pose && Coordinate(value.X) && Coordinate(value.Y) && Coordinate(value.Z)
                && !Single.IsNaN(value.Yaw) && !Single.IsInfinity(value.Yaw) && Math.Abs(value.Yaw) <= 360;
        }

        private static bool Coordinate(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value) && Math.Abs(value) <= 100000; }
        internal static bool IsStyle(CharacterStyle style) { return style == CharacterStyle.Male || style == CharacterStyle.Female; }

        // Called only for an accepted client-to-host application envelope. The
        // envelope identity/sequence is authoritative; the body must agree.
        internal static bool BindClientAppearance(byte[] payload, ulong boundPlayer, ulong boundGeneration,
            uint boundSequence, out AvatarRelayMessage appearance)
        {
            appearance = null;
            AvatarRelayMessage candidate;
            if (boundPlayer < 2 || boundGeneration == 0 || !TryDecode(payload, out candidate)
                || candidate.Kind != AvatarRelayKind.Appearance || candidate.PlayerId != boundPlayer
                || candidate.ConnectionGeneration != boundGeneration || candidate.Sequence != 0) return false;
            candidate.Sequence = boundSequence;
            appearance = candidate;
            return true;
        }
    }

    /// <summary>Critical removals survive failed transport enqueues without unbounded retry storage.</summary>
    internal sealed class AvatarRemovalOutbox
    {
        private sealed class Recipient
        {
            internal ulong Generation;
            internal readonly Dictionary<ulong, AvatarRelayMessage> Pending = new Dictionary<ulong, AvatarRelayMessage>();
        }
        private readonly Dictionary<ulong, Recipient> recipients = new Dictionary<ulong, Recipient>();

        internal bool Queue(ulong target, ulong targetGeneration, AvatarRelayMessage removal)
        {
            if (target < 2 || target > Int32.MaxValue || targetGeneration == 0 || removal == null
                || removal.Kind != AvatarRelayKind.Remove || removal.PlayerId < 2 || removal.PlayerId > Int32.MaxValue
                || removal.PlayerId == target || removal.ConnectionGeneration == 0) return false;
            Recipient recipient;
            if (!recipients.TryGetValue(target, out recipient))
            {
                if (recipients.Count >= 3) return false;
                recipient = new Recipient { Generation = targetGeneration };
                recipients.Add(target, recipient);
            }
            if (targetGeneration < recipient.Generation) return false;
            if (targetGeneration > recipient.Generation) { recipient.Pending.Clear(); recipient.Generation = targetGeneration; }
            AvatarRelayMessage previous;
            if (recipient.Pending.TryGetValue(removal.PlayerId, out previous))
            { if (previous.ConnectionGeneration >= removal.ConnectionGeneration) return true; }
            else if (recipient.Pending.Count >= 128) return false;
            recipient.Pending[removal.PlayerId] = new AvatarRelayMessage { Kind = AvatarRelayKind.Remove,
                PlayerId = removal.PlayerId, ConnectionGeneration = removal.ConnectionGeneration };
            return true;
        }

        internal AvatarRelayMessage[] Pending(ulong target, ulong targetGeneration)
        {
            Recipient recipient;
            if (!recipients.TryGetValue(target, out recipient) || recipient.Generation != targetGeneration) return new AvatarRelayMessage[0];
            List<AvatarRelayMessage> result = new List<AvatarRelayMessage>();
            foreach (AvatarRelayMessage message in recipient.Pending.Values)
                result.Add(new AvatarRelayMessage { Kind = AvatarRelayKind.Remove, PlayerId = message.PlayerId,
                    ConnectionGeneration = message.ConnectionGeneration });
            return result.ToArray();
        }

        internal void Confirm(ulong target, ulong targetGeneration, AvatarRelayMessage delivered)
        {
            Recipient recipient; AvatarRelayMessage pending;
            if (delivered != null && recipients.TryGetValue(target, out recipient) && recipient.Generation == targetGeneration
                && recipient.Pending.TryGetValue(delivered.PlayerId, out pending) && pending.ConnectionGeneration == delivered.ConnectionGeneration)
                recipient.Pending.Remove(delivered.PlayerId);
        }

        internal void RemoveRecipient(ulong target) { recipients.Remove(target); }
        internal void Clear() { recipients.Clear(); }
    }

    /// <summary>Bounded identities and persistent removal tombstones for one accepted session.</summary>
    internal sealed class AvatarRelayTracker
    {
        private sealed class Record
        {
            internal ulong Generation;
            internal uint PoseSequence, AppearanceSequence;
            internal bool Removed, HasPose, HasAppearance;
            internal CharacterStyle Style;
        }
        private readonly Dictionary<ulong, Record> records = new Dictionary<ulong, Record>();
        private int active;

        internal bool Apply(AvatarRelayMessage message, ulong ownPlayer, out bool reset)
        {
            reset = false;
            if (message == null || message.PlayerId == ownPlayer || message.PlayerId == 0 || message.PlayerId > Int32.MaxValue || message.ConnectionGeneration == 0
                || (message.Kind != AvatarRelayKind.Pose && message.Kind != AvatarRelayKind.Remove && message.Kind != AvatarRelayKind.Appearance)
                || (message.Kind == AvatarRelayKind.Appearance && !AvatarRelayCodec.IsStyle(message.Style))) return false;
            Record record;
            if (!records.TryGetValue(message.PlayerId, out record))
            {
                // Refuse new identities when full; never evict a tombstone and resurrect an old peer.
                if (message.Kind == AvatarRelayKind.Pose || records.Count >= 128 || (message.Kind == AvatarRelayKind.Appearance && active >= 3)) return false;
                record = new Record { Generation = message.ConnectionGeneration, AppearanceSequence = message.Sequence,
                    Removed = message.Kind == AvatarRelayKind.Remove, HasAppearance = message.Kind == AvatarRelayKind.Appearance, Style = message.Style };
                records.Add(message.PlayerId, record);
                if (!record.Removed) active++;
                reset = true;
                return true;
            }
            if (message.ConnectionGeneration < record.Generation) return false;
            if (message.ConnectionGeneration > record.Generation)
            {
                // A pose cannot establish a new connection's appearance or revive it.
                if (message.Kind == AvatarRelayKind.Pose || (record.Removed && message.Kind == AvatarRelayKind.Appearance && active >= 3)) return false;
                if (!record.Removed) active--;
                record.Generation = message.ConnectionGeneration; record.AppearanceSequence = message.Sequence;
                record.HasPose = false; record.HasAppearance = message.Kind == AvatarRelayKind.Appearance; record.Style = message.Style;
                record.Removed = message.Kind == AvatarRelayKind.Remove;
                if (!record.Removed) active++;
                reset = true;
                return true;
            }
            if (record.Removed) return false;
            if (message.Kind == AvatarRelayKind.Remove)
            { record.Removed = true; active--; reset = true; return true; }
            if (message.Kind == AvatarRelayKind.Appearance)
            {
                if (record.HasAppearance && !Newer(message.Sequence, record.AppearanceSequence)) return false;
                reset = !record.HasAppearance || record.Style != message.Style;
                record.HasAppearance = true; record.Style = message.Style; record.AppearanceSequence = message.Sequence;
                return true;
            }
            if (message.Kind != AvatarRelayKind.Pose || !record.HasAppearance || (record.HasPose && !Newer(message.Sequence, record.PoseSequence))) return false;
            record.PoseSequence = message.Sequence; record.HasPose = true;
            return true;
        }

        internal bool TryGetAppearance(ulong actor, ulong connectionGeneration, out CharacterStyle style)
        {
            style = CharacterStyle.Male;
            Record record;
            if (!records.TryGetValue(actor, out record) || record.Removed || !record.HasAppearance || record.Generation != connectionGeneration) return false;
            style = record.Style;
            return true;
        }

        internal AvatarRelayMessage[] GetAppearances()
        {
            List<AvatarRelayMessage> result = new List<AvatarRelayMessage>();
            foreach (KeyValuePair<ulong, Record> entry in records)
                if (!entry.Value.Removed && entry.Value.HasAppearance)
                    result.Add(new AvatarRelayMessage { Kind = AvatarRelayKind.Appearance, PlayerId = entry.Key,
                        ConnectionGeneration = entry.Value.Generation, Sequence = entry.Value.AppearanceSequence, Style = entry.Value.Style });
            return result.ToArray();
        }

        private static bool Newer(uint candidate, uint previous) { return unchecked((int)(candidate - previous)) > 0; }
    }
}
