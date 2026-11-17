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
        static SettingsCheckBox nameTags, verbose;
        static SettingsSliderInt maxPlayers;
        public static SettingsKeybind PanelKey, ChatKey, WaveKey, PlayerListKey;

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
            verbose = Settings.AddCheckBox("mscmp_verbose", "Detailed log (for bug reports)", false, () => Log.Verbose = verbose.GetValue());
            Settings.AddButton("Open log folder", OpenLogFolder);

            Keybind.AddHeader("MSC Multiplayer");
            PanelKey = Keybind.Add("mscmp_panel", "Multiplayer menu", KeyCode.F2);
            PlayerListKey = Keybind.Add("mscmp_players", "Show players", KeyCode.Tab);
            ChatKey = Keybind.Add("mscmp_chat", "Chat", KeyCode.T);
            WaveKey = Keybind.Add("mscmp_wave", "Wave to other players", KeyCode.G);
        }

        public static void Loaded()
        {
            try { Log.Verbose = verbose != null && verbose.GetValue(); } catch { }
        }

        static void AppearanceChanged()
        {
            try
            {
                Appearance look = LocalAppearance();
                Log.Info("Appearance changed to " + look);
                if (Session.Active) Session.SendAppearance(look);
            }
            catch (Exception e) { Log.Exception("Appearance change", e); }
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
