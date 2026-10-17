using System;
using System.IO;
using System.Security.Cryptography;
using MSCMP.Core;
using MSCMP.Persistence;
using UnityEngine;

namespace MSCMP.Integration
{
    internal static class SharedSave
    {
        internal static CheckpointStore Open()
        {
            DisposableSessionGuard.RequireActive();
            string root = Path.Combine(Application.persistentDataPath, ".mscmp-shared");
            for (DirectoryInfo current = new DirectoryInfo(root); current != null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Shared saves cannot use redirected directories.");
            if (Directory.Exists(root)) foreach (string path in Directory.GetFileSystemEntries(root))
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Shared save links are not supported.");
            return new CheckpointStore(root, new CheckpointLimits(256 * 1024, 64, 64, 1024));
        }
        internal static void InvalidateNativeReceipt(CheckpointStore store)
        { string path = Path.Combine(store.DirectoryPath, "native-save-receipt.txt"); if (File.Exists(path)) File.Delete(path); }
        internal static bool Exists(CheckpointStore store) { return File.Exists(store.CurrentPath) || File.Exists(store.PreviousPath); }
        internal static void MarkNativeSave(CheckpointStore store)
        {
            // The offline bundle tool requires this receipt plus a closed game.
            // It is not a claim that the after-save callback itself is a barrier.
            string digest;
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = File.OpenRead(store.CurrentPath)) digest = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
            string destination = Path.Combine(store.DirectoryPath, "native-save-receipt.txt");
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, "MSCMP-NATIVE-SAVE-1\n" + digest + "\n" + DateTime.UtcNow.ToString("o") + "\n");
                if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
