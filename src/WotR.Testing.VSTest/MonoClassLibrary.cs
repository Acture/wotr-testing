using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace WotR.Testing.VSTest
{
    internal static class MonoClassLibrary
    {
        public static string Prepare(string managed, string work)
        {
            // Only framework assemblies go on Mono's class-library path. Game/Unity/mod DLLs resolve from the library's
            // rewritten runtime folder. Missing framework facades forward types; no netfx implementations are substituted.
            var framework = Directory.EnumerateFiles(managed, "*.dll").Where(p => IsFramework(Path.GetFileName(p))).ToArray();
            var names = new HashSet<string>(framework.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            var netfx = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319");
            var files = framework.Concat(Directory.EnumerateFiles(netfx, "*.dll")
                .Where(p => !names.Contains(Path.GetFileName(p)) && IsFacade(p))).OrderBy(Path.GetFileName).ToArray();
            string key;
            using (var sha = SHA256.Create()) {
                var manifest = string.Join("\n", files.Select(p => Path.GetFileName(p) + ":" + Hash(p, sha)));
                key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(manifest))).Replace("-", "").ToLowerInvariant();
            }
            var directory = Path.Combine(work, "mono-classlib", key);
            if (File.Exists(Path.Combine(directory, "mscorlib.dll"))) return directory;
            var staging = directory + ".tmp-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            try {
                foreach (var file in files) File.Copy(file, Path.Combine(staging, Path.GetFileName(file)));
                try { Directory.Move(staging, directory); }
                catch (IOException) when (Directory.Exists(directory)) { /* Another discovery/run prepared the same inputs. */ }
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            return directory;
        }

        private static string Hash(string path, HashAlgorithm sha)
        {
            using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream));
        }

        private static bool IsFramework(string name)
            => name == "mscorlib.dll" || name == "netstandard.dll" || name == "Accessibility.dll"
               || new[] { "System", "Mono.", "Microsoft.", "I18N", "Novell." }.Any(p => name.StartsWith(p, StringComparison.Ordinal));

        private static bool IsFacade(string path)
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
