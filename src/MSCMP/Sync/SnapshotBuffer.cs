namespace MSCMP.Sync
{
    /// <summary>
    /// Small time-ordered ring buffer of received states used for smooth interpolation.
    /// Times are in the local clock (already converted from the sender's clock).
    /// </summary>
    internal sealed class SnapshotBuffer<T>
    {
        readonly T[] values;
        readonly float[] times;
        int count;
        int start;

        public SnapshotBuffer(int capacity = 16)
        {
            values = new T[capacity];
            times = new float[capacity];
        }

        public int Count { get { return count; } }

        public void Clear() { count = 0; start = 0; }

        float TimeAt(int i) { return times[(start + i) % times.Length]; }
        T ValueAt(int i) { return values[(start + i) % values.Length]; }

        public float NewestTime { get { return count == 0 ? float.NegativeInfinity : TimeAt(count - 1); } }

        /// <summary>Adds a state. Late (out of order) states older than the newest are ignored.</summary>
        public void Add(float time, T value)
        {
            if (count > 0 && time <= NewestTime) return;
            if (count == values.Length)
            {
                start = (start + 1) % values.Length;
                count--;
            }
            int idx = (start + count) % values.Length;
            values[idx] = value;
            times[idx] = time;
            count++;
        }

        public bool Newest(out T value)
        {
            if (count == 0) { value = default(T); return false; }
            value = ValueAt(count - 1);
            return true;
        }

        /// <summary>Finds the two states around time t. alpha is 0..1 between them.
        /// If t is past the newest state, returns newest/newest with the time beyond it in 'over'.</summary>
        public bool Sample(float t, out T a, out T b, out float alpha, out float over)
        {
            a = default(T); b = default(T); alpha = 0f; over = 0f;
            if (count == 0) return false;
            if (count == 1 || t <= TimeAt(0))
            {
                a = b = ValueAt(count == 1 ? 0 : 0);
                if (count == 1) over = t - TimeAt(0);
                return true;
            }
            for (int i = count - 2; i >= 0; i--)
            {
                float t0 = TimeAt(i);
                if (t0 <= t)
                {
                    float t1 = TimeAt(i + 1);
                    if (t >= t1)
                    {
                        a = b = ValueAt(i + 1);
                        over = t - t1;
                        return true;
                    }
                    a = ValueAt(i);
                    b = ValueAt(i + 1);
                    alpha = (t - t0) / (t1 - t0);
                    return true;
                }
            }
            a = b = ValueAt(0);
            return true;
        }
    }
}
