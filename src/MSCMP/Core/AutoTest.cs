using System;
using System.Collections.Generic;
using System.IO;
using MSCMP.Avatars;
using MSCMP.Game;
using MSCMP.Sync;
using UnityEngine;

namespace MSCMP
{
    /// <summary>
    /// Developer self-test. Only runs when a file named "autotest.txt" exists in the mod's
    /// settings folder: loads the save, spawns bots, runs their orders, checks their bodies,
    /// saves in-game screenshots and a report next to the log, then quits the game.
    /// </summary>
    internal static class AutoTest
    {
        static bool enabled, checkedFlag, finished;
        static float menuSince = -1f, worldSince = -1f;
        static int step;
        static float nextStep;
        static string folder;
        static readonly List<string> report = new List<string>();
        static int failures;
        static int emoteStep;

        static float nextCheck;

        /// <summary>Looks for the flag file every few seconds (so it can be dropped in while the game runs).</summary>
        public static bool Enabled
        {
            get
            {
                if (enabled) return true;
                float now = Time.realtimeSinceStartup;
                if (now < nextCheck) return false;
                nextCheck = now + 3f;
                try
                {
                    string logPath = Log.FilePath;
                    string[] candidates =
                    {
                        logPath != null ? Path.GetDirectoryName(Path.GetFullPath(logPath)) : null,
                        Path.GetFullPath("Mods/Config/Mod Settings/MSCMP"),
                        Path.GetFullPath(".")
                    };
                    foreach (string c in candidates)
                    {
                        if (c == null) continue;
                        if (!checkedFlag) Log.Info("AUTOTEST looking for flag in " + c);
                        if (File.Exists(Path.Combine(c, "autotest.txt")))
                        {
                            folder = c;
                            enabled = true;
                            break;
                        }
                    }
                    checkedFlag = true;
                    if (enabled)
                    {
                        Log.Info("AUTOTEST enabled (" + folder + ").");
                        Application.runInBackground = true;
                        foreach (string f in Directory.GetFiles(folder, "autotest_*.png")) File.Delete(f);
                    }
                }
                catch (Exception e) { Log.ExceptionOnce("AutoTest flag", e); }
                return enabled;
            }
        }

        public static void Update()
        {
            if (!Enabled || finished) return;
            float now = Time.realtimeSinceStartup;
            if (MP.InMenu)
            {
                if (menuSince < 0f) menuSince = now;
                if (now - menuSince > 4f && !MenuControl.Starting) { Log.Info("AUTOTEST: pressing Continue"); MenuControl.StartGame(); }
                return;
            }
            menuSince = -1f;
            if (!MP.InGame) return;
            if (worldSince < 0f) { worldSince = now; nextStep = now + 6f; }
            if (now < nextStep) return;
            try { Step(now); }
            catch (Exception e) { Fail("step " + step + " threw " + e); step++; nextStep = now + 1f; }
        }

        static void Shot(string name)
        {
            string file = Path.Combine(folder, "autotest_" + name + ".png");
            Application.CaptureScreenshot(file);
            Log.Info("AUTOTEST screenshot " + file);
        }

        static void Check(string what)
        {
            // The loopback copies are what a friend's game draws: check them too.
            List<KeyValuePair<string, RemoteAvatar>> remote = new List<KeyValuePair<string, RemoteAvatar>>();
            if (Loopback.Player != null && Loopback.Player.Avatar != null) remote.Add(new KeyValuePair<string, RemoteAvatar>("you via loopback", Loopback.Player.Avatar));
            foreach (Net.NetPlayer rp in Bots.RemotePlayers) if (rp.Avatar != null) remote.Add(new KeyValuePair<string, RemoteAvatar>(rp.Name, rp.Avatar));
            foreach (KeyValuePair<string, RemoteAvatar> kv in remote)
            {
                bool bad = kv.Value.LooksBroken();
                string line = (bad ? "FAIL " : "ok   ") + what + " / " + kv.Key + ": " + kv.Value.Diagnostics();
                report.Add(line);
                Log.Info("AUTOTEST " + line);
                if (bad) failures++;
            }
            foreach (Bots.Bot b in Bots.All)
            {
                string d = b.P.Avatar.Diagnostics();
                bool broken = b.P.Avatar.LooksBroken();
                string line = (broken ? "FAIL " : "ok   ") + what + " / " + b.P.Name + " (" + Catalog.Bodies[b.P.Look.Body].Label + "): " + d;
                report.Add(line);
                Log.Info("AUTOTEST " + line);
                if (broken) failures++;
            }
        }

        static void Fail(string msg)
        {
            failures++;
            report.Add("FAIL " + msg);
            Log.Warn("AUTOTEST FAIL " + msg);
        }

        static void Step(float now)
        {
            nextStep = now + 3f;
            switch (step++)
            {
                case 0:
                    Shot("00_world");
                    for (int i = 0; i < Catalog.Bodies.Length && i < 4; i++) Bots.Spawn();
                    Loopback.Set(true);
                    if (Bots.All.Count == 0) Fail("no bots could be spawned");
                    break;
                case 1:
                    for (int i = 0; i < Bots.All.Count; i++) Bots.PlaceInFront(Bots.All[i], 3.5f, (i - (Bots.All.Count - 1) * 0.5f) * 1.2f);
                    nextStep = now + 4f;
                    break;
                case 2:
                    Check("standing");
                    Shot("01_standing");
                    break;
                case 3:
                    foreach (Bots.Bot b in Bots.All) b.ForceCrouch = 0.6f;
                    nextStep = now + 3f;
                    break;
                case 4:
                    Check("crouching");
                    Shot("02_crouching");
                    foreach (Bots.Bot b in Bots.All) b.ForceCrouch = 1f;
                    break;
                case 5:
                    Check("crouched fully");
                    Shot("03_crouch_full");
                    foreach (Bots.Bot b in Bots.All) { b.ForceCrouch = -1f; Bots.Wave(b); }
                    nextStep = now + 1f;
                    break;
                case 6:
                    Shot("04_waving");
                    Check("waving");
                    foreach (Bots.Bot b in Bots.All) Bots.Command(b, Bots.Order.Wander);
                    nextStep = now + 7f;
                    break;
                case 7:
                    Check("wandering");
                    Shot("05_wandering");
                    foreach (Bots.Bot b in Bots.All) Bots.Command(b, Bots.Order.Follow);
                    nextStep = now + 6f;
                    break;
                case 8:
                    Check("following");
                    Shot("06_following");
                    if (Bots.All.Count > 0) Bots.Command(Bots.All[0], Bots.Order.Fetch);
                    nextStep = now + 15f;
                    break;
                case 9:
                    Check("fetching");
                    Shot("07_fetch");
                    if (Bots.All.Count > 0) report.Add("fetch bot state: " + Bots.All[0].OrderText);
                    foreach (Bots.Bot b in Bots.All) Bots.Command(b, Bots.Order.Copy);
                    nextStep = now + 4f;
                    break;
                case 10:
                    Check("copying");
                    Shot("08_copy");
                    for (int i = 0; i < Bots.All.Count; i++) Bots.NextBody(Bots.All[i]);
                    for (int i = 0; i < Bots.All.Count; i++) Bots.PlaceInFront(Bots.All[i], 3.5f, (i - (Bots.All.Count - 1) * 0.5f) * 1.2f);
                    nextStep = now + 3f;
                    break;
                case 11:
                    Check("other bodies");
                    Shot("09_bodies");
                    for (int i = 0; i < Bots.All.Count; i++) Bots.NextBody(Bots.All[i]);
                    for (int i = 0; i < Bots.All.Count; i++) Bots.NextBody(Bots.All[i]);
                    nextStep = now + 3f;
                    break;
                case 12:
                    Check("more bodies");
                    Shot("10_bodies2");
                    emoteStep = 2;
                    break;
                case 13:
                    // Every emote on every bot (and on the loopback copies).
                    if (emoteStep <= Emotes.Count)
                    {
                        foreach (Bots.Bot b in Bots.All) Bots.Emote(b, (byte)emoteStep);
                        nextStep = now + 1.2f;
                        step = 14;
                    }
                    break;
                case 14:
                    Check("emote " + Emotes.Names[emoteStep]);
                    Shot("11_emote_" + emoteStep);
                    emoteStep++;
                    if (emoteStep <= Emotes.Count) step = 13;
                    break;
                default:
                    Finish();
                    break;
            }
        }

        static void Finish()
        {
            finished = true;
            report.Insert(0, "AUTOTEST " + (failures == 0 ? "PASSED" : "FAILED (" + failures + ")") + " " + DateTime.Now);
            report.Add("World values tracked: " + VarSync.Count + ", interactive FSMs: " + FsmSync.Count + " (" + FsmSync.HookedStates + " states)");
            try { File.WriteAllText(Path.Combine(folder, "autotest_report.txt"), string.Join("\r\n", report.ToArray())); }
            catch (Exception e) { Log.Exception("AutoTest report", e); }
            Log.Info("AUTOTEST finished, failures: " + failures + ". Quitting.");
            Bots.RemoveAll();
            Application.Quit();
        }
    }
}
