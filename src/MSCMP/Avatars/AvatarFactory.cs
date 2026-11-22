using System;
using System.Collections.Generic;
using UnityEngine;

namespace MSCMP.Avatars
{
    /// <summary>
    /// Builds a remote player's body from one of the game's own NPCs: the same skinned
    /// mesh, the same clothing textures and the same animation clips the people of
    /// Peräjärvi use. Only the visual parts are copied (no AI, sounds, crime triggers),
    /// so nothing in the world reacts to the copy as if it were the NPC.
    /// </summary>
    internal static class AvatarFactory
    {
        /// <summary>Clip names by role. Several NPCs share them, so they are looked up globally.</summary>
        public static class Clips
        {
            public const string Stand = "fat_standing";
            public const string Walk = "fat_walk";
            public const string Run = "kid_run";
            public const string Breath = "fat_spine_upper_breath";
            public const string LeftIdle = "fat_handsleft_idle";
            public const string RightIdle = "fat_handsright_idle";
            public const string LeftWalk = "fat_handsleft_walk";
            public const string RightWalk = "fat_handsright_walk";
            public const string LeftRun = "kid_run_arm_L";
            public const string RightRun = "kid_run_arm_R";
            public const string Carry = "fat_handsright_carry";
            public const string Wave = "fat_waving_hello";
            public const string Point = "fat_handsleft_point";
            public const string SitCar = "fat_get_car_idle1";
            public const string SitVan = "fat_get_van_idle1";
        }

        static readonly string[] BoneSkipNames =
        {
            "RagDoll", "RagDollCar", "RagDoll2", "HumanCollider", "HumanTrigger", "HumanTriggerCrime",
            "PlayerColl", "HitCollider", "Speak", "PayMoney", "Ray",
        };

        /// <summary>Builds an avatar for the given look. Returns null if no donor NPC could be found.</summary>
        public static GameObject Build(Appearance look, out AvatarRig rig)
        {
            rig = null;
            look.Clamp();
            // Try the chosen body first, then fall back through the list.
            for (int attempt = 0; attempt < Catalog.Bodies.Length; attempt++)
            {
                int idx = (look.Body + attempt) % Catalog.Bodies.Length;
                Transform donorChar = GameUtil.FindPath(Catalog.Bodies[idx].CharPath);
                if (donorChar == null)
                {
                    Log.Info("Avatar donor not found: " + Catalog.Bodies[idx].CharPath);
                    continue;
                }
                try
                {
                    GameObject go = BuildFrom(donorChar, look, out rig);
                    if (go != null)
                    {
                        if (attempt > 0) Log.Info("Used body " + Catalog.Bodies[idx].Label + " instead of " + Catalog.Bodies[look.Body].Label);
                        return go;
                    }
                }
                catch (Exception e)
                {
                    Log.Exception("Building avatar from " + Catalog.Bodies[idx].Label, e);
                }
            }
            Log.Error("Could not build any player body - remote players will be invisible.");
            return null;
        }

        static GameObject BuildFrom(Transform donorChar, Appearance look, out AvatarRig rig)
        {
            rig = null;
            SkinnedMeshRenderer donorSmr = null;
            SkinnedMeshRenderer[] smrs = donorChar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < smrs.Length; i++)
            {
                // The ragdoll has its own copy of the body; take the live one directly under Char.
                if (smrs[i].transform.parent == donorChar && smrs[i].sharedMesh != null) { donorSmr = smrs[i]; break; }
            }
            if (donorSmr == null) return null;
            Transform donorSkeleton = donorChar.Find("skeleton");
            if (donorSkeleton == null) return null;

            // Which donor transforms are needed: every bone and its ancestors up to Char.
            HashSet<Transform> needed = new HashSet<Transform>();
            Transform[] donorBones = donorSmr.bones;
            for (int i = 0; i < donorBones.Length; i++)
            {
                for (Transform t = donorBones[i]; t != null && t != donorChar; t = t.parent) needed.Add(t);
            }
            needed.Add(donorSkeleton);
            // Keep the look-at pivot and the head even if they are not skinned bones.
            // Different NPCs name the pivot "HeadPivot", "HeadPivot 1".."HeadPivot 5", or put "head" directly under spine_upper.
            Transform donorSpineUpper = Find(donorSkeleton, "pelvis/spine_middle/spine_upper");
            Transform donorHeadPivot = FindHeadPivot(donorSpineUpper);
            Transform donorHead = FindHead(donorHeadPivot ?? donorSpineUpper);
            AddWithAncestors(needed, donorSkeleton, donorHeadPivot);
            AddWithAncestors(needed, donorSkeleton, donorHead);

            GameObject root = new GameObject("MSCMP_Player");
            Transform pivot = new GameObject("Pivot").transform;
            pivot.SetParent(root.transform, false);
            // Keep Pivot at local origin with identity rotation: some NPCs (e.g. VenttiPig sitting at
            // the poker table) offset their donor Pivot downwards by -0.6 m, which would sink them.
            pivot.localPosition = Vector3.zero;
            pivot.localRotation = Quaternion.identity;

            Transform charCopy = new GameObject("Char").transform;
            charCopy.SetParent(pivot, false);
            charCopy.localPosition = donorChar.localPosition;
            charCopy.localRotation = donorChar.localRotation;
            charCopy.localScale = donorChar.localScale;

            Dictionary<Transform, Transform> map = new Dictionary<Transform, Transform>();
            map[donorChar] = charCopy;
            CopyHierarchy(donorChar, charCopy, needed, map);

            // Body mesh
            GameObject bodyGo = new GameObject("bodymesh");
            bodyGo.transform.SetParent(charCopy, false);
            bodyGo.transform.localPosition = donorSmr.transform.localPosition;
            bodyGo.transform.localRotation = donorSmr.transform.localRotation;
            bodyGo.transform.localScale = donorSmr.transform.localScale;
            SkinnedMeshRenderer smr = bodyGo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = donorSmr.sharedMesh;
            Transform[] bones = new Transform[donorBones.Length];
            for (int i = 0; i < donorBones.Length; i++)
            {
                Transform b;
                bones[i] = donorBones[i] != null && map.TryGetValue(donorBones[i], out b) ? b : null;
            }
            smr.bones = bones;
            Transform rootBone;
            if (donorSmr.rootBone != null && map.TryGetValue(donorSmr.rootBone, out rootBone)) smr.rootBone = rootBone;
            smr.localBounds = donorSmr.localBounds;
            smr.updateWhenOffscreen = true;
            smr.sharedMaterials = PickMaterials(donorSmr.sharedMaterials, look);
            smr.castShadows = true;
            smr.receiveShadows = true;

            rig = new AvatarRig();
            rig.Root = root.transform;
            rig.Pivot = pivot;
            rig.Char = charCopy;
            rig.Body = smr;
            rig.Skeleton = charCopy.Find("skeleton");
            rig.Pelvis = rig.Skeleton != null ? rig.Skeleton.Find("pelvis") : null;
            rig.SpineMiddle = Find(rig.Skeleton, "pelvis/spine_middle");
            rig.SpineUpper = Find(rig.Skeleton, "pelvis/spine_middle/spine_upper");
            rig.HeadPivot = FindHeadPivot(rig.SpineUpper);
            rig.Head = FindHead(rig.HeadPivot ?? rig.SpineUpper);
            rig.CollarLeft = Find(rig.Skeleton, "pelvis/spine_middle/spine_upper/collar_left");
            rig.CollarRight = Find(rig.Skeleton, "pelvis/spine_middle/spine_upper/collar_right");
            rig.ShoulderLeft = Find(rig.CollarLeft, "shoulder_left");
            rig.ShoulderRight = Find(rig.CollarRight, "shoulder_right");
            rig.ThighLeft = Find(rig.Skeleton, "thig_left");
            rig.ThighRight = Find(rig.Skeleton, "thig_right");
            rig.KneeLeft = Find(rig.ThighLeft, "knee_left");
            rig.KneeRight = Find(rig.ThighRight, "knee_right");
            rig.AnkleLeft = Find(rig.KneeLeft, "ankle_left");
            rig.AnkleRight = Find(rig.KneeRight, "ankle_right");
            if (rig.Head == null && rig.HeadPivot != null) rig.Head = rig.HeadPivot;
            if (rig.Skeleton == null || rig.Pelvis == null)
            {
                UnityEngine.Object.Destroy(root);
                rig = null;
                return null;
            }

            // Animations, layered exactly like the NPCs: legs, breathing, each arm, and
            // a whole-body layer on Pivot for sitting in vehicles.
            rig.LegsAnim = AddAnimation(rig.Skeleton, Clips.Stand, Clips.Walk, Clips.Run);
            rig.BreathAnim = AddAnimation(rig.SpineUpper, Clips.Breath);
            rig.LeftArmAnim = AddAnimation(rig.CollarLeft, Clips.LeftIdle, Clips.LeftWalk, Clips.LeftRun, Clips.Point);
            rig.RightArmAnim = AddAnimation(rig.CollarRight, Clips.RightIdle, Clips.RightWalk, Clips.RightRun, Clips.Carry, Clips.Wave);
            rig.SeatAnim = AddAnimation(rig.Pivot, Clips.SitCar, Clips.SitVan);

            rig.CaptureRestPose();

            // Body collider: the same "PlayerOnlyColl" layer NPCs use, so you bump into your
            // friend but physics objects and cars are not affected by it.
            GameObject collGo = new GameObject("Collider");
            collGo.transform.SetParent(root.transform, false);
            collGo.layer = LayerMask.NameToLayer("PlayerOnlyColl") >= 0 ? LayerMask.NameToLayer("PlayerOnlyColl") : 23;
            CapsuleCollider cap = collGo.AddComponent<CapsuleCollider>();
            cap.height = 1.75f;
            cap.radius = 0.28f;
            cap.center = new Vector3(0f, 0.88f, 0f);
            Rigidbody rb = collGo.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rig.Collider = cap;

            Log.Info("Built player body from " + GameUtil.PathOf(donorChar) + " (" + bones.Length + " bones)");
            return root;
        }

        static void AddWithAncestors(HashSet<Transform> set, Transform stopAt, Transform t)
        {
            for (; t != null && t != stopAt; t = t.parent) set.Add(t);
        }

        static Transform FindHeadPivot(Transform spineUpper)
        {
            if (spineUpper == null) return null;
            for (int i = 0; i < spineUpper.childCount; i++)
            {
                Transform c = spineUpper.GetChild(i);
                if (c.name.StartsWith("HeadPivot", StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }

        static Transform FindHead(Transform parent)
        {
            if (parent == null) return null;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (string.Equals(c.name, "head", StringComparison.OrdinalIgnoreCase)) return c;
            }
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform nested = FindHead(parent.GetChild(i));
                if (nested != null) return nested;
            }
            return null;
        }

        static Transform Find(Transform t, string path)
        {
            return t != null ? t.Find(path) : null;
        }

        static void CopyHierarchy(Transform src, Transform dst, HashSet<Transform> needed, Dictionary<Transform, Transform> map)
        {
            for (int i = 0; i < src.childCount; i++)
            {
                Transform c = src.GetChild(i);
                if (!needed.Contains(c) || Array.IndexOf(BoneSkipNames, c.name) >= 0) continue;
                Transform copy = new GameObject(c.name).transform;
                copy.SetParent(dst, false);
                copy.localPosition = c.localPosition;
                copy.localRotation = c.localRotation;
                copy.localScale = c.localScale;
                map[c] = copy;
                CopyHierarchy(c, copy, needed, map);
            }
        }

        static Animation AddAnimation(Transform t, params string[] clipNames)
        {
            if (t == null) return null;
            Animation anim = t.gameObject.AddComponent<Animation>();
            anim.playAutomatically = false;
            anim.cullingType = AnimationCullingType.AlwaysAnimate;
            for (int i = 0; i < clipNames.Length; i++)
            {
                AnimationClip clip = GameUtil.FindClip(clipNames[i]);
                if (clip == null)
                {
                    if (Once.First("clip " + clipNames[i])) Log.Info("Animation clip not found: " + clipNames[i]);
                    continue;
                }
                anim.AddClip(clip, clipNames[i]);
                AnimationState s = anim[clipNames[i]];
                if (s != null) s.wrapMode = WrapMode.Loop;
            }
            return anim;
        }

        static Material[] PickMaterials(Material[] donor, Appearance look)
        {
            Material[] mats = (Material[])donor.Clone();
            // NPC bodies use three materials: shirt, trousers, face.
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                string n = mats[i].name;
                string want = null;
                if (look.Shirt > 0 && (n.Contains("shirt"))) want = Catalog.Shirts[look.Shirt];
                else if (look.Pants > 0 && (n.Contains("pants") || n.Contains("curtain") || n.Contains("fabric"))) want = Catalog.Pants[look.Pants];
                else if (look.Face > 0 && n.Contains("face")) want = Catalog.Faces[look.Face];
                if (want == null) continue;
                Material m = GameUtil.FindMaterial(want);
                if (m != null) mats[i] = m;
            }
            return mats;
        }
    }

    /// <summary>References to the parts of a built avatar.</summary>
    internal sealed class AvatarRig
    {
        public Transform Root, Pivot, Char, Skeleton, Pelvis, SpineMiddle, SpineUpper, HeadPivot, Head;
        public Transform CollarLeft, CollarRight, ShoulderLeft, ShoulderRight;
        public Transform ThighLeft, ThighRight, KneeLeft, KneeRight, AnkleLeft, AnkleRight;
        public SkinnedMeshRenderer Body;
        public Animation LegsAnim, BreathAnim, LeftArmAnim, RightArmAnim, SeatAnim;
        public CapsuleCollider Collider;

        Transform[] restBones;
        Vector3[] restPos;
        Quaternion[] restRot;

        public void CaptureRestPose()
        {
            Transform[] all = Char.GetComponentsInChildren<Transform>(true);
            restBones = all;
            restPos = new Vector3[all.Length];
            restRot = new Quaternion[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                restPos[i] = all[i].localPosition;
                restRot[i] = all[i].localRotation;
            }
        }

        /// <summary>Puts every bone back to the NPC's rest pose (used when switching between
        /// sitting and standing so no leftover offsets remain).</summary>
        public void ResetPose()
        {
            if (restBones == null) return;
            for (int i = 0; i < restBones.Length; i++)
            {
                if (restBones[i] == null) continue;
                restBones[i].localPosition = restPos[i];
                restBones[i].localRotation = restRot[i];
            }
        }
    }
}
