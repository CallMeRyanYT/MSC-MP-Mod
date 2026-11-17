using System;
using MSCLoader;
using MSCMP.Avatars;
using MSCMP.Net;
using UnityEngine;

namespace MSCMP
{
    /// <summary>The mod's entries in the MSCLoader settings menu.</summary>
    internal static class Options
    {
        static SettingsDropDownList body, shirt, pants, face;
        static SettingsCheckBox nameTags, verbose, markers, shareMusic, hearMusic, ownBody, emoteCam, migration;
        static SettingsSliderInt maxPlayers;
        public static SettingsKeybind PanelKey, ChatKey, WaveKey, PlayerListKey, EmoteKey, PingKey, MapKey, GiveKey;

        public static void Create()
        {
            Settings.AddHeader("Your character");
            Settings.AddText("Other players see you as one of the game's own characters. Pick a body and, if you like, different clothes.");
            body = Settings.AddDropDownList("mscmp_body", "Body", Catalog.BodyLabels(), 0, AppearanceChanged);
            shirt = Settings.AddDropDownList("mscmp_shirt", "Shirt", Catalog.Pretty(Catalog.Shirts), 0, AppearanceChanged);
            pants = Settings.AddDropDownList("mscmp_pants", "Trousers", Catalog.Pretty(Catalog.Pants), 0, AppearanceChanged);
            face = Settings.AddDropDownList("mscmp_face", "Face", Catalog.Pretty(Catalog.Faces), 0, AppearanceChanged);

            Settings.AddHeader("Multiplayer");
            maxPlayers = Settings.AddSlider("mscmp_maxplayers", "Max players when hosting", 2, Protocol.MaxPlayers, 4, null);
            nameTags = Settings.AddCheckBox("mscmp_nametags", "Show player names above heads", true, null);
            markers = Settings.AddCheckBox("mscmp_markers", "Show where far-away friends are (name and distance)", true, null);
            shareMusic = Settings.AddCheckBox("mscmp_sharemusic", "Share my own songs (Radio / CD folders) with friends", true, null);
            hearMusic = Settings.AddCheckBox("mscmp_hearmusic", "Play friends' songs", true, null);
            ownBody = Settings.AddCheckBox("mscmp_ownbody", "See my own body (look down)", true, null);
            emoteCam = Settings.AddCheckBox("mscmp_emotecam", "Show my emotes from the front for a moment", true, null);
            migration = Settings.AddCheckBox("mscmp_migration", "If the host's game is gone, the next player takes over", true, null);
            verbose = Settings.AddCheckBox("mscmp_verbose", "Detailed log (for bug reports)", false, () => Log.Verbose = verbose.GetValue());
            Settings.AddButton("Open log folder", OpenLogFolder);

            Keybind.AddHeader("MSC Multiplayer");
            PanelKey = Keybind.Add("mscmp_panel", "Multiplayer menu", KeyCode.F2);
            PlayerListKey = Keybind.Add("mscmp_players", "Show players", KeyCode.Tab);
            ChatKey = Keybind.Add("mscmp_chat", "Chat", KeyCode.T);
            WaveKey = Keybind.Add("mscmp_wave", "Wave to other players", KeyCode.G);
            EmoteKey = Keybind.Add("mscmp_emotes", "Emote menu", KeyCode.B);
            PingKey = Keybind.Add("mscmp_ping", "Ping: show friends where you look", KeyCode.Mouse2);
            MapKey = Keybind.Add("mscmp_map", "Map", KeyCode.M);
            GiveKey = Keybind.Add("mscmp_give", "Hand the item you hold to the friend you look at", KeyCode.H);
        }

        public static void Loaded()
        {
            try { Log.Verbose = verbose != null && verbose.GetValue(); } catch { }
        }

        static Appearance lastSent;
        static bool sentOnce;

        static void AppearanceChanged()
        {
            try
            {
                Appearance look = LocalAppearance();
                UI.CharacterPreview.PopupUntil = Time.realtimeSinceStartup + 8f; // show what it looks like
                if (sentOnce && look.Body == lastSent.Body && look.Shirt == lastSent.Shirt && look.Pants == lastSent.Pants && look.Face == lastSent.Face) return;
                lastSent = look;
                sentOnce = true;
                Log.Info("Appearance changed to " + look);
                if (Session.Active) Session.SendAppearance(look);
            }
            catch (Exception e) { Log.Exception("Appearance change", e); }
        }

        public enum Part { Body, Shirt, Pants, Face }

        /// <summary>Next/previous choice for one part (the arrows in the F2 panel). Saved in the
        /// same MSCLoader settings as the drop-down lists.</summary>
        public static void Step(Part part, int delta)
        {
            SettingsDropDownList list; int count;
            switch (part)
            {
                case Part.Body: list = body; count = Catalog.Bodies.Length; break;
                case Part.Shirt: list = shirt; count = Catalog.Shirts.Length; break;
                case Part.Pants: list = pants; count = Catalog.Pants.Length; break;
                default: list = face; count = Catalog.Faces.Length; break;
            }
            if (list == null || count <= 0) return;
            try
            {
                int i = ((SafeIndex(list) + delta) % count + count) % count;
                list.SetSelectedItemIndex(i);
            }
            catch (Exception e) { Log.ExceptionOnce("Options.Step", e); }
            AppearanceChanged();
        }

        /// <summary>Name of the current choice for one part, for the F2 panel.</summary>
        public static string Label(Part part)
        {
            Appearance a = LocalAppearance();
            switch (part)
            {
                case Part.Body: return Catalog.Bodies[a.Body].Label;
                case Part.Shirt: return Catalog.Pretty(Catalog.Shirts)[a.Shirt];
                case Part.Pants: return Catalog.Pretty(Catalog.Pants)[a.Pants];
                default: return Catalog.Pretty(Catalog.Faces)[a.Face];
            }
        }

        public static Appearance LocalAppearance()
        {
            Appearance a;
            a.Body = (byte)SafeIndex(body);
            a.Shirt = (byte)SafeIndex(shirt);
            a.Pants = (byte)SafeIndex(pants);
            a.Face = (byte)SafeIndex(face);
            a.Clamp();
            return a;
        }

        static int SafeIndex(SettingsDropDownList list)
        {
            try { return list != null ? Mathf.Clamp(list.GetSelectedItemIndex(), 0, 255) : 0; }
            catch { return 0; }
        }

        public static bool NameTags
        {
            get { try { return nameTags == null || nameTags.GetValue(); } catch { return true; } }
        }

        public static bool HostMigration
        {
            get { try { return migration == null || migration.GetValue(); } catch { return true; } }
        }

        public static bool ShowOwnBody
        {
            get { try { return ownBody == null || ownBody.GetValue(); } catch { return true; } }
        }

        public static bool EmoteCamera
        {
            get { try { return emoteCam == null || emoteCam.GetValue(); } catch { return true; } }
        }

        public static bool ShareMusic
        {
            get { try { return shareMusic == null || shareMusic.GetValue(); } catch { return true; } }
        }

        public static bool HearMusic
        {
            get { try { return hearMusic == null || hearMusic.GetValue(); } catch { return true; } }
        }

        public static bool FriendMarkers
        {
            get { try { return markers == null || markers.GetValue(); } catch { return true; } }
        }

        public static int MaxPlayers
        {
            get { try { return maxPlayers != null ? Mathf.Clamp(maxPlayers.GetValue(), 2, Protocol.MaxPlayers) : 4; } catch { return 4; } }
        }

        public static bool KeyDown(SettingsKeybind key)
        {
            try { return key != null && key.GetKeybindDown(); } catch { return false; }
        }

        public static bool Key(SettingsKeybind key)
        {
            try { return key != null && key.GetKeybind(); } catch { return false; }
        }

        static void OpenLogFolder()
        {
            try
            {
                string path = Log.FilePath;
                if (!string.IsNullOrEmpty(path)) Application.OpenURL("file:///" + System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
            }
            catch (Exception e) { Log.Exception("Open log folder", e); }
        }
    }
}
