using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MSCMP.Net;
using MSCMP.Sync;
using UnityEngine;

namespace MSCMP.Game
{
    /// <summary>
    /// Each friend's own progress in the host's world: where they are and how they are doing
    /// (hunger, thirst, tiredness, stress, toilet, dirt, drunkenness).
    ///
    /// Everyone sends their needs every few seconds; the host remembers every friend's last
    /// place and needs. When the host saves (toilet), those are written next to the host's
    /// save, so the next time a friend loads into this world they are put back where they
    /// were, as hungry or drunk as they were. The same memory brings a friend back to their
    /// spot after a crash and reconnect. Dying forgets it: they start fresh.
    /// </summary>
    internal static class PlayerCare
    {
        sealed class Record
        {
            public string Name;
            public Vector3 Feet;
            public float Yaw;
            public PlayerStatus Status;
            public bool HasStatus;
        }

        const float StatusInterval = 2f;
        static readonly Dictionary<ulong, Record> records = new Dictionary<ulong, Record>();
        static bool loaded;
        static readonly Dictionary<ulong, float> settling = new Dictionary<ulong, float>(); // just put back: don't record their old spot
        static float nextStatus, nextRemember;

        static string FilePath
        {
            get
            {
                string log = Log.FilePath;
                string dir = log != null ? Path.GetDirectoryName(Path.GetFullPath(log)) : ".";
                return Path.Combine(dir, "friends_progress.txt");
            }
        }

        public static void Init()
        {
            Session.On(Msg.PlayerStatus, OnStatus);
            Session.On(Msg.PlayerRestore, OnRestore);
            Session.On(Msg.PlayerDied, OnDied);
            Session.On(Msg.PlayerTeleport, OnTeleport);
            Session.PlayerLeft += p => { if (Session.IsHost) Remember(p); };
        }

        // ================================================================ per frame

        public static void Update()
        {
            if (!Session.Active || !Session.Connected || Session.Local == null || !LocalPlayer.Bound) return;
            if (GameSession.CatchingUp) return;
            float now = Time.realtimeSinceStartup;

            if (now >= nextStatus)
            {
                nextStatus = now + StatusInterval;
                PlayerStatus s = LocalPlayer.ReadStatus();
                Session.Local.Status = s;
                Session.Local.HasStatus = true;
                NetWriter w = Session.Begin(Msg.PlayerStatus);
                w.Byte(Session.Local.Slot);
                s.Write(w);
                Session.SendToAll(true, Channel.Reliable);
            }

            Autosave();
            if (Session.IsHost && now >= nextRemember)
            {
                nextRemember = now + 1f;
                foreach (NetPlayer p in Session.Remotes)
                    if (p.Phase == PlayerPhase.Playing) Remember(p);
            }
        }

        static void OnStatus(NetPlayer from, NetReader r)
        {
            byte slot = r.Byte();
            PlayerStatus s = PlayerStatus.Read(r);
            if (from == null) return;
            NetPlayer subject = Session.IsHost ? from : Session.BySlot(slot);
            if (subject == null || subject.IsLocal) return;
            subject.Status = s;
            subject.HasStatus = true;
            if (Session.IsHost)
            {
                NetWriter w = Session.Begin(Msg.PlayerStatus);
                w.Byte(subject.Slot);
                s.Write(w);
                Session.BroadcastInGame(true, Channel.Reliable, subject);
            }
        }

        // ================================================================ host: remembering

        /// <summary>Where a player's feet are in the world (next to the car if they sit in one).</summary>
        public static bool WorldFeet(NetPlayer p, out Vector3 feet, out float yaw)
        {
            feet = Vector3.zero;
            yaw = 0f;
            if (p == null || !p.HasState) return false;
            PlayerSnapshot s = p.Last;
            if (s.Vehicle == 0)
            {
                feet = s.Position;
                yaw = s.Yaw;
                return true;
            }
            NetVehicle v = VehicleSync.ById(s.Vehicle);
            if (v == null || v.Root == null) return false;
            if (s.StandingOnVehicle)
            {
                feet = v.Root.TransformPoint(s.Position);
                yaw = (v.Root.rotation * s.Body).eulerAngles.y;
                return true;
            }
            // Beside their door: the driver sits on the left, the passenger on the right.
            feet = v.Root.TransformPoint(s.Position) + v.Root.right * (s.Seat == 1 ? -1.6f : 1.6f);
            yaw = v.Root.eulerAngles.y;
            return true;
        }

        static void Remember(NetPlayer p)
        {
            if (p == null || p.IsLocal || p.SteamId.m_SteamID == 0) return;
            float until;
            if (settling.TryGetValue(p.SteamId.m_SteamID, out until) && Time.realtimeSinceStartup < until) return;
            Vector3 feet; float yaw;
            if (!WorldFeet(p, out feet, out yaw)) return;
            Load();
            Record rec;
            if (!records.TryGetValue(p.SteamId.m_SteamID, out rec)) records[p.SteamId.m_SteamID] = rec = new Record();
            rec.Name = p.Name;
            rec.Feet = feet;
            rec.Yaw = yaw;
            if (p.HasStatus) { rec.Status = p.Status; rec.HasStatus = true; }
        }

        /// <summary>Host: a friend finished loading into our world. Put them back where they were.</summary>
        public static void OnClientReady(NetPlayer p)
        {
            if (!Session.IsHost || p == null) return;
            Load();
            Record rec;
            if (!records.TryGetValue(p.SteamId.m_SteamID, out rec)) return;
            NetWriter w = Session.Begin(Msg.PlayerRestore);
            w.Vector3(rec.Feet);
            w.Float(rec.Yaw);
            w.Bool(rec.HasStatus);
            if (rec.HasStatus) rec.Status.Write(w);
            Session.SendTo(p, true, Channel.Reliable);
            settling[p.SteamId.m_SteamID] = Time.realtimeSinceStartup + 4f;
            Log.Info("Put " + p.Describe() + " back at their last place " + rec.Feet);
        }

        static void OnRestore(NetPlayer from, NetReader r)
        {
            Vector3 feet = r.Vector3();
            float yaw = r.Float();
            bool hasStatus = r.Bool();
            PlayerStatus s = hasStatus ? PlayerStatus.Read(r) : default(PlayerStatus);
            if (!Session.IsClient || from == null || !from.IsHost) return;
            if (float.IsNaN(feet.x) || feet.sqrMagnitude > 1e8f) return;
            if (LocalPlayer.Teleport(feet, yaw)) Notifications.Show("You're back where you left off.");
            if (hasStatus) LocalPlayer.ApplyStatus(s);
        }

        /// <summary>Host: brings a player to the host (they appear behind the host).</summary>
        public static void Bring(NetPlayer p)
        {
            if (!Session.IsHost || p == null || p.IsLocal || !LocalPlayer.Bound) return;
            if (!string.IsNullOrEmpty(LocalPlayer.CurrentVehicleName) || LocalPlayer.PassengerOf != null)
            {
                Notifications.Show("Get out of the vehicle first, so there is room next to you.");
                return;
            }
            Transform me = LocalPlayer.Player;
            NetWriter w = Session.Begin(Msg.PlayerTeleport);
            w.Vector3(LocalPlayer.Feet - me.forward * 1.6f);
            w.Float(me.eulerAngles.y);
            Session.SendTo(p, true, Channel.Reliable);
            Notifications.Show("Bringing " + p.Name + " to you.");
        }

        static void OnTeleport(NetPlayer from, NetReader r)
        {
            Vector3 feet = r.Vector3();
            float yaw = r.Float();
            if (!Session.IsClient || from == null || !from.IsHost) return;
            if (float.IsNaN(feet.x) || feet.sqrMagnitude > 1e8f) return;
            if (LocalPlayer.Teleport(feet, yaw)) Notifications.Show(from.Name + " brought you to them.");
        }

        /// <summary>Client: we died. The host forgets our place; we come back fresh.</summary>
        public static void ReportDeath()
        {
            if (!Session.IsClient || !Session.Connected) return;
            Session.Begin(Msg.PlayerDied);
            Session.SendToHost(true, Channel.Reliable);
        }

        static void OnDied(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null || from.IsLocal) return;
            Load();
            records.Remove(from.SteamId.m_SteamID);
            Notifications.Show(from.Name + " died. They'll be back in a moment.");
            Activity.Add(from.Name + " died.");
        }

        // ================================================================ host: disk

        /// <summary>Host saved the game (toilet): write every friend's progress next to it, and
        /// show everyone the session summary.</summary>
        public static void OnHostSaved()
        {
            if (Session.IsClient) return;
            if (Session.Connected) Stats.ShareSummary();
            WriteProgress();
        }

        static float nextAutosave;

        /// <summary>Every few minutes, so a crash of the host's game doesn't lose where everyone was.</summary>
        static void Autosave()
        {
            if (!Session.IsHost || Time.realtimeSinceStartup < nextAutosave) return;
            nextAutosave = Time.realtimeSinceStartup + 180f;
            WriteProgress();
        }

        static void WriteProgress()
        {
            foreach (NetPlayer p in Session.Remotes) if (p.Phase == PlayerPhase.Playing) Remember(p);
            Load();
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("MSCMP-FRIENDS 1");
                foreach (KeyValuePair<ulong, Record> kv in records)
                {
                    Record r = kv.Value;
                    sb.Append(kv.Key).Append('|').Append((r.Name ?? "").Replace('|', '/')).Append('|')
                      .Append(F(r.Feet.x)).Append('|').Append(F(r.Feet.y)).Append('|').Append(F(r.Feet.z)).Append('|').Append(F(r.Yaw)).Append('|')
                      .Append(r.HasStatus ? "1" : "0");
                    for (int i = 0; i < PlayerStatus.GlobalNames.Length; i++) sb.Append('|').Append(F(r.Status[i]));
                    sb.AppendLine();
                }
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
                if (records.Count > 0) Log.Info("Saved the progress of " + records.Count + " friend(s).");
            }
            catch (Exception e) { Log.Exception("Saving friends' progress", e); }
        }

        /// <summary>Host started a new game: old places belong to the old world.</summary>
        public static void OnNewGame()
        {
            records.Clear();
            loaded = true;
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                string[] lines = File.ReadAllLines(FilePath);
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] f = lines[i].Split('|');
                    if (f.Length < 7 + PlayerStatus.GlobalNames.Length) continue;
                    ulong id;
                    if (!ulong.TryParse(f[0], out id)) continue;
                    Record r = new Record
                    {
                        Name = f[1],
                        Feet = new Vector3(P(f[2]), P(f[3]), P(f[4])),
                        Yaw = P(f[5]),
                        HasStatus = f[6] == "1",
                    };
                    for (int k = 0; k < PlayerStatus.GlobalNames.Length; k++) r.Status[k] = P(f[7 + k]);
                    records[id] = r;
                }
                Log.Info("Loaded the saved progress of " + records.Count + " friend(s).");
            }
            catch (Exception e) { Log.Exception("Loading friends' progress", e); }
        }

        static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        static float P(string s)
        {
            float v;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0f;
        }
    }
}
