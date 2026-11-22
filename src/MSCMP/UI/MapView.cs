using System;
using System.Collections.Generic;
using MSCMP.Game;
using MSCMP.Net;
using MSCMP.Sync;
using UnityEngine;

namespace MSCMP.UI
{
    /// <summary>
    /// The map (M). A picture of the whole world taken from above once you are in it, which
    /// you can drag around and zoom (scroll wheel). It shows you, your friends (on foot, or in
    /// which vehicle), parked vehicles, bots, pings and routes. Click to add waypoints and plan
    /// a route, like in SnowRunner; the next waypoint is also shown in the world. Share the
    /// route and your friends see it too. No teleporting from the map.
    /// </summary>
    internal static class MapView
    {
        public static bool Open;

        // ---------------------------------------------------------------- picture of the world
        static Texture2D picture;        // a normal image: the game can't throw it away
        static float xMin = -2600f, zMin = -2600f, size = 5200f;
        static float captureAt = -1f;
        const int PictureSize = 2048;   // about 2.5 m per pixel; small enough to be cheap
        static int captures;
        static float nextCaptureAllowed;

        // ---------------------------------------------------------------- view
        static Vector2 center;          // world x, z in the middle of the screen
        static float ppm = 0.2f;        // screen pixels per metre
        static bool viewSet;
        static bool mouseDown, dragged;
        static Vector2 pressPos, lastMouse;

        // ---------------------------------------------------------------- routes
        public static readonly List<Vector3> Route = new List<Vector3>();
        public static int Next;          // index of the next waypoint to reach
        public static bool Sharing;
        static readonly Dictionary<byte, List<Vector3>> friendRoutes = new Dictionary<byte, List<Vector3>>();

        static Texture2D arrow, dot, white;
        static GUIStyle label, small, bar, button;

        public static void Init()
        {
            Session.On(Msg.RouteShare, OnRouteShare);
            Session.PlayerLeft += p => { if (p != null) friendRoutes.Remove(p.Slot); };
            Session.Ended += r => friendRoutes.Clear();
        }

        public static void OnWorldReady() { captureAt = Time.realtimeSinceStartup + 3f; }

        public static void OnWorldLeft()
        {
            Open = false;
            viewSet = false;
            captureAt = -1f;
            captures = 0;
            Route.Clear();
            Next = 0;
            Sharing = false;
            if (picture != null) { UnityEngine.Object.Destroy(picture); picture = null; }
            places.Clear();
        }

        // ================================================================ per frame

        public static void Update()
        {
            if (!MP.InGame || !LocalPlayer.Bound) { Open = false; return; }
            float now = Time.realtimeSinceStartup;
            if (captureAt > 0f && now >= captureAt) { captureAt = -1f; Capture(); }
            // If taking it failed, try again a little later (at most 3 times): it's a full
            // render of the whole world and must never happen every frame.
            if (picture == null && captureAt < 0f && captures > 0 && captures < 3 && now >= nextCaptureAllowed) Capture();

            AdvanceRoute();
            if (Open) HandleInput();
        }

        /// <summary>Reached a waypoint? Then the next one is the target.</summary>
        static void AdvanceRoute()
        {
            if (Next >= Route.Count) return;
            Vector3 me = LocalPlayer.Feet;
            Vector3 d = Route[Next] - me;
            d.y = 0f;
            bool driving = !string.IsNullOrEmpty(LocalPlayer.CurrentVehicleName) || LocalPlayer.PassengerOf != null;
            if (d.magnitude > (driving ? 22f : 8f)) return;
            Next++;
            if (Next >= Route.Count) Notifications.Show("You've reached the end of your route.");
        }

        /// <summary>Takes the picture of the world from high above (once per load).</summary>
        static void Capture()
        {
            captures++;
            nextCaptureAllowed = Time.realtimeSinceStartup + 30f;
            GameObject go = null;
            Light sun = null;
            RenderTexture rt = null;
            RenderTexture previous = RenderTexture.active;
            bool fog = RenderSettings.fog;
            Color ambient = RenderSettings.ambientLight;
            try
            {
                ComputeBounds();
                rt = new RenderTexture(PictureSize, PictureSize, 24);
                rt.Create();
                go = new GameObject("MSCMP_MapCamera");
                Camera cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.orthographicSize = size * 0.5f;
                cam.aspect = 1f;
                cam.nearClipPlane = 1f;
                cam.farClipPlane = 4000f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.16f, 0.24f, 0.30f, 1f);
                cam.targetTexture = rt;
                go.transform.position = new Vector3(xMin + size * 0.5f, 2000f, zMin + size * 0.5f);
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // straight down, north up

                // Daylight for the picture, whatever the time of day.
                GameObject sunGo = new GameObject("MSCMP_MapSun");
                sunGo.transform.parent = go.transform;
                sunGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
                sun = sunGo.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.intensity = 0.9f;
                sun.shadows = LightShadows.None;
                RenderSettings.fog = false;
                RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f, 1f);
                cam.Render();
                // Copy it into a normal image straight away: the render texture itself can be
                // thrown away by the game at any moment.
                RenderTexture.active = rt;
                Texture2D img = new Texture2D(PictureSize, PictureSize, TextureFormat.RGB24, false);
                img.ReadPixels(new Rect(0f, 0f, PictureSize, PictureSize), 0, 0);
                img.Apply(false);
                if (picture != null) UnityEngine.Object.Destroy(picture);
                picture = img;
                FindPlaces();
                Log.Info("Map picture taken (" + size.ToString("0") + " m across, " + places.Count + " places named).");
            }
            catch (Exception e) { Log.Exception("Map picture", e); }
            finally
            {
                RenderTexture.active = previous;
                RenderSettings.fog = fog;
                RenderSettings.ambientLight = ambient;
                if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
                if (go != null) UnityEngine.Object.DestroyImmediate(go); // gone before the normal view is drawn
            }
        }

        /// <summary>How big the world is: the terrain, or a generous square around the map.</summary>
        static void ComputeBounds()
        {
            Bounds b = new Bounds();
            bool any = false;
            foreach (string rootName in new[] { "TERRAIN", "MAP" })
            {
                Transform root = GameUtil.FindRoot(rootName);
                if (root == null) continue;
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    Vector3 sz = r.bounds.size;
                    if (sz.x < 50f && sz.z < 50f) continue;      // small things don't decide the size
                    if (sz.x > 3000f || sz.z > 3000f) continue;   // sky domes, water planes...
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
            }
            if (!any || b.size.x < 500f) return; // keep the default
            float s = Mathf.Min(Mathf.Max(b.size.x, b.size.z) + 200f, 6000f);
            xMin = b.center.x - s * 0.5f;
            zMin = b.center.z - s * 0.5f;
            size = s;
        }

        // ================================================================ places

        sealed class Place { public string Name; public Vector3 At; }
        static readonly List<Place> places = new List<Place>();

        /// <summary>Well-known places, found by the names they have in the game's world. A
        /// spawn point is used when there is one; otherwise the middle of the place's buildings.</summary>
        static readonly string[][] PlaceList =
        {
            new[] { "Home", "YARD/SpawnToHome", "YARD" },
            new[] { "Teimo's shop & pub", "STORE/SpawnToStore", "STORE" },
            new[] { "Fleetari's repair shop", "REPAIRSHOP/SpawnToRepair", "REPAIRSHOP" },
            new[] { "Inspection", "INSPECTION" },
            new[] { "Ventti's cabin", "CABIN" },
            new[] { "Cottage", "COTTAGE" },
            new[] { "Farm", "FARM" },
            new[] { "Dance hall", "DANCEHALL" },
            new[] { "Landfill", "LANDFILL" },
            new[] { "Water facility", "WATERFACILITY" },
            new[] { "Airport", "AIRPORT" },
            new[] { "Drag strip", "DRAGRACE", "DRAGSTRIP" },
            new[] { "Church", "CHURCH" },
            new[] { "Kilju guy", "KILJUGUY" },
            new[] { "Swamp", "SWAMP" },
            new[] { "Sewage", "SEWAGE" },
            new[] { "Soccer field", "SOCCER" },
            new[] { "Peräjärvi", "PERAJARVI" },
            new[] { "Rykipohja", "RYKIPOHJA" },
            new[] { "Loppe", "LOPPE" },
            new[] { "Kesseli", "KESSELI" },
        };

        static void FindPlaces()
        {
            places.Clear();
            foreach (string[] entry in PlaceList)
            {
                try
                {
                    Vector3 at;
                    if (Locate(entry, out at)) places.Add(new Place { Name = entry[0], At = at });
                }
                catch (Exception e) { Log.ExceptionOnce("Map place " + entry[0], e); }
            }
        }

        static bool Locate(string[] entry, out Vector3 at)
        {
            at = Vector3.zero;
            for (int i = 1; i < entry.Length; i++)
            {
                string path = entry[i];
                Transform t = path.IndexOf('/') >= 0 ? GameUtil.FindPath(path) : GameUtil.FindRoot(path);
                if (t == null) continue;
                if (path.IndexOf('/') >= 0) { at = t.position; return true; } // a spawn point: exact
                // A whole area: the middle of its buildings (leaving out huge things like terrain).
                Bounds b = new Bounds();
                bool any = false;
                foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true))
                {
                    Vector3 sz = r.bounds.size;
                    if (sz.x > 300f || sz.z > 300f) continue;
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
                if (any && b.size.x < 1500f && b.size.z < 1500f) { at = b.center; return true; }
                if (t.position.sqrMagnitude > 1f) { at = t.position; return true; }
            }
            return false;
        }

        // ================================================================ input

        static float MinPpm { get { return Mathf.Min(Screen.width, Screen.height) / size; } }
        const float MaxPpm = 6f;
        static readonly Rect Toolbar = new Rect(0f, 0f, 100000f, 44f);

        static void HandleInput()
        {
            if (!viewSet) { CenterOnMe(); ppm = Mathf.Max(MinPpm * 3f, 0.6f); viewSet = true; }
            Vector2 mouse = Input.mousePosition; // y up
            Vector2 gui = new Vector2(mouse.x, Screen.height - mouse.y);

            float scroll = Input.mouseScrollDelta.y * 0.1f;
            if (Mathf.Abs(scroll) > 0.001f)
            {
                Vector2 before = ScreenToWorld(gui);
                ppm = Mathf.Clamp(ppm * Mathf.Pow(1.6f, scroll * 10f), MinPpm, MaxPpm);
                Vector2 after = ScreenToWorld(gui);
                center += before - after; // zoom around the cursor
            }

            if (Input.GetMouseButtonDown(0) && !Toolbar.Contains(gui))
            {
                mouseDown = true;
                dragged = false;
                pressPos = mouse;
                lastMouse = mouse;
            }
            if (mouseDown && Input.GetMouseButton(0))
            {
                if ((mouse - pressPos).sqrMagnitude > 25f) dragged = true;
                if (dragged)
                {
                    Vector2 d = mouse - lastMouse;
                    center.x -= d.x / ppm;
                    center.y -= d.y / ppm;
                }
                lastMouse = mouse;
            }
            if (mouseDown && Input.GetMouseButtonUp(0))
            {
                mouseDown = false;
                if (!dragged) AddWaypoint(ScreenToWorld(gui));
            }
            if (Input.GetMouseButtonDown(1) && !Toolbar.Contains(gui)) RemoveNear(gui);
            if (Input.GetKeyDown(KeyCode.C)) CenterOnMe();
            if (Input.GetKeyDown(KeyCode.Backspace) && Route.Count > 0) { Route.RemoveAt(Route.Count - 1); RouteChanged(); }
        }

        static void CenterOnMe()
        {
            Vector3 f = LocalPlayer.Feet;
            center = new Vector2(f.x, f.z);
        }

        static void AddWaypoint(Vector2 xz)
        {
            float y = 0f;
            RaycastHit hit;
            if (Physics.Raycast(new Vector3(xz.x, 1500f, xz.y), Vector3.down, out hit, 3000f, GameUtil.WorldMask)) y = hit.point.y;
            if (Route.Count >= 64) { Notifications.Show("That's a long route already (64 waypoints)."); return; }
            if (Next >= Route.Count) { Route.Clear(); Next = 0; } // a finished route starts over
            Route.Add(new Vector3(xz.x, y, xz.y));
            RouteChanged();
        }

        static void RemoveNear(Vector2 gui)
        {
            for (int i = 0; i < Route.Count; i++)
            {
                if ((WorldToScreen(Route[i]) - gui).sqrMagnitude > 14f * 14f) continue;
                Route.RemoveAt(i);
                if (Next > i) Next--;
                RouteChanged();
                return;
            }
        }

        public static void ClearRoute()
        {
            Route.Clear();
            Next = 0;
            RouteChanged();
        }

        static void RouteChanged()
        {
            if (Next > Route.Count) Next = Route.Count;
            if (Sharing) SendRoute();
        }

        // ================================================================ sharing

        public static void SetSharing(bool on)
        {
            Sharing = on;
            SendRoute();
            if (on) Notifications.Show("Your friends can see your route now.");
        }

        static void SendRoute()
        {
            if (!Session.Active || !Session.Connected || Session.Local == null) return;
            NetWriter w = Session.Begin(Msg.RouteShare);
            w.Byte(Session.Local.Slot);
            List<Vector3> pts = Sharing ? Route : new List<Vector3>();
            w.Byte((byte)Mathf.Min(pts.Count, 64));
            for (int i = 0; i < pts.Count && i < 64; i++) w.Vector3(pts[i]);
            Session.SendToAll(true, Channel.Reliable);
        }

        static void OnRouteShare(NetPlayer from, NetReader r)
        {
            byte slot = r.Byte();
            int n = r.Byte();
            List<Vector3> pts = new List<Vector3>();
            for (int i = 0; i < n; i++) pts.Add(r.Vector3());
            if (from == null) return;
            NetPlayer who = Session.IsHost ? from : Session.BySlot(slot);
            if (who == null || who.IsLocal) return;
            if (pts.Count == 0) friendRoutes.Remove(who.Slot);
            else
            {
                bool isNew = !friendRoutes.ContainsKey(who.Slot);
                friendRoutes[who.Slot] = pts;
                if (isNew) Notifications.Show(who.Name + " shared a route. Open the map (M) to see it.");
            }
            if (Session.IsHost)
            {
                NetWriter w = Session.Begin(Msg.RouteShare);
                w.Byte(who.Slot);
                w.Byte((byte)pts.Count);
                foreach (Vector3 p in pts) w.Vector3(p);
                Session.BroadcastInGame(true, Channel.Reliable, who);
            }
        }

        /// <summary>Friends' shared routes (for the in-world markers).</summary>
        public static IEnumerable<KeyValuePair<byte, List<Vector3>>> FriendRoutes { get { return friendRoutes; } }

        // ================================================================ drawing

        static Vector2 WorldToScreen(Vector3 w)
        {
            return new Vector2(Screen.width * 0.5f + (w.x - center.x) * ppm, Screen.height * 0.5f - (w.z - center.y) * ppm);
        }

        static Vector2 ScreenToWorld(Vector2 gui)
        {
            return new Vector2(center.x + (gui.x - Screen.width * 0.5f) / ppm, center.y - (gui.y - Screen.height * 0.5f) / ppm);
        }

        static void EnsureArt()
        {
            if (white != null) return;
            white = Solid(Color.white);
            dot = Circle(16);
            arrow = Arrow(32);
            label = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, richText = false };
            small = new GUIStyle(label) { fontSize = 11, fontStyle = FontStyle.Normal };
            bar = new GUIStyle(GUI.skin.box);
            bar.normal.background = Solid(new Color(0f, 0f, 0f, 0.75f));
            button = new GUIStyle(GUI.skin.button) { fontSize = 13 };
        }

        public static void Draw()
        {
            if (!Open) return;
            EnsureArt();
            GUI.depth = -200;
            GUI.color = new Color(0.05f, 0.07f, 0.08f, 1f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), white);
            GUI.color = Color.white;

            // The picture: only the visible part of it.
            if (picture != null)
            {
                Vector2 tl = WorldToScreen(new Vector3(xMin, 0f, zMin + size));
                Rect full = new Rect(tl.x, tl.y, size * ppm, size * ppm);
                Rect screen = new Rect(0f, 0f, Screen.width, Screen.height);
                Rect vis = Intersect(full, screen);
                if (vis.width > 0f && vis.height > 0f)
                {
                    Rect uv = new Rect((vis.x - full.x) / full.width, 1f - (vis.yMax - full.y) / full.height, vis.width / full.width, vis.height / full.height);
                    GUI.DrawTextureWithTexCoords(vis, picture, uv);
                }
            }
            else GUI.Label(new Rect(20f, 60f, 500f, 30f), "Taking the picture of the world...", label);

            // Places.
            foreach (Place pl in places)
            {
                Vector2 p = WorldToScreen(pl.At);
                DrawDot(p, 9f, new Color(1f, 0.55f, 0.2f, 0.95f));
                Label(p + new Vector2(8f, -10f), pl.Name, new Color(1f, 0.75f, 0.45f, 1f), label);
            }

            // Parked vehicles.
            foreach (NetVehicle v in VehicleSync.All)
            {
                if (v.Root == null || !v.Root.gameObject.activeInHierarchy) continue;
                Vector2 p = WorldToScreen(v.Root.position);
                DrawDot(p, 7f, new Color(0.85f, 0.85f, 0.85f, 0.85f));
                if (ppm > 0.8f) Label(p + new Vector2(7f, -9f), VehicleLabel(v), new Color(0.9f, 0.9f, 0.9f, 0.85f), small);
            }

            // Routes: friends' first, then ours.
            foreach (KeyValuePair<byte, List<Vector3>> kv in friendRoutes)
            {
                Color c = MultiplayerUI.SlotColors[kv.Key % MultiplayerUI.SlotColors.Length];
                c.a = 0.75f;
                DrawPolyline(kv.Value, c, 2.5f, null);
            }
            if (Route.Count > 0)
            {
                List<Vector3> pts = new List<Vector3> { LocalPlayer.Feet };
                for (int i = Next; i < Route.Count; i++) pts.Add(Route[i]);
                DrawPolyline(pts, new Color(1f, 0.82f, 0.2f, 0.95f), 3.5f, null);
                for (int i = 0; i < Route.Count; i++)
                {
                    Vector2 p = WorldToScreen(Route[i]);
                    bool done = i < Next;
                    DrawDot(p, i == Next ? 14f : 10f, done ? new Color(0.5f, 0.5f, 0.5f, 0.8f) : new Color(1f, 0.82f, 0.2f, 1f));
                    Label(p + new Vector2(8f, -9f), (i + 1).ToString(), Color.white, small);
                }
            }

            // Bots.
            foreach (Bots.Bot b in Bots.All) DrawDot(WorldToScreen(b.Feet), 6f, new Color(0.7f, 0.7f, 1f, 0.9f));
            foreach (NetPlayer rb in Bots.RemotePlayers) { Vector3 f; float y; if (PlayerCare.WorldFeet(rb, out f, out y)) DrawDot(WorldToScreen(f), 6f, new Color(0.7f, 0.7f, 1f, 0.9f)); }

            // Players.
            foreach (NetPlayer p in Session.Remotes)
            {
                Vector3 feet; float yaw;
                if (p.Phase != PlayerPhase.Playing || !PlayerCare.WorldFeet(p, out feet, out yaw)) continue;
                Color c = MultiplayerUI.SlotColors[p.Slot % MultiplayerUI.SlotColors.Length];
                DrawPlayer(WorldToScreen(feet), yaw, c, p.Name + "  " + Doing(p.Last));
            }
            DrawPlayer(WorldToScreen(LocalPlayer.Feet), LocalPlayer.View.transform.eulerAngles.y, Color.white, "You" + MyDoing());

            // Top bar.
            GUI.Box(new Rect(0f, 0f, Screen.width, 44f), GUIContent.none, bar);
            GUILayout.BeginArea(new Rect(10f, 7f, Screen.width - 20f, 32f));
            GUILayout.BeginHorizontal();
            GUILayout.Label("MAP", label, GUILayout.Width(50));
            if (GUILayout.Button("Centre on me (C)", button, GUILayout.Width(140))) CenterOnMe();
            if (GUILayout.Button("Clear route", button, GUILayout.Width(100))) ClearRoute();
            if (Session.Connected)
            {
                bool share = GUILayout.Toggle(Sharing, " Share my route", button, GUILayout.Width(130));
                if (share != Sharing) SetSharing(share);
            }
            GUILayout.Label("Drag to move · scroll to zoom · click: add waypoint · right-click: remove · Backspace: undo" +
                            (Route.Count > 0 ? "   ·   route " + RouteLength().ToString("0") + " m" : ""), small);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close (M)", button, GUILayout.Width(90))) Open = false;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        static float RouteLength()
        {
            float total = 0f;
            Vector3 prev = LocalPlayer.Feet;
            for (int i = Next; i < Route.Count; i++) { total += Vector3.Distance(prev, Route[i]); prev = Route[i]; }
            return total;
        }

        static string MyDoing()
        {
            PlayerSnapshot s = LocalPlayer.Sample();
            string d = Doing(s);
            return d.Length > 0 ? "  " + d : "";
        }

        /// <summary>"on foot", "driving the van", "on the boat"...</summary>
        public static string Doing(PlayerSnapshot s)
        {
            if (s.Vehicle == 0) return "on foot";
            NetVehicle v = VehicleSync.ById(s.Vehicle);
            string what = v != null ? VehicleLabel(v) : "a vehicle";
            if (s.StandingOnVehicle) return "on the " + what;
            if (s.Seat == 1) return (v != null && (v.IsBike || what == "boat") ? "riding the " : "driving the ") + what;
            return "in the " + what;
        }

        public static string VehicleLabel(NetVehicle v)
        {
            string n = v.Name.ToUpperInvariant();
            if (n.StartsWith("SATSUMA")) return "Satsuma";
            if (n.StartsWith("HAYOSIKO")) return "van";
            if (n.StartsWith("GIFU")) return "truck";
            if (n.StartsWith("KEKMET")) return "tractor";
            if (n.StartsWith("JONNEZ")) return "moped";
            if (n.StartsWith("BOAT")) return "boat";
            if (n.StartsWith("FLATBED")) return "trailer";
            if (n.StartsWith("FERNDALE")) return "Ferndale";
            if (n.StartsWith("RCO_RUSCKO")) return "Ruscko";
            if (n.StartsWith("COMBINE")) return "combine";
            string p = v.Name;
            int i = p.IndexOf('(');
            return (i > 0 ? p.Substring(0, i) : p).Trim().ToLowerInvariant();
        }

        // ---------------------------------------------------------------- drawing helpers

        static void DrawPlayer(Vector2 p, float yaw, Color c, string text)
        {
            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(yaw, p);
            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            GUI.DrawTexture(new Rect(p.x - 13f, p.y - 13f, 26f, 26f), arrow);
            GUI.color = c;
            GUI.DrawTexture(new Rect(p.x - 11f, p.y - 11f, 22f, 22f), arrow);
            GUI.matrix = old;
            Label(p + new Vector2(14f, -10f), text, c, label);
            GUI.color = Color.white;
        }

        static void DrawDot(Vector2 p, float d, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(new Rect(p.x - d * 0.5f, p.y - d * 0.5f, d, d), dot);
            GUI.color = Color.white;
        }

        static void Label(Vector2 at, string text, Color c, GUIStyle s)
        {
            Rect r = new Rect(at.x, at.y, 400f, 20f);
            GUI.color = new Color(0f, 0f, 0f, c.a * 0.85f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, s);
            GUI.color = c;
            GUI.Label(r, text, s);
            GUI.color = Color.white;
        }

        static void DrawPolyline(List<Vector3> pts, Color c, float width, string unused)
        {
            for (int i = 1; i < pts.Count; i++) DrawLine(WorldToScreen(pts[i - 1]), WorldToScreen(pts[i]), c, width);
        }

        static void DrawLine(Vector2 a, Vector2 b, Color c, float width)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.5f) return;
            float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, a);
            GUI.color = c;
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, len, width), white);
            GUI.matrix = old;
            GUI.color = Color.white;
        }

        static Rect Intersect(Rect a, Rect b)
        {
            float x = Mathf.Max(a.x, b.x), y = Mathf.Max(a.y, b.y);
            float xm = Mathf.Min(a.xMax, b.xMax), ym = Mathf.Min(a.yMax, b.yMax);
            return new Rect(x, y, Mathf.Max(0f, xm - x), Mathf.Max(0f, ym - y));
        }

        static Texture2D Solid(Color c)
        {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        static Texture2D Circle(int n)
        {
            Texture2D t = new Texture2D(n, n, TextureFormat.ARGB32, false);
            float r = n * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                    float a = Mathf.Clamp01(r - d);
                    float edge = d > r - 2.5f ? 0.25f : 1f; // dark rim
                    t.SetPixel(x, y, new Color(edge, edge, edge, a));
                }
            t.Apply();
            return t;
        }

        /// <summary>An arrow pointing up (north = the way the player faces at 0 degrees).</summary>
        static Texture2D Arrow(int n)
        {
            Texture2D t = new Texture2D(n, n, TextureFormat.ARGB32, false);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    // Texture rows go bottom to top; the tip is at the top.
                    float fy = (float)y / (n - 1);          // 0 bottom .. 1 top
                    float half = (1f - fy) * 0.45f;          // wide at the bottom, a point at the top
                    float fx = Mathf.Abs((float)x / (n - 1) - 0.5f);
                    bool inside = fx <= half && fy > 0.08f && !(fy < 0.35f && fx < (0.35f - fy) * 0.9f);
                    t.SetPixel(x, y, inside ? Color.white : new Color(1f, 1f, 1f, 0f));
                }
            t.Apply();
            return t;
        }
    }
}
