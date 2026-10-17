using System;
using System.Globalization;
using System.IO;
using System.Text;
using MSCLoader;
using UnityEngine;

namespace MSCMP
{
    public sealed class DiscoveryMod : Mod
    {
        private readonly SceneLifetime lifetime = new SceneLifetime();
        private readonly DiagnosticTrace trace = new DiagnosticTrace();
        private DiscoveryObserver observer;
        private NetworkPanel networkPanel;
        private FeedbackReporter feedback;
        private SettingsKeybind bindingsKey;
        private SettingsKeybind feedbackKey;
        private SettingsKeybind overlayKey;
        private SettingsKeybind inspectKey;
        private SettingsKeybind exportKey;
        private bool initialized;
        private bool stopped;
        private bool overlayVisible = true;
        private bool fileFailureReported;
        private string diagnosticDirectory;
        private string diagnosticRun;
        private string overlayText = "Discovery is starting.";
        private string inspectionSummary = "No target inspected.";
        private string exportSummary = "No manual snapshot exported.";
        private float nextOverlayRefresh;
        private float nextInspection;
        private float nextExport;

        public override string ID { get { return "MSCMP"; } }
        public override string Name { get { return "MSC-MP experimental multiplayer"; } }
        public override string Author { get { return "MSC-MP contributors"; } }
        public override string Version { get { return "0.5.0"; } }
        public override string Description
        {
            get { return "Experimental LAN/tailnet multiplayer: characters, 22 shared items and Hayosiko driver/body. Disposable profile required. AI-assisted implementation."; }
        }
        public override Game SupportedGames { get { return Game.MySummerCar; } }

        // The loader requires this method to contain registrations only.
        public override void ModSetup()
        {
            SetupFunction(Setup.ModSettings, ConfigureSettings);
            SetupFunction(Setup.OnMenuLoad, OnMenuLoad);
            SetupFunction(Setup.OnNewGame, OnNewGame);
            SetupFunction(Setup.PreLoad, OnPreLoad);
            SetupFunction(Setup.OnLoad, OnGameLoad);
            SetupFunction(Setup.PostLoad, OnPostLoad);
            SetupFunction(Setup.OnSave, OnGameSaved);
            SetupFunction(Setup.OnModEnabled, OnEnabled);
            SetupFunction(Setup.OnModDisabled, OnDisabled);
            SetupFunction(Setup.Update, PollScene);
        }

        private void ConfigureSettings()
        {
            Keybind.AddHeader("MSC-MP discovery", false);
            bindingsKey = Keybind.Add("mscmp.bindings", "Check player and cup bindings (read only)", KeyCode.F6);
            feedbackKey = Keybind.Add("mscmp.feedback", "Report a mod problem", KeyCode.F7);
            overlayKey = Keybind.Add("mscmp.overlay", "Toggle discovery overlay", KeyCode.F8);
            inspectKey = Keybind.Add("mscmp.inspect", "Inspect aimed object (read only)", KeyCode.F9);
            exportKey = Keybind.Add("mscmp.export", "Export diagnostic snapshot", KeyCode.F10);
            Settings.AddText("Experimental 22-item and Hayosiko sync. Other gameplay is not synchronized. Use a disposable test profile.", false);
        }

        private void InitializeDiagnostics()
        {
            if (initialized)
                return;
            initialized = true;
            networkPanel = new NetworkPanel(RecordNetwork, ReportProblem);
            diagnosticRun = Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                diagnosticDirectory = Path.Combine(ModLoader.GetModSettingsFolder(this), "diagnostics");
                Directory.CreateDirectory(diagnosticDirectory);
                string latest = Path.Combine(diagnosticDirectory, "latest.log");
                // Loader return-to-menu can create a fresh Mod instance. Retain its predecessor's
                // final cleanup evidence before this instance starts writing latest.log.
                if (File.Exists(latest) && new FileInfo(latest).Length <= 262144)
                    File.Copy(latest, Path.Combine(diagnosticDirectory, "previous.log"), true);
            }
            catch (Exception error)
            {
                ReportFileFailure(error);
            }
            if (!String.IsNullOrEmpty(diagnosticDirectory))
                feedback = new FeedbackReporter(diagnosticDirectory, diagnosticRun, Version,
                    FeedbackContext, delegate { return trace.Snapshot(); });
            Record("mod.initialized", "run=" + diagnosticRun + " mod=" + Version + " loader=" + ModLoader.MSCLoader_Ver
                + " unity=" + Application.unityVersion + " pointerBits=" + (IntPtr.Size * 8), true);
            Record("coverage", "experimental; shared=22-authored-items,Hayosiko-driver-body; runtime-validation=pending; disposable-profile-required", true);
        }

        private void OnMenuLoad() { ObserveCallback("loader.menu", false); }
        private void OnNewGame() { ObserveCallback("loader.new-game", false); }
        private void OnPreLoad()
        {
            // A documented phase-1 callback after a ready generation starts a new load,
            // even if the scene has the same name and its old OnDestroy arrives late.
            if (lifetime.Ready)
                ReleaseCurrent("new-load-phase");
            ObserveCallback("loader.pre-load", false);
        }
        private void OnGameLoad() { ObserveCallback("loader.on-load", true); }
        private void OnPostLoad() { ObserveCallback("loader.post-load", false); }
        private void OnEnabled()
        {
            stopped = false;
            ObserveCallback("loader.enabled", false);
        }

        private void ObserveCallback(string eventName, bool loaderReady)
        {
            try
            {
                InitializeDiagnostics();
                if (isDisabled || stopped)
                    return;
                EnsureObserver();
                Record(eventName, SceneDescription(), true);
                if (loaderReady && observer != null && lifetime.MarkReady(observer.Generation))
                    Record("scene.loader-ready", "generation=" + lifetime.Generation + "; this is not gameplay-binding verification", true);
                RefreshOverlay();
                WriteSnapshot("latest.log");
            }
            catch (Exception error)
            {
                HandleRuntimeFailure(eventName, error);
            }
        }

        private void OnGameSaved()
        {
            if (!initialized || isDisabled)
                return;
            if (networkPanel != null) networkPanel.SaveShared(true);
            Record("loader.after-save", "Observed only; this callback cannot prevent or isolate game saves.", true);
            WriteSnapshot("latest.log");
        }

        private void OnDisabled()
        {
            stopped = true;
            if (!initialized)
                return;
            Record("loader.disabled", "Diagnostic observer is being released.", true);
            ReleaseCurrent("mod-disabled");
            WriteSnapshot("latest.log");
        }

        private void EnsureObserver()
        {
            string actualScene = Application.loadedLevelName;
            if (observer != null && lifetime.SceneName == actualScene)
                return;
            ReleaseCurrent("scene-replaced");
            int token = lifetime.Begin(actualScene);
            GameObject owned = null;
            try
            {
                owned = new GameObject("MSCMP.Diagnostics");
                // Intentionally scene-owned: never call DontDestroyOnLoad here.
                observer = owned.AddComponent<DiscoveryObserver>();
                observer.Initialize(this, token);
                Record("scene.begin", SceneDescription(), true);
            }
            catch
            {
                if (owned != null)
                    UnityEngine.Object.Destroy(owned);
                lifetime.End(token);
                throw;
            }
        }

        private void ReleaseCurrent(string reason)
        {
            DiscoveryObserver previous = observer;
            observer = null;
            int token = lifetime.Generation;
            if (previous != null)
            {
                previous.Detach();
                UnityEngine.Object.Destroy(previous.gameObject);
            }
            EndGeneration(token, reason);
        }

        private void EndGeneration(int token, string reason)
        {
            if (!lifetime.End(token))
                return;
            if (networkPanel != null) networkPanel.Dispose();
            inspectionSummary = "No target inspected.";
            Record("scene.end", "generation=" + token + " reason=" + reason
                + " starts=" + lifetime.Starts + " cleanups=" + lifetime.Cleanups, true);
            WriteSnapshot("latest.log");
            if (feedback != null) { feedback.ContextChanged(); feedback.Flush(false); }
        }

        internal void ObserverDestroyed(int token, string reason)
        {
            if (token != lifetime.Generation)
                return;
            if (reason == "application-quit")
                stopped = true;
            observer = null;
            EndGeneration(token, reason);
        }

        private void PollScene()
        {
            if (networkPanel != null && initialized && !stopped && !isDisabled)
            {
                networkPanel.SetSceneContext(lifetime.Generation, lifetime.Ready && ModLoader.CurrentScene == CurrentScene.Game);
                networkPanel.Poll(overlayVisible);
            }
            // Loader Update is independent of our scene-owned object. A direct GAME reload
            // may not replay initialization. Do not recover in MainMenu: the loader resets
            // there and constructs a new Mod instance, which must own its own observer.
            if (!initialized || stopped || isDisabled || observer != null || Application.isLoadingLevel
                || ModLoader.CurrentScene != CurrentScene.Game)
                return;
            try
            {
                EnsureObserver();
                Record("scene.observer-recovered", SceneDescription() + "; awaiting OnLoad", true);
                RefreshOverlay();
                WriteSnapshot("latest.log");
            }
            catch (Exception error)
            {
                HandleRuntimeFailure("loader.update", error);
            }
        }

        internal void ObserveFocus(int token, bool focus)
        {
            if (token == lifetime.Generation)
                Record("application.focus", "focused=" + focus, true);
        }

        internal void ObservePause(int token, bool paused)
        {
            if (token == lifetime.Generation)
                Record("application.pause", "paused=" + paused, true);
        }

        internal void Tick(int token)
        {
            if (token != lifetime.Generation)
                return;
            try
            {
                if (isDisabled)
                {
                    stopped = true;
                    ReleaseCurrent("disabled-observed");
                    return;
                }
                if (lifetime.SceneName != Application.loadedLevelName)
                {
                    ReleaseCurrent("scene-change-observed");
                    return;
                }
                if (overlayKey != null && overlayKey.GetKeybindDown())
                    overlayVisible = !overlayVisible;
                float now = Time.realtimeSinceStartup;
                if (feedback != null) feedback.Poll(now);
                if (bindingsKey != null && bindingsKey.GetKeybindDown() && now >= nextInspection)
                {
                    nextInspection = now + 2.0f;
                    if (networkPanel != null) networkPanel.CheckBindings();
                }
                if (feedbackKey != null && feedbackKey.GetKeybindDown() && now >= nextExport)
                {
                    nextExport = now + 2.0f;
                    ReportProblem("Player pressed the problem-report key.");
                }
                if (inspectKey != null && inspectKey.GetKeybindDown() && now >= nextInspection)
                {
                    nextInspection = now + 2.0f;
                    InspectTarget();
                }
                if (exportKey != null && exportKey.GetKeybindDown() && now >= nextExport)
                {
                    nextExport = now + 2.0f;
                    Record("snapshot.requested", SceneDescription(), true);
                    exportSummary = WriteSnapshot("snapshot.log")
                        ? "Saved diagnostics/snapshot.log in this mod's settings folder."
                        : "Snapshot unavailable; see diagnostic.file-error.";
                }
                if (now >= nextOverlayRefresh)
                {
                    nextOverlayRefresh = now + 0.5f;
                    RefreshOverlay();
                }
            }
            catch (Exception error)
            {
                HandleRuntimeFailure("observer.update", error);
            }
        }

        private void InspectTarget()
        {
            if (!lifetime.Ready || ModLoader.CurrentScene != CurrentScene.Game)
            {
                inspectionSummary = "Inspection waits for the game's loader OnLoad callback.";
                Record("inspect.unavailable", "Game scene with observed OnLoad required.", true);
                return;
            }
            try
            {
                inspectionSummary = TargetInspector.Capture(trace);
                Record("inspect.complete", inspectionSummary, true);
                WriteSnapshot("latest.log");
            }
            catch (Exception error)
            {
                inspectionSummary = "Target inspection failed: " + error.GetType().Name;
                CaptureFailure("inspect", error);
                Record("inspect.failed", error.GetType().Name, true);
            }
        }

        private string SceneDescription()
        {
            return "scene=" + DiagnosticTrace.SafeText(Application.loadedLevelName, 64)
                + " index=" + Application.loadedLevel + " loaderScene=" + ModLoader.CurrentScene
                + " generation=" + lifetime.Generation;
        }

        private void RefreshOverlay()
        {
            overlayText = "MSC-MP " + Version + " | Experimental\n"
                + "22 shared items + Hayosiko | Full-game sync unfinished\n"
                + "Scene: " + DiagnosticTrace.SafeText(Application.loadedLevelName, 64)
                + " | Generation " + lifetime.Generation + "\n"
                + "Loader OnLoad observed: " + lifetime.Ready + " | Gameplay bindings unverified\n"
                + "Observers started: " + lifetime.Starts + " | Cleaned: " + lifetime.Cleanups + "\n"
                + "Time scale: " + Time.timeScale.ToString("0.00", CultureInfo.InvariantCulture)
                + " | Run in background: " + Application.runInBackground + " (observed only)\n"
                + KeyLabel(bindingsKey, "F6") + " bindings | " + KeyLabel(feedbackKey, "F7") + " report | "
                + KeyLabel(overlayKey, "F8") + " overlay | " + KeyLabel(inspectKey, "F9") + " inspect | "
                + KeyLabel(exportKey, "F10") + " export\n"
                + inspectionSummary + "\n" + exportSummary + "\n" + (feedback == null ? "Feedback files unavailable." : feedback.Status);
        }

        private static string KeyLabel(SettingsKeybind binding, string fallback)
        {
            return binding == null ? fallback : DiagnosticTrace.SafeText(binding.GetKeybindValue, 32);
        }

        internal void DrawOverlay(int token)
        {
            if (!overlayVisible || token != lifetime.Generation || isDisabled)
                return;
            try
            {
                GUI.Box(new Rect(12, 12, Math.Min(690, Screen.width - 24), 222), String.Empty);
                GUI.Label(new Rect(24, 22, Math.Min(666, Screen.width - 48), 204), overlayText);
                if (networkPanel != null) networkPanel.Draw();
            }
            catch (Exception error)
            {
                overlayVisible = false;
                CaptureFailure("overlay", error);
                Record("diagnostic.overlay-error", error.GetType().Name + "; overlay hidden", true);
            }
        }

        private void Record(string eventName, string detail, bool console)
        {
            bool lifecycle = eventName.StartsWith("mod.", StringComparison.Ordinal)
                || eventName.StartsWith("loader.", StringComparison.Ordinal)
                || eventName.StartsWith("scene.", StringComparison.Ordinal)
                || eventName.StartsWith("diagnostic.", StringComparison.Ordinal)
                || eventName == "coverage";
            string line = trace.Add(eventName, detail, lifecycle);
            if (!console)
                return;
            try { ModConsole.Log("[MSCMP] " + line); }
            catch { /* The bounded in-memory trace remains available if loader console is shutting down. */ }
        }

        private void RecordNetwork(string eventName, string detail)
        {
            // High-frequency pose samples have bounded transport counters; do not rewrite a log
            // for every movement packet. Semantic connection/failure events remain recorded.
            if (eventName == "network.Pose") return;
            Record(eventName, detail, true);
            if (feedback != null && (eventName.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0
                || eventName == "network.Rejected"
                || (eventName == "network.Stopped" && !detail.StartsWith("Local session left", StringComparison.Ordinal))))
                feedback.Capture(eventName, detail, String.Empty);
            WriteSnapshot("latest.log");
        }

        private string FeedbackContext()
        {
            return SceneDescription() + " loaderReady=" + lifetime.Ready + " stopped=" + stopped
                + " starts=" + lifetime.Starts + " cleanups=" + lifetime.Cleanups
                + "\r\n" + (networkPanel == null ? "Network panel unavailable." : networkPanel.DiagnosticSummary);
        }

        private void ReportProblem(string note)
        {
            if (feedback == null) { exportSummary = "Feedback files are unavailable; check the loader console."; return; }
            feedback.ReportProblem(note);
            exportSummary = feedback.Status;
            RefreshOverlay();
        }

        internal void ObserveUnityError(int token, string message, string stack, LogType type)
        {
            if (token != lifetime.Generation || feedback == null
                || (type != LogType.Exception && type != LogType.Error && type != LogType.Assert)) return;
            // Engine/other-mod errors are not attributed to this project without a
            // matching namespace or explicit mod marker. Never collect whole game logs.
            if ((stack == null || stack.IndexOf("MSCMP.", StringComparison.Ordinal) < 0)
                && (message == null || (message.IndexOf("[MSCMP]", StringComparison.Ordinal) < 0
                && message.IndexOf("MSCMP.", StringComparison.Ordinal) < 0))) return;
            feedback.Capture("unity-" + type, message, stack);
        }

        private void CaptureFailure(string operation, Exception error)
        {
            if (feedback != null) feedback.Capture(operation, error.GetType().FullName + ": " + error.Message, error.StackTrace);
        }

        private bool WriteSnapshot(string fileName)
        {
            if (String.IsNullOrEmpty(diagnosticDirectory))
                return false;
            try
            {
                Directory.CreateDirectory(diagnosticDirectory);
                string header = "MSC-MP discovery diagnostics " + Version + "\r\n"
                    + "Diagnostic run=" + diagnosticRun + " (counters are scoped to this mod instance).\r\n"
                    + "Coverage: lifecycle and transport probes; no shared-gameplay or save-isolation support.\r\n"
                    + "Runtime acceptance: awaiting manual verification.\r\n"
                    + "Active generation=" + lifetime.Generation + " starts=" + lifetime.Starts
                    + " cleanups=" + lifetime.Cleanups + " loaderReady=" + lifetime.Ready + "\r\n"
                    + "Lifecycle evidence: latest " + trace.LifecycleCount + "/" + DiagnosticTrace.LifecycleCapacity
                    + " entries; inspector/focus/export activity cannot evict these.\r\n\r\n";
                string contents = header + trace.LifecycleSnapshot()
                    + "\r\nRecent events (includes inspector): latest " + trace.Count + "/" + DiagnosticTrace.Capacity
                    + ". Events present in both sections represent one occurrence.\r\n\r\n" + trace.Snapshot();
                File.WriteAllText(Path.Combine(diagnosticDirectory, fileName), contents, Encoding.UTF8);
                return true;
            }
            catch (Exception error)
            {
                ReportFileFailure(error);
                return false;
            }
        }

        private void ReportFileFailure(Exception error)
        {
            if (fileFailureReported)
                return;
            fileFailureReported = true;
            Record("diagnostic.file-error", "type=" + error.GetType().Name + "; paths and exception messages omitted", true);
        }

        private void HandleRuntimeFailure(string operation, Exception error)
        {
            stopped = true;
            CaptureFailure(operation, error);
            Record("diagnostic.runtime-error", "operation=" + operation + " type=" + error.GetType().Name, true);
            ReleaseCurrent("diagnostic-error");
            WriteSnapshot("latest.log");
            // A failed observer construction may already have ended its generation,
            // leaving no future Update or EndGeneration flush to save this error.
            if (feedback != null) feedback.Flush(false);
        }
    }
}
