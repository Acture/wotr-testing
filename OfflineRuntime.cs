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

namespace WotR.OfflineTesting
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

        public static OfflineInputs Inputs { get; private set; }
        public static string RuntimeDirectory => runtimeDirectory;
        public static RewriteSummary Rewrite { get; private set; }

        /// <summary>The mod's own dependencies copied next to it, for example BlueprintCore.dll.</summary>
        public static IReadOnlyList<string> ModDependencies { get; private set; } = Array.Empty<string>();

        /// <summary>Resolves game assemblies only after <see cref="Prepare"/> succeeded.</summary>
        [ModuleInitializer]
        internal static void InstallResolver()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                var directory = runtimeDirectory;
                if (directory == null) return null;
                var path = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
        }

        public static void Prepare()
        {
            lock (Gate)
            {
                if (runtimeDirectory != null) return;
                Inputs = OfflineInputs.Load();
                var managed = Inputs.Managed;
                var umm = Path.Combine(managed, "UnityModManager");
                foreach (var required in new[] { Inputs.ModAssembly, Inputs.ModInfo, Path.Combine(umm, "UnityModManager.dll"), Path.Combine(umm, "0Harmony.dll") })
                {
                    if (!File.Exists(required)) throw new OfflineEnvironmentMissingException($"Required input not found: {required}");
                }

                // Private mod dependencies: DLLs next to the mod that are not game or UnityModManager assemblies.
                var gameNames = new HashSet<string>(Directory.GetFiles(managed, "*.dll").Concat(Directory.GetFiles(umm, "*.dll")).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
                var modAssembly = Path.GetFullPath(Inputs.ModAssembly);
                var modDirectory = Path.GetDirectoryName(modAssembly);
                var dependencies = Directory.GetFiles(modDirectory, "*.dll")
                    .Where(p => !gameNames.Contains(Path.GetFileName(p)) && !string.Equals(Path.GetFullPath(p), modAssembly, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Assemblies compiled against a publicized Assembly-CSharp (the mod or libraries like BlueprintCore)
                // decide which game members must become public, so they are part of the cache key.
                var consumers = new[] { Inputs.ModAssembly }.Concat(dependencies).Where(RuntimeAssemblyRewriter.ReferencesGame).ToArray();
                var key = Hash(string.Join("|", new[] { RewriterVersion, Inputs.Identity.AssemblyCSharpSha256 }
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
                }
                Rewrite = JsonSerializer.Deserialize<RewriteSummary>(File.ReadAllText(summaryPath), Report.JsonOptions);

                // The mod and its dependencies come from this build, next to the rewritten game so they bind to it.
                File.Copy(Inputs.ModAssembly, Path.Combine(directory, Path.GetFileName(Inputs.ModAssembly)), true);
                foreach (var dependency in dependencies) File.Copy(dependency, Path.Combine(directory, Path.GetFileName(dependency)), true);
                ModDependencies = dependencies.Select(Path.GetFileName).ToList();
                runtimeDirectory = directory;
            }
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

    /// <summary>Inputs written at build time by WotR.OfflineTesting.targets (wotr-offline-inputs.json).</summary>
    public sealed class OfflineInputs
    {
        public const string InputRootVariable = "WOTR_INPUT_ROOT";
        public const string ReportDirectoryVariable = "WOTR_OFFLINE_REPORT_DIR";
        public const string AllowUnverifiedVersionVariable = "WOTR_ALLOW_UNVERIFIED_VERSION";

        public string InputRoot { get; private set; }
        public string ModAssembly { get; private set; }
        public string ModInfo => Path.Combine(Path.GetDirectoryName(ModAssembly), "Info.json");
        public string WorkDirectory { get; private set; }
        public string ReportDirectory { get; private set; }
        public string Source { get; private set; }
        public string Managed => Path.Combine(InputRoot, "Wrath_Data", "Managed");
        public string StreamingAssets => Path.Combine(InputRoot, "Wrath_Data", "StreamingAssets");
        public string Bundles => Path.Combine(InputRoot, "Bundles");
        public InputIdentity Identity { get; private set; }

        public static OfflineInputs Load()
        {
            var configPath = Path.Combine(Path.GetDirectoryName(typeof(OfflineInputs).Assembly.Location), "wotr-offline-inputs.json");
            if (!File.Exists(configPath)) throw new OfflineEnvironmentMissingException($"Build-time input file missing: {configPath}. Import WotR.OfflineTesting.targets.");
            var config = JsonNode.Parse(File.ReadAllText(configPath));
            var inputs = new OfflineInputs
            {
                InputRoot = Environment.GetEnvironmentVariable(InputRootVariable) is { Length: > 0 } overridden ? overridden : (string)config["inputRoot"],
                ModAssembly = (string)config["modAssembly"],
                WorkDirectory = (string)config["workDirectory"],
                ReportDirectory = Environment.GetEnvironmentVariable(ReportDirectoryVariable) is { Length: > 0 } reports ? reports : (string)config["reportDirectory"],
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
