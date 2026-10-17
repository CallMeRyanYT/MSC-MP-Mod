using System;
using System.Collections.Generic;
using MSCMP.Core;
using MSCMP.Network;
using MSCMP.Sync;
using UnityEngine;

namespace MSCMP.Integration
{
    internal sealed class VehicleSession : IDisposable
    {
        private readonly DiagnosticSession network;
        private readonly WorldSyncSession world;
        private readonly VehicleBinding binding;
        private readonly Action<string, string> record;
        private readonly Dictionary<ulong, VehicleMessage> replies = new Dictionary<ulong, VehicleMessage>();
        private readonly VehicleAuthority authority;
        private ulong revision = 1, driver, passenger, request, inputSequence;
        private bool baseline, disposed, queued, pending;
        private int requestedSeat;
        private float nextRequest, nextInput, nextState;
        private VehicleMessage pendingMessage;
        private bool startLatch;
        private int gearLatch = -1;
        internal ulong EntityId;
        internal bool Ready { get { return !disposed && world.Ready && baseline; } }
        internal VehicleAuthority Authority { get { return authority; } }
        internal ulong Driver { get { return network.IsHost ? authority.Driver : driver; } }
        internal ulong Passenger { get { return network.IsHost ? authority.Occupants[1] : passenger; } }
        internal int SeatOf(ulong player) { return player == 0 ? -1 : Driver == player ? 0 : Passenger == player ? 1 : -1; }
        internal void RequestPassenger(bool exit) { if (!pending && !queued) QueueSeat(exit ? -1 : 1); }
        private long Now { get { return (long)(Time.realtimeSinceStartup * 1000); } }

        internal VehicleSession(DiagnosticSession network, WorldSyncSession world, VehicleBinding binding, Action<string, string> record)
        {
            this.network = network; this.world = world; this.binding = binding; this.record = record;
            if (network.IsHost) { authority = new VehicleAuthority(2, binding.GearCount); baseline = true; }
            binding.SeatRequested = QueueSeat;
            binding.ApprovedInput = delegate { return authority == null ? VehicleInput.Stopped : authority.ReadInput(Now); };
            binding.Failure = delegate(Exception error) { record("vehicle.error", error.ToString()); network.Leave(); };
            network.ApplicationReceived += Receive; network.Event += OnEvent;
            try { binding.Activate(network.IsHost); } catch { Dispose(); throw; }
        }
        private void QueueSeat(int seat)
        {
            if (!Ready || pending || queued) { binding.CompleteSeat(seat, false); return; }
            requestedSeat = seat; queued = true;
        }
        private void OnEvent(SessionEvent value)
        {
            if (value.Kind == "Disconnected")
            {
                replies.Remove(value.PlayerId);
                if (authority != null && authority.Disconnect(value.PlayerId)) nextState = 0;
                if (!network.IsHost) binding.ReleaseLocal();
            }
        }
        private void Receive(ApplicationMessage incoming)
        {
            if (disposed || !VehicleMessages.Matches(incoming.Payload)) return;
            try
            {
                VehicleMessage message = VehicleMessages.Decode(incoming.Payload);
                if (network.IsHost)
                {
                    if (message.Kind == VehicleMessageKind.Request) HandleRequest(incoming.PlayerId, incoming.ConnectionGeneration, message);
                    else if (message.Kind == VehicleMessageKind.Input)
                    {
                        PlayerState player = world.Authority.GetPlayer(incoming.PlayerId);
                        if (player != null && player.Connected && player.Ready && player.ConnectionGeneration == incoming.ConnectionGeneration)
                            authority.AcceptInput(incoming.PlayerId, message.Revision, message.Sequence, message.Input, Now);
                    }
                    else throw new FormatException("Client attempted vehicle authority.");
                }
                else
                {
                    if (incoming.PlayerId != 1 || (message.Kind != VehicleMessageKind.State && message.Kind != VehicleMessageKind.Result)) throw new FormatException("Invalid vehicle host message.");
                    if (message.Revision >= revision)
                    {
                        if (message.Revision == revision && baseline && (message.Driver != driver || message.Passenger != passenger)) throw new FormatException("Conflicting seat revision.");
                        if (message.Revision > revision) { inputSequence = 0; gearLatch = -1; startLatch = false; }
                        revision = message.Revision; driver = message.Driver; passenger = message.Passenger; baseline = true;
                        if (message.Kind == VehicleMessageKind.State) binding.SetReplicaGear(message.Gear);
                    }
                    if (message.Kind == VehicleMessageKind.Result && pending && message.Sequence == request)
                    { pending = false; binding.CompleteSeat(requestedSeat, message.Outcome == SeatOutcome.Accepted && SeatOf(network.LocalPlayerId) == requestedSeat); record("vehicle.seat-result", message.Outcome.ToString()); }
                }
            }
            catch (Exception error) { record("vehicle.error", error.GetType().Name); network.DisconnectPlayer(incoming.PlayerId, "Invalid vehicle message"); }
        }
        private void HandleRequest(ulong player, ulong connection, VehicleMessage message)
        {
            EntityState entity = world.Authority.GetEntity(EntityId);
            SeatOutcome outcome = entity == null ? SeatOutcome.NotReady : authority.Request(world.Authority.GetPlayer(player), connection,
                message.Sequence, message.Revision, message.Seat, entity.Position, SharedWorldSession.Vector(binding.Body.velocity));
            VehicleMessage response = new VehicleMessage { Kind = VehicleMessageKind.Result, Revision = authority.Revision, Sequence = message.Sequence, Driver = authority.Driver, Passenger = authority.Occupants[1], Outcome = outcome };
            if (player == 1) { binding.CompleteSeat(message.Seat, outcome == SeatOutcome.Accepted); record("vehicle.seat-result", outcome.ToString()); }
            else replies[player] = response;
            nextState = 0;
        }
        internal void Poll(bool panelVisible)
        {
            if (disposed) return;
            float now = Time.realtimeSinceStartup;
            if (network.IsHost)
            {
                foreach (ulong occupantId in authority.Occupants)
                {
                    if (occupantId == 0) continue;
                    PlayerState occupant = world.Authority.GetPlayer(occupantId);
                    if (occupant == null || !occupant.Connected) authority.Disconnect(occupantId);
                }
                foreach (ulong peer in new List<ulong>(replies.Keys)) if (network.SendApplication(peer, VehicleMessages.Encode(replies[peer]))) replies.Remove(peer);
                if (now >= nextState)
                {
                    nextState = now + 0.2f;
                    network.BroadcastApplication(VehicleMessages.Encode(new VehicleMessage { Kind = VehicleMessageKind.State, Revision = authority.Revision, Driver = authority.Driver, Passenger = authority.Occupants[1], Gear = binding.CurrentGear }));
                }
            }
            if (!Ready) return;
            if (queued)
            {
                queued = false; request = checked(request + 1);
                pendingMessage = new VehicleMessage { Kind = VehicleMessageKind.Request, Sequence = request, Revision = network.IsHost ? authority.Revision : revision, Seat = requestedSeat };
                if (network.IsHost) HandleRequest(1, 1, pendingMessage); else { pending = true; nextRequest = 0; }
            }
            if (pending && now >= nextRequest) { nextRequest = now + 1; network.SendApplication(1, VehicleMessages.Encode(pendingMessage)); }
            if (Driver != network.LocalPlayerId) { startLatch = false; gearLatch = -1; return; }
            VehicleInput input = panelVisible || Time.timeScale == 0 ? VehicleInput.Stopped : binding.ReadLocalInput();
            startLatch |= input.StartEngine;
            if (input.Gear != binding.CurrentGear) gearLatch = input.Gear;
            if (now < nextInput) return;
            nextInput = now + 0.1f; input.StartEngine = startLatch; if (gearLatch >= 0) input.Gear = gearLatch;
            ulong sequence = checked(inputSequence + 1);
            bool sent;
            if (network.IsHost) sent = authority.AcceptInput(1, authority.Revision, sequence, input, Now);
            else sent = network.SendApplication(1, VehicleMessages.Encode(new VehicleMessage { Kind = VehicleMessageKind.Input, Revision = revision, Sequence = sequence, Input = input }));
            if (sent) { inputSequence = sequence; startLatch = false; gearLatch = -1; }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            network.ApplicationReceived -= Receive; network.Event -= OnEvent;
            binding.SeatRequested = null; binding.ApprovedInput = null; binding.Failure = null; binding.Dispose();
        }
    }
}
