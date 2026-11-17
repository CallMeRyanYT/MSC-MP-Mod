using MSCMP.Net;

namespace MSCMP.Avatars
{
    /// <summary>
    /// What a player looks like. Everything here refers to the game's own
    /// character bodies and clothing textures, so remote players look exactly
    /// like the people living in Peräjärvi.
    /// </summary>
    internal struct Appearance
    {
        public byte Body;   // index into Catalog.Bodies
        public byte Shirt;  // 0 = body default, otherwise index into Catalog.Shirts
        public byte Pants;
        public byte Face;

        public void Write(NetWriter w) { w.Byte(Body); w.Byte(Shirt); w.Byte(Pants); w.Byte(Face); }

        public static Appearance Read(NetReader r)
        {
            Appearance a;
            a.Body = r.Byte(); a.Shirt = r.Byte(); a.Pants = r.Byte(); a.Face = r.Byte();
            a.Clamp();
            return a;
        }

        public void Clamp()
        {
            if (Body >= Catalog.Bodies.Length) Body = 0;
            if (Shirt >= Catalog.Shirts.Length) Shirt = 0;
            if (Pants >= Catalog.Pants.Length) Pants = 0;
            if (Face >= Catalog.Faces.Length) Face = 0;
        }

        public override string ToString()
        {
            return Catalog.Bodies[Body].Label + "/" + Catalog.Shirts[Shirt] + "/" + Catalog.Pants[Pants] + "/" + Catalog.Faces[Face];
        }
    }

    internal sealed class BodyDonor
    {
        public string Label;
        /// <summary>Path of the NPC's "Char" object (bodymesh + skeleton) in the GAME scene.</summary>
        public string CharPath;
        public BodyDonor(string label, string charPath) { Label = label; CharPath = charPath; }
    }

    /// <summary>
    /// Body and clothing choices, taken from the NPCs in the GAME scene.
    /// Paths and material names were read from the installed game (Unity 5.0
    /// scene "level2"); if a future game update moves something the avatar
    /// builder falls back to the next working donor.
    /// </summary>
    internal static class Catalog
    {
        public static readonly BodyDonor[] Bodies =
        {
            new BodyDonor("Kale", "HUMANS/Randomizer/Walkers/Kale/Pivot/Char"),
            new BodyDonor("Rauno", "HUMANS/Randomizer/Walkers/Rauno/Pivot/Char"),
            new BodyDonor("Kristian", "HUMANS/Randomizer/Walkers/Kristian/Pivot/Char"),
            new BodyDonor("Alpo", "HUMANS/Randomizer/Walkers/Alpo/Pivot/Char"),
            new BodyDonor("Unto", "HUMANS/Randomizer/Walkers/Unto/Pivot/Char"),
            new BodyDonor("Julli", "HUMANS/Randomizer/Walkers/Julli/Pivot/Char"),
            new BodyDonor("Jokke", "JOBS/HouseDrunk/Moving/JokkeHiker1/Pivot/Char"),
            new BodyDonor("Rindell (inspector)", "INSPECTION/LOD/Officer/Work/Char"),
            new BodyDonor("Suski", "JOBS/Suski/SuskiPivot/SuskiHiker/Char"),
            new BodyDonor("Ventti pig", "CABIN/Cabin/Ventti/PIG/VenttiPig/Pivot/Char"),
        };

        // Index 0 always means "keep the body's own clothes".
        public static readonly string[] Shirts =
        {
            "Default",
            "char_shirt01", "char_shirt02", "char_shirt03", "char_shirt04", "char_shirt05",
            "char_shirt06", "char_shirt07", "char_shirt08", "char_shirt09", "char_shirt10",
            "char_shirt11", "char_shirt12", "char_shirt13", "char_shirt14", "char_shirt15",
            "char_shirt16", "char_shirt17", "char_shirt18", "char_shirt19", "char_shirt20",
            "char_shirt22", "cop_shirt", "cop_shirt2", "rally_shirt",
        };

        public static readonly string[] Pants =
        {
            "Default",
            "char_pants01", "char_pants02", "char_pants03", "char_pants04",
            "char_pants05", "char_pants06", "char_pants07", "char_pants08",
            "cop_pants", "rally_pants",
        };

        public static readonly string[] Faces =
        {
            "Default",
            "char_face01", "char_face02", "char_face03", "char_face04", "char_face05",
            "char_face06", "char_face07_woman", "char_face08", "char_face09", "char_face10",
            "char_face11",
        };

        public static string[] BodyLabels()
        {
            string[] r = new string[Bodies.Length];
            for (int i = 0; i < r.Length; i++) r[i] = Bodies[i].Label;
            return r;
        }

        public static string[] Pretty(string[] names)
        {
            string[] r = new string[names.Length];
            for (int i = 0; i < r.Length; i++)
            {
                string n = names[i];
                n = n.Replace("char_", "").Replace("_", " ");
                r[i] = n.Length > 0 ? char.ToUpper(n[0]) + n.Substring(1) : n;
            }
            return r;
        }
    }
}
