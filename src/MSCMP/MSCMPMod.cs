using MSCLoader;

namespace MSCMP
{
    /// <summary>MSCLoader entry point. Everything real happens in <see cref="MP"/>.</summary>
    public class MSCMPMod : Mod
    {
        public override string ID { get { return "MSCMP"; } }
        public override string Name { get { return "MSC Multiplayer"; } }
        public override string Author { get { return "CallMeRyanYT"; } }
        public override string Version { get { return BuildInfo.Version; } }
        public override string Description
        {
            get
            {
                return "Play My Summer Car together with your friends through Steam: invite them, they get your world, " +
                       "and you see each other walking, driving, carrying things and building the car. " +
                       "Press F2 in the main menu or in game. (Large parts of this mod were written with the help of AI.)";
            }
        }
        public override MSCLoader.Game SupportedGames { get { return MSCLoader.Game.MySummerCar; } }

        public override void ModSetup()
        {
            MP.Mod = this;
            SetupFunction(Setup.ModSettings, Options.Create);
            SetupFunction(Setup.ModSettingsLoaded, Options.Loaded);
            SetupFunction(Setup.OnMenuLoad, MP.OnMenuLoad);
            SetupFunction(Setup.OnNewGame, MP.OnNewGame);
            SetupFunction(Setup.PostLoad, MP.OnGameLoaded);
            SetupFunction(Setup.OnSave, MP.OnGameSaved);
            SetupFunction(Setup.Update, MP.Tick);
        }
    }
}
