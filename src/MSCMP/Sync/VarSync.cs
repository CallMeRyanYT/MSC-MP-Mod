using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCMP.Net;
using UnityEngine;

namespace MSCMP.Sync
{
    /// <summary>
    /// Keeps the game's world values the same for everyone: fuel, oil, coolant, brake and clutch
    /// fluid, water, battery charge, wear, damage, dirt and paint colour, stored in the state
    /// machines of cars, parts and canisters.
    ///
    /// Each value has one owner: whoever drives the car or holds the object it belongs to
    /// (otherwise the host). The owner's value wins. Anybody can still *add* to a value (pour
    /// fuel from a canister into a car somebody else is driving); additions are sent as
    /// amounts and applied by the owner. Player needs (hunger, thirst...) are global
    /// variables, not part of this, and stay separate for each player.
    /// </summary>
    internal static class VarSync
    {
        sealed class Var
        {
            public uint Id;
            public PlayMakerFSM Fsm;
            public FsmFloat F;
            public FsmColor C;
            public float Last;
            public Color LastColor;
            public float NextSend;
            public string Label;
        }

        static readonly Dictionary<uint, Var> byId = new Dictionary<uint, Var>();
        static readonly List<Var> vars = new List<Var>();
        static float nextTick;
        static readonly List<byte[]> outgoing = new List<byte[]>();          // to everyone (host) / to the host (client)
        static readonly List<KeyValuePair<NetPlayer, byte[]>> direct = new List<KeyValuePair<NetPlayer, byte[]>>();
        static bool ready;

        const float Tick = 0.25f;
        const float MinInterval = 0.5f;      // per value
        const float Epsilon = 0.005f;

        static readonly string[] FloatWords =
        {
            "fuel", "oil", "coolant", "fluid", "water", "liquid", "brake", "clutch", "battery", "charge",
            "wear", "damage", "dirt", "dirty", "level", "amount", "beer", "milk", "juice", "booze", "gas", "petrol", "diesel"
        };
        static readonly string[] ColorWords = { "paint", "color", "colour" };
        static readonly string[] ExcludedRoots = { "PLAYER", "GUI", "Systems", "Sheets", "HUMANS", "TRAFFIC", "NPC_CARS", "MSCMP", "MAP" };

        public static int Count { get { return vars.Count; } }

        public static void Init()
        {
            Session.On(Msg.VarValue, OnValue);
            Session.On(Msg.VarAdd, OnAdd);
            ObjectSync.Registered += o => { if (ready && o.Dynamic) { try { ScanObject(o); } catch (Exception e) { Log.ExceptionOnce("VarSync object", e); } } };
        }

        public static void OnWorldReady()
        {
            Clear();
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(PlayMakerFSM));
            List<KeyValuePair<string, PlayMakerFSM>> keyed = new List<KeyValuePair<string, PlayMakerFSM>>();
            for (int i = 0; i < all.Length; i++)
            {
                PlayMakerFSM pm = all[i] as PlayMakerFSM;
                if (pm == null || !GameUtil.IsSceneObject(pm.gameObject)) continue;
                string path = GameUtil.PathOf(pm.transform);
                if (Excluded(path)) continue;
                keyed.Add(new KeyValuePair<string, PlayMakerFSM>(path + "|" + pm.FsmName + "|" + GameUtil.SiblingChain(pm.transform), pm));
            }
            keyed.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            Dictionary<string, int> dup = new Dictionary<string, int>();
            foreach (KeyValuePair<string, PlayMakerFSM> kv in keyed)
            {
                PlayMakerFSM pm = kv.Value;
                string key = GameUtil.PathOf(pm.transform) + "|" + pm.FsmName;
                int n;
                dup.TryGetValue(key, out n);
                dup[key] = n + 1;
                AddFsm(pm, "var:" + key + "#" + n, false);
            }
            ready = true;
            Log.Info("World values shared: " + vars.Count + " (fuel, fluids, charge, wear, paint...)");
        }

        static void ScanObject(NetObject o)
        {
            if (!o.Alive) return;
            PlayMakerFSM[] list = o.Go.GetComponentsInChildren<PlayMakerFSM>(true);
            Dictionary<string, int> dup = new Dictionary<string, int>();
            foreach (PlayMakerFSM pm in list)
            {
                string key = GameUtil.RelativePath(o.Go.transform, pm.transform) + "|" + pm.FsmName;
                int n;
                dup.TryGetValue(key, out n);
                dup[key] = n + 1;
                AddFsm(pm, "objvar:" + o.Id + ":" + key + "#" + n, true);
            }
        }

        static bool Excluded(string path)
        {
            int slash = path.IndexOf('/');
            string root = slash < 0 ? path : path.Substring(0, slash);
            for (int i = 0; i < ExcludedRoots.Length; i++) if (root.StartsWith(ExcludedRoots[i], StringComparison.Ordinal)) return true;
            return path.IndexOf("SAVEGAME", StringComparison.Ordinal) >= 0;
        }

        static void AddFsm(PlayMakerFSM pm, string key, bool dynamicObject)
        {
            if (pm.UsesTemplate || pm.Fsm == null) return;
            FsmVariables v;
            try
            {
                if (!pm.Fsm.Initialized) pm.Fsm.InitData();
                v = pm.FsmVariables;
            }
            catch { return; }
            if (v == null) return;
            FsmFloat[] floats = v.FloatVariables;
            if (floats != null)
                for (int i = 0; i < floats.Length; i++)
                    if (floats[i] != null && Matches(floats[i].Name, FloatWords))
                        Add(pm, key + "|f|" + floats[i].Name, floats[i], null, dynamicObject);
            FsmColor[] colors = v.ColorVariables;
            if (colors != null)
                for (int i = 0; i < colors.Length; i++)
                    if (colors[i] != null && Matches(colors[i].Name, ColorWords))
                        Add(pm, key + "|c|" + colors[i].Name, null, colors[i], dynamicObject);
        }

        static bool Matches(string name, string[] words)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            for (int i = 0; i < words.Length; i++) if (n.IndexOf(words[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static void Add(PlayMakerFSM pm, string key, FsmFloat f, FsmColor c, bool dynamicObject)
        {
            uint id = GameUtil.Fnv1a(key) & 0x7FFFFFFFu;
            if (dynamicObject) id |= 0x80000000u;
            while (id == 0 || byId.ContainsKey(id)) { key += "~"; id = (GameUtil.Fnv1a(key) & 0x7FFFFFFFu) | (dynamicObject ? 0x80000000u : 0u); }
            Var x = new Var { Id = id, Fsm = pm, F = f, C = c, Label = pm.name + "." + (f != null ? f.Name : c.Name) };
            if (f != null) x.Last = f.Value; else x.LastColor = c.Value;
            byId[id] = x;
            vars.Add(x);
        }

        public static void OnWorldLeft() { Clear(); }

        static void Clear()
        {
            ready = false;
            byId.Clear();
            vars.Clear();
        }

        // ================================================================ ownership

        /// <summary>Slot that decides this value: the driver/simulator of the car it is in,
        /// the player holding the object it is on, or the host.</summary>
        static byte OwnerOf(Var x)
        {
            Transform t = x.Fsm != null ? x.Fsm.transform : null;
            if (t == null) return 0;
            for (Transform c = t; c != null; c = c.parent)
            {
                NetObject o = ObjectSync.Of(c.gameObject);
                if (o != null) return o.Owner;
            }
            Transform root = t.root;
            foreach (NetVehicle v in VehicleSync.All)
                if (v.Root == root) return v.Owner;
            return 0;
        }

        // ================================================================ per frame

        public static void Update()
        {
            if (!ready || !Session.Active || !Session.Connected || Session.Local == null) return;
            if (Game.GameSession.CatchingUp) { Absorb(); return; }
            float now = Time.realtimeSinceStartup;
            if (now < nextTick) return;
            nextTick = now + Tick;
            byte me = Session.Local.Slot;

            for (int i = vars.Count - 1; i >= 0; i--)
            {
                Var x = vars[i];
                if (x.Fsm == null) { byId.Remove(x.Id); vars.RemoveAt(i); continue; }
                if (now < x.NextSend) continue;
                bool mine = OwnerOf(x) == me;
                if (x.F != null)
                {
                    float v = x.F.Value;
                    float diff = v - x.Last;
                    if (Mathf.Abs(diff) < Epsilon || float.IsNaN(v)) continue;
                    if (mine) SendValue(x, v, null);
                    else if (diff > 0f) SendAdd(x, diff);   // we poured something in
                    x.Last = v;                              // our own decreases are not news
                    x.NextSend = now + MinInterval;
                }
                else if (x.C != null)
                {
                    Color c = x.C.Value;
                    if (!mine || Same(c, x.LastColor)) continue;
                    SendValue(x, 0f, c);
                    x.LastColor = c;
                    x.NextSend = now + MinInterval;
                }
            }
            Flush();
        }

        /// <summary>All changes of one tick travel together in one packet per player.</summary>
        static void Flush()
        {
            if (outgoing.Count > 0)
            {
                if (Session.IsHost)
                {
                    foreach (NetPlayer p in Session.Remotes)
                    {
                        if (p.Phase != PlayerPhase.Playing && p.Phase != PlayerPhase.Syncing) continue;
                        BatchSender b = new BatchSender(p);
                        foreach (byte[] m in outgoing) b.Add(m, 0, m.Length);
                        b.Flush();
                    }
                }
                else
                {
                    NetPlayer host = Session.BySlot(0);
                    if (host != null)
                    {
                        BatchSender b = new BatchSender(host);
                        foreach (byte[] m in outgoing) b.Add(m, 0, m.Length);
                        b.Flush();
                    }
                }
                outgoing.Clear();
            }
            foreach (KeyValuePair<NetPlayer, byte[]> kv in direct)
            {
                BatchSender b = new BatchSender(kv.Key);
                b.Add(kv.Value, 0, kv.Value.Length);
                b.Flush();
            }
            direct.Clear();
        }

        /// <summary>While replaying history, everything that changes is old news.</summary>
        static void Absorb()
        {
            foreach (Var x in vars)
            {
                if (x.Fsm == null) continue;
                if (x.F != null) x.Last = x.F.Value; else x.LastColor = x.C.Value;
            }
        }

        static bool Same(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.004f && Mathf.Abs(a.g - b.g) < 0.004f && Mathf.Abs(a.b - b.b) < 0.004f && Mathf.Abs(a.a - b.a) < 0.004f;
        }

        static void WriteValue(NetWriter w, Var x, float v, Color? c)
        {
            w.UInt(x.Id);
            w.Bool(c.HasValue);
            if (c.HasValue) { Color k = c.Value; w.Float(k.r); w.Float(k.g); w.Float(k.b); w.Float(k.a); }
            else w.Float(v);
        }

        static void SendValue(Var x, float v, Color? c)
        {
            NetWriter w = Session.Begin(Msg.VarValue);
            WriteValue(w, x, v, c);
            outgoing.Add(w.ToArray());
        }

        static void SendAdd(Var x, float amount)
        {
            NetWriter w = Session.Begin(Msg.VarAdd);
            w.UInt(x.Id);
            w.Float(amount);
            if (Session.IsHost)
            {
                // The owner is a client: tell them; they will report the new total.
                NetPlayer owner = Session.BySlot(OwnerOf(x));
                if (owner != null && !owner.IsLocal) direct.Add(new KeyValuePair<NetPlayer, byte[]>(owner, w.ToArray()));
            }
            else outgoing.Add(w.ToArray());
        }

        // ================================================================ messages

        static void OnValue(NetPlayer from, NetReader r)
        {
            uint id = r.UInt();
            bool isColor = r.Bool();
            Color c = default(Color);
            float v = 0f;
            if (isColor) { c.r = r.Float(); c.g = r.Float(); c.b = r.Float(); c.a = r.Float(); }
            else v = r.Float();
            if (from == null) return;
            Var x;
            if (!byId.TryGetValue(id, out x) || x.Fsm == null) return;
            if (Session.IsHost)
            {
                if (from.IsLocal || OwnerOf(x) != from.Slot) return; // only the owner decides
                NetWriter w = Session.Begin(Msg.VarValue);
                WriteValue(w, x, v, isColor ? (Color?)c : null);
                Session.BroadcastInGame(true, Channel.Reliable, from);
            }
            if (isColor) { if (x.C != null) { x.C.Value = c; x.LastColor = c; } }
            else if (x.F != null && !float.IsNaN(v)) { x.F.Value = v; x.Last = v; }
        }

        static void OnAdd(NetPlayer from, NetReader r)
        {
            uint id = r.UInt();
            float amount = r.Float();
            if (from == null || from.IsLocal) return;
            Var x;
            if (!byId.TryGetValue(id, out x) || x.Fsm == null || x.F == null) return;
            if (float.IsNaN(amount) || amount <= 0f || amount > 10000f) return;
            byte owner = OwnerOf(x);
            if (Session.IsHost && owner != 0)
            {
                // Pass it to the client who owns the value.
                NetPlayer o = Session.BySlot(owner);
                if (o != null && o != from)
                {
                    NetWriter w = Session.Begin(Msg.VarAdd);
                    w.UInt(id);
                    w.Float(amount);
                    Session.SendTo(o, true, Channel.Reliable);
                    return;
                }
            }
            x.F.Value = x.F.Value + amount; // our Update reports the new total to everyone
        }

        public static void SendCatchUp(NetPlayer p)
        {
            BatchSender batch = new BatchSender(p);
            foreach (Var x in vars)
            {
                if (x.Fsm == null) continue;
                NetWriter w = Session.Begin(Msg.VarValue);
                if (x.F != null) WriteValue(w, x, x.F.Value, null);
                else WriteValue(w, x, 0f, x.C.Value);
                batch.AddCurrent(w);
            }
            batch.Flush();
        }
    }
}
