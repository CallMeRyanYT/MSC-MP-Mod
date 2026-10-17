using System;
using System.IO;
using System.Text;

namespace MSCMP.Network
{
    public static class ProtocolCapabilities
    {
        public const uint DiagnosticNetworking = 1;
        public const uint AvatarAppearanceV2 = 2;
    }

    public sealed class CompatibilityInfo
    {
        public string ModVersion;
        public string GameFingerprint;
        public string ContentManifest;
        public uint Capabilities;

        public void Validate()
        {
            ProtocolCodec.ValidateText(ModVersion, 32);
            ProtocolCodec.ValidateText(GameFingerprint, 64);
            ProtocolCodec.ValidateText(ContentManifest, 64);
            if (String.IsNullOrEmpty(ModVersion) || String.IsNullOrEmpty(GameFingerprint) || String.IsNullOrEmpty(ContentManifest))
                throw new ArgumentException("Compatibility fingerprints cannot be empty.");
        }

        public string Mismatch(CompatibilityInfo other)
        {
            if (other == null) return "Missing compatibility manifest";
            if (ModVersion != other.ModVersion) return "Mod version differs";
            if (GameFingerprint != other.GameFingerprint) return "Game fingerprint differs";
            if (ContentManifest != other.ContentManifest) return "Content/dependency manifest differs";
            if (Capabilities != other.Capabilities) return "Protocol capabilities differ";
            return null;
        }
    }

    public enum MessageKind : byte { Hello = 1, Welcome = 2, Reject = 3, Ping = 4, Pong = 5, Probe = 6, Pose = 7, Application = 8 }

    public sealed class WireMessage
    {
        public MessageKind Kind;
        public Guid SessionId;
        public ulong SessionGeneration;
        public ulong ConnectionGeneration;
        public uint Sequence;
        public Guid Nonce;
        public Guid ReconnectToken;
        public ulong PlayerId;
        public CompatibilityInfo Compatibility;
        public string Text;
        public long Timestamp;
        public float X, Y, Z, Yaw;
        public byte[] Payload;
    }

    public static class ProtocolCodec
    {
        public const ushort Version = 2;
        public const int MaximumMessageBytes = 480;
        public const int MaximumApplicationBytes = 384;
        public const int HeaderBytes = 44;
        private const uint Magic = 0x504D534D;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static bool IsNewer(uint incoming, uint current)
        {
            return incoming != current && unchecked((int)(incoming - current)) > 0;
        }

        public static void ValidateText(string value, int maximumBytes)
        {
            if (value == null || Utf8.GetByteCount(value) > maximumBytes) throw new ArgumentException("String exceeds protocol bound.");
            for (int i = 0; i < value.Length; i++)
                if (Char.IsControl(value[i])) throw new ArgumentException("Control characters are not permitted.");
        }

        public static byte[] Encode(WireMessage message)
        {
            if (message == null) throw new ArgumentNullException("message");
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Utf8))
            {
                writer.Write(Magic); writer.Write(Version); writer.Write((byte)message.Kind); writer.Write((byte)0);
                writer.Write(message.SessionId.ToByteArray()); writer.Write(message.SessionGeneration);
                writer.Write(message.ConnectionGeneration); writer.Write(message.Sequence);
                switch (message.Kind)
                {
                    case MessageKind.Hello:
                        if (message.SessionId != Guid.Empty || message.SessionGeneration != 0 || message.ConnectionGeneration != 0) throw new ArgumentException("Hello must not claim a session.");
                        WriteCompatibility(writer, message.Compatibility); WriteText(writer, message.Text, 32);
                        writer.Write(message.Nonce.ToByteArray()); writer.Write(message.ReconnectToken.ToByteArray()); break;
                    case MessageKind.Welcome:
                        WriteCompatibility(writer, message.Compatibility); writer.Write(message.Nonce.ToByteArray());
                        writer.Write(message.PlayerId); writer.Write(message.ReconnectToken.ToByteArray()); break;
                    case MessageKind.Reject: WriteText(writer, message.Text, 128); break;
                    case MessageKind.Ping:
                    case MessageKind.Pong: writer.Write(message.Timestamp); break;
                    case MessageKind.Probe: WriteText(writer, message.Text, 256); break;
                    case MessageKind.Pose:
                        ValidatePose(message); writer.Write(message.X); writer.Write(message.Y); writer.Write(message.Z); writer.Write(message.Yaw); break;
                    case MessageKind.Application:
                        ValidatePayload(message.Payload); writer.Write((ushort)message.Payload.Length); writer.Write(message.Payload); break;
                    default: throw new ArgumentException("Unknown message type.");
                }
                if (stream.Length > MaximumMessageBytes) throw new ArgumentException("Message exceeds protocol bound.");
                return stream.ToArray();
            }
        }

        public static bool TryDecode(byte[] data, out WireMessage message, out string error)
        {
            message = null; error = null;
            if (data == null || data.Length < HeaderBytes || data.Length > MaximumMessageBytes) { error = "Invalid message length"; return false; }
            try
            {
                using (MemoryStream stream = new MemoryStream(data, false))
                using (BinaryReader reader = new BinaryReader(stream, Utf8))
                {
                    if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Invalid protocol signature");
                    if (reader.ReadUInt16() != Version) throw new InvalidDataException("Protocol version differs");
                    WireMessage m = new WireMessage(); m.Kind = (MessageKind)reader.ReadByte();
                    if (reader.ReadByte() != 0) throw new InvalidDataException("Unknown header flags");
                    m.SessionId = ReadGuid(reader); m.SessionGeneration = reader.ReadUInt64();
                    m.ConnectionGeneration = reader.ReadUInt64(); m.Sequence = reader.ReadUInt32();
                    switch (m.Kind)
                    {
                        case MessageKind.Hello:
                            if (m.SessionId != Guid.Empty || m.SessionGeneration != 0 || m.ConnectionGeneration != 0) throw new InvalidDataException("Hello claims a session");
                            m.Compatibility = ReadCompatibility(reader); m.Text = ReadText(reader, 32); m.Nonce = ReadGuid(reader); m.ReconnectToken = ReadGuid(reader); break;
                        case MessageKind.Welcome:
                            m.Compatibility = ReadCompatibility(reader); m.Nonce = ReadGuid(reader); m.PlayerId = reader.ReadUInt64(); m.ReconnectToken = ReadGuid(reader); break;
                        case MessageKind.Reject: m.Text = ReadText(reader, 128); break;
                        case MessageKind.Ping:
                        case MessageKind.Pong: m.Timestamp = reader.ReadInt64(); break;
                        case MessageKind.Probe: m.Text = ReadText(reader, 256); break;
                        case MessageKind.Pose: m.X = reader.ReadSingle(); m.Y = reader.ReadSingle(); m.Z = reader.ReadSingle(); m.Yaw = reader.ReadSingle(); ValidatePose(m); break;
                        case MessageKind.Application:
                            int size = reader.ReadUInt16();
                            if (size < 1 || size > MaximumApplicationBytes) throw new InvalidDataException("Application payload exceeds bound");
                            m.Payload = ReadExact(reader, size); break;
                        default: throw new InvalidDataException("Unknown message type");
                    }
                    if (stream.Position != stream.Length) throw new InvalidDataException("Trailing message bytes");
                    message = m; return true;
                }
            }
            catch (InvalidDataException ex) { error = ex.Message; return false; }
            catch (IOException ex) { error = ex.Message; return false; }
            catch (ArgumentException ex) { error = ex.Message; return false; }
        }

        private static void ValidatePose(WireMessage m)
        {
            if (!Finite(m.X) || !Finite(m.Y) || !Finite(m.Z) || !Finite(m.Yaw) ||
                Math.Abs(m.X) > 100000 || Math.Abs(m.Y) > 100000 || Math.Abs(m.Z) > 100000 || Math.Abs(m.Yaw) > 360)
                throw new ArgumentException("Invalid or out of bounds pose.");
        }
        public static void ValidatePayload(byte[] payload)
        {
            if (payload == null || payload.Length < 1 || payload.Length > MaximumApplicationBytes)
                throw new ArgumentException("Application payload must contain 1-384 bytes.");
        }
        private static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        private static Guid ReadGuid(BinaryReader reader) { return new Guid(ReadExact(reader, 16)); }
        private static byte[] ReadExact(BinaryReader reader, int size)
        {
            byte[] bytes = reader.ReadBytes(size); if (bytes.Length != size) throw new EndOfStreamException("Truncated message"); return bytes;
        }
        private static void WriteText(BinaryWriter writer, string value, int max)
        {
            ValidateText(value, max); byte[] bytes = Utf8.GetBytes(value); writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        private static string ReadText(BinaryReader reader, int max)
        {
            int length = reader.ReadUInt16(); if (length > max) throw new InvalidDataException("String length exceeds bound");
            string value = Utf8.GetString(ReadExact(reader, length)); ValidateText(value, max); return value;
        }
        private static void WriteCompatibility(BinaryWriter writer, CompatibilityInfo c)
        {
            if (c == null) throw new ArgumentException("Missing compatibility manifest"); c.Validate();
            WriteText(writer, c.ModVersion, 32); WriteText(writer, c.GameFingerprint, 64); WriteText(writer, c.ContentManifest, 64); writer.Write(c.Capabilities);
        }
        private static CompatibilityInfo ReadCompatibility(BinaryReader reader)
        {
            CompatibilityInfo c = new CompatibilityInfo(); c.ModVersion = ReadText(reader, 32); c.GameFingerprint = ReadText(reader, 64);
            c.ContentManifest = ReadText(reader, 64); c.Capabilities = reader.ReadUInt32(); c.Validate(); return c;
        }
    }
}
