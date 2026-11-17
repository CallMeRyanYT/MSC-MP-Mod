using System;
using System.Collections.Generic;
using MSCMP.Net;
using MSCMP.UI;
using UnityEngine;

namespace MSCMP.Game
{
    /// <summary>
    /// Races. The host plans a track on the map (the route's waypoints are the checkpoints,
    /// the last one is the finish), picks the laps and starts: everyone in the world gets a
    /// 3-2-1 countdown, the next checkpoint in the world, their time and place, and the
    /// results at the end.
    /// </summary>
    internal static class Races
    {
        sealed class Racer { public byte Slot; public string Name; public int Passed; public float FinishTime = -1f; }

        static int raceId;
        static readonly List<Vector3> checkpoints = new List<Vector3>();
        static int laps = 1;
        static float startAt = -1f;           // local realtime
        static int passed;                    // our checkpoints passed (all laps)
        static bool finished, active;
        static float myFinish;
        static readonly Dictionary<byte, Racer> racers = new Dictionary<byte, Racer>();
        static float resultsUntil;
        public static int Laps = 1;           // host's choice in the panel

        public static bool Active { get { return active; } }
        static int Total { get { return checkpoints.Count * laps; } }

        public static void Init()
        {
            Session.On(Msg.RaceStart, OnStart);
            Session.On(Msg.RaceProgress, OnProgress);
            Session.On(Msg.RaceEnd, OnEnd);
            Session.Ended += r => { active = false; };
        }

        // ================================================================ host

        public static void HostStart()
        {
            if (Session.Active && !Session.IsHost) return;
            if (MapView.Route.Count < 1) { Notifications.Show("Plan the track on the map first (M): its waypoints are the checkpoints, the last one is the finish."); return; }
            raceId++;
            float start = Time.realtimeSinceStartup + 5f;
            NetWriter w = Session.Begin(Msg.RaceStart);
            WriteStart(w, raceId, Mathf.Clamp(Laps, 1, 9), MapView.Route, start);
            if (Session.Connected) Session.BroadcastInGame(true, Channel.Reliable);
            Begin(raceId, Mathf.Clamp(Laps, 1, 9), new List<Vector3>(MapView.Route), start);
        }

        public static void HostEnd()
        {
            if (!active || (Session.Active && !Session.IsHost)) return;
            NetWriter w = Session.Begin(Msg.RaceEnd);
            w.Int(raceId);
            if (Session.Connected) Session.BroadcastInGame(true, Channel.Reliable);
            End();
        }

        static void WriteStart(NetWriter w, int id, int lapCount, List<Vector3> pts, float start)
        {
            w.Int(id);
            w.Byte((byte)lapCount);
            w.Byte((byte)Mathf.Min(pts.Count, 64));
            for (int i = 0; i < pts.Count && i < 64; i++) w.Vector3(pts[i]);
            w.Float(start); // host clock
            byte n = 0;
            foreach (NetPlayer p in Session.Players) if (p.IsLocal || p.Phase == PlayerPhase.Playing) n++;
            w.Byte(n);
            foreach (NetPlayer p in Session.Players) if (p.IsLocal || p.Phase == PlayerPhase.Playing) { w.Byte(p.Slot); w.String(p.Name); }
        }

        // ================================================================ everyone

        static void OnStart(NetPlayer from, NetReader r)
        {
            int id = r.Int();
            int lapCount = r.Byte();
            int n = r.Byte();
            List<Vector3> pts = new List<Vector3>();
            for (int i = 0; i < n; i++) pts.Add(r.Vector3());
            float start = r.Float();
            int racerCount = r.Byte();
            List<KeyValuePair<byte, string>> who = new List<KeyValuePair<byte, string>>();
            for (int i = 0; i < racerCount; i++) who.Add(new KeyValuePair<byte, string>(r.Byte(), r.String()));
            if (from == null || !from.IsHost || Session.IsHost || pts.Count == 0) return;
            Begin(id, lapCount, pts, from.ToLocalTime(start));
            racers.Clear();
            foreach (KeyValuePair<byte, string> kv in who) racers[kv.Key] = new Racer { Slot = kv.Key, Name = kv.Value };
        }

        static void Begin(int id, int lapCount, List<Vector3> pts, float start)
        {
            raceId = id;
            laps = Math.Max(1, lapCount);
            checkpoints.Clear();
            checkpoints.AddRange(pts);
            startAt = start;
            passed = 0;
            finished = false;
            active = true;
            if (Session.IsHost || !Session.Active)
            {
                racers.Clear();
                foreach (NetPlayer p in Session.Players) if (p.IsLocal || p.Phase == PlayerPhase.Playing) racers[p.Slot] = new Racer { Slot = p.Slot, Name = p.Name };
                if (!Session.Active) racers[0] = new Racer { Slot = 0, Name = "You" };
            }
            Notifications.Show("Race! " + checkpoints.Count + " checkpoint(s)" + (laps > 1 ? ", " + laps + " laps" : "") + ". Get ready...");
        }

        public static void Update()
        {
            if (!active || !LocalPlayer.Bound) return;
            float now = Time.realtimeSinceStartup;
            if (now < startAt || finished) return;
            Vector3 cp = checkpoints[passed % checkpoints.Count];
            Vector3 d = cp - LocalPlayer.Feet;
            d.y = 0f;
            if (d.magnitude > 20f) return;
            passed++;
            byte me = Session.Local != null ? Session.Local.Slot : (byte)0;
            float t = now - startAt;
            if (passed >= Total) { finished = true; myFinish = t; Notifications.Show("Finished! " + FormatTime(t)); }
            SetProgress(me, passed, finished ? t : -1f);
            if (Session.Connected)
            {
                NetWriter w = Session.Begin(Msg.RaceProgress);
                w.Int(raceId);
                w.Byte(me);
                w.Short((short)passed);
                w.Float(finished ? t : -1f);
                Session.SendToAll(true, Channel.Reliable);
            }
        }

        static void SetProgress(byte slot, int count, float finishTime)
        {
            Racer rc;
            if (!racers.TryGetValue(slot, out rc))
            {
                NetPlayer p = Session.BySlot(slot);
                racers[slot] = rc = new Racer { Slot = slot, Name = p != null ? p.Name : "Player" };
            }
            rc.Passed = Math.Max(rc.Passed, count);
            if (finishTime >= 0f && rc.FinishTime < 0f)
            {
                bool first = true;
                foreach (Racer x in racers.Values) if (x != rc && x.FinishTime >= 0f) first = false;
                rc.FinishTime = finishTime;
                if (first)
                {
                    Activity.Add(rc.Name + " won the race in " + FormatTime(finishTime) + ".");
                    Stats.RaceWon(slot);
                }
            }
            if (Session.IsHost)
            {
                bool all = true;
                foreach (Racer x in racers.Values) if (x.FinishTime < 0f) all = false;
                if (all) HostEnd();
            }
        }

        static void OnProgress(NetPlayer from, NetReader r)
        {
            int id = r.Int();
            byte slot = r.Byte();
            int count = r.Short();
            float finish = r.Float();
            if (from == null || id != raceId || !active) return;
            NetPlayer who = Session.IsHost ? from : Session.BySlot(slot);
            if (who == null || who.IsLocal) return;
            if (Session.IsHost)
            {
                NetWriter w = Session.Begin(Msg.RaceProgress);
                w.Int(id);
                w.Byte(who.Slot);
                w.Short((short)count);
                w.Float(finish);
                Session.BroadcastInGame(true, Channel.Reliable, who);
            }
            SetProgress(who.Slot, count, finish);
        }

        static void OnEnd(NetPlayer from, NetReader r)
        {
            int id = r.Int();
            if (from == null || !from.IsHost || id != raceId) return;
            End();
        }

        static void End()
        {
            if (!active) return;
            active = false;
            resultsUntil = Time.realtimeSinceStartup + 15f;
        }

        static List<Racer> Standings()
        {
            List<Racer> list = new List<Racer>(racers.Values);
            list.Sort((a, b) =>
            {
                if (a.FinishTime >= 0f && b.FinishTime >= 0f) return a.FinishTime.CompareTo(b.FinishTime);
                if (a.FinishTime >= 0f) return -1;
                if (b.FinishTime >= 0f) return 1;
                return b.Passed.CompareTo(a.Passed);
            });
            return list;
        }

        static string FormatTime(float t)
        {
            int m = (int)(t / 60f);
            return m + ":" + (t - m * 60f).ToString("00.0");
        }

        // ================================================================ drawing (OnGUI)

        static GUIStyle big, mid;

        public static void Draw()
        {
            if (big == null)
            {
                big = new GUIStyle(GUI.skin.label) { fontSize = 64, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                mid = new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.UpperLeft, richText = true, wordWrap = false };
            }
            float now = Time.realtimeSinceStartup;
            if (active)
            {
                if (now < startAt)
                {
                    int s = Mathf.CeilToInt(startAt - now);
                    if (s <= 3) Shadowed(new Rect(0f, Screen.height * 0.3f, Screen.width, 90f), s.ToString(), big);
                    return;
                }
                if (now - startAt < 1.2f) Shadowed(new Rect(0f, Screen.height * 0.3f, Screen.width, 90f), "GO!", big);

                string line = finished ? "Finished in " + FormatTime(myFinish) + " - waiting for the others"
                    : "Checkpoint " + (passed % checkpoints.Count + 1) + "/" + checkpoints.Count +
                      (laps > 1 ? "  ·  lap " + (passed / checkpoints.Count + 1) + "/" + laps : "") + "  ·  " + FormatTime(now - startAt) + "  ·  " + Place();
                GUI.Box(new Rect(Screen.width / 2f - 260f, 56f, 520f, 28f), line, mid);

                if (!finished && LocalPlayer.View != null)
                {
                    Vector3 cp = checkpoints[passed % checkpoints.Count];
                    Vector3 sp = LocalPlayer.View.WorldToScreenPoint(cp + Vector3.up * 3f);
                    if (sp.z > 0f)
                    {
                        float dist = Vector3.Distance(LocalPlayer.Feet, cp);
                        bool last = passed + 1 >= Total;
                        Shadowed(new Rect(sp.x - 150f, Screen.height - sp.y - 15f, 300f, 30f), (last ? "FINISH  " : "CHECKPOINT  ") + Mathf.RoundToInt(dist) + " m", mid);
                    }
                }
                return;
            }
            if (now < resultsUntil && racers.Count > 0)
            {
                List<Racer> list = Standings();
                string text = "<b>Race results</b>\n";
                for (int i = 0; i < list.Count; i++)
                    text += (i + 1) + ".  " + list[i].Name + "   " + (list[i].FinishTime >= 0f ? FormatTime(list[i].FinishTime) : "did not finish") + "\n";
                GUI.Box(new Rect(Screen.width / 2f - 180f, 90f, 360f, 34f + list.Count * 20f), text, mid);
            }
        }

        static string Place()
        {
            List<Racer> list = Standings();
            byte me = Session.Local != null ? Session.Local.Slot : (byte)0;
            for (int i = 0; i < list.Count; i++) if (list[i].Slot == me) return Ordinal(i + 1) + " of " + list.Count;
            return "";
        }

        static string Ordinal(int n) { return n + (n == 1 ? "st" : n == 2 ? "nd" : n == 3 ? "rd" : "th"); }

        static void Shadowed(Rect r, string text, GUIStyle s)
        {
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), text, s);
            GUI.color = new Color(1f, 0.85f, 0.25f, 1f);
            GUI.Label(r, text, s);
            GUI.color = old;
        }
    }
}
