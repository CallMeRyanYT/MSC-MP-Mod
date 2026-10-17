using System;
using MSCMP.Core;
using MSCMP.Network;
using MSCMP.Sync;
using UnityEngine;

namespace MSCMP.Integration
{
    // Explicit game adapter. No Unity object, input or save access enters the network worker.
    // The inspected cup is the first body; additional families require their own bindings.
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
        private ulong vehicleId;
        private ulong entityId;
        private bool pickupEnabled, useEnabled, originalKinematic, originalCollider;
        private Transform originalParent;
        private int originalLayer;
        private bool changed, disposed, failed, wasReady, vehiclePresented;
        private float nextMotion, nextPose, nextDriverPose, requestAt;
        private ulong appliedRevision, pendingRequest;
        private string status = "Loading shared world";
        internal string Status { get { return status; } }
        internal bool Ready { get { return !disposed && !failed && world != null && world.Ready && entityId != 0 && vehicle != null && vehicle.Ready; } }
        internal bool LocallyDriving { get { return Ready && vehicle.Driver == network.LocalPlayerId; } }
        internal void RequestDriverSeat() { if (Ready) van.RequestSeat(LocallyDriving); }
        internal Func<ulong, Vector3?> CarryPosition;
        internal bool IsDriver(ulong id) { return Ready && vehicle.Driver == id; }
        internal bool IsHolding(ulong id)
        {
            if (!Ready) return false;
            PlayerState state = network.IsHost ? world.Authority.GetPlayer(id) : world.Replica.GetPlayer(id);
            return state != null && state.HandEntityId != 0;
        }

        internal SharedWorldSession(DiagnosticSession active, int sceneGeneration, Action<string, string> recorder)
        {
            network = active; generation = sceneGeneration; record = recorder;
            try
            {
                DisposableSessionGuard.RequireActive();
                string reason;
                if (!LocalPlayerBinding.TryCreate(generation, out player, out reason)) throw new InvalidOperationException(reason);
                if (!SupportedItemBinding.TryCreate(generation, out cup, out reason) || !cup.CanBeginSharedSession(out reason)) throw new InvalidOperationException(reason);
                van = VehicleBinding.Create();
                Vector3 position; float yaw;
                if (!player.TryInitialPose(generation, out position, out yaw)) throw new InvalidOperationException("A loaded walking player is required.");
                pickupEnabled = cup.PickupFsm.enabled; useEnabled = cup.ItemUseFsm.enabled;
                originalKinematic = cup.Body.isKinematic; originalCollider = cup.Collider.enabled;
                originalParent = cup.ItemObject.transform.parent; originalLayer = cup.ItemObject.layer;
                // Stop the native mutation source before accepting any shared input. The
                // session owns its interaction path; unsupported native pickup/use is gated.
                changed = true; cup.PickupFsm.enabled = false; cup.ItemUseFsm.enabled = false;
                cup.Body.isKinematic = !network.IsHost;
                AuthorityWorld authority = null;
                if (network.IsHost)
                {
                    // Native ray is one metre; root-to-item bound includes the camera height.
                    authority = new AuthorityWorld(network.SessionGeneration, new AuthorityLimits(3, 3, 4, 100000, 8, 64, 64), 128, 4096);
                    authority.ConnectPlayer(1, 1, Vector(position), true);
                    EntityState entity = authority.RegisterAuthoredItem(SupportedItemBinding.AuthoredKey, SupportedItemBinding.Archetype, Vector(cup.Body.position));
                    entityId = entity.Id;
                    authority.UpdateFreeMotion(authority.Generation, entity.Id, entity.OwnershipEpoch, 1, Vector(cup.Body.position), Vector(cup.Body.velocity), Rotation(cup.Body.rotation), Vector(cup.Body.angularVelocity));
                    EntityState vanEntity = authority.RegisterAuthoredItem(VehicleBinding.Key, VehicleBinding.Archetype, Vector(van.Body.position)); vehicleId = vanEntity.Id;
                    authority.UpdateFreeMotion(authority.Generation, vanEntity.Id, vanEntity.OwnershipEpoch, 1, Vector(van.Body.position), Vector(van.Body.velocity), Rotation(van.Body.rotation), Vector(van.Body.angularVelocity));
                    authority.InteractionAllowed = delegate(AuthorityCommand command) { return command.EntityId == entityId && (vehicle == null || vehicle.Driver != command.PlayerId); };
                }
                world = new WorldSyncSession(network, authority, Vector(position));
                world.ValidateSnapshot = ValidateMapping;
                world.Fault += OnFault; world.ReceivedResult += OnResult;
                vehicle = new VehicleSession(network, world, van, record); vehicle.EntityId = vehicleId;
                network.PoseReceived += OnPose;
                record("world.started", "disposable profile; cup adapter; role=" + (network.IsHost ? "host" : "client"));
            }
            catch { Dispose(); throw; }
        }

        private void ValidateMapping(WorldSnapshot snapshot)
        {
            EntityState[] entities = snapshot.Entities;
            if (entities.Length != 2)
                throw new InvalidOperationException("The host's supported item map differs from this game.");
            AuthoredMapping.Resolve(entities, new AuthoredCandidate[] { new AuthoredCandidate(SupportedItemBinding.AuthoredKey, SupportedItemBinding.Archetype, cup.ItemObject), new AuthoredCandidate(VehicleBinding.Key, VehicleBinding.Archetype, van.Body) });
            foreach (EntityState entity in entities)
            {
                if (entity.AuthoredKey == SupportedItemBinding.AuthoredKey) entityId = entity.Id;
                else if (entity.AuthoredKey == VehicleBinding.Key) vehicleId = entity.Id;
            }
            vehicle.EntityId = vehicleId;
            // Start the isolated client at its host-assigned join position. This touches
            // only the loaded disposable scene, never the backed-up personal profile.
            foreach (PlayerState state in snapshot.Players) if (state.Id == network.LocalPlayerId) player.PlaceAt(Vector(state.Position));
        }
        private void OnFault(string reason) { failed = true; status = reason; record("world.error", reason); }
        private void OnResult(WorldMessage result)
        {
            // Host results are synchronous; Request may not have returned its ID yet.
            pendingRequest = 0;
            status = result.Outcome == CommandOutcome.Accepted ? "Shared action accepted" : "Action refused: " + result.Outcome;
            record("world.result", "request=" + result.RequestSequence + " outcome=" + result.Outcome);
        }
        private void OnPose(PoseMessage pose)
        {
            if (!network.IsHost || world == null || !Ready) return;
            if (vehicle.Driver == pose.PlayerId) return;
            world.Authority.AcceptPlayerPose(network.SessionGeneration, pose.PlayerId, pose.ConnectionGeneration, pose.Sequence, new WorldVector(pose.X, pose.Y, pose.Z));
        }
        internal void Poll(bool panelVisible)
        {
            if (disposed) return;
            if (Application.loadedLevelName != "GAME" || !network.IsRunning || cup.ItemObject == null) { Dispose(); return; }
            world.Poll();
            vehicle.Poll(panelVisible);
            if (!Ready) { status = failed ? status : "Waiting for the host's item map and world baseline"; return; }
            if (!wasReady) { wasReady = true; status = "Shared cup and van ready"; record("world.ready", "authored map and baseline ready"); }
            float now = Time.realtimeSinceStartup;
            Vector3 position; float yaw;
            bool walking = player.TrySample(generation, out position, out yaw);
            if (walking && now >= nextPose)
            {
                nextPose = now + 0.1f;
                if (network.IsHost) world.Authority.AcceptPlayerPose(network.SessionGeneration, 1, 1, checked(world.Authority.GetPlayer(1).PoseSequence + 1), Vector(position));
                else network.SendPose(position.x, position.y, position.z, yaw);
            }
            if (network.IsHost && vehicle.Driver != 0)
            {
                PlayerState driver = world.Authority.GetPlayer(vehicle.Driver);
                if (driver != null && now >= nextDriverPose)
                {
                    nextDriverPose = now + 0.1f;
                    world.Authority.AcceptPlayerPose(network.SessionGeneration, driver.Id, driver.ConnectionGeneration, checked(driver.PoseSequence + 1), Vector(van.Body.position));
                }
            }
            EntityState entity = Entity();
            if (entity == null) throw new InvalidOperationException("The mapped shared entity disappeared.");
            Apply(entity);
            if (network.IsHost && now >= nextMotion)
            {
                nextMotion = now + 0.1f;
                Rigidbody body = cup.Body;
                if (entity.Lifecycle == EntityLifecycle.Free && (!Vector(body.position).Same(entity.Position) || !Rotation(body.rotation).Same(entity.Rotation) || !Vector(body.velocity).Same(entity.Velocity)))
                    world.Authority.UpdateFreeMotion(network.SessionGeneration, entity.Id, entity.OwnershipEpoch, checked(entity.MotionSequence + 1), Vector(body.position), Vector(body.velocity), Rotation(body.rotation), Vector(body.angularVelocity));
                EntityState car = world.Authority.GetEntity(vehicleId); body = van.Body;
                if (!Vector(body.position).Same(car.Position) || !Rotation(body.rotation).Same(car.Rotation) || !Vector(body.velocity).Same(car.Velocity))
                    world.Authority.UpdateFreeMotion(network.SessionGeneration, car.Id, car.OwnershipEpoch, checked(car.MotionSequence + 1), Vector(body.position), Vector(body.velocity), Rotation(body.rotation), Vector(body.angularVelocity));
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
            if (pendingRequest != 0 && now - requestAt > 5) status = "Waiting for the host's action result; ownership has not been guessed";
            if (!walking || LocallyDriving || panelVisible || Time.timeScale == 0 || pendingRequest != 0) return;
            bool own = entity.Lifecycle == EntityLifecycle.Held && entity.HolderId == network.LocalPlayerId;
            if (own && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)))
            {
                bool throwing = Input.GetMouseButtonDown(1);
                Vector3 release = player.View.transform.position + player.View.transform.forward * 0.7f;
                Request(throwing ? CommandKind.Throw : CommandKind.Drop, Vector(release), throwing ? Vector(player.View.transform.forward * 4f) : WorldVector.Zero);
            }
            else if (!own && Input.GetMouseButtonDown(0))
            {
                RaycastHit hit;
                if (Physics.Raycast(player.View.transform.position, player.View.transform.forward, out hit, 1f, (1 << 19) | (1 << 15) | (1 << 9))
                    && hit.collider == cup.Collider) Request(CommandKind.Pickup, WorldVector.Zero, WorldVector.Zero);
            }
        }
        private void Request(CommandKind kind, WorldVector position, WorldVector velocity)
        {
            requestAt = Time.realtimeSinceStartup;
            ulong sequence = world.Request(kind, entityId, position, velocity);
            if (!network.IsHost) pendingRequest = sequence;
        }
        private EntityState Entity() { return network.IsHost ? world.Authority.GetEntity(entityId) : world.Replica.GetEntity(entityId); }
        private void Apply(EntityState entity)
        {
            Rigidbody body = cup.Body;
            bool held = entity.Lifecycle == EntityLifecycle.Held;
            body.isKinematic = held || !network.IsHost;
            cup.Collider.enabled = !held;
            cup.ItemObject.layer = held ? 16 : originalLayer;
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
            else if (!network.IsHost || appliedRevision != entity.Revision)
            {
                body.position = Vector(entity.Position); body.rotation = Rotation(entity.Rotation);
                if (network.IsHost) { body.velocity = Vector(entity.Velocity); body.angularVelocity = Vector(entity.AngularVelocity); }
            }
            appliedRevision = entity.Revision;
        }
        internal static WorldVector Vector(Vector3 v) { return new WorldVector(v.x, v.y, v.z); }
        internal static Vector3 Vector(WorldVector v) { return new Vector3(v.X, v.Y, v.Z); }
        internal static WorldRotation Rotation(Quaternion q) { return new WorldRotation(q.x, q.y, q.z, q.w); }
        internal static Quaternion Rotation(WorldRotation q) { return new Quaternion(q.X, q.Y, q.Z, q.W); }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            network.PoseReceived -= OnPose;
            try { if (world != null) { world.Fault -= OnFault; world.ReceivedResult -= OnResult; world.Dispose(); } }
            finally
            {
                try { if (vehicle != null) vehicle.Dispose(); else if (van != null) van.Dispose(); }
                catch (Exception error) { record("vehicle.cleanup-error", error.ToString()); }
                if (changed && cup != null && cup.ItemObject != null)
                {
                    cup.ItemObject.transform.parent = originalParent; cup.ItemObject.layer = originalLayer;
                    cup.Body.isKinematic = originalKinematic; cup.Collider.enabled = originalCollider;
                    cup.PickupFsm.enabled = pickupEnabled; cup.ItemUseFsm.enabled = useEnabled;
                }
                if (cup != null) cup.Dispose(); if (player != null) player.Dispose();
            }
        }
    }
}
