using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MSCLoader;
using MSCMP.Network;
using UnityEngine;

namespace MSCMP
{
    internal static class RuntimeCompatibility
    {
        private static string gameFingerprint;

        internal static CompatibilityInfo Create(bool sharedWorld)
        {
            // Read real local files once, before admitting any peer. A packaged build's
            // compile-time fingerprint is not evidence of this machine's installed game.
            if (gameFingerprint == null)
                gameFingerprint = FingerprintGame();
            List<string> mods = new List<string>();
            // Always fingerprint our own implementation, even if a loader omits
            // this mod from its inventory. Equal release labels are insufficient.
            string ownLocation = typeof(RuntimeCompatibility).Assembly.Location;
            if (String.IsNullOrEmpty(ownLocation) || !File.Exists(ownLocation))
                throw new InvalidOperationException("MSC-MP cannot fingerprint its own assembly.");
            mods.Add("MSCMP.SelfAssembly|" + HashFile(ownLocation));
            if (ModLoader.LoadedMods == null)
                throw new InvalidOperationException("Loader mod inventory is unavailable.");
            foreach (Mod mod in ModLoader.LoadedMods)
            {
                if (mod == null || mod.isDisabled)
                    continue;
                Assembly assembly = mod.GetType().Assembly;
                string location = assembly.Location;
                if (String.IsNullOrEmpty(location) || !File.Exists(location))
                    throw new InvalidOperationException("A loaded mod cannot be fingerprinted.");
                mods.Add(mod.ID + "|" + mod.Version + "|" + HashFile(location));
            }
            mods.Sort(StringComparer.Ordinal);
            CompatibilityInfo result = new CompatibilityInfo();
            result.ModVersion = "0.4.0";
            result.GameFingerprint = gameFingerprint;
            result.ContentManifest = HashText(String.Join("\n", mods.ToArray()));
            result.Capabilities = ProtocolCapabilities.DiagnosticNetworking | ProtocolCapabilities.AvatarAppearanceV2;
            if (sharedWorld) result.Capabilities |= ProtocolCapabilities.SharedWorldV2;
            // These capabilities permit diagnostic presentation only, never world readiness.
            return result;
        }

        private static string FingerprintGame()
        {
            string data = Application.dataPath;
            List<string> relativeFiles = new List<string>();
            relativeFiles.Add("Managed/Assembly-CSharp.dll");
            relativeFiles.Add("Managed/Assembly-CSharp-firstpass.dll");
            relativeFiles.Add("Managed/Assembly-UnityScript.dll");
            relativeFiles.Add("Managed/Assembly-UnityScript-firstpass.dll");
            relativeFiles.Add("Managed/UnityEngine.dll");
            relativeFiles.Add("Managed/PlayMaker.dll");
            relativeFiles.Add("Managed/ES2.dll");
            foreach (string path in Directory.GetFiles(data))
            {
                string name = Path.GetFileName(path);
                if (name == "mainData" || name.StartsWith("level", StringComparison.Ordinal)
                    || name.StartsWith("sharedassets", StringComparison.Ordinal)
                    || name == "resources.assets" || name == "resources.resource")
                    relativeFiles.Add(name);
            }
            if (!relativeFiles.Contains("mainData"))
                throw new InvalidOperationException("Game scene catalogue is missing.");
            relativeFiles.Sort(StringComparer.Ordinal);
            StringBuilder catalogue = new StringBuilder();
            catalogue.AppendLine(Application.unityVersion);
            foreach (string relative in relativeFiles)
                catalogue.Append(relative).Append('=').Append(HashFile(Path.Combine(data, relative))).Append('\n');
            return HashText(catalogue.ToString());
        }

        private static string HashFile(string path)
        {
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return Hex(hash.ComputeHash(stream));
        }

        private static string HashText(string text)
        {
            using (SHA256 hash = SHA256.Create())
                return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }
    }
}
