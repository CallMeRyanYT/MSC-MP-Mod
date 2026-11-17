using System;
using System.IO;
using System.Text;
using MSCLoader;
using UnityEngine;

namespace MSCMP
{
    /// <summary>
    /// Logging to the MSCLoader console (things players should see) and to
    /// Mods/Config/Mod Settings/MSCMP/mscmp.log (everything, for bug reports).
    /// </summary>
    internal static class Log
    {
        static StreamWriter writer;
        static string path;
        static readonly object gate = new object();
        public static bool Verbose;

        public static string FilePath { get { return path; } }

        public static void Init(string folder)
        {
            lock (gate)
            {
                if (writer != null) return;
                try
                {
                    Directory.CreateDirectory(folder);
                    path = Path.Combine(folder, "mscmp.log");
                    string previous = Path.Combine(folder, "mscmp.previous.log");
                    if (File.Exists(path))
                    {
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(path, previous);
                    }
                    writer = new StreamWriter(path, false, new UTF8Encoding(false));
                    writer.AutoFlush = true;
                    writer.WriteLine("MSC Multiplayer " + BuildInfo.Version + " log started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    writer.WriteLine("Unity " + Application.unityVersion + ", data path " + Application.persistentDataPath);
                }
                catch (Exception e)
                {
                    writer = null;
                    UnityEngine.Debug.LogError("[MSCMP] Could not open log file: " + e.Message);
                }
            }
        }

        static void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + message;
            lock (gate)
            {
                if (writer != null)
                {
                    try { writer.WriteLine(line); } catch { }
                }
            }
        }

        /// <summary>Detail that only goes to the log file.</summary>
        public static void Debug(string message)
        {
            if (Verbose) Write("DBG", message);
        }

        /// <summary>Normal event, log file only.</summary>
        public static void Info(string message)
        {
            Write("INF", message);
        }

        /// <summary>Shown in the MSCLoader console and logged.</summary>
        public static void Console(string message)
        {
            Write("INF", message);
            try { ModConsole.Print("<color=#7fd7ff>[MSCMP]</color> " + message); } catch { }
        }

        public static void Warn(string message)
        {
            Write("WRN", message);
            try { ModConsole.Warning("[MSCMP] " + message); } catch { }
        }

        public static void Error(string message)
        {
            Write("ERR", message);
            try { ModConsole.Error("[MSCMP] " + message); } catch { }
        }

        public static void Exception(string context, Exception e)
        {
            Write("EXC", context + ": " + e);
            try { ModConsole.Error("[MSCMP] " + context + ": " + e.GetType().Name + ": " + e.Message); } catch { }
        }

        /// <summary>Logs an exception only once per key to avoid flooding the log every frame.</summary>
        public static void ExceptionOnce(string key, Exception e)
        {
            if (Once.First(key)) Exception(key, e);
        }
    }

    /// <summary>Remembers which one-time messages were already emitted.</summary>
    internal static class Once
    {
        static readonly System.Collections.Generic.HashSet<string> seen = new System.Collections.Generic.HashSet<string>();
        public static bool First(string key)
        {
            lock (seen) return seen.Add(key);
        }
    }
}
