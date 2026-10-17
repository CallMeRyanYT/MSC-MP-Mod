using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MSCMP
{
    /// <summary>Bounded, sanitized error evidence. Never reads files or accesses Unity.</summary>
    internal sealed class FeedbackBuffer
    {
        internal const int MaximumEntries = 32;
        internal const int MaximumMessageLength = 2048;
        internal const int MaximumStackLength = 4096;
        internal const int MaximumSnapshotBytes = 64 * 1024;
        internal const long DuplicateWindowMilliseconds = 10000;
        private const int MaximumKindLength = 64;
        private const int HeaderReserveBytes = 128;
        private static readonly Regex UserFolder = new Regex(
            @"(?i)(?:\\\\\?\\)?[a-z]:[\\/]+(?:Users|Documents and Settings)[\\/]+[^\\/""<>|\r\n:]+|\\\\[^\\/""<>|\r\n:]+[\\/]+(?:Users|Documents and Settings)[\\/]+[^\\/""<>|\r\n:]+",
            RegexOptions.CultureInvariant);
        private static readonly Regex Ipv4 = new Regex(
            @"(?<![a-zA-Z0-9_.])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?::[0-9]{1,5})?(?![a-zA-Z0-9_.])",
            RegexOptions.CultureInvariant);

        private sealed class Entry
        {
            internal string Kind, Message, Stack;
            internal long Time;
            internal int Bytes;
        }

        private readonly object gate = new object();
        private readonly List<Entry> entries = new List<Entry>();
        private readonly Func<long> milliseconds;
        private int bytes, dropped;
        private long lastTime;

        internal FeedbackBuffer() : this(CreateClock()) { }

        internal FeedbackBuffer(Func<long> monotonicMilliseconds)
        {
            if (monotonicMilliseconds == null) throw new ArgumentNullException("monotonicMilliseconds");
            milliseconds = monotonicMilliseconds;
        }

        internal int Count { get { lock (gate) return entries.Count; } }
        internal int DroppedCount { get { lock (gate) return dropped; } }

        internal bool Add(string kind, string message, string stack)
        {
            // Sanitize before retaining anything. Processing is bounded even when a
            // third-party logger supplies a very large or malformed string.
            string safeKind = Sanitize(kind, MaximumKindLength);
            string safeMessage = Sanitize(message, MaximumMessageLength);
            string safeStack = Sanitize(stack, MaximumStackLength);
            if (safeKind.Length == 0) safeKind = "Unknown";
            if (safeMessage.Length == 0) safeMessage = "(no message)";
            lock (gate)
            {
                long now = Math.Max(lastTime, Math.Max(0, milliseconds()));
                lastTime = now;
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    Entry existing = entries[i];
                    if (now - existing.Time >= DuplicateWindowMilliseconds) break;
                    if (existing.Kind == safeKind && existing.Message == safeMessage && existing.Stack == safeStack)
                    { IncrementDropped(); return false; }
                }
                string text = "[+" + now.ToString(CultureInfo.InvariantCulture) + "ms] " + safeKind + ": " + safeMessage + "\n";
                if (safeStack.Length > 0) text += "  Stack: " + safeStack + "\n";
                int size = Encoding.UTF8.GetByteCount(text);
                while (entries.Count > 0 && (entries.Count >= MaximumEntries || bytes + size > MaximumSnapshotBytes - HeaderReserveBytes))
                {
                    bytes -= entries[0].Bytes;
                    entries.RemoveAt(0);
                    IncrementDropped();
                }
                // Individual fields guarantee this fits; keep the bound explicit.
                if (size > MaximumSnapshotBytes - HeaderReserveBytes) { IncrementDropped(); return false; }
                entries.Add(new Entry { Kind = safeKind, Message = safeMessage, Stack = safeStack, Time = now, Bytes = size });
                bytes += size;
                return true;
            }
        }

        internal string Snapshot()
        {
            lock (gate)
            {
                StringBuilder result = new StringBuilder();
                result.Append("Feedback entries: ").Append(entries.Count).Append("; suppressed or evicted: ").Append(dropped).Append('\n');
                for (int i = 0; i < entries.Count; i++)
                {
                    Entry entry = entries[i];
                    result.Append("[+").Append(entry.Time.ToString(CultureInfo.InvariantCulture)).Append("ms] ")
                        .Append(entry.Kind).Append(": ").Append(entry.Message).Append('\n');
                    if (entry.Stack.Length > 0) result.Append("  Stack: ").Append(entry.Stack).Append('\n');
                }
                return result.ToString();
            }
        }

        private void IncrementDropped() { if (dropped < Int32.MaxValue) dropped++; }

        private static Func<long> CreateClock()
        {
            Stopwatch clock = Stopwatch.StartNew();
            return delegate { return clock.ElapsedMilliseconds; };
        }

        internal static string Sanitize(string value, int maximum)
        {
            if (maximum < 0) throw new ArgumentOutOfRangeException("maximum");
            maximum = Math.Min(maximum, MaximumStackLength);
            if (String.IsNullOrEmpty(value) || maximum == 0) return String.Empty;
            // A small look-ahead allows redaction to finish across the retained
            // boundary. No original input is stored in the buffer or dedup key.
            int length = Math.Min(value.Length, maximum + 256);
            StringBuilder clean = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                char item = value[i];
                if (Char.IsControl(item) || Char.GetUnicodeCategory(item) == UnicodeCategory.Format) clean.Append(' ');
                else if (Char.IsHighSurrogate(item))
                {
                    if (i + 1 < length && Char.IsLowSurrogate(value[i + 1])) { clean.Append(item); clean.Append(value[++i]); }
                    else clean.Append('\uFFFD');
                }
                else clean.Append(Char.IsLowSurrogate(item) ? '\uFFFD' : item);
            }
            string sanitized = UserFolder.Replace(clean.ToString(), "[user-folder]");
            sanitized = Ipv4.Replace(sanitized, RedactIpv4);
            if (sanitized.Length <= maximum && value.Length <= length) return sanitized;
            const string suffix = "...[truncated]";
            if (maximum <= suffix.Length) return suffix.Substring(0, maximum);
            int keep = Math.Min(sanitized.Length, maximum - suffix.Length);
            if (keep > 0 && Char.IsHighSurrogate(sanitized[keep - 1])) keep--;
            return sanitized.Substring(0, keep) + suffix;
        }

        private static string RedactIpv4(Match match)
        {
            string[] pieces = match.Value.Split(':')[0].Split('.');
            for (int i = 0; i < pieces.Length; i++)
            {
                int octet;
                if (!Int32.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out octet) || octet > 255)
                    return match.Value;
            }
            return "[ipv4]";
        }
    }
}
