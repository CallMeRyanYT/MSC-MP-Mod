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
        float gestureUntil;
        string gestureClip;
        Vector3 restPelvisLocal;
        BoneGuard[] guards = new BoneGuard[0];
        float lastYaw, yawRate;
        Vector3 reachTarget;
        float reachStart, reachUntil;
        float groundOffset;
        bool groundCalibrated;
        Vector3 smoothVelocity;

        public const float InterpDelay = 0.12f;

        public Vector3 HeadPosition
        {
            get { return Rig != null && Rig.Head != null ? Rig.Head.position : (Object != null ? Object.transform.position + Vector3.up * 1.7f : Vector3.zero); }
        }

        public bool Visible { get { return Object != null && Object.activeSelf; } }

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
                new BoneGuard(Rig.ShoulderLeft), new BoneGuard(Rig.ShoulderRight)
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

        public void PlayGesture(byte gesture)
        {
            if (Rig == null || Rig.RightArmAnim == null) return;
            string clip = gesture == 1 ? AvatarFactory.Clips.Wave : null;
            if (clip == null || Rig.RightArmAnim[clip] == null) return;
            gestureClip = clip;
            AnimationState st = Rig.RightArmAnim[clip];
            st.wrapMode = WrapMode.Once;
            st.time = 0f;
            Rig.RightArmAnim.CrossFade(clip, 0.15f);
            gestureUntil = Time.time + Mathf.Max(0.5f, st.length);
            rightClip = clip;
        }

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

            if (veh != null && veh.Root != null)
            {
                SetPose(Pose.Seated);
                t.rotation = veh.Root.rotation;
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
                if (Rig.Collider != null) Rig.Collider.enabled = true;
                t.position = current.Position + Vector3.up * groundOffset;
                t.rotation = Quaternion.Euler(0f, current.Yaw, 0f);
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
            yawRate = Mathf.Lerp(yawRate, Mathf.DeltaAngle(lastYaw, current.Yaw) / dt, Mathf.Clamp01(dt * 10f));
            lastYaw = current.Yaw;

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
            if (Time.time >= gestureUntil)
            {
                gestureClip = null;
                Play(Rig.RightArmAnim, ref rightClip, right, carrying ? 1f : Mathf.Abs(legSpeed));
            }
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
            if (pose == Pose.Standing)
            {
                if (legsClip == AvatarFactory.Clips.Run && Rig.Pelvis != null)
                    Rig.Pelvis.localPosition = restPelvisLocal; // the run clip is from a child's body
                float bend = current.Crouch;
                bool airborne = (current.Flags & PlayerSnapshot.FlagGrounded) == 0 && Mathf.Abs(current.Velocity.y) > 1.2f;
                if (airborne) bend = Mathf.Max(bend, 0.25f); // tuck the legs while jumping or falling
                if (bend > 0.02f) ApplyCrouch(bend);
                CalibrateGround();
                AimHead(0f, current.Pitch, t);
                ApplyReach();
            }
            else if (pose == Pose.Seated)
            {
                NetVehicle veh = VehicleSync.ById(current.Vehicle);
                if (veh != null && veh.Root != null && Rig.Head != null)
                {
                    if (current.Seat == 1) ReachForWheel(t);
                    // Head looks where the player looks (relative to the vehicle).
                    AimHead(Mathf.Clamp(current.Yaw, -100f, 100f), current.Pitch, t);
                    // Move the whole body so the head sits exactly where their camera is.
                    Vector3 target = veh.Root.TransformPoint(current.Head) - t.up * 0.08f;
                    t.position += target - Rig.Head.position;
                }
            }
        }

        void AimHead(float yaw, float pitch, Transform body)
        {
            Transform head = Rig.HeadPivot ?? Rig.Head;
            if (head == null) return;
            float p = Mathf.Clamp(pitch, -70f, 70f) * 0.75f;
            head.rotation = Quaternion.AngleAxis(yaw * 0.8f, body.up) * Quaternion.AngleAxis(p, body.right) * head.rotation;
            if (Rig.SpineUpper != null)
                Rig.SpineUpper.rotation = Quaternion.AngleAxis(yaw * 0.2f, body.up) * Quaternion.AngleAxis(Mathf.Clamp(pitch, -70f, 70f) * 0.2f, body.right) * Rig.SpineUpper.rotation;
        }

        /// <summary>Crouching built from world-space joint rotations, independent of how the
        /// game's skeleton axes are set up: thighs forward, knees back, feet flat, lean in.</summary>
        void ApplyCrouch(float c)
        {
            Transform t = Object.transform;
            Vector3 right = t.right;
            Rotate(Rig.ThighLeft, -80f * c, right);
            Rotate(Rig.ThighRight, -80f * c, right);
            Rotate(Rig.KneeLeft, 130f * c, right);
            Rotate(Rig.KneeRight, 130f * c, right);
            Rotate(Rig.AnkleLeft, -50f * c, right);
            Rotate(Rig.AnkleRight, -50f * c, right);
            Rotate(Rig.SpineMiddle, 25f * c, right);
            // Keep the feet on the ground by lowering the whole skeleton (the legs hang from it,
            // not from the pelvis bone). Clamped, so a strange rig can never fly off.
            if (Rig.AnkleLeft != null && Rig.AnkleRight != null && Rig.Skeleton != null)
            {
                float ankle = Mathf.Min(Rig.AnkleLeft.position.y, Rig.AnkleRight.position.y);
                float drop = Mathf.Clamp(ankle - (t.position.y + 0.09f + groundOffset), 0f, 1.1f);
                Rig.Skeleton.position -= Vector3.up * drop;
            }
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
            return r;
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
}
