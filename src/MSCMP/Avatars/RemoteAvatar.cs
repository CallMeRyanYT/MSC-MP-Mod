using MSCMP.Game;
using MSCMP.Net;
using MSCMP.Sync;
using UnityEngine;

namespace MSCMP.Avatars
{
    /// <summary>
    /// Drives another player's body: smooth movement from network snapshots, the game's
    /// walking/standing/carrying/sitting animations, head looking where they look,
    /// crouching, and sitting in vehicles.
    /// </summary>
    internal sealed class RemoteAvatar
    {
        public NetPlayer Player;
        public GameObject Object;
        public AvatarRig Rig;
        Appearance builtLook;

        readonly SnapshotBuffer<PlayerSnapshot> buffer = new SnapshotBuffer<PlayerSnapshot>(20);
        PlayerSnapshot current;
        bool hasCurrent;

        enum Pose { None, Standing, Seated }
        Pose pose = Pose.None;
        string legsClip, leftClip, rightClip;
        Vector3 restPelvisLocal;
        BoneGuard[] guards = new BoneGuard[0];
        float lastYaw, yawRate, lastYawTime = -1f;
        Vector3 reachTarget;
        float reachStart, reachUntil;
        float groundOffset;
        bool groundCalibrated;
        Vector3 smoothVelocity;

        public const float InterpDelay = 0.085f;   // 30 Hz updates + room for a late packet

        public Vector3 HeadPosition
        {
            get { return Rig != null && Rig.Head != null ? Rig.Head.position : (Object != null ? Object.transform.position + Vector3.up * 1.7f : Vector3.zero); }
        }

        public bool Visible { get { return Object != null && Object.activeSelf; } }

        /// <summary>Numbers that show at a glance whether the body is shaped right (for the log).</summary>
        public string Diagnostics()
        {
            if (Object == null || Rig == null) return "no body";
            Transform t = Object.transform;
            string r = "pose " + pose + ", legs " + (legsClip ?? "-") + ", right arm " + (rightClip ?? "-");
            if (Rig.Body != null)
            {
                Bounds b = Rig.Body.bounds;
                r += ", mesh height " + b.size.y.ToString("F2") + " m (bottom " + (b.min.y - t.position.y).ToString("F2") + ")";
            }
            if (Rig.Head != null) r += ", head " + (Rig.Head.position.y - t.position.y).ToString("F2") + " m above feet";
            if (Rig.AnkleLeft != null) r += ", ankle " + (Rig.AnkleLeft.position.y - t.position.y).ToString("F2");
            r += ", crouch " + current.Crouch.ToString("F2") + ", ground offset " + groundOffset.ToString("F2");
            return r;
        }

        /// <summary>True if the body looks broken (stretched, sunk or floating).</summary>
        public bool LooksBroken()
        {
            if (Object == null || Rig == null || Rig.Body == null || !Object.activeSelf || pose != Pose.Standing) return false;
            Bounds b = Rig.Body.bounds;
            float bottom = b.min.y - Object.transform.position.y;
            return b.size.y > 2.6f || b.size.y < 0.6f || Mathf.Abs(bottom) > 0.5f;
        }

        public static RemoteAvatar Create(NetPlayer p)
        {
            RemoteAvatar a = new RemoteAvatar();
            a.Player = p;
            a.Rebuild(p.Look);
            return a;
        }

        public void Rebuild(Appearance look)
        {
            Vector3 pos = Object != null ? Object.transform.position : Vector3.zero;
            Quaternion rot = Object != null ? Object.transform.rotation : Quaternion.identity;
            bool active = Object != null && Object.activeSelf;
            Destroy();
            AvatarRig rig;
            Object = AvatarFactory.Build(look, out rig);
            Rig = rig;
            builtLook = look;
            pose = Pose.None;
            groundCalibrated = false;
            if (Object == null) return;
            Object.name = "MSCMP_Player_" + Player.Name;
            Object.transform.position = pos;
            Object.transform.rotation = rot;
            restPelvisLocal = Rig.Pelvis.localPosition;
            guards = new BoneGuard[]
            {
                new BoneGuard(Rig.Skeleton), new BoneGuard(Rig.Pelvis), new BoneGuard(Rig.SpineMiddle), new BoneGuard(Rig.SpineUpper),
                new BoneGuard(Rig.HeadPivot), new BoneGuard(Rig.Head), new BoneGuard(Rig.ThighLeft), new BoneGuard(Rig.ThighRight),
                new BoneGuard(Rig.KneeLeft), new BoneGuard(Rig.KneeRight), new BoneGuard(Rig.AnkleLeft), new BoneGuard(Rig.AnkleRight),
                new BoneGuard(Rig.ShoulderLeft), new BoneGuard(Rig.ShoulderRight),
                new BoneGuard(Rig.CollarLeft), new BoneGuard(Rig.CollarRight)
            };
            Object.SetActive(active || hasCurrent);
        }

        public void Destroy()
        {
            if (Object != null) UnityEngine.Object.Destroy(Object);
            Object = null;
            Rig = null;
        }

        public void Push(PlayerSnapshot s)
        {
            buffer.Add(Player.ToLocalTime(s.Time), s);
        }

        bool Sleeping { get { return (current.Flags & PlayerSnapshot.FlagSleeping) != 0; } }

        float knockedUntil;
        bool Knocked { get { return Time.time < knockedUntil; } }
        public bool IsKnocked { get { return Knocked; } }
        bool Carried { get { return (current.Flags & PlayerSnapshot.FlagCarried) != 0; } }

        /// <summary>Hit by a car: lies on the ground for a moment.</summary>
        public void Knock(float seconds) { knockedUntil = Time.time + seconds; }

        /// <summary>How drunk they look, 0..1.</summary>
        float Drunk01 { get { return Player != null && Player.HasStatus ? Mathf.Clamp01(Player.Status.Drunk / 2.5f) : 0f; } }

        // ------------------------------------------------------------ per frame

        public void Update()
        {
            if (Object == null) return;
            bool inWorld = Player.Phase == PlayerPhase.Playing || Player.Phase == PlayerPhase.Syncing;
            float renderTime = Time.realtimeSinceStartup - InterpDelay;

            PlayerSnapshot a, b;
            float alpha, over;
            if (!inWorld || !buffer.Sample(renderTime, out a, out b, out alpha, out over))
            {
                if (Object.activeSelf) Object.SetActive(false);
                return;
            }
            if (!Object.activeSelf) Object.SetActive(true);

            current = Lerp(a, b, alpha);
            hasCurrent = true;
            if (over > 0f && over < 0.35f && current.Vehicle == 0)
                current.Position += current.Velocity * over; // short extrapolation through packet gaps

            NetVehicle veh = current.Vehicle != 0 ? VehicleSync.ById(current.Vehicle) : null;
            Transform t = Object.transform;

            if (current.StandingOnVehicle)
            {
                // Standing on a boat deck / in the van's back: turn their place on the vehicle
                // into a place in the world, so they move with it. Then pose as usual.
                if (veh == null || veh.Root == null) { if (Object.activeSelf) Object.SetActive(false); return; }
                Transform r = veh.Root;
                current.Position = r.TransformPoint(current.Position);
                current.Head = r.TransformPoint(current.Head);
                current.Yaw = (r.rotation * current.Body).eulerAngles.y;
                current.Velocity = r.rotation * current.Velocity;
                current.Vehicle = 0;
                current.Seat = 0;
                veh = null;
            }

            if (veh != null && veh.Root != null)
            {
                SetPose(Pose.Seated);
                t.rotation = SeatRotation(veh); // the way the seat faces; only the head follows their view
                // Rough placement; LateUpdate aligns the head exactly with where they look from.
                t.position = veh.Root.TransformPoint(current.Position);
                if (Rig.Collider != null) Rig.Collider.enabled = false;
                string seatClip = veh.UsesVanSeat ? AvatarFactory.Clips.SitVan : AvatarFactory.Clips.SitCar;
                if (Rig.SeatAnim != null && Rig.SeatAnim[seatClip] != null && !Rig.SeatAnim.IsPlaying(seatClip))
                    Rig.SeatAnim.CrossFade(seatClip, 0.2f);
            }
            else
            {
                SetPose(Pose.Standing);
                if (Sleeping)
                {
                    // Lying on their back, head where their camera is (in bed).
                    if (Rig.Collider != null) Rig.Collider.enabled = false;
                    t.rotation = Quaternion.Euler(0f, current.Yaw, 0f) * Quaternion.Euler(-90f, 0f, 0f);
                    t.position = current.Head - t.up * 1.55f - Vector3.up * 0.12f;
                    UpdateWalkAnimation();
                    return;
                }
                if (Carried)
                {
                    // Over somebody's shoulder, face down, across them.
                    if (Rig.Collider != null) Rig.Collider.enabled = false;
                    t.rotation = Quaternion.Euler(0f, current.Yaw + 90f, 0f) * Quaternion.Euler(90f, 0f, 0f);
                    t.position = current.Position - t.up * 0.95f;
                    UpdateWalkAnimation();
                    return;
                }
                if (Knocked)
                {
                    if (Rig.Collider != null) Rig.Collider.enabled = false;
                    t.rotation = Quaternion.Euler(0f, current.Yaw, 0f) * Quaternion.Euler(-90f, 0f, 0f);
                    t.position = current.Position + Vector3.up * 0.18f;
                    UpdateWalkAnimation();
                    return;
                }
                if (Rig.Collider != null) Rig.Collider.enabled = true;
                t.position = current.Position + Vector3.up * groundOffset;
                float d = Drunk01, ph = Time.time * 1.3f;
                // Drunk: the whole body sways a little.
                t.rotation = Quaternion.Euler(0f, current.Yaw + Mathf.Sin(ph * 0.7f) * 6f * d, 0f) * Quaternion.Euler(Mathf.Sin(ph * 1.1f) * 3f * d, 0f, Mathf.Sin(ph) * 5f * d);
                UpdateWalkAnimation();
            }
        }

        void UpdateWalkAnimation()
        {
            smoothVelocity = Vector3.Lerp(smoothVelocity, current.Velocity, Mathf.Clamp01(Time.deltaTime * 8f));
            Vector3 flat = new Vector3(smoothVelocity.x, 0f, smoothVelocity.z);
            float speed = flat.magnitude;
            bool carrying = current.Held != 0;
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            // Start fresh after spawning, leaving a car or being hidden, so the jump from the old
            // heading never reads as a fast turn.
            if (lastYawTime < 0f || Time.time - lastYawTime > 0.25f) { lastYaw = current.Yaw; yawRate = 0f; }
            yawRate = Mathf.Lerp(yawRate, Mathf.DeltaAngle(lastYaw, current.Yaw) / dt, Mathf.Clamp01(dt * 10f));
            lastYaw = current.Yaw;
            lastYawTime = Time.time;

            string legs, left, right;
            float legSpeed = 1f;
            if (speed < 0.25f && Mathf.Abs(yawRate) > 50f && Rig.LegsAnim != null && Rig.LegsAnim[AvatarFactory.Clips.Walk] != null)
            {
                // Turning on the spot: small steps.
                legs = AvatarFactory.Clips.Walk; left = AvatarFactory.Clips.LeftIdle; right = AvatarFactory.Clips.RightIdle;
                legSpeed = 0.6f;
            }
            else if (speed < 0.25f)
            {
                legs = AvatarFactory.Clips.Stand; left = AvatarFactory.Clips.LeftIdle; right = AvatarFactory.Clips.RightIdle;
            }
            else if (speed < 3.6f || Rig.LegsAnim == null || Rig.LegsAnim[AvatarFactory.Clips.Run] == null)
            {
                legs = AvatarFactory.Clips.Walk; left = AvatarFactory.Clips.LeftWalk; right = AvatarFactory.Clips.RightWalk;
                legSpeed = Mathf.Clamp(speed / 1.35f, 0.5f, 3.2f);
            }
            else
            {
                legs = AvatarFactory.Clips.Run; left = AvatarFactory.Clips.LeftRun; right = AvatarFactory.Clips.RightRun;
                legSpeed = Mathf.Clamp(speed / 5.5f, 0.75f, 1.6f);
            }
            // Walking backwards plays the walk backwards.
            Vector3 fwd = Object.transform.forward;
            if (speed >= 0.25f && Vector3.Dot(flat, fwd) < -0.3f * speed) legSpeed = -legSpeed;
            if (carrying) right = AvatarFactory.Clips.Carry;

            Play(Rig.LegsAnim, ref legsClip, legs, legSpeed);
            Play(Rig.LeftArmAnim, ref leftClip, left, Mathf.Abs(legSpeed));
            Play(Rig.RightArmAnim, ref rightClip, right, carrying ? 1f : Mathf.Abs(legSpeed));
            if (Rig.BreathAnim != null && Rig.BreathAnim[AvatarFactory.Clips.Breath] != null && !Rig.BreathAnim.isPlaying)
                Rig.BreathAnim.Play(AvatarFactory.Clips.Breath);
        }

        static void Play(Animation anim, ref string currentClip, string clip, float speed)
        {
            if (anim == null) return;
            AnimationState st = anim[clip];
            if (st == null) return;
            st.speed = speed;
            if (currentClip != clip || !anim.IsPlaying(clip))
            {
                st.wrapMode = WrapMode.Loop;
                anim.CrossFade(clip, 0.25f);
                currentClip = clip;
            }
        }

        void SetPose(Pose p)
        {
            if (pose == p) return;
            pose = p;
            if (Rig == null) return;
            Rig.ResetPose();
            bool seated = p == Pose.Seated;
            SetAnimEnabled(Rig.LegsAnim, !seated);
            SetAnimEnabled(Rig.BreathAnim, !seated);
            SetAnimEnabled(Rig.LeftArmAnim, !seated);
            SetAnimEnabled(Rig.RightArmAnim, !seated);
            SetAnimEnabled(Rig.SeatAnim, seated);
            legsClip = leftClip = rightClip = null;
        }

        static void SetAnimEnabled(Animation a, bool on)
        {
            if (a == null) return;
            if (!on) a.Stop();
            a.enabled = on;
        }

        /// <summary>After the animations have been sampled: aim the head, crouch, sit exactly.</summary>
        public void LateUpdate()
        {
            if (Object == null || !Object.activeSelf || !hasCurrent || Rig == null) return;
            Transform t = Object.transform;
            // Undo last frame's adjustments on bones no animation is driving, so they never add up.
            for (int i = 0; i < guards.Length; i++) guards[i].Begin();
            try { PoseBones(t); }
            finally { for (int i = 0; i < guards.Length; i++) guards[i].End(); }
        }

        void PoseBones(Transform t)
        {
            if (pose == Pose.Standing && (Sleeping || Knocked || Carried)) return; // lying down: the plain pose is right
            if (pose == Pose.Standing)
            {
                if (legsClip == AvatarFactory.Clips.Run && Rig.Pelvis != null)
                    Rig.Pelvis.localPosition = restPelvisLocal; // the run clip is from a child's body
                float groundY = t.position.y - groundOffset;   // where their feet really are
                float bend = current.Crouch;
                bool airborne = (current.Flags & PlayerSnapshot.FlagGrounded) == 0 && Mathf.Abs(current.Velocity.y) > 1.2f;
                if (airborne) bend = Mathf.Max(bend, 0.25f); // tuck the legs while jumping or falling
                float torsoLean = 0f;
                if (bend > 0.02f) torsoLean = ApplyCrouch(bend, t, groundY);
                else KeepFeetOnGround(groundY);
                CalibrateGround();

                // Leaning: bend the spine so the head sits where their camera is.
                Vector3 local = Quaternion.Inverse(Quaternion.Euler(0f, current.Yaw, 0f)) * (current.Head - current.Position);
                float side = DeadZone(local.x, 0.04f);
                float forward = DeadZone(local.z, 0.12f);
                float sideAngle = Mathf.Clamp(Mathf.Atan2(side, 1.2f) * Mathf.Rad2Deg * 1.3f, -35f, 35f);
                float fwdAngle = Mathf.Clamp(Mathf.Atan2(forward, 1.2f) * Mathf.Rad2Deg * 1.3f, -25f, 40f);
                Rotate(Rig.SpineMiddle, -sideAngle * 0.5f, t.forward);
                Rotate(Rig.SpineUpper, -sideAngle * 0.5f, t.forward);
                Rotate(Rig.SpineMiddle, fwdAngle * 0.5f, t.right);
                Rotate(Rig.SpineUpper, fwdAngle * 0.5f, t.right);
                Transform head = Rig.HeadPivot ?? Rig.Head;
                Rotate(head, sideAngle * 0.4f, t.forward); // keep the eyes roughly level

                // The head leads into turns, and follows the camera up and down.
                headLead = Mathf.Lerp(headLead, Mathf.Clamp(yawRate * 0.12f, -40f, 40f), Mathf.Clamp01(Time.deltaTime * 8f));
                AimHead(headLead, current.Pitch, t, torsoLean + fwdAngle);
                float d = Drunk01;
                if (d > 0.01f)
                {
                    float ph = Time.time * 1.3f;
                    Rotate(Rig.SpineMiddle, Mathf.Sin(ph) * 8f * d, t.forward);
                    Rotate(Rig.SpineUpper, Mathf.Sin(ph * 1.3f + 1f) * 6f * d, t.forward);
                    Rotate(head, Mathf.Sin(ph * 0.8f + 2f) * 12f * d, t.forward);
                }
                ApplyReach();
            }
            else if (pose == Pose.Seated)
            {
                NetVehicle veh = VehicleSync.ById(current.Vehicle);
                if (veh != null && veh.Root != null && Rig.Head != null)
                {
                    if (current.Seat == 1) ReachForWheel(t);
                    if (veh.IsBike) Straddle(t);
                    // Head looks where the player looks (relative to how the body sits).
                    float bodyYaw = (Quaternion.Inverse(veh.Root.rotation) * t.rotation).eulerAngles.y;
                    float look = Mathf.DeltaAngle(bodyYaw, current.Yaw);
                    AimHead(Mathf.Clamp(look, -100f, 100f), current.Pitch, t, 0f);
                    // Move the whole body so the head sits exactly where their camera is.
                    Vector3 target = veh.Root.TransformPoint(current.Head) - t.up * 0.08f;
                    t.position += target - Rig.Head.position;
                }
            }
        }

        float headLead;

        /// <summary>Which way a seat faces. The driver's camera point of every vehicle faces
        /// forward along the seat (also on the moped, whose model is turned); the player's own
        /// rotation can't be used, because the game turns it when you look around while driving.</summary>
        static Quaternion SeatRotation(NetVehicle v)
        {
            Transform root = v.Root;
            Transform pivot = v.DriverHead;
            if (pivot == null) return root.rotation;
            Vector3 up = Vector3.Dot(root.up, Vector3.up) > 0.5f ? root.up : pivot.up;
            Vector3 fwd = Vector3.ProjectOnPlane(pivot.forward, up);
            if (fwd.sqrMagnitude < 1e-4f) return root.rotation;
            return Quaternion.LookRotation(fwd.normalized, up);
        }

        static float DeadZone(float v, float zone)
        {
            if (v > zone) return v - zone;
            if (v < -zone) return v + zone;
            return 0f;
        }

        /// <summary>On the moped: knees apart around the tank, leaning a little into the bars.</summary>
        void Straddle(Transform t)
        {
            Rotate(Rig.ThighLeft, -14f, t.forward);
            Rotate(Rig.ThighRight, 14f, t.forward);
            Rotate(Rig.SpineMiddle, 10f, t.right);
            Rotate(Rig.ShoulderLeft, -20f, t.right);
            Rotate(Rig.ShoulderRight, -20f, t.right);
        }

        /// <summary>Turns the head (and some of the neck and back) to where the camera looks.
        /// <paramref name="torsoLean"/> is how far the back already leans forward, so the eyes
        /// still end up looking where the player looks.</summary>
        void AimHead(float yaw, float pitch, Transform body, float torsoLean)
        {
            Transform head = Rig.HeadPivot ?? Rig.Head;
            if (head == null) return;
            float p = Mathf.Clamp(pitch, -75f, 80f);
            if (Rig.SpineMiddle != null)
                Rig.SpineMiddle.rotation = Quaternion.AngleAxis(p * 0.15f, body.right) * Rig.SpineMiddle.rotation;
            if (Rig.SpineUpper != null)
                Rig.SpineUpper.rotation = Quaternion.AngleAxis(yaw * 0.25f, body.up) * Quaternion.AngleAxis(p * 0.3f, body.right) * Rig.SpineUpper.rotation;
            float headPitch = Mathf.Clamp(p * 0.55f - torsoLean, -70f, 70f);
            head.rotation = Quaternion.AngleAxis(yaw * 0.75f, body.up) * Quaternion.AngleAxis(headPitch, body.right) * head.rotation;
        }

        /// <summary>Crouching built from world-space joint rotations, independent of how the
        /// game's skeleton axes are set up: knees forward, hips back over planted feet, back
        /// leaning in for balance, arms a little forward. Returns the forward lean of the back.</summary>
        float ApplyCrouch(float c, Transform t, float groundY)
        {
            Vector3 right = t.right;
            bool feet = Rig.AnkleLeft != null && Rig.AnkleRight != null && Rig.Skeleton != null;
            Vector3 feetBefore = feet ? (Rig.AnkleLeft.position + Rig.AnkleRight.position) * 0.5f : Vector3.zero;
            Rotate(Rig.ThighLeft, -90f * c, right);
            Rotate(Rig.ThighRight, -90f * c, right);
            Rotate(Rig.KneeLeft, 135f * c, right);
            Rotate(Rig.KneeRight, 135f * c, right);
            Rotate(Rig.AnkleLeft, -45f * c, right);
            Rotate(Rig.AnkleRight, -45f * c, right);
            float lean = 32f * c;
            Rotate(Rig.SpineMiddle, lean * 0.6f, right);
            Rotate(Rig.SpineUpper, lean * 0.4f, right);
            Rotate(Rig.ShoulderLeft, -22f * c, right);
            if (current.Held == 0) Rotate(Rig.ShoulderRight, -22f * c, right);
            if (feet)
            {
                // Feet stay where they were (the hips go back), then the body comes down until
                // they touch the ground. Clamped, so a strange rig can never fly off.
                Vector3 after = (Rig.AnkleLeft.position + Rig.AnkleRight.position) * 0.5f;
                Vector3 shift = feetBefore - after;
                shift.y = 0f;
                Rig.Skeleton.position += Vector3.ClampMagnitude(shift, 0.6f);
                float ankle = Mathf.Min(Rig.AnkleLeft.position.y, Rig.AnkleRight.position.y);
                float drop = Mathf.Clamp(ankle - (groundY + 0.09f), 0f, 1.1f);
                Rig.Skeleton.position -= Vector3.up * drop;
            }
            return lean;
        }

        /// <summary>Some animation clips come from other bodies (the run is a child's) and lift
        /// the whole skeleton off the ground. Put the lower foot back down.</summary>
        void KeepFeetOnGround(float groundY)
        {
            if (Rig.AnkleLeft == null || Rig.AnkleRight == null || Rig.Skeleton == null) return;
            float ankle = Mathf.Min(Rig.AnkleLeft.position.y, Rig.AnkleRight.position.y);
            float excess = ankle - (groundY + 0.09f);
            bool borrowedClip = legsClip == AvatarFactory.Clips.Run;
            if (borrowedClip || Mathf.Abs(excess) > 0.3f)
                Rig.Skeleton.position -= Vector3.up * Mathf.Clamp(excess, -0.4f, 1.6f);
        }

        /// <summary>Reach towards something the player just used (a door, a switch, a bolt...).</summary>
        public void Reach(Vector3 target)
        {
            reachTarget = target;
            reachStart = Time.time;
            reachUntil = Time.time + 0.8f;
        }

        void ApplyReach()
        {
            if (Time.time >= reachUntil || Rig.ShoulderRight == null || Rig.ShoulderRight.childCount == 0) return;
            float k = Mathf.Sin(Mathf.Clamp01((Time.time - reachStart) / 0.8f) * Mathf.PI); // in and out
            Transform shoulder = Rig.ShoulderRight;
            Vector3 along = shoulder.GetChild(0).position - shoulder.position;
            Vector3 want = reachTarget - shoulder.position;
            if (along.sqrMagnitude < 1e-6f || want.sqrMagnitude < 1e-6f) return;
            Quaternion full = Quaternion.FromToRotation(along, want);
            shoulder.rotation = Quaternion.Slerp(Quaternion.identity, full, k * 0.85f) * shoulder.rotation;
        }

        void ReachForWheel(Transform t)
        {
            Rotate(Rig.ShoulderLeft, -45f, t.right);
            Rotate(Rig.ShoulderRight, -45f, t.right);
        }

        static void Rotate(Transform bone, float angle, Vector3 axis)
        {
            if (bone == null) return;
            bone.rotation = Quaternion.AngleAxis(angle, axis) * bone.rotation;
        }

        /// <summary>Makes sure the feet touch the ground whatever donor body was used.</summary>
        void CalibrateGround()
        {
            if (groundCalibrated || legsClip != AvatarFactory.Clips.Stand || current.Crouch > 0.02f) return;
            if (Rig.AnkleLeft == null || Rig.AnkleRight == null) { groundCalibrated = true; return; }
            float ankle = Mathf.Min(Rig.AnkleLeft.position.y, Rig.AnkleRight.position.y) - Object.transform.position.y;
            float wanted = 0.09f;
            float fix = wanted - ankle;
            if (Mathf.Abs(fix) < 0.6f) groundOffset = fix;
            groundCalibrated = true;
            Log.Debug("Avatar ground offset " + groundOffset.ToString("F3"));
        }

        static PlayerSnapshot Lerp(PlayerSnapshot a, PlayerSnapshot b, float t)
        {
            if (a.Vehicle != b.Vehicle || t >= 1f) return t >= 0.5f ? b : a;
            PlayerSnapshot r = b;
            r.Position = Vector3.Lerp(a.Position, b.Position, t);
            r.Head = Vector3.Lerp(a.Head, b.Head, t);
            r.Velocity = Vector3.Lerp(a.Velocity, b.Velocity, t);
            r.Yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, t);
            r.Pitch = Mathf.Lerp(a.Pitch, b.Pitch, t);
            r.Crouch = Mathf.Lerp(a.Crouch, b.Crouch, t);
            r.Body = Quaternion.Slerp(a.Body, b.Body, t);
            return r;
        }
    }

    /// <summary>Remembers what we did to a bone last frame. If no animation has touched it since,
    /// put it back first; otherwise keep the animation's value. Stops adjustments piling up.</summary>
    internal sealed class BoneGuard
    {
        readonly Transform t;
        Quaternion baseRot, writtenRot;
        Vector3 basePos, writtenPos;
        bool has;

        public BoneGuard(Transform t) { this.t = t; }

        public void Begin()
        {
            if (t == null) return;
            if (has && t.localRotation == writtenRot && t.localPosition == writtenPos)
            {
                t.localRotation = baseRot;
                t.localPosition = basePos;
            }
            baseRot = t.localRotation;
            basePos = t.localPosition;
        }

        public void End()
        {
            if (t == null) return;
            writtenRot = t.localRotation;
            writtenPos = t.localPosition;
            has = true;
        }
    }
}
