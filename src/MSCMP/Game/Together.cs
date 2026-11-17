using System;
using MSCMP.Net;
using MSCMP.Sync;
using UnityEngine;

namespace MSCMP.Game
{
    /// <summary>
    /// Small things that make playing together nicer: handing items to each other, chat
    /// bubbles over heads, getting knocked down by a friend's car, and watching a friend
    /// (spectating). The host decides whether cars can knock players down.
    /// </summary>
    internal static class Together
    {
        // ================================================================ session rules

        /// <summary>Host setting: a car someone else drives knocks you down when it hits you.</summary>
        public static bool CarsHurt = true;

        public static void Init()
        {
            Session.On(Msg.GiveItem, OnGive);
            Session.On(Msg.Knocked, OnKnocked);
            Session.On(Msg.SessionRules, OnRules);
            Session.ChatReceived += (p, msg) =>
            {
                if (p == null) return;
                p.LastChat = msg;
                p.LastChatTime = Time.realtimeSinceStartup;
            };
            Session.Ended += r => { StopWatching(); CarsHurt = true; };
        }

        public static void SetCarsHurt(bool on)
        {
            if (!Session.IsHost && Session.Active) return;
            CarsHurt = on;
            SendRules(null);
        }

        public static void SendRules(NetPlayer onlyTo)
        {
            if (!Session.IsHost || !Session.Connected) return;
            NetWriter w = Session.Begin(Msg.SessionRules);
            w.Bool(CarsHurt);
            if (onlyTo != null) Session.SendTo(onlyTo, true, Channel.Reliable);
            else Session.BroadcastInGame(true, Channel.Reliable);
        }

        static void OnRules(NetPlayer from, NetReader r)
        {
            bool hurt = r.Bool();
            if (Session.IsClient && from != null && from.IsHost) CarsHurt = hurt;
        }

        public static void SendCatchUp(NetPlayer p) { SendRules(p); }

        // ================================================================ per frame

        public static void Update()
        {
            if (!MP.InGame || !LocalPlayer.Bound) { StopWatching(); return; }
            HandleGiveKey();
            CheckCarHit();
            UpdateStun();
            float now = Time.realtimeSinceStartup;
            if (now >= nextSlowCheck)
            {
                nextSlowCheck = now + 1f;
                CheckSleeping();
                if (now >= nextQuality) { nextQuality = now + 3f; MeasureConnections(3f); }
            }
            if (Watching != null && Input.GetKeyDown(KeyCode.Escape)) StopWatching();
        }

        public static void LateUpdate()
        {
            UpdateGift();
            UpdateWatchCamera();
        }

        // ================================================================ sleeping together

        static float nextSlowCheck, nextQuality;
        static bool toldHostSleeping, toldSleepAlone, toldOthersAsleep;
        static float sleepFade;

        static bool IsSleeping(PlayerSnapshot s) { return (s.Flags & PlayerSnapshot.FlagSleeping) != 0; }

        /// <summary>Time only moves on when the host sleeps. Tell people what is going on, and
        /// dim everybody's screen while the night passes.</summary>
        static void CheckSleeping()
        {
            if (!Session.Connected || Session.Local == null) { sleepFade = 0f; return; }
            bool me = IsSleeping(LocalPlayer.Sample());
            if (Session.IsClient)
            {
                NetPlayer host = Session.BySlot(0);
                bool hostSleeping = host != null && host.HasState && IsSleeping(host.Last);
                sleepFade = hostSleeping && !me ? 0.55f : 0f;
                if (hostSleeping && !me && !toldHostSleeping) { toldHostSleeping = true; Notifications.Show(host.Name + " is sleeping. Time passes for everyone."); }
                if (!hostSleeping) toldHostSleeping = false;
                if (me && !hostSleeping && !toldSleepAlone) { toldSleepAlone = true; Notifications.Show("Time only passes when " + (host != null ? host.Name : "the host") + " sleeps too."); }
                if (!me) toldSleepAlone = false;
            }
            else
            {
                int others = 0, asleep = 0;
                foreach (NetPlayer p in Session.Remotes)
                {
                    if (p.Phase != PlayerPhase.Playing || !p.HasState) continue;
                    others++;
                    if (IsSleeping(p.Last)) asleep++;
                }
                bool all = others > 0 && asleep == others;
                if (all && !me && !toldOthersAsleep) { toldOthersAsleep = true; Notifications.Show("Everyone else is asleep. Go to bed to let the night pass."); }
                if (!all) toldOthersAsleep = false;
                sleepFade = 0f;
            }
        }

        public static float SleepFade { get { return sleepFade; } }

        // ================================================================ connection quality

        /// <summary>Counts how many of each player's updates arrived (30 a second are sent).</summary>
        static void MeasureConnections(float seconds)
        {
            float now = Time.realtimeSinceStartup;
            foreach (NetPlayer p in Session.Remotes)
            {
                bool counting = p.Phase == PlayerPhase.Playing && MP.InGame && !p.Last.Has(PlayerSnapshot.FlagInMenu);
                if (counting)
                {
                    float expected = seconds * 30f;
                    float loss = Mathf.Clamp01(1f - p.StatesReceived / expected);
                    p.Loss = Mathf.Lerp(p.Loss, loss, 0.5f);
                    bool bad = p.Loss > 0.15f || (Session.IsHost || p.IsHost) && p.Rtt > 0.3f;
                    if (bad && now - p.LastQualityWarning > 60f)
                    {
                        p.LastQualityWarning = now;
                        Notifications.Show("Poor connection to " + p.Name + " (" + Quality(p) + "). Things may stutter.");
                    }
                }
                p.StatesReceived = 0;
            }
        }

        /// <summary>"45 ms, 2% lost" for the player list.</summary>
        public static string Quality(NetPlayer p)
        {
            string r = (Session.IsHost || p.IsHost) && p.Rtt > 0f ? Mathf.RoundToInt(p.Rtt * 1000f) + " ms" : "";
            int lost = Mathf.RoundToInt(p.Loss * 100f);
            if (lost > 0) r += (r.Length > 0 ? ", " : "") + lost + "% lost";
            return r;
        }

        /// <summary>"good", "ok" or "poor".</summary>
        public static string QualityWord(NetPlayer p)
        {
            bool rttKnown = Session.IsHost || p.IsHost;
            if (p.Loss > 0.15f || rttKnown && p.Rtt > 0.3f) return "poor";
            if (p.Loss > 0.05f || rttKnown && p.Rtt > 0.15f) return "ok";
            return "good";
        }

        // ================================================================ handing items over

        sealed class Gift { public NetObject O; public float Until; }
        static Gift gift;

        static void HandleGiveKey()
        {
            if (!Session.Connected || MultiplayerUIState.Typing || MultiplayerUIState.PanelOpen) return;
            if (!Options.KeyDown(Options.GiveKey)) return;
            GameObject held = LocalPlayer.HeldObject;
            NetObject o = held != null ? ObjectSync.Of(held) : null;
            if (o == null) { Notifications.Show("Hold something, look at a friend and press the key to hand it over."); return; }
            NetPlayer to = PlayerInFront(3.5f);
            if (to == null) { Notifications.Show("Look at a friend standing close to you to hand it over."); return; }
            LocalPlayer.ForceDrop();
            if (Session.IsHost) HostGive(Session.Local, o.Id, to);
            else
            {
                NetWriter w = Session.Begin(Msg.GiveItem);
                w.UInt(o.Id);
                w.Byte(to.Slot);
                Session.SendToHost(true, Channel.Reliable);
            }
            Notifications.Show("You hand the " + Bots.Pretty(o.Name) + " to " + to.Name + ".");
        }

        /// <summary>The remote player you are looking at, close by, or null.</summary>
        static NetPlayer PlayerInFront(float maxDistance)
        {
            Transform cam = LocalPlayer.View.transform;
            NetPlayer best = null;
            float bestAngle = 30f;
            foreach (NetPlayer p in Session.Remotes)
            {
                if (p.Avatar == null || !p.Avatar.Visible) continue;
                Vector3 chest = p.Avatar.HeadPosition - Vector3.up * 0.4f;
                Vector3 d = chest - cam.position;
                if (d.magnitude > maxDistance) continue;
                float a = Vector3.Angle(cam.forward, d);
                if (a < bestAngle) { bestAngle = a; best = p; }
            }
            return best;
        }

        static void HostGive(NetPlayer giver, uint id, NetPlayer to)
        {
            NetObject o = ObjectSync.ById(id);
            if (o == null || !o.Alive || to == null) return;
            ObjectSync.GiveTo(o, to.Slot);
            if (to.IsLocal) StartGift(o, giver != null ? giver.Name : "Someone");
            else
            {
                NetWriter w = Session.Begin(Msg.GiveItem);
                w.UInt(id);
                w.Byte(giver != null ? giver.Slot : (byte)0);
                Session.SendTo(to, true, Channel.Reliable);
            }
        }

        static void OnGive(NetPlayer from, NetReader r)
        {
            uint id = r.UInt();
            byte slot = r.Byte();
            if (from == null) return;
            if (Session.IsHost)
            {
                NetObject o = ObjectSync.ById(id);
                if (o == null || o.Owner != from.Slot) return; // only what you have
                HostGive(from, id, Session.BySlot(slot));
            }
            else if (from.IsHost)
            {
                NetObject o = ObjectSync.ById(id);
                NetPlayer giver = Session.BySlot(slot);
                if (o != null) StartGift(o, giver != null ? giver.Name : "A friend");
            }
        }

        static void StartGift(NetObject o, string giver)
        {
            gift = new Gift { O = o, Until = Time.realtimeSinceStartup + 8f };
            Notifications.Show(giver + " hands you the " + Bots.Pretty(o.Name) + " - click to take it.");
        }

        /// <summary>The item hovers in front of you until you take it (or for 8 seconds).</summary>
        static void UpdateGift()
        {
            if (gift == null) return;
            NetObject o = gift.O;
            bool taken = o.Alive && LocalPlayer.HeldObject == o.Go;
            if (!o.Alive || taken || Time.realtimeSinceStartup > gift.Until || !LocalPlayer.Bound)
            {
                if (o.Alive && !taken && !o.Replica) { o.Body.isKinematic = false; o.Body.WakeUp(); }
                gift = null;
                return;
            }
            if (o.Replica) return; // ownership still on its way
            Transform cam = LocalPlayer.View.transform;
            o.Body.isKinematic = true;
            o.Go.transform.position = cam.position + cam.forward * 0.75f - Vector3.up * 0.2f;
        }

        // ================================================================ knocked down by cars

        static float stunUntil, hitCooldown;
        static bool stunned;

        static void CheckCarHit()
        {
            if (!CarsHurt || !Session.Connected || Session.Local == null || stunned) return;
            if (Time.realtimeSinceStartup < hitCooldown) return;
            if (!string.IsNullOrEmpty(LocalPlayer.CurrentVehicleName) || LocalPlayer.PassengerOf != null || LocalPlayer.Platform != null) return;
            byte me = Session.Local.Slot;
            Vector3 feet = LocalPlayer.Feet;
            foreach (NetVehicle v in VehicleSync.All)
            {
                if (v.Root == null || v.Body == null || v.Driver == 255 || v.Driver == me) continue;
                Vector3 local = v.Root.InverseTransformPoint(feet + Vector3.up * 0.9f);
                Bounds b = v.LocalBounds;
                b.Expand(0.6f);
                if (!b.Contains(local)) continue;
                Vector3 vel = v.Body.GetPointVelocity(feet);
                vel.y = 0f;
                if (vel.magnitude < 4.5f) continue; // about 16 km/h
                NetPlayer driver = Session.BySlot(v.Driver);
                Knock(vel, driver != null ? driver.Name : "Someone");
                return;
            }
        }

        static void Knock(Vector3 carVelocity, string driver)
        {
            hitCooldown = Time.realtimeSinceStartup + 4f;
            stunUntil = Time.realtimeSinceStartup + 2.5f;
            stunned = true;
            LocalPlayer.SetStunned(true);
            Vector3 away = carVelocity.normalized * 1.8f;
            LocalPlayer.Teleport(LocalPlayer.Feet + away, LocalPlayer.Player.eulerAngles.y);
            Notifications.Show(driver + " ran you over!");
            NetWriter w = Session.Begin(Msg.Knocked);
            w.Byte(Session.Local.Slot);
            Session.SendToAll(true, Channel.Reliable);
        }

        static void UpdateStun()
        {
            if (!stunned || Time.realtimeSinceStartup < stunUntil) return;
            stunned = false;
            LocalPlayer.SetStunned(false);
        }

        static void OnKnocked(NetPlayer from, NetReader r)
        {
            byte slot = r.Byte();
            if (from == null) return;
            NetPlayer who = Session.IsHost ? from : Session.BySlot(slot);
            if (who == null || who.IsLocal) return;
            if (who.Avatar != null) who.Avatar.Knock(2.5f);
            if (Session.IsHost)
            {
                NetWriter w = Session.Begin(Msg.Knocked);
                w.Byte(who.Slot);
                Session.BroadcastInGame(true, Channel.Reliable, who);
            }
        }

        /// <summary>A dark flash while you are knocked down (drawn by the UI).</summary>
        public static float StunFade { get { return stunned ? Mathf.Clamp01((stunUntil - Time.realtimeSinceStartup) / 2.5f) * 0.7f : 0f; } }

        // ================================================================ watching a friend

        public static NetPlayer Watching { get; private set; }
        static GameObject watchObject;
        static Camera watchCam;

        public static void StartWatching(NetPlayer p)
        {
            if (p == null || p.Avatar == null || !p.Avatar.Visible) { Notifications.Show("Can't see them right now."); return; }
            Watching = p;
            LocalPlayer.SetStunned(true); // you stay where you are
            Notifications.Show("Watching " + p.Name + ". Press Esc to stop.");
        }

        public static void StopWatching()
        {
            if (Watching == null) return;
            Watching = null;
            if (watchCam != null) watchCam.enabled = false;
            if (!stunned) LocalPlayer.SetStunned(false);
        }

        static void UpdateWatchCamera()
        {
            if (Watching == null) return;
            RemoteAvatarRef a = new RemoteAvatarRef(Watching);
            if (!a.Ok || LocalPlayer.View == null) { StopWatching(); return; }
            if (watchObject == null)
            {
                watchObject = new GameObject("MSCMP_WatchCamera");
                UnityEngine.Object.DontDestroyOnLoad(watchObject);
                watchCam = watchObject.AddComponent<Camera>();
            }
            Camera main = LocalPlayer.View;
            watchCam.depth = main.depth + 9f;
            watchCam.fieldOfView = main.fieldOfView;
            watchCam.nearClipPlane = 0.05f;
            watchCam.farClipPlane = main.farClipPlane;
            watchCam.cullingMask = main.cullingMask;
            watchCam.clearFlags = main.clearFlags;
            watchCam.backgroundColor = main.backgroundColor;
            Vector3 head = a.Head;
            Vector3 want = head - a.Forward * 3.6f + Vector3.up * 1.2f;
            RaycastHit hit;
            Vector3 dir = want - head;
            if (Physics.Raycast(head, dir.normalized, out hit, dir.magnitude, GameUtil.WorldMask) && !GameUtil.IsPersonCollider(hit.collider))
                want = head + dir.normalized * Mathf.Max(0.5f, hit.distance - 0.2f);
            watchObject.transform.position = Vector3.Lerp(watchObject.transform.position, want, Mathf.Clamp01(Time.deltaTime * 8f));
            watchObject.transform.LookAt(head);
            watchCam.enabled = true;
        }

        /// <summary>Where a watched player's body is and which way it faces.</summary>
        struct RemoteAvatarRef
        {
            public bool Ok;
            public Vector3 Head, Forward;

            public RemoteAvatarRef(NetPlayer p)
            {
                Ok = p.Avatar != null && p.Avatar.Visible && p.Avatar.Object != null;
                Head = Ok ? p.Avatar.HeadPosition : Vector3.zero;
                Vector3 f = Ok ? p.Avatar.Object.transform.forward : Vector3.forward;
                f.y = 0f;
                Forward = f.sqrMagnitude > 0.01f ? f.normalized : Vector3.forward;
            }
        }
    }
}
