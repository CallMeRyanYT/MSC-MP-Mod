namespace MSCMP.Net
{
    /// <summary>
    /// Packs many small reliable messages into a few large packets (used for the catch-up
    /// burst when a friend joins, which can be thousands of world events).
    /// </summary>
    internal sealed class BatchSender
    {
        const int Limit = 60000;
        readonly NetPlayer to;
        readonly NetWriter w = new NetWriter(Limit + 1024);
        int count;
        int countPos;

        public int Packets { get; private set; }
        public int Messages { get; private set; }

        public BatchSender(NetPlayer to) { this.to = to; }

        /// <summary>Adds a complete message (first byte = message id).</summary>
        public void Add(byte[] data, int offset, int length)
        {
            if (to == null || length <= 0) return;
            if (count > 0 && (w.Length + length + 8 > Limit || count >= 65000)) Flush();
            if (count == 0)
            {
                w.Reset();
                w.Byte((byte)Msg.Batch);
                countPos = w.Length;
                w.UShort(0);
            }
            w.Bytes(data, offset, length);
            count++;
            Messages++;
        }

        /// <summary>Adds the message currently in the session writer.</summary>
        public void AddCurrent(NetWriter current)
        {
            Add(current.Buffer, 0, current.Length);
        }

        public void Flush()
        {
            if (count == 0 || to == null) return;
            byte[] b = w.Buffer;
            b[countPos] = (byte)count;
            b[countPos + 1] = (byte)(count >> 8);
            SteamNet.Send(to.SteamId, b, w.Length, true, Channel.Reliable);
            Packets++;
            count = 0;
        }
    }
}
