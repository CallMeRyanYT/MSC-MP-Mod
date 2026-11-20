using MSCMP.Avatars;
using MSCMP.Game;
using MSCMP.Net;
using MSCMP.Sync;
using UnityEngine;

namespace MSCMP
{
    /// <summary>
    /// Two-player test without a second player: shows you a copy of yourself (and of your
    /// bots) exactly as a friend's game would draw it. Your updates are packed into the real
    /// network format, unpacked again and fed to a remote body standing next to you, so
    /// walking, crouching, leaning, looking around, drunkenness and driving can all
    /// be checked alone. Switch it on in the F2 panel.
    /// </summary>
    internal static class Loopback
    {
        public static bool On { get; private set; }

        /// <summary>Stands in for "a friend" as the owner of mirrored bots.</summary>
        public static readonly NetPlayer BotOwner = new NetPlayer { Slot = 201, Name = "Loopback", Phase = PlayerPhase.Playing, ClockSynced = true };

        /// <summary>The mirrored copies stand this far to the side of the real thing.</summary>
        public static readonly Vector3 Offset = new Vector3(2.5f, 0f, 0f);

        static NetPlayer me;
        static readonly NetWriter writer = new NetWriter(256);
        static readonly NetReader reader = new NetReader();
        static float nextSend, nextStatus;

        public static void Set(bool on)
        {
            if (on == On) return;
            if (on && (!MP.InGame || !LocalPlayer.Bound)) { Notifications.Show("Load into the world first."); return; }
            On = on;
            if (on)
            {
                me = new NetPlayer { Slot = 200, Name = "You (as friends see you)", Phase = PlayerPhase.Playing, ClockSynced = true, Look = Options.LocalAppearance() };
                me.Avatar = RemoteAvatar.Create(me);
                Bots.LoopbackChanged(true);
                Notifications.Show("Loopback test on: the copy next to you is what your friends see.");
            }
            else
            {
                if (me != null && me.Avatar != null) me.Avatar.Destroy();
                me = null;
                Bots.LoopbackChanged(false);
            }
        }

        public static NetPlayer Player { get { return On ? me : null; } }

        public static void Update()
        {
            if (!On) return;
            if (!MP.InGame || !LocalPlayer.Bound || me == null || me.Avatar == null) { Set(false); return; }
            Appearance look = Options.LocalAppearance();
            if (look.Body != me.Look.Body || look.Shirt != me.Look.Shirt || look.Pants != me.Look.Pants || look.Face != me.Look.Face)
            {
                me.Look = look;
                me.Avatar.Rebuild(look);
            }

            float now = Time.realtimeSinceStartup;
            if (now >= nextSend)
            {
                nextSend = now + 0.033f; // like PlayerSync
                PlayerSnapshot s = LocalPlayer.Sample();
                if (s.StandingOnVehicle)
                {
                    s.Position.x += 1.2f; // on the same deck, beside you
                    s.Head.x += 1.2f;
                }
                else if (s.Vehicle != 0)
                {
                    // Driving: the copy sits in the other front seat.
                    s.Seat = (byte)(s.Seat == 1 ? 2 : 1);
                    s.Position.x = -s.Position.x;
                    s.Head.x = -s.Head.x;
                    s.Yaw = -s.Yaw;
                    s.Body = new Quaternion(s.Body.x, -s.Body.y, -s.Body.z, s.Body.w);
                }
                else
                {
                    s.Position += Offset;
                    s.Head += Offset;
                }
                writer.Reset();
                s.Write(writer);
                reader.Set(writer.Buffer, 0, writer.Length);
                PlayerSnapshot back = PlayerSnapshot.Read(reader);
                me.Last = back;
                me.HasState = true;
                me.Avatar.Push(back);
            }
            if (now >= nextStatus)
            {
                nextStatus = now + 2f;
                writer.Reset();
                LocalPlayer.ReadStatus().Write(writer);
                reader.Set(writer.Buffer, 0, writer.Length);
                me.Status = PlayerStatus.Read(reader);
                me.HasStatus = true;
            }
            me.Avatar.Update();
        }

        public static void LateUpdate()
        {
            if (On && me != null && me.Avatar != null) me.Avatar.LateUpdate();
        }

        public static void OnWorldLeft() { Set(false); }
    }
}
