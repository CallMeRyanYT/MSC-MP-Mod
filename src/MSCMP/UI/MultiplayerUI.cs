using System;
using System.Collections.Generic;
using MSCMP.Avatars;
using MSCMP.Game;
using MSCMP.Net;
using MSCMP.Saves;
using MSCMP.Sync;
using UnityEngine;

namespace MSCMP.UI
{
    /// <summary>
    /// Everything on screen: the multiplayer panel (F2), the player list (Tab), chat (T),
    /// notifications and name tags. Plain Unity IMGUI so it works in the menu and in game.
    /// </summary>
    internal sealed class MultiplayerUI : MonoBehaviour
    {
        bool panelOpen = true;      // the main-menu panel starts open
        bool chatOpen;
        bool focusChat;
        string chatText = "";
        bool cursorTaken;
        bool subscribed;
        bool wasInGame, wasInMenu;
        float relockUntil;
        int chatOpenedFrame;

        /// <summary>True while one of the game's own menus has the mouse (pause menu etc.).</summary>
        static bool GameMenuOpen()
        {
            HutongGames.PlayMaker.FsmBool inMenu = GameUtil.GlobalBool("PlayerInMenu");
            return inMenu != null && inMenu.Value;
        }

        GUIStyle panel, title, text, small, button, toast, tagStyle, chatLine, input, header;
        Texture2D dark, darker, accent;
        bool styles;

        static readonly Color Yellow = new Color(1f, 0.85f, 0.3f);
        static readonly Color[] SlotColors =
        {
            new Color(1f, 0.85f, 0.3f), new Color(0.55f, 0.85f, 1f), new Color(0.6f, 1f, 0.6f), new Color(1f, 0.6f, 0.6f),
            new Color(0.85f, 0.65f, 1f), new Color(1f, 0.75f, 0.45f), new Color(0.6f, 1f, 0.95f), new Color(0.95f, 0.95f, 0.95f)
        };

        void Update()
        {
            try { Tick(); }
            catch (Exception e) { Log.ExceptionOnce("MultiplayerUI.Update", e); }
        }

        void Tick()
        {
            if (!subscribed)
            {
                subscribed = true;
                Session.ChatReceived += (p, msg) =>
                {
                    if (p == null) return;
                    Notifications.AddChat(p.Name, Escape(msg), SlotColors[p.Slot % SlotColors.Length]);
                };
            }

            // The panel starts open in the main menu and closed when the world loads.
            if (MP.InGame != wasInGame)
            {
                wasInGame = MP.InGame;
                if (wasInGame) panelOpen = false;
            }
            if (MP.InMenu != wasInMenu)
            {
                wasInMenu = MP.InMenu;
                if (wasInMenu) panelOpen = true;
            }
            if (!chatOpen && Options.KeyDown(Options.PanelKey))
            {
                panelOpen = !panelOpen;
            }
            if (MP.InGame && Session.Connected && !chatOpen && !panelOpen && Options.KeyDown(Options.ChatKey))
            {
                chatOpen = true;
                focusChat = true;
                chatText = "";
                chatOpenedFrame = Time.frameCount;
            }
            if (chatOpen && (!MP.InGame || !Session.Connected)) chatOpen = false;

            MultiplayerUIState.Typing = chatOpen;
            MultiplayerUIState.PanelOpen = panelOpen;

            // The game's own pause menu (Esc) closes ours, so the two never fight over the mouse.
            if (cursorTaken && Input.GetKeyDown(KeyCode.Escape)) { panelOpen = false; chatOpen = false; }

            // In game the cursor belongs to the game; borrow it while our panel or chat is open.
            bool wantCursor = MP.InGame && LocalPlayer.Bound && (chatOpen || panelOpen);
            if (wantCursor != cursorTaken)
            {
                cursorTaken = wantCursor;
                LocalPlayer.SetInputBlocked(wantCursor);
                if (!wantCursor) relockUntil = Time.realtimeSinceStartup + 0.75f;
            }
            if (cursorTaken)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (Time.realtimeSinceStartup < relockUntil && MP.InGame && !GameMenuOpen())
            {
                // Hand the mouse back: lock it for a few frames in a row (one call is not always
                // enough while the window is processing the click that closed our panel).
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        void OnDisable()
        {
            if (cursorTaken)
            {
                cursorTaken = false;
                try
                {
                    LocalPlayer.SetInputBlocked(false);
                    if (MP.InGame) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
                }
                catch { }
            }
        }

        // ================================================================ drawing

        void OnGUI()
        {
            try
            {
                EnsureStyles();
                GUI.depth = -100;
                if (Options.NameTags && MP.InGame) DrawNameTags();
                DrawToasts();
                DrawChat();
                DrawLoadingBanner();
                if (panelOpen && (MP.InMenu || MP.InGame)) DrawPanel();
                if (!panelOpen && !chatOpen && Options.Key(Options.PlayerListKey) && Session.Active) DrawPlayerList(false);
                if (MP.InMenu && !panelOpen) DrawHint();
            }
            catch (Exception e) { Log.ExceptionOnce("MultiplayerUI.OnGUI", e); }
        }

        void EnsureStyles()
        {
            if (styles) return;
            styles = true;
            dark = Solid(new Color(0.05f, 0.05f, 0.05f, 0.82f));
            darker = Solid(new Color(0f, 0f, 0f, 0.6f));
            accent = Solid(new Color(1f, 0.85f, 0.3f, 0.9f));

            panel = new GUIStyle(GUI.skin.box);
            panel.normal.background = dark;
            panel.padding = new RectOffset(14, 14, 12, 12);
            panel.alignment = TextAnchor.UpperLeft;

            title = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, richText = true };
            title.normal.textColor = Yellow;
            header = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, richText = true };
            header.normal.textColor = Yellow;
            text = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, richText = true };
            text.normal.textColor = Color.white;
            small = new GUIStyle(text) { fontSize = 11 };
            small.normal.textColor = new Color(0.8f, 0.8f, 0.8f);

            button = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };
            button.normal.textColor = Color.white;
            button.hover.textColor = Yellow;
            button.padding = new RectOffset(8, 8, 6, 6);

            toast = new GUIStyle(GUI.skin.box) { fontSize = 14, wordWrap = true, richText = true, alignment = TextAnchor.MiddleCenter };
            toast.normal.background = darker;
            toast.padding = new RectOffset(12, 12, 6, 6);

            tagStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, richText = false };
            chatLine = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true, richText = true };
            input = new GUIStyle(GUI.skin.textField) { fontSize = 14 };
        }

        static Texture2D Solid(Color c)
        {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        static string Escape(string s)
        {
            return s == null ? "" : s.Replace("<", "‹").Replace(">", "›");
        }

        // ---------------------------------------------------------------- main panel

        void DrawPanel()
        {
            float w = 380f;
            float x = Screen.width - w - 20f;
            GUILayout.BeginArea(new Rect(x, 20f, w, Screen.height - 40f));
            GUILayout.BeginVertical(panel);
            GUILayout.Label("MSC Multiplayer <size=11><color=#bbbbbb>v" + BuildInfo.Version + "</color></size>", title);
            if (!SteamNet.Available)
            {
                GUILayout.Label("Steam is not running (or the game was not started from Steam). Multiplayer needs Steam.", text);
                Footer();
                return;
            }

            if (!Session.Active) DrawIdle();
            else if (Session.IsHost) DrawHosting();
            else DrawClient();

            Footer();
        }

        void Footer()
        {
            GUILayout.Space(8);
            GUILayout.Label("F2 hides this panel" + (MP.InGame ? " · T to chat · hold Tab for players" : ""), small);
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        void DrawIdle()
        {
            GUILayout.Label("Play this world together with friends.", text);
            GUILayout.Space(6);
            if (MP.InGame)
                GUILayout.Label("You can host from here. Friends who join get your world as it is right now.", small);
            else
                GUILayout.Label("1. Press <b>Host game</b>.\n2. Invite friends (Steam overlay opens).\n3. Press <b>Continue</b>. Friends load in automatically.", text);
            if (!SaveSync.HostCanServe && !string.IsNullOrEmpty(SaveSync.HostProblem) && !MP.InGame)
                GUILayout.Label("<color=#ffb060>" + SaveSync.HostProblem + "</color>", small);
            GUILayout.Space(6);
            if (GUILayout.Button("Host game", button, GUILayout.Height(34)))
            {
                Session.MaxPlayers = Options.MaxPlayers;
                Session.Host();
            }
            GUILayout.Space(8);
            GUILayout.Label("<b>Joining a friend?</b> Accept their Steam invite, or open your Steam friends list, right-click them and choose <b>Join Game</b>. Your own save is backed up and restored automatically.", small);
        }

        void DrawHosting()
        {
            GUILayout.Label(Session.Connected ? "You are hosting." : Session.Status, text);
            if (Session.Connected)
            {
                if (GUILayout.Button("Invite friends", button, GUILayout.Height(32))) SteamNet.OpenInviteDialog();
                if (MP.InMenu)
                    GUILayout.Label("Press <b>Continue</b> when you are ready - everybody loads into your world.", small);
                if (!SaveSync.HostCanServe && !string.IsNullOrEmpty(SaveSync.HostProblem))
                    GUILayout.Label("<color=#ffb060>" + SaveSync.HostProblem + "</color>", small);
            }
            GUILayout.Space(6);
            DrawPlayers(true);
            GUILayout.Space(6);
            if (GUILayout.Button("Stop hosting", button, GUILayout.Height(28))) Session.Leave("host stopped hosting");
        }

        void DrawClient()
        {
            string host = "your friend";
            NetPlayer h = Session.BySlot(0);
            if (h != null) host = h.Name;
            if (!Session.Connected) GUILayout.Label(Session.Status, text);
            else if (MP.InMenu)
            {
                if (GameSession.WaitingToRejoin)
                {
                    GUILayout.Label(host + " is playing. Rejoin whenever you like.", text);
                    if (GUILayout.Button("Rejoin " + host, button, GUILayout.Height(32))) GameSession.Rejoin();
                }
                else if (!SaveSync.ClientHasHostSave)
                    GUILayout.Label("Getting " + host + "'s world... " + Mathf.RoundToInt(SaveSync.DownloadProgress * 100f) + "%", text);
                else if (!Session.HostInGame)
                    GUILayout.Label("Ready. Waiting for " + host + " to press Continue...", text);
                else
                    GUILayout.Label("Loading " + host + "'s world...", text);
            }
            else
            {
                GUILayout.Label(GameSession.CatchingUp ? "Syncing with " + host + "..." : "Playing in " + host + "'s world.", text);
            }
            GUILayout.Space(6);
            DrawPlayers(false);
            GUILayout.Space(6);
            if (GUILayout.Button("Leave", button, GUILayout.Height(28)))
                Session.Leave("left the game");
            GUILayout.Label("When you leave, your own save comes back exactly as it was.", small);
        }

        void DrawPlayers(bool canKick)
        {
            GUILayout.Label("Players (" + Session.PlayerCount + "/" + Session.MaxPlayers + ")", header);
            foreach (NetPlayer p in Session.Players)
            {
                GUILayout.BeginHorizontal();
                string name = "<color=#" + ColorUtility(SlotColors[p.Slot % SlotColors.Length]) + ">" + Escape(p.Name) + "</color>";
                if (p.IsHost) name += " <size=10>(host)</size>";
                if (p.IsLocal) name += " <size=10>(you)</size>";
                GUILayout.Label(name, text, GUILayout.Width(190));
                GUILayout.Label(PhaseText(p), small, GUILayout.Width(110));
                if (canKick && !p.IsLocal)
                {
                    if (GUILayout.Button("Kick", button, GUILayout.Width(48))) Session.Kick(p, "kicked by the host");
                }
                GUILayout.EndHorizontal();
            }
        }

        static string ColorUtility(Color c)
        {
            Color32 c32 = c;
            return c32.r.ToString("x2") + c32.g.ToString("x2") + c32.b.ToString("x2");
        }

        static string PhaseText(NetPlayer p)
        {
            switch (p.Phase)
            {
                case PlayerPhase.Connecting: return "connecting";
                case PlayerPhase.InMenu: return "in menu";
                case PlayerPhase.DownloadingSave: return "getting world " + Mathf.RoundToInt(p.SaveProgress * 100f) + "%";
                case PlayerPhase.SaveReady: return "ready";
                case PlayerPhase.Loading: return "loading";
                case PlayerPhase.Syncing: return "syncing";
                case PlayerPhase.Playing: return p.IsLocal || p.Rtt <= 0f ? "playing" : "playing · " + Mathf.RoundToInt(p.Rtt * 1000f) + " ms";
            }
            return "";
        }

        void DrawHint()
        {
            GUI.Label(new Rect(Screen.width - 260f, 10f, 250f, 22f), "F2: MSC Multiplayer", small);
        }

        // ---------------------------------------------------------------- player list (Tab)

        void DrawPlayerList(bool unused)
        {
            float w = 420f;
            int n = Session.PlayerCount;
            float h = 60f + n * 26f;
            GUILayout.BeginArea(new Rect((Screen.width - w) / 2f, Screen.height * 0.18f, w, h));
            GUILayout.BeginVertical(panel);
            DrawPlayers(false);
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        // ---------------------------------------------------------------- notifications

        void DrawToasts()
        {
            if (Notifications.Toasts.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            float y = 60f;
            float w = Mathf.Min(620f, Screen.width - 40f);
            for (int i = 0; i < Notifications.Toasts.Count; i++)
            {
                Notifications.Entry e = Notifications.Toasts[i];
                float age = now - e.Time;
                float alpha = age > 6f ? Mathf.Clamp01(7f - age) : 1f;
                GUIContent c = new GUIContent(e.Text);
                float h = toast.CalcHeight(c, w);
                Color old = GUI.color;
                GUI.color = new Color(e.Color.r, e.Color.g, e.Color.b, alpha);
                GUI.Box(new Rect((Screen.width - w) / 2f, y, w, h), c, toast);
                GUI.color = old;
                y += h + 6f;
            }
        }

        void DrawChat()
        {
            if (!MP.InGame || !Session.Active) return;
            float now = Time.realtimeSinceStartup;
            float w = Mathf.Min(520f, Screen.width * 0.45f);
            float bottom = Screen.height - 120f;
            int shown = 0;
            List<Notifications.Entry> chat = Notifications.Chat;
            for (int i = chat.Count - 1; i >= 0 && shown < 8; i--)
            {
                float age = now - chat[i].Time;
                if (!chatOpen && age > 14f) break;
                float alpha = chatOpen ? 1f : Mathf.Clamp01(14f - age);
                GUIContent c = new GUIContent(chat[i].Text);
                float h = chatLine.CalcHeight(c, w);
                bottom -= h;
                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, alpha * 0.8f);
                GUI.Label(new Rect(21f, bottom + 1f, w, h), StripTags(chat[i].Text), chatLine);
                GUI.color = new Color(chat[i].Color.r, chat[i].Color.g, chat[i].Color.b, alpha);
                GUI.Label(new Rect(20f, bottom, w, h), c, chatLine);
                GUI.color = old;
                shown++;
            }

            if (!chatOpen) return;
            Event ev = Event.current;
            if (ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter))
            {
                if (chatText.Trim().Length > 0) Session.SendChat(chatText);
                chatText = "";
                chatOpen = false;
                ev.Use();
                return;
            }
            if (ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape)
            {
                chatText = "";
                chatOpen = false;
                ev.Use();
                return;
            }
            GUI.SetNextControlName("mscmp_chat");
            chatText = GUI.TextField(new Rect(20f, Screen.height - 110f, w, 26f), chatText, 200, input);
            if (focusChat)
            {
                GUI.FocusControl("mscmp_chat");
                focusChat = false;
            }
            // The key that opened the chat must not end up in the text.
            if (Time.frameCount <= chatOpenedFrame + 2) chatText = "";
        }

        static string StripTags(string s)
        {
            return s.Replace("<b>", "").Replace("</b>", "");
        }

        void DrawLoadingBanner()
        {
            if (!Session.IsClient || !Session.Connected) return;
            string msg = null;
            if (MP.InGameScene && (!MP.InGame || GameSession.CatchingUp)) msg = "Syncing with the host's world...";
            if (msg == null) return;
            float w = 360f;
            GUI.Box(new Rect((Screen.width - w) / 2f, Screen.height - 70f, w, 30f), msg, toast);
        }

        // ---------------------------------------------------------------- name tags

        void DrawNameTags()
        {
            Camera cam = LocalPlayer.View;
            if (cam == null) return;
            foreach (NetPlayer p in Session.Remotes)
            {
                RemoteAvatar a = p.Avatar;
                if (a == null || !a.Visible) continue;
                Vector3 world = a.HeadPosition + Vector3.up * 0.35f;
                Vector3 sp = cam.WorldToScreenPoint(world);
                if (sp.z <= 0.2f || sp.z > 80f) continue;
                float size = Mathf.Clamp(18f - sp.z * 0.12f, 10f, 18f);
                tagStyle.fontSize = Mathf.RoundToInt(size);
                Rect r = new Rect(sp.x - 120f, Screen.height - sp.y - 14f, 240f, 28f);
                Color old = GUI.color;
                float alpha = Mathf.Clamp01(1.5f - sp.z / 60f);
                GUI.color = new Color(0f, 0f, 0f, 0.8f * alpha);
                GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), p.Name, tagStyle);
                Color c = SlotColors[p.Slot % SlotColors.Length];
                GUI.color = new Color(c.r, c.g, c.b, alpha);
                GUI.Label(r, p.Name, tagStyle);
                GUI.color = old;
            }
        }
    }
}
