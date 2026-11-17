using System;
using MSCMP.Net;
using MSCMP.Saves;
using MSCMP.Sync;
using UnityEngine;

namespace MSCMP.Game
{
    /// <summary>
    /// The join flow on top of the network session:
    ///   host save -> client downloads it -> client loads GAME -> host sends a catch-up
    ///   (every world change since the host loaded) -> client plays.
    /// The world is always "the host's save as it was loaded + every change since",
    /// which is what makes late joining exact.
    /// </summary>
    internal static class GameSession
    {
        static bool autoJoinWorld;
        static bool catchingUp;
        static float savedListenerVolume = 1f;
        static float returnToMenuAt;
        static float saveRequestedAt = -100f;
        static bool leftWorldVoluntarily;
        static float respawnAt;              // after dying: when to load back in

        public static bool CatchingUp { get { return catchingUp; } }
        public static bool WaitingToRejoin { get { return Session.IsClient && Session.Connected && MP.InMenu && Session.HostInGame && !autoJoinWorld; } }

        public static void Init()
        {
            Session.Started += OnStarted;
            Session.Ended += OnEnded;
            Session.PlayerJoined += OnPlayerJoined;
            Session.On(Msg.HostInGame, OnHostInGame);
            Session.On(Msg.HostInMenu, OnHostInMenu);
            Session.On(Msg.SaveInstalled, OnSaveInstalled);
            Session.On(Msg.ClientLoaded, OnClientLoaded);
            Session.On(Msg.CatchUpBegin, OnCatchUpBegin);
            Session.On(Msg.CatchUpEnd, OnCatchUpEnd);
            Session.On(Msg.ClientReady, OnClientReady);
        }

        // ------------------------------------------------------------ session events

        static void OnStarted()
        {
            if (Session.IsHost)
            {
                Session.SetLocalPhase(MP.InGame ? PlayerPhase.Playing : PlayerPhase.InMenu);
                if (!SaveSync.HostCanServe && !string.IsNullOrEmpty(SaveSync.HostProblem))
                    Notifications.Show(SaveSync.HostProblem);
            }
            else
            {
                autoJoinWorld = true;
                leftWorldVoluntarily = false;
                Session.SetLocalPhase(PlayerPhase.InMenu);
            }
        }

        static void OnEnded(string reason)
        {
            EndCatchUp();
            autoJoinWorld = false;
            if (MP.InGameScene && !Session.IsHost)
            {
                // A client that lost the session is standing in someone else's world.
                // Send them home; their own save is restored in the main menu.
                returnToMenuAt = Time.realtimeSinceStartup + 4f;
                Notifications.Show("Multiplayer ended (" + reason + "). Returning to the main menu - your own save will be restored.");
            }
        }

        static void OnPlayerJoined(NetPlayer p)
        {
            if (Session.IsHost) SaveSync.SendSaveTo(p);
        }

        // ------------------------------------------------------------ scene events

        public static void OnMenu()
        {
            returnToMenuAt = 0f;
            EndCatchUp();
            if (!Session.Active) return;
            if (Session.IsHost)
            {
                Session.SetLocalPhase(PlayerPhase.InMenu);
            }
            else
            {
                Session.SetLocalPhase(SaveSync.ClientHasHostSave ? PlayerPhase.SaveReady : PlayerPhase.InMenu);
                if (leftWorldVoluntarily) autoJoinWorld = false;
                leftWorldVoluntarily = false;
            }
        }

        public static void OnWorldReady()
        {
            if (!Session.Active) return;
            if (Session.IsHost)
            {
                Session.SetLocalPhase(PlayerPhase.Playing);
                Session.Begin(Msg.HostInGame);
                Session.Broadcast(true, Channel.Reliable);
                Notifications.Show("Your friends can now load into your world.");
            }
            else
            {
                Session.SetLocalPhase(PlayerPhase.Syncing);
                Session.Begin(Msg.ClientLoaded);
                Session.SendToHost(true, Channel.Reliable);
                Notifications.Show("Synchronising with the host...");
            }
        }

        public static void OnWorldLeft()
        {
            EndCatchUp();
            if (!Session.Active) return;
            if (Session.IsHost)
            {
                Session.Begin(Msg.HostInMenu);
                Session.Broadcast(true, Channel.Reliable);
            }
            else
            {
                // Our game may have saved (toilet) or deleted (death) the copy on disk.
                bool died = !System.IO.File.Exists(System.IO.Path.Combine(Application.persistentDataPath, "defaultES2File.txt"));
                SaveSync.MarkStale();
                if (died && Session.HostInGame)
                {
                    // Dying deletes the save, like in single player. Come back fresh by ourselves.
                    PlayerCare.ReportDeath();
                    respawnAt = Time.realtimeSinceStartup + 8f;
                    autoJoinWorld = true;
                    leftWorldVoluntarily = false;
                    Notifications.Show("You died. You'll be back in " + HostName() + "'s world in a moment.");
                    Log.Info("Died in the host's world; respawning.");
                }
                else if (Session.HostInGame) leftWorldVoluntarily = true;
            }
        }

        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (returnToMenuAt > 0f && now >= returnToMenuAt)
            {
                returnToMenuAt = 0f;
                if (MP.InGameScene) Application.LoadLevel("MainMenu");
            }

            if (!Session.IsClient || !Session.Connected || !MP.InMenu) return;
            if (!Session.HostInGame || !autoJoinWorld || MenuControl.Starting) return;
            if (now < respawnAt) return;

            if (!SaveSync.ClientHasHostSave)
            {
                if (Session.Local != null && Session.Local.Phase != PlayerPhase.DownloadingSave && now - saveRequestedAt > 15f)
                {
                    saveRequestedAt = now;
                    SaveSync.RequestSave();
                }
                return;
            }
            Session.SetLocalPhase(PlayerPhase.Loading);
            MenuControl.StartGame();
        }

        /// <summary>UI: client chose to go back into the host's world.</summary>
        public static void Rejoin()
        {
            autoJoinWorld = true;
            saveRequestedAt = -100f;
        }

        // ------------------------------------------------------------ messages

        static void OnHostInGame(NetPlayer from, NetReader r)
        {
            if (!Session.IsClient) return;
            Session.HostInGame = true;
            if (!MP.InGameScene) autoJoinWorld = true;
        }

        static void OnHostInMenu(NetPlayer from, NetReader r)
        {
            if (!Session.IsClient) return;
            Session.HostInGame = false;
            autoJoinWorld = true; // follow the host into the next world
            if (MP.InGameScene)
            {
                Notifications.Show("The host went back to the main menu. You will follow them into the world again when they continue.");
                returnToMenuAt = Time.realtimeSinceStartup + 2f;
            }
        }

        static void OnSaveInstalled(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null) return;
            from.Phase = PlayerPhase.SaveReady;
            if (MP.InGame)
            {
                Session.Begin(Msg.HostInGame);
                Session.SendTo(from, true, Channel.Reliable);
            }
        }

        static void OnClientLoaded(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null) return;
            if (!MP.InGame)
            {
                Session.Begin(Msg.HostInMenu);
                Session.SendTo(from, true, Channel.Reliable);
                return;
            }
            from.Phase = PlayerPhase.Syncing;
            Log.Info("Sending catch-up to " + from.Describe());
            Session.Begin(Msg.CatchUpBegin);
            Session.SendTo(from, true, Channel.Reliable);
            try
            {
                ObjectSync.SendManifest(from);
                WorldLog.Replay(from);
                ObjectSync.SendCatchUp(from);
                VehicleSync.SendCatchUp(from);
                FsmSync.SendCatchUp(from);
                WorldSync.SendCatchUp(from);
                VarSync.SendCatchUp(from);
                Bots.SendCatchUp(from);
                MusicSync.SendCatchUp(from);
            }
            catch (Exception e)
            {
                Log.Exception("Catch-up for " + from.Describe(), e);
            }
            Session.Begin(Msg.CatchUpEnd);
            Session.SendTo(from, true, Channel.Reliable);
        }

        static void OnCatchUpBegin(NetPlayer from, NetReader r)
        {
            if (!Session.IsClient) return;
            catchingUp = true;
            savedListenerVolume = AudioListener.volume;
            AudioListener.volume = 0f; // hide the burst of replayed sounds (bolts, doors...)
            Log.Info("Catch-up started");
        }

        static void OnCatchUpEnd(NetPlayer from, NetReader r)
        {
            if (!Session.IsClient) return;
            EndCatchUp();
            Session.SetLocalPhase(PlayerPhase.Playing);
            Session.Begin(Msg.ClientReady);
            Session.SendToHost(true, Channel.Reliable);
            Notifications.Show("You are in " + HostName() + "'s world. Have fun!");
            Log.Info("Catch-up finished");
        }

        static void EndCatchUp()
        {
            if (!catchingUp) return;
            catchingUp = false;
            AudioListener.volume = savedListenerVolume <= 0f ? 1f : savedListenerVolume;
        }

        static void OnClientReady(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null) return;
            from.Phase = PlayerPhase.Playing;
            Notifications.Show(from.Name + " is now in your world.");
            PlayerCare.OnClientReady(from);
        }

        static string HostName()
        {
            NetPlayer h = Session.BySlot(0);
            return h != null ? h.Name : "the host";
        }
    }
}
