using System;
using System.Collections.Generic;
using System.IO;
using MSCMP.Net;
using MSCMP.Sync;
using UnityEngine;

namespace MSCMP.Game
{
    /// <summary>
    /// A shopping list everyone shares (F2): anyone can add, tick and remove items. When an
    /// item of that kind appears in the world (bought at Teimo's...), it ticks itself off.
    /// The host keeps the list and saves it between sessions.
    /// </summary>
    internal static class ShoppingList
    {
        public sealed class Entry { public string Text; public bool Done; }

        public static readonly List<Entry> Items = new List<Entry>();
        static bool loaded;

        static string FilePath
        {
            get
            {
                string log = Log.FilePath;
                string dir = log != null ? Path.GetDirectoryName(Path.GetFullPath(log)) : ".";
                return Path.Combine(dir, "shopping_list.txt");
            }
        }

        public static void Init()
        {
            Session.On(Msg.ShopList, OnList);
            Session.On(Msg.ShopEdit, OnEdit);
            ObjectSync.Registered += o =>
            {
                if (o == null || !o.Dynamic || (Session.Active && !Session.IsHost)) return;
                try { AutoTick(Bots.Pretty(o.Name)); } catch (Exception e) { Log.ExceptionOnce("ShoppingList", e); }
            };
        }

        // ---------------------------------------------------------------- editing (UI)

        public static void Add(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > 40) text = text.Substring(0, 40);
            Edit(0, -1, text);
        }

        public static void Toggle(int index) { Edit(1, index, ""); }
        public static void Remove(int index) { Edit(2, index, ""); }

        /// <summary>op 0 = add, 1 = tick/untick, 2 = remove.</summary>
        static void Edit(byte op, int index, string text)
        {
            if (!Session.Active || Session.IsHost) { Apply(op, index, text); Changed(); return; }
            NetWriter w = Session.Begin(Msg.ShopEdit);
            w.Byte(op);
            w.Short((short)index);
            w.String(text);
            Session.SendToHost(true, Channel.Reliable);
        }

        static void Apply(byte op, int index, string text)
        {
            Load();
            if (op == 0) { if (Items.Count < 40) Items.Add(new Entry { Text = text }); }
            else if (index >= 0 && index < Items.Count)
            {
                if (op == 1) Items[index].Done = !Items[index].Done;
                else if (op == 2) Items.RemoveAt(index);
            }
        }

        static void OnEdit(NetPlayer from, NetReader r)
        {
            byte op = r.Byte();
            int index = r.Short();
            string text = r.String();
            if (!Session.IsHost || from == null) return;
            if (text.Length > 40) text = text.Substring(0, 40);
            Apply(op, index, text);
            Changed();
        }

        static void AutoTick(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return;
            Load();
            string n = itemName.ToLowerInvariant();
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].Done) continue;
                string want = Items[i].Text.ToLowerInvariant();
                if (n.IndexOf(want, StringComparison.Ordinal) < 0 && want.IndexOf(n, StringComparison.Ordinal) < 0) continue;
                Items[i].Done = true;
                Notifications.Show("Shopping list: " + Items[i].Text + " - got it!");
                Changed();
                return;
            }
        }

        // ---------------------------------------------------------------- sharing

        static void Changed()
        {
            Save();
            if (Session.IsHost && Session.Connected) SendList(null);
        }

        public static void SendList(NetPlayer onlyTo)
        {
            if (!Session.IsHost) return;
            Load();
            NetWriter w = Session.Begin(Msg.ShopList);
            w.Byte((byte)Items.Count);
            foreach (Entry e in Items) { w.String(e.Text); w.Bool(e.Done); }
            if (onlyTo != null) Session.SendTo(onlyTo, true, Channel.Reliable);
            else Session.BroadcastInGame(true, Channel.Reliable);
        }

        static void OnList(NetPlayer from, NetReader r)
        {
            int n = r.Byte();
            List<Entry> list = new List<Entry>();
            for (int i = 0; i < n; i++) list.Add(new Entry { Text = r.String(), Done = r.Bool() });
            if (Session.IsHost || from == null || !from.IsHost) return;
            Items.Clear();
            Items.AddRange(list);
        }

        // ---------------------------------------------------------------- disk (host)

        static void Load()
        {
            if (loaded || Session.IsClient) return;
            loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    if (line.Length < 2) continue;
                    Items.Add(new Entry { Done = line[0] == '1', Text = line.Substring(1) });
                }
            }
            catch (Exception e) { Log.Exception("Loading the shopping list", e); }
        }

        static void Save()
        {
            if (Session.IsClient) return;
            try
            {
                List<string> lines = new List<string>();
                foreach (Entry e in Items) lines.Add((e.Done ? "1" : "0") + e.Text);
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception e) { Log.Exception("Saving the shopping list", e); }
        }

        /// <summary>Session ended: a client's copy was the host's; start from our own again.</summary>
        public static void OnSessionEnded()
        {
            Items.Clear();
            loaded = false;
        }
    }
}
