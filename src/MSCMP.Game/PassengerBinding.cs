using System;
using UnityEngine;

namespace MSCMP.Integration
{
    // Owns only the passenger's walking controller flags and root placement.
    // Never enters the native driver FSM or writes PlayerInCar/global save state.
    internal sealed class PassengerBinding : IDisposable
    {
        private readonly LocalPlayerBinding player;
        private readonly VehicleBinding van;
        private PassengerFollow follow;
        private Vector3 fallback, exit;
        private bool exitReady, active;
        private float yaw;
        internal bool Active { get { return active; } }
        internal PassengerBinding(LocalPlayerBinding player, VehicleBinding van) { this.player = player; this.van = van; }
        internal bool PrepareExit()
        {
            if (van.Body == null) return false;
            // Find a clear standing location beside either front door. Raycasts
            // inspect this scene only and never change a collider or save file.
            for (int side = 1; side >= -1; side -= 2)
            {
                Vector3 start = van.Body.position + van.Body.transform.right * (2.2f * side) + Vector3.up * 3;
                RaycastHit hit;
                if (!Physics.Raycast(start, Vector3.down, out hit, 6, ~(1 << 2))) continue;
                Vector3 feet = hit.point + Vector3.up * 0.12f;
                if (Physics.CheckCapsule(feet + Vector3.up * 0.4f, feet + Vector3.up * 1.5f, 0.3f, ~(1 << 2))) continue;
                exit = feet; exitReady = true; return true;
            }
            return false;
        }
        internal void SetSeated(bool seated)
        {
            if (seated == active) return;
            if (seated)
            {
                fallback = player.Root.position; yaw = van.SeatYaw;
                player.BeginPassenger(); active = true;
                follow = player.Root.gameObject.AddComponent<PassengerFollow>(); follow.Binding = this;
                Tick();
            }
            else Release();
        }
        internal void Tick()
        {
            if (!active || van.Body == null || player.Root == null) return;
            player.HoldPassenger();
            float nextYaw = van.SeatYaw;
            player.Root.rotation = Quaternion.Euler(0, Mathf.DeltaAngle(yaw, nextYaw), 0) * player.Root.rotation;
            yaw = nextYaw;
            player.Root.position += van.SeatHead(1) - player.View.transform.position;
        }
        private void Release()
        {
            if (!active) return;
            active = false;
            if (follow != null) { follow.Binding = null; UnityEngine.Object.Destroy(follow); follow = null; }
            // Disconnect/teardown also tries the doors. The entry location is a
            // final fallback when the van is upside down or both exits are blocked.
            try { exitReady = false; PrepareExit(); }
            finally { player.EndPassenger(exitReady ? exit : fallback); exitReady = false; }
        }
        public void Dispose() { Release(); }
    }
    public sealed class PassengerFollow : MonoBehaviour
    {
        internal PassengerBinding Binding;
        private void LateUpdate() { if (Binding != null) Binding.Tick(); }
    }
}
