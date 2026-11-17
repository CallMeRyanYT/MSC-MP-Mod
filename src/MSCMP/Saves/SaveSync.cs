using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MSCMP.Net;
using UnityEngine;

namespace MSCMP.Saves
{
    /// <summary>
    /// Gives every client an exact copy of the host's save so both games start from the
    /// same world. The client's own save is backed up first and restored automatically
    /// when the session ends (or on the next start if the game crashed).
    /// </summary>
    internal static class SaveSync
    {
        /// <summary>Files that make up a My Summer Car save (in Application.persistentDataPath).
        /// options.txt (settings), trophies.txt and graveyard.txt are personal and never touched.</summary>
        public static readonly string[] SaveFiles =
        {
            "defaultES2File.txt", "items.txt", "meshsave.txt", "notepad.txt",
            "sceneObjectsFile.txt", "createdObjectsFile.txt", "SavedPrefabs.txt", "textureFile.txt",
        };

        const int ChunkSize = 32 * 1024;
        const int MaxFileSize = 64 * 1024 * 1024;
        const int ChunksPerFrame = 3;

        sealed class SaveFile
        {
            public string Name;
            public byte[] Data;
            public uint Crc;
        }

        sealed class Upload
        {
            public NetPlayer To;
            public uint Id;
            public int File;
            public int Offset;
            public int Sent;
            public int Total;
        }

        sealed class Download
        {
            public uint Id;
            public string[] Names;
            public int[] Sizes;
            public uint[] Crcs;
            public byte[][] Data;
            public int Received;
            public int Total;
        }

        // Host
        static List<SaveFile> snapshot;
        static bool snapshotValid;
        static string snapshotProblem = "";
        static readonly List<Upload> uploads = new List<Upload>();
        static uint nextTransferId = 1;

        // Client
        static Download download;
        public static bool ClientHasHostSave { get; private set; }
        public static float DownloadProgress { get { return download == null || download.Total == 0 ? (ClientHasHostSave ? 1f : 0f) : (float)download.Received / download.Total; } }

        static string SaveDir { get { return Application.persistentDataPath; } }
        static string BackupRoot { get { return Path.Combine(Application.persistentDataPath, "MSCMP_backups"); } }
        static string JournalPath { get { return Path.Combine(BackupRoot, "active-backup.txt"); } }

        public static bool HostCanServe { get { return snapshotValid && snapshot != null && snapshot.Count > 0; } }
        public static string HostProblem { get { return snapshotProblem; } }

        public static void Init()
        {
            Session.On(Msg.SaveRequest, OnSaveRequest);
            Session.On(Msg.SaveBegin, OnSaveBegin);
            Session.On(Msg.SaveChunk, OnSaveChunk);
            Session.On(Msg.SaveEnd, OnSaveEnd);
            Session.Ended += OnSessionEnded;
            Session.PlayerLeft += p => uploads.RemoveAll(u => u.To == p);
        }

        // ================================================================ host side

        /// <summary>Called every time the main menu loads: the files on disk are now exactly what
        /// "Continue" will load, so that is what friends receive.</summary>
        public static void OnMenu()
        {
            if (Session.IsClient) return; // a connected client's disk holds the host's copy
            if (!Session.Active) RestoreOwnSave(); // back from someone else's world
            TakeSnapshot();
        }

        public static void OnNewGameStarted()
        {
            snapshotValid = false;
            snapshotProblem = "You started a new game. Save it once (use a toilet to save & quit), then host again so friends get this world.";
            Log.Info("New game started: the save on disk no longer matches the world.");
        }

        public static void OnHostSaved()
        {
            // The game quits to the menu right after a save; OnMenu takes the fresh snapshot.
        }

        static void TakeSnapshot()
        {
            snapshot = new List<SaveFile>();
            snapshotValid = false;
            try
            {
                for (int i = 0; i < SaveFiles.Length; i++)
                {
                    string path = Path.Combine(SaveDir, SaveFiles[i]);
                    if (!File.Exists(path)) continue;
                    byte[] data = File.ReadAllBytes(path);
                    if (data.Length > MaxFileSize) continue;
                    snapshot.Add(new SaveFile { Name = SaveFiles[i], Data = data, Crc = Crc32.Compute(data, 0, data.Length) });
                }
                bool hasMain = snapshot.Exists(f => f.Name == "defaultES2File.txt");
                snapshotValid = hasMain;
                snapshotProblem = hasMain ? "" : "You have no saved game yet. Start a game, save it once at a toilet, then host.";
                Log.Info("Save snapshot: " + snapshot.Count + " files" + (hasMain ? "" : " (no main save)"));
            }
            catch (Exception e)
            {
                snapshotProblem = "Could not read your save: " + e.Message;
                Log.Exception("Reading save files", e);
            }
        }

        /// <summary>Host: start sending the save to a client.</summary>
        public static void SendSaveTo(NetPlayer p)
        {
            if (!Session.IsHost || p == null || p.IsLocal) return;
            if (!HostCanServe)
            {
                Session.SendNotice(p, "The host cannot share their world yet: " + snapshotProblem);
                Notifications.Show(snapshotProblem);
                return;
            }
            uploads.RemoveAll(u => u.To == p);
            Upload up = new Upload { To = p, Id = nextTransferId++ };
            for (int i = 0; i < snapshot.Count; i++) up.Total += snapshot[i].Data.Length;
            uploads.Add(up);
            p.SaveProgress = 0f;

            NetWriter w = Session.Begin(Msg.SaveBegin);
            w.UInt(up.Id);
            w.Byte((byte)snapshot.Count);
            for (int i = 0; i < snapshot.Count; i++)
            {
                w.String(snapshot[i].Name);
                w.Int(snapshot[i].Data.Length);
                w.UInt(snapshot[i].Crc);
            }
            Session.SendTo(p, true, Channel.Bulk);
            Log.Info("Sending save (" + up.Total / 1024 + " KB) to " + p.Describe());
        }

        static void OnSaveRequest(NetPlayer from, NetReader r)
        {
            if (Session.IsHost && from != null) SendSaveTo(from);
        }

        static void PumpUploads()
        {
            for (int u = uploads.Count - 1; u >= 0; u--)
            {
                Upload up = uploads[u];
                for (int n = 0; n < ChunksPerFrame; n++)
                {
                    if (up.File >= snapshot.Count)
                    {
                        NetWriter end = Session.Begin(Msg.SaveEnd);
                        end.UInt(up.Id);
                        Session.SendTo(up.To, true, Channel.Bulk);
                        up.To.SaveProgress = 1f;
                        up.To.SaveSent = true;
                        uploads.RemoveAt(u);
                        Log.Info("Save sent to " + up.To.Describe());
                        break;
                    }
                    SaveFile f = snapshot[up.File];
                    int count = Math.Min(ChunkSize, f.Data.Length - up.Offset);
                    NetWriter w = Session.Begin(Msg.SaveChunk);
                    w.UInt(up.Id);
                    w.Byte((byte)up.File);
                    w.Int(up.Offset);
                    w.Bytes(f.Data, up.Offset, count);
                    Session.SendTo(up.To, true, Channel.Bulk);
                    up.Offset += count;
                    up.Sent += count;
                    up.To.SaveProgress = up.Total > 0 ? (float)up.Sent / up.Total : 1f;
                    if (up.Offset >= f.Data.Length) { up.File++; up.Offset = 0; }
                }
            }
        }

        public static void Update()
        {
            if (Session.IsHost && uploads.Count > 0 && snapshot != null) PumpUploads();
        }

        // ================================================================ client side

        public static void RequestSave()
        {
            if (!Session.IsClient || !Session.Connected) return;
            Session.Begin(Msg.SaveRequest);
            Session.SendToHost(true, Channel.Reliable);
        }

        static void OnSaveBegin(NetPlayer from, NetReader r)
        {
            if (!Session.IsClient) return;
            Download d = new Download();
            d.Id = r.UInt();
            int count = r.Byte();
            d.Names = new string[count];
            d.Sizes = new int[count];
            d.Crcs = new uint[count];
            d.Data = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                string name = r.String();
                int size = r.Int();
                uint crc = r.UInt();
                if (Array.IndexOf(SaveFiles, name) < 0) throw new FormatException("Unexpected save file " + name);
                if (size < 0 || size > MaxFileSize) throw new FormatException("Bad save size");
                d.Names[i] = name; d.Sizes[i] = size; d.Crcs[i] = crc;
                d.Data[i] = new byte[size];
                d.Total += size;
            }
            download = d;
            ClientHasHostSave = false;
            Session.SetLocalPhase(PlayerPhase.DownloadingSave);
            Log.Info("Receiving host save: " + count + " files, " + d.Total / 1024 + " KB");
        }

        static void OnSaveChunk(NetPlayer from, NetReader r)
        {
            if (!Session.IsClient || download == null) return;
            uint id = r.UInt();
            if (id != download.Id) return;
            int file = r.Byte();
            int offset = r.Int();
            byte[] bytes = r.Bytes(ChunkSize);
            if (file >= download.Data.Length) throw new FormatException("Bad chunk file");
            if (offset < 0 || offset + bytes.Length > download.Sizes[file]) throw new FormatException("Bad chunk range");
            Buffer.BlockCopy(bytes, 0, download.Data[file], offset, bytes.Length);
            download.Received += bytes.Length;
        }

        static void OnSaveEnd(NetPlayer from, NetReader r)
        {
            if (!Session.IsClient || download == null) return;
            uint id = r.UInt();
            if (id != download.Id) return;
            Download d = download;
            download = null;
            for (int i = 0; i < d.Names.Length; i++)
            {
                uint crc = Crc32.Compute(d.Data[i], 0, d.Data[i].Length);
                if (crc != d.Crcs[i])
                {
                    Notifications.Show("The host's save arrived damaged - asking for it again.");
                    Log.Warn("CRC mismatch on " + d.Names[i]);
                    RequestSave();
                    return;
                }
            }
            try
            {
                InstallHostSave(d.Names, d.Data);
                ClientHasHostSave = true;
                Session.SetLocalPhase(PlayerPhase.SaveReady);
                Session.Begin(Msg.SaveInstalled);
                Session.SendToHost(true, Channel.Reliable);
                Notifications.Show("Host's world downloaded. Your own save is safely backed up.");
            }
            catch (Exception e)
            {
                Log.Exception("Installing host save", e);
                Notifications.Show("Could not install the host's save: " + e.Message);
                Session.Leave("save install failed");
            }
        }

        /// <summary>Client: true if the files on disk still are the host's save (e.g. not deleted by death).</summary>
        public static bool HostSaveStillOnDisk()
        {
            return ClientHasHostSave && File.Exists(Path.Combine(SaveDir, "defaultES2File.txt"));
        }

        // ================================================================ backup / restore

        static void InstallHostSave(string[] names, byte[][] data)
        {
            if (Session.IsHost) throw new InvalidOperationException("Host never installs a remote save");
            EnsureBackup();
            for (int i = 0; i < SaveFiles.Length; i++)
            {
                string name = SaveFiles[i];
                string path = Path.Combine(SaveDir, name);
                int idx = Array.IndexOf(names, name);
                if (idx >= 0)
                {
                    string tmp = path + ".mscmp-tmp";
                    File.WriteAllBytes(tmp, data[idx]);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path); // backed up; the host has no such file
                }
            }
            Log.Info("Host save installed.");
        }

        /// <summary>Copies the player's own save away once per session (never overwrites an existing backup).</summary>
        static void EnsureBackup()
        {
            if (File.Exists(JournalPath)) return; // already backed up in this session
            Directory.CreateDirectory(BackupRoot);
            string folderName = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string folder = Path.Combine(BackupRoot, folderName);
            Directory.CreateDirectory(folder);
            StringBuilder journal = new StringBuilder();
            journal.AppendLine("MSCMP-BACKUP 1");
            journal.AppendLine(folderName);
            for (int i = 0; i < SaveFiles.Length; i++)
            {
                string src = Path.Combine(SaveDir, SaveFiles[i]);
                bool existed = File.Exists(src);
                if (existed) File.Copy(src, Path.Combine(folder, SaveFiles[i]), true);
                journal.AppendLine((existed ? "1:" : "0:") + SaveFiles[i]);
            }
            string tmp = JournalPath + ".tmp";
            File.WriteAllText(tmp, journal.ToString());
            if (File.Exists(JournalPath)) File.Delete(JournalPath);
            File.Move(tmp, JournalPath);
            Log.Info("Backed up your own save to " + folder);
            PruneOldBackups(folderName);
        }

        static void PruneOldBackups(string keep)
        {
            try
            {
                string[] dirs = Directory.GetDirectories(BackupRoot);
                Array.Sort(dirs, StringComparer.Ordinal);
                int excess = dirs.Length - 8;
                for (int i = 0; i < excess; i++)
                    if (Path.GetFileName(dirs[i]) != keep) Directory.Delete(dirs[i], true);
            }
            catch (Exception e) { Log.Exception("Pruning old backups", e); }
        }

        /// <summary>Puts the player's own save back. Safe to call any time; does nothing without a backup.</summary>
        public static bool RestoreOwnSave()
        {
            if (!File.Exists(JournalPath)) return false;
            try
            {
                string[] lines = File.ReadAllLines(JournalPath);
                if (lines.Length < 2 || !lines[0].StartsWith("MSCMP-BACKUP")) throw new IOException("Backup journal is unreadable");
                string folder = Path.Combine(BackupRoot, lines[1].Trim());
                for (int i = 2; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length < 3) continue;
                    bool existed = line[0] == '1';
                    string name = line.Substring(2);
                    if (Array.IndexOf(SaveFiles, name) < 0) continue;
                    string dst = Path.Combine(SaveDir, name);
                    string src = Path.Combine(folder, name);
                    if (existed && !File.Exists(src)) throw new IOException("Backup copy of " + name + " is missing");
                    if (File.Exists(dst)) File.Delete(dst);
                    if (existed) File.Copy(src, dst, true);
                }
                File.Delete(JournalPath);
                ClientHasHostSave = false;
                Log.Console("Your own save game has been restored.");
                return true;
            }
            catch (Exception e)
            {
                Log.Exception("Restoring your save", e);
                Notifications.Show("Could not restore your save automatically. A copy is in " + BackupRoot);
                return false;
            }
        }

        /// <summary>At startup: if the game was closed while playing someone else's world, put the player's save back.</summary>
        public static void RecoverInterruptedSession()
        {
            if (Session.Active) return;
            if (File.Exists(JournalPath))
            {
                Log.Info("Found an unfinished multiplayer session backup - restoring your save.");
                RestoreOwnSave();
            }
        }

        static void OnSessionEnded(string reason)
        {
            uploads.Clear();
            download = null;
            if (MP.InMenu) RestoreOwnSave();
            // Otherwise the player is still inside the host's world; GameSession sends them
            // back to the menu and the restore happens on the next menu load.
        }

        /// <summary>Client: the copy on disk was changed by our own game (a save or a death),
        /// so it has to be downloaded again before the next load.</summary>
        public static void MarkStale()
        {
            ClientHasHostSave = false;
        }
    }

    /// <summary>Standard CRC-32 (IEEE), used to verify transferred save files.</summary>
    internal static class Crc32
    {
        static uint[] table;

        public static uint Compute(byte[] data, int offset, int count)
        {
            if (table == null)
            {
                table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[i] = c;
                }
            }
            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++) crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
