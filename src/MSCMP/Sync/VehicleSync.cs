using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCMP.Game;
using MSCMP.Net;
using UnityEngine;

namespace MSCMP.Sync
{
    internal struct VehState
    {
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Vel;
        public Vector3 AngVel;
        public float Steer, Throttle, Brake, Handbrake, Clutch;
        public sbyte Gear;
        public float Rpm;
    }

    /// <summary>A drivable vehicle (or the trailer) shared between players.</summary>
    internal sealed class NetVehicle
    {
        public uint Id;
        public string Name;
        public Transform Root;
        public Rigidbody Body;
        public Drivetrain Drive;
        public CarController Controller;
        public Wheel[] Wheels = new Wheel[0];
        public PlayMakerFSM DriveTrigger;
        public Transform DriverHead;
        public Transform GetInPivot;
        public Vector3 PassengerHeadLocal;
        public bool HasPassengerSeat;
        public bool UsesVanSeat;
        public Bounds LocalBounds;

        public byte Owner;          // simulating player (0 = host)
        public byte Driver = 255;   // 255 = nobody
        public byte Passenger = 255;
        public bool Replica;
        public bool OriginalKinematic;
        public bool ControllerWasEnabled = true;
        public readonly SnapshotBuffer<VehState> Buffer = new SnapshotBuffer<VehState>(16);
        public float StillSince = -1f;
        public bool RestSent = true;
        public float LastStateTime;

        public Vector3 PassengerExitWorld()
        {
            if (GetInPivot != null) return GetInPivot.position + Vector3.up * 0.15f;
            return Root.TransformPoint(PassengerHeadLocal + new Vector3(Mathf.Sign(PassengerHeadLocal.x) * 1.4f, -0.8f, 0f));
        }
    }

    /// <summary>
    /// Vehicles: whoever drives a vehicle simulates it (so it feels like single player),
    /// everybody else gets a physically simulated copy that is steered with the driver's
    /// exact inputs and continuously corrected towards the driver's position - so wheels
    /// turn, suspension works and the engine revs on every screen. The host decides who
    /// may drive and who sits where.
    /// </summary>
    internal static class VehicleSync
    {
        static readonly List<NetVehicle> vehicles = new List<NetVehicle>();
        static readonly Dictionary<uint, NetVehicle> byId = new Dictionary<uint, NetVehicle>();
        static NetVehicle localDriving;
        static float nextSend;
        static readonly NetWriter w2 = new NetWriter(256);

        const float SendInterval = 0.033f;     // 30 Hz

        public static IList<NetVehicle> All { get { return vehicles; } }

        public static void Init()
        {
            Session.On(Msg.VehOwnRequest, OnOwnRequest);
            Session.On(Msg.VehOwner, OnOwner);
            Session.On(Msg.VehOwnDeny, OnOwnDeny);
            Session.On(Msg.VehRelease, OnRelease);
            Session.On(Msg.VehState, OnState);
            Session.On(Msg.SeatRequest, OnSeatRequest);
            Session.On(Msg.Seats, OnSeats);
            Session.PlayerLeft += OnPlayerLeft;
            Session.Started += () => { if (MP.InGame) ApplyRoleToAll(); };
            Session.Ended += reason => { if (MP.InGame) RestoreAll(); };
        }

        // ================================================================ registry

        public static NetVehicle ById(uint id)
        {
            NetVehicle v;
            return byId.TryGetValue(id, out v) ? v : null;
        }

        public static bool IsVehicleRootName(string name)
        {
            for (int i = 0; i < vehicles.Count; i++) if (vehicles[i].Name == name) return true;
            return false;
        }

        /// <summary>True for rigidbodies that belong to a vehicle (wheels, head pivot, the body itself).</summary>
        public static bool IsVehiclePart(Rigidbody rb)
        {
            Transform root = rb.transform.root;
            if (root.GetComponent<AxisCarController>() != null) return true;
            return root.name == "FLATBED" || root.name == "BOAT";
        }

        public static void OnWorldReady()
        {
            vehicles.Clear();
            byId.Clear();
            localDriving = null;
            HashSet<Transform> roots = new HashSet<Transform>();
            UnityEngine.Object[] ctrls = Resources.FindObjectsOfTypeAll(typeof(AxisCarController));
            for (int i = 0; i < ctrls.Length; i++)
            {
                AxisCarController c = ctrls[i] as AxisCarController;
                if (c == null || !GameUtil.IsSceneObject(c.gameObject) || c.transform.parent != null) continue;
                roots.Add(c.transform);
            }
            string[] extra = { "FLATBED", "BOAT" };
            for (int i = 0; i < extra.Length; i++)
            {
                Transform t = GameUtil.FindRoot(extra[i]);
                if (t != null && t.GetComponent<Rigidbody>() != null) roots.Add(t);
            }
            foreach (Transform r in roots)
            {
                try { Register(r); }
                catch (Exception e) { Log.Exception("Registering vehicle " + r.name, e); }
            }
            Log.Info("Registered " + vehicles.Count + " vehicles.");
            ApplyRoleToAll();
        }

        static void Register(Transform root)
        {
            NetVehicle v = new NetVehicle();
            v.Root = root;
            v.Name = root.name;
            v.Id = (GameUtil.Fnv1a("veh:" + root.name) & 0x7FFFFFFFu) | 1u;
            v.Body = root.GetComponent<Rigidbody>();
            v.Drive = root.GetComponent<Drivetrain>();
            v.Controller = root.GetComponent<CarController>();
            Axles axles = root.GetComponent<Axles>();
            if (axles != null && axles.allWheels != null) v.Wheels = axles.allWheels;
            v.OriginalKinematic = v.Body != null && v.Body.isKinematic;

            // The game's "ENTER DRIVING MODE" trigger.
            PlayMakerFSM[] fsms = root.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < fsms.Length; i++)
            {
                if (fsms[i].FsmName == "PlayerTrigger" && fsms[i].gameObject.name == "DriveTrigger") { v.DriveTrigger = fsms[i]; break; }
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (v.DriverHead == null && all[i].name == "DriverHeadPivot") v.DriverHead = all[i];
                if (v.GetInPivot == null && all[i].name == "GetInPivot" && all[i].parent == root) v.GetInPivot = all[i];
            }
            if (v.DriverHead != null)
            {
                Vector3 d = root.InverseTransformPoint(v.DriverHead.position);
                v.PassengerHeadLocal = new Vector3(-d.x, d.y, d.z);
                v.HasPassengerSeat = v.GetInPivot != null && Mathf.Abs(d.x) > 0.15f;
            }
            string n = root.name.ToUpperInvariant();
            v.UsesVanSeat = n.StartsWith("HAYOSIKO") || n.StartsWith("GIFU") || n.StartsWith("KEKMET") || n.StartsWith("COMBINE");
            v.LocalBounds = ComputeLocalBounds(root);

            byId[v.Id] = v;
            vehicles.Add(v);
        }

        static Bounds ComputeLocalBounds(Transform root)
        {
            Bounds b = new Bounds(Vector3.zero, Vector3.zero);
            bool any = false;
            Collider[] cols = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                Collider c = cols[i];
                if (c == null || c.isTrigger || !c.enabled || c is WheelCollider) continue;
                if (c.gameObject.layer == 20 || c.gameObject.layer == 23) continue; // player-only colliders
                Bounds wb = c.bounds;
                Vector3 mn = wb.min, mx = wb.max;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 corner = new Vector3((k & 1) == 0 ? mn.x : mx.x, (k & 2) == 0 ? mn.y : mx.y, (k & 4) == 0 ? mn.z : mx.z);
                    Vector3 local = root.InverseTransformPoint(corner);
                    if (!any) { b = new Bounds(local, Vector3.zero); any = true; }
                    else b.Encapsulate(local);
                }
            }
            if (!any) b = new Bounds(Vector3.zero, new Vector3(2f, 1.5f, 4f));
            return b;
        }

        public static void OnWorldLeft()
        {
            RestoreAll();
            vehicles.Clear();
            byId.Clear();
            localDriving = null;
        }

        /// <summary>The vehicle the local player is driving (the game parents the player to it).</summary>
        public static NetVehicle FindByChild(Transform t)
        {
            if (t == null || string.IsNullOrEmpty(LocalPlayer.CurrentVehicleName)) return null;
            for (int i = 0; i < vehicles.Count; i++)
                if (vehicles[i].Root != null && t.IsChildOf(vehicles[i].Root)) return vehicles[i];
            return null;
        }

        public static NetVehicle VehicleContaining(Vector3 world)
        {
            for (int i = 0; i < vehicles.Count; i++)
            {
                NetVehicle v = vehicles[i];
                if (v.Root == null || !v.Root.gameObject.activeInHierarchy) continue;
                Vector3 local = v.Root.InverseTransformPoint(world);
                Bounds b = v.LocalBounds;
                if (local.x > b.min.x && local.x < b.max.x && local.y > b.min.y - 0.1f && local.y < b.max.y && local.z > b.min.z && local.z < b.max.z)
                    return v;
            }
            return null;
        }

        public static bool IsCargoOfLocalVehicle(NetObject o)
        {
            if (Session.Local == null) return false;
            NetVehicle v = VehicleContaining(o.Go.transform.position);
            return v != null && v.Owner == Session.Local.Slot && v.Body != null && v.Body.velocity.sqrMagnitude > 0.04f;
        }

        // ================================================================ roles

        static bool OwnedLocally(NetVehicle v)
        {
            if (!Session.Active) return true;
            return Session.Local != null && v.Owner == Session.Local.Slot;
        }

        static void ApplyRoleToAll()
        {
            for (int i = 0; i < vehicles.Count; i++) ApplyRole(vehicles[i]);
        }

        static void ApplyRole(NetVehicle v)
        {
            if (v.Root == null || v.Body == null) return;
            bool replica = Session.Active && Session.Connected && !OwnedLocally(v);
            if (replica && !v.Replica)
            {
                v.Replica = true;
                v.OriginalKinematic = v.Body.isKinematic;
                if (v.Controller != null) { v.ControllerWasEnabled = v.Controller.enabled; v.Controller.enabled = false; }
                v.Buffer.Clear();
                v.Body.isKinematic = v.Driver == 255; // parked copies stay put; driven copies simulate
            }
            else if (!replica && v.Replica)
            {
                v.Replica = false;
                v.Body.isKinematic = v.OriginalKinematic;
                // The game switches the controller on when you enter driving mode and off when
                // you leave; only keep it on if we are the one sitting in the driver's seat.
                if (v.Controller != null) v.Controller.enabled = LocalPlayerDrives(v);
                v.Body.WakeUp();
                v.RestSent = true;
            }
            UpdateDriveTriggerLock(v);
        }

        static bool LocalPlayerDrives(NetVehicle v)
        {
            return LocalPlayer.Player != null && LocalPlayer.PassengerOf == null && v.Root != null &&
                   !string.IsNullOrEmpty(LocalPlayer.CurrentVehicleName) && LocalPlayer.Player.IsChildOf(v.Root);
        }

        /// <summary>Stops you entering driving mode in a vehicle somebody else is driving.</summary>
        static void UpdateDriveTriggerLock(NetVehicle v)
        {
            if (v.DriveTrigger == null) return;
            bool lockIt = Session.Active && v.Driver != 255 && (Session.Local == null || v.Driver != Session.Local.Slot);
            if (v.DriveTrigger.enabled == lockIt)
            {
                v.DriveTrigger.enabled = !lockIt;
                if (lockIt)
                {
                    // Do not leave "ENTER DRIVING MODE" hanging on screen.
                    FsmString gui = GameUtil.GlobalString("GUIinteraction");
                    if (gui != null && gui.Value == "ENTER DRIVING MODE") gui.Value = "";
                }
            }
        }

        static void RestoreAll()
        {
            for (int i = 0; i < vehicles.Count; i++)
            {
                NetVehicle v = vehicles[i];
                if (v.Root == null || v.Body == null) continue;
                if (v.Replica)
                {
                    v.Body.isKinematic = v.OriginalKinematic;
                    if (v.Controller != null) v.Controller.enabled = LocalPlayerDrives(v);
                    v.Replica = false;
                }
                v.Owner = 0; v.Driver = 255; v.Passenger = 255;
                if (v.DriveTrigger != null) v.DriveTrigger.enabled = true;
            }
        }

        // ================================================================ per frame

        public static void Update()
        {
            if (!Session.Active || !Session.Connected || Session.Local == null) return;
            byte me = Session.Local.Slot;

            // Local driving changes
            NetVehicle driving = LocalPlayer.PassengerOf == null ? FindByChild(LocalPlayer.Player) : null;
            if (driving != localDriving)
            {
                if (localDriving != null) OnLocalExit(localDriving);
                localDriving = driving;
                if (driving != null) OnLocalEnter(driving);
            }

            float now = Time.realtimeSinceStartup;
            if (now >= nextSend)
            {
                nextSend = now + SendInterval;
                for (int i = 0; i < vehicles.Count; i++)
                {
                    NetVehicle v = vehicles[i];
                    if (v.Root == null || v.Body == null || v.Replica || v.Owner != me) continue;
                    SendStateIfNeeded(v, now);
                    CheckOwnerRelease(v, now);
                }
            }
        }

        static void OnLocalEnter(NetVehicle v)
        {
            byte me = Session.Local.Slot;
            if (v.Driver != 255 && v.Driver != me)
            {
                Notifications.Show("Someone else is driving this vehicle.");
                return;
            }
            Log.Info("Driving " + v.Name);
            if (Session.IsHost)
            {
                SetOwner(v, 0, 0);
            }
            else
            {
                // Take it immediately; the host confirms (or refuses and we hand it back).
                v.Owner = me;
                v.Driver = me;
                ApplyRole(v);
                NetWriter w = Session.Begin(Msg.VehOwnRequest);
                w.UInt(v.Id);
                Session.SendToHost(true, Channel.Reliable);
            }
        }

        static void OnLocalExit(NetVehicle v)
        {
            byte me = Session.Local.Slot;
            if (v.Driver != me) return;
            Log.Info("Stopped driving " + v.Name);
            if (Session.IsHost)
            {
                SetOwner(v, 0, 255);
            }
            else
            {
                v.Driver = 255;
                NetWriter w = Session.Begin(Msg.VehRelease);
                w.UInt(v.Id);
                w.Byte(1); // driver left, still simulating until it stops
                Session.SendToHost(true, Channel.Reliable);
            }
        }

        static void SendStateIfNeeded(NetVehicle v, float now)
        {
            bool moving = v.Body.velocity.sqrMagnitude > 0.0025f || v.Body.angularVelocity.sqrMagnitude > 0.0025f;
            bool driven = v.Driver != 255;
            if (!moving && !driven)
            {
                if (!v.RestSent)
                {
                    v.RestSent = true;
                    SendState(v, true);
                }
                return;
            }
            v.RestSent = false;
            SendState(v, false);
        }

        static void SendState(NetVehicle v, bool reliable)
        {
            NetWriter w = Session.Begin(Msg.VehState);
            WriteState(w, v, Time.realtimeSinceStartup);
            Session.SendToAll(reliable, reliable ? Channel.Reliable : Channel.State);
        }

        static void WriteState(NetWriter w, NetVehicle v, float time)
        {
            w.UInt(v.Id);
            w.Float(time);
            w.Vector3(v.Body.position);
            w.Quaternion(v.Body.rotation);
            w.Half(v.Body.velocity.x, 80f); w.Half(v.Body.velocity.y, 80f); w.Half(v.Body.velocity.z, 80f);
            w.Half(v.Body.angularVelocity.x, 30f); w.Half(v.Body.angularVelocity.y, 30f); w.Half(v.Body.angularVelocity.z, 30f);
            CarController c = v.Controller;
            Drivetrain d = v.Drive;
            w.Signed8(c != null ? c.steering : 0f);
            w.Unit8(d != null ? d.throttle : 0f);
            w.Unit8(c != null ? c.brake : 0f);
            w.Unit8(c != null ? c.handbrakeInput : 0f);
            w.Unit8(d != null && d.clutch != null ? d.clutch.GetClutchPosition() : 1f);
            w.SByte((sbyte)(d != null ? Mathf.Clamp(d.gear, -1, 120) : 0));
            w.UShort((ushort)Mathf.Clamp(d != null ? d.rpm : 0f, 0f, 65000f));
        }

        static VehState ReadState(NetReader r)
        {
            VehState s;
            s.Pos = r.Vector3();
            s.Rot = r.Quaternion();
            s.Vel = new Vector3(r.Half(80f), r.Half(80f), r.Half(80f));
            s.AngVel = new Vector3(r.Half(30f), r.Half(30f), r.Half(30f));
            s.Steer = r.Signed8();
            s.Throttle = r.Unit8();
            s.Brake = r.Unit8();
            s.Handbrake = r.Unit8();
            s.Clutch = r.Unit8();
            s.Gear = r.SByte();
            s.Rpm = r.UShort();
            return s;
        }

        /// <summary>Owner side: once a vehicle nobody drives has stopped, hand it back to the host.</summary>
        static void CheckOwnerRelease(NetVehicle v, float now)
        {
            if (Session.IsHost || v.Driver != 255) { v.StillSince = -1f; return; }
            bool still = v.Body.velocity.sqrMagnitude < 0.04f;
            if (!still) { v.StillSince = -1f; return; }
            if (v.StillSince < 0f) { v.StillSince = now; return; }
            if (now - v.StillSince < 2f) return;
            v.StillSince = -1f;
            NetWriter w = Session.Begin(Msg.VehRelease);
            w.UInt(v.Id);
            w.Byte(2); // give ownership back
            w.Vector3(v.Body.position);
            w.Quaternion(v.Body.rotation);
            Session.SendToHost(true, Channel.Reliable);
            v.Owner = 0;
            ApplyRole(v);
        }

        public static void FixedUpdate()
        {
            if (!Session.Active || !Session.Connected) return;
            float renderTime = Time.realtimeSinceStartup - 0.07f;
            for (int i = 0; i < vehicles.Count; i++)
            {
                NetVehicle v = vehicles[i];
                if (!v.Replica || v.Root == null || v.Body == null || v.Buffer.Count == 0) continue;
                VehState a, b;
                float alpha, over;
                if (!v.Buffer.Sample(renderTime, out a, out b, out alpha, out over)) continue;
                VehState t = b;
                t.Pos = Vector3.Lerp(a.Pos, b.Pos, alpha);
                t.Rot = Quaternion.Slerp(a.Rot, b.Rot, alpha);
                t.Vel = Vector3.Lerp(a.Vel, b.Vel, alpha);
                t.AngVel = Vector3.Lerp(a.AngVel, b.AngVel, alpha);
                if (over > 0f) t.Pos += t.Vel * Mathf.Min(over, 0.4f);

                bool driven = v.Driver != 255 || over < 1f;
                if (v.Body.isKinematic != !driven) v.Body.isKinematic = !driven;

                if (v.Body.isKinematic)
                {
                    v.Body.MovePosition(t.Pos);
                    v.Body.MoveRotation(t.Rot);
                    continue;
                }

                Vector3 err = t.Pos - v.Body.position;
                if (err.sqrMagnitude > 36f)
                {
                    v.Body.position = t.Pos;
                    v.Body.rotation = t.Rot;
                    v.Body.velocity = t.Vel;
                    v.Body.angularVelocity = t.AngVel;
                }
                else
                {
                    v.Body.velocity = Vector3.Lerp(v.Body.velocity, t.Vel + err * 4f, 0.5f);
                    Quaternion dq = t.Rot * Quaternion.Inverse(v.Body.rotation);
                    float angle; Vector3 axis;
                    dq.ToAngleAxis(out angle, out axis);
                    if (angle > 180f) angle -= 360f;
                    Vector3 angCorr = float.IsNaN(axis.x) ? Vector3.zero : axis * (angle * Mathf.Deg2Rad * 4f);
                    v.Body.angularVelocity = Vector3.Lerp(v.Body.angularVelocity, t.AngVel + angCorr, 0.5f);
                }
                ApplyInputs(v, t);
            }
        }

        /// <summary>Feeds the driver's inputs into the copy so wheels, suspension and the engine behave the same.</summary>
        static void ApplyInputs(NetVehicle v, VehState s)
        {
            for (int i = 0; i < v.Wheels.Length; i++)
            {
                Wheel wh = v.Wheels[i];
                if (wh == null) continue;
                wh.deltaSteering = s.Steer - wh.steering;
                wh.steering = s.Steer;
                wh.brake = s.Brake;
                wh.handbrake = s.Handbrake;
            }
            if (v.Drive != null)
            {
                v.Drive.throttle = s.Throttle;
                if (v.Drive.clutch != null) v.Drive.clutch.SetClutchPosition(s.Clutch);
                int gear = s.Gear;
                if (gear >= 0 && v.Drive.gearRatios != null && gear < v.Drive.gearRatios.Length && v.Drive.gear != gear && !v.Drive.changingGear)
                    v.Drive.Shift(gear);
            }
        }

        public static void LateUpdate()
        {
        }

        // ================================================================ messages

        static void SetOwner(NetVehicle v, byte owner, byte driver)
        {
            v.Owner = owner;
            v.Driver = driver;
            ApplyRole(v);
            if (Session.IsHost)
            {
                NetWriter w = Session.Begin(Msg.VehOwner);
                w.UInt(v.Id);
                w.Byte(owner);
                w.Byte(driver);
                Session.BroadcastInGame(true, Channel.Reliable);
            }
        }

        static void OnOwnRequest(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null) return;
            NetVehicle v = ById(r.UInt());
            if (v == null) return;
            if (v.Driver != 255 && v.Driver != from.Slot)
            {
                NetWriter w = Session.Begin(Msg.VehOwnDeny);
                w.UInt(v.Id);
                Session.SendTo(from, true, Channel.Reliable);
                return;
            }
            if (v.Passenger == from.Slot) v.Passenger = 255;
            SetOwner(v, from.Slot, from.Slot);
        }

        static void OnOwnDeny(NetPlayer from, NetReader r)
        {
            NetVehicle v = ById(r.UInt());
            if (v == null) return;
            Notifications.Show("Someone else is already driving the " + Pretty(v.Name) + ".");
            if (localDriving == v && v.DriveTrigger != null)
            {
                // Get out the same way the game does when you press the driving key.
                if (!GameUtil.ForceState(v.DriveTrigger, "In car")) GameUtil.ForceState(v.DriveTrigger, "Create player");
            }
            localDriving = null;
        }

        static void OnOwner(NetPlayer from, NetReader r)
        {
            if (Session.IsHost) return;
            NetVehicle v = ById(r.UInt());
            byte owner = r.Byte();
            byte driver = r.Byte();
            if (v == null) return;
            v.Owner = owner;
            v.Driver = driver;
            ApplyRole(v);
        }

        static void OnRelease(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null) return;
            NetVehicle v = ById(r.UInt());
            byte kind = r.Byte();
            if (v == null || v.Owner != from.Slot) return;
            if (kind == 1)
            {
                SetOwner(v, v.Owner, 255);
            }
            else if (kind == 2)
            {
                Vector3 pos = r.Vector3();
                Quaternion rot = r.Quaternion();
                SetOwner(v, 0, 255);
                v.Body.position = pos;
                v.Body.rotation = rot;
                v.Body.velocity = Vector3.zero;
                v.Body.angularVelocity = Vector3.zero;
            }
        }

        static void OnState(NetPlayer from, NetReader r)
        {
            if (from == null) return;
            NetVehicle v = ById(r.UInt());
            float time = r.Float();
            VehState s = ReadState(r);
            if (v == null || v.Root == null) return;
            if (Session.IsHost)
            {
                if (v.Owner != from.Slot) return;
                // Relay with the host clock.
                NetWriter w = w2.Reset();
                w.Byte((byte)Msg.VehState);
                w.UInt(v.Id);
                w.Float(from.ToLocalTime(time));
                WriteParsed(w, s);
                foreach (NetPlayer p in Session.Remotes)
                {
                    if (p == from || (p.Phase != PlayerPhase.Playing && p.Phase != PlayerPhase.Syncing)) continue;
                    SteamNet.Send(p.SteamId, w.Buffer, w.Length, false, Channel.State);
                }
            }
            if (!v.Replica)
            {
                if (GameSession.CatchingUp) { v.Body.position = s.Pos; v.Body.rotation = s.Rot; }
                return;
            }
            v.Buffer.Add(from.ToLocalTime(time), s);
            v.LastStateTime = Time.realtimeSinceStartup;
        }

        static void WriteParsed(NetWriter w, VehState s)
        {
            w.Vector3(s.Pos);
            w.Quaternion(s.Rot);
            w.Half(s.Vel.x, 80f); w.Half(s.Vel.y, 80f); w.Half(s.Vel.z, 80f);
            w.Half(s.AngVel.x, 30f); w.Half(s.AngVel.y, 30f); w.Half(s.AngVel.z, 30f);
            w.Signed8(s.Steer);
            w.Unit8(s.Throttle);
            w.Unit8(s.Brake);
            w.Unit8(s.Handbrake);
            w.Unit8(s.Clutch);
            w.SByte(s.Gear);
            w.UShort((ushort)Mathf.Clamp(s.Rpm, 0f, 65000f));
        }

        static void OnPlayerLeft(NetPlayer p)
        {
            if (!MP.InGame) return;
            for (int i = 0; i < vehicles.Count; i++)
            {
                NetVehicle v = vehicles[i];
                if (v.Passenger == p.Slot) v.Passenger = 255;
                if (v.Owner == p.Slot || v.Driver == p.Slot)
                {
                    if (Session.IsHost) SetOwner(v, 0, 255);
                    else { v.Owner = 0; v.Driver = 255; ApplyRole(v); }
                }
            }
            if (Session.IsHost) BroadcastSeats();
        }

        // ================================================================ passenger seats

        public static NetVehicle PassengerSeatNear(Vector3 feet, Vector3 head)
        {
            if (Session.Local == null) return null;
            byte me = Session.Local.Slot;
            for (int i = 0; i < vehicles.Count; i++)
            {
                NetVehicle v = vehicles[i];
                if (!v.HasPassengerSeat || v.Root == null || !v.Root.gameObject.activeInHierarchy) continue;
                if (v.Driver == me || v.Passenger != 255) continue;
                if (v.Driver == 255) continue; // only ride along with a friend
                if ((v.GetInPivot.position - feet).sqrMagnitude < 1.7f * 1.7f) return v;
            }
            return null;
        }

        public static void RequestPassengerSeat(NetVehicle v)
        {
            if (Session.IsHost) { TrySeat(Session.Local, v, true); return; }
            NetWriter w = Session.Begin(Msg.SeatRequest);
            w.UInt(v.Id);
            w.Bool(true);
            Session.SendToHost(true, Channel.Reliable);
        }

        public static void LeavePassengerSeat(NetVehicle v)
        {
            if (Session.IsHost) { TrySeat(Session.Local, v, false); return; }
            NetWriter w = Session.Begin(Msg.SeatRequest);
            w.UInt(v.Id);
            w.Bool(false);
            Session.SendToHost(true, Channel.Reliable);
        }

        static void OnSeatRequest(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null) return;
            NetVehicle v = ById(r.UInt());
            bool enter = r.Bool();
            if (v != null) TrySeat(from, v, enter);
        }

        static void TrySeat(NetPlayer p, NetVehicle v, bool enter)
        {
            if (enter)
            {
                if (!v.HasPassengerSeat || v.Passenger != 255 || v.Driver == p.Slot) { BroadcastSeats(); return; }
                // Leave any other seat first.
                for (int i = 0; i < vehicles.Count; i++) if (vehicles[i].Passenger == p.Slot) vehicles[i].Passenger = 255;
                v.Passenger = p.Slot;
            }
            else if (v.Passenger == p.Slot) v.Passenger = 255;
            BroadcastSeats();
            if (p.IsLocal) ApplyLocalSeat();
        }

        static void BroadcastSeats()
        {
            NetWriter w = Session.Begin(Msg.Seats);
            w.Byte((byte)vehicles.Count);
            for (int i = 0; i < vehicles.Count; i++)
            {
                w.UInt(vehicles[i].Id);
                w.Byte(vehicles[i].Passenger);
            }
            Session.BroadcastInGame(true, Channel.Reliable);
        }

        static void OnSeats(NetPlayer from, NetReader r)
        {
            if (Session.IsHost) return;
            int n = r.Byte();
            for (int i = 0; i < n; i++)
            {
                NetVehicle v = ById(r.UInt());
                byte passenger = r.Byte();
                if (v != null) v.Passenger = passenger;
            }
            ApplyLocalSeat();
        }

        static void ApplyLocalSeat()
        {
            if (Session.Local == null) return;
            byte me = Session.Local.Slot;
            NetVehicle mine = null;
            for (int i = 0; i < vehicles.Count; i++) if (vehicles[i].Passenger == me) mine = vehicles[i];
            if (mine != null && LocalPlayer.PassengerOf != mine)
            {
                if (LocalPlayer.PassengerOf != null) LocalPlayer.ExitPassenger(false);
                LocalPlayer.EnterPassenger(mine);
            }
            else if (mine == null && LocalPlayer.PassengerOf != null)
            {
                LocalPlayer.ExitPassenger(false);
            }
        }

        // ================================================================ catch-up

        public static void SendCatchUp(NetPlayer p)
        {
            for (int i = 0; i < vehicles.Count; i++)
            {
                NetVehicle v = vehicles[i];
                if (v.Root == null || v.Body == null) continue;
                NetWriter w = Session.Begin(Msg.VehState);
                WriteState(w, v, Time.realtimeSinceStartup);
                Session.SendTo(p, true, Channel.Reliable);
                w = Session.Begin(Msg.VehOwner);
                w.UInt(v.Id);
                w.Byte(v.Owner);
                w.Byte(v.Driver);
                Session.SendTo(p, true, Channel.Reliable);
            }
            NetWriter s = Session.Begin(Msg.Seats);
            s.Byte((byte)vehicles.Count);
            for (int i = 0; i < vehicles.Count; i++) { s.UInt(vehicles[i].Id); s.Byte(vehicles[i].Passenger); }
            Session.SendTo(p, true, Channel.Reliable);
        }

        public static string Pretty(string rootName)
        {
            int i = rootName.IndexOf('(');
            string n = i > 0 ? rootName.Substring(0, i) : rootName;
            n = n.Replace("RCO_", "").Replace("_", " ").Trim();
            if (n.Length == 0) return rootName;
            return n.Substring(0, 1).ToUpperInvariant() + n.Substring(1).ToLowerInvariant();
        }
    }
}
