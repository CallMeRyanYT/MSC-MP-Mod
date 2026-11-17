using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MSCMP.Game;
using MSCMP.Net;
using MSCMP.Saves;
using UnityEngine;

namespace MSCMP.Sync
{
    /// <summary>
    /// Shares custom music. My Summer Car plays your own .ogg songs from the Radio and CD1-CD3
    /// folders; every player has different files, so the radio would play different songs on
    /// every screen. This makes everybody hear the song of whoever played it, from the same
    /// speaker in the world (car radio, CD player...), in time.
    ///
    /// How: your songs are fingerprinted in the background (size + CRC, and the exact length
    /// from the Ogg header). About once a second we look for a speaker playing a clip with
    /// the length of one of your songs; that speaker and the song are announced. The host
    /// decides who controls a speaker (first come). A friend who doesn't have the song gets it
    /// through the host, keeps it in a cache folder, and plays it on the same speaker at the
    /// right position. Positions are re-sent every few seconds to stay in time.
    /// </summary>
    internal static class MusicSync
    {
        // ------------------------------------------------------------ songs on this computer

        sealed class Song
        {
            public string Path, Name;
            public uint Crc;
            public int Size;
            public int Rate, Channels;
            public long Samples;
        }

        static readonly List<Song> songs = new List<Song>();       // own Radio/CD folders and the cache
        static readonly object songsLock = new object();
        static bool scanning;

        // ------------------------------------------------------------ playback state

        /// <summary>A shared song on one speaker.</summary>
        sealed class Track
        {
            public string Key;                  // which speaker
            public uint Crc;
            public int Size;
            public string Name;
            public byte Owner;                  // slot of the player whose song it is
            public float StartedAt;             // local realtime when position 0 was
            // receiver side
            public WWW Loading;
            public AudioClip Clip;
            public bool Waiting;                // file still downloading
            public float NextCheck;
        }

        static readonly Dictionary<string, Track> remote = new Dictionary<string, Track>();   // others' songs we play
        static readonly Dictionary<string, Track> mine = new Dictionary<string, Track>();     // our songs others play
        static readonly Dictionary<string, Track> owners = new Dictionary<string, Track>();   // host: who controls each speaker
        static readonly HashSet<AudioClip> ourClips = new HashSet<AudioClip>();

        // ------------------------------------------------------------ speakers

        static readonly Dictionary<string, AudioSource> speakers = new Dictionary<string, AudioSource>();
        static readonly Dictionary<AudioSource, string> speakerKeys = new Dictionary<AudioSource, string>();
        static float nextScan, nextResync;
        static bool ready;

        // ------------------------------------------------------------ transfers

        sealed class Upload { public NetPlayer To; public byte[] Data; public uint Crc; public int Offset; }
        sealed class Download { public uint Crc; public int Size; public byte[] Data; public int Received; public string Name; }

        static readonly List<Upload> uploads = new List<Upload>();
        static readonly Dictionary<uint, Download> downloads = new Dictionary<uint, Download>();
        static readonly Dictionary<uint, List<NetPlayer>> waitingForHost = new Dictionary<uint, List<NetPlayer>>(); // host: who wants a file the host is still fetching
        static readonly Dictionary<uint, float> requested = new Dictionary<uint, float>();

        const int ChunkSize = 32 * 1024;
        const int ChunksPerFrame = 2;
        const int MaxSongBytes = 40 * 1024 * 1024;
        const float ResyncInterval = 4f;
        const float DriftTolerance = 0.6f;

        static string CacheDir
        {
            get
            {
                string log = Log.FilePath;
                string dir = log != null ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(log)) : ".";
                return System.IO.Path.Combine(dir, "music_cache");
            }
        }

        public static void Init()
        {
            Session.On(Msg.MusicPlay, OnPlay);
            Session.On(Msg.MusicStop, OnStop);
            Session.On(Msg.MusicRequest, OnRequest);
            Session.On(Msg.MusicChunk, OnChunk);
            Session.PlayerLeft += OnPlayerLeft;
            Session.Ended += reason => StopAllRemote();
            ScanSongs();
        }

        // ================================================================ song catalogue

        /// <summary>Reads the song folders in the background (file reading and CRCs only).</summary>
        public static void ScanSongs()
        {
            if (scanning) return;
            scanning = true;
            string gameDir = System.IO.Path.GetFullPath(".");
            string cache = CacheDir;
            Thread t = new Thread(() =>
            {
                List<Song> found = new List<Song>();
                try
                {
                    foreach (string folder in new[] { "Radio", "CD1", "CD2", "CD3" })
                        AddFolder(found, System.IO.Path.Combine(gameDir, folder));
                    if (Directory.Exists(cache)) AddFolder(found, cache);
                }
                catch (Exception e) { Log.Info("Music scan problem: " + e.Message); } // file only: this runs on a background thread
                lock (songsLock) { songs.Clear(); songs.AddRange(found); }
                scanning = false;
                Log.Info("Music: " + found.Count + " song(s) found (own folders and cache).");
            });
            t.IsBackground = true;
            t.Start();
        }

        static void AddFolder(List<Song> into, string folder)
        {
            if (!Directory.Exists(folder)) return;
            foreach (string f in Directory.GetFiles(folder, "*.ogg"))
            {
                try
                {
                    FileInfo fi = new FileInfo(f);
                    if (fi.Length <= 0 || fi.Length > MaxSongBytes) continue;
                    byte[] data = File.ReadAllBytes(f);
                    Song s = new Song { Path = f, Name = System.IO.Path.GetFileNameWithoutExtension(f), Size = data.Length, Crc = Crc32.Compute(data, 0, data.Length) };
                    if (!ReadOggInfo(data, out s.Rate, out s.Channels, out s.Samples)) continue;
                    into.Add(s);
                }
                catch (Exception e) { Log.Debug("Music: skipped " + f + ": " + e.Message); }
            }
        }

        /// <summary>Sample rate, channels and length (in samples) of an Ogg Vorbis file, from its
        /// headers alone: the identification header on the first page and the granule position
        /// of the last page.</summary>
        static bool ReadOggInfo(byte[] d, out int rate, out int channels, out long samples)
        {
            rate = 0; channels = 0; samples = 0;
            int id = IndexOf(d, new byte[] { 1, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s' }, 0, Math.Min(d.Length, 512));
            if (id < 0 || id + 16 > d.Length) return false;
            channels = d[id + 11];
            rate = BitConverter.ToInt32(d, id + 12);
            for (int i = d.Length - 27; i >= Math.Max(0, d.Length - 128 * 1024); i--)
            {
                if (d[i] == 'O' && d[i + 1] == 'g' && d[i + 2] == 'g' && d[i + 3] == 'S')
                {
                    samples = BitConverter.ToInt64(d, i + 6);
                    break;
                }
            }
            return rate > 0 && channels > 0 && samples > 0;
        }

        static int IndexOf(byte[] d, byte[] what, int from, int to)
        {
            for (int i = from; i <= to - what.Length; i++)
            {
                int k = 0;
                while (k < what.Length && d[i + k] == what[k]) k++;
                if (k == what.Length) return i;
            }
            return -1;
        }

        static Song FindSong(uint crc, int size)
        {
            lock (songsLock)
                foreach (Song s in songs) if (s.Crc == crc && s.Size == size && File.Exists(s.Path)) return s;
            return null;
        }

        static Song MatchClip(AudioClip c)
        {
            if (c == null) return null;
            lock (songsLock)
                foreach (Song s in songs)
                    if (s.Rate == c.frequency && s.Channels == c.channels && Math.Abs(s.Samples - c.samples) <= s.Rate / 10)
                        return s;
            return null;
        }

        // ================================================================ speakers

        public static void OnWorldReady()
        {
            speakers.Clear();
            speakerKeys.Clear();
            List<KeyValuePair<string, AudioSource>> keyed = new List<KeyValuePair<string, AudioSource>>();
            foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(typeof(AudioSource)))
            {
                AudioSource a = o as AudioSource;
                if (a == null || !GameUtil.IsSceneObject(a.gameObject)) continue;
                string path = GameUtil.PathOf(a.transform);
                if (path.StartsWith("MSCMP", StringComparison.Ordinal)) continue;
                keyed.Add(new KeyValuePair<string, AudioSource>(path + "|" + GameUtil.SiblingChain(a.transform), a));
            }
            keyed.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            Dictionary<string, int> dup = new Dictionary<string, int>();
            foreach (KeyValuePair<string, AudioSource> kv in keyed)
            {
                string path = GameUtil.PathOf(kv.Value.transform);
                int n;
                dup.TryGetValue(path, out n);
                dup[path] = n + 1;
                string key = path + "#" + n;
                speakers[key] = kv.Value;
                speakerKeys[kv.Value] = key;
            }
            ready = true;
            ScanSongs(); // new songs may have been added since the game started
        }

        public static void OnWorldLeft()
        {
            ready = false;
            StopAllRemote();
            mine.Clear();
            owners.Clear();
            speakers.Clear();
            speakerKeys.Clear();
        }

        // ================================================================ per frame

        public static void Update()
        {
            if (Session.Active && Session.Connected) PumpUploads();
            if (!ready || !Session.Active || !Session.Connected || Session.Local == null) return;
            float now = Time.realtimeSinceStartup;

            foreach (Track t in remote.Values) KeepPlaying(t, now);

            if (now >= nextScan)
            {
                nextScan = now + 1f;
                if (Options.ShareMusic) DetectOwnMusic(now);
                else if (mine.Count > 0) { foreach (string k in new List<string>(mine.Keys)) SendStop(k); mine.Clear(); }
            }
            if (now >= nextResync)
            {
                nextResync = now + ResyncInterval;
                foreach (Track t in mine.Values)
                {
                    AudioSource a;
                    if (speakers.TryGetValue(t.Key, out a) && a != null) t.StartedAt = now - a.time;
                    SendPlay(t, null);
                }
            }
        }

        /// <summary>Finds speakers playing one of our songs, and notices when they stop.</summary>
        static void DetectOwnMusic(float now)
        {
            foreach (KeyValuePair<string, AudioSource> kv in speakers)
            {
                AudioSource a = kv.Value;
                if (a == null || !a.isPlaying || a.clip == null || ourClips.Contains(a.clip)) continue;
                if (remote.ContainsKey(kv.Key)) continue; // a friend's song is on this speaker
                Song s = MatchClip(a.clip);
                if (s == null) continue;
                Track t;
                if (mine.TryGetValue(kv.Key, out t) && t.Crc == s.Crc) continue;
                t = new Track { Key = kv.Key, Crc = s.Crc, Size = s.Size, Name = s.Name, Owner = Session.Local.Slot, StartedAt = now - a.time };
                mine[kv.Key] = t;
                if (Session.IsHost) owners[kv.Key] = t;
                SendPlay(t, null);
                Log.Info("Music: sharing \"" + s.Name + "\" on " + kv.Key);
            }
            foreach (string key in new List<string>(mine.Keys))
            {
                AudioSource a;
                Track t = mine[key];
                bool still = speakers.TryGetValue(key, out a) && a != null && a.isPlaying && a.clip != null && !ourClips.Contains(a.clip);
                if (still) { Song s = MatchClip(a.clip); still = s != null && s.Crc == t.Crc; }
                if (still) continue;
                mine.Remove(key);
                if (Session.IsHost) owners.Remove(key);
                SendStop(key);
            }
        }

        /// <summary>Plays a friend's song on our copy of the speaker, at the right position.</summary>
        static void KeepPlaying(Track t, float now)
        {
            AudioSource a;
            if (!speakers.TryGetValue(t.Key, out a) || a == null) return;
            if (t.Clip == null)
            {
                if (t.Loading == null)
                {
                    if (!Options.HearMusic) return;
                    Song s = FindSong(t.Crc, t.Size);
                    if (s == null) { RequestFile(t, now); return; }
                    t.Waiting = false;
                    t.Loading = new WWW("file:///" + s.Path.Replace('\\', '/'));
                }
                if (!t.Loading.isDone) return;
                if (!string.IsNullOrEmpty(t.Loading.error))
                {
                    Log.Warn("Music: could not load \"" + t.Name + "\": " + t.Loading.error);
                    t.Loading = null;
                    t.NextCheck = now + 30f;
                    return;
                }
                t.Clip = t.Loading.GetAudioClip(true, false, AudioType.OGGVORBIS);
                t.Loading = null;
                if (t.Clip == null) return;
                ourClips.Add(t.Clip);
            }
            if (!t.Clip.isReadyToPlay) return;
            float pos = now - t.StartedAt;
            if (pos < 0f || pos >= t.Clip.length) return; // over (the owner sends a stop)
            if (a.clip != t.Clip)
            {
                a.clip = t.Clip;
                a.time = pos;
                a.Play();
            }
            else if (!a.isPlaying)
            {
                a.time = pos;
                a.Play();
            }
            else if (now >= t.NextCheck)
            {
                t.NextCheck = now + 1f;
                if (Mathf.Abs(a.time - pos) > DriftTolerance) a.time = pos;
            }
        }

        static void StopRemote(string key)
        {
            Track t;
            if (!remote.TryGetValue(key, out t)) return;
            remote.Remove(key);
            AudioSource a;
            if (t.Clip != null && speakers.TryGetValue(key, out a) && a != null && a.clip == t.Clip) a.Stop();
            ReleaseClip(t);
        }

        static void StopAllRemote()
        {
            foreach (string k in new List<string>(remote.Keys)) StopRemote(k);
            owners.Clear();
        }

        static void ReleaseClip(Track t)
        {
            if (t.Clip == null) return;
            ourClips.Remove(t.Clip);
            // Free the decoded audio unless another speaker still uses the same song.
            bool used = false;
            foreach (Track o in remote.Values) if (o.Clip == t.Clip) used = true;
            if (!used) UnityEngine.Object.Destroy(t.Clip);
            t.Clip = null;
        }

        static void OnPlayerLeft(NetPlayer p)
        {
            if (p == null) return;
            foreach (KeyValuePair<string, Track> kv in new List<KeyValuePair<string, Track>>(remote))
                if (kv.Value.Owner == p.Slot) StopRemote(kv.Key);
            if (Session.IsHost)
                foreach (KeyValuePair<string, Track> kv in new List<KeyValuePair<string, Track>>(owners))
                    if (kv.Value.Owner == p.Slot) { owners.Remove(kv.Key); BroadcastStop(kv.Key, p.Slot, null); }
            uploads.RemoveAll(u => u.To == p);
            foreach (List<NetPlayer> l in waitingForHost.Values) l.Remove(p);
        }

        // ================================================================ messages: play / stop

        static void WritePlay(NetWriter w, Track t, float now)
        {
            w.String(t.Key);
            w.UInt(t.Crc);
            w.Int(t.Size);
            w.String(t.Name ?? "");
            w.Byte(t.Owner);
            w.Float(now - t.StartedAt);     // position right now
            w.Float(now);                   // sender's clock
        }

        static void SendPlay(Track t, NetPlayer onlyTo)
        {
            float now = Time.realtimeSinceStartup;
            NetWriter w = Session.Begin(Msg.MusicPlay);
            WritePlay(w, t, now);
            if (onlyTo != null) Session.SendTo(onlyTo, true, Channel.Reliable);
            else Session.SendToAll(true, Channel.Reliable);
        }

        static void SendStop(string key)
        {
            NetWriter w = Session.Begin(Msg.MusicStop);
            w.String(key);
            w.Byte(Session.Local.Slot);
            Session.SendToAll(true, Channel.Reliable);
        }

        static void BroadcastStop(string key, byte owner, NetPlayer except)
        {
            NetWriter w = Session.Begin(Msg.MusicStop);
            w.String(key);
            w.Byte(owner);
            Session.BroadcastInGame(true, Channel.Reliable, except);
        }

        static void OnPlay(NetPlayer from, NetReader r)
        {
            string key = r.String();
            uint crc = r.UInt();
            int size = r.Int();
            string name = r.String();
            byte owner = r.Byte();
            float pos = r.Float();
            float sent = r.Float();
            if (from == null || size <= 0 || size > MaxSongBytes || float.IsNaN(pos)) return;
            if (name.Length > 80) name = name.Substring(0, 80);
            float now = Time.realtimeSinceStartup;
            pos += Mathf.Clamp(now - from.ToLocalTime(sent), 0f, 2f); // time it took to get here

            if (Session.IsHost)
            {
                owner = from.Slot;
                Track current;
                if (owners.TryGetValue(key, out current) && current.Owner != from.Slot)
                {
                    // Someone else already plays a song on this speaker: they win; tell the latecomer.
                    NetWriter back = Session.Begin(Msg.MusicPlay);
                    WritePlay(back, current, now);
                    Session.SendTo(from, true, Channel.Reliable);
                    return;
                }
                Track t = current != null && current.Crc == crc ? current : new Track { Key = key, Crc = crc, Size = size, Name = name, Owner = owner };
                t.StartedAt = now - pos;
                owners[key] = t;
                if (mine.ContainsKey(key)) mine.Remove(key); // a client's song replaces ours here
                NetWriter w = Session.Begin(Msg.MusicPlay);
                WritePlay(w, t, now);
                Session.BroadcastInGame(true, Channel.Reliable, from);
            }
            if (Session.Local != null && owner == Session.Local.Slot) return;
            StartRemote(key, crc, size, name, owner, now - pos);
        }

        static void StartRemote(string key, uint crc, int size, string name, byte owner, float startedAt)
        {
            Track t;
            if (remote.TryGetValue(key, out t) && t.Crc == crc)
            {
                t.StartedAt = startedAt; // just keeping time
                t.Owner = owner;
                return;
            }
            if (t != null) StopRemote(key);
            if (mine.ContainsKey(key)) mine.Remove(key); // the host gave this speaker to someone else
            remote[key] = new Track { Key = key, Crc = crc, Size = size, Name = name, Owner = owner, StartedAt = startedAt };
            NetPlayer who = Session.BySlot(owner);
            if (Options.HearMusic) Notifications.Show((who != null ? who.Name : "A friend") + " is playing \"" + name + "\".");
        }

        static void OnStop(NetPlayer from, NetReader r)
        {
            string key = r.String();
            byte owner = r.Byte();
            if (from == null) return;
            if (Session.IsHost)
            {
                Track current;
                if (!owners.TryGetValue(key, out current) || current.Owner != from.Slot) return;
                owners.Remove(key);
                BroadcastStop(key, from.Slot, from);
                owner = from.Slot;
            }
            Track t;
            if (remote.TryGetValue(key, out t) && t.Owner == owner) StopRemote(key);
        }

        /// <summary>Host: tells a player who just joined which songs are playing.</summary>
        public static void SendCatchUp(NetPlayer p)
        {
            foreach (Track t in owners.Values) if (t.Owner != p.Slot) SendPlay(t, p);
        }

        // ================================================================ file transfer

        static void RequestFile(Track t, float now)
        {
            t.Waiting = true;
            float last;
            if (requested.TryGetValue(t.Crc, out last) && now - last < 20f) return;
            if (downloads.ContainsKey(t.Crc)) return;
            requested[t.Crc] = now;
            if (Session.IsHost)
            {
                // The owner is a client: ask them to send it to us.
                NetPlayer owner = Session.BySlot(t.Owner);
                if (owner != null && !owner.IsLocal) SendRequest(owner, t.Crc, t.Size, t.Name);
            }
            else
            {
                NetPlayer host = Session.BySlot(0);
                if (host != null) SendRequest(host, t.Crc, t.Size, t.Name);
            }
            Log.Info("Music: downloading \"" + t.Name + "\" (" + t.Size / 1024 + " KB)");
        }

        static void SendRequest(NetPlayer to, uint crc, int size, string name)
        {
            NetWriter w = Session.Begin(Msg.MusicRequest);
            w.UInt(crc);
            w.Int(size);
            w.String(name ?? "");
            Session.SendTo(to, true, Channel.Reliable);
        }

        static void OnRequest(NetPlayer from, NetReader r)
        {
            uint crc = r.UInt();
            int size = r.Int();
            string name = r.String();
            if (from == null || from.IsLocal) return;
            if (!Session.IsHost && !from.IsHost) return;
            Song s = FindSong(crc, size);
            if (s != null && Options.ShareMusic)
            {
                StartUpload(from, s);
                return;
            }
            if (!Session.IsHost) return;
            // The host doesn't have it yet: fetch it from whoever plays it, then pass it on.
            List<NetPlayer> list;
            if (!waitingForHost.TryGetValue(crc, out list)) waitingForHost[crc] = list = new List<NetPlayer>();
            if (!list.Contains(from)) list.Add(from);
            foreach (Track t in owners.Values)
            {
                if (t.Crc != crc) continue;
                NetPlayer owner = Session.BySlot(t.Owner);
                if (owner != null && !owner.IsLocal && owner != from && !downloads.ContainsKey(crc))
                {
                    requested[crc] = Time.realtimeSinceStartup;
                    SendRequest(owner, crc, size, name);
                }
                break;
            }
        }

        static void StartUpload(NetPlayer to, Song s)
        {
            if (uploads.Exists(u => u.To == to && u.Crc == s.Crc)) return;
            try
            {
                byte[] data = File.ReadAllBytes(s.Path);
                uploads.Add(new Upload { To = to, Data = data, Crc = s.Crc });
            }
            catch (Exception e) { Log.Warn("Music: could not read " + s.Path + ": " + e.Message); }
        }

        static void PumpUploads()
        {
            for (int i = uploads.Count - 1; i >= 0; i--)
            {
                Upload u = uploads[i];
                for (int n = 0; n < ChunksPerFrame && u.Offset < u.Data.Length; n++)
                {
                    int count = Math.Min(ChunkSize, u.Data.Length - u.Offset);
                    NetWriter w = Session.Begin(Msg.MusicChunk);
                    w.UInt(u.Crc);
                    w.Int(u.Data.Length);
                    w.Int(u.Offset);
                    w.Bytes(u.Data, u.Offset, count);
                    Session.SendTo(u.To, true, Channel.Bulk);
                    u.Offset += count;
                }
                if (u.Offset >= u.Data.Length) uploads.RemoveAt(i);
            }
        }

        static void OnChunk(NetPlayer from, NetReader r)
        {
            uint crc = r.UInt();
            int size = r.Int();
            int offset = r.Int();
            byte[] bytes = r.Bytes(ChunkSize);
            if (from == null || from.IsLocal || size <= 0 || size > MaxSongBytes) return;
            if (!Session.IsHost && !from.IsHost) return;
            Download d;
            if (!downloads.TryGetValue(crc, out d))
            {
                if (FindSong(crc, size) != null) return; // already have it
                d = new Download { Crc = crc, Size = size, Data = new byte[size] };
                foreach (Track t in remote.Values) if (t.Crc == crc) d.Name = t.Name;
                foreach (Track t in owners.Values) if (t.Crc == crc) d.Name = t.Name;
                downloads[crc] = d;
            }
            if (d.Size != size || offset < 0 || offset + bytes.Length > size) throw new FormatException("Bad music chunk");
            Buffer.BlockCopy(bytes, 0, d.Data, offset, bytes.Length);
            d.Received += bytes.Length;
            if (d.Received < d.Size) return;

            downloads.Remove(crc);
            if (Crc32.Compute(d.Data, 0, d.Data.Length) != crc)
            {
                Log.Warn("Music: a song arrived damaged; asking again.");
                requested.Remove(crc);
                return;
            }
            Song s = SaveToCache(d);
            if (s == null) return;
            Log.Info("Music: got \"" + s.Name + "\".");
            // Host: pass it on to everyone who asked for it.
            List<NetPlayer> list;
            if (Session.IsHost && waitingForHost.TryGetValue(crc, out list))
            {
                waitingForHost.Remove(crc);
                foreach (NetPlayer p in list) StartUpload(p, s);
            }
        }

        static Song SaveToCache(Download d)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                string safe = d.Name ?? "song";
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                if (safe.Length > 60) safe = safe.Substring(0, 60);
                string path = System.IO.Path.Combine(CacheDir, d.Crc.ToString("x8") + "_" + safe + ".ogg");
                File.WriteAllBytes(path, d.Data);
                Song s = new Song { Path = path, Name = d.Name ?? safe, Crc = d.Crc, Size = d.Size };
                if (!ReadOggInfo(d.Data, out s.Rate, out s.Channels, out s.Samples)) { s.Rate = 0; }
                lock (songsLock) songs.Add(s);
                PruneCache();
                return s;
            }
            catch (Exception e)
            {
                Log.Exception("Music cache", e);
                return null;
            }
        }

        /// <summary>Keeps the cache under 500 MB, dropping the songs not played for longest.</summary>
        static void PruneCache()
        {
            try
            {
                List<FileInfo> files = new List<FileInfo>(new DirectoryInfo(CacheDir).GetFiles("*.ogg"));
                long total = 0;
                foreach (FileInfo f in files) total += f.Length;
                if (total <= 500L * 1024 * 1024) return;
                files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                foreach (FileInfo f in files)
                {
                    if (total <= 400L * 1024 * 1024) break;
                    total -= f.Length;
                    lock (songsLock) songs.RemoveAll(s => s.Path == f.FullName);
                    f.Delete();
                }
            }
            catch (Exception e) { Log.Debug("Music cache prune: " + e.Message); }
        }
    }
}
