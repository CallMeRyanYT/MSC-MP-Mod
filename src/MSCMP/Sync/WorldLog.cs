using System.Collections.Generic;
using MSCMP.Net;

namespace MSCMP.Sync
{
    /// <summary>
    /// Host-side ordered history of every world change since the host loaded its save
    /// (doors, assembly, bolts, spawned/destroyed objects). A friend who joins later
    /// loads the same save and receives this log, so their world ends up identical.
    /// </summary>
    internal static class WorldLog
    {
        static readonly List<byte[]> entries = new List<byte[]>();
        static int bytes;

        public static int Count { get { return entries.Count; } }
        public static int Bytes { get { return bytes; } }

        public static void Clear()
        {
            entries.Clear();
            bytes = 0;
        }

        /// <summary>Stores a copy of the message currently in the session writer.</summary>
        public static void Append(NetWriter w)
        {
            // The host - or a player alone, who may start hosting later - remembers everything.
            if (!Session.IsAuthority || !MP.InGame) return;
            byte[] copy = w.ToArray();
            entries.Add(copy);
            bytes += copy.Length;
        }

        public static void Replay(NetPlayer p)
        {
            BatchSender batch = new BatchSender(p);
            for (int i = 0; i < entries.Count; i++)
            {
                byte[] e = entries[i];
                batch.Add(e, 0, e.Length);
            }
            batch.Flush();
            Log.Info("World log sent to " + p.Name + ": " + entries.Count + " events (" + bytes / 1024 + " KB) in " + batch.Packets + " packets.");
        }
    }
}
