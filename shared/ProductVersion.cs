using System.Reflection;

[assembly: AssemblyFileVersion(TiaPortalSourceBridge.ProductVersion.File)]
[assembly: AssemblyInformationalVersion(TiaPortalSourceBridge.ProductVersion.Release)]

namespace TiaPortalSourceBridge
{
    // The single public release version, compiled into both engines. Packaging
    // reads this constant; installed applications never need the source file.
    internal static class ProductVersion
    {
        public const string Release = "4.7.0";
        public const string File = Release + ".0";
    }
}
