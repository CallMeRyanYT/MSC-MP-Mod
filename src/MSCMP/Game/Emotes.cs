using MSCMP.Net;
using MSCMP.Sync;

namespace MSCMP.Game
{
    /// <summary>Things you can do with your body for the others to see (the emote menu).</summary>
    internal static class Emotes
    {
        // Index = gesture id sent over the network (0 is unused).
        public static readonly string[] Names =
        {
            "", "Wave", "Point", "Cheer", "Shrug", "Drink", "Facepalm", "Sit down", "Dance",
        };

        public const byte Wave = 1, Point = 2, Cheer = 3, Shrug = 4, Drink = 5, Facepalm = 6, Sit = 7, Dance = 8;

        public static int Count { get { return Names.Length - 1; } }

        /// <summary>Plays an emote: everyone else sees it on your body, copying bots repeat it.</summary>
        public static void Play(byte id)
        {
            if (id < 1 || id >= Names.Length) return;
            PlayerSync.SendGesture(id);
            Bots.MirrorEmote(id);
            Loopback.OnGesture(id);
        }
    }
}
