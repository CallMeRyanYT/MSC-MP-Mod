using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MSCMP.Core;
using MSCMP.Network;

namespace MSCMP.Sync
{
    // Executable world replication foundation. A game adapter must explicitly construct it;
    // diagnostic acceptance alone never starts item replication or grants game input.
    public sealed class WorldSyncSession : IDisposable
    {
        private sealed class Link
        {
            internal ulong Player, Connection, Cursor, Snapshot;
            internal bool Ready;
            internal long JoinDeadline;
            internal int Baselines;
            internal readonly ReliableBlobSender Sender = new ReliableBlobSender();
            internal readonly ReliableBlobReceiver Receiver = new ReliableBlobReceiver();
        }
        private readonly DiagnosticSession network;
        private readonly AuthorityWorld authority;
        private readonly WorldVector spawn;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Func<long> testClock;
        private long Now { get { return testClock == null ? clock.ElapsedMilliseconds : testClock(); } }
        private readonly Dictionary<ulong, Link> links = new Dictionary<ulong, Link>();
        private bool disposed;
        private ReplicaWorld replica;
        private ulong clientSnapshot;
        private ulong requestSequence;
        private ulong acceptedWorldGeneration;
        public event Action<string> Fault;
        public event Action<WorldMessage> ReceivedResult;
        // Game adapters prove authored bindings before acknowledging or applying a baseline.
        public Action<WorldSnapshot> ValidateSnapshot;
        public AuthorityWorld Authority { get { return authority; } }
        public ReplicaWorld Replica { get { return replica; } }
        public bool Ready { get { return !disposed && network.IsRunning && acceptedWorldGeneration == network.SessionGeneration
            && (authority != null || (replica != null && replica.Ready)); } }

        public WorldSyncSession(DiagnosticSession network, AuthorityWorld authority, WorldVector spawn)
            : this(network, authority, spawn, null) { }

        internal WorldSyncSession(DiagnosticSession network, AuthorityWorld authority, WorldVector spawn, Func<long> monotonicTestClock)
        {
            if (network == null || !network.IsRunning || network.IsHost != (authority != null)) throw new ArgumentException("Start the matching network role first.");
            if (authority != null && (authority.Generation != network.SessionGeneration || authority.GetPlayer(1) == null || !authority.GetPlayer(1).Ready)) throw new ArgumentException("Authority must have the current generation and ready host identity.");
            this.network = network; this.authority = authority; this.spawn = spawn;
            testClock = monotonicTestClock;
            if (authority != null) acceptedWorldGeneration = authority.Generation;
            network.Event += OnSessionEvent; network.ApplicationReceived += OnApplication;
            foreach (SessionPeer peer in network.GetAcceptedPeers()) Add(peer.PlayerId, peer.ConnectionGeneration);
        }
        private void OnSessionEvent(SessionEvent value)
        {
            if (disposed) return;
            if (value.Kind == "Accepted") Add(value.PlayerId, value.ConnectionGeneration);
            else if (value.Kind == "Disconnected") Remove(value.PlayerId, value.ConnectionGeneration);
            else if (value.Kind == "Stopped")
            {
                foreach (Link link in new List<Link>(links.Values)) Remove(link.Player, link.Connection);
                replica = null;
            }
        }
        private void Add(ulong player, ulong connection)
        {
            if (disposed) return;
            if (acceptedWorldGeneration != 0 && acceptedWorldGeneration != network.SessionGeneration)
            { Fail(player, "World generation changed; construct a new world adapter"); return; }
            acceptedWorldGeneration = network.SessionGeneration;
            if (links.ContainsKey(player)) { Fail(player, "Duplicate active world link"); return; }
            Link link = new Link { Player = player, Connection = connection, JoinDeadline = Now + 90000 }; links.Add(player, link);
            if (authority != null)
            {
                try { authority.ConnectPlayer(player, connection, spawn, false); StartBaseline(link); }
                catch (Exception) { Fail(player, "World player admission failed"); }
            }
            else { replica = new ReplicaWorld(network.SessionGeneration); requestSequence = 0; }
        }
        private void StartBaseline(Link link)
        {
            if (++link.Baselines > 3) throw new FormatException("Bootstrap restart limit exceeded.");
            WorldSnapshot snapshot = authority.BeginJoin(link.Player, link.Connection);
            link.Snapshot = snapshot.SnapshotId; link.Cursor = snapshot.Watermark; link.Ready = false;
            Queue(link, new WorldMessage { Kind = WorldMessageKind.Snapshot, Snapshot = snapshot });
        }
        private void Remove(ulong player, ulong connection)
        {
            Link link;
            if (!links.TryGetValue(player, out link) || link.Connection != connection) return;
            links.Remove(player);
            if (authority != null) authority.DisconnectPlayer(player, connection); else replica = null;
        }
        private void OnApplication(ApplicationMessage application)
        {
            if (disposed) return;
            // Avatar presentation shares the reliable application stream. Its own decoder
            // validates MAV2; it must never enter the stateful world-chunk receiver.
            byte[] payload = application.Payload;
            if (VehicleMessages.Matches(payload)) return;
            if (payload != null && payload.Length >= 4 && payload[0] == 0x4d && payload[1] == 0x41 && payload[2] == 0x56 && payload[3] == 0x32) return;
            Link link;
            if (!links.TryGetValue(application.PlayerId, out link) || link.Connection != application.ConnectionGeneration) return;
            try
            {
                byte[] bytes = link.Receiver.Receive(application.Payload, Now);
                if (bytes == null) return;
                WorldMessage message = WorldMessages.Decode(bytes, application.PlayerId, application.ConnectionGeneration);
                if (authority != null) ReceiveHost(link, message); else ReceiveClient(link, message);
            }
            catch (Exception error)
            {
                // No exception messages: wire strings and filesystem paths never enter diagnostics.
                Fail(link.Player, "World message rejected (" + error.GetType().Name + ")");
            }
        }
        private void ReceiveHost(Link link, WorldMessage message)
        {
            switch (message.Kind)
            {
                case WorldMessageKind.SnapshotAck:
                    if (link.Ready) throw new FormatException("Already ready.");
                    SendCatchUp(link, message.SnapshotId, message.Watermark); break;
                case WorldMessageKind.ReadyRequest:
                    ReadyGrant grant = authority.ConfirmReady(link.Player, link.Connection, message.SnapshotId, message.Watermark);
                    if (grant.Status == ReadyStatus.MoreChanges) SendCatchUp(link, message.SnapshotId, message.Watermark);
                    else if (grant.Status == ReadyStatus.RestartRequired) StartBaseline(link);
                    else if (grant.Status == ReadyStatus.Ready)
                    {
                        Queue(link, new WorldMessage { Kind = WorldMessageKind.Ready, Grant = grant });
                        link.Ready = true; link.Cursor = grant.Commit.Sequence;
                    }
                    else throw new FormatException("Invalid readiness request.");
                    break;
                case WorldMessageKind.Command:
                    CommandResult result = authority.Process(message.Command);
                    Queue(link, new WorldMessage { Kind = WorldMessageKind.Result, Outcome = result.Outcome, RequestSequence = result.RequestSequence, Watermark = result.Watermark });
                    break;
                default: throw new FormatException("Client attempted an authoritative message.");
            }
        }
        private void SendCatchUp(Link link, ulong snapshot, ulong watermark)
        {
            CatchUpBatch batch = authority.ReadCatchUp(link.Player, link.Connection, snapshot, watermark);
            if (batch.Status == CatchUpStatus.RestartRequired) StartBaseline(link);
            else if (batch.Status != CatchUpStatus.Success) throw new FormatException("Invalid snapshot acknowledgement.");
            else Queue(link, new WorldMessage { Kind = WorldMessageKind.CatchUp, CatchUp = batch });
        }
        private void ReceiveClient(Link link, WorldMessage message)
        {
            switch (message.Kind)
            {
                case WorldMessageKind.Snapshot:
                    if (++link.Baselines > 3) throw new FormatException("Bootstrap restart limit exceeded.");
                    link.Ready = false;
                    if (ValidateSnapshot != null) ValidateSnapshot(message.Snapshot);
                    if (replica.LoadSnapshot(message.Snapshot) != ApplyOutcome.Applied) throw new FormatException("Invalid world baseline.");
                    clientSnapshot = message.Snapshot.SnapshotId;
                    Queue(link, new WorldMessage { Kind = WorldMessageKind.SnapshotAck, SnapshotId = clientSnapshot, Watermark = replica.Watermark }); break;
                case WorldMessageKind.CatchUp:
                    if (replica.ApplyCatchUp(message.CatchUp) != ApplyOutcome.Applied) throw new FormatException("Invalid catch-up.");
                    Queue(link, new WorldMessage { Kind = WorldMessageKind.ReadyRequest, SnapshotId = clientSnapshot, Watermark = replica.Watermark }); break;
                case WorldMessageKind.Ready:
                    if (!replica.ApplyReady(message.Grant, network.LocalPlayerId, link.Connection)) throw new FormatException("Invalid ready grant.");
                    link.Ready = true; break;
                case WorldMessageKind.Commit:
                    ApplyOutcome outcome = replica.Apply(message.Commit);
                    if (outcome != ApplyOutcome.Applied && outcome != ApplyOutcome.DuplicateOrStale) throw new FormatException("Invalid live commit."); break;
                case WorldMessageKind.Result:
                    Action<WorldMessage> callback = ReceivedResult; if (callback != null) callback(message); break;
                default: throw new FormatException("Host sent an invalid world message.");
            }
        }
        public void Poll()
        {
            if (disposed) return;
            foreach (Link link in new List<Link>(links.Values))
            {
                try
                {
                    if (!link.Ready && Now > link.JoinDeadline) throw new FormatException("Bootstrap deadline exceeded.");
                    link.Receiver.CheckTimeout(Now);
                    if (authority != null && link.Ready && link.Sender.PendingCount < 16)
                    {
                        CatchUpBatch live = authority.ReadEventsSince(link.Cursor);
                        if (live.Status != CatchUpStatus.Success) throw new FormatException("Live history expired; reconnect required.");
                        foreach (CommitEvent commit in live.Events)
                        {
                            // Bounded backpressure: advance only once this event is queued.
                            if (link.Sender.PendingCount >= 16) break;
                            Queue(link, new WorldMessage { Kind = WorldMessageKind.Commit, Commit = commit }); link.Cursor = commit.Sequence;
                        }
                    }
                    link.Sender.Pump(delegate(byte[] bytes) { return network.SendApplication(link.Player, bytes); }, 4);
                }
                catch (Exception error) { Fail(link.Player, "World transfer stopped (" + error.GetType().Name + ")"); }
            }
        }
        // No prediction of ownership. The adapter waits for the committed state; a timeout is uncertain.
        public ulong Request(CommandKind kind, ulong entityId, WorldVector position, WorldVector velocity)
        {
            if (disposed || !Ready) throw new InvalidOperationException("World baseline is not ready.");
            if (requestSequence == UInt64.MaxValue) throw new InvalidOperationException("Request sequence exhausted.");
            EntityState entity = authority != null ? authority.GetEntity(entityId) : replica.GetEntity(entityId);
            if (entity == null) throw new ArgumentException("Unknown entity.");
            ulong connection = authority != null ? authority.GetPlayer(1).ConnectionGeneration : links[1].Connection;
            AuthorityCommand command = new AuthorityCommand(kind, network.SessionGeneration, network.LocalPlayerId, connection, ++requestSequence,
                entity.Id, entity.Revision, entity.OwnershipEpoch, position, velocity);
            if (authority != null)
            {
                CommandResult result = authority.Process(command); Action<WorldMessage> callback = ReceivedResult;
                if (callback != null) callback(new WorldMessage { Kind = WorldMessageKind.Result, Outcome = result.Outcome, RequestSequence = result.RequestSequence, Watermark = result.Watermark });
            }
            else Queue(links[1], new WorldMessage { Kind = WorldMessageKind.Command, Command = command });
            return requestSequence;
        }
        private static void Queue(Link link, WorldMessage message)
        { if (!link.Sender.Enqueue(WorldMessages.Encode(message))) throw new FormatException("World send queue full."); }
        private void Fail(ulong player, string reason)
        {
            Link link; if (links.TryGetValue(player, out link)) Remove(player, link.Connection);
            network.DisconnectPlayer(player, "World protocol or capacity error");
            Action<string> callback = Fault; if (callback != null) callback(reason);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            network.Event -= OnSessionEvent; network.ApplicationReceived -= OnApplication;
            foreach (Link link in new List<Link>(links.Values))
            {
                Remove(link.Player, link.Connection);
                network.DisconnectPlayer(link.Player, "World session ended");
            }
            links.Clear(); replica = null;
        }
    }
}
