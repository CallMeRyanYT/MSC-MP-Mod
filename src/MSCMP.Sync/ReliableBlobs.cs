using System;
using System.Collections.Generic;
using System.IO;

namespace MSCMP.Sync
{
    // Application framing over an already ordered, reliable, connection-bound stream.
    // Never enables transport-library fragmentation. A peer owns at most one receive buffer.
    public sealed class ReliableBlobSender
    {
        // Small proven slice: at the shared 128 datagram/s limit, three concurrent
        // transfers fit comfortably within the timeout. Larger worlds need a paced bulk channel.
        public const int MaximumBlob = 128 * 1024;
        public const int ChunkSize = 350;
        private readonly Queue<byte[]> pending = new Queue<byte[]>();
        private int bytes;
        private int offset;
        private ulong transfer = 1;
        public int PendingBytes { get { return bytes; } }
        public int PendingCount { get { return pending.Count; } }

        public bool Enqueue(byte[] payload)
        {
            if (payload == null || payload.Length == 0 || payload.Length > MaximumBlob) throw new ArgumentException("Invalid blob length.");
            if (pending.Count >= 32 || bytes > MaximumBlob - payload.Length) return false;
            pending.Enqueue((byte[])payload.Clone()); bytes += payload.Length; return true;
        }
        public int Pump(Func<byte[], bool> send, int budget)
        {
            if (send == null || budget < 1 || budget > 64) throw new ArgumentException("Invalid send budget.");
            int sent = 0;
            while (pending.Count > 0 && sent < budget)
            {
                byte[] blob = pending.Peek();
                int count = Math.Min(ChunkSize, blob.Length - offset);
                byte[] frame;
                using (MemoryStream stream = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write((uint)0x31424D53); writer.Write(transfer); writer.Write(blob.Length); writer.Write(offset);
                    writer.Write((ushort)count); writer.Write(blob, offset, count); frame = stream.ToArray();
                }
                if (!send(frame)) break;
                sent++; offset += count;
                if (offset == blob.Length)
                {
                    bytes -= blob.Length; pending.Dequeue(); offset = 0;
                    if (transfer == UInt64.MaxValue) throw new InvalidOperationException("Transfer identity exhausted; reconnect.");
                    transfer++;
                }
            }
            return sent;
        }
    }

    public sealed class ReliableBlobReceiver
    {
        private byte[] buffer;
        private int received;
        private ulong expectedTransfer = 1;
        private long started;
        public int AllocatedBytes { get { return buffer == null ? 0 : buffer.Length; } }
        public byte[] Receive(byte[] frame, long nowMilliseconds)
        {
            CheckTimeout(nowMilliseconds);
            if (frame == null || frame.Length < 23 || frame.Length > ReliableBlobSender.ChunkSize + 22) throw new FormatException("Invalid chunk length.");
            using (MemoryStream stream = new MemoryStream(frame, false))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt32() != 0x31424D53) throw new FormatException("Unknown chunk format.");
                ulong transfer = reader.ReadUInt64(); int total = reader.ReadInt32(); int offset = reader.ReadInt32(); int count = reader.ReadUInt16();
                if (transfer != expectedTransfer || total < 1 || total > ReliableBlobSender.MaximumBlob || count < 1 || count > ReliableBlobSender.ChunkSize
                    || frame.Length != count + 22 || offset != received || offset > total - count) throw new FormatException("Invalid chunk sequence or bound.");
                if (buffer == null) { buffer = new byte[total]; started = nowMilliseconds; }
                if (total != buffer.Length) throw new FormatException("Chunk changed the transfer size.");
                Buffer.BlockCopy(frame, 22, buffer, received, count); received += count;
                if (received != buffer.Length) return null;
                byte[] result = buffer; buffer = null; received = 0;
                if (expectedTransfer == UInt64.MaxValue) throw new FormatException("Transfer identity exhausted.");
                expectedTransfer++; return result;
            }
        }
        public void CheckTimeout(long nowMilliseconds)
        {
            if (buffer != null && (nowMilliseconds < started || nowMilliseconds - started > 120000))
            { buffer = null; received = 0; throw new FormatException("Incomplete world transfer timed out; reconnect."); }
        }
    }
}
