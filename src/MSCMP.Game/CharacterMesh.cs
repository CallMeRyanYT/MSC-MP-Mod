using System;
using System.Collections.Generic;
using UnityEngine;

namespace MSCMP.Integration
{
    /// <summary>
    /// Original source-authored human geometry and pixel textures. No game mesh,
    /// NPC texture, downloaded model, input handler or save component is reused.
    /// Nine rigid mesh parts share one small atlas; call only on Unity's main thread.
    /// </summary>
    internal sealed class CharacterMesh : IDisposable
    {
        private readonly List<Mesh> meshes = new List<Mesh>();
        private readonly Transform[] arms = new Transform[2];
        private readonly Transform[] forearms = new Transform[2];
        private readonly Transform[] thighs = new Transform[2];
        private readonly Transform[] shins = new Transform[2];
        private GameObject root;
        private Transform body;
        private Material material;
        private Texture2D atlas;
        private float phase;
        private float lastTime = -1;
        private float stride;
        internal GameObject Root { get { return root; } }
        internal int VertexCount { get; private set; }
        internal int TriangleCount { get; private set; }

        internal static CharacterMesh Create(Transform parent, CharacterStyle style, int identity)
        {
            if (style != CharacterStyle.Male && style != CharacterStyle.Female)
                throw new ArgumentOutOfRangeException("style");
            Shader shader = Shader.Find("Diffuse");
            if (shader == null) throw new InvalidOperationException("Unity Diffuse shader unavailable.");
            CharacterMesh human = new CharacterMesh();
            try
            {
                human.root = Node("Original " + style + " character " + identity, parent, Vector3.zero);
                human.root.SetActive(false);
                human.atlas = MakeAtlas(style);
                human.material = new Material(shader);
                human.material.name = "MSCMP.OriginalClothing." + style;
                human.material.hideFlags = HideFlags.DontSave;
                human.material.mainTexture = human.atlas;
                human.material.color = Color.white;
                human.body = Node("Articulated body", human.root.transform, Vector3.zero).transform;
                human.Build(style);
                return human;
            }
            catch { human.Dispose(); throw; }
        }

        private void Build(CharacterStyle style)
        {
            bool female = style == CharacterStyle.Female;
            float shoulder = female ? 0.210f : 0.237f;
            Builder torso = new Builder();
            torso.Tube(new Ring[] {
                new Ring(0.91f, female ? 0.180f : 0.177f, 0.117f),
                new Ring(1.04f, female ? 0.163f : 0.184f, 0.116f),
                new Ring(1.23f, female ? 0.180f : 0.211f, 0.128f),
                new Ring(1.39f, shoulder, 0.119f),
                new Ring(1.46f, shoulder * 0.72f, 0.098f) }, 0, Vector3.zero, false);
            torso.Tube(new Ring[] { new Ring(0.895f, 0.180f, 0.119f), new Ring(0.936f, 0.180f, 0.119f) }, 7, Vector3.zero, false);
            torso.Box(new Vector3(0, 0.916f, 0.122f), new Vector3(0.047f, 0.029f, 0.012f), 3);
            torso.Tube(new Ring[] { new Ring(1.43f, 0.061f, 0.060f), new Ring(1.57f, 0.061f, 0.062f) }, 2, Vector3.zero, false);
            torso.Tube(new Ring[] {
                new Ring(1.525f, female ? 0.066f : 0.074f, 0.074f),
                new Ring(1.585f, female ? 0.098f : 0.105f, 0.109f),
                new Ring(1.710f, 0.116f, 0.125f),
                new Ring(1.780f, 0.099f, 0.103f),
                new Ring(1.804f, 0.059f, 0.065f) }, 2, Vector3.zero, false);
            // A shaped front plane carries the original face, while the nose has real depth.
            torso.Quad(new Vector3(-0.079f, 1.555f, 0.113f), new Vector3(0.079f, 1.555f, 0.113f),
                new Vector3(0.079f, 1.724f, 0.130f), new Vector3(-0.079f, 1.724f, 0.130f), 4);
            torso.Triangle(new Vector3(-0.016f, 1.630f, 0.132f), new Vector3(0.016f, 1.630f, 0.132f), new Vector3(0, 1.657f, 0.160f), 2);
            torso.Triangle(new Vector3(0, 1.657f, 0.160f), new Vector3(0.016f, 1.630f, 0.132f), new Vector3(0, 1.688f, 0.129f), 2);
            torso.Triangle(new Vector3(-0.016f, 1.630f, 0.132f), new Vector3(0, 1.657f, 0.160f), new Vector3(0, 1.688f, 0.129f), 2);
            torso.Box(new Vector3(-0.119f, 1.661f, -0.006f), new Vector3(0.023f, 0.054f, 0.038f), 2);
            torso.Box(new Vector3(0.119f, 1.661f, -0.006f), new Vector3(0.023f, 0.054f, 0.038f), 2);
            torso.Tube(new Ring[] { new Ring(1.720f, 0.120f, 0.128f), new Ring(1.788f, 0.108f, 0.113f), new Ring(1.827f, 0.062f, 0.073f) }, 5, new Vector3(0, 0, -0.004f), false);
            // Open front of the longer hair keeps the face unobstructed.
            if (female)
            {
                torso.Tube(new Ring[] { new Ring(1.475f, 0.153f, 0.111f), new Ring(1.610f, 0.145f, 0.129f), new Ring(1.754f, 0.125f, 0.132f) }, 5, new Vector3(0, 0, -0.018f), true);
                torso.Box(new Vector3(-0.109f, 1.658f, 0.097f), new Vector3(0.025f, 0.176f, 0.029f), 5);
                torso.Box(new Vector3(0.109f, 1.658f, 0.097f), new Vector3(0.025f, 0.176f, 0.029f), 5);
            }
            // Worn cream undershirt at an open workshirt collar; no branded graphics.
            torso.Triangle(new Vector3(-0.073f, 1.451f, 0.104f), new Vector3(0, 1.330f, 0.131f), new Vector3(0.073f, 1.451f, 0.104f), 6);
            torso.Triangle(new Vector3(-0.127f, 1.447f, 0.110f), new Vector3(-0.055f, 1.347f, 0.134f), new Vector3(-0.043f, 1.450f, 0.108f), 0);
            torso.Triangle(new Vector3(0.043f, 1.450f, 0.108f), new Vector3(0.055f, 1.347f, 0.134f), new Vector3(0.127f, 1.447f, 0.110f), 0);
            for (int button = 0; button < 3; button++)
                torso.Box(new Vector3(0, 1.105f + button * 0.074f, 0.131f), new Vector3(0.009f, 0.009f, 0.004f), 7);
            AddPart("Clothed torso and original face", body, Vector3.zero, torso);

            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1 : 1;
                string label = side == 0 ? "Left " : "Right ";
                Builder sleeve = new Builder();
                sleeve.Tube(new Ring[] { new Ring(-0.286f, 0.071f, 0.073f), new Ring(-0.225f, 0.080f, 0.079f), new Ring(-0.040f, 0.090f, 0.087f), new Ring(0.018f, 0.070f, 0.074f) }, 0, Vector3.zero, false);
                arms[side] = AddPart(label + "short sleeve", body, new Vector3(sign * (shoulder + 0.023f), 1.397f, 0), sleeve);
                Builder arm = new Builder();
                arm.Tube(new Ring[] { new Ring(-0.260f, 0.043f, 0.045f), new Ring(-0.175f, 0.055f, 0.055f), new Ring(0.010f, 0.063f, 0.065f) }, 2, Vector3.zero, false);
                arm.Tube(new Ring[] { new Ring(-0.367f, 0.046f, 0.033f), new Ring(-0.290f, 0.050f, 0.038f), new Ring(-0.249f, 0.040f, 0.035f) }, 2, new Vector3(0, 0, 0.009f), false);
                arm.Box(new Vector3(sign * -0.044f, -0.294f, 0.024f), new Vector3(0.031f, 0.062f, 0.036f), 2);
                forearms[side] = AddPart(label + "forearm and hand", arms[side], new Vector3(0, -0.271f, 0), arm);
                Builder thigh = new Builder();
                thigh.Tube(new Ring[] { new Ring(-0.445f, 0.073f, 0.075f), new Ring(-0.340f, 0.084f, 0.087f), new Ring(-0.060f, 0.102f, 0.108f), new Ring(0.025f, 0.094f, 0.102f) }, 1, Vector3.zero, false);
                thighs[side] = AddPart(label + "denim thigh", body, new Vector3(sign * 0.090f, 0.930f, 0), thigh);
                Builder shin = new Builder();
                shin.Tube(new Ring[] { new Ring(-0.399f, 0.069f, 0.070f), new Ring(-0.310f, 0.072f, 0.072f), new Ring(-0.040f, 0.077f, 0.078f), new Ring(0.017f, 0.073f, 0.076f) }, 1, Vector3.zero, false);
                shin.Tube(new Ring[] { new Ring(-0.489f, 0.086f, 0.145f), new Ring(-0.465f, 0.088f, 0.150f) }, 7, new Vector3(0, 0, 0.045f), false);
                shin.Tube(new Ring[] { new Ring(-0.465f, 0.087f, 0.147f), new Ring(-0.410f, 0.084f, 0.142f), new Ring(-0.375f, 0.069f, 0.083f) }, 3, new Vector3(0, 0, 0.043f), false);
                shins[side] = AddPart(label + "denim shin and workshoe", thighs[side], new Vector3(0, -0.441f, 0), shin);
            }
        }

        internal void Animate(float now, float speed)
        { Animate(now, speed, false, false); }

        internal Vector3 CarryPosition { get { return forearms[1].TransformPoint(new Vector3(0, -0.31f, 0.035f)); } }

        internal void Animate(float now, float speed, bool seated, bool holding)
        {
            if (root == null || Single.IsNaN(now) || Single.IsInfinity(now)) return;
            if (Single.IsNaN(speed) || Single.IsInfinity(speed) || speed < 0) speed = 0;
            float delta = lastTime < 0 ? 0 : Mathf.Clamp(now - lastTime, 0, 0.1f);
            lastTime = now;
            float target = Mathf.Clamp01(speed / 2.1f);
            stride = Mathf.MoveTowards(stride, target, delta * 4);
            phase += delta * (4.8f + Mathf.Clamp(speed, 0, 6) * 1.2f) * stride;
            if (phase > Mathf.PI * 2) phase -= Mathf.PI * 2;
            float swing = Mathf.Sin(phase) * 26 * stride;
            body.localPosition = new Vector3(0, Mathf.Abs(Mathf.Sin(phase)) * 0.016f * stride + Mathf.Sin(now * 1.7f) * 0.002f * (1 - stride), 0);
            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? 1 : -1;
                thighs[side].localRotation = Quaternion.Euler(swing * sign, 0, 0);
                shins[side].localRotation = Quaternion.Euler(Mathf.Max(0, -Mathf.Sin(phase) * sign) * 28 * stride, 0, 0);
                arms[side].localRotation = Quaternion.Euler(-swing * sign * 0.70f, 0, side == 0 ? -4 : 4);
                forearms[side].localRotation = Quaternion.Euler(-7 - Mathf.Max(0, Mathf.Sin(phase) * sign) * 13 * stride, 0, 0);
                if (seated)
                {
                    thighs[side].localRotation = Quaternion.Euler(-85, 0, 0);
                    shins[side].localRotation = Quaternion.Euler(80, 0, 0);
                    arms[side].localRotation = Quaternion.Euler(-55, 0, side == 0 ? -8 : 8);
                    forearms[side].localRotation = Quaternion.Euler(-25, 0, 0);
                }
                else if (holding && side == 1)
                {
                    arms[side].localRotation = Quaternion.Euler(-35, 0, 5);
                    forearms[side].localRotation = Quaternion.Euler(-70, 0, 0);
                }
            }
        }

        private Transform AddPart(string name, Transform parent, Vector3 position, Builder builder)
        {
            GameObject part = Node(name, parent, position);
            Mesh mesh = builder.ToMesh(name);
            meshes.Add(mesh);
            VertexCount += mesh.vertexCount;
            TriangleCount += builder.TriangleCount;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            part.AddComponent<MeshRenderer>().sharedMaterial = material;
            return part.transform;
        }

        private static GameObject Node(string name, Transform parent, Vector3 position)
        {
            GameObject node = new GameObject(name);
            node.layer = 2; // Ignore Raycast. No colliders or rigidbodies are created.
            node.hideFlags = HideFlags.DontSave;
            node.transform.parent = parent;
            node.transform.localPosition = position;
            node.transform.localRotation = Quaternion.identity;
            node.transform.localScale = Vector3.one;
            return node;
        }

        public void Dispose()
        {
            if (root != null) { root.SetActive(false); UnityEngine.Object.Destroy(root); root = null; }
            for (int i = 0; i < meshes.Count; i++) if (meshes[i] != null) UnityEngine.Object.Destroy(meshes[i]);
            meshes.Clear();
            if (material != null) { UnityEngine.Object.Destroy(material); material = null; }
            if (atlas != null) { UnityEngine.Object.Destroy(atlas); atlas = null; }
        }

        private static Texture2D MakeAtlas(CharacterStyle style)
        {
            Texture2D texture = new Texture2D(256, 128, TextureFormat.RGB24, false);
            try
            {
            texture.name = "MSCMP.OriginalPixelAtlas." + style;
            texture.hideFlags = HideFlags.DontSave;
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            Color[] pixels = new Color[256 * 128];
            Color skin = new Color(0.69f, 0.53f, 0.42f);
            for (int y = 0; y < 128; y++) for (int x = 0; x < 256; x++)
            {
                int u = x % 64, v = y % 64, tile = x / 64 + (y / 64) * 4;
                float noise = (((u * 17 + v * 31 + u * v * 3) % 23) - 11) * 0.0018f;
                Color colour;
                if (tile == 0)
                {
                    colour = style == CharacterStyle.Male ? new Color(0.31f, 0.36f, 0.34f) : new Color(0.43f, 0.27f, 0.25f);
                    float check = (u % 16 < 4 ? -0.055f : 0) + (v % 16 < 4 ? -0.050f : 0);
                    if (u % 16 == 8 || v % 16 == 8) check += 0.035f;
                    colour += new Color(check, check, check);
                }
                else if (tile == 1)
                {
                    colour = new Color(0.22f, 0.29f, 0.35f);
                    float weave = (u + v) % 4 == 0 ? 0.027f : 0;
                    colour += new Color(weave, weave, weave);
                    if (u == 3 || u == 60) colour = new Color(0.42f, 0.39f, 0.28f);
                    if (v > 24 && v < 36) colour += new Color(0.025f, 0.025f, 0.025f);
                }
                else if (tile == 2 || tile == 4) colour = skin;
                else if (tile == 3)
                {
                    colour = new Color(0.20f, 0.17f, 0.13f);
                    if (v % 12 == 0 && u > 20 && u < 44) colour = new Color(0.36f, 0.32f, 0.25f);
                }
                else if (tile == 5)
                {
                    float strand = (u + v / 7) % 7 == 0 ? 0.045f : 0;
                    colour = style == CharacterStyle.Male ? new Color(0.29f + strand, 0.22f + strand, 0.13f + strand) : new Color(0.23f + strand, 0.16f + strand, 0.10f + strand);
                }
                else if (tile == 6) colour = new Color(0.65f, 0.62f, 0.53f);
                else colour = new Color(0.13f, 0.115f, 0.10f);
                colour += new Color(noise, noise, noise);
                if (tile == 4) colour = Face(u, v, colour);
                pixels[y * 256 + x] = colour;
            }
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
            }
            catch { UnityEngine.Object.Destroy(texture); throw; }
        }

        private static Color Face(int x, int y, Color skin)
        {
            bool left = x >= 13 && x <= 24, right = x >= 39 && x <= 50;
            if (((x >= 18 && x <= 20) || (x >= 43 && x <= 45)) && y >= 47 && y <= 50) return new Color(0.16f, 0.20f, 0.20f);
            if ((left || right) && y >= 47 && y <= 49) return new Color(0.66f, 0.64f, 0.56f);
            if ((left || right) && y == 51) return new Color(0.38f, 0.28f, 0.22f);
            if ((left || right) && y >= 54 && y <= 55) return new Color(0.27f, 0.20f, 0.14f);
            if (x >= 24 && x <= 39 && y == 18) return new Color(0.41f, 0.26f, 0.21f);
            if (x >= 26 && x <= 37 && y == 17) return new Color(0.56f, 0.36f, 0.29f);
            if ((x == 27 || x == 36) && y >= 30 && y <= 33) return new Color(0.51f, 0.37f, 0.28f);
            if (y < 10 || x < 5 || x > 58) return skin * 0.94f;
            return skin;
        }

        private struct Ring
        {
            internal float Y, Width, Depth;
            internal Ring(float y, float width, float depth) { Y = y; Width = width; Depth = depth; }
        }

        private sealed class Builder
        {
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<Vector2> uv = new List<Vector2>();
            private readonly List<int> triangles = new List<int>();
            private static readonly Vector2[] Section = new Vector2[] {
                new Vector2(-0.65f, 1), new Vector2(0.65f, 1), new Vector2(1, 0.45f), new Vector2(1, -0.50f),
                new Vector2(0.60f, -1), new Vector2(-0.60f, -1), new Vector2(-1, -0.50f), new Vector2(-1, 0.45f) };
            internal int TriangleCount { get { return triangles.Count / 3; } }

            internal void Tube(Ring[] rings, int tile, Vector3 offset, bool openFront)
            {
                for (int row = 0; row < rings.Length - 1; row++) for (int side = openFront ? 1 : 0; side < 8; side++)
                {
                    int next = (side + 1) % 8;
                    Quad(Point(rings[row], side) + offset, Point(rings[row], next) + offset,
                        Point(rings[row + 1], next) + offset, Point(rings[row + 1], side) + offset, tile);
                }
                if (openFront) return;
                for (int side = 0; side < 8; side++)
                {
                    int next = (side + 1) % 8;
                    Ring bottom = rings[0], top = rings[rings.Length - 1];
                    Triangle(new Vector3(0, bottom.Y, 0) + offset, Point(bottom, next) + offset, Point(bottom, side) + offset, tile);
                    Triangle(new Vector3(0, top.Y, 0) + offset, Point(top, side) + offset, Point(top, next) + offset, tile);
                }
            }

            private static Vector3 Point(Ring ring, int side)
            { return new Vector3(Section[side].x * ring.Width, ring.Y, Section[side].y * ring.Depth); }

            internal void Box(Vector3 center, Vector3 size, int tile)
            {
                float x = size.x * 0.5f, y = size.y * 0.5f, z = size.z * 0.5f;
                Vector3 a = center + new Vector3(-x, -y, z), b = center + new Vector3(x, -y, z), c = center + new Vector3(x, y, z), d = center + new Vector3(-x, y, z);
                Vector3 e = center + new Vector3(-x, -y, -z), f = center + new Vector3(x, -y, -z), g = center + new Vector3(x, y, -z), h = center + new Vector3(-x, y, -z);
                Quad(a, b, c, d, tile); Quad(f, e, h, g, tile); Quad(b, f, g, c, tile); Quad(e, a, d, h, tile); Quad(d, c, g, h, tile); Quad(e, f, b, a, tile);
            }

            internal void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int tile)
            {
                int start = vertices.Count;
                vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
                uv.Add(Uv(tile, 0, 0)); uv.Add(Uv(tile, 1, 0)); uv.Add(Uv(tile, 1, 1)); uv.Add(Uv(tile, 0, 1));
                triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
                triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
            }

            internal void Triangle(Vector3 a, Vector3 b, Vector3 c, int tile)
            {
                int start = vertices.Count;
                vertices.Add(a); vertices.Add(b); vertices.Add(c);
                uv.Add(Uv(tile, 0, 0)); uv.Add(Uv(tile, 1, 0)); uv.Add(Uv(tile, 0.5f, 1));
                triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
            }

            private static Vector2 Uv(int tile, float u, float v)
            { return new Vector2(((tile % 4) * 64 + 1 + u * 62) / 256f, ((tile / 4) * 64 + 1 + v * 62) / 128f); }

            internal Mesh ToMesh(string name)
            {
                Mesh result = new Mesh();
                try
                {
                result.name = "MSCMP.Original." + name;
                result.hideFlags = HideFlags.DontSave;
                result.vertices = vertices.ToArray(); result.uv = uv.ToArray(); result.triangles = triangles.ToArray();
                result.RecalculateNormals(); result.RecalculateBounds();
                return result;
                }
                catch { UnityEngine.Object.Destroy(result); throw; }
            }
        }
    }
}
