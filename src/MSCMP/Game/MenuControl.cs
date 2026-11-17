using System;
using HutongGames.PlayMaker;
using MSCMP.Net;
using UnityEngine;

namespace MSCMP.Game
{
    /// <summary>
    /// Main menu automation for clients: presses "Continue" when the host's world is
    /// ready, and greys out "New game" while connected (a new game would not be the
    /// host's world).
    /// </summary>
    internal static class MenuControl
    {
        static bool wantStart;
        static float startRequested;
        static float nextClick;
        static int clicks;
        static bool newGameBlocked;
        static Collider newGameCollider;

        public static bool Starting { get { return wantStart; } }

        public static void OnMenu()
        {
            wantStart = false;
            newGameBlocked = false;
            newGameCollider = null;
        }

        /// <summary>Loads the GAME scene from the main menu exactly like pressing Continue.</summary>
        public static void StartGame()
        {
            if (!MP.InMenu || wantStart) return;
            wantStart = true;
            startRequested = Time.realtimeSinceStartup;
            nextClick = 0f;
            clicks = 0;
            Log.Info("Starting the game (Continue) to load the host's world.");
            Notifications.Show("Loading the host's world...");
        }

        public static void Update()
        {
            if (!MP.InMenu) { wantStart = false; return; }
            UpdateNewGameLock();
            if (!wantStart) return;
            if (Application.isLoadingLevel) return;

            float now = Time.realtimeSinceStartup;
            if (now - startRequested > 6f)
            {
                // The button did not react (e.g. it was hidden because there was no save when
                // the menu opened). Load the scene directly; the game loads whatever save is on disk.
                Log.Info("Continue button did not start the game; loading GAME directly.");
                wantStart = false;
                Application.LoadLevel("GAME");
                return;
            }
            if (now < nextClick) return;
            nextClick = now + 0.5f;
            clicks++;

            GameObject button = GameObject.Find("Interface/Buttons/ButtonContinue");
            if (button == null) return;
            PlayMakerFSM[] fsms = button.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < fsms.Length; i++)
            {
                if (TryClick(fsms[i])) return;
            }
        }

        /// <summary>Sends whatever mouse event the button's FSM is waiting for.</summary>
        static bool TryClick(PlayMakerFSM fsm)
        {
            if (fsm == null || fsm.Fsm == null || fsm.Fsm.ActiveState == null) return false;
            string[] preferred = { "MOUSE DOWN", "MOUSE UP", "MOUSE UP AS BUTTON", "CLICK", "MOUSE OVER", "MOUSE ENTER" };
            FsmTransition[] transitions = fsm.Fsm.ActiveState.Transitions;
            for (int p = 0; p < preferred.Length; p++)
            {
                for (int t = 0; t < transitions.Length; t++)
                {
                    if (transitions[t].EventName == preferred[p])
                    {
                        Log.Debug("Continue button: sending " + preferred[p] + " in state " + fsm.Fsm.ActiveStateName);
                        fsm.SendEvent(preferred[p]);
                        return true;
                    }
                }
            }
            FsmTransition[] global = fsm.FsmGlobalTransitions;
            for (int p = 0; p < preferred.Length; p++)
                for (int t = 0; t < global.Length; t++)
                    if (global[t].EventName == preferred[p])
                    {
                        fsm.SendEvent(preferred[p]);
                        return true;
                    }
            return false;
        }

        static void UpdateNewGameLock()
        {
            bool shouldBlock = Session.IsClient && Session.Connected;
            if (shouldBlock == newGameBlocked && (newGameCollider != null || !shouldBlock)) return;
            if (newGameCollider == null)
            {
                GameObject b = GameObject.Find("Interface/Buttons/ButtonNewgame");
                if (b != null) newGameCollider = b.GetComponent<Collider>();
            }
            if (newGameCollider != null)
            {
                newGameCollider.enabled = !shouldBlock;
                newGameBlocked = shouldBlock;
            }
        }
    }
}
