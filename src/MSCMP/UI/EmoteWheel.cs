using System;
using System.IO;
using MSCMP.Game;
using UnityEngine;

namespace MSCMP.UI
{
    /// <summary>
    /// The emote wheel. Hold the emote key: the wheel appears; point the mouse at an emote and
    /// let go of the key to play it (let go in the middle to cancel). Tap the key instead and
    /// the wheel stays open: click an emote, or press 1-8. Esc, a right click or the key again
    /// closes it. The key can be changed in the F2 panel (or in MSCLoader's keybinds).
    /// </summary>
    internal static class EmoteWheel
    {
        public static bool Open { get; private set; }
        public static bool Capturing { get; private set; }  // waiting for the new key

        static float openedAt;
        static bool holding;
        static int hover;               // 0 = none, else emote id
        static bool keyLoaded;
        static KeyCode chosenKey = KeyCode.None;

        const float DeadZone = 50f, Radius = 160f;

        // ================================================================ the key

        static string KeyFile
        {
            get
            {
                string log = Log.FilePath;
                string dir = log != null ? Path.GetDirectoryName(Path.GetFullPath(log)) : ".";
                return Path.Combine(dir, "emote_wheel_key.txt");
            }
        }

        /// <summary>The key that opens the wheel: your choice from the F2 panel, else MSCLoader's keybind.</summary>
        public static KeyCode Key
        {
            get
            {
                if (!keyLoaded)
                {
                    keyLoaded = true;
                    try
                    {
                        if (File.Exists(KeyFile)) chosenKey = (KeyCode)Enum.Parse(typeof(KeyCode), File.ReadAllText(KeyFile).Trim());
                    }
                    catch { chosenKey = KeyCode.None; }
                }
                if (chosenKey != KeyCode.None) return chosenKey;
                KeyCode bound;
                if (TryKeybindKey(Options.EmoteKey, out bound) && bound != KeyCode.None) return bound;
                return KeyCode.B;
            }
        }

        public static void StartCapture() { Capturing = true; }

        // MSCLoader's keybind keeps its key in a field whose name differs between versions:
        // find the KeyCode member that isn't the modifier.
        static System.Reflection.MemberInfo keyMember;
        static bool keyMemberSearched;

        static System.Reflection.MemberInfo KeyMember(object kb)
        {
            if (keyMemberSearched || kb == null) return keyMember;
            keyMemberSearched = true;
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            for (Type t = kb.GetType(); t != null && keyMember == null; t = t.BaseType)
            {
                foreach (System.Reflection.FieldInfo f in t.GetFields(all))
                    if (f.FieldType == typeof(KeyCode) && f.Name.IndexOf("mod", StringComparison.OrdinalIgnoreCase) < 0) { keyMember = f; break; }
                if (keyMember != null) break;
                foreach (System.Reflection.PropertyInfo pr in t.GetProperties(all))
                    if (pr.PropertyType == typeof(KeyCode) && pr.CanRead && pr.Name.IndexOf("mod", StringComparison.OrdinalIgnoreCase) < 0) { keyMember = pr; break; }
            }
            return keyMember;
        }

        static bool TryKeybindKey(object kb, out KeyCode key)
        {
            key = KeyCode.None;
            try
            {
                System.Reflection.MemberInfo m = KeyMember(kb);
                System.Reflection.FieldInfo f = m as System.Reflection.FieldInfo;
                System.Reflection.PropertyInfo p = m as System.Reflection.PropertyInfo;
                if (f != null) key = (KeyCode)f.GetValue(kb);
                else if (p != null) key = (KeyCode)p.GetValue(kb, null);
                else return false;
                return true;
            }
            catch { return false; }
        }

        static void SetKeybindKey(object kb, KeyCode key)
        {
            try
            {
                System.Reflection.MemberInfo m = KeyMember(kb);
                System.Reflection.FieldInfo f = m as System.Reflection.FieldInfo;
                System.Reflection.PropertyInfo p = m as System.Reflection.PropertyInfo;
                if (f != null) f.SetValue(kb, key);
                else if (p != null && p.CanWrite) p.SetValue(kb, key, null);
            }
            catch (Exception e) { Log.ExceptionOnce("Setting the emote keybind", e); }
        }

        static void Capture()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { Capturing = false; return; }
            foreach (KeyCode k in (KeyCode[])Enum.GetValues(typeof(KeyCode)))
            {
                if (k == KeyCode.None || k == KeyCode.Mouse0 || k == KeyCode.Mouse1 || k == KeyCode.Escape) continue;
                if (!Input.GetKeyDown(k)) continue;
                chosenKey = k;
                keyLoaded = true;
                Capturing = false;
                try { File.WriteAllText(KeyFile, k.ToString()); } catch (Exception e) { Log.Exception("Saving the emote key", e); }
                SetKeybindKey(Options.EmoteKey, k); // keep MSCLoader's keybind list in step
                Notifications.Show("Emote wheel key: " + k);
                return;
            }
        }

        // ================================================================ per frame

        /// <summary>Called by the UI every frame. <paramref name="allowed"/>: in the world, not typing.</summary>
        public static void Tick(bool allowed)
        {
            if (Capturing) { Capture(); return; }
            if (!allowed) { Open = false; return; }
            KeyCode key = Key;
            bool down = Input.GetKeyDown(key), up = Input.GetKeyUp(key);
            float now = Time.realtimeSinceStartup;

            if (!Open)
            {
                if (!down) return;
                Open = true;
                holding = true;
                openedAt = now;
                hover = 0;
                return;
            }

            UpdateHover();
            if (holding && up)
            {
                if (hover == 0 && now - openedAt < 0.3f) holding = false; // a tap: stay open, pick with a click
                else { Play(hover); return; }
            }
            else if (!holding && down) { Close(); return; }
            if (!holding && Input.GetMouseButtonDown(0)) { Play(hover); return; }
            if (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
            for (int i = 1; i <= Emotes.Count && i <= 9; i++)
                if (Input.GetKeyDown(KeyCode.Alpha0 + i)) { Play(i); return; }
        }

        public static void Close() { Open = false; holding = false; hover = 0; }

        static void Play(int id)
        {
            Close();
            if (id >= 1 && id <= Emotes.Count) Emotes.Play((byte)id);
        }

        static Vector2 Center { get { return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f); } }

        static void UpdateHover()
        {
            Vector2 m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            Vector2 d = m - Center;
            if (d.magnitude < DeadZone) { hover = 0; return; }
            float slice = 360f / Emotes.Count;
            float angle = Mathf.Atan2(d.x, -d.y) * Mathf.Rad2Deg;   // 0 = straight up, clockwise
            if (angle < 0f) angle += 360f;
            hover = (Mathf.RoundToInt(angle / slice) % Emotes.Count) + 1;
        }

        // ================================================================ drawing (OnGUI)

        static GUIStyle item, itemHot, middle, hint;
        static Texture2D ring;

        public static void Draw()
        {
            if (!Open) return;
            if (item == null)
            {
                item = new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.MiddleCenter };
                itemHot = new GUIStyle(item) { fontSize = 18, fontStyle = FontStyle.Bold };
                itemHot.normal.textColor = new Color(1f, 0.85f, 0.3f);
                middle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                hint = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
                ring = Disc(128, new Color(0f, 0f, 0f, 0.55f));
            }
            Vector2 c = Center;
            float r = Radius + 70f;
            GUI.DrawTexture(new Rect(c.x - r, c.y - r, r * 2f, r * 2f), ring);

            float slice = 360f / Emotes.Count;
            for (int i = 1; i <= Emotes.Count; i++)
            {
                float a = (i - 1) * slice * Mathf.Deg2Rad;
                Vector2 p = c + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * Radius;
                bool hot = i == hover;
                float w = hot ? 132f : 116f, h = hot ? 40f : 34f;
                GUI.Box(new Rect(p.x - w / 2f, p.y - h / 2f, w, h), i + "  " + Emotes.Names[i], hot ? itemHot : item);
            }
            GUI.Label(new Rect(c.x - 120f, c.y - 18f, 240f, 36f), hover > 0 ? Emotes.Names[hover] : "Emotes", middle);
            string how = holding ? "Point at an emote and let go of " + Key + "  ·  let go in the middle to cancel"
                                 : "Click an emote or press 1-" + Emotes.Count + "  ·  Esc or right-click closes";
            GUI.Label(new Rect(c.x - 300f, c.y + r + 6f, 600f, 22f), how, hint);
        }

        static Texture2D Disc(int n, Color color)
        {
            Texture2D t = new Texture2D(n, n, TextureFormat.ARGB32, false);
            float rad = n * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - rad) * (x + 0.5f - rad) + (y + 0.5f - rad) * (y + 0.5f - rad));
                    float inner = rad * 0.22f;
                    float a = Mathf.Clamp01(rad - d) * Mathf.Clamp01(d - inner);
                    t.SetPixel(x, y, new Color(color.r, color.g, color.b, color.a * a));
                }
            t.Apply();
            return t;
        }
    }
}
