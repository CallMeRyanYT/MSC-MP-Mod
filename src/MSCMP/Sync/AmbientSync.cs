using System;
using System.Collections.Generic;
using MSCMP.Net;
using UnityEngine;

namespace MSCMP.Sync
{
    /// <summary>
    /// The rest of the world that moves by itself is the host's: highway and dirt-road traffic,
    /// NPC cars, the bus, the train, the rally cars, the villagers walking around and the moose.
    /// Without this every game runs its own traffic, so a car that hits you might not even exist
    /// on your friend's screen.
    ///
    /// Only the actual moving actor roots are tracked (not static waypoints or child bones),
    /// found in the same deterministic order on every game. The host sends whatever moved ten
    /// times a second; clients smoothly interpolate them in world space. When the host's game
    /// switches an actor off because the host is far away, nothing is sent and your own game
    /// keeps running it near you.
    /// </summary>
    internal static class AmbientSync
    {
        struct AmbState
        {
            public Vector3 Pos;
            public Quaternion Rot;
        }

        sealed class Thing
        {
            public uint Id;
            public Transform T;
            public Rigidbody Body;
            public bool BodyWasKinematic;
            public bool Following;          // client: we hold it to the host's pose
            public Vector3 SentPos;
            public Quaternion SentRot = Quaternion.identity;
            public bool EverMoved;
            public readonly SnapshotBuffer<AmbState> Buffer = new SnapshotBuffer<AmbState>(10);
            public float LastHeard;
        }

        static readonly string[] VehicleAreas =
        {
            "TRAFFIC/VehiclesHighway",
            "TRAFFIC/VehiclesDirtRoad",
            "NPC_CARS",
            "TRAIN",
            "RALLY/RallyCars",
        };
        static readonly string[] SkipRigidbodies =
        {
            "ragdoll", "deadbody", "driverheadpivot", "massplayer", "deathforce", "passenger", "door"
        };

        const float SendInterval = 0.1f;
        const float InterpDelay = 0.14f;
        const float FollowTimeout = 1.0f;

        static readonly List<Thing> things = new List<Thing>();
        static readonly Dictionary<uint, Thing> byId = new Dictionary<uint, Thing>();
        static float nextSend;
        static bool ready;
        static readonly NetWriter batch = new NetWriter(1200);

        public static int Count { get { return things.Count; } }

        public static void Init()
        {
            Session.On(Msg.AmbientStates, OnStates);
            Session.Ended += r => ReleaseAll();
        }

        // ================================================================ finding them

        public static void OnWorldReady()
        {
            things.Clear();
            byId.Clear();
            List<Transform> raw = new List<Transform>();

            for (int i = 0; i < VehicleAreas.Length; i++)
            {
                string area = VehicleAreas[i];
                Transform root = area.IndexOf('/') >= 0 ? GameUtil.FindPath(area) : GameUtil.FindRoot(area);
                if (root != null) CollectVehicles(root, 0, raw);
            }

            Transform walkers = GameUtil.FindPath("HUMANS/Randomizer/Walkers");
            if (walkers != null)
            {
                foreach (Transform c in walkers)
                    if (c != null) raw.Add(c);
            }

            Transform moose = GameUtil.FindPath("AnimalsMoose/Moose");
            if (moose != null) raw.Add(moose);

            List<KeyValuePair<string, Transform>> keyed = new List<KeyValuePair<string, Transform>>(raw.Count);
            HashSet<Transform> seen = new HashSet<Transform>();
            for (int i = 0; i < raw.Count; i++)
            {
                Transform t = raw[i];
                if (t == null || !seen.Add(t)) continue;
                keyed.Add(new KeyValuePair<string, Transform>(GameUtil.PathOf(t) + "|" + GameUtil.SiblingChain(t), t));
            }
            keyed.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            Dictionary<string, int> dup = new Dictionary<string, int>();
            for (int i = 0; i < keyed.Count; i++)
            {
                Transform t = keyed[i].Value;
                string path = GameUtil.PathOf(t);
                int n;
                dup.TryGetValue(path, out n);
                dup[path] = n + 1;
                string idKey = "amb:" + path + "#" + n;
                uint id = GameUtil.Fnv1a(idKey) | 1u;
                while (byId.ContainsKey(id)) { idKey += "~"; id = GameUtil.Fnv1a(idKey) | 1u; }
                Thing th = new Thing { Id = id, T = t, Body = t.GetComponent<Rigidbody>() };
                th.SentPos = t.position;
                th.SentRot = t.rotation;
                things.Add(th);
                byId[id] = th;
            }
            ready = true;
            Log.Info("World movers shared by the host: " + things.Count + " (traffic, NPC cars, train, villagers, animals).");
        }

        static void CollectVehicles(Transform parent, int depth, List<Transform> into)
        {
            if (depth > 3) return;
            foreach (Transform c in parent)
            {
                if (c == null) continue;
                if (ObjectSync.Of(c.gameObject) != null) continue;
                if (IsDrivableVehicle(c)) continue;
                if (c.GetComponent<Rigidbody>() != null)
                {
                    if (ShouldSkipBody(c.name)) continue;
                    into.Add(c);
                    // Towed trailers under traffic vehicles (e.g. POLSA/CARAVAN) have their own Rigidbody.
                    foreach (Transform sub in c)
                    {
                        if (sub != null && sub.name.IndexOf("CARAVAN", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            sub.GetComponent<Rigidbody>() != null)
                            into.Add(sub);
                    }
                    continue; // do not descend into doors, ragdolls, triggers or wheels
                }
                CollectVehicles(c, depth + 1, into);
            }
        }

        static bool IsDrivableVehicle(Transform t)
        {
            IList<NetVehicle> all = VehicleSync.All;
            for (int i = 0; i < all.Count; i++) if (all[i].Root == t) return true;
            return false;
        }

        static bool ShouldSkipBody(string name)
        {
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < SkipRigidbodies.Length; i++)
                if (lower.IndexOf(SkipRigidbodies[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public static void OnWorldLeft() { ReleaseAll(); ready = false; things.Clear(); byId.Clear(); }

        // ================================================================ host: sending

        public static void Update()
        {
            if (!ready || !Session.Active || !Session.Connected) return;
            float now = Time.realtimeSinceStartup;
            if (Session.IsHost)
            {
                if (now < nextSend) return;
                nextSend = now + SendInterval;
                SendMoved(now, null);
            }
            else ReleaseStale(now);
        }

        static void SendMoved(float now, NetPlayer onlyTo)
        {
            bool all = onlyTo != null;
            int count = 0, countPos = 0, packets = 0;
            for (int i = 0; i < things.Count; i++)
            {
                Thing th = things[i];
                if (th.T == null || !th.T.gameObject.activeInHierarchy) continue;
                Vector3 p = th.T.position;
                Quaternion r = th.T.rotation;
                bool moved = (p - th.SentPos).sqrMagnitude > 0.0004f || Quaternion.Angle(r, th.SentRot) > 0.5f;
                if (!all && !moved) continue;
                if (all && !th.EverMoved) continue;
                if (moved) { th.SentPos = p; th.SentRot = r; th.EverMoved = true; }
                if (count == 0)
                {
                    batch.Reset();
                    batch.Byte((byte)Msg.AmbientStates);
                    batch.Float(now);
                    countPos = batch.Length;
                    batch.Byte(0);
                }
                batch.UInt(th.Id);
                batch.Vector3(p);
                if (r.w < 0f) { r.x = -r.x; r.y = -r.y; r.z = -r.z; r.w = -r.w; }
                batch.Half(r.x, 1f); batch.Half(r.y, 1f); batch.Half(r.z, 1f); batch.Half(r.w, 1f);
                count++;
                if (count == 40 || batch.Length > Protocol.MaxPacket - 40)
                {
                    Flush(countPos, count, onlyTo);
                    count = 0;
                    if (++packets >= 8 && !all) break; // bandwidth cap per tick
                }
            }
            if (count > 0) Flush(countPos, count, onlyTo);
        }

        static void Flush(int countPos, int count, NetPlayer onlyTo)
        {
            batch.Buffer[countPos] = (byte)count;
            if (onlyTo != null) { SteamNet.Send(onlyTo.SteamId, batch.Buffer, batch.Length, true, Channel.Reliable); return; }
            foreach (NetPlayer p in Session.Remotes)
            {
                if (p.Phase != PlayerPhase.Playing && p.Phase != PlayerPhase.Syncing) continue;
                SteamNet.Send(p.SteamId, batch.Buffer, batch.Length, false, Channel.State);
            }
        }

        /// <summary>Host: a player who joins gets everything that has moved so far.</summary>
        public static void SendCatchUp(NetPlayer p)
        {
            if (ready) SendMoved(Time.realtimeSinceStartup, p);
        }

        // ================================================================ client: following

        static void OnStates(NetPlayer from, NetReader r)
        {
            float sent = r.Float();
            int n = r.Byte();
            if (from == null || !from.IsHost || Session.IsHost) return;
            float t = from.ToLocalTime(sent);
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < n; i++)
            {
                uint id = r.UInt();
                Vector3 p = r.Vector3();
                Quaternion q = Normalize(new Quaternion(r.Half(1f), r.Half(1f), r.Half(1f), r.Half(1f)));
                Thing th;
                if (!ready || !byId.TryGetValue(id, out th) || th.T == null) continue;
                if (!th.Following)
                {
                    th.Following = true;
                    th.Buffer.Clear();
                    if (th.Body != null) { th.BodyWasKinematic = th.Body.isKinematic; th.Body.isKinematic = true; }
                }
                th.Buffer.Add(t, new AmbState { Pos = p, Rot = q });
                th.LastHeard = now;
            }
        }

        /// <summary>Client: place everything we follow (after the game's own movement this frame).</summary>
        public static void LateUpdate()
        {
            if (!ready || Session.IsHost || !Session.Connected) return;
            float renderTime = Time.realtimeSinceStartup - InterpDelay;
            for (int i = 0; i < things.Count; i++)
            {
                Thing th = things[i];
                if (!th.Following || th.T == null) continue;
                AmbState a, b;
                float alpha, over;
                if (!th.Buffer.Sample(renderTime, out a, out b, out alpha, out over)) continue;
                th.T.position = Vector3.Lerp(a.Pos, b.Pos, alpha);
                th.T.rotation = Quaternion.Slerp(a.Rot, b.Rot, alpha);
                if (th.Body != null && !th.Body.isKinematic) th.Body.isKinematic = true; // the game turned it back on
            }
        }

        static Quaternion Normalize(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m > 0.01f ? new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m) : Quaternion.identity;
        }

        /// <summary>No news from the host for a while (it's switched off there): our game runs it.</summary>
        static void ReleaseStale(float now)
        {
            for (int i = 0; i < things.Count; i++)
            {
                Thing th = things[i];
                if (th.Following && now - th.LastHeard > FollowTimeout) Release(th);
            }
        }

        static void Release(Thing th)
        {
            th.Following = false;
            th.Buffer.Clear();
            if (th.Body != null) { th.Body.isKinematic = th.BodyWasKinematic; if (!th.Body.isKinematic) th.Body.WakeUp(); }
        }

        static void ReleaseAll()
        {
            foreach (Thing th in things) if (th.Following && th.T != null) Release(th);
        }
    }
}
