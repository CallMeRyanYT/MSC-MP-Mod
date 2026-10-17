using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MSCMP.Core;
using MSCMP.Network;
using MSCMP.Sync;

internal static class SyncTests
{
    private sealed class TracedTransport : INetworkTransport
    {
        internal readonly LiteNetTransport Inner = new LiteNetTransport();
        private readonly Queue<string> trace = new Queue<string>();
        public bool IsRunning { get { return Inner.IsRunning; } }
        public int LocalPort { get { return Inner.LocalPort; } }
        public int PeerCount { get { return Inner.PeerCount; } }
        public int QueueDepth { get { return Inner.QueueDepth; } }
        public long SentBytes { get { return Inner.SentBytes; } }
        public long ReceivedBytes { get { return Inner.ReceivedBytes; } }
        public void Start(int port, int maximum) { Inner.Start(port, maximum); }
        public void Connect(string address, int port) { Inner.Connect(address, port); }
        public void Disconnect(int connection) { Inner.Disconnect(connection); }
        public void Stop() { Inner.Stop(); }
        public void Dispose() { Inner.Dispose(); }
        private void Trace(string direction, int connection, byte[] bytes)
        {
            WireMessage message; string error;
            if (!ProtocolCodec.TryDecode(bytes, out message, out error)) return;
            trace.Enqueue(direction + " link=" + connection + " kind=" + message.Kind + " generation=" + message.ConnectionGeneration + " sequence=" + message.Sequence);
            if (trace.Count > 48) trace.Dequeue();
        }
        public bool Send(int connection, byte[] bytes, bool sequenced)
        { bool sent = Inner.Send(connection, bytes, sequenced); if (sent) Trace("out", connection, bytes); return sent; }
        public TransportEvent[] Poll()
        { TransportEvent[] events = Inner.Poll(); foreach (TransportEvent e in events) if (e.Kind == TransportEventKind.Data) Trace("in", e.Connection, e.Data); return events; }
        internal void Dump() { foreach (string line in trace) Console.Error.WriteLine(line); }
    }
    private static int assertions;
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Refused(Action action, string message)
    { bool refused = false; try { action(); } catch (ArgumentException) { refused = true; } catch (FormatException) { refused = true; } catch (EndOfStreamException) { refused = true; } Check(refused, message); }
    private static CompatibilityInfo Compatibility()
    { return new CompatibilityInfo { ModVersion = "0.2.0-sync-test", GameFingerprint = new string('a', 64), ContentManifest = new string('b', 64), Capabilities = 1 }; }
    private static AuthorityWorld World(ulong generation)
    {
        AuthorityWorld world = new AuthorityWorld(generation, new AuthorityLimits(3, 4, 20, 10000, 5, 64, 4096), 32, 256);
        world.ConnectPlayer(1, 1, WorldVector.Zero, true); return world;
    }
    private static void Framing()
    {
        ReliableBlobSender sender = new ReliableBlobSender(); ReliableBlobReceiver receiver = new ReliableBlobReceiver();
        byte[] source = new byte[10007]; for (int i = 0; i < source.Length; i++) source[i] = (byte)(i % 251);
        Check(sender.Enqueue(source), "Large blob queued"); source[0] = 200;
        int blocked = sender.Pump(delegate(byte[] frame) { return false; }, 4);
        Check(blocked == 0 && sender.PendingCount == 1, "Backpressure preserves queued blob");
        byte[] completed = null; List<byte[]> frames = new List<byte[]>();
        while (sender.PendingCount != 0)
            sender.Pump(delegate(byte[] frame) { frames.Add(frame); byte[] result = receiver.Receive(frame, 10); if (result != null) completed = result; return true; }, 4);
        Check(completed != null && completed.Length == source.Length && completed[0] == 0, "Sender snapshots bytes and transfer completes");
        for (int i = 1; i < source.Length; i += 37) Check(completed[i] == source[i], "Chunked bytes preserved");
        Check(receiver.AllocatedBytes == 0 && sender.PendingBytes == 0, "Buffers released after completion");
        Refused(delegate { receiver.Receive(frames[0], 20); }, "Old transfer cannot resurrect");
        Refused(delegate { new ReliableBlobReceiver().Receive(frames[1], 20); }, "Out of order chunks rejected");
        byte[] huge = (byte[])frames[0].Clone(); Buffer.BlockCopy(BitConverter.GetBytes(Int32.MaxValue), 0, huge, 12, 4);
        ReliableBlobReceiver capped = new ReliableBlobReceiver(); Refused(delegate { capped.Receive(huge, 20); }, "Huge allocation refused"); Check(capped.AllocatedBytes == 0, "No allocation before size check");
        ReliableBlobReceiver timeout = new ReliableBlobReceiver(); timeout.Receive(frames[0], 20);
        Refused(delegate { timeout.CheckTimeout(120021); }, "Incomplete transfer timeout"); Check(timeout.AllocatedBytes == 0, "Expired buffer released");
        ReliableBlobSender cap = new ReliableBlobSender(); Check(cap.Enqueue(new byte[ReliableBlobSender.MaximumBlob]), "Exact byte cap accepted"); Check(!cap.Enqueue(new byte[1]), "Queue byte cap enforced");
        Refused(delegate { cap.Enqueue(new byte[ReliableBlobSender.MaximumBlob + 1]); }, "Oversize blob refused");
    }
    private static void Codec()
    {
        AuthorityWorld world = World(7); world.RegisterAuthoredItem("authored/path:42", "test-block", WorldVector.Zero);
        WorldSnapshot snapshot = world.CaptureSnapshot(); byte[] encoded = WorldMessages.Encode(new WorldMessage { Kind = WorldMessageKind.Snapshot, Snapshot = snapshot });
        WorldMessage decoded = WorldMessages.Decode(encoded, 88, 99);
        ReplicaWorld replica = new ReplicaWorld(7);
        Check(replica.LoadSnapshot(decoded.Snapshot) == ApplyOutcome.Applied && replica.GetEntity(1).AuthoredKey == "authored/path:42", "Snapshot wire roundtrip");
        for (int i = 0; i < encoded.Length; i++)
        {
            byte[] cut = new byte[i]; Buffer.BlockCopy(encoded, 0, cut, 0, i);
            Refused(delegate { WorldMessages.Decode(cut, 1, 1); }, "Every truncated snapshot prefix rejected");
        }
        byte[] trailing = new byte[encoded.Length + 1]; Buffer.BlockCopy(encoded, 0, trailing, 0, encoded.Length);
        Refused(delegate { WorldMessages.Decode(trailing, 1, 1); }, "Trailing bytes rejected");
        byte[] countBomb = (byte[])encoded.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(Int32.MaxValue), 0, countBomb, 29, 4);
        Refused(delegate { WorldMessages.Decode(countBomb, 1, 1); }, "Snapshot count bomb rejected");
        AuthorityCommand forged = new AuthorityCommand(CommandKind.Pickup, 7, 888, 999, 1, 1, 1, 1, WorldVector.Zero, WorldVector.Zero);
        WorldMessage command = WorldMessages.Decode(WorldMessages.Encode(new WorldMessage { Kind = WorldMessageKind.Command, Command = forged }), 2, 3);
        Check(command.Command.PlayerId == 2 && command.Command.ConnectionGeneration == 3, "Wire cannot choose player or connection identity");
    }
    private static void BatchCodec()
    {
        AuthorityWorld world = World(7); ulong before = world.Watermark;
        for (int i = 0; i < 16; i++) world.SpawnItem("test-block", new WorldVector(i, 0, 0));
        CommitEvent[] events = world.ReadEventsSince(before).Events;
        byte[] bytes = WorldMessages.Encode(new WorldMessage { Kind = WorldMessageKind.LiveBatch, Commits = events });
        WorldMessage decoded = WorldMessages.Decode(bytes, 1, 1);
        Check(decoded.Commits.Length == 16 && decoded.Commits[15].Entity.Position.X == 15, "Full live batch preserves records and order");
        Refused(delegate { WorldMessages.Encode(new WorldMessage { Kind = WorldMessageKind.LiveBatch, Commits = new CommitEvent[0] }); }, "Empty live batch refused");
        Refused(delegate { WorldMessages.Encode(new WorldMessage { Kind = WorldMessageKind.LiveBatch, Commits = new CommitEvent[17] }); }, "Oversized live batch refused before entries are read");
        byte[] bomb = (byte[])bytes.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(Int32.MaxValue), 0, bomb, 5, 4);
        Refused(delegate { WorldMessages.Decode(bomb, 1, 1); }, "Untrusted batch count rejected before allocation");
        byte[] receipt = WorldMessages.Encode(new WorldMessage { Kind = WorldMessageKind.PoseReceipt, PoseSequence = UInt32.MaxValue, PoseAccepted = false, Position = WorldVector.Zero });
        for (int length = 0; length < receipt.Length; length++)
        { byte[] cut = new byte[length]; Buffer.BlockCopy(receipt, 0, cut, 0, length); Refused(delegate { WorldMessages.Decode(cut, 1, 1); }, "Truncated receipt rejected"); }
        receipt[9] = 2; Refused(delegate { WorldMessages.Decode(receipt, 1, 1); }, "Invalid receipt boolean rejected");
    }
    private static void PumpUntil(DiagnosticSession[] networks, WorldSyncSession[] worlds, Func<bool> done, string condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 20000)
        {
            foreach (DiagnosticSession network in networks) network.Poll();
            foreach (WorldSyncSession world in worlds) world.Poll();
            if (done()) { Check(true, condition); return; }
            Thread.Sleep(10);
        }
        foreach (DiagnosticSession network in networks)
        {
            Console.Error.WriteLine("Network at timeout: " + network.Status + " player=" + network.LocalPlayerId + " peers=" + network.AcceptedPeerCount);
            DumpLinks(network, "peers", new string[] { "PlayerId", "ApplicationIn", "ApplicationOut", "Generation" });
            DumpTransport(network);
        }
        foreach (WorldSyncSession world in worlds)
        {
            Console.Error.WriteLine("World at timeout: ready=" + world.Ready + " watermark=" + (world.Authority != null ? world.Authority.Watermark : (world.Replica != null ? world.Replica.Watermark : 0)));
            DumpLinks(world, "links", new string[] { "Player", "Connection", "Ready", "Baselines" });
        }
        throw new Exception("Timed out: " + condition);
    }
    private static void DumpLinks(object owner, string fieldName, string[] fields)
    {
        System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        System.Collections.IDictionary links = (System.Collections.IDictionary)owner.GetType().GetField(fieldName, flags).GetValue(owner);
        foreach (object value in links.Values)
        {
            foreach (string field in fields) Console.Error.Write(field + "=" + value.GetType().GetField(field, flags).GetValue(value) + " ");
            System.Reflection.FieldInfo sender = value.GetType().GetField("Sender", flags);
            if (sender != null)
            {
                ReliableBlobSender queued = (ReliableBlobSender)sender.GetValue(value);
                ReliableBlobReceiver received = (ReliableBlobReceiver)value.GetType().GetField("Receiver", flags).GetValue(value);
                Console.Error.Write("pending=" + queued.PendingCount + " receiveAllocation=" + received.AllocatedBytes);
            }
            Console.Error.WriteLine();
        }
    }
    private static void DumpTransport(DiagnosticSession network)
    {
        System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        object transport = network.GetType().GetField("transport", flags).GetValue(network);
        TracedTransport traced = transport as TracedTransport;
        if (traced != null) { traced.Dump(); transport = traced.Inner; }
        object manager = transport.GetType().GetField("manager", flags).GetValue(transport);
        System.Collections.IDictionary peers = (System.Collections.IDictionary)transport.GetType().GetField("peers", flags).GetValue(transport);
        foreach (object peer in peers.Values)
        {
            object channel = peer.GetType().GetField("_reliableOrderedChannel", flags).GetValue(peer);
            Console.Error.Write("Reliable link resendDelay=" + peer.GetType().GetField("_resendDelay", flags).GetValue(peer) + " ");
            foreach (string field in new string[] { "_localSeqence", "_localWindowStart", "_remoteSequence", "_remoteWindowStart" })
                Console.Error.Write(field + "=" + channel.GetType().GetField(field, flags).GetValue(channel) + " ");
            object head = channel.GetType().GetField("_headPendingPacket", flags).GetValue(channel);
            Console.Error.WriteLine("pendingHead=" + (head != null));
        }
    }
    private static void UdpIntegration()
    {
        List<DiagnosticSession> networks = new List<DiagnosticSession>(); List<WorldSyncSession> syncs = new List<WorldSyncSession>();
        try
        {
            DiagnosticSession host = new DiagnosticSession(Compatibility(), "Host", new TracedTransport()); networks.Add(host); host.Host(0);
            AuthorityWorld authority = World(host.SessionGeneration);
            for (int i = 0; i < 24; i++) authority.RegisterAuthoredItem("synthetic/" + i, "test-block", WorldVector.Zero);
            WorldSyncSession hostWorld = new WorldSyncSession(host, authority, WorldVector.Zero); syncs.Add(hostWorld);
            DiagnosticSession a = new DiagnosticSession(Compatibility(), "Alice", new TracedTransport()); networks.Add(a); a.Join("127.0.0.1", host.LocalPort);
            WorldSyncSession aw = new WorldSyncSession(a, null, WorldVector.Zero); syncs.Add(aw);
            DiagnosticSession b = new DiagnosticSession(Compatibility(), "Bob", new TracedTransport()); networks.Add(b); b.Join("127.0.0.1", host.LocalPort);
            WorldSyncSession bw = new WorldSyncSession(b, null, WorldVector.Zero); syncs.Add(bw);
            foreach (WorldSyncSession sync in syncs) sync.Fault += delegate(string reason) { Console.Error.WriteLine("World fault: " + reason); };
            PumpUntil(networks.ToArray(), syncs.ToArray(), delegate { return aw.Ready && bw.Ready && aw.Replica.Watermark == authority.Watermark && bw.Replica.Watermark == authority.Watermark; }, "Two clients installed baseline, catch-up and ready barrier over UDP");
            int receipts = 0;
            aw.PoseReceipt += delegate(WorldMessage receipt) { Check(receipt.PoseSequence == 42 && !receipt.PoseAccepted && receipt.Position.X == 2, "Receipt retains correlation and authoritative position"); receipts++; };
            hostWorld.SendPoseReceipt(a.LocalPlayerId, 42, false, new WorldVector(2, 0, 0));
            PumpUntil(networks.ToArray(), syncs.ToArray(), delegate { return receipts == 1; }, "Only affected client receives correction over UDP");
            for (ulong id = 1; id <= 24; id++)
                Check(authority.UpdateFreeMotion(authority.Generation, id, 1, 1, new WorldVector(0, 0, 0), WorldVector.Zero), "Shared catalogue body update");
            PumpUntil(networks.ToArray(), syncs.ToArray(), delegate { return aw.Replica.Watermark == authority.Watermark && bw.Replica.Watermark == authority.Watermark; }, "Multi-batch catalogue updates converge in order");
            int results = 0, accepted = 0;
            aw.ReceivedResult += delegate(WorldMessage m) { results++; if (m.Outcome == CommandOutcome.Accepted) accepted++; };
            bw.ReceivedResult += delegate(WorldMessage m) { results++; if (m.Outcome == CommandOutcome.Accepted) accepted++; };
            ulong retry = aw.Request(CommandKind.Pickup, 1, WorldVector.Zero, WorldVector.Zero);
            Check(aw.RetryRequest(retry), "Ambiguous action retry queues original request identity");
            bw.Request(CommandKind.Pickup, 1, WorldVector.Zero, WorldVector.Zero);
            PumpUntil(networks.ToArray(), syncs.ToArray(), delegate { return results == 2 && aw.Replica.Watermark == authority.Watermark && bw.Replica.Watermark == authority.Watermark; }, "Racing pickup replies and authoritative commit converge");
            Check(!aw.RetryRequest(retry), "Correlated result clears retry storage");
            Check(accepted == 1 && authority.GetEntity(1).Lifecycle == EntityLifecycle.Held, "Exactly one UDP contender owns item");
            ulong holder = authority.GetEntity(1).HolderId;
            Check(aw.Replica.GetEntity(1).HolderId == holder && bw.Replica.GetEntity(1).HolderId == holder, "Both clients agree on holder");
            WorldSyncSession holderWorld = a.LocalPlayerId == holder ? aw : bw;
            DiagnosticSession holderNetwork = a.LocalPlayerId == holder ? a : b;
            holderWorld.Request(CommandKind.Throw, 1, new WorldVector(1, 0, 0), new WorldVector(2, 0, 0));
            PumpUntil(networks.ToArray(), syncs.ToArray(), delegate { return authority.GetEntity(1).Lifecycle == EntityLifecycle.Free && aw.Replica.Watermark == authority.Watermark && bw.Replica.Watermark == authority.Watermark; }, "Throw commits velocity and frees hand on both clients");
            Check(aw.Replica.GetEntity(1).Velocity.X == 2 && authority.GetPlayer(holder).HandEntityId == 0, "Throw state coherent");
            holderWorld.Request(CommandKind.Pickup, 1, WorldVector.Zero, WorldVector.Zero);
            PumpUntil(networks.ToArray(), syncs.ToArray(), delegate { return authority.GetEntity(1).Lifecycle == EntityLifecycle.Held && aw.Replica.Watermark == authority.Watermark && bw.Replica.Watermark == authority.Watermark; }, "Pickup before late join");
            DiagnosticSession c = new DiagnosticSession(Compatibility(), "Late"); networks.Add(c); c.Join("127.0.0.1", host.LocalPort);
            WorldSyncSession cw = new WorldSyncSession(c, null, WorldVector.Zero); syncs.Add(cw);
            PumpUntil(networks.ToArray(), syncs.ToArray(), delegate { return cw.Ready && cw.Replica.Watermark == authority.Watermark; }, "Fourth participant late joins held item");
            Check(cw.Replica.GetEntity(1).HolderId == holder && cw.Replica.GetPlayer(holder).HandEntityId == 1, "Late join preserves held relationship");
            int routed = 0;
            c.ApplicationReceived += delegate(ApplicationMessage m)
            {
                if (VehicleMessages.Matches(m.Payload) || (m.Payload.Length >= 4 && m.Payload[0] == 0x4d && m.Payload[1] == 0x41 && m.Payload[2] == 0x56 && m.Payload[3] == 0x32)) routed++;
            };
            Check(host.SendApplication(c.LocalPlayerId, VehicleMessages.Encode(new VehicleMessage { Kind = VehicleMessageKind.State, Revision = 1, Driver = holder, Gear = 1 })), "Vehicle state shares the application transport");
            // The world layer treats avatar bytes as opaque. The avatar decoder owns
            // their schema/validation; world chunk state must not consume these bytes.
            Check(host.SendApplication(c.LocalPlayerId, new byte[] { 0x4d, 0x41, 0x56, 0x32, 2, 1 }), "Avatar namespace shares the application transport");
            PumpUntil(networks.ToArray(), syncs.ToArray(), delegate { return routed == 2; }, "Independent application namespaces delivered");
            Check(cw.Ready && cw.Replica.Watermark == authority.Watermark, "Vehicle/avatar delivery preserves world readiness and watermark");
            holderWorld.Dispose(); holderNetwork.Leave(); syncs.Remove(holderWorld); networks.Remove(holderNetwork);
            PumpUntil(networks.ToArray(), syncs.ToArray(), delegate { return !authority.GetPlayer(holder).Connected && authority.GetEntity(1).Lifecycle == EntityLifecycle.Free && cw.Replica.Watermark == authority.Watermark; }, "Disconnect releases held item and converges");
            Check(cw.Replica.GetPlayer(holder).HandEntityId == 0, "Disconnect clears replicated hand");
            holderNetwork.Dispose();
            // A connected client cannot publish a host-owned snapshot/commit.
            ReliableBlobSender malicious = new ReliableBlobSender();
            malicious.Enqueue(WorldMessages.Encode(new WorldMessage { Kind = WorldMessageKind.Snapshot, Snapshot = authority.CaptureSnapshot() }));
            // New sequence starts at1, whereas the existing world stream has advanced: either framing
            // rejection or authoritative-message rejection must remove only this peer.
            malicious.Pump(delegate(byte[] frame) { return c.SendApplication(1, frame); }, 4);
            PumpUntil(networks.ToArray(), syncs.ToArray(), delegate { return !authority.GetPlayer(c.LocalPlayerId).Connected; }, "Forged world traffic disconnects sender");
            Check(host.IsRunning && host.AcceptedPeerCount >= 1, "Malformed peer does not stop host or healthy peer");
            hostWorld.Dispose();
            Check(!hostWorld.Ready, "Disposed authority cannot advertise ready");
            PumpUntil(networks.ToArray(), syncs.ToArray(), delegate { return !aw.Ready && !bw.Ready && !cw.Ready; }, "World disposal invalidates connected replicas");
        }
        finally { foreach (WorldSyncSession sync in syncs) sync.Dispose(); foreach (DiagnosticSession network in networks) network.Dispose(); }
    }
    private static void StalledBootstrap()
    {
        using (DiagnosticSession host = new DiagnosticSession(Compatibility(), "Host"))
        using (DiagnosticSession client = new DiagnosticSession(Compatibility(), "Stalled"))
        {
            host.Host(0); AuthorityWorld authority = World(host.SessionGeneration); long now = 0;
            using (WorldSyncSession sync = new WorldSyncSession(host, authority, WorldVector.Zero, delegate { return now; }))
            {
                client.Join("127.0.0.1", host.LocalPort);
                PumpUntil(new DiagnosticSession[] { host, client }, new WorldSyncSession[] { sync }, delegate { return client.LocalPlayerId != 0; }, "Stalled peer reaches transport acceptance");
                ulong player = client.LocalPlayerId; Check(authority.GetPlayer(player).Connected && !authority.GetPlayer(player).Ready, "No snapshot ACK means no readiness");
                now = 90001; sync.Poll(); Check(!authority.GetPlayer(player).Connected, "Complete bootstrap deadline releases silent peer");
                host.Leave(); Check(!sync.Ready, "Network leave invalidates host world readiness");
                host.Host(0); Check(!sync.Ready, "Restarted network cannot reuse old authoritative generation");
            }
        }
    }
    public static int Main()
    {
        try { Framing(); Codec(); BatchCodec(); UdpIntegration(); StalledBootstrap(); Console.WriteLine("Sync tests passed: " + assertions + " assertions; real localhost UDP with a host and three clients, synthetic world only."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
