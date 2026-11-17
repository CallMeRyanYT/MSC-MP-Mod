using System;
using System.Collections.Generic;
using Steamworks;

namespace MSCMP.Net
{
    /// <summary>
    /// Thin wrapper over the game's own Steamworks.NET: friends-only lobbies,
    /// overlay invites, and P2P packets (Valve relays them, so no port
    /// forwarding or IP addresses are ever needed).
    /// </summary>
    internal static class SteamNet
    {
        public static bool Available { get; private set; }
        public static CSteamID LocalId { get; private set; }
        public static string LocalName { get; private set; }
        public static CSteamID Lobby { get; private set; }
        public static bool InLobby { get { return Lobby.m_SteamID != 0; } }

        // Events (raised from SteamAPI.RunCallbacks on the main thread)
        public static event Action<CSteamID> LobbyCreated;
        public static event Action<string> LobbyCreateFailed;
        public static event Action<CSteamID, CSteamID> LobbyEntered; // lobby, owner
        public static event Action<string> LobbyEnterFailed;
        public static event Action<CSteamID> JoinRequested;          // lobby id from an invite / "Join game"
        public static event Action<CSteamID, bool> LobbyMemberChanged; // user, joined
        public static event Action<CSteamID, string> P2PFailed;

        /// <summary>Who we are willing to exchange packets with.</summary>
        public static Func<CSteamID, bool> AcceptPeer;

        static Callback<P2PSessionRequest_t> cbSessionRequest;
        static Callback<P2PSessionConnectFail_t> cbConnectFail;
        static Callback<GameLobbyJoinRequested_t> cbLobbyJoinRequested;
        static Callback<GameRichPresenceJoinRequested_t> cbRichJoin;
        static Callback<LobbyChatUpdate_t> cbChatUpdate;
        static CallResult<LobbyCreated_t> crLobbyCreated;
        static CallResult<LobbyEnter_t> crLobbyEnter;

        static readonly Dictionary<ulong, float> pendingSessions = new Dictionary<ulong, float>();
        static byte[] readBuffer = new byte[64 * 1024];
        static bool initTried;

        /// <summary>Why Steam is not available, in words for the player ("" when it is).</summary>
        public static string Problem { get; private set; }
        static float nextRetry;

        /// <summary>Tries again every few seconds while Steam is unavailable: Steam may still be
        /// starting, or the game may set it up after the mod loaded.</summary>
        public static bool RetryIfNeeded()
        {
            if (Available) return false;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < nextRetry) return false;
            nextRetry = now + 5f;
            return Init();
        }

        public static bool Init()
        {
            if (Available) return true;
            bool firstTry = !initTried;
            initTried = true;
            string problem;
            try
            {
                bool running = false;
                try { running = SteamAPI.IsSteamRunning(); } catch (Exception e) { Log.ExceptionOnce("SteamAPI.IsSteamRunning", e); }
                if (!running)
                {
                    problem = "Steam is not running. Start Steam, then start My Summer Car from your Steam library.";
                    return Fail(problem, firstTry);
                }
                // Make sure the Steam API is started before using it. The game normally does this
                // itself; if it hasn't (yet), or the game was not started through Steam, start it
                // here. Starting it again when it is already running is harmless.
                bool apiReady;
                try { apiReady = SteamAPI.Init(); }
                catch (Exception e) { Log.ExceptionOnce("SteamAPI.Init", e); apiReady = false; }
                if (!apiReady)
                {
                    problem = "Steam is running, but the game could not connect to it. Close the game and start My Summer Car from your Steam library (not from the .exe or a shortcut). If Steam runs as administrator, the game must too (or neither).";
                    return Fail(problem, firstTry);
                }
                LocalId = SteamUser.GetSteamID();
                if (LocalId.m_SteamID == 0)
                {
                    problem = "Steam is running, but no Steam user is logged in. Log in to Steam and try again.";
                    return Fail(problem, firstTry);
                }
                LocalName = SteamFriends.GetPersonaName();
                cbSessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
                cbConnectFail = Callback<P2PSessionConnectFail_t>.Create(OnConnectFail);
                cbLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
                cbRichJoin = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichPresenceJoin);
                cbChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnChatUpdate);
                crLobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
                crLobbyEnter = CallResult<LobbyEnter_t>.Create(OnLobbyEnter);
                SteamNetworking.AllowP2PPacketRelay(true);
                Available = true;
                Problem = "";
                Log.Info("Steam ready as " + LocalName + " (" + LocalId.m_SteamID + ")" + (firstTry ? "" : " (after retrying)"));
            }
            catch (Exception e)
            {
                Available = false;
                Log.Exception("Steam initialisation", e);
                return Fail("Steam could not be started (" + e.GetType().Name + "). Please send your mscmp.log.", firstTry);
            }
            return Available;
        }

        static bool Fail(string problem, bool firstTry)
        {
            Available = false;
            if (problem != Problem || firstTry)
            {
                // Details that tell a bug report how the game was started.
                string appIdFile = "?";
                try { appIdFile = System.IO.File.Exists("steam_appid.txt") ? "yes" : "no"; } catch { }
                string launchedBySteam = Environment.GetEnvironmentVariable("SteamAppId") ?? Environment.GetEnvironmentVariable("SteamGameId");
                Log.Warn("Steam unavailable: " + problem + " [started by Steam: " + (launchedBySteam != null ? "yes (" + launchedBySteam + ")" : "no") +
                         ", steam_appid.txt: " + appIdFile + "] Retrying every 5 s.");
            }
            Problem = problem;
            return false;
        }

        /// <summary>Pump Steam callbacks. The game's SteamManager does not survive
        /// MSCLoader's return-to-menu reset, so the mod always pumps them itself.</summary>
        public static void RunCallbacks()
        {
            if (!Available) return;
            try { SteamAPI.RunCallbacks(); }
            catch (Exception e) { Log.ExceptionOnce("SteamAPI.RunCallbacks", e); }

            if (pendingSessions.Count > 0)
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                List<ulong> done = null;
                foreach (KeyValuePair<ulong, float> p in pendingSessions)
                {
                    CSteamID id = new CSteamID(p.Key);
                    bool accept = AcceptPeer != null && AcceptPeer(id);
                    if (accept)
                    {
                        SteamNetworking.AcceptP2PSessionWithUser(id);
                        Log.Info("Accepted P2P session from " + p.Key);
                    }
                    if (accept || now - p.Value > 10f)
                    {
                        if (done == null) done = new List<ulong>();
                        done.Add(p.Key);
                    }
                }
                if (done != null) foreach (ulong k in done) pendingSessions.Remove(k);
            }
        }

        // ---------------------------------------------------------------- lobbies

        public static void CreateLobby(int maxMembers)
        {
            SteamAPICall_t call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxMembers);
            crLobbyCreated.Set(call, null);
        }

        public static void JoinLobby(CSteamID lobby)
        {
            SteamAPICall_t call = SteamMatchmaking.JoinLobby(lobby);
            crLobbyEnter.Set(call, null);
        }

        public static void LeaveLobby()
        {
            if (!InLobby) return;
            try { SteamMatchmaking.LeaveLobby(Lobby); } catch (Exception e) { Log.Exception("LeaveLobby", e); }
            Lobby = CSteamID.Nil;
            try { SteamFriends.SetRichPresence("connect", ""); } catch { }
        }

        public static void OpenInviteDialog()
        {
            if (!InLobby) return;
            try { SteamFriends.ActivateGameOverlayInviteDialog(Lobby); }
            catch (Exception e) { Log.Exception("Invite dialog", e); }
        }

        public static void SetLobbyData(string key, string value)
        {
            if (InLobby) SteamMatchmaking.SetLobbyData(Lobby, key, value);
        }

        public static string GetLobbyData(string key)
        {
            return InLobby ? SteamMatchmaking.GetLobbyData(Lobby, key) : string.Empty;
        }

        public static void SetJoinable(bool joinable)
        {
            if (InLobby) SteamMatchmaking.SetLobbyJoinable(Lobby, joinable);
        }

        public static bool IsLobbyMember(CSteamID id)
        {
            if (!InLobby) return false;
            int n = SteamMatchmaking.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i) == id) return true;
            return false;
        }

        public static string NameOf(CSteamID id)
        {
            try
            {
                string n = SteamFriends.GetFriendPersonaName(id);
                return string.IsNullOrEmpty(n) ? id.m_SteamID.ToString() : n;
            }
            catch { return id.m_SteamID.ToString(); }
        }

        public static void SetRichPresenceConnect()
        {
            if (!InLobby) return;
            try
            {
                SteamFriends.SetRichPresence("connect", "+connect_lobby " + Lobby.m_SteamID);
                SteamFriends.SetRichPresence("status", "Playing My Summer Car multiplayer");
            }
            catch (Exception e) { Log.Exception("Rich presence", e); }
        }

        /// <summary>Parses "+connect_lobby 1234" from a launch argument or rich presence string.</summary>
        public static bool TryParseConnect(string text, out CSteamID lobby)
        {
            lobby = CSteamID.Nil;
            if (string.IsNullOrEmpty(text)) return false;
            int i = text.IndexOf("+connect_lobby", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            string rest = text.Substring(i + "+connect_lobby".Length).Trim();
            int sp = rest.IndexOf(' ');
            if (sp > 0) rest = rest.Substring(0, sp);
            ulong id;
            if (!ulong.TryParse(rest, out id) || id == 0) return false;
            lobby = new CSteamID(id);
            return true;
        }

        // ---------------------------------------------------------------- packets

        public static bool Send(CSteamID to, byte[] data, int length, bool reliable, int channel)
        {
            if (!Available) return false;
            EP2PSend mode = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay;
            try
            {
                bool ok = SteamNetworking.SendP2PPacket(to, data, (uint)length, mode, channel);
                if (!ok) Log.Debug("SendP2PPacket failed to " + to.m_SteamID + " len " + length);
                return ok;
            }
            catch (Exception e)
            {
                Log.ExceptionOnce("SendP2PPacket", e);
                return false;
            }
        }

        public delegate void PacketHandler(CSteamID from, int channel, byte[] data, int length);

        public static void Poll(PacketHandler handler)
        {
            if (!Available) return;
            for (int ch = 0; ch < Channel.Count; ch++)
            {
                uint size;
                int guard = 0;
                while (guard++ < 4096 && SteamNetworking.IsP2PPacketAvailable(out size, ch))
                {
                    if (size > readBuffer.Length) readBuffer = new byte[Math.Max(size, (uint)readBuffer.Length * 2)];
                    uint read;
                    CSteamID from;
                    if (!SteamNetworking.ReadP2PPacket(readBuffer, (uint)readBuffer.Length, out read, out from, ch)) break;
                    if (AcceptPeer != null && !AcceptPeer(from)) continue;
                    try { handler(from, ch, readBuffer, (int)read); }
                    catch (Exception e) { Log.Exception("Packet from " + from.m_SteamID + " on channel " + ch, e); }
                }
            }
        }

        public static void ClosePeer(CSteamID id)
        {
            try { SteamNetworking.CloseP2PSessionWithUser(id); } catch { }
        }

        // ---------------------------------------------------------------- callbacks

        static void OnSessionRequest(P2PSessionRequest_t req)
        {
            CSteamID id = req.m_steamIDRemote;
            if (AcceptPeer != null && AcceptPeer(id))
            {
                SteamNetworking.AcceptP2PSessionWithUser(id);
                Log.Info("Accepted P2P session from " + id.m_SteamID);
            }
            else
            {
                // The lobby member list may not have caught up yet; re-check for a few seconds.
                pendingSessions[id.m_SteamID] = UnityEngine.Time.realtimeSinceStartup;
            }
        }

        static void OnConnectFail(P2PSessionConnectFail_t fail)
        {
            string reason = "P2P error " + fail.m_eP2PSessionError;
            switch (fail.m_eP2PSessionError)
            {
                case 1: reason = "the other player is not running the game"; break;
                case 2: reason = "the other player has no connection to Steam"; break;
                case 3: reason = "connection timed out"; break;
                case 4: reason = "the Steam servers could not relay the connection"; break;
            }
            Log.Warn("Connection to " + fail.m_steamIDRemote.m_SteamID + " failed: " + reason);
            if (P2PFailed != null) P2PFailed(fail.m_steamIDRemote, reason);
        }

        static void OnLobbyJoinRequested(GameLobbyJoinRequested_t req)
        {
            Log.Info("Steam join request for lobby " + req.m_steamIDLobby.m_SteamID + " (friend " + req.m_steamIDFriend.m_SteamID + ")");
            if (JoinRequested != null) JoinRequested(req.m_steamIDLobby);
        }

        static void OnRichPresenceJoin(GameRichPresenceJoinRequested_t req)
        {
            CSteamID lobby;
            if (TryParseConnect(req.m_rgchConnect, out lobby))
            {
                Log.Info("Steam rich presence join request for lobby " + lobby.m_SteamID);
                if (JoinRequested != null) JoinRequested(lobby);
            }
        }

        static void OnChatUpdate(LobbyChatUpdate_t upd)
        {
            if (upd.m_ulSteamIDLobby != Lobby.m_SteamID) return;
            // EChatMemberStateChange: 1 entered, 2 left, 4 disconnected, 8 kicked, 16 banned
            bool joined = (upd.m_rgfChatMemberStateChange & 1u) != 0;
            CSteamID user = new CSteamID(upd.m_ulSteamIDUserChanged);
            if (LobbyMemberChanged != null) LobbyMemberChanged(user, joined);
        }

        static void OnLobbyCreated(LobbyCreated_t res, bool ioFailure)
        {
            if (ioFailure || res.m_eResult != EResult.k_EResultOK)
            {
                string why = ioFailure ? "Steam did not answer" : res.m_eResult.ToString();
                Log.Warn("Could not create lobby: " + why);
                if (LobbyCreateFailed != null) LobbyCreateFailed(why);
                return;
            }
            Lobby = new CSteamID(res.m_ulSteamIDLobby);
            Log.Info("Lobby created " + Lobby.m_SteamID);
            if (LobbyCreated != null) LobbyCreated(Lobby);
        }

        static void OnLobbyEnter(LobbyEnter_t res, bool ioFailure)
        {
            if (ioFailure || res.m_EChatRoomEnterResponse != 1)
            {
                string why;
                if (ioFailure) why = "Steam did not answer";
                else switch (res.m_EChatRoomEnterResponse)
                    {
                        case 2: why = "the game no longer exists"; break;
                        case 3: why = "you are not allowed to join"; break;
                        case 4: why = "the game is full"; break;
                        default: why = "Steam refused (" + res.m_EChatRoomEnterResponse + ")"; break;
                    }
                Log.Warn("Could not join lobby: " + why);
                if (LobbyEnterFailed != null) LobbyEnterFailed(why);
                return;
            }
            Lobby = new CSteamID(res.m_ulSteamIDLobby);
            CSteamID owner = SteamMatchmaking.GetLobbyOwner(Lobby);
            Log.Info("Entered lobby " + Lobby.m_SteamID + " owned by " + owner.m_SteamID);
            if (LobbyEntered != null) LobbyEntered(Lobby, owner);
        }
    }
}
