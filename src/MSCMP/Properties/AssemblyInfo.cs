using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("MSC Multiplayer")]
[assembly: AssemblyDescription("Co-op multiplayer for My Summer Car using Steam invites.")]
[assembly: AssemblyCompany("MSC-MP-Mod contributors")]
[assembly: AssemblyProduct("MSCMP")]
[assembly: AssemblyCopyright("MSC-MP-Mod contributors")]
// MSCLoader asks mods to disclose AI-generated code here.
[assembly: AssemblyTrademark("AI-assisted: large parts of this mod were written with Claude (Anthropic).")]
[assembly: ComVisible(false)]
[assembly: Guid("6d1f3c0e-6a8b-4a55-9a1e-6b2c1f0e7a42")]
[assembly: AssemblyVersion(MSCMP.BuildInfo.Version + ".0")]
[assembly: AssemblyFileVersion(MSCMP.BuildInfo.Version + ".0")]

namespace MSCMP
{
    internal static class BuildInfo
    {
        /// <summary>Mod version. Both players must run the same protocol version.</summary>
        public const string Version = "0.9.0";
    }
}
