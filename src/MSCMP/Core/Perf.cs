using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MSCMP
{
    /// <summary>
    /// How much time the mod itself takes. Every part of the mod that runs each frame is timed;
    /// every 5 seconds the average cost per frame is worked out (shown in the F2 panel), and
    /// when the mod takes more than 2 ms per frame the most expensive parts go to mscmp.log.
    /// </summary>
    internal static class Perf
    {
        static readonly Dictionary<string, long> ticks = new Dictionary<string, long>();
        static int frames;
        static long windowStart;

        /// <summary>Average milliseconds per frame the mod used in the last 5 seconds.</summary>
        public static float MsPerFrame { get; private set; }

        public static long Now { get { return Stopwatch.GetTimestamp(); } }

        public static void Add(string name, long elapsedTicks)
        {
            long t;
            ticks.TryGetValue(name, out t);
            ticks[name] = t + elapsedTicks;
        }

        /// <summary>Called once per frame.</summary>
        public static void Frame()
        {
            frames++;
            long now = Stopwatch.GetTimestamp();
            if (windowStart == 0) { windowStart = now; return; }
            double seconds = (now - windowStart) / (double)Stopwatch.Frequency;
            if (seconds < 5.0 || frames == 0) return;

            long total = 0;
            foreach (long t in ticks.Values) total += t;
            double msPerTick = 1000.0 / Stopwatch.Frequency;
            MsPerFrame = (float)(total * msPerTick / frames);
            if (MsPerFrame > 2f)
            {
                List<KeyValuePair<string, long>> list = new List<KeyValuePair<string, long>>(ticks);
                list.Sort((a, b) => b.Value.CompareTo(a.Value));
                StringBuilder sb = new StringBuilder("Performance: the mod uses " + MsPerFrame.ToString("0.0") + " ms per frame (" + (frames / seconds).ToString("0") + " fps). Most:");
                for (int i = 0; i < list.Count && i < 5; i++)
                    sb.Append(' ').Append(list[i].Key).Append(' ').Append((list[i].Value * msPerTick / frames).ToString("0.00")).Append(" ms;");
                Log.Info(sb.ToString());
            }
            ticks.Clear();
            frames = 0;
            windowStart = now;
        }
    }
}
