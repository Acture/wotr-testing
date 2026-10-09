using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// Builds an isolated runtime folder from the real WotR inputs and makes it the only place game,
    /// Harmony and mod assemblies are loaded from. The test process never starts Wrath.exe or Unity.
    /// </summary>
    public static class OfflineRuntime
    {
        // Bump when RuntimeAssemblyRewriter changes, so cached runtime folders are rebuilt.
        private const string RewriterVersion = "2";

        private static readonly object Gate = new();
        private static string runtimeDirectory;

        /// <summary>
        /// True on the game's own Unity Mono runtime (started by wotr-mono-host), false on .NET Framework. Unity Mono
        /// does not check member access and runs type initializers as the game expects, so the .NET Framework-only
        /// adaptations (widening, initializer order) are not applied there.
        /// </summary>
        public static bool IsUnityMono { get; } = Type.GetType("Mono.Runtime") != null;

        /// <summary>"unity-mono" or "netfx".</summary>
        public static string RuntimeKind => IsUnityMono ? "unity-mono" : "netfx";

        public static OfflineInputs Inputs { get; private set; }
        public static string RuntimeDirectory => runtimeDirectory;

        /// <summary>
        /// This process's copies of the mods and their private libraries, under the runtime folder. Separate per process,
        /// so concurrent runs with different mod builds never overwrite or lock each other's files.
        /// </summary>
        public static string ModRuntimeDirectory { get; private set; }
        public static RewriteSummary Rewrite { get; private set; }

        /// <summary>Problems that do not stop the run but may differ from a player's game, for the report.</summary>
        public static IReadOnlyList<string> Warnings => warnings;
        private static readonly List<string> warnings = new();

        /// <summary>Private libraries copied next to the mods, for example BlueprintCore.dll.</summary>
        public static IReadOnlyList<string> ModDependencies { get; private set; } = Array.Empty<string>();

        /// <summary>The copy of each private library this run uses: file name, file version and source path.</summary>
        public static IReadOnlyList<ModLibrary> ModLibraries { get; private set; } = Array.Empty<ModLibrary>();

        private static int resolverInstalled;
        private static Exception prepareError;
        [ThreadStatic] private static bool resolving;

        /// <summary>
        /// Installs the resolver that loads game, Harmony and mod assemblies from the prepared runtime folder. The build
        /// targets call this from a module initializer generated into the test assembly, so game types may appear anywhere
        /// in test classes: the first request for a game assembly, even during test discovery, prepares the folder.
        /// </summary>
        [ModuleInitializer]
        public static void InstallResolver()
        {
            if (System.Threading.Interlocked.Exchange(ref resolverInstalled, 1) == 1) return;
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                var name = new AssemblyName(args.Name).Name;
                if (resolving || name.EndsWith(".resources", StringComparison.Ordinal)) return null;
                if (runtimeDirectory == null)
                {
                    if (prepareError != null || !IsInputAssembly(name)) return null;
                    resolving = true;
                    try
                    {
                        Prepare();
                    }
                    catch (Exception error)
                    {
                        // Reported, classified, by the fixture's runtime stage; discovery just cannot resolve the type.
                        prepareError = error;
                        return null;
                    }
                    finally
                    {
                        resolving = false;
                    }
                }
                foreach (var directory in new[] { runtimeDirectory, ModRuntimeDirectory })
                {
                    var path = Path.Combine(directory, name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
        }

        /// <summary>Cheap check against the build-time inputs, so unrelated assembly requests never trigger preparation.</summary>
        private static bool IsInputAssembly(string name)
        {
            try
            {
                var configPath = Path.Combine(Path.GetDirectoryName(typeof(OfflineRuntime).Assembly.Location), "wotr-offline-inputs.json");
                if (!File.Exists(configPath)) return false;
                var config = JsonNode.Parse(File.ReadAllText(configPath));
                var root = Environment.GetEnvironmentVariable(OfflineInputs.InputRootVariable) is { Length: > 0 } overridden ? overridden : (string)config["inputRoot"];
                var managed = Path.Combine(root, "Wrath_Data", "Managed");
                var file = name + ".dll";
                return File.Exists(Path.Combine(managed, file))
                    || File.Exists(Path.Combine(managed, "UnityModManager", file))
                    || OfflineInputs.ReadModAssemblies(config).Any(mod => File.Exists(Path.Combine(Path.GetDirectoryName(mod), file)));
            }
            catch (IOException)
            {
                return false;
            }
        }

        public static void Prepare()
        {
            lock (Gate)
            {
                if (runtimeDirectory != null) return;
                prepareError = null;
                Inputs = OfflineInputs.Load();
                var managed = Inputs.Managed;
                var umm = Path.Combine(managed, "UnityModManager");
                foreach (var required in Inputs.AllMods.SelectMany(mod => new[] { mod, OfflineInputs.InfoPath(mod) })
                    .Concat(new[] { Path.Combine(umm, "UnityModManager.dll"), Path.Combine(umm, "0Harmony.dll") }))
                {
                    if (!File.Exists(required)) throw new OfflineEnvironmentMissingException($"Required input not found: {required}");
                }

                // Private mod libraries: DLLs next to a mod that are neither game, UnityModManager nor mod assemblies.
                // All mods share one AppDomain, as in the game. When mods ship different copies of one library, the game
                // (Unity Mono) uses the first one loaded for every mod, whatever version each was built against; the run
                // does the same in load order and reports it.
                var gameNames = new HashSet<string>(Directory.GetFiles(managed, "*.dll").Concat(Directory.GetFiles(umm, "*.dll")).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
                var modAssemblies = Inputs.AllMods.Select(Path.GetFullPath).ToList();
                var sameName = modAssemblies.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                if (sameName != null)
                    throw new OfflineEnvironmentMissingException($"Mods share the assembly file name {sameName.Key}: {string.Join(", ", sameName)}. They cannot load side by side.");
                var modNames = new HashSet<string>(modAssemblies.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
                var dependencies = new List<string>();
                warnings.Clear();
                foreach (var library in LoadOrder(modAssemblies).SelectMany(mod => Directory.GetFiles(Path.GetDirectoryName(mod), "*.dll").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    .Where(p => !gameNames.Contains(Path.GetFileName(p)) && !modNames.Contains(Path.GetFileName(p))))
                {
                    var used = dependencies.FirstOrDefault(d => string.Equals(Path.GetFileName(d), Path.GetFileName(library), StringComparison.OrdinalIgnoreCase));
                    if (used == null) dependencies.Add(library);
                    else if (FileSha256(used) != FileSha256(library))
                        warnings.Add($"Mods ship different copies of {Path.GetFileName(library)}: {Describe(used)} is used, {Describe(library)} is not. "
                            + "In the game the copy loaded first is used by every mod, so the version depends on the player's mods and their load order.");
                }
                dependencies.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b)));

                // On .NET Framework, assemblies compiled against a publicized Assembly-CSharp (mods or libraries like
                // BlueprintCore) decide which game members must become public, so they are part of the cache key.
                var consumers = IsUnityMono ? Array.Empty<string>() : modAssemblies.Concat(dependencies).Where(RuntimeAssemblyRewriter.ReferencesGame).ToArray();
                var key = Hash(string.Join("|", new[] { RewriterVersion, RuntimeKind, Inputs.Identity.AssemblyCSharpSha256 }
                    .Concat(consumers.Select(FileSha256))
                    .Concat(Directory.GetFiles(managed, "*.dll").OrderBy(p => p, StringComparer.Ordinal).Select(p => $"{Path.GetFileName(p)}:{new FileInfo(p).Length}"))));
                var directory = Path.Combine(Inputs.WorkDirectory, "runtime", key.Substring(0, 16));
                var summaryPath = Path.Combine(directory, "rewrite-summary.json");
                if (!File.Exists(summaryPath))
                {
                    var staging = directory + ".tmp-" + Guid.NewGuid().ToString("N");
                    var summary = RuntimeAssemblyRewriter.Generate(managed, staging, consumers);
                    foreach (var file in Directory.GetFiles(umm, "*.dll")) File.Copy(file, Path.Combine(staging, Path.GetFileName(file)), true);
                    File.WriteAllText(Path.Combine(staging, "rewrite-summary.json"), JsonSerializer.Serialize(summary, Report.JsonOptions));
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                    Directory.Move(staging, directory);
                    RemoveOtherRuntimeFolders(directory);
                }
                Rewrite = JsonSerializer.Deserialize<RewriteSummary>(File.ReadAllText(summaryPath), Report.JsonOptions);

                // The mods and their libraries come from these builds, next to the rewritten game so they bind to it.
                var searchDirectories = new[] { managed, umm }.Concat(modAssemblies.Select(Path.GetDirectoryName)).Distinct().ToList();
                var process = System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
                MarkInUse(directory, process);
                var modDirectory = Path.Combine(directory, "mods", process);
                if (Directory.Exists(modDirectory)) Directory.Delete(modDirectory, true);
                Directory.CreateDirectory(modDirectory);
                foreach (var path in modAssemblies.Concat(dependencies))
                {
                    var destination = Path.Combine(modDirectory, Path.GetFileName(path));
                    if (IsUnityMono) File.Copy(path, destination, true);
                    else Rewrite.ModTypesWithPreciseInitialization += RuntimeAssemblyRewriter.CopyModAssembly(path, destination, searchDirectories);
                }
                ModDependencies = dependencies.Select(Path.GetFileName).ToList();
                ModLibraries = dependencies.Select(path => new ModLibrary
                {
                    Name = Path.GetFileName(path),
                    Version = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion,
                    Source = path,
                }).ToList();
                ModRuntimeDirectory = Path.GetFullPath(modDirectory);
                runtimeDirectory = Path.GetFullPath(directory);
            }
        }

        /// <summary>
        /// Each mod build or input change makes a new runtime folder; keep only the current one. Folders still in use by
        /// another process (or otherwise undeletable) are left alone. The summary file goes first, so a partly deleted
        /// folder is never reused: it lacks rewrite-summary.json and is regenerated.
        /// </summary>
        private static void RemoveOtherRuntimeFolders(string current)
        {
            foreach (var folder in Directory.GetDirectories(Path.GetDirectoryName(current)))
            {
                if (string.Equals(folder, current, StringComparison.OrdinalIgnoreCase) || InUse(folder)) continue;
                try
                {
                    var summary = Path.Combine(folder, "rewrite-summary.json");
                    if (File.Exists(summary)) File.Delete(summary);
                    Directory.Delete(folder, true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>Mod DLLs in UnityModManager load order, read from their Info.json without loading UnityModManager.</summary>
        private static List<string> LoadOrder(List<string> modAssemblies)
        {
            string[] Strings(JsonNode node) => node is JsonArray array ? array.Select(v => (string)v).ToArray() : Array.Empty<string>();
            var infos = modAssemblies.Select(path => (path, info: JsonNode.Parse(File.ReadAllText(OfflineInputs.InfoPath(path))))).ToList();
            return ModLoadOrder.Sort(infos, m => (string)m.info["Id"], m => Strings(m.info["Requirements"]), m => Strings(m.info["LoadAfter"]))
                .Select(m => m.path).ToList();
        }

        private static string Describe(string library)
        {
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(library).FileVersion;
            return $"{library} ({(string.IsNullOrEmpty(version) ? "no file version" : version)})";
        }

        /// <summary>
        /// Records that this process uses a runtime folder, and removes the marks and mod copies of finished processes,
        /// so folder cleanup never deletes files another running test process still needs.
        /// </summary>
        private static void MarkInUse(string directory, string process)
        {
            var marks = Path.Combine(directory, "in-use");
            Directory.CreateDirectory(marks);
            File.WriteAllText(Path.Combine(marks, process), "");
            foreach (var mark in Directory.GetFiles(marks).Where(m => !IsRunning(Path.GetFileName(m))))
            {
                try
                {
                    File.Delete(mark);
                    var mods = Path.Combine(directory, "mods", Path.GetFileName(mark));
                    if (Directory.Exists(mods)) Directory.Delete(mods, true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        private static bool InUse(string folder)
        {
            var marks = Path.Combine(folder, "in-use");
            return Directory.Exists(marks) && Directory.GetFiles(marks).Any(m => IsRunning(Path.GetFileName(m)));
        }

        private static bool IsRunning(string processId)
        {
            if (!int.TryParse(processId, out var id)) return false;
            try { return !System.Diagnostics.Process.GetProcessById(id).HasExited; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        public static string FileSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
        }

        private static string Hash(string text)
        {
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
        }
    }

    public sealed class ModLibrary
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string Source { get; set; }
    }

    /// <summary>Inputs written at build time by WotR.Testing.Offline.targets (wotr-offline-inputs.json).</summary>
    public sealed class OfflineInputs
    {
        public const string InputRootVariable = "WOTR_INPUT_ROOT";
        public const string ReportDirectoryVariable = "WOTR_OFFLINE_REPORT_DIR";
        public const string AllowUnverifiedVersionVariable = "WOTR_ALLOW_UNVERIFIED_VERSION";

        public string InputRoot { get; private set; }
        /// <summary>The mod under test.</summary>
        public string ModAssembly { get; private set; }
        public string ModInfo => InfoPath(ModAssembly);

        /// <summary>WotrDlc / WOTR_DLC: all (default), none, local or a comma-separated list of DLC names.</summary>
        public string Dlc { get; private set; }

        /// <summary>Other mods loaded with it (WotrDependencyMod), in declaration order.</summary>
        public IReadOnlyList<string> DependencyMods { get; private set; } = Array.Empty<string>();

        public IEnumerable<string> AllMods => DependencyMods.Append(ModAssembly);
        public string WorkDirectory { get; private set; }
        public string ReportDirectory { get; private set; }
        public string Source { get; private set; }
        public string Managed => Path.Combine(InputRoot, "Wrath_Data", "Managed");
        public string MonoRuntime => Path.Combine(InputRoot, "MonoBleedingEdge", "EmbedRuntime");
        public string StreamingAssets => Path.Combine(InputRoot, "Wrath_Data", "StreamingAssets");
        public string Bundles => Path.Combine(InputRoot, "Bundles");
        public InputIdentity Identity { get; private set; }

        public static OfflineInputs Load()
        {
            var configPath = Path.Combine(Path.GetDirectoryName(typeof(OfflineInputs).Assembly.Location), "wotr-offline-inputs.json");
            if (!File.Exists(configPath)) throw new OfflineEnvironmentMissingException($"Build-time input file missing: {configPath}. Import WotR.Testing.Offline.targets.");
            var config = JsonNode.Parse(File.ReadAllText(configPath));
            var inputs = new OfflineInputs
            {
                // Full, native-separator paths, so prefix checks against Assembly.Location and module paths match.
                InputRoot = Path.GetFullPath(Environment.GetEnvironmentVariable(InputRootVariable) is { Length: > 0 } overridden ? overridden : (string)config["inputRoot"]),
                ModAssembly = Path.GetFullPath((string)config["modAssembly"]),
                DependencyMods = ReadModAssemblies(config).Skip(1).Select(Path.GetFullPath).ToList(),
                WorkDirectory = Path.GetFullPath((string)config["workDirectory"]),
                Dlc = Environment.GetEnvironmentVariable(OfflineDlc.Variable) is { Length: > 0 } dlc ? dlc : (string)config["dlc"] ?? "all",
                ReportDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable(ReportDirectoryVariable) is { Length: > 0 } reports ? reports : (string)config["reportDirectory"]),
            };
            inputs.Source = File.Exists(Path.Combine(inputs.InputRoot, "manifest.json")) ? "snapshot" : "installation";
            foreach (var required in new[]
            {
                Path.Combine(inputs.Managed, "Assembly-CSharp.dll"),
                Path.Combine(inputs.Bundles, "blueprints-pack.bbp"),
                Path.Combine(inputs.Bundles, "blueprint.assets"),
                Path.Combine(inputs.StreamingAssets, "Version.info"),
                Path.Combine(inputs.StreamingAssets, "Localization", "enGB.json"),
            })
            {
                if (!File.Exists(required)) throw new OfflineEnvironmentMissingException($"Required WotR input not found: {required}");
            }
            inputs.Identity = InputIdentity.Read(inputs);
            if (!inputs.Identity.VerifiedVersion && Environment.GetEnvironmentVariable(AllowUnverifiedVersionVariable) != "1")
                throw new OfflineEnvironmentMissingException(
                    $"Game version '{inputs.Identity.GameVersion}' has not been verified (verified: {string.Join(", ", InputIdentity.VerifiedGameVersions)}). Set {AllowUnverifiedVersionVariable}=1 to run anyway.");
            return inputs;
        }

        public static string InfoPath(string modAssembly) => Path.Combine(Path.GetDirectoryName(modAssembly), "Info.json");

        /// <summary>The mod under test first, then its declared dependency mods.</summary>
        internal static IEnumerable<string> ReadModAssemblies(JsonNode config)
            => new[] { (string)config["modAssembly"] }
                .Concat(((string)config["dependencyMods"] ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()))
                .Where(p => p.Length > 0);
    }

    public sealed class InputIdentity
    {
        /// <summary>Game builds this library has been verified against (Version.info, last field).</summary>
        public static readonly string[] VerifiedGameVersions = { "2.7.0" };

        public string GameVersion { get; set; }
        public bool VerifiedVersion { get; set; }
        public string AssemblyCSharpSha256 { get; set; }
        public Dictionary<string, string> ResourceSha256 { get; set; }
        public string SnapshotManifestGameVersion { get; set; }

        public static InputIdentity Read(OfflineInputs inputs)
        {
            var version = File.ReadAllText(Path.Combine(inputs.StreamingAssets, "Version.info")).Trim();
            var identity = new InputIdentity
            {
                GameVersion = version,
                VerifiedVersion = VerifiedGameVersions.Contains(version.Split(' ').Last()),
                AssemblyCSharpSha256 = OfflineRuntime.FileSha256(Path.Combine(inputs.Managed, "Assembly-CSharp.dll")),
                ResourceSha256 = new[]
                {
                    "Bundles/blueprints-pack.bbp",
                    "Bundles/blueprint.assets",
                    "Wrath_Data/StreamingAssets/Localization/enGB.json",
                }.ToDictionary(p => p, p => OfflineRuntime.FileSha256(Path.Combine(inputs.InputRoot, p))),
            };

            // A snapshot must describe the same game build it contains; never mix versions.
            var manifestPath = Path.Combine(inputs.InputRoot, "manifest.json");
            if (File.Exists(manifestPath))
            {
                var manifest = JsonNode.Parse(File.ReadAllText(manifestPath));
                identity.SnapshotManifestGameVersion = (string)manifest["gameVersion"];
                if (identity.SnapshotManifestGameVersion != identity.GameVersion)
                    throw new OfflineEnvironmentMissingException($"Snapshot manifest version '{identity.SnapshotManifestGameVersion}' does not match Version.info '{identity.GameVersion}'.");
                foreach (var file in manifest["files"].AsObject())
                {
                    var path = Path.Combine(inputs.InputRoot, file.Key);
                    if (identity.ResourceSha256.TryGetValue(file.Key, out var hash) && hash != (string)file.Value)
                        throw new OfflineEnvironmentMissingException($"Snapshot file {file.Key} does not match its manifest hash.");
                    if (file.Key == "Wrath_Data/Managed/Assembly-CSharp.dll" && identity.AssemblyCSharpSha256 != (string)file.Value)
                        throw new OfflineEnvironmentMissingException("Snapshot Assembly-CSharp.dll does not match its manifest hash.");
                    if (!File.Exists(path)) throw new OfflineEnvironmentMissingException($"Snapshot file listed in manifest is missing: {file.Key}");
                }
            }
            return identity;
        }
    }
}

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
