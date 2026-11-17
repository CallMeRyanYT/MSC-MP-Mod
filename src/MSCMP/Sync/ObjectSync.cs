using System;
using System.Collections.Generic;
using MSCMP.Game;
using MSCMP.Net;
using UnityEngine;

namespace MSCMP.Sync
{
    internal struct ObjState
    {
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Vel;
        public Vector3 AngVel;
    }

    /// <summary>A physics object (item, loose car part, tool...) shared between players.</summary>
    internal sealed class NetObject
    {
        public uint Id;
        public GameObject Go;
        public Rigidbody Body;
        public string Name;
        public byte Owner;                 // slot that simulates it; 0 = host
        public bool OriginalKinematic;     // what the game wants when we are not overriding
        public bool Replica;               // we forced it kinematic to follow the network
        public bool PendingOwnership;      // client asked the host for it
        public readonly SnapshotBuffer<ObjState> Buffer = new SnapshotBuffer<ObjState>(10);
        public Vector3 SentPos;
        public Quaternion SentRot = Quaternion.identity;
        public float LastChange;           // when it last moved (owner side)
        public bool RestSent = true;
        public bool Dynamic;               // created after the save was loaded
        public byte HeldBy = 255;          // slot holding it in hand

        public bool Alive { get { return Go != null && Body != null; } }
    }

    /// <summary>
    /// Shares every pickable physics object. The host is the referee: it decides who
    /// "owns" (simulates) each object. Whoever holds or throws an object owns it, so it
    /// feels instant for them; everybody else sees a smooth copy. Untouched objects are
    /// simulated by the host only, which keeps every game in the same state.
    /// </summary>
    internal static class ObjectSync
    {
        static readonly Dictionary<uint, NetObject> byId = new Dictionary<uint, NetObject>();
        static readonly Dictionary<int, NetObject> byInstance = new Dictionary<int, NetObject>();
        static readonly List<NetObject> all = new List<NetObject>();
        static uint nextDynamicId = 1;
        static float nextSend, nextScan, nextCargoCheck;
        static NetObject localHeld;
        static readonly NetWriter batch = new NetWriter(1200);

        // Client-side objects that appeared locally and are waiting to be matched to a host id.
        sealed class LocalSpawn { public GameObject Go; public float FirstSeen; public uint Token; public bool Requested; }
        static readonly Dictionary<int, LocalSpawn> localSpawns = new Dictionary<int, LocalSpawn>();
        static uint nextToken = 1;
        // Host side: new objects wait a moment before being shared, so world-event sync
        // (FsmSync) can link objects that every game creates at the same time.
        static readonly Dictionary<int, float> hostSeen = new Dictionary<int, float>();

        /// <summary>Raised whenever an object joins the shared registry.</summary>
        public static event Action<NetObject> Registered;

        static readonly string[] ExcludedRoots = { "PLAYER", "TRAFFIC", "NPC_CARS", "HUMANS", "Systems", "GUI", "RALLY", "DRAGRACE", "Sheets", "TRAIN", "MSCMP", "MSCMP_Player" };
        const float SendRate = 0.08f;          // ~12 Hz per object while moving
        const float MoveEpsilon = 0.004f;
        const float AngleEpsilon = 0.6f;
        const int ParentTagRagdoll = 0;

        public static int Count { get { return all.Count; } }

        public static void Init()
        {
            Session.On(Msg.ObjOwnRequest, OnOwnRequest);
            Session.On(Msg.ObjOwner, OnOwner);
            Session.On(Msg.ObjOwnDeny, OnOwnDeny);
            Session.On(Msg.ObjRelease, OnRelease);
            Session.On(Msg.ObjStates, OnStates);
            Session.On(Msg.ObjRest, OnRest);
            Session.On(Msg.ObjSpawn, OnSpawn);
            Session.On(Msg.ObjSpawnRequest, OnSpawnRequest);
            Session.On(Msg.ObjDestroy, OnDestroy);
            Session.On(Msg.Manifest, OnManifest);
            Session.PlayerLeft += OnPlayerLeft;
            Session.Started += () => { if (MP.InGame) ApplyRoleToAll(); };
            Session.Ended += reason => { if (MP.InGame) ReleaseAllReplicas(); };
        }

        // ================================================================ registry

        public static NetObject ById(uint id)
        {
            NetObject o;
            return byId.TryGetValue(id, out o) ? o : null;
        }

        public static NetObject Of(GameObject go)
        {
            if (go == null) return null;
            NetObject o;
            return byInstance.TryGetValue(go.GetInstanceID(), out o) ? o : null;
        }

        public static uint IdOf(GameObject go)
        {
            NetObject o = Of(go);
            return o != null ? o.Id : 0u;
        }

        public static IEnumerable<NetObject> All { get { return all; } }

        public static void OnWorldReady()
        {
            Clear();
            nextDynamicId = 1;
            List<Rigidbody> candidates = new List<Rigidbody>();
            UnityEngine.Object[] bodies = Resources.FindObjectsOfTypeAll(typeof(Rigidbody));
            for (int i = 0; i < bodies.Length; i++)
            {
                Rigidbody rb = bodies[i] as Rigidbody;
                if (rb != null && IsCandidate(rb)) candidates.Add(rb);
            }

            // Deterministic keys: full path + order among identical paths (by sibling order).
            List<KeyValuePair<string, Rigidbody>> keyed = new List<KeyValuePair<string, Rigidbody>>();
            for (int i = 0; i < candidates.Count; i++)
                keyed.Add(new KeyValuePair<string, Rigidbody>(GameUtil.PathOf(candidates[i].transform) + "|" + SiblingChain(candidates[i].transform), candidates[i]));
            keyed.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            Dictionary<string, int> dup = new Dictionary<string, int>();
            HashSet<uint> used = new HashSet<uint>();
            for (int i = 0; i < keyed.Count; i++)
            {
                Rigidbody rb = keyed[i].Value;
                string path = GameUtil.PathOf(rb.transform);
                int n;
                dup.TryGetValue(path, out n);
                dup[path] = n + 1;
                string key = path + "#" + n;
                uint id = StaticId(key);
                while (used.Contains(id)) { key += "~"; id = StaticId(key); }
                used.Add(id);
                Register(id, rb, false);
            }
            Log.Info("Registered " + all.Count + " shared physics objects.");
            ApplyRoleToAll();
        }

        public static void OnWorldLeft()
        {
            Clear();
        }

        static void Clear()
        {
            byId.Clear();
            byInstance.Clear();
            all.Clear();
            localSpawns.Clear();
            hostSeen.Clear();
            localHeld = null;
            WorldLog.Clear();
        }

        static uint StaticId(string key)
        {
            uint h = GameUtil.Fnv1a(key) & 0x7FFFFFFFu;
            return h == 0 ? 1u : h;
        }

        static string SiblingChain(Transform t)
        {
            return GameUtil.SiblingChain(t);
        }

        static bool IsCandidate(Rigidbody rb)
        {
            return IsCandidate(rb, false);
        }

        /// <summary>allowInHand: also accept an item the local player holds (the game parents held
        /// things to the player's hand), used when linking objects a world event just created.</summary>
        static bool IsCandidate(Rigidbody rb, bool allowInHand)
        {
            GameObject go = rb.gameObject;
            if (!GameUtil.IsSceneObject(go)) return false;
            string root = rb.transform.root.name;
            bool inHand = false;
            if (allowInHand && root == "PLAYER")
            {
                HutongGames.PlayMaker.FsmGameObject pivot = GameUtil.GlobalGameObject("ItemPivot");
                inHand = pivot != null && pivot.Value != null && rb.transform.IsChildOf(pivot.Value.transform);
            }
            if (!inHand)
                for (int i = 0; i < ExcludedRoots.Length; i++) if (root == ExcludedRoots[i]) return false;
            if (VehicleSync.IsVehiclePart(rb)) return false;
            string tag;
            try { tag = go.tag; } catch { tag = ""; }
            if (tag == "RAGDOLL" || go.name.IndexOf("RagDoll", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            bool tagged = tag == "PART" || tag == "ITEM" || tag == "TOOL";
            bool layer = go.layer == 19 || go.layer == 21; // Parts, DynamicBodies
            if (!tagged && !layer) return false;
            if (rb.GetComponent<Joint>() != null && !tagged) return false; // hinged world objects (doors...)
            return true;
        }

        static NetObject Register(uint id, Rigidbody rb, bool dynamic)
        {
            NetObject o = new NetObject();
            o.Id = id;
            o.Go = rb.gameObject;
            o.Body = rb;
            o.Name = rb.gameObject.name;
            o.OriginalKinematic = rb.isKinematic;
            o.Dynamic = dynamic;
            o.SentPos = rb.transform.position;
            o.SentRot = rb.transform.rotation;
            byId[id] = o;
            int inst = rb.gameObject.GetInstanceID();
            byInstance[inst] = o;
            localSpawns.Remove(inst);
            hostSeen.Remove(inst);
            all.Add(o);
            if (Registered != null)
            {
                try { Registered(o); }
                catch (Exception e) { Log.ExceptionOnce("ObjectSync.Registered", e); }
            }
            return o;
        }

        /// <summary>True if this object would be shared (pickable physics object in the world).</summary>
        public static bool IsShareable(GameObject go)
        {
            if (go == null) return false;
            Rigidbody rb = go.GetComponent<Rigidbody>();
            return rb != null && IsCandidate(rb);
        }

        /// <summary>Shares an object that every game created by the same world event, under an id
        /// all games derive the same way. Returns null if it cannot be shared.</summary>
        public static NetObject Link(GameObject go, uint id, byte owner)
        {
            if (go == null || id == 0) return null;
            NetObject existing = Of(go);
            if (existing != null) return existing.Id == id ? existing : null;
            if (byId.ContainsKey(id))
            {
                Log.Warn("Object id clash for " + go.name + " (" + id + ") - it will not be shared.");
                return null;
            }
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null || !IsCandidate(rb, true)) return null;
            NetObject o = Register(id, rb, true);
            o.Owner = owner;
            ApplyRole(o);
            Log.Debug("Linked " + o.Name + " as " + id + " (owner " + owner + ")");
            return o;
        }

        /// <summary>Fallback for a linked object that our own copy of the event did not create.</summary>
        public static NetObject LinkOrCreate(uint id, string name, Vector3 pos, Quaternion rot, byte owner)
        {
            NetObject o = ById(id);
            if (o != null) return o;
            GameObject go = FindUnregisteredNear(name, pos, 2.5f);
            if (go == null) go = CreateFromTemplate(name, pos, rot);
            if (go == null)
            {
                Log.Warn("Could not create shared object '" + name + "' - it will be missing for you.");
                return null;
            }
            o = Link(go, id, owner);
            if (o != null && !OwnedLocally(o))
            {
                go.transform.position = pos;
                go.transform.rotation = rot;
            }
            return o;
        }

        static void Unregister(NetObject o)
        {
            byId.Remove(o.Id);
            if (o.Go != null) byInstance.Remove(o.Go.GetInstanceID());
            all.Remove(o);
            if (localHeld == o) localHeld = null;
        }

        public static bool OwnedLocally(NetObject o)
        {
            if (!Session.Active) return true;
            return Session.Local != null && o.Owner == Session.Local.Slot;
        }

        /// <summary>Makes every object follow the network or the local physics, depending on ownership.</summary>
        static void ApplyRoleToAll()
        {
            for (int i = 0; i < all.Count; i++) ApplyRole(all[i]);
        }

        static void ApplyRole(NetObject o)
        {
            if (!o.Alive) return;
            bool replica = Session.Active && Session.Connected && !OwnedLocally(o);
            // Never freeze something that is in our own hand: a frozen (kinematic) object held by
            // the game's hand joint passes through walls. It becomes a copy once it is dropped.
            if (replica && LocalPlayer.HeldObject == o.Go) replica = false;
            if (replica && !o.Replica)
            {
                o.OriginalKinematic = o.Body.isKinematic;
                o.Body.isKinematic = true;
                o.Replica = true;
                o.Buffer.Clear();
            }
            else if (!replica && o.Replica)
            {
                o.Body.isKinematic = o.OriginalKinematic;
                o.Replica = false;
                o.RestSent = true;
                o.SentPos = o.Go.transform.position;
                o.SentRot = o.Go.transform.rotation;
                if (!o.Body.isKinematic) o.Body.WakeUp();
            }
        }

        static void ReleaseAllReplicas()
        {
            for (int i = 0; i < all.Count; i++)
            {
                NetObject o = all[i];
                if (o.Alive && o.Replica)
                {
                    o.Body.isKinematic = o.OriginalKinematic;
                    o.Replica = false;
                }
                o.Owner = 0;
                o.PendingOwnership = false;
            }
        }

        // ================================================================ per frame

        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (!Session.Active)
            {
                // Playing alone: keep track of what changes, so hosting later shares it all.
                if (!MP.InGame) return;
                CheckDestroyed();
                if (now >= nextScan)
                {
                    nextScan = now + 1.5f;
                    ScanForNewObjects();
                }
                return;
            }
            if (!Session.Connected) return;

            CheckLocalHold();
            CheckDestroyed();

            if (now >= nextScan)
            {
                nextScan = now + 1.5f;
                ScanForNewObjects();
            }
            if (Session.IsHost && now >= nextCargoCheck)
            {
                nextCargoCheck = now + 1f;
                AssignCargoToDrivers();
            }
            if (now >= nextSend)
            {
                nextSend = now + SendRate;
                SendOwnedStates();
                if (Session.IsClient)
                    for (int i = 0; i < all.Count; i++) CheckRelease(all[i]);
            }
        }

        public static void FixedUpdate()
        {
            if (!Session.Active || !Session.Connected) return;
            float renderTime = Time.realtimeSinceStartup - 0.1f;
            for (int i = 0; i < all.Count; i++)
            {
                NetObject o = all[i];
                if (!o.Replica || !o.Alive) continue;
                if (o == localHeld) continue; // picked up this instant; ownership is being sorted out
                if (!o.Body.isKinematic) o.Body.isKinematic = true; // the game turned it back on
                if (o.Buffer.Count == 0) continue;
                ObjState a, b;
                float alpha, over;
                if (!o.Buffer.Sample(renderTime, out a, out b, out alpha, out over)) continue;
                if (over > 1.5f) continue; // resting; already placed
                Vector3 pos = Vector3.Lerp(a.Pos, b.Pos, alpha);
                Quaternion rot = Quaternion.Slerp(a.Rot, b.Rot, alpha);
                if (over > 0f && over < 0.3f) pos += b.Vel * over;
                if ((o.Go.transform.position - pos).sqrMagnitude > 25f)
                {
                    o.Go.transform.position = pos;
                    o.Go.transform.rotation = rot;
                }
                else
                {
                    o.Body.MovePosition(pos);
                    o.Body.MoveRotation(rot);
                }
            }
        }

        /// <summary>Picking something up asks the host for ownership (instantly, optimistically).</summary>
        static void CheckLocalHold()
        {
            GameObject held = LocalPlayer.HeldObject;
            NetObject o = held != null ? Of(held) : null;
            if (o == localHeld) return;

            NetObject previous = localHeld;
            if (previous != null) previous.HeldBy = 255;
            localHeld = o;
            // Something we were refused (or lost) while holding it becomes a copy now that it's dropped.
            if (previous != null && previous.Alive && !OwnedLocally(previous)) ApplyRole(previous);
            if (o == null) return;
            o.HeldBy = Session.Local.Slot;

            if (OwnedLocally(o)) return;
            if (Session.IsHost)
            {
                // The host may only take objects no client is holding.
                NetPlayer holder = Session.BySlot(o.Owner);
                if (holder != null && !holder.IsLocal && holder.Last.Held == o.Id)
                {
                    Notifications.Show(holder.Name + " is holding that.");
                    LocalPlayer.ForceDrop();
                    localHeld = null;
                    return;
                }
                SetOwner(o, 0, true);
            }
            else
            {
                // Simulate it right away so the pickup feels instant; the host confirms or refuses.
                o.PendingOwnership = true;
                o.Owner = Session.Local.Slot;
                ApplyRole(o);
                NetWriter w = Session.Begin(Msg.ObjOwnRequest);
                w.UInt(o.Id);
                Session.SendToHost(true, Channel.Reliable);
            }
        }

        static void SendOwnedStates()
        {
            float now = Time.realtimeSinceStartup;
            batch.Reset();
            batch.Byte((byte)Msg.ObjStates);
            batch.Float(now);
            int countPos = batch.Length;
            batch.Byte(0);
            int count = 0;
            int packets = 0;

            for (int i = 0; i < all.Count; i++)
            {
                NetObject o = all[i];
                if (!o.Alive || o.Replica || !OwnedLocally(o)) continue;
                if (!o.Go.activeInHierarchy) continue;
                Transform t = o.Go.transform;
                Vector3 pos = t.position;
                Quaternion rot = t.rotation;
                bool moved = (pos - o.SentPos).sqrMagnitude > MoveEpsilon * MoveEpsilon || Quaternion.Angle(rot, o.SentRot) > AngleEpsilon;
                if (moved)
                {
                    o.LastChange = now;
                    o.RestSent = false;
                    o.SentPos = pos;
                    o.SentRot = rot;
                    WriteState(batch, o);
                    count++;
                    if (count == 28 || batch.Length > Protocol.MaxPacket - 40)
                    {
                        Flush(countPos, count);
                        packets++;
                        batch.Reset();
                        batch.Byte((byte)Msg.ObjStates);
                        batch.Float(now);
                        countPos = batch.Length;
                        batch.Byte(0);
                        count = 0;
                        if (packets >= 6) break; // bandwidth cap per tick
                    }
                }
                else if (!o.RestSent && now - o.LastChange > 0.4f)
                {
                    // Came to rest: send the exact final pose reliably.
                    o.RestSent = true;
                    SendRest(o, null);
                }
            }
            if (count > 0) Flush(countPos, count);
        }

        static void Flush(int countPos, int count)
        {
            batch.Buffer[countPos] = (byte)count;
            if (Session.IsHost)
            {
                foreach (NetPlayer p in Session.Remotes)
                {
                    if (p.Phase != PlayerPhase.Playing && p.Phase != PlayerPhase.Syncing) continue;
                    SteamNet.Send(p.SteamId, batch.Buffer, batch.Length, false, Channel.State);
                }
            }
            else SteamNet.Send(Session.HostId, batch.Buffer, batch.Length, false, Channel.State);
        }

        static void WriteState(NetWriter w, NetObject o)
        {
            Transform t = o.Go.transform;
            w.UInt(o.Id);
            w.Vector3(t.position);
            w.QuaternionPacked(t.rotation);
            Vector3 v = o.Body.isKinematic ? Vector3.zero : o.Body.velocity;
            Vector3 av = o.Body.isKinematic ? Vector3.zero : o.Body.angularVelocity;
            w.Half(v.x, 60f); w.Half(v.y, 60f); w.Half(v.z, 60f);
            w.Half(av.x, 50f); w.Half(av.y, 50f); w.Half(av.z, 50f);
        }

        static ObjState ReadState(NetReader r, out uint id)
        {
            id = r.UInt();
            ObjState s;
            s.Pos = r.Vector3();
            s.Rot = r.QuaternionPacked();
            s.Vel = new Vector3(r.Half(60f), r.Half(60f), r.Half(60f));
            s.AngVel = new Vector3(r.Half(50f), r.Half(50f), r.Half(50f));
            return s;
        }

        static void SendRest(NetObject o, NetPlayer onlyTo)
        {
            NetWriter w = Session.Begin(Msg.ObjRest);
            WriteRest(w, o);
            if (onlyTo != null) Session.SendTo(onlyTo, true, Channel.Reliable);
            else Session.SendToAll(true, Channel.Reliable);
        }

        static void WriteRest(NetWriter w, NetObject o)
        {
            w.UInt(o.Id);
            w.Byte(o.Owner);
            w.Bool(o.Go.activeSelf);
            Transform t = o.Go.transform;
            w.Vector3(t.position);
            w.Quaternion(t.rotation);
        }

        // ================================================================ ownership messages

        static void SetOwner(NetObject o, byte owner, bool broadcast)
        {
            o.Owner = owner;
            o.PendingOwnership = false;
            ApplyRole(o);
            if (broadcast && Session.IsHost)
            {
                NetWriter w = Session.Begin(Msg.ObjOwner);
                w.UInt(o.Id);
                w.Byte(owner);
                Session.BroadcastInGame(true, Channel.Reliable);
            }
        }

        static void OnOwnRequest(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null) return;
            uint id = r.UInt();
            NetObject o = ById(id);
            if (o == null || !o.Alive) { Deny(from, id); return; }
            bool hostHolding = localHeld == o;
            bool otherHolding = false;
            NetPlayer current = Session.BySlot(o.Owner);
            if (current != null && !current.IsLocal && current != from && current.Last.Held == id) otherHolding = true;
            if (hostHolding || otherHolding) { Deny(from, id); return; }
            SetOwner(o, from.Slot, true);
        }

        static void Deny(NetPlayer to, uint id)
        {
            NetWriter w = Session.Begin(Msg.ObjOwnDeny);
            w.UInt(id);
            Session.SendTo(to, true, Channel.Reliable);
        }

        static void OnOwnDeny(NetPlayer from, NetReader r)
        {
            uint id = r.UInt();
            NetObject o = ById(id);
            if (o == null) return;
            if (localHeld == o)
            {
                LocalPlayer.ForceDrop();
                localHeld = null;
                Notifications.Show("Someone else is holding that.");
            }
            o.Owner = 0;
            o.PendingOwnership = false;
            ApplyRole(o);
        }

        static void OnOwner(NetPlayer from, NetReader r)
        {
            if (Session.IsHost) return;
            uint id = r.UInt();
            byte owner = r.Byte();
            NetObject o = ById(id);
            if (o == null) return;
            bool wasMine = OwnedLocally(o);
            o.Owner = owner;
            o.PendingOwnership = false;
            if (wasMine && !OwnedLocally(o) && localHeld == o)
            {
                LocalPlayer.ForceDrop();
                localHeld = null;
            }
            ApplyRole(o);
        }

        /// <summary>Owner side: let the host take an object back once it has settled.</summary>
        static void CheckRelease(NetObject o)
        {
            if (Session.IsHost || !OwnedLocally(o) || o.PendingOwnership || localHeld == o) return;
            if (!o.RestSent) return;
            if (VehicleSync.IsCargoOfLocalVehicle(o)) return;
            NetWriter w = Session.Begin(Msg.ObjRelease);
            WriteRest(w, o);
            Session.SendToHost(true, Channel.Reliable);
            o.Owner = 0;
            ApplyRole(o);
        }

        static void OnRelease(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null) return;
            uint id = r.UInt();
            r.Byte();
            bool active = r.Bool();
            Vector3 pos = r.Vector3();
            Quaternion rot = r.Quaternion();
            NetObject o = ById(id);
            if (o == null || !o.Alive || o.Owner != from.Slot) return;
            o.Go.transform.position = pos;
            o.Go.transform.rotation = rot;
            SetOwner(o, 0, true);
            o.Buffer.Clear();
            SendRest(o, null);
        }

        static void OnPlayerLeft(NetPlayer p)
        {
            if (!MP.InGame) return;
            for (int i = 0; i < all.Count; i++)
            {
                NetObject o = all[i];
                if (o.Owner == p.Slot)
                {
                    if (Session.IsHost) SetOwner(o, 0, true);
                    else { o.Owner = 0; ApplyRole(o); }
                }
            }
        }

        // ================================================================ state messages

        static readonly NetWriter relay = new NetWriter(1200);

        static void OnStates(NetPlayer from, NetReader r)
        {
            if (from == null) return;
            float time = r.Float();
            int count = r.Byte();
            float local = from.ToLocalTime(time);
            int relayCountPos = 0, relayCount = 0;
            if (Session.IsHost)
            {
                // Re-stamped with the host clock: other clients convert host time to theirs.
                relay.Reset();
                relay.Byte((byte)Msg.ObjStates);
                relay.Float(local);
                relayCountPos = relay.Length;
                relay.Byte(0);
            }
            for (int i = 0; i < count; i++)
            {
                uint id;
                ObjState s = ReadState(r, out id);
                NetObject o = ById(id);
                if (o == null) continue;
                if (Session.IsHost)
                {
                    if (o.Owner != from.Slot) continue; // only the owner may move it
                    WriteStateRaw(relay, id, s);
                    relayCount++;
                }
                if (o.Replica) o.Buffer.Add(local, s);
            }
            if (Session.IsHost && relayCount > 0)
            {
                relay.Buffer[relayCountPos] = (byte)relayCount;
                foreach (NetPlayer p in Session.Remotes)
                {
                    if (p == from || (p.Phase != PlayerPhase.Playing && p.Phase != PlayerPhase.Syncing)) continue;
                    SteamNet.Send(p.SteamId, relay.Buffer, relay.Length, false, Channel.State);
                }
            }
        }

        static void WriteStateRaw(NetWriter w, uint id, ObjState s)
        {
            w.UInt(id);
            w.Vector3(s.Pos);
            w.QuaternionPacked(s.Rot);
            w.Half(s.Vel.x, 60f); w.Half(s.Vel.y, 60f); w.Half(s.Vel.z, 60f);
            w.Half(s.AngVel.x, 50f); w.Half(s.AngVel.y, 50f); w.Half(s.AngVel.z, 50f);
        }

        static void OnRest(NetPlayer from, NetReader r)
        {
            if (from == null) return;
            uint id = r.UInt();
            byte owner = r.Byte();
            bool active = r.Bool();
            Vector3 pos = r.Vector3();
            Quaternion rot = r.Quaternion();
            NetObject o = ById(id);
            if (o == null || !o.Alive) return;
            if (Session.IsHost)
            {
                if (o.Owner != from.Slot) return;
                ApplyRest(o, active, pos, rot);
                NetWriter w = Session.Begin(Msg.ObjRest);
                WriteRest(w, o);
                Session.BroadcastInGame(true, Channel.Reliable, from);
                return;
            }
            if (GameSession.CatchingUp) { o.Owner = owner; ApplyRole(o); }
            if (OwnedLocally(o)) return;
            ApplyRest(o, active, pos, rot);
        }

        static void ApplyRest(NetObject o, bool active, Vector3 pos, Quaternion rot)
        {
            if (o.Go.activeSelf != active) o.Go.SetActive(active);
            o.Buffer.Clear();
            o.Go.transform.position = pos;
            o.Go.transform.rotation = rot;
            o.SentPos = pos;
            o.SentRot = rot;
            if (!o.Replica && !o.Body.isKinematic)
            {
                o.Body.velocity = Vector3.zero;
                o.Body.angularVelocity = Vector3.zero;
            }
        }

        // ================================================================ spawn / destroy

        static void CheckDestroyed()
        {
            for (int i = all.Count - 1; i >= 0; i--)
            {
                NetObject o = all[i];
                if (o.Go != null && o.Body != null) continue;
                Unregister(o);
                NetWriter w = Session.Begin(Msg.ObjDestroy);
                w.UInt(o.Id);
                if (Session.IsAuthority)
                {
                    WorldLog.Append(w);
                    Session.BroadcastInGame(true, Channel.Reliable);
                }
                else Session.SendToHost(true, Channel.Reliable);
            }
        }

        static void OnDestroy(NetPlayer from, NetReader r)
        {
            uint id = r.UInt();
            NetObject o = ById(id);
            if (o != null)
            {
                Unregister(o);
                if (o.Go != null) UnityEngine.Object.Destroy(o.Go);
            }
            if (Session.IsHost && from != null && !from.IsLocal)
            {
                NetWriter w = Session.Begin(Msg.ObjDestroy);
                w.UInt(id);
                WorldLog.Append(w);
                Session.BroadcastInGame(true, Channel.Reliable, from);
            }
        }

        /// <summary>Finds physics objects created after loading (bought items, removed parts...).</summary>
        static void ScanForNewObjects()
        {
            UnityEngine.Object[] bodies = UnityEngine.Object.FindObjectsOfType(typeof(Rigidbody));
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < bodies.Length; i++)
            {
                Rigidbody rb = bodies[i] as Rigidbody;
                if (rb == null) continue;
                int inst = rb.gameObject.GetInstanceID();
                if (byInstance.ContainsKey(inst)) continue;
                if (!IsCandidate(rb)) continue;

                if (Session.IsAuthority)
                {
                    float first;
                    if (!hostSeen.TryGetValue(inst, out first)) { hostSeen[inst] = now; continue; }
                    if (now - first < 1f) continue;
                    NetObject o = Register(0x80000000u | nextDynamicId++, rb, true);
                    AnnounceSpawn(o, 0, 0);
                }
                else
                {
                    LocalSpawn ls;
                    if (!localSpawns.TryGetValue(inst, out ls))
                    {
                        localSpawns[inst] = new LocalSpawn { Go = rb.gameObject, FirstSeen = now };
                    }
                    else if (!ls.Requested && now - ls.FirstSeen > 6f)
                    {
                        // The host never announced it, so it came from something only we did
                        // (e.g. we bought it). Ask the host to add it to the shared world.
                        ls.Requested = true;
                        ls.Token = nextToken++;
                        NetWriter w = Session.Begin(Msg.ObjSpawnRequest);
                        w.UInt(ls.Token);
                        w.String(SpawnName(rb.gameObject));
                        w.Vector3(rb.transform.position);
                        w.Quaternion(rb.transform.rotation);
                        Session.SendToHost(true, Channel.Reliable);
                    }
                }
            }
            // Forget local spawns that disappeared.
            if (localSpawns.Count > 0)
            {
                List<int> dead = null;
                foreach (KeyValuePair<int, LocalSpawn> kv in localSpawns)
                    if (kv.Value.Go == null) { if (dead == null) dead = new List<int>(); dead.Add(kv.Key); }
                if (dead != null) foreach (int k in dead) localSpawns.Remove(k);
            }
            if (hostSeen.Count > 256)
            {
                // Objects that were seen once and vanished (or never qualified).
                List<int> stale = new List<int>();
                foreach (KeyValuePair<int, float> kv in hostSeen) if (now - kv.Value > 30f) stale.Add(kv.Key);
                foreach (int k in stale) hostSeen.Remove(k);
            }
        }

        static string SpawnName(GameObject go)
        {
            return go.name;
        }

        static void AnnounceSpawn(NetObject o, uint token, byte requester)
        {
            NetWriter w = Session.Begin(Msg.ObjSpawn);
            w.UInt(o.Id);
            w.String(o.Name);
            w.Vector3(o.Go.transform.position);
            w.Quaternion(o.Go.transform.rotation);
            w.Byte(o.Owner);
            w.UInt(token);
            w.Byte(requester);
            WorldLog.Append(w);
            Session.BroadcastInGame(true, Channel.Reliable);
            Log.Debug("Spawned shared object " + o.Name + " id " + o.Id);
        }

        static void OnSpawn(NetPlayer from, NetReader r)
        {
            if (Session.IsHost) return;
            uint id = r.UInt();
            string name = r.String();
            Vector3 pos = r.Vector3();
            Quaternion rot = r.Quaternion();
            byte owner = r.Byte();
            uint token = r.UInt();
            byte requester = r.Byte();
            if (ById(id) != null) return;

            GameObject go = null;
            if (token != 0 && Session.Local != null && requester == Session.Local.Slot)
            {
                foreach (LocalSpawn ls in localSpawns.Values)
                    if (ls.Token == token && ls.Go != null) { go = ls.Go; break; }
            }
            if (go == null) go = FindUnregisteredNear(name, pos, 2.5f);
            if (go == null) go = CreateFromTemplate(name, pos, rot);
            if (go == null)
            {
                Log.Warn("Could not create shared object '" + name + "' - it will be missing for you.");
                return;
            }
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null) return;
            localSpawns.Remove(go.GetInstanceID());
            NetObject o = Register(id, rb, true);
            o.Owner = owner;
            ApplyRole(o);
            if (!OwnedLocally(o))
            {
                go.transform.position = pos;
                go.transform.rotation = rot;
            }
        }

        static void OnSpawnRequest(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null) return;
            uint token = r.UInt();
            string name = r.String();
            Vector3 pos = r.Vector3();
            Quaternion rot = r.Quaternion();
            GameObject go = FindUnregisteredNear(name, pos, 2.5f);
            NetObject o = null;
            if (go != null)
            {
                Rigidbody rb = go.GetComponent<Rigidbody>();
                if (rb != null) o = Register(0x80000000u | nextDynamicId++, rb, true);
            }
            if (o == null)
            {
                go = CreateFromTemplate(name, pos, rot);
                Rigidbody rb = go != null ? go.GetComponent<Rigidbody>() : null;
                if (rb == null)
                {
                    Log.Warn("Could not reproduce '" + name + "' that " + from.Name + " created.");
                    return;
                }
                o = Register(0x80000000u | nextDynamicId++, rb, true);
            }
            o.Owner = from.Slot; // the requester keeps simulating its own copy
            ApplyRole(o);
            AnnounceSpawn(o, token, from.Slot);
        }

        static GameObject FindUnregisteredNear(string name, Vector3 pos, float radius)
        {
            UnityEngine.Object[] bodies = UnityEngine.Object.FindObjectsOfType(typeof(Rigidbody));
            GameObject best = null;
            float bestD = radius * radius;
            for (int i = 0; i < bodies.Length; i++)
            {
                Rigidbody rb = bodies[i] as Rigidbody;
                if (rb == null || rb.gameObject.name != name) continue;
                if (byInstance.ContainsKey(rb.gameObject.GetInstanceID())) continue;
                float d = (rb.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = rb.gameObject; }
            }
            return best;
        }

        /// <summary>Copies an existing object of the same kind (shop stock, prefab) to create one.</summary>
        static GameObject CreateFromTemplate(string name, Vector3 pos, Quaternion rot)
        {
            string baseName = name.Replace("(Clone)", "").Trim();
            GameObject template = null;
            UnityEngine.Object[] objs = Resources.FindObjectsOfTypeAll(typeof(GameObject));
            for (int i = 0; i < objs.Length && template == null; i++)
            {
                GameObject g = objs[i] as GameObject;
                if (g != null && g.name == name && g.GetComponent<Rigidbody>() != null) template = g;
            }
            for (int i = 0; i < objs.Length && template == null; i++)
            {
                GameObject g = objs[i] as GameObject;
                if (g != null && g.name == baseName && g.GetComponent<Rigidbody>() != null) template = g;
            }
            if (template == null) return null;
            GameObject copy = (GameObject)UnityEngine.Object.Instantiate(template, pos, rot);
            copy.name = name;
            copy.SetActive(true);
            return copy;
        }

        // ================================================================ cargo

        /// <summary>Host: loose objects inside a vehicle a client is driving are simulated by
        /// that client, so a beer crate in the van does not lag behind the van.</summary>
        static void AssignCargoToDrivers()
        {
            for (int i = 0; i < all.Count; i++)
            {
                NetObject o = all[i];
                if (!o.Alive || !o.Go.activeInHierarchy) continue;
                NetVehicle v = VehicleSync.VehicleContaining(o.Go.transform.position);
                if (v == null || v.Owner == 0 || v.Owner == o.Owner) continue;
                NetPlayer holder = Session.BySlot(o.Owner);
                if (holder != null && (holder.IsLocal ? localHeld == o : holder.Last.Held == o.Id)) continue;
                SetOwner(o, v.Owner, true);
            }
        }

        // ================================================================ catch-up

        public static void SendManifest(NetPlayer p)
        {
            NetWriter w = Session.Begin(Msg.Manifest);
            int n = 0;
            for (int i = 0; i < all.Count; i++) if (!all[i].Dynamic) n++;
            w.Int(n);
            for (int i = 0; i < all.Count; i++) if (!all[i].Dynamic) w.UInt(all[i].Id);
            Session.SendTo(p, true, Channel.Reliable);
        }

        static void OnManifest(NetPlayer from, NetReader r)
        {
            int n = r.Int();
            int matched = 0, missing = 0;
            HashSet<uint> hostIds = new HashSet<uint>();
            for (int i = 0; i < n; i++)
            {
                uint id = r.UInt();
                hostIds.Add(id);
                if (byId.ContainsKey(id)) matched++; else missing++;
            }
            int extra = 0;
            for (int i = 0; i < all.Count; i++) if (!all[i].Dynamic && !hostIds.Contains(all[i].Id)) extra++;
            Log.Info("Object manifest: " + matched + " matched, " + missing + " missing here, " + extra + " only here.");
            if (missing + extra > 0)
                Log.Warn("Some objects differ from the host's world (" + (missing + extra) + "). Make sure both games are the same version and have the same mods.");
        }

        public static void SendCatchUp(NetPlayer p)
        {
            BatchSender batch = new BatchSender(p);
            for (int i = 0; i < all.Count; i++)
            {
                NetObject o = all[i];
                if (!o.Alive) continue;
                NetWriter w = Session.Begin(Msg.ObjRest);
                WriteRest(w, o);
                batch.AddCurrent(w); // carries the current owner too
            }
            batch.Flush();
        }
    }
}
