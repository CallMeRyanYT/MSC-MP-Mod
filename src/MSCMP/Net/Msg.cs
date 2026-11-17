namespace MSCMP.Net
{
    /// <summary>Steam P2P channels.</summary>
    internal static class Channel
    {
        /// <summary>Reliable, ordered: session control, ownership, world events.</summary>
        public const int Reliable = 0;
        /// <summary>Unreliable: high-rate player/object/vehicle state.</summary>
        public const int State = 1;
        /// <summary>Reliable bulk transfer (save files, catch-up).</summary>
        public const int Bulk = 2;
        public const int Count = 3;
    }

    /// <summary>All network message types. The first byte of every packet.</summary>
    internal enum Msg : byte
    {
        // --- session (Reliable) ---
        Hello = 1,              // client -> host: protocol, version, name, appearance
        Welcome = 2,            // host -> client: slot, players, host phase
        Reject = 3,             // host -> client: reason
        PlayerJoined = 4,       // host -> all
        PlayerLeft = 5,         // host -> all
        Ping = 6,
        Pong = 7,
        Chat = 8,               // client -> host -> all
        Appearance = 9,         // client -> host -> all
        Kick = 10,              // host -> client
        PlayerPhase = 11,       // host -> all: a player's loading phase changed
        Notice = 12,            // host -> client: text notification
        Batch = 13,             // host -> client: many reliable messages in one packet

        // --- save / loading (Bulk) ---
        SaveRequest = 20,       // client -> host
        SaveBegin = 21,         // host -> client
        SaveChunk = 22,
        SaveEnd = 23,
        SaveInstalled = 24,     // client -> host
        HostInGame = 25,        // host -> clients: host is in GAME, load now
        HostInMenu = 26,        // host -> clients: host returned to the menu
        ClientLoaded = 27,      // client -> host: GAME loaded and world registered
        CatchUpBegin = 28,      // host -> client
        CatchUpEnd = 29,        // host -> client
        ClientReady = 30,       // client -> host
        Manifest = 31,          // host -> client: registered object ids (diagnostics)

        // --- players ---
        PlayerState = 40,       // State channel
        Gesture = 41,           // Reliable
        BotSpawn = 42,          // owner slot, bot id, name, appearance (also a body change)
        BotRemove = 43,         // owner slot, bot id
        BotStates = 44,         // State channel: owner slot, count, (bot id, snapshot)...
        BotAction = 45,         // owner slot, bot id, 1 = wave / 2 = reach (+ point)
        PlayerStatus = 46,      // client -> host -> all: slot, needs (hunger, thirst, drunk...)
        PlayerRestore = 47,     // host -> client: put back at the last place, with the same needs
        MapPing = 48,           // client -> host -> all: slot, point, "look here" marker
        PlayerDied = 49,        // client -> host: forget their saved place (they start fresh)

        // --- objects ---
        ObjOwnRequest = 50,
        ObjOwner = 51,          // host -> all: owner changed
        ObjOwnDeny = 52,
        ObjRelease = 53,        // owner -> host
        ObjStates = 54,         // State channel, batched
        ObjSpawn = 55,          // host -> all
        ObjSpawnRequest = 56,   // client -> host
        ObjDestroy = 57,
        ObjRest = 58,           // reliable final resting transform

        // --- vehicles ---
        VehOwnRequest = 60,
        VehOwner = 61,          // host -> all: driver/owner changed
        VehRelease = 62,
        VehState = 63,          // State channel
        SeatRequest = 64,       // client -> host: passenger seat
        Seats = 65,             // host -> all: passenger occupancy
        VehOwnDeny = 66,

        // --- world ---
        FsmState = 70,          // client -> host -> all
        TimeWeather = 71,       // host -> all (periodic)
        Money = 72,             // client -> host (delta) / host -> all (value)
        FsmSnapshot = 73,       // reserved
        FsmSpawn = 74,          // object created by a world event: id, name, pose (fallback link)
        VarValue = 75,          // world value (fuel, fluids, wear, paint...) set by its owner
        VarAdd = 76,            // amount added to a world value by someone else (pouring)

        // --- music ---
        MusicPlay = 77,         // speaker, song (crc, size, name), owner, position, sender clock
        MusicStop = 78,         // speaker, owner
        MusicRequest = 79,      // "send me this song" (to the host, or host -> the song's owner)
        MusicChunk = 80,        // Bulk channel: crc, size, offset, bytes

        // --- host tools ---
        PlayerTeleport = 81,    // host -> client: "come here" (feet position, facing)
    }

    internal static class Protocol
    {
        /// <summary>Bump whenever the wire format changes. Peers must match exactly.</summary>
        public const ushort Version = 5;          // 5: shared music; 4: needs, restore, pings, emotes; 3: bots; 2: world values
        public const int MaxPlayers = 8;
        public const int MaxPacket = 1100;          // unreliable payload budget (Steam limit is 1200)
        public const int MaxReliablePacket = 512 * 1024;
        public const string LobbyKey = "mscmp";
        public const string LobbyVersionKey = "mscmp_ver";
    }

    /// <summary>Loading/connection phase of a player, shown in the player list.</summary>
    internal enum PlayerPhase : byte
    {
        Connecting = 0,
        InMenu = 1,
        DownloadingSave = 2,
        SaveReady = 3,
        Loading = 4,
        Syncing = 5,
        Playing = 6,
    }
}
