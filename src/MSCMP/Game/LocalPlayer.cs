using System;
using HutongGames.PlayMaker;
using MSCMP.Net;
using MSCMP.Sync;
using MSCMP.UI;
using UnityEngine;

namespace MSCMP.Game
{
    /// <summary>
    /// Reads the local player (position, look, crouch, vehicle, item in hand) and
    /// implements riding as a passenger in a friend's car.
    /// </summary>
    internal static class LocalPlayer
    {
        public static Transform Player;            // PLAYER
        public static Transform PitchPivot;        // PLAYER/Pivot/AnimPivot/Camera/FPSCamera (pitch)
        public static Camera View;                 // the first person camera
        public static CharacterController Controller;
        static Behaviour motor, input;
        static Behaviour[] mouseLooks = new Behaviour[0];
        static PlayMakerFSM pickUp;
        static FsmGameObject pickedObject, itemPivot;
        static FsmString currentVehicle;
        static FsmBool inMenu;

        static Vector3 lastPos;
        static float lastTime;
        static Vector3 velocity;
        static float standingEye = -1f;     // learned from the player's own standing eye height
        static bool inputBlocked;

        // Passenger state
        public static NetVehicle PassengerOf { get; private set; }
        static Transform passengerAnchor;
        static float passengerCooldown;
        static bool savedController = true, savedMotor = true, savedInput = true;

        public static bool Bound { get { return Player != null && View != null; } }

        /// <summary>Finds the local player objects. Returns false while the scene is still settling.</summary>
        public static bool Bind()
        {
            GameObject p = GameObject.Find("PLAYER");
            if (p == null || !p.activeInHierarchy) return false;
            Player = p.transform;
            Controller = p.GetComponent<CharacterController>();
            motor = p.GetComponent("CharacterMotor") as Behaviour;
            input = p.GetComponent("FPSInputController") as Behaviour;
            PitchPivot = Player.Find("Pivot/AnimPivot/Camera/FPSCamera");

            View = null;
            FsmGameObject pov = GameUtil.GlobalGameObject("POV");
            if (pov != null && pov.Value != null) View = pov.Value.GetComponent<Camera>();
            if (View == null)
            {
                Transform cam = Player.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera");
                if (cam != null) View = cam.GetComponent<Camera>();
            }
            if (View == null) return false;

            System.Collections.Generic.List<Behaviour> looks = new System.Collections.Generic.List<Behaviour>();
            AddLook(looks, Player);
            if (PitchPivot != null) AddLook(looks, PitchPivot);
            mouseLooks = looks.ToArray();

            Transform hand = Player.Find("Pivot/AnimPivot/Camera/FPSCamera/1Hand_Assemble/Hand");
            pickUp = hand != null ? GameUtil.GetFsm(hand.gameObject, "PickUp") : null;
            pickedObject = pickUp != null ? GameUtil.FsmGameObject(pickUp, "PickedObject") : null;
            if (pickUp == null) Log.Warn("PickUp FSM not found - held items will not be shown to others.");

            currentVehicle = GameUtil.GlobalString("PlayerCurrentVehicle");
            inMenu = GameUtil.GlobalBool("PlayerInMenu");
            lastPos = Player.position;
            lastTime = Time.time;
            standingEye = -1f;
            Log.Info("Local player bound (camera " + View.name + ")");
            return true;
        }

        static void AddLook(System.Collections.Generic.List<Behaviour> list, Transform t)
        {
            Component[] cs = t.GetComponents<Component>();
            for (int i = 0; i < cs.Length; i++)
            {
                Behaviour b = cs[i] as Behaviour;
                if (b == null) continue;
                string n = b.GetType().Name;
                if (n == "MouseLook" || n == "SimpleSmoothMouseLook") list.Add(b);
            }
        }

        public static void OnWorldLeft()
        {
            if (PassengerOf != null) ExitPassenger(false);
            SetInputBlocked(false);
            Player = null; View = null; PitchPivot = null; Controller = null;
            pickUp = null; pickedObject = null; itemPivot = null; sleeps = null; needs = null;
        }

        // ------------------------------------------------------------ sampling

        /// <summary>What the player is carrying right now. The game leaves PickedObject set after a
        /// drop, so it only counts while the pickup machine is in a "... picked" state or the
        /// object is attached to the hand.</summary>
        public static GameObject HeldObject
        {
            get
            {
                try
                {
                    GameObject go = pickedObject != null ? pickedObject.Value : null;
                    if (go == null || pickUp == null) return null;
                    string state = pickUp.ActiveStateName;
                    if (state != null && state.IndexOf("picked", StringComparison.OrdinalIgnoreCase) >= 0) return go;
                    if (itemPivot == null) itemPivot = GameUtil.GlobalGameObject("ItemPivot");
                    if (itemPivot != null && itemPivot.Value != null && go.transform.IsChildOf(itemPivot.Value.transform)) return go;
                    return null;
                }
                catch { return null; }
            }
        }

        /// <summary>Drops whatever the player holds (used when someone else already owns it).</summary>
        public static void ForceDrop()
        {
            if (pickUp == null) return;
            if (!GameUtil.ForceState(pickUp, "Drop part"))
                if (pickedObject != null) pickedObject.Value = null;
        }

        public static string CurrentVehicleName
        {
            get { try { return currentVehicle != null ? currentVehicle.Value : ""; } catch { return ""; } }
        }

        public static bool InMenu
        {
            get { try { return inMenu != null && inMenu.Value; } catch { return false; } }
        }

        public static Vector3 Feet
        {
            get
            {
                if (Controller == null) return Player.position;
                Vector3 center = Player.TransformPoint(Controller.center);
                float half = Controller.height * 0.5f * Player.lossyScale.y;
                return center - Vector3.up * half;
            }
        }

        public static void Update()
        {
            if (!Bound) { Bind(); return; }
            float dt = Time.time - lastTime;
            if (dt > 0.0001f)
            {
                Vector3 v = (Player.position - lastPos) / dt;
                if (v.sqrMagnitude > 2500f) v = Vector3.zero; // teleport
                velocity = Vector3.Lerp(velocity, v, Mathf.Clamp01(dt * 10f));
                lastPos = Player.position;
                lastTime = Time.time;
            }
            UpdatePassenger();
        }

        public static void LateUpdate()
        {
            if (PassengerOf != null) HoldPassengerPose();
            try { UpdatePlatform(); } catch (Exception e) { Log.ExceptionOnce("LocalPlayer.Platform", e); }
        }

        // ------------------------------------------------------------ standing on vehicles

        /// <summary>The vehicle you are standing on (boat, van back, trailer...), or null.</summary>
        public static NetVehicle Platform { get; private set; }
        static Vector3 platformLocal, platformPrevWorld;
        static float platformPrevYaw;

        /// <summary>Carries you along with the vehicle you stand on while somebody drives it,
        /// like standing on a boat or in the back of the van.</summary>
        static void UpdatePlatform()
        {
            NetVehicle found = null;
            if (Bound && Controller != null && PassengerOf == null && string.IsNullOrEmpty(CurrentVehicleName))
            {
                RaycastHit[] hits = Physics.RaycastAll(Feet + Vector3.up * 0.3f, Vector3.down, 0.9f, GameUtil.WorldMask);
                float best = float.MaxValue;
                Collider ground = null;
                for (int i = 0; i < hits.Length; i++)
                {
                    if (hits[i].collider.isTrigger || GameUtil.IsPersonCollider(hits[i].collider)) continue;
                    if (hits[i].distance < best) { best = hits[i].distance; ground = hits[i].collider; }
                }
                if (ground != null)
                    foreach (NetVehicle v in VehicleSync.All)
                        if (v.Root != null && ground.transform.IsChildOf(v.Root)) { found = v; break; }
            }

            if (found != null && found == Platform)
            {
                Transform r = found.Root;
                Vector3 delta = r.TransformPoint(platformLocal) - platformPrevWorld;
                float turn = Mathf.DeltaAngle(platformPrevYaw, r.eulerAngles.y);
                if (delta.sqrMagnitude > 1e-8f && delta.sqrMagnitude < 25f)
                {
                    Player.position += delta;
                    lastPos += delta; // being carried is not walking
                }
                if (Mathf.Abs(turn) > 0.001f && Mathf.Abs(turn) < 45f) Player.Rotate(0f, turn, 0f, Space.World);
            }
            Platform = found;
            if (found != null)
            {
                platformLocal = found.Root.InverseTransformPoint(Player.position);
                platformPrevWorld = Player.position;
                platformPrevYaw = found.Root.eulerAngles.y;
            }
        }

        public static PlayerSnapshot Sample()
        {
            PlayerSnapshot s = new PlayerSnapshot();
            s.Time = Time.realtimeSinceStartup;
            Vector3 feet = Feet;
            Vector3 head = View.transform.position;
            float yaw = Player.eulerAngles.y;
            float pitch = 0f;
            if (PitchPivot != null) pitch = Mathf.DeltaAngle(0f, PitchPivot.localEulerAngles.x);

            NetVehicle veh = null;
            byte seat = 0;
            if (PassengerOf != null) { veh = PassengerOf; seat = 2; }
            else
            {
                veh = VehicleSync.FindByChild(Player);
                if (veh != null) seat = 1;
            }

            if (veh != null && veh.Root != null)
            {
                Transform r = veh.Root;
                s.Vehicle = veh.Id;
                s.Seat = seat;
                s.Position = r.InverseTransformPoint(feet);
                s.Head = r.InverseTransformPoint(head);
                s.Yaw = Mathf.DeltaAngle(0f, (Quaternion.Inverse(r.rotation) * View.transform.rotation).eulerAngles.y);
                s.Pitch = Mathf.DeltaAngle(0f, (Quaternion.Inverse(r.rotation) * View.transform.rotation).eulerAngles.x);
                s.Velocity = veh.Body != null ? veh.Body.velocity : Vector3.zero;
                s.Body = Quaternion.Inverse(r.rotation) * Player.rotation; // how the body really sits in it
            }
            else if (Platform != null && Platform.Root != null)
            {
                // Standing on a vehicle (boat, van back, trailer): everything relative to it, so
                // everyone sees us move with it.
                Transform r = Platform.Root;
                s.Vehicle = Platform.Id;
                s.Seat = 0;
                s.Position = r.InverseTransformPoint(feet);
                s.Head = r.InverseTransformPoint(head);
                s.Body = Quaternion.Inverse(r.rotation) * Player.rotation;
                s.Yaw = Mathf.DeltaAngle(0f, s.Body.eulerAngles.y);
                s.Pitch = pitch;
                s.Velocity = Quaternion.Inverse(r.rotation) * velocity; // walking on the deck (being carried isn't counted)
                float eye = head.y - feet.y;
                if (standingEye < 0f || eye > standingEye) standingEye = Mathf.Min(eye, 2.2f);
                s.Crouch = Mathf.Clamp01((standingEye - eye - 0.12f) / 0.7f);
            }
            else
            {
                s.Position = feet;
                s.Head = head;
                s.Yaw = yaw;
                s.Pitch = pitch;
                s.Velocity = velocity;
                float eye = head.y - feet.y;
                if (standingEye < 0f || eye > standingEye) standingEye = Mathf.Min(eye, 2.2f);
                // A dead zone so head bob and slopes never read as crouching.
                s.Crouch = Mathf.Clamp01((standingEye - eye - 0.12f) / 0.7f);
            }

            byte flags = 0;
            Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
            if (flat.magnitude > 3.6f) flags |= PlayerSnapshot.FlagRunning;
            if (InMenu) flags |= PlayerSnapshot.FlagInMenu;
            if (Controller != null && Controller.enabled && Controller.isGrounded) flags |= PlayerSnapshot.FlagGrounded;
            if (sleeps == null) sleeps = GameUtil.GlobalBool("PlayerSleeps");
            if (sleeps != null && sleeps.Value) flags |= PlayerSnapshot.FlagSleeping;
            s.Flags = flags;

            GameObject held = HeldObject;
            s.Held = held != null ? ObjectSync.IdOf(held) : 0u;
            return s;
        }

        static FsmBool sleeps;
        static FsmFloat[] needs;

        /// <summary>Your needs as the game has them right now.</summary>
        public static PlayerStatus ReadStatus()
        {
            PlayerStatus s = new PlayerStatus();
            if (needs == null)
            {
                needs = new FsmFloat[PlayerStatus.GlobalNames.Length];
                for (int i = 0; i < needs.Length; i++) needs[i] = GameUtil.GlobalFloat(PlayerStatus.GlobalNames[i]);
            }
            for (int i = 0; i < needs.Length; i++) if (needs[i] != null) s[i] = needs[i].Value;
            return s;
        }

        /// <summary>Puts your needs back (after rejoining someone's world).</summary>
        public static void ApplyStatus(PlayerStatus s)
        {
            ReadStatus(); // makes sure the variables are found
            for (int i = 0; i < needs.Length; i++)
                if (needs[i] != null) needs[i].Value = Mathf.Clamp(s[i], 0f, i == 6 ? 10f : 100f);
        }

        /// <summary>Moves you to a place on foot (on the ground there, facing <paramref name="yaw"/>).</summary>
        public static bool Teleport(Vector3 feet, float yaw)
        {
            if (!Bound) return false;
            if (PassengerOf != null || !string.IsNullOrEmpty(CurrentVehicleName))
            {
                Notifications.Show("Get out of the vehicle first.");
                return false;
            }
            Vector3 ground = GameUtil.GroundPoint(feet);
            bool was = Controller != null && Controller.enabled;
            if (Controller != null) Controller.enabled = false;
            // The PLAYER object's position is its centre; keep the same height above the feet.
            Player.position = ground + (Player.position - Feet) + Vector3.up * 0.05f;
            Player.rotation = Quaternion.Euler(0f, yaw, 0f);
            if (Controller != null) Controller.enabled = was;
            lastPos = Player.position;
            velocity = Vector3.zero;
            return true;
        }

        // ------------------------------------------------------------ input blocking (chat/menu)

        static bool[] savedLooks = new bool[0];
        static bool savedInputEnabled = true, savedInMenu;

        /// <summary>Freezes looking/walking while our menu or chat is open, and puts back exactly
        /// what was there before (the game's own menus are left alone).</summary>
        public static void SetInputBlocked(bool blocked)
        {
            if (blocked == inputBlocked) return;
            inputBlocked = blocked;
            try
            {
                if (blocked)
                {
                    savedLooks = new bool[mouseLooks.Length];
                    for (int i = 0; i < mouseLooks.Length; i++)
                    {
                        savedLooks[i] = mouseLooks[i] != null && mouseLooks[i].enabled;
                        if (mouseLooks[i] != null) mouseLooks[i].enabled = false;
                    }
                    savedInputEnabled = input != null && input.enabled;
                    if (input != null) input.enabled = false;
                    savedInMenu = inMenu != null && inMenu.Value;
                    if (inMenu != null) inMenu.Value = true;
                }
                else
                {
                    for (int i = 0; i < mouseLooks.Length && i < savedLooks.Length; i++)
                        if (mouseLooks[i] != null) mouseLooks[i].enabled = savedLooks[i];
                    if (input != null) input.enabled = savedInputEnabled && PassengerOf == null;
                    if (inMenu != null) inMenu.Value = savedInMenu;
                }
            }
            catch (Exception e) { Log.ExceptionOnce("SetInputBlocked", e); }
        }

        public static bool InputBlocked { get { return inputBlocked; } }

        // ------------------------------------------------------------ passenger seat

        static void UpdatePassenger()
        {
            if (!Session.Active || !MP.InGame) return;
            if (passengerCooldown > 0f) passengerCooldown -= Time.deltaTime;

            if (PassengerOf != null)
            {
                bool lost = PassengerOf.Root == null || !Session.Active;
                if (lost || (passengerCooldown <= 0f && !inputBlocked && cInput.GetKeyDown("DrivingMode")))
                {
                    ExitPassenger(true);
                }
                return;
            }

            NetVehicle near = null;
            if (!inputBlocked && HeldObject == null && string.IsNullOrEmpty(CurrentVehicleName))
                near = VehicleSync.PassengerSeatNear(Feet, View.transform.position);
            if (near == null)
            {
                if (promptShown) { GameUtil.SetInteractionText(""); promptShown = false; }
                return;
            }
            GameUtil.SetInteractionText("RIDE AS PASSENGER");
            promptShown = true;
            if (passengerCooldown <= 0f && cInput.GetKeyDown("DrivingMode"))
            {
                passengerCooldown = 1f;
                VehicleSync.RequestPassengerSeat(near);
            }
        }

        static bool promptShown;

        /// <summary>Called by VehicleSync when the host has given us the passenger seat.</summary>
        public static void EnterPassenger(NetVehicle v)
        {
            if (v == null || v.Root == null || PassengerOf != null) return;
            try
            {
                PassengerOf = v;
                passengerCooldown = 1f;
                if (Controller != null) { savedController = Controller.enabled; Controller.enabled = false; }
                if (motor != null) { savedMotor = motor.enabled; motor.enabled = false; }
                if (input != null) { savedInput = input.enabled; input.enabled = false; }

                if (passengerAnchor == null)
                {
                    GameObject a = new GameObject("MSCMP_PassengerAnchor");
                    passengerAnchor = a.transform;
                }
                passengerAnchor.SetParent(v.Root, false);
                passengerAnchor.localPosition = v.PassengerHeadLocal;
                passengerAnchor.localRotation = Quaternion.identity;

                Player.SetParent(v.Root, true);
                Player.localRotation = Quaternion.identity;
                HoldPassengerPose();
                GameUtil.SetInteractionText("");
                promptShown = false;
                Notifications.Show("Riding as a passenger. Press your Driving Mode key to get out.");
            }
            catch (Exception e)
            {
                Log.Exception("Entering passenger seat", e);
                ExitPassenger(true);
            }
        }

        static void HoldPassengerPose()
        {
            if (PassengerOf == null || PassengerOf.Root == null || passengerAnchor == null) return;
            if (Player.parent != PassengerOf.Root) Player.SetParent(PassengerOf.Root, true);
            // Put the player so the camera sits at the seat's head position.
            Vector3 camOffsetWorld = View.transform.position - Player.position;
            Player.position = passengerAnchor.position - camOffsetWorld;
        }

        public static void ExitPassenger(bool notifyHost)
        {
            NetVehicle v = PassengerOf;
            PassengerOf = null;
            try
            {
                if (Player != null)
                {
                    Player.SetParent(null, true);
                    Vector3 exit = Player.position;
                    if (v != null && v.Root != null) exit = v.PassengerExitWorld();
                    Player.position = exit;
                    Vector3 e = Player.eulerAngles;
                    Player.rotation = Quaternion.Euler(0f, e.y, 0f);
                }
                if (Controller != null) Controller.enabled = savedController;
                if (motor != null) motor.enabled = savedMotor;
                if (input != null) input.enabled = savedInput && !inputBlocked;
            }
            catch (Exception e) { Log.Exception("Leaving passenger seat", e); }
            if (passengerAnchor != null) { UnityEngine.Object.Destroy(passengerAnchor.gameObject); passengerAnchor = null; }
            passengerCooldown = 1f;
            if (notifyHost && v != null) VehicleSync.LeavePassengerSeat(v);
        }
    }
}
