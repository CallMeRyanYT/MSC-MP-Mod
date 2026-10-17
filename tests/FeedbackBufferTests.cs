using System;
using System.Text;
using System.Threading;
using MSCMP;

internal static class FeedbackBufferTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }

    private static void Deduplication()
    {
        long now = 0;
        FeedbackBuffer buffer = new FeedbackBuffer(delegate { return now; });
        Check(buffer.Count == 0 && buffer.DroppedCount == 0, "Empty buffer is ready for first error");
        Check(buffer.Add("Exception", "Unable to bind", "at Pickup.Start():42"), "First exception retained");
        Check(!buffer.Add("Exception", "Unable to bind", "at Pickup.Start():42"), "Identical frame spam suppressed");
        now = 9999;
        Check(!buffer.Add("Exception", "Unable to bind", "at Pickup.Start():42"), "Suppression lasts for the full rate window");
        now = 10000;
        Check(buffer.Add("Exception", "Unable to bind", "at Pickup.Start():42"), "Persistent error is reported again after the rate window");
        Check(buffer.Count == 2 && buffer.DroppedCount == 2, "Suppression is counted without growing retained entries");
        Check(buffer.Add("Error", "Unable to bind", "at Pickup.Start():42"), "Different severity is distinct");
        Check(buffer.Add("Exception", "Unable to bind", "at Pickup.Update():91"), "Same message with different stack remains visible");
        Check(buffer.Add("Exception", "Unable to bind second item", "at Pickup.Start():42"), "Different message remains visible");
        now = 0;
        Check(!buffer.Add("Exception", "Unable to bind", "at Pickup.Start():42"), "Clock regression cannot bypass spam suppression");
        Check(buffer.Snapshot().Contains("suppressed or evicted: 3"), "Snapshot reports omitted evidence count");
    }

    private static void Privacy()
    {
        FeedbackBuffer buffer = new FeedbackBuffer(delegate { return 0; });
        buffer.Add("Exception", @"InvalidOperationException from C:\Users\Alice Private\Desktop\plugin.cs at 192.168.1.77:20577",
            @"at Pickup.Update() in D:\Documents and Settings\Bob Smith\code\Mod.cs:line 42");
        string text = buffer.Snapshot();
        Check(!text.Contains("Alice Private") && !text.Contains("Bob Smith"), "Windows profile names are redacted");
        Check(!text.Contains("192.168.1.77") && !text.Contains("20577"), "IPv4 endpoint and port are redacted");
        Check(text.Contains("InvalidOperationException") && text.Contains("Pickup.Update()") && text.Contains("line 42"), "Exception type and useful stack location survive redaction");
        Check(text.Contains(@"[user-folder]\Desktop\plugin.cs") && text.Contains(@"[user-folder]\code\Mod.cs"), "Relative source locations remain useful");
        buffer.Add("Error", @"\\?\C:\Users\Delta\cache\file; c:/users/Echo/code/file", @"\\FILESERVER\Users\Charlie\Desktop\Tools.cs:line 4");
        text = buffer.Snapshot();
        Check(!text.Contains("Delta") && !text.Contains("Echo") && !text.Contains("Charlie") && !text.Contains("FILESERVER"), "Extended, slash and UNC user paths are redacted");
        buffer.Add("Error", "127.0.0.1 10.1.2.3:12345 invalid 999.999.999.999", "at MSCMP.Network.Session.Tick():100");
        text = buffer.Snapshot();
        Check(!text.Contains("127.0.0.1") && !text.Contains("10.1.2.3") && text.Contains("999.999.999.999"), "Valid addresses are redacted without treating invalid numeric text as an endpoint");
        Check(text.Contains("MSCMP.Network.Session.Tick():100"), "Qualified type and source line preserved");
        FeedbackBuffer controls = new FeedbackBuffer(delegate { return 0; });
        controls.Add("Err\u0000or", "first\r\nforged\u001b[31m\u202Ehidden", "stack\tline\nsecond\u0007");
        text = controls.Snapshot();
        bool safe = !text.Contains("\u202E");
        for (int i = 0; i < text.Length; i++) safe &= !Char.IsControl(text[i]) || text[i] == '\n';
        Check(safe, "Input control and bidirectional formatting characters cannot survive");
        int breaks = 0; for (int i = 0; i < text.Length; i++) if (text[i] == '\n') breaks++;
        Check(breaks == 3, "Only buffer-generated header, message and stack line breaks remain");
        FeedbackBuffer sameSanitized = new FeedbackBuffer(delegate { return 0; });
        Check(sameSanitized.Add("Error", @"C:\Users\FirstUser\code\Foo.cs", "same")
            && !sameSanitized.Add("Error", @"C:\Users\SecondUser\code\Foo.cs", "same"), "Dedup retains only sanitized fields, never raw private identities");
        Check(FeedbackBuffer.Sanitize(@"probe from 10.0.0.2:20577 C:\Users\Private\file", 320) == @"probe from [ipv4] [user-folder]\file",
            "Shared sanitizer also protects context and diagnostic sections");
        Check(FeedbackBuffer.Sanitize("long text", 0) == String.Empty && FeedbackBuffer.Sanitize("long text", 2).Length == 2,
            "Shared sanitizer handles zero and very small output bounds");
        Check(FeedbackBuffer.Sanitize(new string('x', 100000), Int32.MaxValue).Length == FeedbackBuffer.MaximumStackLength,
            "Caller cannot make shared sanitizer processing or output unbounded");
    }

    private static void Bounds()
    {
        FeedbackBuffer countBound = new FeedbackBuffer(delegate { return 0; });
        for (int i = 0; i < 40; i++) countBound.Add("Error", "entry-" + i + "-end", "stack");
        string text = countBound.Snapshot();
        Check(countBound.Count == 32 && countBound.DroppedCount == 8, "Entry cap evicts oldest evidence with an exact omitted count");
        Check(!text.Contains("entry-7-end") && text.Contains("entry-8-end") && text.Contains("entry-39-end"), "Newest errors are retained in the bounded window");
        Check(text.IndexOf("entry-8-end", StringComparison.Ordinal) < text.IndexOf("entry-39-end", StringComparison.Ordinal), "Snapshot remains chronological after eviction");

        FeedbackBuffer byteBound = new FeedbackBuffer(delegate { return 0; });
        string message = new string('\u6F22', 2048), stack = new string('\u6F22', 4096);
        bool bounded = true;
        for (int i = 0; i < 30; i++)
        {
            byteBound.Add("Error-" + i + "-end", message, stack);
            bounded &= Encoding.UTF8.GetByteCount(byteBound.Snapshot()) <= FeedbackBuffer.MaximumSnapshotBytes;
        }
        Check(bounded && byteBound.Count < 32 && byteBound.DroppedCount > 0, "UTF8 byte cap is enforced before the entry cap for multibyte logs");
        Check(byteBound.Snapshot().Contains("Error-29-end"), "Byte pressure still retains the newest report");
        FeedbackBuffer clipped = new FeedbackBuffer(delegate { return 0; });
        clipped.Add("Exception", new string('m', 100000) + "private-tail", new string('s', 100000) + "private-tail");
        text = clipped.Snapshot();
        string[] lines = text.Split('\n');
        string retainedMessage = lines[1].Substring(lines[1].IndexOf(": ", StringComparison.Ordinal) + 2);
        string retainedStack = lines[2].Substring("  Stack: ".Length);
        Check(retainedMessage.Length == FeedbackBuffer.MaximumMessageLength && retainedStack.Length == FeedbackBuffer.MaximumStackLength, "Message and stack have independent exact length limits");
        Check(retainedMessage.EndsWith("...[truncated]", StringComparison.Ordinal) && retainedStack.EndsWith("...[truncated]", StringComparison.Ordinal), "Truncation is explicit to the report reader");
        Check(!text.Contains("private-tail"), "Unprocessed oversized tail is never retained");
        FeedbackBuffer malformed = new FeedbackBuffer(delegate { return Int64.MaxValue; });
        malformed.Add(null, null, null);
        malformed.Add("Error", "unpaired\uD800surrogate\uDC00", new string('x', 4095) + "\uD83D\uDE00");
        bool validUnicode = true;
        try { new UTF8Encoding(false, true).GetBytes(malformed.Snapshot()); }
        catch (EncoderFallbackException) { validUnicode = false; }
        Check(validUnicode && malformed.Count == 2, "Null input, extreme timestamp and malformed/truncated surrogate input are handled");
        Check(Encoding.UTF8.GetByteCount(malformed.Snapshot()) <= FeedbackBuffer.MaximumSnapshotBytes, "Header and timestamp count toward the byte bound");
    }

    private static void Concurrency()
    {
        FeedbackBuffer buffer = new FeedbackBuffer(delegate { return 0; });
        Thread[] workers = new Thread[4];
        Exception failure = null;
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = new Thread(delegate()
            {
                try
                {
                    for (int attempt = 0; attempt < 250; attempt++)
                    {
                        buffer.Add("Exception", "same frame error", "at Loop.Update():12");
                        if (Encoding.UTF8.GetByteCount(buffer.Snapshot()) > FeedbackBuffer.MaximumSnapshotBytes)
                            throw new Exception("Concurrent snapshot exceeded its bound");
                    }
                }
                catch (Exception error) { Interlocked.CompareExchange<Exception>(ref failure, error, null); }
            });
            workers[i].Start();
        }
        for (int i = 0; i < workers.Length; i++) workers[i].Join();
        Check(failure == null && buffer.Count == 1 && buffer.DroppedCount == 999, "Concurrent callbacks and snapshots retain one duplicate with an exact omitted count");
    }

    private static int Main()
    {
        try
        {
            Deduplication(); Privacy(); Bounds(); Concurrency();
            Console.WriteLine("PASS: " + assertions + " bounded feedback assertions.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
