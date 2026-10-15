using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// Chooses the 0Harmony.dll a run uses and compares mods with other Harmony copies. Players' games differ: WotR ships
    /// Harmony 2.0.4 in Wrath_Data/Managed, each UnityModManager release ships its own copy, and every mod binds to
    /// whichever 0Harmony is loaded first. A member that exists only in the copy a run uses can fail with
    /// MissingMethodException in a player's game.
    /// </summary>
    public static class HarmonyCompatibility
    {
        public const string HarmonyVariable = "WOTR_HARMONY";
        public const string ReferencesVariable = "WOTR_HARMONY_REFERENCES";
        public const string Umm = "umm";
        public const string Game = "game";
        private const string HarmonyAssembly = "0Harmony";

        /// <summary>The 0Harmony.dll for umm (UnityModManager's copy), game (Wrath_Data/Managed) or a file path.</summary>
        public static string Resolve(string value, string managed)
        {
            var path = value switch
            {
                Umm => Path.Combine(managed, "UnityModManager", HarmonyAssembly + ".dll"),
                Game => Path.Combine(managed, HarmonyAssembly + ".dll"),
                _ => Path.GetFullPath(value),
            };
            if (!File.Exists(path))
                throw new OfflineEnvironmentMissingException($"Harmony copy '{value}' not found: {path}. Use umm, game or the path of a 0Harmony.dll.");
            return path;
        }

        public static HarmonyCopy Describe(string value, string path) => new()
        {
            Source = value is Umm or Game ? value : "file",
            Path = path,
            Version = FileVersion(path),
            Sha256 = OfflineRuntime.FileSha256(path),
        };

        /// <summary>
        /// Compares the assemblies with every reference copy other than the one the run uses, and returns the report with
        /// one warning per reference copy and assembly that references members the copy lacks.
        /// </summary>
        public static HarmonyReport Compare(string managed, HarmonyCopy used, IEnumerable<HarmonyCopy> references, IReadOnlyCollection<string> assemblies, List<string> warnings)
        {
            var report = new HarmonyReport { Used = used };
            foreach (var reference in references.Where(r => r.Sha256 != used.Sha256).GroupBy(r => r.Sha256).Select(g => g.First()))
            {
                var comparison = new HarmonyComparison { Reference = reference };
                using var resolver = new ReferenceResolver(managed, reference.Path);
                foreach (var path in assemblies)
                {
                    var missing = Missing(path, resolver);
                    if (missing.Count == 0) continue;
                    comparison.Missing[Path.GetFileName(path)] = missing;
                    warnings.Add($"{Path.GetFileName(path)} references Harmony members missing from {Label(reference)}: {string.Join(", ", missing)}. "
                        + $"This run uses {Label(used)}. In the game every mod uses whichever 0Harmony is loaded first; if that is a copy "
                        + "without these members, patching fails with MissingMethodException or MissingFieldException.");
                }
                report.References.Add(comparison);
            }
            return report;
        }

        private static string Label(HarmonyCopy copy)
        {
            var name = copy.Source switch
            {
                Umm => "UnityModManager's 0Harmony.dll",
                Game => "the game's own 0Harmony.dll",
                _ => "0Harmony.dll",
            };
            return $"{name} {copy.Version} ({copy.Path})";
        }

        private static List<string> Missing(string path, IAssemblyResolver resolver)
        {
            ModuleDefinition module;
            try
            {
                module = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver });
            }
            catch (BadImageFormatException)
            {
                return new List<string>();
            }
            using (module)
            {
                if (!module.AssemblyReferences.Any(r => r.Name == HarmonyAssembly)) return new List<string>();
                var types = module.GetTypeReferences().Where(IsHarmony).Where(t => TryResolve(t.Resolve) == null).Select(t => t.FullName);
                var members = module.GetMemberReferences().Where(m => IsHarmony(m.DeclaringType)).Where(m => TryResolve(m.Resolve) == null).Select(Signature);
                return types.Concat(members).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            }
        }

        private static bool IsHarmony(TypeReference type)
            => type?.GetElementType().Scope is AssemblyNameReference scope && scope.Name == HarmonyAssembly;

        private static object TryResolve(Func<object> resolve)
        {
            try
            {
                return resolve();
            }
            catch (AssemblyResolutionException)
            {
                return null;
            }
        }

        private static string Signature(MemberReference member) => member switch
        {
            MethodReference method => $"{method.DeclaringType.GetElementType().FullName}.{method.Name}({string.Join(", ", method.Parameters.Select(p => p.ParameterType.FullName))})",
            _ => $"{member.DeclaringType.GetElementType().FullName}.{member.Name}",
        };

        private static string FileVersion(string path)
        {
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion;
            return string.IsNullOrEmpty(version) ? "no file version" : version;
        }

        /// <summary>Resolves 0Harmony to the reference copy and everything else from the game's Managed folder only.</summary>
        private sealed class ReferenceResolver : DefaultAssemblyResolver
        {
            private readonly string harmony;
            private AssemblyDefinition harmonyAssembly;

            public ReferenceResolver(string managed, string harmony)
            {
                this.harmony = harmony;
                foreach (var directory in GetSearchDirectories()) RemoveSearchDirectory(directory);
                AddSearchDirectory(managed);
            }

            public override AssemblyDefinition Resolve(AssemblyNameReference name)
                => name.Name == HarmonyAssembly ? harmonyAssembly ??= AssemblyDefinition.ReadAssembly(harmony, new ReaderParameters { AssemblyResolver = this }) : base.Resolve(name);

            protected override void Dispose(bool disposing)
            {
                harmonyAssembly?.Dispose();
                base.Dispose(disposing);
            }
        }
    }

    public sealed class HarmonyCopy
    {
        /// <summary>umm, game or file.</summary>
        public string Source { get; set; }
        public string Path { get; set; }
        public string Version { get; set; }
        public string Sha256 { get; set; }
    }

    public sealed class HarmonyComparison
    {
        public HarmonyCopy Reference { get; set; }

        /// <summary>Assembly file name to the Harmony types and members it references that the reference copy lacks.</summary>
        public Dictionary<string, List<string>> Missing { get; set; } = new();
    }

    public sealed class HarmonyReport
    {
        /// <summary>The copy this run loaded.</summary>
        public HarmonyCopy Used { get; set; }
        public List<HarmonyComparison> References { get; set; } = new();
    }
}
