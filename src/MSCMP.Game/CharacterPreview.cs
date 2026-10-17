using System;
using UnityEngine;

namespace MSCMP.Integration
{
    /// <summary>Owned offscreen portraits; no main camera, ambient light or game object is modified.</summary>
    internal sealed class CharacterPreview : IDisposable
    {
        private const int PreviewLayer = 31;
        private readonly CharacterMesh[] characters = new CharacterMesh[2];
        private readonly RenderTexture[] portraits = new RenderTexture[2];
        private GameObject stage;
        private Camera camera;
        private bool disposed;

        internal CharacterPreview()
        {
            try
            {
                stage = new GameObject("MSCMP.OwnedCharacterPreview");
                stage.hideFlags = HideFlags.HideAndDontSave;
                // Far beyond the playable area, with no colliders or global lighting.
                stage.transform.position = new Vector3(20000, 20000, 20000);
                GameObject cameraObject = Child("Offscreen camera", new Vector3(0, 1.04f, 3.4f));
                camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.cullingMask = 1 << PreviewLayer;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.12f, 0.14f, 0.14f, 1);
                camera.orthographic = true;
                camera.orthographicSize = 1.03f;
                camera.aspect = 256f / 384f;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 7;
                camera.transform.rotation = Quaternion.LookRotation(stage.transform.position + new Vector3(0, 0.94f, 0) - camera.transform.position);
                AddLight("Owned warm key", new Vector3(-1.0f, 2.2f, 1.8f), 1.6f, new Color(1, 0.93f, 0.82f));
                AddLight("Owned cool fill", new Vector3(1.1f, 1.4f, 1.0f), 0.8f, new Color(0.82f, 0.89f, 1));
                stage.SetActive(false);
            }
            catch { Dispose(); throw; }
        }

        internal Texture GetPreview(CharacterStyle style)
        {
            if (disposed || stage == null || camera == null) throw new ObjectDisposedException("CharacterPreview");
            if (style != CharacterStyle.Male && style != CharacterStyle.Female) throw new ArgumentOutOfRangeException("style");
            int index = style == CharacterStyle.Male ? 0 : 1;
            if (portraits[index] != null && portraits[index].IsCreated()) return portraits[index];
            // Render targets can lose their device allocation on a fullscreen/device
            // transition while the managed Unity object remains alive. Recreate once.
            if (portraits[index] != null)
            {
                portraits[index].Release();
                UnityEngine.Object.Destroy(portraits[index]);
                portraits[index] = null;
            }
            CharacterMesh character = characters[index];
            RenderTexture portrait = null;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;
            try
            {
                if (character == null || character.Root == null)
                {
                    if (character != null) character.Dispose();
                    character = CharacterMesh.Create(stage.transform, style, index + 1);
                    SetPreviewLayer(character.Root.transform);
                    character.Root.transform.localRotation = Quaternion.Euler(0, -12, 0);
                    character.Animate(0, 0);
                }
                portrait = new RenderTexture(256, 384, 16, RenderTextureFormat.ARGB32);
                portrait.name = "MSCMP.OriginalPortrait." + style;
                portrait.hideFlags = HideFlags.HideAndDontSave;
                portrait.Create();
                if (!portrait.IsCreated()) throw new InvalidOperationException("Offscreen portrait target could not be created.");
                camera.targetTexture = portrait;
                character.Root.SetActive(true);
                stage.SetActive(true);
                camera.Render();
                characters[index] = character;
                portraits[index] = portrait;
                return portrait;
            }
            catch
            {
                if (camera != null) camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (character != null) character.Dispose();
                characters[index] = null;
                if (portrait != null) { portrait.Release(); UnityEngine.Object.Destroy(portrait); }
                throw;
            }
            finally
            {
                // IMGUI may already be drawing to another target. Never leak the
                // preview target or its short-range lighting into later GUI passes.
                if (character != null && character.Root != null) character.Root.SetActive(false);
                if (stage != null) stage.SetActive(false);
                if (camera != null) camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
            }
        }

        private GameObject Child(string name, Vector3 position)
        {
            GameObject child = new GameObject(name);
            child.hideFlags = HideFlags.HideAndDontSave;
            child.layer = PreviewLayer;
            child.transform.parent = stage.transform;
            child.transform.localPosition = position;
            child.transform.localRotation = Quaternion.identity;
            return child;
        }

        private void AddLight(string name, Vector3 position, float intensity, Color colour)
        {
            Light light = Child(name, position).AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 6;
            light.intensity = intensity;
            light.color = colour;
            light.cullingMask = 1 << PreviewLayer;
            light.shadows = LightShadows.None;
        }

        private static void SetPreviewLayer(Transform node)
        {
            node.gameObject.layer = PreviewLayer;
            node.gameObject.hideFlags = HideFlags.HideAndDontSave;
            for (int i = 0; i < node.childCount; i++) SetPreviewLayer(node.GetChild(i));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (camera != null) camera.targetTexture = null;
            for (int i = 0; i < characters.Length; i++)
            {
                if (characters[i] != null) { characters[i].Dispose(); characters[i] = null; }
                if (portraits[i] != null) { portraits[i].Release(); UnityEngine.Object.Destroy(portraits[i]); portraits[i] = null; }
            }
            if (stage != null) { stage.SetActive(false); UnityEngine.Object.Destroy(stage); stage = null; }
            camera = null;
        }
    }
}
