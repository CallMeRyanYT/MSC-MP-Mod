using System;
using System.Collections.Generic;
using MSCMP.Net;
using UnityEngine;

namespace MSCMP.Sync
{
    /// <summary>
    /// The rest of the world that moves by itself is the host's: traffic, the police and other
    /// NPC cars, the bus, the train, the rally cars, the villagers walking around and the
    /// animals. Without this every game runs its own traffic, so a car that hits you might not
    /// even exist on your friend's screen.
    ///
    /// Everything under those parts of the world that moves is found the same way on every
    /// game (same scene, same order). The host sends whatever moved, ten times a second;
    /// everybody else's copy follows (cars with physics are held still and moved along). When
    /// the host's game has switched something off because the host is far away, nothing is
    /// sent and your own game keeps running it near you, so nothing vanishes in front of you.
    /// </summary>
    internal static class AmbientSync
    {
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
            // client: the two latest poses from the host
            public Vector3 FromPos, ToPos;
            public Quaternion FromRot, ToRot;
            public float ToTime, FromTime, LastHeard;
        }

        /// <summary>Parts of the world that move by themselves (top-level names, and sub-paths).</summary>
        static readonly string[] Areas = { "TRAFFIC", "NPC_CARS", "TRAIN", "RALLY", "HUMANS/Randomizer" };
        static readonly string[] Skip = { "wheel", "tire", "tyre", "rim", "steering", "waypoint", "path", "spawn", "trigger", "light", "sound", "audio", "particle", "coll" };
        const int MaxDepth = 4;
        const float SendInterval = 0.1f;
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
            List<KeyValuePair<string, Transform>> found = new List<KeyValuePair<string, Transform>>();
            foreach (string area in Areas)
            {
                Transform root = area.IndexOf('/') >= 0 ? GameUtil.FindPath(area) : GameUtil.FindRoot(area);
                if (root == null) continue;
                Collect(root, area, 0, found);
            }
            // Animals live in several places; find them by name.
            foreach (Transform t in Resources.FindObjectsOfTypeAll(typeof(Transform)))
            {
                if (t == null || !GameUtil.IsSceneObject(t.gameObject)) continue;
                string n = t.name;
                if (n.StartsWith("AnimalsMoose", StringComparison.Ordinal) || n == "Moose" || n == "Birds")
                    foreach (Transform c in t) found.Add(new KeyValuePair<string, Transform>(GameUtil.PathOf(c), c));
            }
            found.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            Dictionary<string, int> dup = new Dictionary<string, int>();
            HashSet<Transform> seen = new HashSet<Transform>();
            foreach (KeyValuePair<string, Transform> kv in found)
            {
                if (!seen.Add(kv.Value)) continue;
                int n;
                dup.TryGetValue(kv.Key, out n);
                dup[kv.Key] = n + 1;
                uint id = GameUtil.Fnv1a("amb:" + kv.Key + "#" + n) | 1u;
                if (byId.ContainsKey(id)) continue;
                Thing th = new Thing { Id = id, T = kv.Value, Body = kv.Value.GetComponent<Rigidbody>() };
                th.SentPos = th.T.localPosition;
                th.SentRot = th.T.localRotation;
                things.Add(th);
                byId[id] = th;
            }
            ready = true;
            Log.Info("World movers shared by the host: " + things.Count + " (traffic, NPC cars, train, villagers, animals).");
        }

        static void Collect(Transform t, string path, int depth, List<KeyValuePair<string, Transform>> into)
        {
            if (depth > 0)
            {
                string lower = t.name.ToLowerInvariant();
                for (int i = 0; i < Skip.Length; i++) if (lower.IndexOf(Skip[i], StringComparison.Ordinal) >= 0) return;
                if (ObjectSync.Of(t.gameObject) != null) return;              // shared items have their own sync
                foreach (NetVehicle v in VehicleSync.All) if (v.Root == t) return; // so do drivable vehicles
                if (t.GetComponent<SkinnedMeshRenderer>() != null) return;     // bodies animate by themselves
                into.Add(new KeyValuePair<string, Transform>(path, t));
            }
            if (depth >= MaxDepth) return;
            foreach (Transform c in t) Collect(c, path + "/" + c.name, depth + 1, into);
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
                Vector3 p = th.T.localPosition;
                Quaternion r = th.T.localRotation;
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
            if (from == null || !from.IsHost || Session.IsHost) { return; }
            float t = from.ToLocalTime(sent);
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < n; i++)
            {
                uint id = r.UInt();
                Vector3 p = r.Vector3();
                Quaternion q = new Quaternion(r.Half(1f), r.Half(1f), r.Half(1f), r.Half(1f));
                Thing th;
                if (!ready || !byId.TryGetValue(id, out th) || th.T == null) continue;
                if (!th.Following)
                {
                    th.Following = true;
                    th.FromPos = p; th.FromRot = q; th.FromTime = t - SendInterval;
                    th.ToPos = p; th.ToRot = q; th.ToTime = t;
                    if (th.Body != null) { th.BodyWasKinematic = th.Body.isKinematic; th.Body.isKinematic = true; }
                }
                else if (t > th.ToTime)
                {
                    th.FromPos = th.ToPos; th.FromRot = th.ToRot; th.FromTime = th.ToTime;
                    th.ToPos = p; th.ToRot = q; th.ToTime = t;
                }
                th.LastHeard = now;
            }
        }

        /// <summary>Client: place everything we follow (after the game's own movement this frame).</summary>
        public static void LateUpdate()
        {
            if (!ready || Session.IsHost || !Session.Connected) return;
            float renderTime = Time.realtimeSinceStartup - 0.12f;
            for (int i = 0; i < things.Count; i++)
            {
                Thing th = things[i];
                if (!th.Following || th.T == null) continue;
                float span = th.ToTime - th.FromTime;
                float a = span > 0.0001f ? Mathf.Clamp01((renderTime - th.FromTime) / span) : 1f;
                Vector3 p = Vector3.Lerp(th.FromPos, th.ToPos, a);
                Quaternion q = Quaternion.Slerp(Normalize(th.FromRot), Normalize(th.ToRot), a);
                th.T.localPosition = p;
                th.T.localRotation = q;
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
            if (th.Body != null) { th.Body.isKinematic = th.BodyWasKinematic; if (!th.Body.isKinematic) th.Body.WakeUp(); }
        }

        static void ReleaseAll()
        {
            foreach (Thing th in things) if (th.Following && th.T != null) Release(th);
        }
    }
}
