using System;
using System.Collections.Generic;
using System.Text;
using MSCMP.Net;
using MSCMP.Sync;
using UnityEngine;

namespace MSCMP.Game
{
    /// <summary>"What happened": a short list of who did what in this session (F2).</summary>
    internal static class Activity
    {
        public struct Line { public string Time, Text; }
        public static readonly List<Line> Lines = new List<Line>();

        public static void Init()
        {
            Session.PlayerJoined += p => { if (p != null) Add(p.Name + " joined."); };
            Session.PlayerLeft += p => { if (p != null) Add(p.Name + " left."); };
        }

        public static void Add(string text)
        {
            Lines.Add(new Line { Time = DateTime.Now.ToString("HH:mm"), Text = text });
            if (Lines.Count > 60) Lines.RemoveAt(0);
            if (Log.Verbose) Log.Debug("Activity: " + text);
        }
    }

    /// <summary>
    /// Session statistics, kept by the host: how far everybody walked and drove, money earned
    /// and spent, how drunk they got, knock-downs, items handed over, races won. Shown to
    /// everyone when the host saves (toilet), or any time from the F2 panel.
    /// </summary>
    internal static class Stats
    {
        sealed class S
        {
            public string Name;
            public float Walked, Driven, Ridden;
            public float Earned, Spent, PeakDrunk;
            public int KnockedDown, Handed, RacesWon;
            public Vector3 LastPos;
            public bool HasPos;
        }

        static readonly Dictionary<ulong, S> stats = new Dictionary<ulong, S>();
        static float nextTick;
        static float hostMoneyLast = float.NaN, clientMoneySinceTick;
        static string shownText;
        static float shownUntil;
        static HutongGames.PlayMaker.FsmFloat money;

        public static void Init()
        {
            Session.On(Msg.SessionSummary, OnSummary);
            Session.Ended += r => { stats.Clear(); hostMoneyLast = float.NaN; };
        }

        static S Of(NetPlayer p)
        {
            S s;
            ulong id = p.SteamId.m_SteamID;
            if (!stats.TryGetValue(id, out s)) stats[id] = s = new S();
            s.Name = p.Name;
            return s;
        }

        public static void Update()
        {
            if (!Session.IsHost || !Session.Connected || !MP.InGame) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextTick) return;
            nextTick = now + 1f;
            foreach (NetPlayer p in Session.Players)
            {
                PlayerSnapshot snap;
                if (p.IsLocal) { if (!LocalPlayer.Bound) continue; snap = LocalPlayer.Sample(); }
                else { if (!p.HasState || p.Phase != PlayerPhase.Playing) continue; snap = p.Last; }
                Vector3 feet; float yaw;
                if (p.IsLocal) feet = LocalPlayer.Feet;
                else if (!PlayerCare.WorldFeet(p, out feet, out yaw)) continue;
                S s = Of(p);
                if (s.HasPos)
                {
                    float d = Vector3.Distance(feet, s.LastPos);
                    if (d < 80f) // not a teleport
                    {
                        if (snap.Vehicle == 0 || snap.StandingOnVehicle) { if (snap.Vehicle == 0) s.Walked += d; else s.Ridden += d; }
                        else if (snap.Seat == 1) s.Driven += d;
                        else s.Ridden += d;
                    }
                }
                s.LastPos = feet;
                s.HasPos = true;
                PlayerStatus st = p.IsLocal ? LocalPlayer.ReadStatus() : p.Status;
                if (p.IsLocal || p.HasStatus) s.PeakDrunk = Mathf.Max(s.PeakDrunk, st.Drunk);
            }
            // The host's own money: whatever changed that wasn't a friend's earning or spending.
            if (money == null) money = GameUtil.GlobalFloat("PlayerMoney");
            if (money != null && Session.Local != null)
            {
                float m = money.Value;
                if (!float.IsNaN(hostMoneyLast))
                {
                    float own = m - hostMoneyLast - clientMoneySinceTick;
                    if (Mathf.Abs(own) > 0.01f) { S me = Of(Session.Local); if (own > 0f) me.Earned += own; else me.Spent -= own; }
                }
                hostMoneyLast = m;
                clientMoneySinceTick = 0f;
            }
        }

        public static void Money(NetPlayer p, float delta)
        {
            if (!Session.IsHost || p == null) return;
            S s = Of(p);
            if (delta > 0f) s.Earned += delta; else s.Spent -= delta;
            clientMoneySinceTick += delta;
            if (Mathf.Abs(delta) >= 50f) Activity.Add(p.Name + (delta > 0f ? " earned " : " spent ") + Mathf.Abs(delta).ToString("0") + " mk.");
        }

        public static void Knocked(NetPlayer p) { if (Session.IsHost && p != null) Of(p).KnockedDown++; }
        public static void Handed(NetPlayer p) { if (Session.IsHost && p != null) Of(p).Handed++; }
        public static void RaceWon(byte slot) { NetPlayer p = Session.BySlot(slot); if (Session.IsHost && p != null) Of(p).RacesWon++; }

        /// <summary>Host: sends everyone the summary (and shows it here).</summary>
        public static void ShareSummary()
        {
            if (!Session.IsHost) return;
            string text = Build();
            if (Session.Connected)
            {
                NetWriter w = Session.Begin(Msg.SessionSummary);
                w.String(text);
                Session.BroadcastInGame(true, Channel.Reliable);
            }
            Show(text);
        }

        static string Build()
        {
            StringBuilder sb = new StringBuilder("<b>Session summary</b>\n");
            foreach (S s in stats.Values)
            {
                sb.Append("<b>").Append(s.Name).Append("</b>: walked ").Append(Km(s.Walked)).Append(", drove ").Append(Km(s.Driven));
                if (s.Ridden > 50f) sb.Append(", rode along ").Append(Km(s.Ridden));
                if (s.Earned > 0f || s.Spent > 0f) sb.Append(", earned ").Append(s.Earned.ToString("0")).Append(" mk, spent ").Append(s.Spent.ToString("0")).Append(" mk");
                if (s.PeakDrunk > 0.4f) sb.Append(s.PeakDrunk > 2f ? ", got very drunk" : ", had a few");
                if (s.KnockedDown > 0) sb.Append(", run over ").Append(s.KnockedDown).Append("x");
                if (s.Handed > 0) sb.Append(", handed over ").Append(s.Handed).Append(" item(s)");
                if (s.RacesWon > 0) sb.Append(", won ").Append(s.RacesWon).Append(" race(s)");
                sb.Append('\n');
            }
            return sb.ToString();
        }

        static string Km(float m) { return m >= 1000f ? (m / 1000f).ToString("0.0") + " km" : Mathf.RoundToInt(m) + " m"; }

        static void OnSummary(NetPlayer from, NetReader r)
        {
            string text = r.String();
            if (from != null && from.IsHost && !Session.IsHost) Show(text);
        }

        static void Show(string text)
        {
            shownText = text;
            shownUntil = Time.realtimeSinceStartup + 20f;
        }

        static GUIStyle style;

        public static void Draw()
        {
            if (shownText == null || Time.realtimeSinceStartup > shownUntil) return;
            if (style == null) style = new GUIStyle(GUI.skin.box) { fontSize = 14, alignment = TextAnchor.UpperLeft, richText = true, wordWrap = true, padding = new RectOffset(12, 12, 10, 10) };
            float w = Mathf.Min(640f, Screen.width - 40f);
            float h = style.CalcHeight(new GUIContent(shownText), w);
            Rect r = new Rect((Screen.width - w) / 2f, Screen.height * 0.25f, w, h);
            GUI.Box(r, shownText, style);
            if (GUI.Button(new Rect(r.xMax - 24f, r.y + 4f, 20f, 20f), "x")) shownUntil = 0f;
        }
    }
}
