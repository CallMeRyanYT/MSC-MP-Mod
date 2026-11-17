using System;
using System.Collections.Generic;
using MSCMP.Avatars;
using MSCMP.Game;
using MSCMP.Net;
using MSCMP.UI;
using UnityEngine;

namespace MSCMP.Sync
{
    /// <summary>
    /// Bots: helpers that walk around in the world with a real player body. Everybody in the
    /// session sees them; the game that spawned a bot runs it and sends its movement like a
    /// player's. They copy you, follow you (and ride along in your car), wander, go places,
    /// and do small jobs: fetch the item you're looking at, collect every item of a kind into
    /// a pile, tidy up around you, or carry something to a place.
    /// </summary>
    internal static class Bots
    {
        public enum Order { Copy, Follow, Stay, Wander, GoTo, Fetch, Collect, Deliver }

        public sealed class Place
        {
            public string Label, Path;
            public Vector3 Fallback;
            public Place(string label, string path, Vector3 fallback) { Label = label; Path = path; Fallback = fallback; }

            public Vector3 Position
            {
                get { Transform t = GameUtil.FindPath(Path); return t != null ? t.position : Fallback; }
            }
        }

        public static readonly Place[] Places =
        {
            new Place("Home", "YARD/SpawnToHome", new Vector3(-10.6f, 0.2f, 7.5f)),
            new Place("Teimo's shop", "STORE/SpawnToStore", new Vector3(-1547.7f, 3.6f, 1176.4f)),
            new Place("Fleetari's", "REPAIRSHOP/SpawnToRepair", new Vector3(1548.8f, 5.5f, 734.3f)),
        };

        public sealed class Bot
        {
            public int Number;
            public byte NetId;
            public NetPlayer P;
            public Order Order = Order.Copy;
            public string OrderText = "copying you";
            public Vector3 Feet;
            public float Yaw;
            public Vector3 Velocity;
            public Vector3 Target;
            public Vector3 Home;
            public float NextWander;
            public NetObject Carry;
            public bool CarryWasKinematic;
            public NetObject FetchTarget;
            public bool FetchFixed;             // fetching one particular item (not "the nearest")
            public float StuckTime;
            public Vector3 LastProgressPos;
            public int BrokenReports;
            public float ForceCrouch = -1f;     // self-test: pose override
            public readonly Queue<KeyValuePair<float, PlayerSnapshot>> Delayed = new Queue<KeyValuePair<float, PlayerSnapshot>>();

            // Jobs
            public Vector3 Pile;                // where collected things go
            public string Kind;                 // item kind to collect (null = any loose item)
            public float Radius;                // how far from the pile to look
            public int Done;                    // items delivered in this job
            public Place Destination;           // Deliver: where to take it
            public bool Teleport;               // Deliver: teleport instead of walking
            public bool Riding;                 // in your passenger seat

            // Network
            public PlayerSnapshot Last;
            public bool HasLast;
        }

        sealed class RemoteBot
        {
            public byte Owner, Id;
            public NetPlayer P;
            public Appearance Look;
        }

        const float CopyDelay = 0.8f;
        const int MaxBots = 12;
        const float SendInterval = 0.05f;
        const float CarryMaxMass = 60f;
        static readonly List<Bot> bots = new List<Bot>();
        static readonly Dictionary<int, RemoteBot> remote = new Dictionary<int, RemoteBot>();
        static readonly Queue<KeyValuePair<float, Vector3>> reaches = new Queue<KeyValuePair<float, Vector3>>();
        static readonly Queue<KeyValuePair<float, byte>> emotes = new Queue<KeyValuePair<float, byte>>();
        static readonly NetWriter writer = new NetWriter(128);
        static readonly NetReader reader = new NetReader();
        static float nextSample, nextReport, nextSend;
        static bool meInVehicle;            // this frame: are you in a car (driving or riding)?
        static int counter, bodyCycle;
        static readonly System.Random rng = new System.Random();

        public static IList<Bot> All { get { return bots; } }
        public static bool Any { get { return bots.Count > 0; } }

        /// <summary>Other players' bots (for name tags).</summary>
        public static IEnumerable<NetPlayer> RemotePlayers
        {
            get { foreach (RemoteBot rb in remote.Values) yield return rb.P; }
        }

        public static void Init()
        {
            Session.On(Msg.BotSpawn, OnSpawn);
            Session.On(Msg.BotRemove, OnRemove);
            Session.On(Msg.BotStates, OnStates);
            Session.On(Msg.BotAction, OnAction);
            Session.PlayerLeft += p => { if (p != null) RemoveRemoteOf(p.Slot); };
            Session.Ended += reason => ClearRemote();
        }

        // ================================================================ commands

        public static void Spawn()
        {
            if (!MP.InGame || !LocalPlayer.Bound) { Notifications.Show("Bots can only be spawned in the world."); return; }
            if (bots.Count >= MaxBots) { Notifications.Show("That's enough bots (" + MaxBots + ")."); return; }
            Bot b = new Bot { Number = ++counter, NetId = FreeNetId() };
            b.P = new NetPlayer
            {
                Slot = 250,                 // not a real player slot
                Name = "Bot " + b.Number,
                Phase = PlayerPhase.Playing,
                ClockSynced = true,
                Look = Options.LocalAppearance()
            };
            b.P.Look.Body = (byte)(bodyCycle++ % Catalog.Bodies.Length);
            b.P.Avatar = RemoteAvatar.Create(b.P);
            Transform me = LocalPlayer.Player;
            b.Feet = Ground(LocalPlayer.Feet + me.right * (1.5f + bots.Count * 0.9f));
            b.Yaw = me.eulerAngles.y;
            b.Home = b.Feet;
            b.LastProgressPos = b.Feet;
            bots.Add(b);
            nextReport = Time.realtimeSinceStartup + 3f;
            SendSpawn(b, null);
            Log.Info(b.P.Name + " spawned (" + Catalog.Bodies[b.P.Look.Body].Label + ").");
        }

        static byte FreeNetId()
        {
            for (int id = 1; id < 256; id++)
            {
                bool used = false;
                foreach (Bot b in bots) if (b.NetId == id) { used = true; break; }
                if (!used) return (byte)id;
            }
            return 255;
        }

        public static void Remove(Bot b)
        {
            if (b == null) return;
            DropCarry(b, false);
            if (b.P.Avatar != null) b.P.Avatar.Destroy();
            bots.Remove(b);
            SendRemove(b.NetId);
            Log.Info(b.P.Name + " removed.");
        }

        public static void RemoveAll()
        {
            while (bots.Count > 0) Remove(bots[bots.Count - 1]);
        }

        public static void NextBody(Bot b)
        {
            b.P.Look.Body = (byte)((b.P.Look.Body + 1) % Catalog.Bodies.Length);
            b.P.Avatar.Rebuild(b.P.Look);
            SendSpawn(b, null);
            Log.Info(b.P.Name + " body: " + Catalog.Bodies[b.P.Look.Body].Label);
        }

        public static void Command(Bot b, Order order)
        {
            b.FetchTarget = null;
            b.FetchFixed = false;
            if (order != Order.Fetch && order != Order.Follow && order != Order.Deliver) DropCarry(b, true);
            if (order != Order.Follow) LeaveCar(b);
            b.Order = order;
            b.Home = b.Feet;
            b.NextWander = 0f;
            b.StuckTime = 0f;
            switch (order)
            {
                case Order.Copy: b.OrderText = "copying you"; b.Delayed.Clear(); break;
                case Order.Follow: b.OrderText = "following you"; break;
                case Order.Stay: b.OrderText = "staying here"; break;
                case Order.Wander: b.OrderText = "wandering around"; break;
                case Order.Fetch: b.OrderText = "looking for something to bring you"; break;
            }
        }

        public static void GoTo(Bot b, Place place, bool teleport)
        {
            DropCarry(b, true);
            LeaveCar(b);
            Vector3 target = place.Position;
            b.Order = Order.GoTo;
            b.Target = target;
            b.OrderText = (teleport ? "teleported to " : "walking to ") + place.Label;
            b.StuckTime = 0f;
            if (teleport) b.Feet = Ground(target + new Vector3(UnityEngine.Random.Range(-2f, 2f), 0f, UnityEngine.Random.Range(-2f, 2f)));
            Log.Info(b.P.Name + " " + b.OrderText);
        }

        public static void Wave(Bot b) { Emote(b, Emotes.Wave); }

        /// <summary>Plays an emote on a bot (gesture id, see <see cref="Emotes"/>).</summary>
        public static void Emote(Bot b, byte id)
        {
            b.P.Avatar.PlayGesture(id);
            SendAction(b, (byte)(id == Emotes.Wave ? 1 : 10 + id), Vector3.zero);
        }

        /// <summary>Copying bots repeat your emotes.</summary>
        public static void MirrorEmote(byte id)
        {
            if (Any) emotes.Enqueue(new KeyValuePair<float, byte>(Time.realtimeSinceStartup + CopyDelay, id));
        }

        // ---------------------------------------------------------------- jobs

        /// <summary>The item in the middle of your screen (within reach of a long arm), or null.</summary>
        public static NetObject LookedAt()
        {
            if (!LocalPlayer.Bound) return null;
            Transform cam = LocalPlayer.View.transform;
            RaycastHit[] hits = Physics.RaycastAll(cam.position, cam.forward, 8f, WorldMask);
            Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                if (IsBotOrPlayer(hits[i].collider)) continue;
                for (Transform t = hits[i].collider.transform; t != null; t = t.parent)
                {
                    NetObject o = ObjectSync.Of(t.gameObject);
                    if (o != null) return o;
                }
                if (!hits[i].collider.isTrigger) return null; // a wall or the ground is in the way
            }
            return null;
        }

        /// <summary>Why a bot can't pick this up, or null if it can.</summary>
        static string CannotCarry(NetObject o)
        {
            if (o == null || !o.Alive) return "Look at an item first (it has to be in the middle of the screen).";
            if (o.Go == LocalPlayer.HeldObject) return "You're holding that yourself.";
            if (!ObjectSync.OwnedLocally(o) || o.Replica) return "Someone else is using that.";
            if (o.Body.isKinematic) return "That is fixed in place.";
            if (o.Body.mass > CarryMaxMass) return "That's too heavy to carry.";
            return null;
        }

        /// <summary>Bring me the item I'm looking at.</summary>
        public static void FetchThat(IEnumerable<Bot> who)
        {
            NetObject o = LookedAt();
            string why = CannotCarry(o);
            if (why != null) { Notifications.Show(why); return; }
            foreach (Bot b in who)
            {
                Command(b, Order.Fetch);
                b.FetchTarget = o;
                b.FetchFixed = true;
                b.OrderText = "getting " + Pretty(o.Name) + " for you";
                return; // one bot is enough for one item
            }
        }

        /// <summary>Pile every item of the kind I'm looking at where I stand now.</summary>
        public static void CollectLikeThat(IEnumerable<Bot> who)
        {
            NetObject o = LookedAt();
            if (o == null || !o.Alive) { Notifications.Show("Look at an item first: the bots collect every item of that kind."); return; }
            StartCollect(who, Pretty(o.Name), 80f);
        }

        /// <summary>Pile up the loose items around me.</summary>
        public static void TidyUp(IEnumerable<Bot> who)
        {
            StartCollect(who, null, 15f);
        }

        static void StartCollect(IEnumerable<Bot> who, string kind, float radius)
        {
            if (!LocalPlayer.Bound) return;
            Vector3 pile = Ground(LocalPlayer.Feet + Flat3(LocalPlayer.View.transform.forward) * 1.2f);
            int n = 0;
            foreach (Bot b in who)
            {
                Command(b, Order.Collect);
                b.Pile = pile;
                b.Kind = kind;
                b.Radius = radius;
                b.Done = 0;
                b.OrderText = kind != null ? "collecting " + kind + " into a pile" : "tidying up";
                n++;
            }
            if (n > 0) Notifications.Show(kind != null ? "Collecting every " + kind + " here." : "Tidying up the loose things around here.");
        }

        /// <summary>Take the item I'm looking at (or what the bot already carries) to a place.</summary>
        public static void Deliver(IEnumerable<Bot> who, Place place, bool teleport)
        {
            NetObject o = LookedAt();
            foreach (Bot b in who)
            {
                if (b.Carry == null)
                {
                    string why = CannotCarry(o);
                    if (why != null) { Notifications.Show(why); return; }
                }
                LeaveCar(b);
                b.Order = Order.Deliver;
                b.Destination = place;
                b.Teleport = teleport;
                b.StuckTime = 0f;
                b.FetchTarget = b.Carry == null ? o : null;
                b.FetchFixed = b.Carry == null;
                b.OrderText = "taking " + (b.Carry != null ? Pretty(b.Carry.Name) : Pretty(o.Name)) + " to " + place.Label;
                return; // one item, one bot
            }
        }

        /// <summary>Self-test: stand in front of the camera, facing it.</summary>
        public static void PlaceInFront(Bot b, float distance, float side)
        {
            Transform cam = LocalPlayer.View.transform;
            Vector3 fwd = Flat3(cam.forward);
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            b.Feet = Ground(LocalPlayer.Feet + fwd * distance + right * side);
            b.Yaw = Quaternion.LookRotation(-fwd).eulerAngles.y;
            b.Home = b.Feet;
            b.Velocity = Vector3.zero;
            Command(b, Order.Stay);
        }

        /// <summary>Copy bots repeat the reach towards things you use.</summary>
        public static void MirrorReach(Vector3 target)
        {
            if (Any) reaches.Enqueue(new KeyValuePair<float, Vector3>(Time.realtimeSinceStartup + CopyDelay, target));
        }

        public static void OnWorldLeft()
        {
            for (int i = bots.Count - 1; i >= 0; i--) Remove(bots[i]);
            reaches.Clear();
            emotes.Clear();
            // Other players' bots come back when we are in the world again.
            foreach (RemoteBot rb in remote.Values) DestroyAvatar(rb);
        }

        // ================================================================ per frame

        public static void Update()
        {
            UpdateRemote();
            if (bots.Count == 0) return;
            if (!MP.InGame || !LocalPlayer.Bound) { OnWorldLeft(); return; }
            float now = Time.realtimeSinceStartup;

            bool sample = now >= nextSample;
            PlayerSnapshot mine = default(PlayerSnapshot);
            if (sample)
            {
                nextSample = now + 0.05f;
                mine = LocalPlayer.Sample();
            }
            Bot rider = Rider();
            meInVehicle = LocalPlayer.PassengerOf != null || VehicleSync.FindByChild(LocalPlayer.Player) != null;

            for (int i = 0; i < bots.Count; i++)
            {
                Bot b = bots[i];
                try
                {
                    if (b.Order == Order.Copy)
                    {
                        if (sample)
                        {
                            PlayerSnapshot s = mine;
                            CopyOffset(ref s, i);
                            b.Delayed.Enqueue(new KeyValuePair<float, PlayerSnapshot>(now + CopyDelay, s));
                        }
                        while (b.Delayed.Count > 0 && b.Delayed.Peek().Key <= now)
                        {
                            KeyValuePair<float, PlayerSnapshot> d = b.Delayed.Dequeue();
                            PlayerSnapshot s = d.Value;
                            s.Time = d.Key;
                            if (s.Vehicle == 0) { b.Feet = s.Position; b.Yaw = s.Yaw; }
                            Push(b, s);
                        }
                    }
                    else if (b.Order == Order.Follow && sample && mine.Vehicle != 0 && mine.Seat == 1 && b == rider)
                    {
                        // Ride along in the passenger seat.
                        if (!b.Riding) { DropCarry(b, true); b.Riding = true; b.OrderText = "riding with you"; }
                        PlayerSnapshot s = mine;
                        s.Time = now;
                        CopyOffset(ref s, 0);
                        Push(b, s);
                    }
                    else
                    {
                        if (b.Riding && mine.Vehicle == 0 && sample) LeaveCar(b);
                        if (!b.Riding)
                        {
                            Think(b, Time.deltaTime);
                            if (sample) Push(b, Synth(b, now));
                        }
                    }
                    b.P.Avatar.Update();
                }
                catch (Exception e) { Log.ExceptionOnce("Bot " + b.P.Name, e); }
            }

            while (reaches.Count > 0 && reaches.Peek().Key <= now)
            {
                Vector3 target = reaches.Dequeue().Value;
                foreach (Bot b in bots)
                    if (b.Order == Order.Copy)
                    {
                        Vector3 at = target + (b.P.Avatar.HeadPosition - LocalPlayer.View.transform.position);
                        b.P.Avatar.Reach(at);
                        SendAction(b, 2, at);
                    }
            }
            while (emotes.Count > 0 && emotes.Peek().Key <= now)
            {
                byte id = emotes.Dequeue().Value;
                foreach (Bot b in bots) if (b.Order == Order.Copy) Emote(b, id);
            }

            if (now >= nextSend)
            {
                nextSend = now + SendInterval;
                SendStates();
            }

            if (now >= nextReport)
            {
                nextReport = now + 5f;
                foreach (Bot b in bots)
                {
                    if (Log.Verbose) Log.Debug(b.P.Name + " (" + b.OrderText + "): " + b.P.Avatar.Diagnostics());
                    if (b.P.Avatar.LooksBroken() && b.BrokenReports++ < 2)
                        Log.Info(b.P.Name + " body looks off: " + b.P.Avatar.Diagnostics());
                }
            }
        }

        /// <summary>The bot that gets the passenger seat: the first one following you.</summary>
        static Bot Rider()
        {
            foreach (Bot b in bots) if (b.Riding) return b;
            foreach (Bot b in bots) if (b.Order == Order.Follow) return b;
            return null;
        }

        static void LeaveCar(Bot b)
        {
            if (!b.Riding) return;
            b.Riding = false;
            if (LocalPlayer.Bound)
            {
                b.Feet = Ground(LocalPlayer.Feet + LocalPlayer.Player.right * 1.5f);
                b.Yaw = LocalPlayer.Player.eulerAngles.y;
                b.LastProgressPos = b.Feet;
            }
            b.Velocity = Vector3.zero;
            if (b.Order == Order.Follow) b.OrderText = "following you";
        }

        public static void LateUpdate()
        {
            foreach (RemoteBot rb in remote.Values)
                if (rb.P.Avatar != null) rb.P.Avatar.LateUpdate();

            for (int i = 0; i < bots.Count; i++)
            {
                Bot b = bots[i];
                if (b.P.Avatar == null) continue;
                b.P.Avatar.LateUpdate();
                if (b.Carry != null && b.Carry.Alive && b.Carry.Go == LocalPlayer.HeldObject)
                {
                    // You took it out of the bot's hands: let go, or the two of you pull it back and forth.
                    NetObject o = b.Carry;
                    b.Carry = null;
                    o.Body.isKinematic = b.CarryWasKinematic;
                    if (b.Order == Order.Fetch || b.Order == Order.Deliver) Command(b, Order.Follow);
                    Log.Info(b.P.Name + " let go of " + o.Name);
                }
                else if (b.Carry != null && b.Carry.Alive)
                {
                    // Hold it in front of the chest, like a player carrying something.
                    Transform t = b.Carry.Go.transform;
                    Vector3 fwd = Quaternion.Euler(0f, b.Yaw, 0f) * Vector3.forward;
                    t.position = b.P.Avatar.HeadPosition + fwd * 0.45f - Vector3.up * 0.55f;
                }
            }
        }

        /// <summary>Same packing and unpacking a real player's update goes through.</summary>
        static void Push(Bot b, PlayerSnapshot s)
        {
            writer.Reset();
            s.Write(writer);
            reader.Set(writer.Buffer, 0, writer.Length);
            PlayerSnapshot r = PlayerSnapshot.Read(reader);
            b.P.Avatar.Push(r);
            b.Last = s;
            b.HasLast = true;
        }

        static void CopyOffset(ref PlayerSnapshot s, int index)
        {
            if (s.Vehicle != 0)
            {
                // Driver -> passenger seat (the first bot); the others "sit" in the same seat.
                s.Seat = (byte)(s.Seat == 1 ? 2 : 1);
                s.Position.x = -s.Position.x;
                s.Head.x = -s.Head.x;
                if (s.Seat == 2) s.Yaw = -s.Yaw;
                return;
            }
            Vector3 side = Quaternion.Euler(0f, s.Yaw, 0f) * Vector3.right * (1.5f + index * 0.9f);
            s.Position += side;
            s.Head += side;
        }

        static PlayerSnapshot Synth(Bot b, float now)
        {
            PlayerSnapshot s = new PlayerSnapshot();
            s.Time = now;
            s.Position = b.Feet;
            s.Head = b.Feet + Vector3.up * 1.62f;
            s.Yaw = b.Yaw;
            s.Pitch = b.Carry != null ? 25f : 0f;
            s.Velocity = b.Velocity;
            if (b.ForceCrouch >= 0f) s.Crouch = b.ForceCrouch;
            s.Flags = PlayerSnapshot.FlagGrounded;
            if (new Vector3(b.Velocity.x, 0f, b.Velocity.z).magnitude > 3.6f) s.Flags |= PlayerSnapshot.FlagRunning;
            s.Held = b.Carry != null ? b.Carry.Id : 0u;
            return s;
        }

        // ================================================================ behaviour

        static void Think(Bot b, float dt)
        {
            Vector3 me = LocalPlayer.Feet;
            switch (b.Order)
            {
                case Order.Stay:
                    Stop(b, me);
                    break;

                case Order.Follow:
                {
                    if (meInVehicle) { Stop(b, me); b.OrderText = "waiting for you to get out of the car"; break; }
                    b.OrderText = "following you";
                    float d = Flat(me - b.Feet);
                    if (d > 60f) { b.Feet = Ground(me - LocalPlayer.Player.forward * 3f); b.Velocity = Vector3.zero; break; }
                    if (d > 2.5f) Walk(b, me, d > 8f ? 5f : 1.6f, dt, 2.2f);
                    else Stop(b, me);
                    if (b.Carry != null && d < 2.6f) DropCarry(b, true);
                    break;
                }

                case Order.Wander:
                    if (Time.realtimeSinceStartup >= b.NextWander || Flat(b.Target - b.Feet) < 0.8f)
                    {
                        b.NextWander = Time.realtimeSinceStartup + 6f + (float)rng.NextDouble() * 6f;
                        float ang = (float)rng.NextDouble() * 360f;
                        float r = 3f + (float)rng.NextDouble() * 12f;
                        b.Target = b.Home + Quaternion.Euler(0f, ang, 0f) * Vector3.forward * r;
                    }
                    if (Flat(b.Target - b.Feet) > 0.8f) Walk(b, b.Target, 1.3f, dt, 0.6f);
                    else Stop(b, b.Target);
                    break;

                case Order.GoTo:
                {
                    float d = Flat(b.Target - b.Feet);
                    if (d > 1.5f) Walk(b, b.Target, d > 15f ? 5f : 1.6f, dt, 1.2f);
                    else { Stop(b, me); b.OrderText = b.OrderText.Replace("walking to ", "at "); }
                    break;
                }

                case Order.Fetch:
                    Fetch(b, dt, me);
                    break;

                case Order.Collect:
                    Collect(b, dt);
                    break;

                case Order.Deliver:
                    DeliverStep(b, dt, me);
                    break;
            }
        }

        static void Fetch(Bot b, float dt, Vector3 me)
        {
            if (b.Carry != null)
            {
                if (WaitWhileFarAway(b, me)) return;
                float d = Flat(me - b.Feet);
                b.OrderText = "bringing you " + Pretty(b.Carry.Name);
                if (d > 2.4f) Walk(b, me, d > 10f ? 4.5f : 1.6f, dt, 2f);
                else
                {
                    DropCarry(b, true);
                    Command(b, Order.Follow);
                }
                return;
            }
            if (!Gettable(b, b.FetchTarget))
            {
                if (b.FetchFixed)
                {
                    Notifications.Show(b.P.Name + ": I can't get that anymore.");
                    Command(b, Order.Follow);
                    return;
                }
                b.FetchTarget = FindItem(b.Feet, null, b.Feet, 40f, 0f);
                if (b.FetchTarget == null)
                {
                    Notifications.Show(b.P.Name + ": nothing I can carry nearby.");
                    Command(b, Order.Follow);
                    return;
                }
            }
            if (WalkTo(b, b.FetchTarget, dt)) PickUp(b);
        }

        static bool WaitWhileFarAway(Bot b, Vector3 me)
        {
            // Bringing something to a moving car makes no sense; wait at the spot instead.
            if (Flat(me - b.Feet) > 150f)
            {
                b.OrderText = "waiting with " + Pretty(b.Carry.Name) + " (you're far away)";
                Stop(b, me);
                return true;
            }
            return false;
        }

        static void Collect(Bot b, float dt)
        {
            if (b.Carry != null)
            {
                float d = Flat(b.Pile - b.Feet);
                b.OrderText = (b.Kind != null ? "collecting " + b.Kind : "tidying up") + ": carrying " + Pretty(b.Carry.Name) + " (" + b.Done + " done)";
                if (d > 1.0f) { Walk(b, b.Pile, d > 12f ? 4.5f : 1.8f, dt, 0.8f); return; }
                // Lay it down next to the others, in a small spiral so they don't stack up.
                NetObject o = b.Carry;
                float ang = b.Done * 47f + b.NetId * 113f;
                float r = 0.25f + 0.12f * (b.Done % 6);
                Vector3 at = b.Pile + Quaternion.Euler(0f, ang, 0f) * Vector3.forward * r + Vector3.up * 0.5f;
                DropCarry(b, false);
                if (o.Alive) o.Go.transform.position = at;
                b.Done++;
                return;
            }
            if (!Gettable(b, b.FetchTarget))
            {
                b.FetchTarget = FindItem(b.Pile, b.Kind, b.Feet, b.Radius, 3.0f);
                if (b.FetchTarget == null)
                {
                    string what = b.Kind != null ? b.Kind : "loose things";
                    Vector3 pile = b.Pile;
                    Command(b, Order.Stay);
                    b.OrderText = "finished (" + b.Done + " " + what + ")";
                    // One message for the whole job, when the last helper on this pile is done.
                    int total = 0;
                    foreach (Bot other in bots)
                    {
                        if ((other.Pile - pile).sqrMagnitude > 0.01f) continue;
                        if (other.Order == Order.Collect) return;
                        total += other.Done;
                    }
                    Notifications.Show("Done: " + total + " " + what + " piled up.");
                    return;
                }
            }
            b.OrderText = (b.Kind != null ? "collecting " + b.Kind : "tidying up") + ": going to get " + Pretty(b.FetchTarget.Name) + " (" + b.Done + " done)";
            if (WalkTo(b, b.FetchTarget, dt)) PickUp(b);
        }

        static void DeliverStep(Bot b, float dt, Vector3 me)
        {
            if (b.Carry == null)
            {
                if (!Gettable(b, b.FetchTarget))
                {
                    Notifications.Show(b.P.Name + ": I can't get that anymore.");
                    Command(b, Order.Follow);
                    return;
                }
                if (WalkTo(b, b.FetchTarget, dt)) PickUp(b);
                return;
            }
            Place place = b.Destination;
            Vector3 to = place.Position;
            if (b.Teleport && Flat(to - b.Feet) > 5f)
                b.Feet = Ground(to + new Vector3(UnityEngine.Random.Range(-1.5f, 1.5f), 0f, UnityEngine.Random.Range(-1.5f, 1.5f)));
            float d = Flat(to - b.Feet);
            b.OrderText = "taking " + Pretty(b.Carry.Name) + " to " + place.Label;
            if (d > 2f) { Walk(b, to, d > 15f ? 5f : 1.6f, dt, 1.5f); return; }
            string what = Pretty(b.Carry.Name);
            DropCarry(b, true);
            Command(b, Order.Stay);
            b.OrderText = "left " + what + " at " + place.Label + ", waiting there";
            Notifications.Show(b.P.Name + ": " + what + " is at " + place.Label + ".");
        }

        static bool Gettable(Bot b, NetObject o)
        {
            if (o == null || !o.Alive || !o.Go.activeInHierarchy || o.Replica || !ObjectSync.OwnedLocally(o)) return false;
            if (o.HeldBy != 255 || o.Go == LocalPlayer.HeldObject) return false;
            foreach (Bot other in bots) if (other != b && (other.Carry == o || other.FetchTarget == o)) return false;
            return true;
        }

        /// <summary>Walks to an item; true once it is within reach.</summary>
        static bool WalkTo(Bot b, NetObject o, float dt)
        {
            Vector3 at = o.Go.transform.position;
            float dist = Flat(at - b.Feet);
            if (dist > 0.9f) { Walk(b, at, dist > 10f ? 4.5f : 1.6f, dt, 0.7f); return false; }
            return true;
        }

        static void PickUp(Bot b)
        {
            NetObject o = b.FetchTarget;
            b.FetchTarget = null;
            b.FetchFixed = false;
            b.Carry = o;
            b.CarryWasKinematic = o.Body.isKinematic;
            o.Body.isKinematic = true;
            o.HeldBy = b.P.Slot;
            Vector3 at = o.Go.transform.position;
            b.P.Avatar.Reach(at);
            SendAction(b, 2, at);
            Log.Info(b.P.Name + " picked up " + o.Name);
        }

        /// <summary>The nearest loose item a bot may move (owned by this game). With a kind, only
        /// that kind; skips things already lying within <paramref name="leaveWithin"/> of the pile.</summary>
        static NetObject FindItem(Vector3 around, string kind, Vector3 from, float radius, float leaveWithin)
        {
            NetObject best = null;
            float bestD = float.MaxValue;
            GameObject mineInHand = LocalPlayer.HeldObject;
            foreach (NetObject o in ObjectSync.All)
            {
                if (!o.Alive || !o.Go.activeInHierarchy || o.Replica || o.HeldBy != 255 || o.Go == mineInHand) continue;
                if (!ObjectSync.OwnedLocally(o) || o.Body.isKinematic || o.Body.mass > CarryMaxMass) continue;
                Vector3 p = o.Go.transform.position;
                float fromPile = Flat(p - around);
                if (fromPile > radius || fromPile < leaveWithin) continue;
                if (kind != null && !string.Equals(Pretty(o.Name), kind, StringComparison.OrdinalIgnoreCase)) continue;
                // Tidying only moves loose items (shop goods, tools, bottles...), never car parts
                // you laid out on purpose. The game names those "(itemx)", parts "(Clone)".
                if (kind == null && o.Name.IndexOf("(itemx)", StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool taken = false;
                foreach (Bot other in bots) if (other.Carry == o || other.FetchTarget == o) taken = true;
                if (taken) continue;
                float d = (p - from).sqrMagnitude;
                if (d < bestD) { bestD = d; best = o; }
            }
            return best;
        }

        static void DropCarry(Bot b, bool inFront)
        {
            NetObject o = b.Carry;
            b.Carry = null;
            if (o == null || !o.Alive) return;
            if (inFront)
            {
                Vector3 fwd = Quaternion.Euler(0f, b.Yaw, 0f) * Vector3.forward;
                o.Go.transform.position = b.Feet + fwd * 0.7f + Vector3.up * 0.6f;
            }
            o.Body.isKinematic = b.CarryWasKinematic;
            o.HeldBy = 255;
            if (!o.Body.isKinematic) { o.Body.velocity = Vector3.zero; o.Body.WakeUp(); }
            Log.Info(b.P.Name + " put down " + o.Name);
        }

        static void Stop(Bot b, Vector3 face)
        {
            b.Velocity = Vector3.Lerp(b.Velocity, Vector3.zero, 0.3f);
            Vector3 d = face - b.Feet;
            d.y = 0f;
            if (d.sqrMagnitude > 0.25f) b.Yaw = Mathf.MoveTowardsAngle(b.Yaw, Quaternion.LookRotation(d).eulerAngles.y, 240f * Time.deltaTime);
            b.Feet = Ground(b.Feet);
        }

        /// <summary>Walks towards a point: turns smoothly, steps around obstacles, follows the ground.</summary>
        static void Walk(Bot b, Vector3 target, float speed, float dt, float stopAt)
        {
            Vector3 to = target - b.Feet;
            to.y = 0f;
            float dist = to.magnitude;
            if (dist < stopAt || dist < 0.01f) { Stop(b, target); return; }
            Vector3 dir = to / dist;
            // Look a little ahead; if something blocks the way, try angles to the side.
            float[] tries = { 0f, 35f, -35f, 70f, -70f, 110f, -110f };
            Vector3 chosen = dir;
            for (int i = 0; i < tries.Length; i++)
            {
                Vector3 d = Quaternion.Euler(0f, tries[i], 0f) * dir;
                if (!Blocked(b.Feet, d, 0.9f)) { chosen = d; break; }
            }
            float wantYaw = Quaternion.LookRotation(chosen).eulerAngles.y;
            b.Yaw = Mathf.MoveTowardsAngle(b.Yaw, wantYaw, 300f * dt);
            Vector3 fwd = Quaternion.Euler(0f, b.Yaw, 0f) * Vector3.forward;
            float step = Mathf.Min(speed * dt, dist);
            Vector3 next = Ground(b.Feet + fwd * step);
            b.Velocity = dt > 0f ? (next - b.Feet) / dt : Vector3.zero;
            b.Feet = next;

            // Stuck for a while (fence, wall)? Hop a couple of metres sideways.
            if (Flat(b.Feet - b.LastProgressPos) > 1f) { b.LastProgressPos = b.Feet; b.StuckTime = 0f; }
            else if ((b.StuckTime += dt) > 3f)
            {
                b.StuckTime = 0f;
                b.Feet = Ground(b.Feet + Quaternion.Euler(0f, 90f, 0f) * dir * 2f + dir * 1.5f);
                b.LastProgressPos = b.Feet;
            }
        }

        static bool Blocked(Vector3 feet, Vector3 dir, float range)
        {
            RaycastHit[] hits = Physics.RaycastAll(feet + Vector3.up * 0.55f, dir, range, WorldMask);
            for (int i = 0; i < hits.Length; i++) if (!hits[i].collider.isTrigger && !IsBotOrPlayer(hits[i].collider)) return true;
            return false;
        }

        /// <summary>Puts a point on the ground (terrain, floors, roads).</summary>
        static Vector3 Ground(Vector3 p)
        {
            RaycastHit[] hits = Physics.RaycastAll(p + Vector3.up * 1.6f, Vector3.down, 30f, WorldMask);
            float best = float.MaxValue;
            Vector3 result = p;
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider.isTrigger || IsBotOrPlayer(hits[i].collider) || hits[i].rigidbody != null && !hits[i].rigidbody.isKinematic) continue;
                if (hits[i].distance < best) { best = hits[i].distance; result = hits[i].point; }
            }
            return result;
        }

        static int WorldMask
        {
            get
            {
                int mask = ~0;
                mask &= ~(1 << 2);   // Ignore Raycast
                mask &= ~(1 << 23);  // PlayerOnlyColl (player bodies, ours included)
                return mask;
            }
        }

        static bool IsBotOrPlayer(Collider c)
        {
            Transform r = c.transform.root;
            return r.name == "PLAYER" || r.name.StartsWith("MSCMP", StringComparison.Ordinal);
        }

        static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        static Vector3 Flat3(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        public static string Pretty(string n)
        {
            if (string.IsNullOrEmpty(n)) return "something";
            int i = n.IndexOf('(');
            return (i > 0 ? n.Substring(0, i) : n).Trim();
        }

        // ================================================================ network
        //
        // The game that spawned a bot runs it. Its body, movement, waves and reaches go to the
        // host, which passes them on to everybody else stamped with the owner's slot, exactly
        // like a player's own updates.

        static bool Online { get { return Session.Active && Session.Connected && Session.Local != null; } }
        static bool Wanted { get { return Online || Loopback.On; } }
        static readonly NetReader loopReader = new NetReader();

        /// <summary>Sends the message in the session writer, and with the loopback test on also
        /// hands it to our own receiving code as if a friend had sent it.</summary>
        static void Emit(NetWriter w, Msg msg, bool reliable, int channel, NetPlayer onlyTo)
        {
            if (Online)
            {
                if (onlyTo != null) Session.SendTo(onlyTo, reliable, channel);
                else Session.SendToAll(reliable, channel);
            }
            if (Loopback.On && onlyTo == null)
            {
                // Skip the message id and our slot; the loopback "friend" owns these.
                loopReader.Set(w.Buffer, 2, w.Length - 2);
                try
                {
                    switch (msg)
                    {
                        case Msg.BotSpawn: HandleSpawn(Loopback.BotOwner, loopReader, false); break;
                        case Msg.BotRemove: HandleRemove(Loopback.BotOwner, loopReader, false); break;
                        case Msg.BotStates: HandleStates(Loopback.BotOwner, loopReader, false); break;
                        case Msg.BotAction: HandleAction(Loopback.BotOwner, loopReader, false); break;
                    }
                }
                catch (Exception e) { Log.ExceptionOnce("Bot loopback " + msg, e); }
            }
        }

        static void SendSpawn(Bot b, NetPlayer onlyTo)
        {
            if (!Wanted) return;
            NetWriter w = Session.Begin(Msg.BotSpawn);
            w.Byte(Session.LocalSlot);
            w.Byte(b.NetId);
            w.String(b.P.Name);
            b.P.Look.Write(w);
            Emit(w, Msg.BotSpawn, true, Channel.Reliable, onlyTo);
        }

        static void SendRemove(byte id)
        {
            if (!Wanted) return;
            NetWriter w = Session.Begin(Msg.BotRemove);
            w.Byte(Session.LocalSlot);
            w.Byte(id);
            Emit(w, Msg.BotRemove, true, Channel.Reliable, null);
        }

        /// <summary>kind 1 = wave, 2 = reach towards a point, 10 + id = another emote.</summary>
        static void SendAction(Bot b, byte kind, Vector3 at)
        {
            if (!Wanted) return;
            NetWriter w = Session.Begin(Msg.BotAction);
            w.Byte(Session.LocalSlot);
            w.Byte(b.NetId);
            w.Byte(kind);
            if (kind == 2) w.Vector3(at);
            Emit(w, Msg.BotAction, true, Channel.Reliable, null);
        }

        static void SendStates()
        {
            if (!Wanted) return;
            NetWriter w = Session.Begin(Msg.BotStates);
            w.Byte(Session.LocalSlot);
            int countPos = w.Length;
            w.Byte(0);
            byte n = 0;
            foreach (Bot b in bots)
            {
                if (!b.HasLast) continue;
                w.Byte(b.NetId);
                b.Last.Write(w);
                n++;
            }
            if (n == 0) return;
            w.Buffer[countPos] = n;
            Emit(w, Msg.BotStates, false, Channel.State, null);
        }

        /// <summary>The loopback test was switched on or off: show or hide the mirrored bots.</summary>
        public static void LoopbackChanged(bool on)
        {
            if (on) { foreach (Bot b in bots) SendSpawn(b, null); }
            else RemoveRemoteOf(Loopback.BotOwner.Slot);
        }

        /// <summary>Host: tells a player who just joined about every bot in the session.</summary>
        public static void SendCatchUp(NetPlayer p)
        {
            foreach (Bot b in bots)
            {
                NetWriter w = Session.Begin(Msg.BotSpawn);
                w.Byte(Session.LocalSlot);
                w.Byte(b.NetId);
                w.String(b.P.Name);
                b.P.Look.Write(w);
                Session.SendTo(p, true, Channel.Reliable);
            }
            foreach (RemoteBot rb in remote.Values)
            {
                if (rb.Owner == p.Slot || rb.Owner == Loopback.BotOwner.Slot) continue;
                NetWriter w = Session.Begin(Msg.BotSpawn);
                w.Byte(rb.Owner);
                w.Byte(rb.Id);
                w.String(BaseName(rb.P.Name));
                rb.Look.Write(w);
                Session.SendTo(p, true, Channel.Reliable);
            }
        }

        /// <summary>Who really sent it: the host stamps clients' messages with their own slot.</summary>
        static NetPlayer OwnerOf(NetPlayer from, byte claimed)
        {
            if (from == null) return null;
            return Session.IsHost ? from : Session.BySlot(claimed);
        }

        static int Key(byte owner, byte id) { return (owner << 8) | id; }

        static void OnSpawn(NetPlayer from, NetReader r) { byte slot = r.Byte(); HandleSpawn(OwnerOf(from, slot), r, Session.IsHost); }
        static void OnRemove(NetPlayer from, NetReader r) { byte slot = r.Byte(); HandleRemove(OwnerOf(from, slot), r, Session.IsHost); }
        static void OnStates(NetPlayer from, NetReader r) { byte slot = r.Byte(); HandleStates(OwnerOf(from, slot), r, Session.IsHost); }
        static void OnAction(NetPlayer from, NetReader r) { byte slot = r.Byte(); HandleAction(OwnerOf(from, slot), r, Session.IsHost); }

        static void HandleSpawn(NetPlayer owner, NetReader r, bool relay)
        {
            byte id = r.Byte();
            string name = r.String();
            Appearance look = Appearance.Read(r);
            if (owner == null || owner.IsLocal || id == 0) return;
            if (name.Length > 24) name = name.Substring(0, 24);

            RemoteBot rb;
            if (!remote.TryGetValue(Key(owner.Slot, id), out rb))
            {
                if (CountOf(owner.Slot) >= MaxBots) return;
                rb = new RemoteBot
                {
                    Owner = owner.Slot,
                    Id = id,
                    P = new NetPlayer { Slot = 250, Phase = PlayerPhase.Playing, ClockSynced = true }
                };
                remote[Key(owner.Slot, id)] = rb;
                Log.Info(owner.Name + "'s " + name + " joined.");
            }
            rb.P.Name = name + " (" + owner.Name + ")";
            bool changed = rb.P.Avatar != null && (rb.Look.Body != look.Body || rb.Look.Shirt != look.Shirt || rb.Look.Pants != look.Pants || rb.Look.Face != look.Face);
            rb.Look = look;
            rb.P.Look = look;
            if (changed) rb.P.Avatar.Rebuild(look);

            if (relay)
            {
                NetWriter w = Session.Begin(Msg.BotSpawn);
                w.Byte(owner.Slot);
                w.Byte(id);
                w.String(name);
                look.Write(w);
                Session.BroadcastInGame(true, Channel.Reliable, owner);
            }
        }

        static void HandleRemove(NetPlayer owner, NetReader r, bool relay)
        {
            byte id = r.Byte();
            if (owner == null || owner.IsLocal) return;
            RemoteBot rb;
            if (remote.TryGetValue(Key(owner.Slot, id), out rb))
            {
                DestroyAvatar(rb);
                remote.Remove(Key(owner.Slot, id));
            }
            if (relay)
            {
                NetWriter w = Session.Begin(Msg.BotRemove);
                w.Byte(owner.Slot);
                w.Byte(id);
                Session.BroadcastInGame(true, Channel.Reliable, owner);
            }
        }

        static readonly List<KeyValuePair<byte, PlayerSnapshot>> incoming = new List<KeyValuePair<byte, PlayerSnapshot>>();

        static void HandleStates(NetPlayer owner, NetReader r, bool relay)
        {
            int n = r.Byte();
            if (owner == null || owner.IsLocal) return;
            incoming.Clear();
            for (int i = 0; i < n; i++)
            {
                byte id = r.Byte();
                incoming.Add(new KeyValuePair<byte, PlayerSnapshot>(id, PlayerSnapshot.Read(r)));
            }
            bool mirrored = owner == Loopback.BotOwner;
            foreach (KeyValuePair<byte, PlayerSnapshot> kv in incoming)
            {
                RemoteBot rb;
                if (!remote.TryGetValue(Key(owner.Slot, kv.Key), out rb)) continue;
                PlayerSnapshot s = kv.Value;
                s.Time = owner.ToLocalTime(s.Time); // the bot's NetPlayer keeps local time
                if (mirrored && s.Vehicle == 0) { s.Position += Loopback.Offset; s.Head += Loopback.Offset; }
                rb.P.Last = s;
                rb.P.HasState = true;
                if (rb.P.Avatar != null) rb.P.Avatar.Push(s);
            }
            if (relay && incoming.Count > 0)
            {
                NetWriter w = Session.Begin(Msg.BotStates);
                w.Byte(owner.Slot);
                w.Byte((byte)incoming.Count);
                foreach (KeyValuePair<byte, PlayerSnapshot> kv in incoming) { w.Byte(kv.Key); kv.Value.Write(w); }
                Session.BroadcastInGame(false, Channel.State, owner);
            }
        }

        static void HandleAction(NetPlayer owner, NetReader r, bool relay)
        {
            byte id = r.Byte();
            byte kind = r.Byte();
            Vector3 at = kind == 2 ? r.Vector3() : Vector3.zero;
            if (owner == null || owner.IsLocal) return;
            Vector3 shown = owner == Loopback.BotOwner ? at + Loopback.Offset : at;
            RemoteBot rb;
            if (remote.TryGetValue(Key(owner.Slot, id), out rb) && rb.P.Avatar != null && rb.P.Avatar.Visible)
            {
                if (kind == 1) rb.P.Avatar.PlayGesture(Emotes.Wave);
                else if (kind > 10) rb.P.Avatar.PlayGesture((byte)(kind - 10));
                else if (kind == 2 && (rb.P.Avatar.HeadPosition - shown).sqrMagnitude < 9f) rb.P.Avatar.Reach(shown);
            }
            if (relay)
            {
                NetWriter w = Session.Begin(Msg.BotAction);
                w.Byte(owner.Slot);
                w.Byte(id);
                w.Byte(kind);
                if (kind == 2) w.Vector3(at);
                Session.BroadcastInGame(true, Channel.Reliable, owner);
            }
        }

        static void UpdateRemote()
        {
            if (remote.Count == 0) return;
            bool inWorld = MP.InGame && LocalPlayer.Bound;
            foreach (RemoteBot rb in remote.Values)
            {
                try
                {
                    if (!inWorld) { DestroyAvatar(rb); continue; }
                    if (rb.P.Avatar == null)
                    {
                        rb.P.Avatar = RemoteAvatar.Create(rb.P);
                        if (rb.P.HasState) rb.P.Avatar.Push(rb.P.Last);
                    }
                    rb.P.Avatar.Update();
                }
                catch (Exception e) { Log.ExceptionOnce("Remote bot " + rb.P.Name, e); }
            }
        }

        static int CountOf(byte owner)
        {
            int n = 0;
            foreach (RemoteBot rb in remote.Values) if (rb.Owner == owner) n++;
            return n;
        }

        static void DestroyAvatar(RemoteBot rb)
        {
            if (rb.P.Avatar == null) return;
            rb.P.Avatar.Destroy();
            rb.P.Avatar = null;
        }

        static void RemoveRemoteOf(byte owner)
        {
            List<int> gone = new List<int>();
            foreach (KeyValuePair<int, RemoteBot> kv in remote)
                if (kv.Value.Owner == owner) { DestroyAvatar(kv.Value); gone.Add(kv.Key); }
            foreach (int k in gone) remote.Remove(k);
        }

        static void ClearRemote()
        {
            foreach (RemoteBot rb in remote.Values) DestroyAvatar(rb);
            remote.Clear();
        }

        static string BaseName(string shown)
        {
            int i = shown.LastIndexOf(" (", StringComparison.Ordinal);
            return i > 0 ? shown.Substring(0, i) : shown;
        }
    }
}
