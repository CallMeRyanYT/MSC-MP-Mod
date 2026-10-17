using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MSCMP
{
    // Local, bounded reports only. This component has no network or save-file access.
    internal sealed class FeedbackReporter
    {
        private readonly FeedbackBuffer buffer = new FeedbackBuffer();
        private readonly string directory;
        private readonly string run;
        private readonly string version;
        private readonly string assemblyHash;
        private readonly Func<string> context;
        private readonly Func<string> recentEvents;
        private bool dirty = true;
        private float nextWrite;
        private long revision;
        private string status = "Local feedback ready.";

        internal string Status { get { return status; } }
        internal void ContextChanged() { dirty = true; }

        internal FeedbackReporter(string folder, string runId, string modVersion, Func<string> currentContext, Func<string> events)
        {
            directory = folder; run = runId; version = modVersion;
            context = currentContext; recentEvents = events;
            assemblyHash = OwnAssemblyHash();
            try
            {
                Directory.CreateDirectory(directory);
                string latest = Path.Combine(directory, "feedback-latest.log");
                if (File.Exists(latest) && new FileInfo(latest).Length <= 262144)
                    File.Copy(latest, Path.Combine(directory, "feedback-previous.log"), true);
            }
            catch (Exception error) { status = "Feedback file unavailable: " + error.GetType().Name; }
        }

        internal void Capture(string kind, string message, string stack)
        {
            try
            {
                buffer.Add(kind, message, stack);
                dirty = true;
            }
            catch { status = "Feedback capture failed; the game can continue."; }
        }

        internal void ReportProblem(string note)
        {
            Capture("player-report", String.IsNullOrEmpty(note) ? "Player requested a problem report without a note." : note, String.Empty);
            Flush(true);
        }

        internal void Poll(float now)
        {
            if (!dirty || now < nextWrite) return;
            nextWrite = now + 1.0f;
            Flush(false);
        }

        internal void Flush(bool manual)
        {
            if (!dirty && !manual) return;
            try
            {
                Directory.CreateDirectory(directory);
                string utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                string current = SanitizeLines(context == null ? String.Empty : context(), 4096);
                string events = SanitizeLines(recentEvents == null ? String.Empty : recentEvents(), 65536);
                // Trace labels have their own input bounds. A report has one final
                // byte limit even if later diagnostics accidentally grow their output.
                string body = "MSCMP local feedback v1\r\nUTC=" + utc + "\r\nRun=" + run
                    + "\r\nVersion=" + version + "\r\nAssemblySHA256=" + assemblyHash
                    + "\r\nScope=diagnostics and visual avatars; shared gameplay unavailable\r\n"
                    + current + "\r\n\r\nReports:\r\n" + buffer.Snapshot()
                    + "\r\nRecent mod events:\r\n" + events;
                if (Encoding.UTF8.GetByteCount(body) > 262144)
                    throw new InvalidOperationException("Feedback report exceeds its byte limit.");
                AtomicWrite(Path.Combine(directory, "feedback-latest.log"), body);
                if (manual) AtomicWrite(Path.Combine(directory, "problem-latest.log"), body);
                revision++;
                string state = "{\"schema\":1,\"run\":\"" + run + "\",\"revision\":" + revision.ToString(CultureInfo.InvariantCulture)
                    + ",\"updatedUtc\":\"" + utc + "\",\"reportCount\":" + buffer.Count.ToString(CultureInfo.InvariantCulture)
                    + ",\"suppressedOrDropped\":" + buffer.DroppedCount.ToString(CultureInfo.InvariantCulture)
                    + ",\"manual\":" + (manual ? "true" : "false") + ",\"assemblySha256\":\"" + assemblyHash + "\"}";
                AtomicWrite(Path.Combine(directory, "feedback-status.json"), state);
                dirty = false;
                status = manual ? "Problem report saved locally. Tell Codex to check the feedback." : "Local error feedback is up to date.";
            }
            catch (Exception error)
            {
                // Do not emit Unity log errors here: the global error callback would
                // recursively report a failed report. Keep the bounded buffer for retry.
                status = "Feedback could not be saved: " + error.GetType().Name;
            }
        }

        private static void AtomicWrite(string path, string contents)
        {
            string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(staging, contents, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(staging, path, null);
                else File.Move(staging, path);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }

        private static string SanitizeLines(string value, int limit)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            if (value.Length > limit) value = value.Substring(0, limit);
            string[] lines = value.Split('\n');
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < lines.Length && i < 256; i++)
                result.AppendLine(FeedbackBuffer.Sanitize(lines[i], 1024));
            return result.ToString();
        }

        private static string OwnAssemblyHash()
        {
            try
            {
                using (SHA256 hash = SHA256.Create())
                using (FileStream file = File.OpenRead(typeof(FeedbackReporter).Assembly.Location))
                    return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", String.Empty);
            }
            catch { return "unavailable"; }
        }
    }
}
