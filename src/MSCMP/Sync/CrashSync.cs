using System;
using System.Collections.Generic;
using MSCMP.Net;
using UnityEngine;

namespace MSCMP.Sync
{
    /// <summary>
    /// Crash damage looks the same for everyone. The game dents a car by bending the shapes
    /// (meshes) of its body panels when it hits something. The game that drives a car notices
    /// hard knocks, looks which panels changed shape, and sends their new shape; everybody
    /// else bends their copy the same way. Players who join get every dent made since the
    /// world was loaded.
    /// </summary>
    internal static class CrashSync
    {
        sealed class Panel
        {
            public string Key;          // vehicle id + path inside the vehicle
            public NetVehicle Vehicle;
            public MeshFilter Filter;
            public int Hash;
            public bool Dented;         // changed since the world loaded (for late joiners)
        }

        static readonly Dictionary<string, Panel> panels = new Dictionary<string, Panel>();
        static readonly Dictionary<NetVehicle, List<Panel>> byVehicle = new Dictionary<NetVehicle, List<Panel>>();
        static readonly Dictionary<NetVehicle, Vector3> lastVelocity = new Dictionary<NetVehicle, Vector3>();
        static readonly Dictionary<NetVehicle, float> checkAt = new Dictionary<NetVehicle, float>();
        static bool ready;
        const float KnockDelta = 3.5f;     // m/s of sudden speed change that counts as a crash
        const float Range = 8f;            // vertex positions are sent as halves within +-8 m

        public static void Init()
        {
            Session.On(Msg.VehDent, OnDent);
        }

        public static void OnWorldReady()
        {
            panels.Clear();
            byVehicle.Clear();
            lastVelocity.Clear();
            checkAt.Clear();
            foreach (NetVehicle v in VehicleSync.All)
            {
                if (v.Root == null) continue;
                List<Panel> list = new List<Panel>();
                Dictionary<string, int> dup = new Dictionary<string, int>();
                foreach (MeshFilter mf in v.Root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf == null || mf.sharedMesh == null) continue;
                    if (mf.sharedMesh.vertexCount < 24 || mf.sharedMesh.vertexCount > 20000) continue;
                    string path = GameUtil.RelativePath(v.Root, mf.transform);
                    int n;
                    dup.TryGetValue(path, out n);
                    dup[path] = n + 1;
                    Panel p = new Panel { Key = v.Id + ":" + path + "#" + n, Vehicle = v, Filter = mf };
                    panels[p.Key] = p;
                    list.Add(p);
                }
                byVehicle[v] = list;
            }
            ready = true;
            // Shapes as loaded (dents from the save are already the same for everyone).
            foreach (Panel p in panels.Values) p.Hash = HashOf(p.Filter.sharedMesh);
        }

        public static void OnWorldLeft() { ready = false; panels.Clear(); byVehicle.Clear(); }

        public static void Update()
        {
            if (!ready || !Session.Active || !Session.Connected || Session.Local == null) return;
            float now = Time.realtimeSinceStartup;
            byte me = Session.Local.Slot;
            foreach (KeyValuePair<NetVehicle, List<Panel>> kv in byVehicle)
            {
                NetVehicle v = kv.Key;
                if (v.Body == null || v.Owner != me || v.Replica) { lastVelocity.Remove(v); continue; }
                Vector3 vel = v.Body.velocity;
                Vector3 before;
                if (lastVelocity.TryGetValue(v, out before) && (vel - before).magnitude > KnockDelta && !checkAt.ContainsKey(v))
                    checkAt[v] = now + 0.6f; // let the game finish bending first
                lastVelocity[v] = vel;
                float when;
                if (checkAt.TryGetValue(v, out when) && now >= when)
                {
                    checkAt.Remove(v);
                    foreach (Panel p in kv.Value) SendIfChanged(p);
                }
            }
        }

        static void SendIfChanged(Panel p)
        {
            if (p.Filter == null) return;
            Mesh m = p.Filter.sharedMesh;
            if (m == null) return;
            int h = HashOf(m);
            if (h == p.Hash) return;
            p.Hash = h;
            p.Dented = true;
            Send(p, null);
        }

        static void Send(Panel p, NetPlayer onlyTo)
        {
            Vector3[] verts = p.Filter.sharedMesh.vertices;
            NetWriter w = Session.Begin(Msg.VehDent);
            w.String(p.Key);
            w.Int(verts.Length);
            for (int i = 0; i < verts.Length; i++) { w.Half(verts[i].x, Range); w.Half(verts[i].y, Range); w.Half(verts[i].z, Range); }
            if (onlyTo != null) Session.SendTo(onlyTo, true, Channel.Bulk);
            else Session.SendToAll(true, Channel.Bulk);
        }

        static void OnDent(NetPlayer from, NetReader r)
        {
            string key = r.String();
            int n = r.Int();
            if (from == null || n < 0 || n > 20000) return;
            Vector3[] verts = new Vector3[n];
            for (int i = 0; i < n; i++) verts[i] = new Vector3(r.Half(Range), r.Half(Range), r.Half(Range));
            Panel p;
            if (!panels.TryGetValue(key, out p) || p.Filter == null) return;
            Mesh m = p.Filter.mesh; // our own copy, like the game's dents
            if (m == null || m.vertexCount != n) return;
            m.vertices = verts;
            m.RecalculateNormals();
            m.RecalculateBounds();
            p.Hash = HashOf(m);
            p.Dented = true;
            if (Session.IsHost)
            {
                NetWriter w = Session.Begin(Msg.VehDent);
                w.String(key);
                w.Int(n);
                for (int i = 0; i < n; i++) { w.Half(verts[i].x, Range); w.Half(verts[i].y, Range); w.Half(verts[i].z, Range); }
                Session.BroadcastInGame(true, Channel.Bulk, from);
            }
        }

        /// <summary>Host: a player who joins gets every dent made since the world was loaded.</summary>
        public static void SendCatchUp(NetPlayer to)
        {
            foreach (Panel p in panels.Values) if (p.Dented && p.Filter != null && p.Filter.sharedMesh != null) Send(p, to);
        }

        /// <summary>A cheap fingerprint of a shape (a sample of its vertices).</summary>
        static int HashOf(Mesh m)
        {
            Vector3[] v = m.vertices;
            int h = v.Length;
            int step = Math.Max(1, v.Length / 256);
            for (int i = 0; i < v.Length; i += step)
                h = h * 31 + Mathf.RoundToInt(v[i].x * 1000f) * 7 + Mathf.RoundToInt(v[i].y * 1000f) * 13 + Mathf.RoundToInt(v[i].z * 1000f);
            return h;
        }
    }
}
