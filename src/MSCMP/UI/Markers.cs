using System;
using System.Collections.Generic;
using MSCMP.Game;
using MSCMP.Net;
using UnityEngine;

namespace MSCMP.UI
{
    /// <summary>
    /// Finding each other on the big map: a marker for every friend who is far away or
    /// behind you (name and distance, stuck to the edge of the screen), and pings: press
    /// the ping key and everybody sees a "look here" marker where you were looking.
    /// </summary>
    internal static class Markers
    {
        sealed class Ping
        {
            public Vector3 At;
            public string Who;
            public float Until;
        }

        const float PingSeconds = 10f;
        const float MarkerFrom = 25f;      // closer than this, the name tag is enough
        static readonly List<Ping> pings = new List<Ping>();
        static float nextPingAllowed;
        static GUIStyle style, pingStyle;

        public static void Init()
        {
            Session.On(Msg.MapPing, OnPing);
            Session.Ended += reason => pings.Clear();
        }

        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            pings.RemoveAll(p => now > p.Until);
            if (!LocalPlayer.Bound || MultiplayerUIState.Typing || MultiplayerUIState.PanelOpen) return;
            if (!Options.KeyDown(Options.PingKey) || now < nextPingAllowed) return;
            nextPingAllowed = now + 1f;

            Transform cam = LocalPlayer.View.transform;
            RaycastHit[] hits = Physics.RaycastAll(cam.position, cam.forward, 500f, GameUtil.WorldMask);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            Vector3 at = cam.position + cam.forward * 60f;
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider.isTrigger || GameUtil.IsPersonCollider(hits[i].collider)) continue;
                at = hits[i].point;
                break;
            }
            string me = Session.Local != null ? Session.Local.Name : "You";
            Add(at, me);
            if (Session.Active && Session.Connected && Session.Local != null)
            {
                NetWriter w = Session.Begin(Msg.MapPing);
                w.Byte(Session.Local.Slot);
                w.Vector3(at);
                Session.SendToAll(true, Channel.Reliable);
            }
        }

        static void Add(Vector3 at, string who)
        {
            pings.RemoveAll(p => p.Who == who); // one ping per player at a time
            pings.Add(new Ping { At = at, Who = who, Until = Time.realtimeSinceStartup + PingSeconds });
        }

        static void OnPing(NetPlayer from, NetReader r)
        {
            byte slot = r.Byte();
            Vector3 at = r.Vector3();
            if (from == null || float.IsNaN(at.x) || at.sqrMagnitude > 1e8f) return;
            NetPlayer who = Session.IsHost ? from : Session.BySlot(slot);
            if (who == null || who.IsLocal) return;
            Add(at, who.Name);
            if (Session.IsHost)
            {
                NetWriter w = Session.Begin(Msg.MapPing);
                w.Byte(who.Slot);
                w.Vector3(at);
                Session.BroadcastInGame(true, Channel.Reliable, who);
            }
        }

        // ================================================================ drawing (OnGUI)

        public static void Draw()
        {
            Camera cam = LocalPlayer.View;
            if (cam == null || !LocalPlayer.Bound) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, richText = false };
                pingStyle = new GUIStyle(style) { fontSize = 14 };
            }
            Vector3 me = cam.transform.position;

            if (Options.FriendMarkers)
            {
                foreach (NetPlayer p in Session.Remotes)
                {
                    if (p.Phase != PlayerPhase.Playing) continue;
                    Vector3 feet; float yaw;
                    if (!PlayerCare.WorldFeet(p, out feet, out yaw)) continue;
                    Vector3 at = feet + Vector3.up * 2.1f;
                    float d = Vector3.Distance(me, at);
                    bool onScreen;
                    Vector2 pos = ScreenPoint(cam, at, out onScreen);
                    if (d < MarkerFrom) continue; // close by: the name tag (or a glance around) is enough
                    DrawLabel(pos, (onScreen ? "" : "< ") + p.Name + "  " + Distance(d), new Color(0.55f, 0.85f, 1f), style);
                }
            }

            foreach (Ping ping in pings)
            {
                float d = Vector3.Distance(me, ping.At);
                bool onScreen;
                Vector2 pos = ScreenPoint(cam, ping.At + Vector3.up * 0.4f, out onScreen);
                float left = ping.Until - Time.realtimeSinceStartup;
                Color c = new Color(1f, 0.82f, 0.25f, Mathf.Clamp01(left / 2f));
                DrawLabel(pos, "v  " + ping.Who + ": here  " + Distance(d), c, pingStyle);
            }
        }

        /// <summary>Screen position (GUI coordinates); off-screen points stick to the screen edge.</summary>
        static Vector2 ScreenPoint(Camera cam, Vector3 world, out bool onScreen)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            bool behind = sp.z < 0f;
            if (behind) { sp.x = Screen.width - sp.x; sp.y = Screen.height - sp.y; }
            onScreen = !behind && sp.x >= 0f && sp.x <= Screen.width && sp.y >= 0f && sp.y <= Screen.height;
            float margin = 60f;
            if (!onScreen)
            {
                // Push it out to the edge in its direction from the centre.
                Vector2 c = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                Vector2 dir = new Vector2(sp.x, sp.y) - c;
                if (behind && dir.sqrMagnitude < 1f) dir = new Vector2(0f, -1f);
                float sx = (c.x - margin) / Mathf.Max(Mathf.Abs(dir.x), 0.001f);
                float sy = (c.y - margin) / Mathf.Max(Mathf.Abs(dir.y), 0.001f);
                Vector2 edge = c + dir * Mathf.Min(sx, sy);
                sp.x = edge.x; sp.y = edge.y;
            }
            return new Vector2(Mathf.Clamp(sp.x, margin, Screen.width - margin), Screen.height - Mathf.Clamp(sp.y, margin * 0.5f, Screen.height - margin * 0.5f));
        }

        static void DrawLabel(Vector2 pos, string text, Color color, GUIStyle s)
        {
            Rect r = new Rect(pos.x - 120f, pos.y - 12f, 240f, 24f);
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, color.a * 0.8f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, s);
            GUI.color = color;
            GUI.Label(r, text, s);
            GUI.color = old;
        }

        static string Distance(float m)
        {
            return m >= 1000f ? (m / 1000f).ToString("0.0") + " km" : Mathf.RoundToInt(m) + " m";
        }

        /// <summary>Teleports you next to another player (F2 panel).</summary>
        public static void GoTo(NetPlayer p)
        {
            if (Session.Active && !Session.IsHost) { Notifications.Show("Only the host can teleport."); return; }
            Vector3 feet; float yaw;
            if (!PlayerCare.WorldFeet(p, out feet, out yaw)) { Notifications.Show("Can't see where " + p.Name + " is yet."); return; }
            Vector3 back = Quaternion.Euler(0f, yaw, 0f) * Vector3.back * 1.8f;
            if (p.Last.Seated) back = Vector3.zero; // already beside their door
            if (LocalPlayer.Teleport(feet + back, yaw)) Notifications.Show("Teleported to " + p.Name + ".");
        }
    }
}
