using System;
using System.Collections.Generic;
using UnityEngine;

namespace MSCMP.Integration
{
    /// <summary>Original, noninteractive remote presentation. All calls belong on Unity's main thread.</summary>
    internal sealed class RemoteAvatars : IDisposable
    {
        private const int MaximumRemotes = 3;
        private const int BufferCapacity = 8;
        private const float InterpolationDelay = 0.10f;
        private const float StaleAfter = 2.0f;

        private struct Pose
        {
            internal Vector3 Position;
            internal float Yaw;
            internal float ReceivedAt;
        }

        private sealed class Avatar
        {
            internal int PlayerId;
            internal GameObject Root;
            internal CharacterMesh Character;
            internal readonly Pose[] Poses = new Pose[BufferCapacity];
            internal int Count;
        }

        private readonly int generation;
        private readonly int localPlayerId;
        private readonly Avatar[] avatars = new Avatar[MaximumRemotes];
        private readonly Dictionary<int, CharacterStyle> appearances = new Dictionary<int, CharacterStyle>();
        private GameObject container;
        internal Func<ulong, bool> Seated, Holding;
        internal Vector3? CarryPosition(ulong player)
        { Avatar avatar = player <= Int32.MaxValue ? Find((int)player) : null; return avatar == null || avatar.Root == null ? (Vector3?)null : avatar.Character.CarryPosition; }

        internal RemoteAvatars(int sceneGeneration, int ownPlayerId)
        {
            if (sceneGeneration <= 0 || ownPlayerId < 1 || Application.loadedLevelName != "GAME")
                throw new InvalidOperationException("Remote avatars require an active GAME scene and accepted player identity.");
            generation = sceneGeneration;
            localPlayerId = ownPlayerId;
            // Scene-owned: deliberately not DontDestroyOnLoad.
            container = new GameObject("MSCMP.RemoteAvatars." + sceneGeneration);
        }

        internal bool ReceivePose(int sceneGeneration, int playerId, Vector3 position, float yaw, float receivedAt)
        {
            if (!Alive(sceneGeneration) || playerId < 1 || playerId == localPlayerId || !Valid(position.x)
                || !Valid(position.y) || !Valid(position.z) || !Valid(yaw) || !Valid(receivedAt) || receivedAt < 0
                || Math.Abs(position.x) > 100000 || Math.Abs(position.y) > 100000 || Math.Abs(position.z) > 100000) return false;
            CharacterStyle style;
            if (!appearances.TryGetValue(playerId, out style)) return false;
            Avatar avatar = Find(playerId);
            if (avatar == null)
            {
                int slot = -1;
                for (int i = 0; i < avatars.Length; i++) if (avatars[i] == null) { slot = i; break; }
                if (slot < 0) return false;
                avatar = Create(playerId, style);
                avatars[slot] = avatar;
            }
            if (avatar.Count > 0 && receivedAt < avatar.Poses[avatar.Count - 1].ReceivedAt) return false;
            if (avatar.Count > 0 && (position - avatar.Poses[avatar.Count - 1].Position).sqrMagnitude > 25)
                avatar.Count = 0; // Discontinuous teleport/recovery: never lerp across the world.
            if (avatar.Count > 0 && receivedAt == avatar.Poses[avatar.Count - 1].ReceivedAt)
            {
                // The relay already rejects obsolete network sequences. Several newer
                // packets can arrive in one Unity frame; keep the freshest in that slot.
                avatar.Poses[avatar.Count - 1] = new Pose { Position = position, Yaw = yaw, ReceivedAt = receivedAt };
                return true;
            }
            if (avatar.Count == BufferCapacity)
            {
                Array.Copy(avatar.Poses, 1, avatar.Poses, 0, BufferCapacity - 1);
                avatar.Count--;
            }
            avatar.Poses[avatar.Count++] = new Pose { Position = position, Yaw = yaw, ReceivedAt = receivedAt };
            return true;
        }

        // A validated appearance baseline must precede poses. Changing identity style
        // requires Remove first, so old queued poses cannot silently replace a mesh.
        internal bool SetAppearance(int sceneGeneration, int playerId, CharacterStyle style)
        {
            if (!Alive(sceneGeneration) || playerId < 1 || playerId == localPlayerId
                || (style != CharacterStyle.Male && style != CharacterStyle.Female)) return false;
            CharacterStyle previous;
            if (appearances.TryGetValue(playerId, out previous)) return previous == style;
            if (appearances.Count >= MaximumRemotes) return false;
            appearances.Add(playerId, style);
            return true;
        }

        internal void Update(int sceneGeneration, float now)
        {
            if (!Alive(sceneGeneration) || !Valid(now)) { HideAll(); return; }
            for (int i = 0; i < avatars.Length; i++)
            {
                Avatar avatar = avatars[i];
                if (avatar == null || avatar.Root == null || avatar.Count == 0) continue;
                Pose latest = avatar.Poses[avatar.Count - 1];
                if (now < latest.ReceivedAt || now - latest.ReceivedAt > StaleAfter)
                {
                    avatar.Root.SetActive(false);
                    continue;
                }
                float target = now - InterpolationDelay;
                Pose left = avatar.Poses[0], right = left;
                for (int sample = 1; sample < avatar.Count; sample++)
                {
                    right = avatar.Poses[sample];
                    if (right.ReceivedAt >= target) break;
                    left = right;
                }
                float amount = right.ReceivedAt > left.ReceivedAt
                    ? Mathf.Clamp01((target - left.ReceivedAt) / (right.ReceivedAt - left.ReceivedAt)) : 0;
                avatar.Root.transform.position = Vector3.Lerp(left.Position, right.Position, amount);
                avatar.Root.transform.rotation = Quaternion.Euler(0, Mathf.LerpAngle(left.Yaw, right.Yaw, amount), 0);
                float speed = 0;
                if (avatar.Count > 1 && now - latest.ReceivedAt <= 0.25f)
                {
                    Pose previous = avatar.Poses[avatar.Count - 2];
                    float elapsed = latest.ReceivedAt - previous.ReceivedAt;
                    Vector3 movement = latest.Position - previous.Position;
                    movement.y = 0;
                    // Teleports must not produce an implausible running animation.
                    if (elapsed > 0 && movement.magnitude < 5) speed = Mathf.Min(movement.magnitude / elapsed, 6);
                }
                avatar.Character.Animate(now, speed, Seated != null && Seated((ulong)avatar.PlayerId), Holding != null && Holding((ulong)avatar.PlayerId));
                avatar.Root.SetActive(true);
            }
        }

        internal void Remove(int sceneGeneration, int playerId)
        {
            if (sceneGeneration != generation) return;
            appearances.Remove(playerId);
            for (int i = 0; i < avatars.Length; i++)
                if (avatars[i] != null && avatars[i].PlayerId == playerId) { Destroy(avatars[i]); avatars[i] = null; }
        }

        public void Dispose()
        {
            for (int i = 0; i < avatars.Length; i++) { if (avatars[i] != null) Destroy(avatars[i]); avatars[i] = null; }
            appearances.Clear();
            if (container != null) { container.SetActive(false); UnityEngine.Object.Destroy(container); container = null; }
        }

        private bool Alive(int sceneGeneration)
        {
            return sceneGeneration == generation && container != null && Application.loadedLevelName == "GAME";
        }

        private Avatar Find(int playerId)
        {
            for (int i = 0; i < avatars.Length; i++) if (avatars[i] != null && avatars[i].PlayerId == playerId) return avatars[i];
            return null;
        }

        private Avatar Create(int playerId, CharacterStyle style)
        {
            Avatar avatar = new Avatar();
            avatar.PlayerId = playerId;
            try
            {
                avatar.Character = CharacterMesh.Create(container.transform, style, playerId);
                avatar.Root = avatar.Character.Root;
                return avatar;
            }
            catch
            {
                Destroy(avatar);
                throw;
            }
        }

        private void HideAll()
        {
            for (int i = 0; i < avatars.Length; i++) if (avatars[i] != null && avatars[i].Root != null) avatars[i].Root.SetActive(false);
        }

        private static void Destroy(Avatar avatar)
        {
            if (avatar.Character != null) { avatar.Character.Dispose(); avatar.Character = null; }
            avatar.Root = null;
        }

        private static bool Valid(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
    }
}
