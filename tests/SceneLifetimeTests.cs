using System;
using MSCMP;

internal static class SceneLifetimeTests
{
    private static int assertions;

    private static void Expect(bool condition, string message)
    {
        assertions++;
        if (!condition)
            throw new Exception(message);
    }

    private static int Main()
    {
        try
        {
            SceneLifetime life = new SceneLifetime();
            Expect(!life.End(0) && !life.MarkReady(0), "An absent scene cannot end or become ready.");
            int first = life.Begin("observed-game-scene");
            Expect(first > 0 && life.Starts == 1 && life.Cleanups == 0, "First scene starts once.");
            Expect(life.Begin("observed-game-scene") == first && life.Starts == 1, "Duplicate callback is idempotent.");
            Expect(life.MarkReady(first) && life.Ready, "Current callback marks scene ready.");
            Expect(!life.MarkReady(first), "Duplicate ready callback is idempotent.");
            Expect(life.End(first) && !life.Ready && life.Cleanups == 1, "Scene cleanup clears readiness once.");
            Expect(!life.End(first) && life.Cleanups == 1, "Disable then OnDestroy cannot clean twice.");
            int second = life.Begin("observed-game-scene");
            Expect(second != first && !life.Ready && life.Starts == 2, "Same-named scene reload gets a new generation.");
            Expect(!life.End(first) && life.Generation == second, "Delayed destruction cannot end replacement.");
            Expect(!life.MarkReady(first) && !life.Ready, "Stale readiness cannot activate replacement.");
            int menu = life.Begin("observed-menu-scene");
            Expect(menu != second && life.Cleanups == 2 && life.Starts == 3, "Scene replacement cleans previous scene.");
            Expect(!life.End(second) && life.Generation == menu, "Delayed prior scene cleanup is ignored.");
            Expect(life.End(menu) && life.Cleanups == life.Starts, "All started generations are cleaned exactly once.");
            int enabled = life.Begin("observed-game-scene");
            Expect(!life.Ready, "Enable after a previous OnLoad does not inherit readiness.");
            life.MarkReady(enabled);
            life.End(enabled);
            int reloaded = life.Begin("observed-game-scene");
            life.MarkReady(reloaded);
            Expect(!life.End(enabled) && life.Ready && life.Generation == reloaded, "Late same-scene OnDestroy cannot clear new OnLoad readiness.");
            life.End(reloaded);
            int normalized = life.Begin(null);
            Expect(life.Begin(String.Empty) == normalized, "Empty scene labels normalize consistently.");

            DiagnosticTrace trace = new DiagnosticTrace();
            for (int index = 0; index < DiagnosticTrace.Capacity + 10; index++)
                trace.Add("event", "sequence=" + index);
            string snapshot = trace.Snapshot();
            Expect(trace.Count == DiagnosticTrace.Capacity, "Trace memory remains bounded.");
            Expect(!snapshot.Contains("sequence=9\r\n") && snapshot.Contains("sequence=10"), "Trace evicts oldest entries.");
            Expect(snapshot.IndexOf("sequence=10\r\n", StringComparison.Ordinal) < snapshot.IndexOf("sequence=137", StringComparison.Ordinal), "Trace stays in chronological order after wrap.");
            string longEntry = trace.Add(new String('a', 1000), new String('b', 10000));
            Expect(longEntry.Length <= DiagnosticTrace.MaxEntryLength, "Trace entry is capped.");
            Expect(DiagnosticTrace.SafeText("a\r\nb<t>", 20) == "a__b_t_", "Labels cannot inject lines or rich text.");
            Expect(DiagnosticTrace.SafeText("abcdef", 3) == "abc", "Labels are truncated.");
            DiagnosticTrace evidence = new DiagnosticTrace();
            evidence.Add("scene.begin", "retained-begin", true);
            evidence.Add("scene.end", "retained-end", true);
            for (int index = 0; index < 1000; index++)
                evidence.Add("inspect.state", "state=" + index);
            Expect(!evidence.Snapshot().Contains("retained-begin"), "Recent inspector trace may evict old general events.");
            Expect(evidence.LifecycleSnapshot().Contains("retained-begin") && evidence.LifecycleSnapshot().Contains("retained-end"), "Repeated inspection cannot erase lifecycle evidence.");
            for (int index = 0; index < 100; index++)
                evidence.Add("scene.end", "generation=" + index, true);
            Expect(evidence.LifecycleCount == DiagnosticTrace.LifecycleCapacity, "Lifecycle evidence remains bounded.");
            Expect(!evidence.LifecycleSnapshot().Contains("retained-begin") && evidence.LifecycleSnapshot().Contains("generation=99"), "Lifecycle evidence retains newest generations when its own bound is exceeded.");
            Console.WriteLine("PASS: " + assertions + " lifecycle and bounded-trace assertions. Unity integration remains awaiting manual test.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error.Message);
            return 1;
        }
    }
}
