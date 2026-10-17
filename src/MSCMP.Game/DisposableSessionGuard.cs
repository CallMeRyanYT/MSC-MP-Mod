using System;
using System.IO;
using UnityEngine;

namespace MSCMP.Integration
{
    internal static class DisposableSessionGuard
    {
        // This is a read-only admission check. It never creates a profile, marker or backup.
        internal static void RequireActive()
        {
            string path = Path.Combine(Application.persistentDataPath, ".mscmp-disposable-profile");
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length < 32 || stream.Length > 36) throw new InvalidOperationException("A disposable MSC-MP profile is required.");
                using (StreamReader reader = new StreamReader(stream))
                {
                    string marker = reader.ReadToEnd().Trim();
                    if (marker.Length != 32) throw new InvalidOperationException("Invalid disposable profile marker.");
                    foreach (char c in marker) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) throw new InvalidOperationException("Invalid disposable profile marker.");
                }
            }
        }
    }
}
