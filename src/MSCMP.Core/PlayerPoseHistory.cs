using System;
using System.Collections.Generic;

namespace MSCMP.Core
{
    // Bounded history of positions actually sent. A rejection corrects that sample
    // while preserving subsequent local movement, then invalidates old in-flight samples.
    public sealed class PlayerPoseHistory
    {
        private readonly Dictionary<uint, WorldVector> samples = new Dictionary<uint, WorldVector>();
        private readonly Queue<uint> order = new Queue<uint>();
        private uint latestReceipt;
        private bool hasReceipt;
        public int Count { get { return samples.Count; } }
        public void Sent(uint sequence, WorldVector position)
        {
            if (!position.Within(100000)) throw new ArgumentException("Invalid player position.");
            if (samples.ContainsKey(sequence)) return;
            samples.Add(sequence, position); order.Enqueue(sequence);
            if (order.Count > 64) samples.Remove(order.Dequeue());
        }
        public bool Receive(uint sequence, bool accepted, WorldVector authoritative, WorldVector current, out WorldVector corrected)
        {
            corrected = current;
            if (!authoritative.Within(100000) || !current.Within(100000) || (hasReceipt && !Newer(sequence, latestReceipt))) return false;
            WorldVector sent;
            if (!samples.TryGetValue(sequence, out sent)) return false;
            latestReceipt = sequence; hasReceipt = true;
            while (order.Count != 0 && !Newer(order.Peek(), sequence)) samples.Remove(order.Dequeue());
            if (accepted) return false;
            WorldVector delta = new WorldVector(current.X - sent.X, current.Y - sent.Y, current.Z - sent.Z);
            corrected = delta.LengthSquared <= 64 ? new WorldVector(authoritative.X + delta.X, authoritative.Y + delta.Y, authoritative.Z + delta.Z) : authoritative;
            if (!corrected.Within(100000)) corrected = authoritative;
            // Replies to poses preceding this correction cannot reapply it.
            samples.Clear(); order.Clear(); return true;
        }
        public void Clear() { samples.Clear(); order.Clear(); hasReceipt = false; latestReceipt = 0; }
        private static bool Newer(uint a, uint b) { return a != b && unchecked((int)(a - b)) > 0; }
    }
}
