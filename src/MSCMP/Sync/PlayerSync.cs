using System.Collections.Generic;
using MSCMP.Avatars;
using MSCMP.Game;
using MSCMP.Net;
using UnityEngine;

namespace MSCMP.Sync
{
    /// <summary>Sends the local player's state and keeps everybody else's body up to date.</summary>
    internal static class PlayerSync
    {
        const float SendInterval = 0.033f; // 30 Hz
        static float nextSend;
        static readonly NetReader reader = new NetReader();

        public static void Init()
        {
            Session.On(Msg.PlayerState, OnPlayerState);
            Session.PlayerJoined += p => { if (MP.InGame) EnsureAvatar(p); };
            Session.PlayerLeft += RemoveAvatar;
            Session.Ended += reason => RemoveAll();
        }

        public static void OnWorldReady()
        {
            foreach (NetPlayer p in Session.Remotes) EnsureAvatar(p);
        }

        public static void OnWorldLeft()
        {
            RemoveAll();
        }

        static void EnsureAvatar(NetPlayer p)
        {
            if (p == null || p.IsLocal || p.Avatar != null) return;
            p.Avatar = RemoteAvatar.Create(p);
        }

        static void RemoveAvatar(NetPlayer p)
        {
            if (p == null || p.Avatar == null) return;
            p.Avatar.Destroy();
            p.Avatar = null;
        }

        static void RemoveAll()
        {
            foreach (NetPlayer p in new List<NetPlayer>(Session.Players)) RemoveAvatar(p);
        }

        public static void Update()
        {
            if (!Session.Connected || Session.Local == null) return;

            if (LocalPlayer.Bound && Time.realtimeSinceStartup >= nextSend)
            {
                nextSend = Time.realtimeSinceStartup + SendInterval;
                PlayerSnapshot s = LocalPlayer.Sample();
                NetWriter w = Session.Begin(Msg.PlayerState);
                w.Byte(Session.Local.Slot);
                s.Write(w);
                Session.SendToAll(false, Channel.State);
            }


            foreach (NetPlayer p in Session.Remotes)
            {
                if (p.Avatar == null) EnsureAvatar(p);
                if (p.Avatar != null) p.Avatar.Update();
            }
        }

        public static void LateUpdate()
        {
            foreach (NetPlayer p in Session.Remotes)
                if (p.Avatar != null) p.Avatar.LateUpdate();
        }

        static void OnPlayerState(NetPlayer from, NetReader r)
        {
            if (from == null) return;
            byte slot = r.Byte();
            int payloadStart = r.Position;
            PlayerSnapshot s = PlayerSnapshot.Read(r);
            NetPlayer subject = Session.IsHost ? from : Session.BySlot(slot);
            if (subject == null || subject.IsLocal) return;
            subject.Last = s;
            subject.HasState = true;
            subject.StatesReceived++;
            if (subject.Avatar != null) subject.Avatar.Push(s);

            if (Session.IsHost)
            {
                // Relay to the other clients, stamped with the real slot.
                NetWriter w = Session.Begin(Msg.PlayerState);
                w.Byte(subject.Slot);
                s.Write(w);
                Session.BroadcastInGame(false, Channel.State, subject);
            }
        }

        /// <summary>The nearest remote player (for UI hints), or null.</summary>
        public static NetPlayer Nearest(Vector3 pos, float maxDistance)
        {
            NetPlayer best = null;
            float bestD = maxDistance;
            foreach (NetPlayer p in Session.Remotes)
            {
                if (p.Avatar == null || !p.Avatar.Visible) continue;
                float d = Vector3.Distance(p.Avatar.HeadPosition, pos);
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }
    }
}
