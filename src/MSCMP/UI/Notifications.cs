using System.Collections.Generic;
using UnityEngine;

namespace MSCMP
{
    /// <summary>Short on-screen messages ("Ryan joined", errors) and chat history.</summary>
    internal static class Notifications
    {
        internal struct Entry
        {
            public string Text;
            public float Time;
            public Color Color;
        }

        public static readonly List<Entry> Toasts = new List<Entry>();
        public static readonly List<Entry> Chat = new List<Entry>();
        public static float LastChatTime = -100f;

        public static void Show(string text)
        {
            Show(text, new Color(1f, 0.92f, 0.6f));
        }

        public static void Show(string text, Color color)
        {
            Log.Info("Notice: " + text);
            Toasts.Add(new Entry { Text = text, Time = Time.realtimeSinceStartup, Color = color });
            while (Toasts.Count > 6) Toasts.RemoveAt(0);
        }

        public static void AddChat(string who, string text, Color color)
        {
            Log.Info("Chat " + who + ": " + text);
            Chat.Add(new Entry { Text = "<b>" + who + ":</b> " + text, Time = Time.realtimeSinceStartup, Color = color });
            while (Chat.Count > 30) Chat.RemoveAt(0);
            LastChatTime = Time.realtimeSinceStartup;
        }

        public static void Prune()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = Toasts.Count - 1; i >= 0; i--)
                if (now - Toasts[i].Time > 7f) Toasts.RemoveAt(i);
        }
    }
}
