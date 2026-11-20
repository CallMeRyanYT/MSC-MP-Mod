using MSCMP.Game;
using MSCMP.Net;
using UnityEngine;

namespace MSCMP.Avatars
{
    /// <summary>
    /// Seeing your own emotes. In first person you see nothing of yourself, like in single
    /// player. When you play an emote the view switches to third person for a few seconds
    /// and shows you your character (the one your friends see) doing it. The body keeps
    /// following you invisibly in the meantime, so it is ready the moment you need it.
    /// </summary>
    internal static class SelfBody
    {
        static NetPlayer me;
        static GameObject camObject;
        static Camera cam;
        static float emoteCamUntil;
        static Vector3 emoteStartPos;

        public static void Update()
        {
            bool want = Options.EmoteCamera && MP.InGame && LocalPlayer.Bound;
            if (!want) { Hide(); return; }
            if (me == null)
            {
                me = new NetPlayer { Slot = 202, Name = "You", Phase = PlayerPhase.Playing, ClockSynced = true, Look = Options.LocalAppearance() };
                me.Avatar = RemoteAvatar.Create(me);
                me.Avatar.IsSelf = true;
            }
            Appearance look = Options.LocalAppearance();
            if (look.Body != me.Look.Body || look.Shirt != me.Look.Shirt || look.Pants != me.Look.Pants || look.Face != me.Look.Face)
            {
                me.Look = look;
                me.Avatar.Rebuild(look);
                me.Avatar.IsSelf = true;
            }
            PlayerSnapshot s = LocalPlayer.Sample();
            if (Session.Local != null && Session.Local.HasStatus) { me.Status = Session.Local.Status; me.HasStatus = true; }
            me.Avatar.Push(s);
            me.Avatar.Update();

            // The emote camera ends early when you walk off.
            if (EmoteCamOn && (LocalPlayer.Feet - emoteStartPos).sqrMagnitude > 1f) emoteCamUntil = 0f;
        }

        static bool EmoteCamOn { get { return Time.realtimeSinceStartup < emoteCamUntil; } }

        public static void LateUpdate()
        {
            if (me == null || me.Avatar == null) return;
            me.Avatar.SetVisible(EmoteCamOn); // only in the third-person emote view
            me.Avatar.LateUpdate();
            UpdateEmoteCamera();
        }

        /// <summary>You played an emote: third person for a moment, showing your body doing it.</summary>
        public static void OnEmote(byte id)
        {
            if (me == null || me.Avatar == null) return;
            me.Avatar.PlayGesture(id);
            if (!Options.EmoteCamera || !string.IsNullOrEmpty(LocalPlayer.CurrentVehicleName) || LocalPlayer.PassengerOf != null) return;
            emoteCamUntil = Time.realtimeSinceStartup + (id == Emotes.Sit ? 6f : id == Emotes.Dance ? 6.5f : 3f);
            emoteStartPos = LocalPlayer.Feet;
        }

        static void UpdateEmoteCamera()
        {
            if (!EmoteCamOn || LocalPlayer.View == null)
            {
                if (cam != null) cam.enabled = false;
                return;
            }
            if (camObject == null)
            {
                camObject = new GameObject("MSCMP_EmoteCamera");
                Object.DontDestroyOnLoad(camObject);
                cam = camObject.AddComponent<Camera>();
            }
            Camera main = LocalPlayer.View;
            cam.depth = main.depth + 10f;
            cam.fieldOfView = main.fieldOfView;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = main.farClipPlane;
            cam.cullingMask = main.cullingMask;
            cam.clearFlags = main.clearFlags;
            cam.backgroundColor = main.backgroundColor;
            // In front of you and a little to the side, looking back at you.
            Transform p = LocalPlayer.Player;
            Vector3 chest = LocalPlayer.Feet + Vector3.up * 1.15f;
            Vector3 want = chest + p.forward * 2.4f + p.right * 0.6f + Vector3.up * 0.35f;
            // Never inside a wall.
            RaycastHit hit;
            Vector3 dir = want - chest;
            if (Physics.Raycast(chest, dir.normalized, out hit, dir.magnitude, GameUtil.WorldMask) && !GameUtil.IsPersonCollider(hit.collider))
                want = chest + dir.normalized * Mathf.Max(0.6f, hit.distance - 0.2f);
            camObject.transform.position = want;
            camObject.transform.LookAt(chest);
            cam.enabled = true;
        }

        static void Hide()
        {
            if (me != null && me.Avatar != null) me.Avatar.Destroy();
            me = null;
            if (cam != null) cam.enabled = false;
            emoteCamUntil = 0f;
        }
    }
}
