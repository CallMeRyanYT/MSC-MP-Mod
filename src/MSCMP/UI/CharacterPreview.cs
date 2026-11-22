using System;
using MSCMP.Avatars;
using MSCMP.Game;
using MSCMP.Net;
using UnityEngine;

namespace MSCMP.UI
{
    /// <summary>
    /// Shows you your own character as other players see it: a copy of your body stands far
    /// above the map, slowly turning, and a small camera films it into a picture for the F2
    /// panel. Only exists while you are looking at it. Needs the world loaded, because the
    /// bodies are borrowed from the game's own villagers.
    /// </summary>
    internal static class CharacterPreview
    {
        static readonly Vector3 Spot = new Vector3(0f, 1800f, 0f);
        const int Width = 220, Height = 300;

        static NetPlayer fake;
        static RemoteAvatar avatar;
        static GameObject rig;
        static Camera cam;
        static RenderTexture texture;
        static float spin;
        static int wantedFrame = -10;

        /// <summary>Until when to pop the preview up after a change in MSCLoader's settings.</summary>
        public static float PopupUntil;

        public static Texture Texture { get { return texture; } }
        public static bool Ready { get { return avatar != null && avatar.Visible && texture != null; } }

        /// <summary>The UI calls this every frame it wants the picture.</summary>
        public static void Want() { wantedFrame = Time.frameCount; }

        public static bool Popup { get { return Time.realtimeSinceStartup < PopupUntil; } }

        public static void Update()
        {
            bool wanted = Time.frameCount - wantedFrame <= 2 || Popup;
            if (!wanted || !MP.InGame || !LocalPlayer.Bound) { Hide(); return; }
            try
            {
                Ensure();
                Appearance look = Options.LocalAppearance();
                if (!Same(look, fake.Look))
                {
                    fake.Look = look;
                    avatar.Rebuild(look);
                }
                spin = Mathf.Repeat(spin + Time.unscaledDeltaTime * 35f, 360f); // keeps turning while the game is paused
                PlayerSnapshot s = new PlayerSnapshot();
                s.Time = Time.realtimeSinceStartup;
                s.Position = Spot;
                s.Head = Spot + Vector3.up * 1.62f;
                s.Yaw = spin;
                s.Flags = PlayerSnapshot.FlagGrounded;
                avatar.Push(s);
                avatar.Update();
                cam.enabled = true;
            }
            catch (Exception e)
            {
                Log.ExceptionOnce("CharacterPreview", e);
                Hide();
            }
        }

        public static void LateUpdate()
        {
            if (avatar != null && cam != null && cam.enabled) avatar.LateUpdate();
        }

        static void Ensure()
        {
            if (fake == null)
                fake = new NetPlayer { Name = "Preview", Slot = 251, Phase = PlayerPhase.Playing, ClockSynced = true, Look = Options.LocalAppearance() };
            if (avatar == null || avatar.Object == null)
            {
                fake.Look = Options.LocalAppearance();
                avatar = RemoteAvatar.Create(fake);
            }
            if (texture == null)
            {
                texture = new RenderTexture(Width, Height, 16);
                texture.Create();
            }
            if (rig == null)
            {
                rig = new GameObject("MSCMP_Preview");
                UnityEngine.Object.DontDestroyOnLoad(rig);
                rig.transform.position = Spot + new Vector3(0f, 1.05f, 3.1f);
                rig.transform.LookAt(Spot + Vector3.up * 0.95f);
                cam = rig.AddComponent<Camera>();
                cam.targetTexture = texture;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.13f, 0.14f, 0.16f, 1f);
                cam.fieldOfView = 38f;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 12f;
                cam.depth = -50f;
                // Its own light, so the picture is never dark at night. Nothing else is up here.
                Light light = rig.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = 9f;
                light.intensity = 1.3f;
                light.color = new Color(1f, 0.97f, 0.92f);
            }
        }

        static void Hide()
        {
            if (cam != null) cam.enabled = false;
            if (avatar != null)
            {
                avatar.Destroy();
                avatar = null;
            }
            if (rig != null)
            {
                UnityEngine.Object.Destroy(rig);
                rig = null;
                cam = null;
            }
            if (texture != null)
            {
                texture.Release();
                UnityEngine.Object.Destroy(texture);
                texture = null;
            }
        }

        static bool Same(Appearance a, Appearance b)
        {
            return a.Body == b.Body && a.Shirt == b.Shirt && a.Pants == b.Pants && a.Face == b.Face;
        }
    }
}
