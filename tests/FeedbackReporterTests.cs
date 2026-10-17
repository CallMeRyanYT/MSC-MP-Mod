using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MSCMP;

internal static class FeedbackReporterTests
{
    private static int assertions;
    private static string fixtureRoot;

    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }

    private static string Folder(string name)
    {
        string path = Path.Combine(fixtureRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Read(string folder, string name)
    { return File.ReadAllText(Path.Combine(folder, name), new UTF8Encoding(false, true)); }

    private static void NoStagingFiles(string folder)
    { Check(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories).Length == 0, "Completed or failed writes leave no staging files"); }

    private static void CapturedAndManualReports()
    {
        string folder = Folder("reports"), current = @"scene=GAME source=C:\Users\ContextPerson\scene at 10.1.2.3:20577" + "\nstate=Listening";
        FeedbackReporter reporter = new FeedbackReporter(folder, "run-one", "test-version", delegate { return current; },
            delegate { return @"network.Probe from 192.168.6.7:30000 C:\Users\TracePerson\logs\mod.log" + "\nscene.begin generation=1"; });
        Check(!File.Exists(Path.Combine(folder, "feedback-latest.log")), "Construction does not invent a completed report");
        try { throw new InvalidOperationException(@"Failed binding C:\Users\ErrorPerson\mod\cup at 172.16.0.8:4321"); }
        catch (Exception error) { reporter.Capture("binding", error.GetType().FullName + ": " + error.Message,
            error.StackTrace + @" in C:\Users\StackPerson\code\Binding.cs:line 42"); }
        reporter.Poll(0);
        string first = Read(folder, "feedback-latest.log"), state = Read(folder, "feedback-status.json");
        Check(first.Contains("Run=run-one") && first.Contains("Version=test-version"), "Report identifies the current run and version");
        Check(first.Contains("InvalidOperationException") && first.Contains("CapturedAndManualReports") && first.Contains("line 42"), "Captured exception preserves type and useful stack evidence");
        Check(!first.Contains("ContextPerson") && !first.Contains("TracePerson") && !first.Contains("ErrorPerson") && !first.Contains("StackPerson"), "Profile names are redacted in every report section");
        Check(!first.Contains("10.1.2.3") && !first.Contains("192.168.6.7") && !first.Contains("172.16.0.8"), "Context, trace and exception endpoints are redacted");
        Check(first.Contains("state=Listening") && first.Contains("\nscene.begin generation=1"), "Safe multiline context and recent event structure survive redaction");
        Check(Regex.IsMatch(first, "AssemblySHA256=[A-F0-9]{64}\\r?\\n"), "Report hashes the actual loaded assembly");
        Check(state.Contains("\"revision\":1") && state.Contains("\"reportCount\":1") && state.Contains("\"manual\":false"), "Automatic status describes the committed report");
        Check(!File.Exists(Path.Combine(folder, "problem-latest.log")), "Automatic capture does not impersonate a manual problem report");
        byte[] bytes = File.ReadAllBytes(Path.Combine(folder, "feedback-latest.log"));
        Check(bytes.Length < 262144 && bytes[0] == (byte)'M', "Report is bounded UTF8 without a BOM");
        NoStagingFiles(folder);

        reporter.Capture("second", "another error", "at MSCMP.Loop.Tick()");
        reporter.Poll(0.5f);
        Check(Read(folder, "feedback-latest.log") == first, "Automatic reports respect the one-second write interval");
        reporter.Poll(1);
        Check(Read(folder, "feedback-latest.log").Contains("another error") && Read(folder, "feedback-status.json").Contains("\"revision\":2"), "Pending evidence is committed at the next write interval");
        reporter.ReportProblem(@"Cup stopped near 10.20.30.40; note C:\Users\NotePerson\recording");
        string manual = Read(folder, "problem-latest.log");
        Check(manual == Read(folder, "feedback-latest.log") && manual.Contains("player-report: Cup stopped"), "Manual report contains the note and same current diagnostics");
        Check(!manual.Contains("NotePerson") && !manual.Contains("10.20.30.40"), "Manual notes use the same privacy filtering");
        Check(Read(folder, "feedback-status.json").Contains("\"manual\":true") && Read(folder, "feedback-status.json").Contains("\"revision\":3"), "Manual publication advances the status revision");
        Check(reporter.Status.Contains("saved locally"), "Successful manual save is reported truthfully");

        current = "scene=MainMenu generation=0 cleanups=1";
        reporter.ContextChanged();
        reporter.Flush(false);
        Check(Read(folder, "feedback-latest.log").Contains("cleanups=1") && Read(folder, "feedback-status.json").Contains("\"revision\":4"), "Explicit teardown context can publish without a new error");
        string cleanState = Read(folder, "feedback-status.json");
        reporter.Flush(false);
        Check(Read(folder, "feedback-status.json") == cleanState, "An unchanged report is not rewritten");
        NoStagingFiles(folder);
    }

    private static void ProviderFailureAndRetry()
    {
        string folder = Folder("provider-retry");
        bool failContext = false, failEvents = false;
        FeedbackReporter reporter = new FeedbackReporter(folder, "retry-run", "test", delegate
        {
            if (failContext) throw new InvalidOperationException("private context failure");
            return "scene=GAME";
        }, delegate
        {
            if (failEvents) throw new IOException("private trace failure");
            return "scene.ready";
        });
        reporter.Capture("initial", "before failure", String.Empty);
        reporter.Flush(false);
        string before = Read(folder, "feedback-latest.log"), stateBefore = Read(folder, "feedback-status.json");
        failContext = true;
        reporter.Capture("pending", "must survive failed context", "at MSCMP.Observer.Update()");
        reporter.Flush(false);
        Check(reporter.Status.Contains("could not be saved: InvalidOperationException"), "Provider failure is contained without logging recursively");
        Check(!reporter.Status.Contains("private context failure"), "Failure status omits provider exception message");
        Check(Read(folder, "feedback-latest.log") == before && Read(folder, "feedback-status.json") == stateBefore, "Failed context leaves the previously committed files intact");
        NoStagingFiles(folder);
        failContext = false;
        failEvents = true;
        reporter.Flush(false);
        Check(Read(folder, "feedback-latest.log") == before && reporter.Status.Contains("IOException"), "Failure of the recent-event provider also preserves the last report");
        failEvents = false;
        reporter.Flush(false);
        Check(Read(folder, "feedback-latest.log").Contains("must survive failed context") && Read(folder, "feedback-status.json").Contains("\"reportCount\":2"), "Pending captured evidence survives both provider failures and retries");
        Check(Read(folder, "feedback-status.json").Contains("\"revision\":2"), "Failures before publication do not consume a revision");
        NoStagingFiles(folder);
    }

    private static void AtomicReplacementAndBackup()
    {
        string folder = Folder("atomic");
        FeedbackReporter first = new FeedbackReporter(folder, "old-run", "test", null, null);
        first.Capture("initial", "old report retained", String.Empty);
        first.Flush(false);
        string original = Read(folder, "feedback-latest.log"), oldState = Read(folder, "feedback-status.json");
        first.Capture("after-lock", "must survive a locked destination", String.Empty);
        using (FileStream held = new FileStream(Path.Combine(folder, "feedback-latest.log"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            first.Flush(false);
            Check(first.Status.Contains("could not be saved"), "An exclusively locked destination fails without escaping into gameplay");
            NoStagingFiles(folder);
        }
        Check(Read(folder, "feedback-latest.log") == original && Read(folder, "feedback-status.json") == oldState, "Failed replacement preserves the complete previous report and status");
        first.Flush(false);
        string replaced = Read(folder, "feedback-latest.log");
        Check(replaced.Contains("must survive a locked destination") && replaced.EndsWith("Recent mod events:\r\n", StringComparison.Ordinal), "Retry replaces the report completely after the file becomes available");
        NoStagingFiles(folder);

        FeedbackReporter second = new FeedbackReporter(folder, "new-run", "test", null, null);
        Check(Read(folder, "feedback-previous.log") == replaced, "A fresh mod instance preserves its predecessor's complete latest report");
        second.Poll(0);
        Check(Read(folder, "feedback-latest.log").Contains("Run=new-run") && !Read(folder, "feedback-latest.log").Contains("must survive a locked destination"), "The new run does not inherit old in-memory error ownership");
        Check(Read(folder, "feedback-previous.log") == replaced, "Publishing a new run leaves the previous-run evidence intact");
        Check(Read(folder, "feedback-status.json").Contains("\"revision\":1"), "A fresh run starts its own revision sequence");
        second.ReportProblem(null);
        Check(Read(folder, "problem-latest.log").Contains("without a note"), "An empty manual note produces an explicit user report");
        NoStagingFiles(folder);
    }

    private static void BoundsAndUnavailableDirectory()
    {
        string folder = Folder("bounds");
        StringBuilder manyEvents = new StringBuilder();
        for (int i = 0; i < 300; i++) manyEvents.Append(new string('\u6F22', 300)).Append('\n');
        manyEvents.Append("unprocessed-private-tail");
        FeedbackReporter reporter = new FeedbackReporter(folder, "bounded-run", "test", delegate { return new string('c', 100000) + "unprocessed-private-tail"; }, delegate { return manyEvents.ToString(); });
        for (int i = 0; i < 100; i++) reporter.Capture("error-" + i, new string('\u6F22', 2048), new string('\u6F22', 4096));
        reporter.ReportProblem("newest manual evidence");
        string text = Read(folder, "feedback-latest.log");
        Check(text.Contains("newest manual evidence") && !text.Contains("unprocessed-private-tail"), "Large context, trace and error inputs retain recent evidence without processing their tails");
        Check(text.Contains("suppressed or evicted:"), "Eviction remains visible in the published report");
        string[] files = Directory.GetFiles(folder);
        Check(files.Length == 3, "Reporting creates only the bounded latest, manual and status files");
        foreach (string file in files)
            Check(new FileInfo(file).Length <= 262144, "Every published feedback file obeys the byte ceiling");
        NoStagingFiles(folder);

        string unavailable = Path.Combine(fixtureRoot, "blocked-folder");
        File.WriteAllText(unavailable, "test-owned blocker");
        FeedbackReporter blocked = new FeedbackReporter(unavailable, "blocked-run", "test", null, null);
        Check(blocked.Status.Contains("unavailable"), "Unavailable output directory is handled during construction");
        blocked.ReportProblem("retained while directory is unavailable");
        Check(blocked.Status.Contains("could not be saved"), "Manual reporting never claims success while the directory is unavailable");
        File.Delete(unavailable);
        blocked.Flush(false);
        Check(Read(unavailable, "feedback-latest.log").Contains("retained while directory is unavailable"), "Captured note survives directory recovery");
        NoStagingFiles(unavailable);
    }

    private static int Main()
    {
        string testRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local\\tests"));
        fixtureRoot = Path.GetFullPath(Path.Combine(testRoot, "feedback-reporter-" + Guid.NewGuid().ToString("N")));
        try
        {
            if (!fixtureRoot.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test fixture escaped its workspace test directory.");
            Directory.CreateDirectory(fixtureRoot);
            CapturedAndManualReports(); ProviderFailureAndRetry(); AtomicReplacementAndBackup(); BoundsAndUnavailableDirectory();
            Console.WriteLine("PASS: " + assertions + " local feedback reporter assertions.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            // This unique directory was created only by this test; never touch saves,
            // the installed mod, or a broader computed parent when cleaning up.
            if (fixtureRoot.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, true);
        }
    }
}
