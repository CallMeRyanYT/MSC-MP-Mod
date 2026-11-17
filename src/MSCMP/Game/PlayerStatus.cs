using MSCMP.Net;

namespace MSCMP
{
    /// <summary>A player's needs, read from the game's own global variables (0..100, drunk 0..~5).</summary>
    internal struct PlayerStatus
    {
        public float Hunger, Thirst, Fatigue, Stress, Urine, Dirt, Drunk;

        public static readonly string[] GlobalNames =
            { "PlayerHunger", "PlayerThirst", "PlayerFatigue", "PlayerStress", "PlayerUrine", "PlayerDirtiness", "PlayerDrunk" };

        public float this[int i]
        {
            get
            {
                switch (i)
                {
                    case 0: return Hunger; case 1: return Thirst; case 2: return Fatigue; case 3: return Stress;
                    case 4: return Urine; case 5: return Dirt; default: return Drunk;
                }
            }
            set
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
                switch (i)
                {
                    case 0: Hunger = value; break; case 1: Thirst = value; break; case 2: Fatigue = value; break;
                    case 3: Stress = value; break; case 4: Urine = value; break; case 5: Dirt = value; break;
                    default: Drunk = value; break;
                }
            }
        }

        public void Write(NetWriter w) { for (int i = 0; i < GlobalNames.Length; i++) w.Float(this[i]); }

        public static PlayerStatus Read(NetReader r)
        {
            PlayerStatus s = new PlayerStatus();
            for (int i = 0; i < GlobalNames.Length; i++) s[i] = r.Float();
            return s;
        }

        /// <summary>Short words for the player list ("drunk, hungry"), or "".</summary>
        public string Describe()
        {
            string r = "";
            if (Drunk > 0.4f) r = Add(r, Drunk > 2f ? "very drunk" : "drunk");
            if (Hunger > 70f) r = Add(r, "hungry");
            if (Thirst > 70f) r = Add(r, "thirsty");
            if (Fatigue > 75f) r = Add(r, "tired");
            if (Urine > 80f) r = Add(r, "needs the toilet");
            if (Stress > 75f) r = Add(r, "stressed");
            if (Dirt > 80f) r = Add(r, "dirty");
            return r;
        }

        static string Add(string a, string b) { return a.Length == 0 ? b : a + ", " + b; }
    }
}
