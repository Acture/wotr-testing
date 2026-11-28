using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace WotR.Testing.Client
{
    /// <summary>
    /// Contract facades that portable test libraries (xUnit's core among them) reference but the player does not ship.
    /// As for the offline runtime, they come from the local .NET Framework and only forward types to the player's own
    /// class library; no framework implementation is substituted.
    /// </summary>
    public static class PlayerFacades
    {
        public static IReadOnlyList<string> Find(string installRoot)
        {
            var managed = Path.Combine(installRoot, "Wrath_Data", "Managed");
            var netfx = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319");
            if (!Directory.Exists(netfx)) return Array.Empty<string>();
            return Directory.EnumerateFiles(netfx, "*.dll")
                .Where(path => !File.Exists(Path.Combine(managed, Path.GetFileName(path))) && IsFacade(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>No types of its own, only forwarders.</summary>
        public static bool IsFacade(string path)
        {
            try {
                using (var stream = File.OpenRead(path))
                using (var pe = new PEReader(stream)) {
                    if (!pe.HasMetadata) return false;
                    var metadata = pe.GetMetadataReader();
                    return metadata.TypeDefinitions.Count <= 1 && metadata.ExportedTypes.Count > 0;
                }
            }
            catch (BadImageFormatException) { return false; }
        }
    }
}
