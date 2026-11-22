using System;
using System.Collections.Generic;
using MSCMP.Avatars;
using Steamworks;
using UnityEngine;

namespace MSCMP.Net
{
    internal enum Role { None, Host, Client }

    /// <summary>
    /// The multiplayer session: who is connected, the handshake, keep-alives and
    /// message routing. The host is authoritative; clients only ever talk to the host,
    /// which relays to everybody else.
    /// </summary>
    internal static class Session
    {
        public delegate void Handler(NetPlayer from, NetReader r);

        public static Role Role { get; private set; }
        /// <summary>The role this session had just before Reset() cleared it (for Session.Ended handlers).</summary>
        public static Role LastRole { get; private set; }
        public static bool Active { get { return Role != Role.None; } }
        public static bool IsHost { get { return Role == Role.Host; } }
        public static bool IsClient { get { return Role == Role.Client; } }
        /// <summary>True when this game decides what happens in the world: the host, or a player
        /// with no session (who may start hosting later and must remember everything since loading).</summary>
        public static bool IsAuthority { get { return Role != Role.Client; } }
        /// <summary>Our player slot (0 = host, also used when playing alone).</summary>
        public static byte LocalSlot { get { return Local != null ? Local.Slot : (byte)0; } }
        /// <summary>True once a client has been accepted by the host (always true for a host).</summary>
        public static bool Connected { get; private set; }

        public static NetPlayer Local { get; private set; }
        public static CSteamID HostId { get; private set; }
        public static string Status = "";
        /// <summary>Client side: what the host is doing.</summary>
        public static bool HostInGame;
        /// <summary>Host: nobody new may join (players who were already here may come back).</summary>
        public static bool Locked;
        static readonly HashSet<ulong> members = new HashSet<ulong>();

        // Host migration: when the host's game is gone, the next player takes over.
        /// <summary>True while we are switching from client to host (nothing should send us home).</summary>
        public static bool TakingOver { get; private set; }
        static readonly HashSet<ulong> rejoiners = new HashSet<ulong>();
        static CSteamID rejoinHost;
        static float rejoinAt, rejoinGiveUp;
        static bool directJoin;
        public static int MaxPlayers = 4;

        static readonly NetPlayer[] slots = new NetPlayer[Protocol.MaxPlayers];
        static readonly Dictionary<ulong, NetPlayer> bySteam = new Dictionary<ulong, NetPlayer>();
        static readonly Handler[] handlers = new Handler[256];
        static readonly NetWriter writer = new NetWriter(2048);
        static readonly NetReader reader = new NetReader();

        static float nextPing, nextHello, joinStarted;
        static CSteamID pendingJoinLobby;

        public static event Action<NetPlayer> PlayerJoined;
        public static event Action<NetPlayer> PlayerLeft;
        public static event Action<string> Ended;
        public static event Action Started;          // host: lobby open / client: welcomed
        public static event Action<NetPlayer, string> ChatReceived;

        // My Summer Car freezes for up to a minute or more while it loads the world, and a
        // frozen game cannot answer pings. Real disconnects are reported by Steam itself
        // (lobby leave / P2P failure), so these timeouts are deliberately generous.
        const float TimeoutSeconds = 60f;
        const float LoadingTimeoutSeconds = 300f;
        const float HelloTimeout = 150f;
        static float lastUpdate;

        // ------------------------------------------------------------ setup

        public static void Init()
        {
            SteamNet.AcceptPeer = AcceptPeer;
            SteamNet.LobbyCreated += OnLobbyCreated;
            SteamNet.LobbyCreateFailed += why => { Notifications.Show("Could not host: " + why); Reset("hosting failed"); };
            SteamNet.LobbyEntered += OnLobbyEntered;
            SteamNet.LobbyEnterFailed += why => { Notifications.Show("Could not join: " + why); Reset("join failed"); };
            SteamNet.JoinRequested += OnJoinRequested;
            SteamNet.LobbyMemberChanged += OnLobbyMemberChanged;
            SteamNet.P2PFailed += OnP2PFailed;

            On(Msg.Hello, OnHello);
            On(Msg.Welcome, OnWelcome);
            On(Msg.Reject, OnReject);
            On(Msg.PlayerJoined, OnPlayerJoinedMsg);
            On(Msg.PlayerLeft, OnPlayerLeftMsg);
            On(Msg.Ping, OnPing);
            On(Msg.Pong, OnPong);
            On(Msg.Chat, OnChat);
            On(Msg.Appearance, OnAppearance);
            On(Msg.Kick, OnKick);
            On(Msg.PlayerPhase, OnPlayerPhase);
            On(Msg.Notice, OnNotice);
            On(Msg.Batch, OnBatch);
        }

        public static void On(Msg msg, Handler handler) { handlers[(byte)msg] = handler; }

        public static IEnumerable<NetPlayer> Players
        {
            get { for (int i = 0; i < slots.Length; i++) if (slots[i] != null) yield return slots[i]; }
        }

        public static IEnumerable<NetPlayer> Remotes
        {
            get { for (int i = 0; i < slots.Length; i++) if (slots[i] != null && !slots[i].IsLocal) yield return slots[i]; }
        }

        public static int PlayerCount
        {
            get { int n = 0; for (int i = 0; i < slots.Length; i++) if (slots[i] != null) n++; return n; }
        }

        public static NetPlayer BySlot(int slot)
        {
            return slot >= 0 && slot < slots.Length ? slots[slot] : null;
        }

        static NetPlayer BySteam(CSteamID id)
        {
            NetPlayer p;
            return bySteam.TryGetValue(id.m_SteamID, out p) ? p : null;
        }

        // ------------------------------------------------------------ public actions

        public static void Host()
        {
            if (Active) return;
            if (!SteamNet.Init()) { Notifications.Show(SteamNet.Problem); return; }
            Role = Role.Host;
            Connected = false;
            Status = "Opening a Steam lobby...";
            SteamNet.CreateLobby(MaxPlayers);
        }

        public static void Join(CSteamID lobby)
        {
            if (!SteamNet.Init()) { Notifications.Show(SteamNet.Problem); return; }
            if (Active)
            {
                if (SteamNet.Lobby == lobby) return;
                Leave("joining another game");
            }
            Role = Role.Client;
            Connected = false;
            joinStarted = Time.realtimeSinceStartup;
            Status = "Joining your friend's game...";
            SteamNet.JoinLobby(lobby);
        }

        // ------------------------------------------------------------ host migration

        /// <summary>Client: the host's game is gone. The remaining player with the lowest slot
        /// keeps playing and becomes the host; everyone else reconnects to them.</summary>
        static bool TryTakeOver()
        {
            if (Local == null) return false;
            NetPlayer heir = null;
            List<ulong> others = new List<ulong>();
            foreach (NetPlayer p in Players)
            {
                if (p.IsHost) continue;
                if (heir == null || p.Slot < heir.Slot) heir = p;
                if (!p.IsLocal) others.Add(p.SteamId.m_SteamID);
            }
            if (heir == null) return false;
            NetPlayer oldHost = BySlot(0);
            string oldName = oldHost != null ? oldHost.Name : "The host";
            if (heir.IsLocal)
            {
                if (!MP.InGame) return false; // nothing to host from
                Log.Info("Host lost: taking over as host.");
                TakingOver = true;
                try { Reset("host lost - taking over"); }
                finally { TakingOver = false; }
                foreach (ulong id in others) rejoiners.Add(id);
                Saves.SaveSync.PrepareTakeover();
                Notifications.Show(oldName + "'s game is gone. You are the host now - your friends are reconnecting.");
                Host();
                return true;
            }
            if (!heir.SteamId.IsValid()) return false;
            rejoinHost = heir.SteamId;
            rejoinAt = Time.realtimeSinceStartup + 12f;
            rejoinGiveUp = Time.realtimeSinceStartup + 120f;
            Notifications.Show(oldName + "'s game is gone. " + heir.Name + " takes over - reconnecting in a moment.");
            Reset("host lost - " + heir.Name + " takes over");
            return true;
        }

        /// <summary>Joins a player directly (after a host migration: no lobby invite needed).</summary>
        public static void JoinDirect(CSteamID host)
        {
            if (Active || !SteamNet.Init()) return;
            Role = Role.Client;
            Connected = false;
            HostId = host;
            directJoin = true;
            joinStarted = Time.realtimeSinceStartup;
            nextHello = 0f;
            Status = "Reconnecting to " + SteamNet.NameOf(host) + "...";
            Log.Info("Reconnecting directly to " + host.m_SteamID);
        }

        static void UpdateRejoin()
        {
            if (rejoinAt <= 0f) return;
            float now = Time.realtimeSinceStartup;
            if (Active || now > rejoinGiveUp) { rejoinAt = 0f; return; }
            if (now < rejoinAt || !MP.InMenu) return;
            rejoinAt = 0f;
            JoinDirect(rejoinHost);
        }

        /// <summary>Leaves the session. Host: ends it for everybody.</summary>
        public static void Leave(string reason)
        {
            if (!Active) return;
            Log.Info("Leaving session: " + reason);
            if (IsHost)
            {
                NetWriter w = Begin(Msg.Kick);
                w.String("The host closed the game (" + reason + ")");
                Broadcast(true, Channel.Reliable);
            }
            else if (Connected)
            {
                Begin(Msg.PlayerLeft).Byte(Local != null ? Local.Slot : (byte)255);
                SendToHost(true, Channel.Reliable);
            }
            Reset(reason);
        }

        static void Reset(string reason)
        {
            Locked = false;
            members.Clear();
            rejoiners.Clear();
            directJoin = false;
            bool wasActive = Active;
            List<NetPlayer> remotes = new List<NetPlayer>(Remotes);
            foreach (NetPlayer p in remotes)
            {
                SteamNet.ClosePeer(p.SteamId);
                RemovePlayer(p, false);
            }
            if (HostId.m_SteamID != 0 && IsClient) SteamNet.ClosePeer(HostId);
            SteamNet.LeaveLobby();
            Array.Clear(slots, 0, slots.Length);
            bySteam.Clear();
            LastRole = Role;
            Role = Role.None;
            Connected = false;
            HostId = CSteamID.Nil;
            HostInGame = false;
            Local = null;
            Status = "";
            if (wasActive && Ended != null)
            {
                try { Ended(reason); } catch (Exception e) { Log.Exception("Session ended handler", e); }
            }
        }

        public static void SendChat(string text)
        {
            if (!Connected || string.IsNullOrEmpty(text)) return;
            text = text.Trim();
            if (text.Length == 0) return;
            if (text.Length > 200) text = text.Substring(0, 200);
            NetWriter w = Begin(Msg.Chat);
            w.Byte(Local.Slot);
            w.String(text);
            if (IsHost) Broadcast(true, Channel.Reliable);
            else SendToHost(true, Channel.Reliable);
            // The host relays to everybody except the sender, so show our own line here.
            if (ChatReceived != null) ChatReceived(Local, text);
        }

        public static void SendAppearance(Appearance look)
        {
            if (Local == null) return;
            Local.Look = look;
            if (!Connected) return;
            NetWriter w = Begin(Msg.Appearance);
            w.Byte(Local.Slot);
            look.Write(w);
            if (IsHost) Broadcast(true, Channel.Reliable); else SendToHost(true, Channel.Reliable);
        }

        public static void SetLocalPhase(PlayerPhase phase)
        {
            if (Local == null || Local.Phase == phase) return;
            Local.Phase = phase;
            if (!Connected) return;
            NetWriter w = Begin(Msg.PlayerPhase);
            w.Byte(Local.Slot);
            w.Byte((byte)phase);
            if (IsHost) Broadcast(true, Channel.Reliable); else SendToHost(true, Channel.Reliable);
        }

        public static void Kick(NetPlayer p, string reason)
        {
            if (!IsHost || p == null || p.IsLocal) return;
            NetWriter w = Begin(Msg.Kick);
            w.String(reason);
            SendTo(p, true, Channel.Reliable);
            RemovePlayer(p, true);
        }

        // ------------------------------------------------------------ sending

        /// <summary>Starts a new outgoing message in the shared writer.</summary>
        public static NetWriter Begin(Msg msg)
        {
            writer.Reset();
            writer.Byte((byte)msg);
            return writer;
        }

        public static void SendTo(NetPlayer p, bool reliable, int channel)
        {
            if (p == null || p.IsLocal) return;
            SteamNet.Send(p.SteamId, writer.Buffer, writer.Length, reliable, channel);
        }

        public static void SendToHost(bool reliable, int channel)
        {
            if (!IsClient || HostId.m_SteamID == 0) return;
            SteamNet.Send(HostId, writer.Buffer, writer.Length, reliable, channel);
        }

        /// <summary>Host: send the current message to every connected client (optionally except one).</summary>
        public static void Broadcast(bool reliable, int channel, NetPlayer except = null)
        {
            if (!IsHost) return;
            for (int i = 1; i < slots.Length; i++)
            {
                NetPlayer p = slots[i];
                if (p == null || p == except || p.IsLocal) continue;
                SteamNet.Send(p.SteamId, writer.Buffer, writer.Length, reliable, channel);
            }
        }

        /// <summary>Host: send to every client that is in the GAME scene and synced.</summary>
        public static void BroadcastInGame(bool reliable, int channel, NetPlayer except = null)
        {
            if (!IsHost) return;
            for (int i = 1; i < slots.Length; i++)
            {
                NetPlayer p = slots[i];
                if (p == null || p == except || p.IsLocal) continue;
                if (p.Phase != PlayerPhase.Playing && p.Phase != PlayerPhase.Syncing) continue;
                SteamNet.Send(p.SteamId, writer.Buffer, writer.Length, reliable, channel);
            }
        }

        /// <summary>Client: to host. Host: to all clients. Convenience for "tell everybody".</summary>
        public static void SendToAll(bool reliable, int channel)
        {
            if (IsHost) BroadcastInGame(reliable, channel);
            else SendToHost(reliable, channel);
        }

        // ------------------------------------------------------------ update

        public static void Update()
        {
            SteamNet.RunCallbacks();
            if (!SteamNet.Available) return;

            SteamNet.Poll(OnPacket);
            UpdateRejoin();
            if (!Active) return;

            float now = Time.realtimeSinceStartup;

            // If *we* were frozen (loading), nobody could reach us: don't blame them.
            float gap = now - lastUpdate;
            lastUpdate = now;
            if (gap > 2f)
            {
                foreach (NetPlayer p in Remotes) p.LastHeard += gap;
                joinStarted += gap;
            }

            if (IsClient && !Connected && (SteamNet.InLobby || directJoin))
            {
                if (now >= nextHello)
                {
                    nextHello = now + 2f;
                    SendHello();
                }
                if (now - joinStarted > 20f && HostId.m_SteamID != 0)
                    Status = "Waiting for " + SteamNet.NameOf(HostId) + " to answer (their game may be loading)...";
                if (now - joinStarted > HelloTimeout)
                {
                    Notifications.Show("Could not reach the host over Steam. Ask them to invite you again.");
                    Leave("handshake timeout");
                    return;
                }
            }

            if (now >= nextPing)
            {
                nextPing = now + 1f;
                foreach (NetPlayer p in new List<NetPlayer>(Remotes))
                {
                    if (IsClient && !p.IsHost) continue;
                    NetWriter w = Begin(Msg.Ping);
                    w.Float(now);
                    SendTo(p, false, Channel.Reliable);
                    if (now - p.LastHeard > TimeoutFor(p))
                    {
                        if (IsHost)
                        {
                            Notifications.Show(p.Name + " lost connection.");
                            RemovePlayer(p, true);
                        }
                        else
                        {
                            if (Options.HostMigration && TryTakeOver()) return;
                            Notifications.Show("Lost connection to the host.");
                            Leave("host timeout");
                            return;
                        }
                    }
                }
            }
        }

        /// <summary>How long a player may stay silent: much longer while they may be loading.</summary>
        static float TimeoutFor(NetPlayer p)
        {
            bool mayBeLoading = p.Phase == PlayerPhase.Loading || p.Phase == PlayerPhase.SaveReady ||
                                p.Phase == PlayerPhase.Syncing || p.Phase == PlayerPhase.InMenu || p.Phase == PlayerPhase.Connecting;
            if (IsClient && p.IsHost && (MP.InMenu || !HostInGame)) mayBeLoading = true;
            return mayBeLoading ? LoadingTimeoutSeconds : TimeoutSeconds;
        }

        static void OnPacket(CSteamID from, int channel, byte[] data, int length)
        {
            if (length < 1) return;
            byte id = data[0];
            Handler h = handlers[id];
            if (h == null) { Log.Debug("Unhandled message " + id); return; }

            NetPlayer sender = BySteam(from);
            if (sender == null)
            {
                // Only Hello may come from strangers (host side), and only Welcome/Reject before we are accepted.
                bool allowed = (IsHost && id == (byte)Msg.Hello)
                    || (IsClient && from == HostId && (id == (byte)Msg.Welcome || id == (byte)Msg.Reject || id == (byte)Msg.Kick));
                if (!allowed) return;
            }
            else sender.LastHeard = Time.realtimeSinceStartup;

            if (IsClient && from != HostId) return; // clients only listen to the host

            reader.Set(data, 1, length - 1);
            lastSender = from;
            try
            {
                h(sender, reader);
            }
            catch (FormatException e)
            {
                Log.Warn("Malformed message " + (Msg)id + " from " + from.m_SteamID + ": " + e.Message);
            }
        }

        /// <summary>Client: unpacks a batch from the host and handles each message in order.</summary>
        static void OnBatch(NetPlayer from, NetReader r)
        {
            if (from == null) return;
            int n = r.UShort();
            for (int i = 0; i < n; i++)
            {
                byte[] sub = r.Bytes(Protocol.MaxReliablePacket);
                if (sub.Length < 1) continue;
                Handler h = handlers[sub[0]];
                if (h == null || sub[0] == (byte)Msg.Batch) continue;
                NetReader sr = new NetReader();
                sr.Set(sub, 1, sub.Length - 1);
                try { h(from, sr); }
                catch (FormatException e) { Log.Warn("Malformed batched message " + (Msg)sub[0] + ": " + e.Message); }
                catch (Exception e) { Log.ExceptionOnce("Batched " + (Msg)sub[0], e); }
            }
        }

        static bool AcceptPeer(CSteamID id)
        {
            if (!Active) return false;
            if (IsClient) return id == HostId;
            // Host: anyone who is in our lobby (friends-only) may connect.
            return BySteam(id) != null || SteamNet.IsLobbyMember(id) || rejoiners.Contains(id.m_SteamID);
        }

        // ------------------------------------------------------------ lobby events

        static void OnLobbyCreated(CSteamID lobby)
        {
            if (!IsHost) { SteamNet.LeaveLobby(); return; }
            SteamNet.SetLobbyData(Protocol.LobbyKey, "1");
            SteamNet.SetLobbyData(Protocol.LobbyVersionKey, Protocol.Version.ToString());
            SteamNet.SetLobbyData("name", SteamNet.LocalName + "'s game");
            SteamNet.SetRichPresenceConnect();

            Local = new NetPlayer
            {
                Slot = 0,
                SteamId = SteamNet.LocalId,
                Name = SteamNet.LocalName,
                Look = Options.LocalAppearance(),
                IsLocal = true,
                LastHeard = Time.realtimeSinceStartup,
                Phase = MP.InGame ? PlayerPhase.Playing : PlayerPhase.InMenu,
            };
            AddPlayer(Local);
            HostId = SteamNet.LocalId;
            Connected = true;
            Status = "Hosting. Invite friends from the Steam overlay.";
            Log.Console("Hosting a multiplayer game. Invite your friend with the Invite button (Steam overlay).");
            if (Started != null) Started();
            SteamNet.OpenInviteDialog();
        }

        static void OnLobbyEntered(CSteamID lobby, CSteamID owner)
        {
            if (IsHost) return; // our own lobby
            if (!IsClient) { SteamNet.LeaveLobby(); return; }
            string marker = SteamNet.GetLobbyData(Protocol.LobbyKey);
            if (marker != "1")
            {
                Notifications.Show("That Steam game is not an MSC Multiplayer session.");
                Reset("not an MSCMP lobby");
                return;
            }
            string ver = SteamNet.GetLobbyData(Protocol.LobbyVersionKey);
            if (ver != Protocol.Version.ToString())
            {
                Notifications.Show("Version mismatch: host uses protocol " + ver + ", you use " + Protocol.Version + ". Both players need the same MSCMP version.");
                Reset("version mismatch");
                return;
            }
            HostId = owner;
            Status = "Connecting to " + SteamNet.NameOf(owner) + "...";
            nextHello = 0f;
            joinStarted = Time.realtimeSinceStartup;
        }

        static void OnJoinRequested(CSteamID lobby)
        {
            if (IsHost && Session.Connected && SteamNet.Lobby == lobby) return;
            if (MP.InGame && !(IsClient && SteamNet.Lobby == lobby))
            {
                // Joining requires loading the host's world from the main menu.
                pendingJoinLobby = lobby;
                Notifications.Show("Join request received. Quit to the main menu (save at a toilet) and you will join automatically.");
                return;
            }
            Join(lobby);
        }

        /// <summary>Called when the main menu loads; continues a join requested while in-game.</summary>
        public static void OnMenu()
        {
            if (pendingJoinLobby.m_SteamID != 0)
            {
                CSteamID lobby = pendingJoinLobby;
                pendingJoinLobby = CSteamID.Nil;
                Join(lobby);
            }
        }

        public static void JoinFromCommandLine()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                {
                    string text = args[i];
                    if (string.Equals(text, "+connect_lobby", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                        text = text + " " + args[i + 1];
                    CSteamID lobby;
                    if (SteamNet.TryParseConnect(text, out lobby))
                    {
                        Log.Info("Joining lobby from launch arguments: " + lobby.m_SteamID);
                        Join(lobby);
                        return;
                    }
                }
            }
            catch (Exception e) { Log.Exception("Command line join", e); }
        }

        static void OnLobbyMemberChanged(CSteamID user, bool joined)
        {
            if (joined) return;
            if (IsClient && user == HostId)
            {
                Notifications.Show("The host left the game.");
                Reset("host left");
                return;
            }
            if (IsHost)
            {
                NetPlayer p = BySteam(user);
                if (p != null)
                {
                    Notifications.Show(p.Name + " left the game.");
                    RemovePlayer(p, true);
                }
            }
        }

        static void OnP2PFailed(CSteamID user, string reason)
        {
            if (IsClient && user == HostId)
            {
                if (Options.HostMigration && TryTakeOver()) return;
                Notifications.Show("Connection to the host failed: " + reason);
                Reset("p2p failure");
            }
            else if (IsHost)
            {
                NetPlayer p = BySteam(user);
                if (p != null)
                {
                    Notifications.Show(p.Name + " disconnected (" + reason + ").");
                    RemovePlayer(p, true);
                }
            }
        }

        // ------------------------------------------------------------ player list

        static void AddPlayer(NetPlayer p)
        {
            slots[p.Slot] = p;
            bySteam[p.SteamId.m_SteamID] = p;
            members.Add(p.SteamId.m_SteamID);
        }

        static void RemovePlayer(NetPlayer p, bool announce)
        {
            if (p == null) return;
            if (slots[p.Slot] == p) slots[p.Slot] = null;
            bySteam.Remove(p.SteamId.m_SteamID);
            if (!p.IsLocal) SteamNet.ClosePeer(p.SteamId);
            if (announce && IsHost)
            {
                Begin(Msg.PlayerLeft).Byte(p.Slot);
                Broadcast(true, Channel.Reliable);
            }
            if (PlayerLeft != null)
            {
                try { PlayerLeft(p); } catch (Exception e) { Log.Exception("PlayerLeft handler", e); }
            }
        }

        static byte FreeSlot()
        {
            int max = Math.Min(Math.Max(MaxPlayers, 2), slots.Length);
            for (byte i = 1; i < max; i++) if (slots[i] == null) return i;
            return 255;
        }

        // ------------------------------------------------------------ handshake

        static void SendHello()
        {
            NetWriter w = Begin(Msg.Hello);
            w.UShort(Protocol.Version);
            w.String(BuildInfo.Version);
            w.Int(MP.GameBuildId);
            w.String(SteamNet.LocalName);
            Options.LocalAppearance().Write(w);
            SendToHost(true, Channel.Reliable);
        }

        static void OnHello(NetPlayer known, NetReader r)
        {
            if (!IsHost) return;
            CSteamID from = lastSender;
            ushort proto = r.UShort();
            string modVersion = r.String();
            int build = r.Int();
            string name = r.String();
            Appearance look = Appearance.Read(r);

            if (known != null)
            {
                // Duplicate hello (retry); answer again.
                SendWelcome(known);
                return;
            }
            if (proto != Protocol.Version)
            {
                RejectStranger(from, "Version mismatch: host runs MSCMP " + BuildInfo.Version + ", you run " + modVersion + ". Both players need the same version.");
                return;
            }
            if (Locked && !members.Contains(from.m_SteamID))
            {
                RejectStranger(from, "The host has locked this game.");
                return;
            }
            byte slot = FreeSlot();
            if (slot == 255)
            {
                RejectStranger(from, "The game is full.");
                return;
            }
            if (build != 0 && MP.GameBuildId != 0 && build != MP.GameBuildId)
            {
                Notifications.Show(name + " has a different My Summer Car version (" + build + " vs " + MP.GameBuildId + "). Things may desync - update both games.");
            }

            NetPlayer p = new NetPlayer
            {
                Slot = slot,
                SteamId = from,
                Name = SanitizeName(name),
                Look = look,
                Phase = PlayerPhase.InMenu,
                LastHeard = Time.realtimeSinceStartup,
                HelloTime = Time.realtimeSinceStartup,
            };
            AddPlayer(p);
            SendWelcome(p);

            NetWriter w = Begin(Msg.PlayerJoined);
            WritePlayer(w, p);
            Broadcast(true, Channel.Reliable, p);

            Notifications.Show(p.Name + " joined.");
            Log.Info("Player joined: " + p.Describe() + " look " + look);
            if (PlayerJoined != null)
            {
                try { PlayerJoined(p); } catch (Exception e) { Log.Exception("PlayerJoined handler", e); }
            }
        }

        static void SendWelcome(NetPlayer p)
        {
            NetWriter w = Begin(Msg.Welcome);
            w.Byte(p.Slot);
            w.Bool(MP.InGame);
            int count = 0;
            foreach (NetPlayer q in Players) count++;
            w.Byte((byte)count);
            foreach (NetPlayer q in Players) WritePlayer(w, q);
            SendTo(p, true, Channel.Reliable);
        }

        static void RejectStranger(CSteamID to, string reason)
        {
            NetWriter w = Begin(Msg.Reject);
            w.String(reason);
            SteamNet.Send(to, w.Buffer, w.Length, true, Channel.Reliable);
            Log.Info("Rejected " + to.m_SteamID + ": " + reason);
        }

        static void WritePlayer(NetWriter w, NetPlayer p)
        {
            w.Byte(p.Slot);
            w.ULong(p.SteamId.m_SteamID);
            w.String(p.Name);
            p.Look.Write(w);
            w.Byte((byte)p.Phase);
        }

        static NetPlayer ReadPlayer(NetReader r)
        {
            NetPlayer p = new NetPlayer();
            p.Slot = r.Byte();
            p.SteamId = new CSteamID(r.ULong());
            p.Name = SanitizeName(r.String());
            p.Look = Appearance.Read(r);
            p.Phase = (PlayerPhase)r.Byte();
            p.LastHeard = Time.realtimeSinceStartup;
            if (p.Slot >= Protocol.MaxPlayers) throw new FormatException("Bad slot");
            return p;
        }

        static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Player";
            name = name.Replace('<', '(').Replace('>', ')').Trim();
            if (name.Length > 32) name = name.Substring(0, 32);
            return name.Length == 0 ? "Player" : name;
        }

        static void OnWelcome(NetPlayer from, NetReader r)
        {
            if (!IsClient || Connected) return;
            byte mySlot = r.Byte();
            HostInGame = r.Bool();
            int count = r.Byte();
            Array.Clear(slots, 0, slots.Length);
            bySteam.Clear();
            for (int i = 0; i < count; i++)
            {
                NetPlayer p = ReadPlayer(r);
                if (p.Slot == mySlot)
                {
                    p.IsLocal = true;
                    p.SteamId = SteamNet.LocalId;
                    p.Look = Options.LocalAppearance();
                    Local = p;
                }
                AddPlayer(p);
            }
            if (Local == null) throw new FormatException("Welcome without our slot");
            NetPlayer host = BySlot(0);
            if (host != null) host.SteamId = HostId;
            Connected = true;
            Status = "Connected to " + (host != null ? host.Name : "host");
            Notifications.Show("Connected to " + (host != null ? host.Name : "the host") + "'s game.");
            Log.Info("Welcomed as slot " + mySlot + "; host in game: " + HostInGame);
            if (Started != null) Started();
            foreach (NetPlayer p in Remotes)
                if (PlayerJoined != null) PlayerJoined(p);
        }

        static void OnReject(NetPlayer from, NetReader r)
        {
            if (!IsClient) return;
            string reason = r.String();
            Notifications.Show("The host refused the connection: " + reason);
            Reset("rejected: " + reason);
        }

        static void OnKick(NetPlayer from, NetReader r)
        {
            if (!IsClient) return;
            string reason = r.String();
            Notifications.Show(reason);
            Reset(reason);
        }

        static void OnPlayerJoinedMsg(NetPlayer from, NetReader r)
        {
            if (!IsClient || !Connected) return;
            NetPlayer p = ReadPlayer(r);
            if (Local != null && p.Slot == Local.Slot) return;
            NetPlayer old = BySlot(p.Slot);
            if (old != null) RemovePlayer(old, false);
            AddPlayer(p);
            Notifications.Show(p.Name + " joined.");
            if (PlayerJoined != null) PlayerJoined(p);
        }

        static void OnPlayerLeftMsg(NetPlayer from, NetReader r)
        {
            byte slot = r.Byte();
            if (IsHost)
            {
                // A client telling us it is leaving.
                if (from != null && !from.IsLocal)
                {
                    Notifications.Show(from.Name + " left the game.");
                    RemovePlayer(from, true);
                }
                return;
            }
            NetPlayer p = BySlot(slot);
            if (p != null && !p.IsLocal)
            {
                Notifications.Show(p.Name + " left the game.");
                RemovePlayer(p, false);
            }
        }

        static void OnPing(NetPlayer from, NetReader r)
        {
            if (from == null) return;
            float t = r.Float();
            NetWriter w = Begin(Msg.Pong);
            w.Float(t);
            w.Float(Time.realtimeSinceStartup);
            SendTo(from, false, Channel.Reliable);
        }

        static void OnPong(NetPlayer from, NetReader r)
        {
            if (from == null) return;
            float sent = r.Float();
            float remote = r.Float();
            from.OnPong(sent, remote, Time.realtimeSinceStartup);
        }

        static void OnChat(NetPlayer from, NetReader r)
        {
            byte slot = r.Byte();
            string text = r.String();
            if (text.Length > 200) text = text.Substring(0, 200);
            NetPlayer speaker;
            if (IsHost)
            {
                if (from == null) return;
                speaker = from; // never trust the slot a client claims
                NetWriter w = Begin(Msg.Chat);
                w.Byte(from.Slot);
                w.String(text);
                Broadcast(true, Channel.Reliable, from);
            }
            else speaker = BySlot(slot);
            if (speaker != null && ChatReceived != null) ChatReceived(speaker, text);
        }

        static void OnAppearance(NetPlayer from, NetReader r)
        {
            byte slot = r.Byte();
            Appearance look = Appearance.Read(r);
            NetPlayer p = IsHost ? from : BySlot(slot);
            if (p == null || p.IsLocal) return;
            p.Look = look;
            if (p.Avatar != null) p.Avatar.Rebuild(look);
            if (IsHost)
            {
                NetWriter w = Begin(Msg.Appearance);
                w.Byte(p.Slot);
                look.Write(w);
                Broadcast(true, Channel.Reliable, p);
            }
        }

        static void OnPlayerPhase(NetPlayer from, NetReader r)
        {
            byte slot = r.Byte();
            PlayerPhase phase = (PlayerPhase)r.Byte();
            NetPlayer p = IsHost ? from : BySlot(slot);
            if (p == null || p.IsLocal) return;
            p.Phase = phase;
            if (IsHost)
            {
                NetWriter w = Begin(Msg.PlayerPhase);
                w.Byte(p.Slot);
                w.Byte((byte)phase);
                Broadcast(true, Channel.Reliable, p);
            }
        }

        static void OnNotice(NetPlayer from, NetReader r)
        {
            Notifications.Show(r.String());
        }

        public static void SendNotice(NetPlayer to, string text)
        {
            NetWriter w = Begin(Msg.Notice);
            w.String(text);
            SendTo(to, true, Channel.Reliable);
        }

        /// <summary>The Steam id of the packet currently being handled (lets Hello see strangers).</summary>
        static CSteamID lastSender;
    }
}
