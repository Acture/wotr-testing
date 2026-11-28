using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.Client
{
    /// <summary>Build identities of mod assemblies, compared with the modules the player actually loaded.</summary>
    public static class ModuleIdentities
    {
        public static ModuleIdentity Read(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var pe = new PEReader(stream)) {
                var reader = pe.GetMetadataReader();
                return new ModuleIdentity {
                    Name = reader.GetString(reader.GetAssemblyDefinition().Name),
                    Mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("D")
                };
            }
        }

        public static List<ModuleIdentity> Read(IEnumerable<string> paths)
            => (paths ?? Enumerable.Empty<string>()).Where(path => !string.IsNullOrEmpty(path)).Select(Read).ToList();
    }
}
