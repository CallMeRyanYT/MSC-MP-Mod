using MSCMP.Avatars;
using Steamworks;
using UnityEngine;

namespace MSCMP.Net
{
    /// <summary>One participant of the session (including the local player).</summary>
    internal sealed class NetPlayer
    {
        public byte Slot;                 // 0 = host
        public CSteamID SteamId;
        public string Name = "Player";
        public Appearance Look;
        public PlayerPhase Phase;
        public bool IsLocal;

        // Connection health
        public float LastHeard;           // Time.realtimeSinceStartup of last packet
        public float Rtt = 0.1f;          // seconds
        public float ClockOffset;         // add to remote time to get local time
        public bool ClockSynced;
        float bestRtt = 10f;

        // Host-side bookkeeping
        public float HelloTime;
        public float SaveProgress;
        public bool SaveSent;

        // Gameplay
        public RemoteAvatar Avatar;
        public PlayerSnapshot Last;       // most recent state received
        public bool HasState;

        public bool IsHost { get { return Slot == 0; } }

        public string Describe()
        {
            return Name + " [" + Slot + "]";
        }

        /// <summary>Updates RTT and the remote->local clock mapping from a ping round trip.</summary>
        public void OnPong(float sentLocal, float remoteTime, float nowLocal)
        {
            float rtt = Mathf.Max(0.001f, nowLocal - sentLocal);
            Rtt = Mathf.Lerp(Rtt, rtt, 0.25f);
            // Remote clock at the moment it answered ~ halfway through the round trip.
            float offset = (sentLocal + rtt * 0.5f) - remoteTime;
            if (!ClockSynced || rtt < bestRtt * 1.2f)
            {
                ClockOffset = ClockSynced ? Mathf.Lerp(ClockOffset, offset, 0.3f) : offset;
                ClockSynced = true;
                bestRtt = Mathf.Min(bestRtt, rtt);
            }
            // Slowly forget the best RTT so the estimate can follow route changes.
            bestRtt = Mathf.Min(bestRtt * 1.01f + 0.001f, 10f);
        }

        /// <summary>Converts a timestamp from this player's clock into the local clock.</summary>
        public float ToLocalTime(float remoteTime)
        {
            return ClockSynced ? remoteTime + ClockOffset : Time.realtimeSinceStartup - Rtt * 0.5f;
        }
    }
}
