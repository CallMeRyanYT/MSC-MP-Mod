using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCMP.Core;
using MSCMP.Network;
using MSCMP.Sync;
using MSCMP.Persistence;
using UnityEngine;

namespace MSCMP.Integration
{
    // Explicit game adapter. No Unity object, input or save access enters the network worker.
    // Persistent item identities are resolved once; only host physics mutates shared bodies.
    internal sealed class SharedWorldSession : IDisposable
    {
        private readonly DiagnosticSession network;
        private readonly int generation;
        private readonly Action<string, string> record;
        private LocalPlayerBinding player;
        private SupportedItemBinding cup;
        private WorldSyncSession world;
        private VehicleBinding van;
        private VehicleSession vehicle;
        private PassengerBinding passenger;
        private ulong vehicleId;
        private List<SharedItemBinding> items;
        private PlayMakerFSM saveItems;
        private bool pickupEnabled, saveItemsEnabled;
        private int motionCursor;
        private bool changed, disposed, failed, wasReady, vehiclePresented;
        private float nextMotion, nextPose, nextDriverPose, requestAt;
        private ulong pendingRequest, resultWatermark;
        private WorldMessage pendingResult;
        private float retryAt;
        private readonly PlayerPoseHistory poseHistory = new PlayerPoseHistory();
        private WorldMessage correction;
        internal Vector3? AcceptedPosition(ulong id)
        {
            if (!Ready) return null;
            PlayerState state = network.IsHost ? world.Authority.GetPlayer(id) : world.Replica.GetPlayer(id);
            return state == null || !state.Ready ? (Vector3?)null : Vector(state.Position);
        }
        private string status = "Loading shared world";
        internal string Status { get { return status; } }
        internal bool Ready { get { return !disposed && !failed && world != null && world.Ready && items != null && items.Count != 0 && items[0].EntityId != 0 && vehicle != null && vehicle.Ready; } }
        internal bool LocallyDriving { get { return Ready && vehicle.Driver == network.LocalPlayerId; } }
        internal bool LocallySeated { get { return Ready && vehicle.SeatOf(network.LocalPlayerId) >= 0; } }
        internal bool LocallyPassenger { get { return Ready && vehicle.Passenger == network.LocalPlayerId; } }
        internal void RequestDriverSeat() { if (Ready && !LocallyPassenger) van.RequestSeat(LocallyDriving); }
        internal void RequestPassengerSeat()
        {
            if (!Ready || LocallyDriving) return;
            if (LocallyPassenger && !passenger.PrepareExit()) { status = "No clear passenger exit. Move the van to open ground."; return; }
            vehicle.RequestPassenger(LocallyPassenger);
        }
        internal bool IsSeated(ulong id) { return Ready && vehicle.SeatOf(id) >= 0; }
        internal Vector3? SeatPosition(ulong id) { return IsSeated(id) ? (Vector3?)van.SeatFeet(vehicle.SeatOf(id)) : null; }
        internal float SeatYaw(ulong id) { return van.SeatYaw; }
        internal Func<ulong, Vector3?> CarryPosition;
        internal bool IsDriver(ulong id) { return Ready && vehicle.Driver == id; }
        internal bool IsHolding(ulong id)
        {
            if (!Ready) return false;
            PlayerState state = network.IsHost ? world.Authority.GetPlayer(id) : world.Replica.GetPlayer(id);
            return state != null && state.HandEntityId != 0;
        }

        internal SharedWorldSession(DiagnosticSession active, int sceneGeneration, Action<string, string> recorder, WorldSnapshot restored)
        {
            network = active; generation = sceneGeneration; record = recorder;
            try
            {
                DisposableSessionGuard.RequireActive();
                string reason;
                if (!LocalPlayerBinding.TryCreate(generation, out player, out reason)) throw new InvalidOperationException(reason);
                if (!SupportedItemBinding.TryCreate(generation, out cup, out reason) || !cup.CanBeginSharedSession(out reason)) throw new InvalidOperationException(reason);
                items = SharedItemBinding.Discover(cup, out saveItems);
                van = VehicleBinding.Create();
                Vector3 position; float yaw;
                if (!player.TryInitialPose(generation, out position, out yaw)) throw new InvalidOperationException("A loaded walking player is required.");
                pickupEnabled = cup.PickupFsm.enabled; saveItemsEnabled = saveItems.enabled;
                changed = true; cup.PickupFsm.enabled = false; saveItems.enabled = false;
                foreach (SharedItemBinding item in items) item.Begin(network.IsHost);
                AuthorityWorld authority = null;
                if (network.IsHost)
                {
                    // Native ray is one metre; root-to-item bound includes the camera height.
                    authority = new AuthorityWorld(network.SessionGeneration, new AuthorityLimits(3, 3, 4, 100000, 8, 64, 64), 128, 4096);
                    if (restored != null)
                    {
                        ValidateRestore(restored);
                        authority.RestoreEntities(restored);
                        foreach (PlayerState saved in restored.Players) if (saved.Id == 1) { position = Vector(saved.Position); player.PlaceAt(position); }
                    }
                    authority.ConnectPlayer(1, 1, Vector(position), true);
                    foreach (SharedItemBinding item in items)
                    {
                        EntityState entity = restored == null ? authority.RegisterAuthoredItem(item.Key, item.Archetype, Vector(item.Body.position)) : RestoredEntity(authority, restored, item.Key);
                        if (restored != null) RestoreBody(item.Body, entity);
                        item.EntityId = entity.Id;
                        authority.UpdateFreeMotion(authority.Generation, entity.Id, entity.OwnershipEpoch, 1, Vector(item.Body.position), Vector(item.Body.velocity), Rotation(item.Body.rotation), Vector(item.Body.angularVelocity));
                    }
                    EntityState vanEntity = restored == null ? authority.RegisterAuthoredItem(VehicleBinding.Key, VehicleBinding.Archetype, Vector(van.Body.position)) : RestoredEntity(authority, restored, VehicleBinding.Key); vehicleId = vanEntity.Id;
                    if (restored != null) RestoreBody(van.Body, vanEntity);
                    authority.UpdateFreeMotion(authority.Generation, vanEntity.Id, vanEntity.OwnershipEpoch, 1, Vector(van.Body.position), Vector(van.Body.velocity), Rotation(van.Body.rotation), Vector(van.Body.angularVelocity));
                    authority.InteractionAllowed = delegate(AuthorityCommand command) { return FindItem(command.EntityId) != null && (vehicle == null || vehicle.SeatOf(command.PlayerId) < 0); };
                }
                world = new WorldSyncSession(network, authority, Vector(position));
                world.ValidateSnapshot = ValidateMapping;
                world.Fault += OnFault; world.ReceivedResult += OnResult; world.PoseReceipt += OnPoseReceipt;
                vehicle = new VehicleSession(network, world, van, record); vehicle.EntityId = vehicleId;
                passenger = new PassengerBinding(player, van);
                van.CanEnterDriver = delegate { return !LocallyPassenger; };
                network.PoseReceived += OnPose;
                record("world.started", "disposable profile; items=" + items.Count + "; role=" + (network.IsHost ? "host" : "client"));
            }
            catch { Dispose(); throw; }
        }

        internal void Save(CheckpointStore store)
        {
            if (!network.IsHost || !Ready) throw new InvalidOperationException("Only a ready host can save the shared world.");
            if (vehicle.Driver != 0 || vehicle.Passenger != 0) throw new InvalidOperationException("Everyone must leave the van before saving.");
            DisposableSessionGuard.RequireActive();
            foreach (SharedItemBinding item in items) PublishMotion(item.EntityId, item.Body);
            PublishMotion(vehicleId, van.Body);
            // Main-thread capture: no network action or physics tick can interleave.
            store.Save(world.Authority.CaptureSnapshot());
            status = "Shared items and van saved. Use the game's Save & Quit for native progression.";
            record("world.saved", "host checkpoint committed; native save requires Save & Quit");
        }
        private void ValidateRestore(WorldSnapshot snapshot)
        {
            if (snapshot.Entities.Length != items.Count + 1) throw new InvalidOperationException("Saved item catalogue differs from this build.");
            List<AuthoredCandidate> candidates = new List<AuthoredCandidate>();
            foreach (SharedItemBinding item in items) candidates.Add(new AuthoredCandidate(item.Key, item.Archetype, item.Object));
            candidates.Add(new AuthoredCandidate(VehicleBinding.Key, VehicleBinding.Archetype, van.Body));
            AuthoredMapping.Resolve(snapshot.Entities, candidates.ToArray());
            bool hostFound = false;
            foreach (PlayerState saved in snapshot.Players) if (saved.Id == 1 && saved.Position.Within(100000)) hostFound = true;
            if (!hostFound) throw new InvalidOperationException("Saved host position is missing.");
        }
        private static EntityState RestoredEntity(AuthorityWorld authority, WorldSnapshot snapshot, string key)
        {
            foreach (EntityState entity in snapshot.Entities) if (entity.AuthoredKey == key) return authority.GetEntity(entity.Id);
            throw new InvalidOperationException("Saved authored entity is missing.");
        }
        private static void RestoreBody(Rigidbody body, EntityState state)
        { body.position = Vector(state.Position); body.rotation = Rotation(state.Rotation); body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }

        private void ValidateMapping(WorldSnapshot snapshot)
        {
            EntityState[] entities = snapshot.Entities;
            if (entities.Length != items.Count + 1)
                throw new InvalidOperationException("The host's supported item map differs from this game.");
            List<AuthoredCandidate> candidates = new List<AuthoredCandidate>();
            foreach (SharedItemBinding item in items) candidates.Add(new AuthoredCandidate(item.Key, item.Archetype, item.Object));
            candidates.Add(new AuthoredCandidate(VehicleBinding.Key, VehicleBinding.Archetype, van.Body));
            AuthoredMapping.Resolve(entities, candidates.ToArray());
            foreach (EntityState entity in entities)
            {
                foreach (SharedItemBinding item in items) if (entity.AuthoredKey == item.Key) item.EntityId = entity.Id;
                if (entity.AuthoredKey == VehicleBinding.Key) vehicleId = entity.Id;
            }
            vehicle.EntityId = vehicleId;
            // Start the isolated client at its host-assigned join position. This touches
            // only the loaded disposable scene, never the backed-up personal profile.
            foreach (PlayerState state in snapshot.Players) if (state.Id == network.LocalPlayerId) player.PlaceAt(Vector(state.Position));
        }
        private void OnFault(string reason)
        {
            // WorldSyncSession already revokes the offending peer and releases its
            // held item. A bad peer must not freeze the host's healthy world adapter.
            failed = !network.IsHost; status = reason; record("world.error", reason);
        }
        private void OnResult(WorldMessage result)
        {
            // Wait until the corresponding committed state is applied before another click.
            // Host callbacks are synchronous, before Request returns its sequence.
            if (!network.IsHost && result.RequestSequence != pendingRequest) return;
            pendingResult = result; resultWatermark = result.Watermark;
        }
        private void OnPose(PoseMessage pose)
        {
            if (!network.IsHost || world == null || !Ready) return;
            if (vehicle.SeatOf(pose.PlayerId) >= 0) return;
            PlayerState state = world.Authority.GetPlayer(pose.PlayerId);
            if (state == null || !state.Ready) return;
            bool accepted = world.Authority.AcceptPlayerPose(network.SessionGeneration, pose.PlayerId, pose.ConnectionGeneration, checked(state.PoseSequence + 1), new WorldVector(pose.X, pose.Y, pose.Z));
            world.SendPoseReceipt(pose.PlayerId, pose.Sequence, accepted, world.Authority.GetPlayer(pose.PlayerId).Position);
        }
        private void OnPoseReceipt(WorldMessage receipt)
        { if (correction == null || correction.PoseAccepted) correction = receipt; }
        internal void Poll(bool panelVisible)
        {
            if (disposed) return;
            if (Application.loadedLevelName != "GAME" || !network.IsRunning || cup.ItemObject == null) { Dispose(); return; }
            world.Poll();
            vehicle.Poll(panelVisible);
            passenger.SetSeated(LocallyPassenger);
            if (!Ready) { status = failed ? status : "Waiting for the host's item map and world baseline"; return; }
            if (!wasReady) { wasReady = true; status = "Shared items and van ready (" + items.Count + " items)"; record("world.ready", "authored map and baseline ready"); }
            float now = Time.realtimeSinceStartup;
            Vector3 position; float yaw;
            bool walking = player.TrySample(generation, out position, out yaw);
            if (LocallySeated) { poseHistory.Clear(); correction = null; }
            if (walking && !LocallySeated && correction != null)
            {
                WorldVector corrected;
                if (poseHistory.Receive(correction.PoseSequence, correction.PoseAccepted, correction.Position, Vector(position), out corrected))
                {
                    player.PlaceAt(Vector(corrected)); position = Vector(corrected);
                    record("world.player-corrected", "host rejected movement; local root reconciled");
                }
                correction = null;
            }
            if ((walking || (LocallySeated && player.TrySampleSeated(out position, out yaw))) && now >= nextPose)
            {
                nextPose = now + 0.1f;
                if (network.IsHost && !LocallySeated)
                {
                    PlayerState host = world.Authority.GetPlayer(1);
                    if (!world.Authority.AcceptPlayerPose(network.SessionGeneration, 1, 1, checked(host.PoseSequence + 1), Vector(position)))
                    { player.PlaceAt(Vector(host.Position)); position = Vector(host.Position); }
                }
                if (network.SendPose(position.x, position.y, position.z, yaw) && !network.IsHost && !LocallySeated)
                    poseHistory.Sent(network.LastSentPoseSequence, Vector(position));
            }
            if (network.IsHost && now >= nextDriverPose)
            {
                nextDriverPose = now + 0.1f;
                foreach (ulong id in vehicle.Authority.Occupants)
                {
                    PlayerState occupant = world.Authority.GetPlayer(id);
                    if (occupant != null) world.Authority.AcceptPlayerPose(network.SessionGeneration, id, occupant.ConnectionGeneration,
                        checked(occupant.PoseSequence + 1), Vector(van.SeatFeet(vehicle.SeatOf(id))));
                }
            }
            foreach (SharedItemBinding item in items)
            {
                EntityState state = Entity(item.EntityId);
                if (state == null) throw new InvalidOperationException("The mapped shared entity disappeared.");
                Apply(item, state);
            }
            if (network.IsHost && now >= nextMotion)
            {
                nextMotion = now + 0.1f;
                // Keep item bursts within the existing per-peer application budget.
                // Round-robin fairness prevents a moving near item starving a distant one.
                int sent = 0;
                for (int visited = 0; visited < items.Count && sent < 8; visited++)
                {
                    SharedItemBinding item = items[motionCursor++ % items.Count];
                    if (motionCursor >= items.Count) motionCursor = 0;
                    if (PublishMotion(item.EntityId, item.Body)) sent++;
                }
                PublishMotion(vehicleId, van.Body);
            }
            if (!network.IsHost)
            {
                EntityState car = world.Replica.GetEntity(vehicleId);
                Vector3 target = Vector(car.Position);
                float blend = !vehiclePresented || (van.Body.position - target).sqrMagnitude > 100f ? 1f : Mathf.Clamp01(Time.unscaledDeltaTime * 15f);
                van.Body.position = Vector3.Lerp(van.Body.position, target, blend);
                van.Body.rotation = Quaternion.Slerp(van.Body.rotation, Rotation(car.Rotation), blend);
                vehiclePresented = true;
            }
            if (pendingResult != null && (network.IsHost || world.Replica.Watermark >= resultWatermark))
            {
                status = pendingResult.Outcome == CommandOutcome.Accepted ? "Shared action accepted" : "Action refused: " + pendingResult.Outcome;
                record("world.result", "request=" + pendingResult.RequestSequence + " outcome=" + pendingResult.Outcome);
                pendingRequest = 0; pendingResult = null;
            }
            if (pendingRequest != 0 && pendingResult == null && now >= retryAt)
            { retryAt = now + 1; world.RetryRequest(pendingRequest); }
            if (pendingRequest != 0 && now - requestAt > 5) status = "Waiting for the host's action result; ownership has not been guessed";
            if (!walking || LocallySeated || panelVisible || Time.timeScale == 0 || pendingRequest != 0) return;
            PlayerState local = network.IsHost ? world.Authority.GetPlayer(network.LocalPlayerId) : world.Replica.GetPlayer(network.LocalPlayerId);
            SharedItemBinding held = FindItem(local.HandEntityId);
            bool own = held != null;
            if (own && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)))
            {
                bool throwing = Input.GetMouseButtonDown(1);
                Vector3 release = player.View.transform.position + player.View.transform.forward * 0.7f;
                Request(held.EntityId, throwing ? CommandKind.Throw : CommandKind.Drop, Vector(release), throwing ? Vector(player.View.transform.forward * 4f) : WorldVector.Zero);
            }
            else if (!own && Input.GetMouseButtonDown(0))
            {
                RaycastHit hit;
                if (Physics.Raycast(player.View.transform.position, player.View.transform.forward, out hit, 1f, (1 << 19) | (1 << 15) | (1 << 9)))
                    foreach (SharedItemBinding item in items) if (item.Hit(hit.collider)) { Request(item.EntityId, CommandKind.Pickup, WorldVector.Zero, WorldVector.Zero); break; }
            }
        }
        private void Request(ulong entityId, CommandKind kind, WorldVector position, WorldVector velocity)
        {
            requestAt = Time.realtimeSinceStartup; retryAt = requestAt + 1;
            ulong sequence = world.Request(kind, entityId, position, velocity);
            if (!network.IsHost) pendingRequest = sequence;
        }
        private EntityState Entity(ulong id) { return network.IsHost ? world.Authority.GetEntity(id) : world.Replica.GetEntity(id); }
        private SharedItemBinding FindItem(ulong id)
        { if (items != null) foreach (SharedItemBinding item in items) if (id != 0 && item.EntityId == id) return item; return null; }
        private bool PublishMotion(ulong id, Rigidbody body)
        {
            EntityState state = world.Authority.GetEntity(id);
            if (state == null || state.Lifecycle != EntityLifecycle.Free) return false;
            if (Vector(body.position).Same(state.Position) && Rotation(body.rotation).Same(state.Rotation) && Vector(body.velocity).Same(state.Velocity) && Vector(body.angularVelocity).Same(state.AngularVelocity)) return false;
            if (!world.Authority.UpdateFreeMotion(network.SessionGeneration, id, state.OwnershipEpoch, checked(state.MotionSequence + 1), Vector(body.position), Vector(body.velocity), Rotation(body.rotation), Vector(body.angularVelocity)))
                throw new InvalidOperationException("Host body motion exceeded authority bounds.");
            return true;
        }
        private void Apply(SharedItemBinding item, EntityState entity)
        {
            Rigidbody body = item.Body;
            bool held = entity.Lifecycle == EntityLifecycle.Held;
            item.SetHeld(held, network.IsHost);
            if (held)
            {
                PlayerState holder = network.IsHost ? world.Authority.GetPlayer(entity.HolderId) : world.Replica.GetPlayer(entity.HolderId);
                if (holder == null) throw new InvalidOperationException("Missing item holder.");
                // A single body is presented at a carry anchor; never clone an interactive item.
                Vector3 carry = Vector(holder.Position) + new Vector3(0, 1f, 0);
                Vector3? anchor = CarryPosition == null ? null : CarryPosition(entity.HolderId);
                if (anchor.HasValue) carry = anchor.Value;
                if (entity.HolderId == network.LocalPlayerId) carry = player.View.transform.position + player.View.transform.forward * 0.6f - player.View.transform.up * 0.25f;
                body.position = carry;
            }
            else if (!network.IsHost || item.AppliedRevision != entity.Revision)
            {
                body.position = Vector(entity.Position); body.rotation = Rotation(entity.Rotation);
                if (network.IsHost) { body.velocity = Vector(entity.Velocity); body.angularVelocity = Vector(entity.AngularVelocity); }
            }
            item.AppliedRevision = entity.Revision;
        }
        internal static WorldVector Vector(Vector3 v) { return new WorldVector(v.x, v.y, v.z); }
        internal static Vector3 Vector(WorldVector v) { return new Vector3(v.X, v.Y, v.Z); }
        internal static WorldRotation Rotation(Quaternion q) { return new WorldRotation(q.x, q.y, q.z, q.w); }
        internal static Quaternion Rotation(WorldRotation q) { return new Quaternion(q.X, q.Y, q.Z, q.W); }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            network.PoseReceived -= OnPose;
            try { if (world != null) { world.Fault -= OnFault; world.ReceivedResult -= OnResult; world.PoseReceipt -= OnPoseReceipt; world.Dispose(); } }
            finally
            {
                try { if (passenger != null) passenger.Dispose(); if (vehicle != null) vehicle.Dispose(); else if (van != null) van.Dispose(); }
                catch (Exception error) { record("vehicle.cleanup-error", error.ToString()); }
                if (items != null) foreach (SharedItemBinding item in items) item.Dispose();
                if (changed)
                {
                    if (cup != null && cup.PickupFsm != null) cup.PickupFsm.enabled = pickupEnabled;
                    if (saveItems != null) saveItems.enabled = saveItemsEnabled;
                }
                if (cup != null) cup.Dispose(); if (player != null) player.Dispose();
            }
        }
    }
}
