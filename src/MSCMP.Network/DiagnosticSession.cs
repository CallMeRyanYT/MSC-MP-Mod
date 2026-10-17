using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;

namespace MSCMP.Network
{
    public sealed class SessionEvent
    {
        public string Kind;
        public string Detail;
        public ulong PlayerId;
        public ulong ConnectionGeneration;
        public uint Sequence;
    }
    public sealed class SessionPeer
    {
        public ulong PlayerId;
        public ulong ConnectionGeneration;
    }
    public sealed class ApplicationMessage
    {
        public ulong PlayerId;
        public ulong ConnectionGeneration;
        public uint Sequence;
        public byte[] Payload;
    }
    public sealed class PoseMessage
    {
        public ulong PlayerId;
        public ulong ConnectionGeneration;
        public uint Sequence;
        public float X, Y, Z, Yaw;
    }

    // Diagnostic session only. Acceptance here never means gameplay readiness.
    public sealed class DiagnosticSession : IDisposable
    {
        private sealed class Peer
        {
            public int Connection;
            public bool Accepted;
            public ulong PlayerId, Generation;
            public Guid Nonce, Token;
            public long Started, DisconnectAt, RateWindow;
            public int MessagesThisWindow;
            public uint ProbeIn, ProbeOut, PoseIn, PoseOut, PingOut, ApplicationIn, ApplicationOut;
            public bool HasPose;
            public long PingTimestamp;
            public uint PendingPing;
        }
        private sealed class ResumeRecord
        {
            public ulong PlayerId;
            public long Expires;
            public bool Active;
        }

        private readonly CompatibilityInfo compatibility;
        private readonly string displayName;
        private readonly INetworkTransport transport;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        // Legacy .NET 3.5 RNGCryptoServiceProvider has no IDisposable contract.
        // Retain one provider for the AppDomain rather than allocating one per token.
        private static readonly RNGCryptoServiceProvider RandomProvider = new RNGCryptoServiceProvider();
        private readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();
        private readonly Dictionary<Guid, ResumeRecord> resumes = new Dictionary<Guid, ResumeRecord>();
        private ulong nextPlayer = 1, nextGeneration;
        private Guid resumeToken;
        private string resumeEndpoint;
        private string endpoint;
        private Guid joinNonce;

        public event Action<SessionEvent> Event;
        public event Action<ApplicationMessage> ApplicationReceived;
        public event Action<PoseMessage> PoseReceived;
        public uint LastSentPoseSequence { get; private set; }
        public string Status { get; private set; }
        public bool IsHost { get; private set; }
        public bool IsRunning { get { return transport.IsRunning; } }
        public int LocalPort { get { return transport.LocalPort; } }
        public int PeerCount { get { return transport.PeerCount; } }
        public int QueueDepth { get { return transport.QueueDepth; } }
        public long SentBytes { get { return transport.SentBytes; } }
        public long ReceivedBytes { get { return transport.ReceivedBytes; } }
        public int LastPingMilliseconds { get; private set; }
        public int ProbesReceived { get; private set; }
        public int PongsReceived { get; private set; }
        public Guid SessionId { get; private set; }
        public ulong SessionGeneration { get; private set; }
        public ulong LocalPlayerId { get; private set; }
        public int AcceptedPeerCount
        {
            get { int count = 0; foreach (Peer peer in peers.Values) if (peer.Accepted) count++; return count; }
        }

        public DiagnosticSession(CompatibilityInfo compatibility, string displayName)
            : this(compatibility, displayName, new LiteNetTransport()) { }

        public DiagnosticSession(CompatibilityInfo compatibility, string displayName, INetworkTransport transport)
        {
            if (compatibility == null || transport == null) throw new ArgumentNullException("compatibility/transport");
            compatibility.Validate(); ProtocolCodec.ValidateText(displayName, 32);
            if (displayName.Length == 0) throw new ArgumentException("Display name cannot be empty.");
            // Capture values; callers cannot mutate the admission policy mid-session.
            this.compatibility = new CompatibilityInfo { ModVersion = compatibility.ModVersion, GameFingerprint = compatibility.GameFingerprint,
                ContentManifest = compatibility.ContentManifest, Capabilities = compatibility.Capabilities };
            this.displayName = displayName; this.transport = transport; Status = "Offline";
        }

        public void Host(int port)
        { Host(port, 0); }

        // A persistent host supplies a generation reserved by its checkpoint store.
        // The random session ID still changes on every start, including copied saves.
        public void Host(int port, ulong reservedGeneration)
        {
            Leave(); IsHost = true; resumes.Clear(); nextPlayer = 1; nextGeneration = 0;
            SessionId = RandomIdentity(); SessionGeneration = BitConverter.ToUInt64(RandomIdentity().ToByteArray(), 0);
            if (SessionGeneration == 0) SessionGeneration = 1;
            if (reservedGeneration != 0) SessionGeneration = reservedGeneration;
            LocalPlayerId = 1; transport.Start(port, 3); Status = "Diagnostic host listening on UDP " + LocalPort;
            Emit("Listening", Status, null, 0);
        }

        public void Join(string ip, int port)
        {
            Leave(); IsHost = false; endpoint = ip + ":" + port;
            if (endpoint != resumeEndpoint) resumeToken = Guid.Empty;
            joinNonce = RandomIdentity(); transport.Start(0, 1);
            try { transport.Connect(ip, port); }
            catch { transport.Stop(); throw; }
            Status = "Connecting diagnostic session";
        }

        public void Poll()
        {
            foreach (TransportEvent e in transport.Poll())
            {
                if (!transport.IsRunning && e.Kind != TransportEventKind.Error) break;
                Peer peer;
                if (e.Kind == TransportEventKind.Connected)
                {
                    if ((!IsHost && peers.Count != 0) || peers.Count >= 3) { transport.Disconnect(e.Connection); continue; }
                    peer = new Peer { Connection = e.Connection, Started = clock.ElapsedMilliseconds, Nonce = joinNonce };
                    peers.Add(e.Connection, peer);
                    if (!IsHost)
                    {
                        WireMessage hello = new WireMessage { Kind = MessageKind.Hello, Compatibility = compatibility, Text = displayName, Nonce = joinNonce, ReconnectToken = resumeToken };
                        Send(peer, hello, false); Status = "Checking compatibility";
                    }
                    continue;
                }
                if (e.Kind == TransportEventKind.Error)
                {
                    if (e.Connection != 0) { Reject(e.Connection, e.Detail); continue; }
                    EndWithReason(e.Detail); break;
                }
                if (!peers.TryGetValue(e.Connection, out peer)) continue;
                if (e.Kind == TransportEventKind.Disconnected)
                {
                    RemovePeer(peer, e.Detail);
                    if (!IsHost) { transport.Stop(); Status = "Disconnected: " + e.Detail; }
                    continue;
                }
                if (e.Kind == TransportEventKind.Data) Receive(peer, e.Data);
            }
            long now = clock.ElapsedMilliseconds;
            foreach (Peer peer in new List<Peer>(peers.Values))
            {
                if (peer.DisconnectAt != 0 && now >= peer.DisconnectAt) { transport.Disconnect(peer.Connection); RemovePeer(peer, "Rejected"); }
                else if (!peer.Accepted && now - peer.Started > 5000) { transport.Disconnect(peer.Connection); RemovePeer(peer, "Handshake timeout"); }
            }
            foreach (Guid token in new List<Guid>(resumes.Keys))
                if (!resumes[token].Active && resumes[token].Expires <= now) resumes.Remove(token);
        }

        private void Receive(Peer peer, byte[] data)
        {
            if (peer.DisconnectAt != 0) return;
            long now = clock.ElapsedMilliseconds;
            if (now - peer.RateWindow >= 1000) { peer.RateWindow = now; peer.MessagesThisWindow = 0; }
            if (++peer.MessagesThisWindow > 256) { Reject(peer.Connection, "Message rate exceeds diagnostic limit"); return; }
            WireMessage m; string error;
            if (!ProtocolCodec.TryDecode(data, out m, out error)) { Reject(peer.Connection, error); return; }
            if (!peer.Accepted)
            {
                if (IsHost && m.Kind == MessageKind.Hello) { AcceptHello(peer, m); return; }
                if (!IsHost && m.Kind == MessageKind.Welcome) { AcceptWelcome(peer, m); return; }
                if (!IsHost && m.Kind == MessageKind.Reject) { EndWithReason("Rejected: " + m.Text); return; }
                Reject(peer.Connection, "Handshake required before messages"); return;
            }
            if (m.SessionId != SessionId || m.SessionGeneration != SessionGeneration || m.ConnectionGeneration != peer.Generation)
            { Emit("Stale", "Discarded stale session/connection generation", peer, m.Sequence); return; }
            switch (m.Kind)
            {
                case MessageKind.Reject:
                    if (!IsHost) EndWithReason("Rejected: " + m.Text);
                    else Reject(peer.Connection, "Client cannot reject host authority");
                    break;
                case MessageKind.Ping:
                    WireMessage pong = Create(peer, MessageKind.Pong); pong.Timestamp = m.Timestamp; pong.Sequence = m.Sequence; Send(peer, pong, false); break;
                case MessageKind.Pong:
                    if (peer.PendingPing == 0 || peer.PendingPing != m.Sequence || m.Timestamp != peer.PingTimestamp) break;
                    peer.PendingPing = 0; LastPingMilliseconds = (int)Math.Min(Int32.MaxValue, Math.Max(0, now - m.Timestamp)); PongsReceived++;
                    Emit("Pong", "Application RTT " + LastPingMilliseconds + " ms", peer, m.Sequence); break;
                case MessageKind.Probe:
                    if (!ProtocolCodec.IsNewer(m.Sequence, peer.ProbeIn)) break;
                    if (m.Sequence != unchecked(peer.ProbeIn + 1)) { Reject(peer.Connection, "Reliable probe sequence gap"); return; }
                    peer.ProbeIn = m.Sequence; ProbesReceived++; Emit("Probe", m.Text, peer, m.Sequence); break;
                case MessageKind.Pose:
                    if (peer.HasPose && !ProtocolCodec.IsNewer(m.Sequence, peer.PoseIn)) break;
                    peer.PoseIn = m.Sequence; peer.HasPose = true;
                    Action<PoseMessage> poseCallback = PoseReceived;
                    if (poseCallback != null) poseCallback(new PoseMessage { PlayerId = peer.PlayerId, ConnectionGeneration = peer.Generation,
                        Sequence = m.Sequence, X = m.X, Y = m.Y, Z = m.Z, Yaw = m.Yaw });
                    break;
                case MessageKind.Application:
                    if (!ProtocolCodec.IsNewer(m.Sequence, peer.ApplicationIn)) break;
                    if (m.Sequence != unchecked(peer.ApplicationIn + 1)) { Reject(peer.Connection, "Reliable application sequence gap"); return; }
                    peer.ApplicationIn = m.Sequence;
                    Action<ApplicationMessage> applicationCallback = ApplicationReceived;
                    if (applicationCallback != null) applicationCallback(new ApplicationMessage { PlayerId = peer.PlayerId,
                        ConnectionGeneration = peer.Generation, Sequence = m.Sequence, Payload = m.Payload });
                    break;
                default: Reject(peer.Connection, "Unexpected message after handshake"); break;
            }
        }

        private void AcceptHello(Peer peer, WireMessage m)
        {
            string mismatch = compatibility.Mismatch(m.Compatibility);
            if (mismatch != null) { Reject(peer.Connection, mismatch); return; }
            if (m.Nonce == Guid.Empty || m.Text.Length == 0) { Reject(peer.Connection, "Invalid client nonce/name"); return; }
            if (nextGeneration == UInt64.MaxValue) { Reject(peer.Connection, "Connection generation exhausted; restart diagnostic host"); return; }
            ulong player;
            if (m.ReconnectToken != Guid.Empty)
            {
                ResumeRecord old;
                if (!resumes.TryGetValue(m.ReconnectToken, out old) || old.Active || old.Expires <= clock.ElapsedMilliseconds)
                { Reject(peer.Connection, "Reconnect token expired or already active; create a fresh diagnostic client"); return; }
                player = old.PlayerId; resumes.Remove(m.ReconnectToken);
            }
            else
            {
                if (resumes.Count >= 16) { Reject(peer.Connection, "Reconnect record capacity reached; wait for expiry"); return; }
                if (nextPlayer == UInt64.MaxValue) { Reject(peer.Connection, "Player identity space exhausted; restart diagnostic host"); return; }
                player = ++nextPlayer;
            }
            peer.PlayerId = player; peer.Generation = ++nextGeneration; peer.Token = RandomIdentity(); peer.Accepted = true;
            resumes.Add(peer.Token, new ResumeRecord { PlayerId = player, Active = true });
            WireMessage welcome = Create(peer, MessageKind.Welcome); welcome.Compatibility = compatibility; welcome.Nonce = m.Nonce;
            welcome.PlayerId = player; welcome.ReconnectToken = peer.Token;
            if (!Send(peer, welcome, false)) { Reject(peer.Connection, "Outgoing queue full during handshake"); return; }
            Status = "Diagnostic host: " + AcceptedPeerCount + " accepted peer(s)";
            Emit("Accepted", "Compatible diagnostic peer; gameplay remains disabled", peer, 0);
        }

        private void AcceptWelcome(Peer peer, WireMessage m)
        {
            string mismatch = compatibility.Mismatch(m.Compatibility);
            if (mismatch != null || m.Nonce != joinNonce || m.SessionId == Guid.Empty || m.SessionGeneration == 0 || m.ConnectionGeneration == 0 || m.PlayerId < 2 || m.ReconnectToken == Guid.Empty)
            { Reject(peer.Connection, mismatch ?? "Invalid host handshake identity"); return; }
            SessionId = m.SessionId; SessionGeneration = m.SessionGeneration; LocalPlayerId = m.PlayerId;
            peer.PlayerId = 1; peer.Generation = m.ConnectionGeneration; peer.Accepted = true;
            resumeToken = m.ReconnectToken; resumeEndpoint = endpoint;
            Status = "Diagnostic connected; gameplay disabled"; Emit("Accepted", Status, peer, 0);
        }

        public bool SendProbe(string text)
        {
            ProtocolCodec.ValidateText(text, 256); bool any = false;
            foreach (Peer peer in peers.Values)
            {
                if (!peer.Accepted || peer.DisconnectAt != 0) continue;
                WireMessage m = Create(peer, MessageKind.Probe); m.Text = text; m.Sequence = unchecked(peer.ProbeOut + 1);
                if (Send(peer, m, false)) { peer.ProbeOut = m.Sequence; any = true; }
            }
            return any;
        }
        public bool SendPing()
        {
            bool any = false;
            foreach (Peer peer in peers.Values)
            {
                if (!peer.Accepted || peer.DisconnectAt != 0) continue;
                WireMessage m = Create(peer, MessageKind.Ping); m.Sequence = ++peer.PingOut; if (m.Sequence == 0) m.Sequence = ++peer.PingOut;
                m.Timestamp = clock.ElapsedMilliseconds;
                if (Send(peer, m, false)) { peer.PendingPing = m.Sequence; peer.PingTimestamp = m.Timestamp; any = true; }
            }
            return any;
        }
        public bool SendPose(float x, float y, float z, float yaw)
        {
            bool any = false;
            foreach (Peer peer in peers.Values)
            {
                if (!peer.Accepted || peer.DisconnectAt != 0) continue;
                WireMessage m = Create(peer, MessageKind.Pose); m.X = x; m.Y = y; m.Z = z; m.Yaw = yaw; m.Sequence = ++peer.PoseOut;
                if (Send(peer, m, true)) { any = true; LastSentPoseSequence = m.Sequence; }
            }
            return any;
        }
        public SessionPeer[] GetAcceptedPeers()
        {
            List<SessionPeer> result = new List<SessionPeer>();
            foreach (Peer peer in peers.Values)
                if (peer.Accepted && peer.DisconnectAt == 0) result.Add(new SessionPeer { PlayerId = peer.PlayerId, ConnectionGeneration = peer.Generation });
            return result.ToArray();
        }
        public bool SendApplication(ulong playerId, byte[] payload)
        {
            ProtocolCodec.ValidatePayload(payload);
            if (!IsHost && playerId != 1) throw new ArgumentException("A client may send application messages only to its accepted host.");
            foreach (Peer peer in peers.Values)
            {
                if (!peer.Accepted || peer.DisconnectAt != 0 || peer.PlayerId != playerId) continue;
                return SendApplicationTo(peer, payload);
            }
            return false;
        }
        public bool BroadcastApplication(byte[] payload)
        {
            ProtocolCodec.ValidatePayload(payload);
            if (!IsHost) throw new InvalidOperationException("Only the host can broadcast application messages.");
            bool any = false, all = true;
            foreach (Peer peer in peers.Values)
            {
                if (!peer.Accepted || peer.DisconnectAt != 0) continue;
                any = true; if (!SendApplicationTo(peer, payload)) all = false;
            }
            return any && all;
        }
        public bool DisconnectPlayer(ulong playerId, string controlledReason)
        {
            ProtocolCodec.ValidateText(controlledReason, 96);
            if (controlledReason.Length == 0) throw new ArgumentException("A controlled disconnect reason is required.");
            if (!IsHost && playerId != 1) throw new ArgumentException("A client can disconnect only its accepted host.");
            foreach (Peer peer in peers.Values)
            {
                if (!peer.Accepted || peer.PlayerId != playerId) continue;
                if (IsHost) Reject(peer.Connection, controlledReason);
                else EndWithReason(controlledReason);
                return true;
            }
            return false;
        }
        private bool SendApplicationTo(Peer peer, byte[] payload)
        {
            WireMessage m = Create(peer, MessageKind.Application); m.Payload = payload; m.Sequence = unchecked(peer.ApplicationOut + 1);
            if (!Send(peer, m, false)) return false;
            peer.ApplicationOut = m.Sequence; return true;
        }
        private WireMessage Create(Peer peer, MessageKind kind)
        { return new WireMessage { Kind = kind, SessionId = SessionId, SessionGeneration = SessionGeneration, ConnectionGeneration = peer.Generation }; }
        private bool Send(Peer peer, WireMessage message, bool sequenced) { return transport.Send(peer.Connection, ProtocolCodec.Encode(message), sequenced); }
        private void Reject(int connection, string reason)
        {
            Peer peer; if (!peers.TryGetValue(connection, out peer)) { transport.Disconnect(connection); return; }
            if (peer.DisconnectAt != 0) return;
            // Controlled protocol errors only; no remote endpoint or session token in logs.
            if (reason.Length > 100) reason = reason.Substring(0, 100);
            if (IsHost)
            {
                WireMessage rejection = peer.Accepted ? Create(peer, MessageKind.Reject) : new WireMessage { Kind = MessageKind.Reject };
                rejection.Text = reason; Send(peer, rejection, false);
            }
            peer.DisconnectAt = clock.ElapsedMilliseconds + 250;
            Status = "Rejected: " + reason; Emit("Rejected", reason, peer, 0);
        }
        private void RemovePeer(Peer peer, string reason)
        {
            if (!peers.Remove(peer.Connection)) return;
            ResumeRecord resume;
            if (peer.Token != Guid.Empty && resumes.TryGetValue(peer.Token, out resume)) { resume.Active = false; resume.Expires = clock.ElapsedMilliseconds + 120000; }
            Emit("Disconnected", reason, peer, 0);
        }
        private void EndWithReason(string reason) { transport.Stop(); peers.Clear(); Status = reason; Emit("Stopped", reason, null, 0); }
        private void Emit(string kind, string detail, Peer peer, uint sequence)
        {
            Action<SessionEvent> callback = Event;
            if (callback != null) callback(new SessionEvent { Kind = kind, Detail = detail, PlayerId = peer == null ? 0 : peer.PlayerId,
                ConnectionGeneration = peer == null ? 0 : peer.Generation, Sequence = sequence });
        }
        private static Guid RandomIdentity()
        {
            byte[] bytes = new byte[16];
            lock (RandomProvider)
            {
                Guid result;
                do { RandomProvider.GetBytes(bytes); result = new Guid(bytes); } while (result == Guid.Empty);
                return result;
            }
        }
        public void Leave()
        {
            bool wasRunning = transport.IsRunning;
            transport.Stop(); peers.Clear(); resumes.Clear(); SessionId = Guid.Empty; SessionGeneration = 0; LocalPlayerId = 0;
            IsHost = false; Status = "Offline"; ProbesReceived = 0; PongsReceived = 0; LastPingMilliseconds = 0;
            if (wasRunning) Emit("Stopped", "Local session left", null, 0);
        }
        public void Dispose() { Leave(); transport.Dispose(); }
    }
}
