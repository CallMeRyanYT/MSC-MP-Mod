using System;
using System.Globalization;
using System.Text;

namespace MSCMP
{
    internal sealed class DiagnosticTrace
    {
        internal const int Capacity = 128;
        internal const int LifecycleCapacity = 64;
        internal const int MaxEntryLength = 320;
        private readonly string[] entries = new string[Capacity];
        private readonly string[] lifecycleEntries = new string[LifecycleCapacity];
        private int next;
        private int count;
        private int lifecycleNext;
        private int lifecycleCount;

        internal int Count { get { return count; } }
        internal int LifecycleCount { get { return lifecycleCount; } }

        internal string Add(string eventName, string detail)
        {
            return Add(eventName, detail, false);
        }

        internal string Add(string eventName, string detail, bool retainLifecycle)
        {
            string line = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
                + " " + SafeText(eventName, 48) + " " + SafeText(detail, 240);
            if (line.Length > MaxEntryLength)
                line = line.Substring(0, MaxEntryLength);
            entries[next] = line;
            next = (next + 1) % Capacity;
            if (count < Capacity)
                count++;
            if (retainLifecycle)
            {
                lifecycleEntries[lifecycleNext] = line;
                lifecycleNext = (lifecycleNext + 1) % LifecycleCapacity;
                if (lifecycleCount < LifecycleCapacity)
                    lifecycleCount++;
            }
            return line;
        }

        internal string Snapshot()
        {
            return Format(entries, next, count);
        }

        internal string LifecycleSnapshot()
        {
            return Format(lifecycleEntries, lifecycleNext, lifecycleCount);
        }

        private static string Format(string[] buffer, int nextEntry, int entryCount)
        {
            StringBuilder result = new StringBuilder(entryCount * 160);
            int first = (nextEntry - entryCount + buffer.Length) % buffer.Length;
            for (int index = 0; index < entryCount; index++)
                result.AppendLine(buffer[(first + index) % buffer.Length]);
            return result.ToString();
        }

        internal string LastEntry
        {
            get { return count == 0 ? "No events yet." : entries[(next - 1 + Capacity) % Capacity]; }
        }

        // These values are labels, never paths, variables, save contents or exception messages.
        // Remove controls and rich-text delimiters so object names cannot forge log entries.
        internal static string SafeText(string value, int maxLength)
        {
            if (String.IsNullOrEmpty(value))
                return "(none)";
            int length = Math.Min(value.Length, maxLength);
            StringBuilder result = new StringBuilder(length);
            for (int index = 0; index < length; index++)
            {
                char item = value[index];
                result.Append(Char.IsControl(item) || item == '<' || item == '>' ? '_' : item);
            }
            return result.ToString();
        }
    }
}
