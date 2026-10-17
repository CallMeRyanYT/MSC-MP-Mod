using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MSCMP.Network;

internal static class NetworkTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception("FAIL: " + message); }
    private static CompatibilityInfo Compatibility()
    { return new CompatibilityInfo { ModVersion = "0.2.0-diagnostics", GameFingerprint = new string('a', 64), ContentManifest = new string('b', 64), Capabilities = 1 }; }
    private static void RejectEncode(WireMessage message, string name)
    {
        bool rejected = false; try { ProtocolCodec.Encode(message); } catch (ArgumentException) { rejected = true; }
        Check(rejected, name);
    }
    private static bool Decodes(byte[] bytes)
    { WireMessage message; string error; return ProtocolCodec.TryDecode(bytes, out message, out error); }
    private static void Codec()
    {
        CompatibilityInfo legacy = Compatibility(), current = Compatibility();
        current.Capabilities = ProtocolCapabilities.DiagnosticNetworking | ProtocolCapabilities.AvatarAppearanceV2;
        Check(current.Capabilities == 3, "appearance v2 explicitly advertised in admission capabilities");
        Check(current.Mismatch(legacy) == "Protocol capabilities differ" && legacy.Mismatch(current) == "Protocol capabilities differ",
            "legacy avatar builds rejected in both handshake directions despite equal release labels");
        CompatibilityInfo otherBuild = Compatibility(); otherBuild.ContentManifest = new string('c', 64);
        Check(legacy.Mismatch(otherBuild) == "Content/dependency manifest differs", "different assemblies with equal release labels are incompatible");
        WireMessage hello = new WireMessage { Kind = MessageKind.Hello, Compatibility = Compatibility(), Text = "Tester", Nonce = Guid.NewGuid() };
        byte[] encoded = ProtocolCodec.Encode(hello); Check(Decodes(encoded), "hello roundtrip");
        for (int length = 0; length < encoded.Length; length++)
        { byte[] truncated = new byte[length]; Array.Copy(encoded, truncated, length); Check(!Decodes(truncated), "every truncated hello rejected " + length); }
        byte[] oversized = new byte[481]; Check(!Decodes(oversized), "oversize rejected before parse");
        byte[] extra = new byte[encoded.Length + 1]; Array.Copy(encoded, extra, encoded.Length); Check(!Decodes(extra), "trailing bytes rejected");
        byte[] corrupt = (byte[])encoded.Clone(); corrupt[4] = (byte)(ProtocolCodec.Version + 1); Check(!Decodes(corrupt), "protocol mismatch rejected");
        corrupt = (byte[])encoded.Clone(); corrupt[6] = 255; Check(!Decodes(corrupt), "unknown enum rejected");
        corrupt = (byte[])encoded.Clone(); corrupt[7] = 1; Check(!Decodes(corrupt), "unknown flags rejected");
        corrupt = (byte[])encoded.Clone(); corrupt[44] = 255; corrupt[45] = 255; Check(!Decodes(corrupt), "string prefix bound enforced");
        corrupt = (byte[])encoded.Clone(); corrupt[46] = 255; Check(!Decodes(corrupt), "invalid UTF8 rejected");
        RejectEncode(new WireMessage { Kind = MessageKind.Pose, X = Single.NaN }, "NaN rejected");
        RejectEncode(new WireMessage { Kind = MessageKind.Pose, Y = Single.PositiveInfinity }, "infinity rejected");
        RejectEncode(new WireMessage { Kind = MessageKind.Pose, Z = 100001 }, "position bound rejected");
        RejectEncode(new WireMessage { Kind = MessageKind.Probe, Text = new string('x', 257) }, "long probe rejected");
        RejectEncode(new WireMessage { Kind = MessageKind.Probe, Text = "line\ncontrol" }, "control character rejected");
        RejectEncode(new WireMessage { Kind = MessageKind.Probe, Text = new string('\u00e9', 129) }, "UTF8 bound uses bytes");
        RejectEncode(new WireMessage { Kind = MessageKind.Application, Payload = new byte[385] }, "application payload upper bound");
        RejectEncode(new WireMessage { Kind = MessageKind.Application, Payload = new byte[0] }, "empty application payload rejected");
        Check(Decodes(ProtocolCodec.Encode(new WireMessage { Kind = MessageKind.Application, Payload = new byte[384] })), "maximum application payload roundtrip");
        byte[] pose = ProtocolCodec.Encode(new WireMessage { Kind = MessageKind.Pose, X = 1, Y = 2, Z = 3, Yaw = 90 });
        Array.Copy(BitConverter.GetBytes(Single.NaN), 0, pose, 44, 4); Check(!Decodes(pose), "received NaN rejected");
        Check(ProtocolCodec.IsNewer(0, UInt32.MaxValue), "sequence wraps forward");
        Check(!ProtocolCodec.IsNewer(UInt32.MaxValue, 0), "old wrap packet rejected");
        Check(!ProtocolCodec.IsNewer(1, 1), "duplicate sequence rejected");
        Check(!ProtocolCodec.IsNewer(0x80000000, 0), "half-range ambiguity rejected");
        Check(!LiteNetLib.DiagnosticPacketGuard.IsAllowed(new byte[] { 128 }, 1), "fragment rejected before transport allocation");
        Check(!LiteNetLib.DiagnosticPacketGuard.IsAllowed(new byte[] { 19, 255, 255 }, 3), "merged packet rejected");
        for (int kind = 1; kind <= 10; kind++) Check(!LiteNetLib.DiagnosticPacketGuard.IsAllowed(new byte[] { (byte)kind }, 1), "short transport control " + kind);
        Check(!LiteNetLib.DiagnosticPacketGuard.IsAllowed(new byte[513], 513), "raw datagram bound");
        Random random = new Random(20577);
        for (int i = 0; i < 2000; i++) { byte[] bytes = new byte[random.Next(0, 600)]; random.NextBytes(bytes); Decodes(bytes); }
        Check(true, "2000 seeded malformed packets did not throw");
    }
    private static void Pump(DiagnosticSession host, DiagnosticSession client, Func<bool> done, string operation)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 7000 && !done()) { host.Poll(); client.Poll(); Thread.Sleep(10); }
        Check(done(), operation + "; host=" + host.Status + "; client=" + client.Status);
    }
    private static void Localhost()
    {
        using (DiagnosticSession host = new DiagnosticSession(Compatibility(), "Host"))
        using (DiagnosticSession client = new DiagnosticSession(Compatibility(), "Client"))
        {
            host.Host(0, 12345); Check(host.LocalPort > 0, "OS assigned host port");
            Check(host.SessionGeneration == 12345, "Persisted host generation used with a fresh session identity");
            // Exercise the actual UDP parser with malformed datagrams, then prove liveness.
            using (UdpClient injector = new UdpClient())
            {
                Random r = new Random(71);
                for (int i = 0; i < 100; i++) { byte[] bytes = new byte[r.Next(1, 800)]; r.NextBytes(bytes); injector.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, host.LocalPort)); }
            }
            ulong firstGeneration = 0; ulong nextGeneration = 0;
            int clientStops = 0; client.Event += delegate(SessionEvent e) { if (e.Kind == "Stopped") clientStops++; };
            List<uint> received = new List<uint>();
            host.Event += delegate(SessionEvent e) { if (e.Kind == "Accepted") { if (firstGeneration == 0) firstGeneration = e.ConnectionGeneration; else nextGeneration = e.ConnectionGeneration; } if (e.Kind == "Probe") received.Add(e.Sequence); };
            client.Join("127.0.0.1", host.LocalPort);
            Pump(host, client, delegate { return host.AcceptedPeerCount == 1 && client.AcceptedPeerCount == 1; }, "real UDP handshake after malformed datagrams");
            Check(host.SessionId == client.SessionId && host.SessionGeneration == client.SessionGeneration, "host issued shared session identity");
            Check(client.LocalPlayerId >= 2, "connection bound player assigned");
            Check(client.LocalPort != host.LocalPort, "two actual UDP endpoints");
            SessionPeer[] boundPeers = host.GetAcceptedPeers();
            Check(boundPeers.Length == 1 && boundPeers[0].PlayerId == client.LocalPlayerId && boundPeers[0].ConnectionGeneration == firstGeneration, "accepted peer metadata binds player and connection");
            ApplicationMessage hostMessage = null, clientMessage = null; PoseMessage hostPose = null;
            host.ApplicationReceived += delegate(ApplicationMessage m) { hostMessage = m; };
            client.ApplicationReceived += delegate(ApplicationMessage m) { clientMessage = m; };
            host.PoseReceived += delegate(PoseMessage m) { hostPose = m; };
            byte[] fullPayload = new byte[384]; fullPayload[0] = 255; fullPayload[383] = 17;
            Check(client.SendApplication(1, fullPayload), "client application payload sent to host");
            Check(host.SendApplication(client.LocalPlayerId, new byte[] { 7, 8 }), "host targeted application payload sent");
            for (int i = 0; i < 32; i++) Check(client.SendProbe("reliable-" + i), "probe accepted " + i);
            Check(client.SendPing(), "client ping sent"); Check(host.SendPing(), "host ping sent");
            Check(client.SendPose(1, 2, 3, 90), "sequenced diagnostic pose sent");
            Pump(host, client, delegate { return host.ProbesReceived == 32 && client.PongsReceived == 1 && host.PongsReceived == 1; }, "reliable probes and bidirectional ping/pong");
            Pump(host, client, delegate { return hostMessage != null && clientMessage != null && hostPose != null; }, "typed app and pose delivery");
            Check(hostMessage.PlayerId == client.LocalPlayerId && hostMessage.ConnectionGeneration == firstGeneration && hostMessage.Sequence == 1, "application sender identity bound to accepted connection");
            Check(hostMessage.Payload.Length == 384 && hostMessage.Payload[383] == 17, "application payload preserved");
            Check(clientMessage.PlayerId == 1 && clientMessage.Payload[0] == 7, "client binds messages to accepted host");
            Check(hostPose.PlayerId == client.LocalPlayerId && hostPose.X == 1 && hostPose.Y == 2 && hostPose.Z == 3 && hostPose.Yaw == 90, "typed pose contains validated values and bound identity");
            bool spoofRejected = false;
            try { client.SendApplication(999, new byte[] { 1 }); } catch (ArgumentException) { spoofRejected = true; }
            Check(spoofRejected, "client cannot target/spoof another peer");
            Check(host.BroadcastApplication(new byte[] { 3 }), "host application broadcast");
            Pump(host, client, delegate { return clientMessage.Payload[0] == 3; }, "host broadcast delivered");
            for (int i = 0; i < received.Count; i++) Check(received[i] == (uint)i + 1, "reliable ordering " + i);
            ulong player = client.LocalPlayerId;
            client.Leave(); Check(clientStops == 1, "local leave notifies dependent world lifecycle");
            Pump(host, client, delegate { return host.AcceptedPeerCount == 0; }, "disconnect cleanup");
            client.Join("127.0.0.1", host.LocalPort);
            Pump(host, client, delegate { return host.AcceptedPeerCount == 1 && client.AcceptedPeerCount == 1; }, "reconnect handshake");
            Check(client.LocalPlayerId == player, "host token resumes identity");
            Check(nextGeneration > firstGeneration, "reconnect advances generation");
            Check(client.SendProbe("after reconnect"), "new connection probe sent");
            Pump(host, client, delegate { return host.ProbesReceived == 33; }, "reconnect resets ordered stream");
            Check(host.DisconnectPlayer(player, "Test complete"), "host disconnects selected bound player");
            Pump(host, client, delegate { return client.Status.IndexOf("Test complete") >= 0 && host.AcceptedPeerCount == 0; }, "controlled rejection reaches selected peer");
            Check(host.IsRunning, "peer rejection preserves host session");
            client.Leave(); host.Leave(); Check(!client.IsRunning && !host.IsRunning && client.QueueDepth == 0 && host.PeerCount == 0, "stop clears queues and peers");
        }
        using (DiagnosticSession host = new DiagnosticSession(Compatibility(), "Host"))
        {
            CompatibilityInfo wrong = Compatibility(); wrong.GameFingerprint = "different";
            using (DiagnosticSession client = new DiagnosticSession(wrong, "Client"))
            {
                host.Host(0); client.Join("127.0.0.1", host.LocalPort);
                Pump(host, client, delegate { return client.Status.IndexOf("Game fingerprint differs") >= 0; }, "manifest rejection useful message");
                Check(client.AcceptedPeerCount == 0 && client.LocalPlayerId == 0, "mismatch never accepted");
            }
        }
    }

    private sealed class FakeTransport : INetworkTransport
    {
        public readonly Queue<TransportEvent> Events = new Queue<TransportEvent>();
        public readonly List<byte[]> Sent = new List<byte[]>();
        public bool IsRunning { get; private set; }
        public int LocalPort { get { return 20577; } }
        public int PeerCount { get { return 1; } }
        public int QueueDepth { get { return Events.Count; } }
        public long SentBytes { get { return 0; } }
        public long ReceivedBytes { get { return 0; } }
        public void Start(int port, int max) { IsRunning = true; }
        public void Connect(string ip, int port) { Events.Enqueue(new TransportEvent { Kind = TransportEventKind.Connected, Connection = 1 }); }
        public bool Send(int peer, byte[] data, bool sequenced) { Sent.Add(data); return true; }
        public void Disconnect(int peer) { }
        public TransportEvent[] Poll() { TransportEvent[] result = Events.ToArray(); Events.Clear(); return result; }
        public void Stop() { IsRunning = false; Events.Clear(); }
        public void Dispose() { Stop(); }
        public void Receive(WireMessage message) { Events.Enqueue(new TransportEvent { Kind = TransportEventKind.Data, Connection = 1, Data = ProtocolCodec.Encode(message) }); }
    }
    private static void StalePackets()
    {
        CompatibilityInfo appearance = Compatibility();
        appearance.Capabilities = ProtocolCapabilities.DiagnosticNetworking | ProtocolCapabilities.AvatarAppearanceV2;
        FakeTransport legacyPeer = new FakeTransport();
        using (DiagnosticSession host = new DiagnosticSession(appearance, "Host", legacyPeer))
        {
            host.Host(20577);
            legacyPeer.Events.Enqueue(new TransportEvent { Kind = TransportEventKind.Connected, Connection = 1 });
            legacyPeer.Receive(new WireMessage { Kind = MessageKind.Hello, Compatibility = Compatibility(), Text = "Legacy", Nonce = Guid.NewGuid() });
            host.Poll();
            Check(host.AcceptedPeerCount == 0 && host.Status.IndexOf("capabilities differ") >= 0,
                "appearance-aware host rejects legacy hello before acceptance");
        }
        FakeTransport legacyHost = new FakeTransport();
        using (DiagnosticSession client = new DiagnosticSession(appearance, "Client", legacyHost))
        {
            client.Join("127.0.0.1", 20577); client.Poll();
            WireMessage hello; string error;
            Check(ProtocolCodec.TryDecode(legacyHost.Sent[0], out hello, out error), "appearance client hello decoded");
            legacyHost.Receive(new WireMessage { Kind = MessageKind.Welcome, SessionId = Guid.NewGuid(), SessionGeneration = 1,
                ConnectionGeneration = 1, PlayerId = 2, Compatibility = Compatibility(), Nonce = hello.Nonce, ReconnectToken = Guid.NewGuid() });
            client.Poll();
            Check(client.AcceptedPeerCount == 0 && client.LocalPlayerId == 0 && client.Status.IndexOf("capabilities differ") >= 0,
                "appearance-aware client rejects legacy welcome before identity assignment");
        }
        FakeTransport fake = new FakeTransport();
        using (DiagnosticSession client = new DiagnosticSession(Compatibility(), "Test", fake))
        {
            client.Join("127.0.0.1", 20577); client.Poll();
            WireMessage hello; string error; Check(ProtocolCodec.TryDecode(fake.Sent[0], out hello, out error), "captured hello decoded");
            Guid session = Guid.NewGuid();
            fake.Receive(new WireMessage { Kind = MessageKind.Welcome, SessionId = session, SessionGeneration = 50, ConnectionGeneration = 7, PlayerId = 2,
                Compatibility = Compatibility(), Nonce = hello.Nonce, ReconnectToken = Guid.NewGuid() });
            client.Poll(); Check(client.AcceptedPeerCount == 1, "synthetic protocol setup accepted");
            fake.Receive(new WireMessage { Kind = MessageKind.Probe, SessionId = session, SessionGeneration = 50, ConnectionGeneration = 6, Sequence = 1, Text = "old" });
            client.Poll(); Check(client.ProbesReceived == 0, "old connection generation ignored");
            fake.Receive(new WireMessage { Kind = MessageKind.Probe, SessionId = Guid.NewGuid(), SessionGeneration = 50, ConnectionGeneration = 7, Sequence = 1, Text = "wrong world" });
            client.Poll(); Check(client.ProbesReceived == 0, "old session ignored");
            WireMessage fresh = new WireMessage { Kind = MessageKind.Probe, SessionId = session, SessionGeneration = 50, ConnectionGeneration = 7, Sequence = 1, Text = "fresh" };
            fake.Receive(fresh); fake.Receive(fresh); client.Poll(); Check(client.ProbesReceived == 1, "application duplicate ignored");
            int applicationCount = 0; client.ApplicationReceived += delegate(ApplicationMessage m) { applicationCount++; };
            WireMessage app = new WireMessage { Kind = MessageKind.Application, SessionId = session, SessionGeneration = 50, ConnectionGeneration = 6, Sequence = 1, Payload = new byte[] { 255 } };
            fake.Receive(app); client.Poll(); Check(applicationCount == 0, "old application connection generation ignored");
            app.ConnectionGeneration = 7; fake.Receive(app); fake.Receive(app); client.Poll(); Check(applicationCount == 1, "application data duplicate suppressed");
            fake.Receive(new WireMessage { Kind = MessageKind.Reject, SessionId = session, SessionGeneration = 50, ConnectionGeneration = 7, Text = "Session ended" });
            fake.Events.Enqueue(new TransportEvent { Kind = TransportEventKind.Connected, Connection = 99 });
            client.Poll(); Check(!client.IsRunning && client.AcceptedPeerCount == 0, "queued connected event cannot resurrect stopped session");
        }
        FakeTransport exhausted = new FakeTransport();
        using (DiagnosticSession host = new DiagnosticSession(Compatibility(), "Host", exhausted))
        {
            host.Host(20577);
            typeof(DiagnosticSession).GetField("nextGeneration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(host, UInt64.MaxValue);
            exhausted.Events.Enqueue(new TransportEvent { Kind = TransportEventKind.Connected, Connection = 1 });
            exhausted.Receive(new WireMessage { Kind = MessageKind.Hello, Compatibility = Compatibility(), Text = "Test", Nonce = Guid.NewGuid() });
            host.Poll(); Check(host.AcceptedPeerCount == 0 && host.Status.IndexOf("generation exhausted") >= 0, "connection generation cannot wrap");
        }
    }
    private static void BoundsAndTimeouts()
    {
        using (LiteNetTransport host = new LiteNetTransport())
        using (LiteNetTransport client = new LiteNetTransport())
        {
            host.Start(0, 1); client.Start(0, 1); client.Connect("127.0.0.1", host.LocalPort);
            int connection = 0; Stopwatch deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 4000 && (connection == 0 || host.PeerCount == 0))
            {
                foreach (TransportEvent e in client.Poll()) if (e.Kind == TransportEventKind.Connected) connection = e.Connection;
                Thread.Sleep(10);
            }
            Check(connection != 0 && host.PeerCount == 1, "bounded transport connected"); host.Poll();
            byte[] data = ProtocolCodec.Encode(new WireMessage { Kind = MessageKind.Probe, Text = "bounded" });
            int sent = 0;
            for (int i = 0; i < 300; i++) if (client.Send(connection, data, false)) sent++;
            Check(sent <= 128 && sent > 0, "outbound rate bounded");
            Thread.Sleep(1100);
            for (int i = 0; i < 128; i++) client.Send(connection, data, false);
            Thread.Sleep(1100);
            for (int i = 0; i < 64; i++) client.Send(connection, data, false);
            Thread.Sleep(200);
            Check(host.QueueDepth <= LiteNetTransport.MaximumQueuedEvents, "inbound queue never exceeds cap");
            TransportEvent[] result = host.Poll();
            Check(result.Length == 1 && result[0].Kind == TransportEventKind.Error && !host.IsRunning, "queue overflow ends session deterministically");
        }
        using (DiagnosticSession client = new DiagnosticSession(Compatibility(), "NoHost"))
        {
            int unusedPort;
            using (UdpClient reserve = new UdpClient(0)) unusedPort = ((IPEndPoint)reserve.Client.LocalEndPoint).Port;
            client.Join("127.0.0.1", unusedPort);
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 7000 && client.IsRunning) { client.Poll(); Thread.Sleep(20); }
            Check(!client.IsRunning, "unreachable endpoint times out and stops");
        }
        using (DiagnosticSession host = new DiagnosticSession(Compatibility(), "Host"))
        using (LiteNetTransport silent = new LiteNetTransport())
        {
            host.Host(0); silent.Start(0, 1); silent.Connect("127.0.0.1", host.LocalPort);
            Stopwatch timer = Stopwatch.StartNew(); bool connected = false;
            while (timer.ElapsedMilliseconds < 7000)
            {
                host.Poll(); silent.Poll(); connected |= host.PeerCount > 0;
                if (connected && host.PeerCount == 0) break;
                Thread.Sleep(20);
            }
            Check(connected && host.PeerCount == 0 && host.AcceptedPeerCount == 0, "peer without application handshake times out");
        }
    }
    private sealed class ImpairedRelay : IDisposable
    {
        private sealed class Pending
        {
            public byte[] Data; public IPEndPoint Target; public long Due;
        }
        private readonly UdpClient socket;
        private readonly IPEndPoint host;
        private readonly Thread worker;
        private volatile bool running = true;
        public int Dropped, Duplicated, Forwarded;
        public Exception Failure;
        public int Port { get; private set; }
        public ImpairedRelay(int hostPort)
        {
            host = new IPEndPoint(IPAddress.Loopback, hostPort); socket = new UdpClient(0); socket.Client.Blocking = false;
            Port = ((IPEndPoint)socket.Client.LocalEndPoint).Port;
            worker = new Thread(Run); worker.IsBackground = true; worker.Start();
        }
        private void Run()
        {
            Random random = new Random(20577); Stopwatch time = Stopwatch.StartNew();
            List<Pending> pending = new List<Pending>(); IPEndPoint client = null;
            try
            {
                while (running)
                {
                    for (int budget = 0; budget < 32 && socket.Available > 0; budget++)
                    {
                        IPEndPoint source = new IPEndPoint(IPAddress.Any, 0); byte[] bytes = socket.Receive(ref source);
                        bool fromHost = source.Port == host.Port;
                        if (!fromHost) client = source;
                        IPEndPoint target = fromHost ? client : host;
                        if (target == null) continue;
                        if (random.Next(100) < 5) { Dropped++; continue; }
                        if (pending.Count >= 256) throw new Exception("Impairment relay pending queue overflow");
                        long due = time.ElapsedMilliseconds + 35 + random.Next(-10, 11);
                        pending.Add(new Pending { Data = bytes, Target = target, Due = due });
                        if (random.Next(100) < 5)
                        { pending.Add(new Pending { Data = bytes, Target = target, Due = due + random.Next(1, 9) }); Duplicated++; }
                    }
                    for (int index = pending.Count - 1; index >= 0; index--)
                    {
                        Pending packet = pending[index]; if (packet.Due > time.ElapsedMilliseconds) continue;
                        socket.Send(packet.Data, packet.Data.Length, packet.Target); pending.RemoveAt(index); Forwarded++;
                    }
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex) { if (running) Failure = ex; }
        }
        public void Dispose() { running = false; worker.Join(1000); socket.Close(); }
    }
    private static void Impairment()
    {
        using (DiagnosticSession host = new DiagnosticSession(Compatibility(), "Host"))
        using (DiagnosticSession client = new DiagnosticSession(Compatibility(), "Client"))
        {
            host.Host(0);
            using (ImpairedRelay relay = new ImpairedRelay(host.LocalPort))
            {
                client.Join("127.0.0.1", relay.Port);
                Pump(host, client, delegate { return host.AcceptedPeerCount == 1 && client.AcceptedPeerCount == 1; }, "handshake under seeded loss/jitter/duplicates/reordering");
                List<uint> sequences = new List<uint>(); host.Event += delegate(SessionEvent e) { if (e.Kind == "Probe") sequences.Add(e.Sequence); };
                for (int i = 0; i < 64; i++) Check(client.SendProbe("impaired-" + i), "impaired reliable send " + i);
                Check(client.SendPing(), "impaired ping sent");
                Pump(host, client, delegate { return host.ProbesReceived == 64 && client.PongsReceived == 1; }, "reliable delivery under seeded impairment");
                for (int i = 0; i < sequences.Count; i++) Check(sequences[i] == (uint)i + 1, "impaired order and dedup " + i);
                Check(relay.Failure == null, "impairment relay stayed within bounds");
                Check(relay.Dropped > 0 && relay.Duplicated > 0 && relay.Forwarded > 64, "actual UDP loss/duplication injected");
                Console.WriteLine("Impairment: seed20577; delay35ms one-way +/-10ms; 5% loss, 5% duplication; dropped=" + relay.Dropped + ", duplicated=" + relay.Duplicated + ".");
            }
        }
    }
    private static void PeerRemovalRaces()
    {
        using (LiteNetTransport host = new LiteNetTransport())
        using (LiteNetTransport client = new LiteNetTransport())
        {
            host.Start(0, 1); client.Start(0, 1);
            for (int round = 0; round < 24; round++)
            {
                int hostConnection = 0, clientConnection = 0;
                client.Connect("127.0.0.1", host.LocalPort); Stopwatch deadline = Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < 4000 && (hostConnection == 0 || clientConnection == 0))
                {
                    foreach (TransportEvent e in host.Poll()) if (e.Kind == TransportEventKind.Connected) hostConnection = e.Connection;
                    foreach (TransportEvent e in client.Poll()) if (e.Kind == TransportEventKind.Connected) clientConnection = e.Connection;
                    Thread.Sleep(1);
                }
                Check(hostConnection > 0 && clientConnection > 0, "disconnect race setup " + round);
                host.Disconnect(hostConnection); client.Disconnect(clientConnection);
                deadline.Reset(); deadline.Start();
                while (deadline.ElapsedMilliseconds < 3000 && (host.PeerCount > 0 || client.PeerCount > 0))
                { host.Poll(); client.Poll(); Thread.Sleep(1); }
                Check(host.IsRunning && client.IsRunning && host.PeerCount == 0 && client.PeerCount == 0, "concurrent local/remote removal stays coherent " + round);
                host.Poll(); client.Poll();
            }
        }
    }
    private static void ReconnectInsideDisconnectCallback()
    {
        LiteNetLib.EventBasedNetListener listener = new LiteNetLib.EventBasedNetListener();
        LiteNetLib.NetManager manager = new LiteNetLib.NetManager(listener, 1, "MSCMP-DIAGNOSTICS-2");
        manager.UnsyncedEvents = true;
        LiteNetLib.NetPeer replacement = null;
        try
        {
            Check(manager.Start(0), "callback reconnect socket opened");
            // An unconnected loopback port keeps this deterministic: only the local
            // disconnect callback may alter this in-progress peer.
            using (UdpClient sink = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                int port = ((IPEndPoint)sink.Client.LocalEndPoint).Port;
                LiteNetLib.NetPeer original = manager.Connect("127.0.0.1", port);
                listener.PeerDisconnectedEvent += delegate(LiteNetLib.NetPeer peer, LiteNetLib.DisconnectInfo info)
                { replacement = manager.Connect("127.0.0.1", port); };
                manager.DisconnectPeer(original);
                Check(replacement != null && !Object.ReferenceEquals(original, replacement), "disconnect callback creates a new peer rather than returning the retired peer");
                Check(Object.ReferenceEquals(manager.Connect("127.0.0.1", port), replacement), "old cleanup does not remove callback replacement");
                manager.DisconnectPeer(original);
                Check(Object.ReferenceEquals(manager.Connect("127.0.0.1", port), replacement), "late duplicate disconnect cannot remove a replacement at the same endpoint");
            }
        }
        finally { manager.Stop(); }
    }
    public static int Main()
    {
        try { ReconnectInsideDisconnectCallback(); Codec(); StalePackets(); Localhost(); BoundsAndTimeouts(); Impairment(); PeerRemovalRaces(); Console.WriteLine("PASS: " + assertions + " network assertions; real localhost UDP endpoints; not an MSC runtime proof."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
