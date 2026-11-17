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
    /// Test bots: pretend players that only exist in your own game, for testing alone. They use
    /// the real player body, animations and network format (every update is packed and
    /// unpacked like a friend's), and take simple orders: copy me, follow, stay, wander,
    /// go somewhere, fetch an item. Each one reports the shape of its body to the log.
    /// </summary>
    internal static class Bots
    {
        public enum Order { Copy, Follow, Stay, Wander, GoTo, Fetch }

        public sealed class Place
        {
            public string Label, Path;
            public Vector3 Fallback;
            public Place(string label, string path, Vector3 fallback) { Label = label; Path = path; Fallback = fallback; }
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
            public float StuckTime;
            public Vector3 LastProgressPos;
            public int BrokenReports;
            public float ForceCrouch = -1f;   // self-test: pose override
            public readonly Queue<KeyValuePair<float, PlayerSnapshot>> Delayed = new Queue<KeyValuePair<float, PlayerSnapshot>>();
        }

        const float CopyDelay = 0.8f;
        const int MaxBots = 12;
        static readonly List<Bot> bots = new List<Bot>();
        static readonly Queue<KeyValuePair<float, Vector3>> reaches = new Queue<KeyValuePair<float, Vector3>>();
        static readonly Queue<float> waves = new Queue<float>();
        static readonly NetWriter writer = new NetWriter(128);
        static readonly NetReader reader = new NetReader();
        static float nextSample, nextReport;
        static int counter, bodyCycle;
        static readonly System.Random rng = new System.Random();

        public static IList<Bot> All { get { return bots; } }
        public static bool Any { get { return bots.Count > 0; } }

        // ================================================================ commands

        public static void Spawn()
        {
            if (!MP.InGame || !LocalPlayer.Bound) { Notifications.Show("Bots can only be spawned in the world."); return; }
            if (bots.Count >= MaxBots) { Notifications.Show("That's enough bots (" + MaxBots + ")."); return; }
            Bot b = new Bot { Number = ++counter };
            b.P = new NetPlayer
            {
                Slot = (byte)(1 + (b.Number % 7)),
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
            Log.Info(b.P.Name + " spawned (" + Catalog.Bodies[b.P.Look.Body].Label + ").");
        }

        public static void Remove(Bot b)
        {
            if (b == null) return;
            DropCarry(b, false);
            if (b.P.Avatar != null) b.P.Avatar.Destroy();
            bots.Remove(b);
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
            Log.Info(b.P.Name + " body: " + Catalog.Bodies[b.P.Look.Body].Label);
        }

        public static void Command(Bot b, Order order)
        {
            if (order != Order.Fetch) b.FetchTarget = null;
            if (order != Order.Fetch && order != Order.Follow) DropCarry(b, true);
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
            Transform t = GameUtil.FindPath(place.Path);
            Vector3 target = t != null ? t.position : place.Fallback;
            b.Order = Order.GoTo;
            b.Target = target;
            b.OrderText = (teleport ? "teleported to " : "walking to ") + place.Label;
            b.StuckTime = 0f;
            if (teleport) b.Feet = Ground(target + new Vector3(UnityEngine.Random.Range(-2f, 2f), 0f, UnityEngine.Random.Range(-2f, 2f)));
            Log.Info(b.P.Name + " " + b.OrderText);
        }

        public static void Wave(Bot b) { b.P.Avatar.PlayGesture(1); }

        /// <summary>Self-test: stand in front of the camera, facing it.</summary>
        public static void PlaceInFront(Bot b, float distance, float side)
        {
            Transform cam = LocalPlayer.View.transform;
            Vector3 fwd = cam.forward; fwd.y = 0f; fwd.Normalize();
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
            waves.Clear();
        }

        // ================================================================ per frame

        public static void Update()
        {
            if (bots.Count == 0) return;
            if (!MP.InGame || !LocalPlayer.Bound) { OnWorldLeft(); return; }
            float now = Time.realtimeSinceStartup;
            if (Options.KeyDown(Options.WaveKey) && !MultiplayerUIState.Typing) waves.Enqueue(now + CopyDelay);

            bool sample = now >= nextSample;
            PlayerSnapshot mine = default(PlayerSnapshot);
            if (sample)
            {
                nextSample = now + 0.05f;
                mine = LocalPlayer.Sample();
            }

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
                    else
                    {
                        Think(b, Time.deltaTime);
                        if (sample) Push(b, Synth(b, now));
                    }
                    b.P.Avatar.Update();
                }
                catch (Exception e) { Log.ExceptionOnce("Bot " + b.P.Name, e); }
            }

            while (reaches.Count > 0 && reaches.Peek().Key <= now)
            {
                Vector3 target = reaches.Dequeue().Value;
                foreach (Bot b in bots)
                    if (b.Order == Order.Copy) b.P.Avatar.Reach(target + (b.P.Avatar.HeadPosition - LocalPlayer.View.transform.position));
            }
            while (waves.Count > 0 && waves.Peek() <= now)
            {
                waves.Dequeue();
                foreach (Bot b in bots) if (b.Order == Order.Copy) b.P.Avatar.PlayGesture(1);
            }

            if (now >= nextReport)
            {
                nextReport = now + 5f;
                foreach (Bot b in bots)
                {
                    Log.Info(b.P.Name + " (" + b.OrderText + "): " + b.P.Avatar.Diagnostics());
                    if (b.P.Avatar.LooksBroken() && b.BrokenReports++ < 5)
                        Log.Warn(b.P.Name + " body looks wrong: " + b.P.Avatar.Diagnostics());
                }
            }
        }

        public static void LateUpdate()
        {
            for (int i = 0; i < bots.Count; i++)
            {
                Bot b = bots[i];
                if (b.P.Avatar == null) continue;
                b.P.Avatar.LateUpdate();
                if (b.Carry != null && b.Carry.Alive)
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
            b.P.Avatar.Push(PlayerSnapshot.Read(reader));
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
            s.Pitch = 0f;
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
            }
        }

        static void Fetch(Bot b, float dt, Vector3 me)
        {
            if (b.Carry != null)
            {
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
            if (b.FetchTarget == null || !b.FetchTarget.Alive || b.FetchTarget.HeldBy != 255 || b.FetchTarget.Replica)
            {
                b.FetchTarget = FindItem(b.Feet);
                if (b.FetchTarget == null)
                {
                    Notifications.Show(b.P.Name + ": nothing I can carry nearby.");
                    Command(b, Order.Follow);
                    return;
                }
            }
            Vector3 at = b.FetchTarget.Go.transform.position;
            float dist = Flat(at - b.Feet);
            b.OrderText = "going to get " + Pretty(b.FetchTarget.Name);
            if (dist > 0.9f) Walk(b, at, dist > 10f ? 4.5f : 1.6f, dt, 0.7f);
            else
            {
                NetObject o = b.FetchTarget;
                b.FetchTarget = null;
                b.Carry = o;
                b.CarryWasKinematic = o.Body.isKinematic;
                o.Body.isKinematic = true;
                o.HeldBy = b.P.Slot;
                b.P.Avatar.Reach(at);
                Log.Info(b.P.Name + " picked up " + o.Name);
            }
        }

        /// <summary>The nearest loose item we may move (owned by this game), within 40 m.</summary>
        static NetObject FindItem(Vector3 from)
        {
            NetObject best = null;
            float bestD = 40f * 40f;
            GameObject mineInHand = LocalPlayer.HeldObject;
            foreach (NetObject o in ObjectSync.All)
            {
                if (!o.Alive || !o.Go.activeInHierarchy || o.Replica || o.HeldBy != 255 || o.Go == mineInHand) continue;
                if (!ObjectSync.OwnedLocally(o) || o.Body.isKinematic || o.Body.mass > 60f) continue;
                bool taken = false;
                foreach (Bot other in bots) if (other.Carry == o || other.FetchTarget == o) taken = true;
                if (taken) continue;
                float d = (o.Go.transform.position - from).sqrMagnitude;
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

        static string Pretty(string n)
        {
            if (string.IsNullOrEmpty(n)) return "something";
            int i = n.IndexOf('(');
            return (i > 0 ? n.Substring(0, i) : n).Trim();
        }
    }
}
