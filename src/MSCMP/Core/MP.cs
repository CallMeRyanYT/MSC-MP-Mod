using System;
using System.Reflection;
using Harmony;
using MSCMP.Avatars;
using MSCMP.Game;
using MSCMP.Net;
using MSCMP.Saves;
using MSCMP.Sync;
using MSCMP.UI;
using Steamworks;
using UnityEngine;

namespace MSCMP
{
    /// <summary>
    /// Central coordinator: owns the lifecycle (menu / loading / in game), the
    /// update loop, and tells every subsystem when the world becomes ready or goes away.
    /// </summary>
    internal static class MP
    {
        public static MSCMPMod Mod;
        public static int GameBuildId;

        /// <summary>The GAME scene is loaded and all world registries are built.</summary>
        public static bool InGame { get; private set; }
        /// <summary>The GAME scene is the active level (possibly still loading).</summary>
        public static bool InGameScene { get { return Application.loadedLevelName == "GAME"; } }
        public static bool InMenu { get { return Application.loadedLevelName == "MainMenu"; } }

        static bool processInit;
        static bool gameLoadCalled;
        static float gameLoadTime;
        static string lastLevel = "";
        static GameObject runnerObject;

        // ------------------------------------------------------------ lifecycle (from MSCLoader)

        public static void OnMenuLoad()
        {
            EnsureProcessInit();
            EnsureRunner();
            if (InGame) LeaveWorld();
            gameLoadCalled = false;
            GameUtil.ClearCaches();
            SaveSync.OnMenu();
            MenuControl.OnMenu();
            Session.OnMenu();
            GameSession.OnMenu();
        }

        public static void OnNewGame()
        {
            SaveSync.OnNewGameStarted();
            if (!Session.IsClient) PlayerCare.OnNewGame();
        }

        public static void OnGameLoaded()
        {
            EnsureProcessInit();
            EnsureRunner();
            GameUtil.ClearCaches();
            gameLoadCalled = true;
            gameLoadTime = Time.realtimeSinceStartup;
            Log.Info("GAME scene loaded; waiting for the world to settle.");
            if (Session.Active) Session.SetLocalPhase(PlayerPhase.Syncing);
        }

        public static void OnGameSaved()
        {
            Log.Info("Game saved (toilet save & quit).");
            SaveSync.OnHostSaved();
            Safe("PlayerCare.Saved", PlayerCare.OnHostSaved);
        }

        static bool gameBuildRead;

        static void ReadGameBuild()
        {
            try { GameBuildId = SteamApps.GetAppBuildId(); } catch { GameBuildId = 0; }
        }

        static void EnsureProcessInit()
        {
            if (processInit) return;
            processInit = true;
            try
            {
                Log.Init(MSCLoader.ModLoader.GetModSettingsFolder(Mod));
                Log.Info("MSC Multiplayer " + BuildInfo.Version + " starting.");
                PatchSteamShutdown();
                SteamNet.Init();
                Session.Init();
                GameSession.Init();
                SaveSync.Init();
                PlayerSync.Init();
                ObjectSync.Init();
                VehicleSync.Init();
                FsmSync.Init();
                WorldSync.Init();
                VarSync.Init();
                Bots.Init();
                PlayerCare.Init();
                Markers.Init();
                SaveSync.RecoverInterruptedSession();
                Session.JoinFromCommandLine();
            }
            catch (Exception e)
            {
                Log.Exception("Initialisation", e);
            }
        }

        /// <summary>
        /// MSCLoader destroys every GameObject when you return to the main menu, which
        /// includes the game's SteamManager, whose OnDestroy calls SteamAPI.Shutdown().
        /// That would drop the multiplayer session, so the shutdown is skipped (Steam is
        /// still cleaned up normally when the game process exits).
        /// </summary>
        static void PatchSteamShutdown()
        {
            try
            {
                // SteamManager is internal to the game's Assembly-CSharp.
                Type steamManager = typeof(Drivetrain).Assembly.GetType("SteamManager");
                MethodInfo target = steamManager != null ? steamManager.GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) : null;
                MethodInfo prefix = typeof(MP).GetMethod("SteamManagerOnDestroyPrefix", BindingFlags.Static | BindingFlags.NonPublic);
                if (target == null || prefix == null)
                {
                    Log.Warn("SteamManager.OnDestroy not found; returning to the menu will end multiplayer sessions.");
                    return;
                }
                HarmonyInstance.Create("mscmp.steamkeepalive").Patch(target, new HarmonyMethod(prefix), null, null);
                Log.Info("Steam keep-alive patch applied.");
            }
            catch (Exception e)
            {
                Log.Exception("Steam keep-alive patch", e);
            }
        }

        static bool SteamManagerOnDestroyPrefix()
        {
            return false; // never shut Steam down from SteamManager.OnDestroy
        }

        static void EnsureRunner()
        {
            if (runnerObject != null) return;
            runnerObject = new GameObject("MSCMP");
            UnityEngine.Object.DontDestroyOnLoad(runnerObject);
            runnerObject.AddComponent<Runner>();
            runnerObject.AddComponent<MultiplayerUI>();
        }

        /// <summary>Called from Mod.Update so the runner is recreated after MSCLoader resets.</summary>
        public static void Tick()
        {
            if (!processInit) return;
            EnsureRunner();
        }

        // ------------------------------------------------------------ frame loop (from Runner)

        public static void Update()
        {
            string level = Application.loadedLevelName;
            if (level != lastLevel)
            {
                Log.Info("Level changed: " + lastLevel + " -> " + level);
                if (lastLevel == "GAME" && InGame) LeaveWorld();
                lastLevel = level;
            }

            if (SteamNet.RetryIfNeeded()) Notifications.Show("Connected to Steam. Multiplayer is ready.");
            if (SteamNet.Available && !gameBuildRead) { gameBuildRead = true; ReadGameBuild(); }
            try { Session.Update(); }
            catch (Exception e) { Log.ExceptionOnce("Session.Update", e); }

            if (InGameScene && !InGame && gameLoadCalled && Time.realtimeSinceStartup - gameLoadTime > 1.5f)
                TryEnterWorld();

            Safe("GameSession", GameSession.Update);
            Safe("SaveSync", SaveSync.Update);
            Safe("MenuControl", MenuControl.Update);
            if (InGame)
            {
                Safe("LocalPlayer", LocalPlayer.Update);
                Safe("PlayerSync", PlayerSync.Update);
                Safe("Bots", Bots.Update);
                Safe("FsmSync", FsmSync.Update);        // before ObjectSync: links objects created by world events
                Safe("ObjectSync", ObjectSync.Update);
                Safe("VehicleSync", VehicleSync.Update);
                Safe("WorldSync", WorldSync.Update);
                Safe("VarSync", VarSync.Update);
                Safe("PlayerCare", PlayerCare.Update);
                Safe("Markers", Markers.Update);
                Safe("Loopback", Loopback.Update);
            }
            Safe("AutoTest", AutoTest.Update);
            Safe("CharacterPreview", UI.CharacterPreview.Update);
            Notifications.Prune();
        }

        public static void LateUpdate()
        {
            if (!InGame) return;
            Safe("FsmSync.Late", FsmSync.LateUpdate);
            Safe("PlayerSync.Late", PlayerSync.LateUpdate);
            Safe("CharacterPreview.Late", UI.CharacterPreview.LateUpdate);
            Safe("Bots.Late", Bots.LateUpdate);
            Safe("VehicleSync.Late", VehicleSync.LateUpdate);
            Safe("LocalPlayer.Late", LocalPlayer.LateUpdate);
        }

        public static void FixedUpdate()
        {
            if (!InGame) return;
            Safe("ObjectSync.Fixed", ObjectSync.FixedUpdate);
            Safe("VehicleSync.Fixed", VehicleSync.FixedUpdate);
        }

        static void Safe(string name, Action a)
        {
            try { a(); }
            catch (Exception e) { Log.ExceptionOnce(name, e); }
        }

        // ------------------------------------------------------------ world

        static void TryEnterWorld()
        {
            if (!LocalPlayer.Bind()) return; // PLAYER not ready yet
            InGame = true;
            Log.Info("World ready - building registries.");
            Safe("ObjectSync.WorldReady", ObjectSync.OnWorldReady);
            Safe("VehicleSync.WorldReady", VehicleSync.OnWorldReady);
            Safe("FsmSync.WorldReady", FsmSync.OnWorldReady);
            Safe("WorldSync.WorldReady", WorldSync.OnWorldReady);
            Safe("VarSync.WorldReady", VarSync.OnWorldReady);
            Safe("PlayerSync.WorldReady", PlayerSync.OnWorldReady);
            Safe("GameSession.WorldReady", GameSession.OnWorldReady);
        }

        static void LeaveWorld()
        {
            if (!InGame) return;
            InGame = false;
            Log.Info("Left the GAME world.");
            Safe("GameSession.WorldLeft", GameSession.OnWorldLeft);
            Safe("PlayerSync.WorldLeft", PlayerSync.OnWorldLeft);
            Safe("VehicleSync.WorldLeft", VehicleSync.OnWorldLeft);
            Safe("ObjectSync.WorldLeft", ObjectSync.OnWorldLeft);
            Safe("FsmSync.WorldLeft", FsmSync.OnWorldLeft);
            Safe("WorldSync.WorldLeft", WorldSync.OnWorldLeft);
            Safe("VarSync.WorldLeft", VarSync.OnWorldLeft);
            Safe("Bots.WorldLeft", Bots.OnWorldLeft);
            Safe("LocalPlayer.WorldLeft", LocalPlayer.OnWorldLeft);
            GameUtil.ClearCaches();
        }
    }

    /// <summary>Persistent MonoBehaviour that drives the mod every frame.</summary>
    internal sealed class Runner : MonoBehaviour
    {
        void Update() { MP.Update(); }
        void LateUpdate() { MP.LateUpdate(); }
        void FixedUpdate() { MP.FixedUpdate(); }
    }
}
