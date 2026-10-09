using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.Json;
using HarmonyLib;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// Starts the real game systems a mod depends on, in the order the game uses, then loads the mod through the
    /// entry method named in its Info.json. Each step is recorded; a failure stops the session with the stage name.
    /// </summary>
    public sealed class GameSession
    {
        public const string HarmonyId = "WotR.Testing.Offline";

        private Action<GameSession> verifyMod;

        public List<StageResult> Stages { get; } = new();

        /// <summary>UnityModManager log lines written by the mod under test (prefixed with its Id).</summary>
        public List<string> ModLog { get; private set; } = new();

        /// <summary>Every loaded mod in load order; the mod under test has <see cref="LoadedMod.UnderTest"/>.</summary>
        public List<LoadedMod> Mods { get; } = new();

        public string ModId => UnderTest?.Id;
        public string ModEntryPath => UnderTest?.EntryPath;
        public Assembly ModAssembly => UnderTest?.Assembly;

        private LoadedMod UnderTest => Mods.FirstOrDefault(m => m.UnderTest);

        /// <param name="verifyModInitialized">
        /// Mod-specific check after the blueprint cache initialized, for example a log line the mod writes on success.
        /// Throw to fail; the failure is reported as [INIT_FAILED] mod-verification.
        /// </param>
        public void Start(Action<GameSession> verifyModInitialized = null)
        {
            verifyMod = verifyModInitialized;
            // Usually already prepared by the resolver; running it as a stage records it and classifies its failure.
            Run("runtime", "Prepare rewritten runtime folder", OfflineRuntime.Prepare);
            StartGame();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void StartGame()
        {
            HarnessState.Inputs = OfflineRuntime.Inputs;
            HarnessState.PersistentDirectory = Path.Combine(OfflineRuntime.Inputs.WorkDirectory, "persistent", Process.GetCurrentProcess().Id.ToString());
            RemoveFinishedRunState(Path.GetDirectoryName(HarnessState.PersistentDirectory));
            Run("harmony", "Apply declared environment boundaries", ApplyBoundaries);
            Run("dlc", "Choose available DLCs from WotrDlc (all, none, local or a list)", () => OfflineDlc.Configure(HarnessState.Inputs));
            Run("application-paths", "Kingmaker.Utility.ApplicationPaths.Init (RuntimeInitializeOnLoadMethod)", Kingmaker.Utility.ApplicationPaths.Init);
            Run("type-cache", "StartGameLoader.PrepareTypeCache", () => new Kingmaker.Blueprints.JsonSystem.StartGameLoader().PrepareTypeCache());
            Run("asset-list", "Open blueprint.assets and install the referenced asset list", InstallAssetList);
            Run("unity-services", "Register audio and character atlas services without Unity objects", RegisterInertServices);
            Run("settings", "SettingsRoot.Initialize with SettingsValues read from blueprint.assets", InitializeSettings);
            Run("localization", "LocalizationManager.Init loads the real enGB pack (English selected in game settings)", LoadLocalization);
            Run("mod-load", "Entry methods from Info.json through UnityModManager ModEntries, in Requirements/LoadAfter order", LoadMods);
            Run("blueprints", "BlueprintsCache.Init (blueprints-pack.bbp) with the mod's patches", InitializeBlueprints);
            if (verifyMod != null) Run("mod-verification", "Mod-specific initialization check", () => verifyMod(this));
        }

        /// <summary>Per-run state (persistent data, mod folder copies) of processes that are no longer running.</summary>
        private static void RemoveFinishedRunState(string root)
        {
            if (!Directory.Exists(root)) return;
            foreach (var folder in Directory.GetDirectories(root))
            {
                if (!int.TryParse(Path.GetFileName(folder), out var id) || IsRunning(id)) continue;
                try { Directory.Delete(folder, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }

        private static bool IsRunning(int processId)
        {
            try { return !Process.GetProcessById(processId).HasExited; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private void Run(string stage, string description, Action action)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                action();
                Stages.Add(new StageResult { Stage = stage, Description = description, Passed = true, Milliseconds = timer.ElapsedMilliseconds });
            }
            catch (Exception error)
            {
                var root = error is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : error;
                Stages.Add(new StageResult { Stage = stage, Description = description, Passed = false, Milliseconds = timer.ElapsedMilliseconds, Error = root.ToString() });
                if (root is OfflineEnvironmentMissingException or OfflineInitializationException) throw root;
                throw new OfflineInitializationException(stage, root.Message, root);
            }
        }

        private static void ApplyBoundaries()
        {
            Boundaries.DeclareHarmonyBoundaries();
            new Harmony(HarmonyId).PatchAll(typeof(GameSession).Assembly);
        }

        private static void InstallAssetList()
        {
            HarnessState.Assets = UnityAssetMapper.Open(Path.Combine(HarnessState.Inputs.Bundles, "blueprint.assets"));
            // The real list is a ScriptableObject in a Unity bundle; its entries are served lazily by ReferencedAssetsPatch.
            Kingmaker.Blueprints.JsonSystem.Converters.UnityObjectConverter.AssetList =
                (Kingmaker.SharedTypes.BlueprintReferencedAssets)FormatterServices.GetUninitializedObject(typeof(Kingmaker.SharedTypes.BlueprintReferencedAssets));
        }

        private static void RegisterInertServices()
        {
            Boundaries.Declare("audio-service", "audio", "Kingmaker.Visual.Sound.SoundState service",
                "Registered without running its constructor, which creates a MusicPlayer GameObject.");
            Boundaries.Declare("character-atlas-service", "graphics", "Kingmaker.Visual.CharacterSystem.CharacterAtlasService",
                "Registered without running its constructor (texture atlas).");
            OfflineGame.RegisterUninitialized<Kingmaker.Visual.Sound.SoundState>();
            Boundaries.Hit("audio-service");
            OfflineGame.RegisterUninitialized<Kingmaker.Visual.CharacterSystem.CharacterAtlasService>();
            Boundaries.Hit("character-atlas-service");
        }

        private static void InitializeSettings()
        {
            Boundaries.Declare("settings-asset", "assets", "SettingsValues ScriptableObject from blueprint.assets",
                "Mapped from the real asset's type tree, including difficulty presets; only preset icons (Sprites) are null.");
            var values = (Kingmaker.Settings.SettingsValues)HarnessState.Assets.MapByScriptClass("SettingsValues", typeof(Kingmaker.Settings.SettingsValues));
            Boundaries.Hit("settings-asset");
            Kingmaker.Settings.SettingsRoot.Initialize(values);
        }

        private static void LoadLocalization()
        {
            // Test configuration, not an adapter: English is chosen in the game's own language setting, as a player would.
            // Without it, Init asks Unity for the operating system language.
            Kingmaker.Settings.SettingsRoot.Game.Main.Localization.SetValueAndConfirm(Kingmaker.Localization.Shared.LocaleExtensions.UILocale.enGB);
            Kingmaker.Localization.LocalizationManager.Init();
            Kingmaker.Localization.LocalizationManager.WaitForInit();
            var locale = Kingmaker.Localization.LocalizationManager.CurrentLocale;
            if (locale != Kingmaker.Localization.Shared.Locale.enGB || Kingmaker.Localization.LocalizationManager.CurrentPack?.Locale != locale)
                throw new OfflineInitializationException("localization", $"Expected the enGB pack, got locale {locale} and pack {Kingmaker.Localization.LocalizationManager.CurrentPack?.Locale}.");
            if (Kingmaker.Localization.LocalizationManager.CurrentPack == null)
                throw new OfflineInitializationException("localization", $"No localization pack loaded for {locale}.");
        }

        private void LoadMods()
        {
            var options = new JsonSerializerOptions { IncludeFields = true };
            var declared = HarnessState.Inputs.AllMods.Select(path => new LoadedMod
            {
                Info = JsonSerializer.Deserialize<UnityModManagerNet.UnityModManager.ModInfo>(File.ReadAllText(OfflineInputs.InfoPath(path)), options),
                SourcePath = path,
                UnderTest = path == HarnessState.Inputs.ModAssembly,
            }).ToList();
            foreach (var mod in ModLoadOrder.Sort(declared))
            {
                ModLoadOrder.CheckRequirements(mod, Mods);
                LoadMod(mod);
                Mods.Add(mod);
            }
        }

        /// <summary>Loads one mod as UnityModManager does: own ModEntry and assembly, entry method, then started and registered.</summary>
        private static void LoadMod(LoadedMod mod)
        {
            // ModEntry.Path is a per-run copy of the mod folder, so the mod reads its own Assets, Localization and
            // settings files and may write settings without touching the build output.
            mod.EntryPath = Path.Combine(HarnessState.PersistentDirectory, "Mods", mod.Id) + Path.DirectorySeparatorChar;
            CopyModFolder(Path.GetDirectoryName(Path.GetFullPath(mod.SourcePath)), mod.EntryPath);
            mod.Assembly = Assembly.LoadFrom(Path.Combine(OfflineRuntime.ModRuntimeDirectory, Path.GetFileName(mod.SourcePath)));

            // UnityModManager convention: "Namespace.Type.Method", a static method taking ModEntry and returning bool or void.
            var entryMethod = mod.Info.EntryMethod;
            var split = entryMethod?.LastIndexOf('.') ?? -1;
            if (split <= 0 || split == entryMethod.Length - 1)
                throw new OfflineInitializationException("mod-load", $"{mod.Id}: Info.json EntryMethod '{entryMethod}' is not Namespace.Type.Method.");
            var type = mod.Assembly.GetType(entryMethod.Substring(0, split), throwOnError: true);
            var method = type.GetMethod(entryMethod.Substring(split + 1), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(UnityModManagerNet.UnityModManager.ModEntry) }, null)
                ?? throw new OfflineInitializationException("mod-load", $"{mod.Id}: entry method {entryMethod}(ModEntry) not found.");
            var entry = new UnityModManagerNet.UnityModManager.ModEntry(mod.Info, mod.EntryPath);
            Traverse.Create(entry).Field("mAssembly").SetValue(mod.Assembly);
            entry.Enabled = true;
            if (method.Invoke(null, new object[] { entry }) is false)
                throw new OfflineInitializationException("mod-load", $"{mod.Id}: {entryMethod} returned false.");
            Traverse.Create(entry).Field("mStarted").SetValue(true);
            Traverse.Create(entry).Field("mActive").SetValue(true);
            UnityModManagerNet.UnityModManager.modEntries.Add(entry);
            mod.Entry = entry;
        }

        private static void CopyModFolder(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(destination + directory.Substring(source.Length + 1));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, destination + file.Substring(source.Length + 1), true);
        }

        private void InitializeBlueprints()
        {
            ModBlueprintObserver.Active = true;
            try
            {
                ResourcesLibrary.BlueprintsCache.Init();
            }
            finally
            {
                ModBlueprintObserver.Active = false;
            }
            foreach (var mod in Mods) mod.Log = ReadModLog(mod.Id);
            ModLog = UnderTest?.Log ?? new List<string>();
        }

        private static List<string> ReadModLog(string modId) => ModLogSink.For(modId);

    }

    /// <summary>Helpers for building test scenarios from vanilla blueprints.</summary>
    public static class OfflineGame
    {
        public static T Blueprint<T>(string guid) where T : BlueprintScriptableObject
            => ResourcesLibrary.TryGetBlueprint<T>(guid)
               ?? throw new OfflineInitializationException("blueprint-lookup", $"{typeof(T).Name} {guid} was not found.");

        /// <summary>Creates a unit from a real vanilla blueprint through the game's view-less constructor.</summary>
        public static UnitEntityData CreateUnit(string blueprintGuid)
            => new(Guid.NewGuid().ToString("N"), isInGame: false, Blueprint<BlueprintUnit>(blueprintGuid));

        /// <summary>ModifiedValue of every stat the unit has, for before/after comparisons.</summary>
        public static Dictionary<Kingmaker.EntitySystem.Stats.StatType, int> StatSnapshot(UnitEntityData unit)
            => Enum.GetValues(typeof(Kingmaker.EntitySystem.Stats.StatType)).Cast<Kingmaker.EntitySystem.Stats.StatType>().Distinct()
                .Select(stat => (stat, value: unit.Stats.GetStat(stat)))
                .Where(x => x.value != null)
                .ToDictionary(x => x.stat, x => x.value.ModifiedValue);

        internal static void RegisterUninitialized<T>() where T : class, Owlcat.Runtime.Core.Utils.Locator.IService
        {
            if (Owlcat.Runtime.Core.Utils.Locator.Services.GetInstance<T>() == null)
                Owlcat.Runtime.Core.Utils.Locator.Services.RegisterServiceInstance((T)FormatterServices.GetUninitializedObject(typeof(T)));
        }
    }

    /// <summary>A mod loaded into the session, as UnityModManager would hold it.</summary>
    public sealed class LoadedMod
    {
        // UnityModManager objects are kept in object fields: Unity Mono resolves field types when it loads this type, and
        // when runtime preparation fails UnityModManager cannot load, which must not hide the classified failure.
        private object info;
        private object entry;

        public string Id => Info.Id;
        public string Version => Info.Version;
        public bool UnderTest { get; set; }
        public string SourcePath { get; set; }
        public string EntryPath { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public UnityModManagerNet.UnityModManager.ModInfo Info { get => (UnityModManagerNet.UnityModManager.ModInfo)info; set => info = value; }
        [System.Text.Json.Serialization.JsonIgnore] public Assembly Assembly { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public UnityModManagerNet.UnityModManager.ModEntry Entry { get => (UnityModManagerNet.UnityModManager.ModEntry)entry; set => entry = value; }
        /// <summary>Every line the mod logged; the report keeps a summary and writes the full log to logs/.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public List<string> Log { get; set; } = new();
    }

    /// <summary>UnityModManager's load rules: Requirements ("Id" or "Id-MinVersion") and LoadAfter decide the order.</summary>
    internal static class ModLoadOrder
    {
        public static List<LoadedMod> Sort(List<LoadedMod> declared)
            => Sort(declared, m => m.Id, m => m.Info.Requirements, m => m.Info.LoadAfter);

        /// <summary>Works on any mod description, so runtime preparation can order mods before game types load.</summary>
        public static List<T> Sort<T>(List<T> declared, Func<T, string> id, Func<T, string[]> requirements, Func<T, string[]> loadAfter)
        {
            var duplicate = declared.GroupBy(id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
                throw new OfflineEnvironmentMissingException($"Mod Id '{duplicate.Key}' is declared {duplicate.Count()} times; declare each mod once.");
            var ids = new HashSet<string>(declared.Select(id));
            var before = declared.ToDictionary(id, m => new HashSet<string>((requirements(m) ?? Array.Empty<string>())
                .Select(r => Parse(r, ids).id).Concat(loadAfter(m) ?? Array.Empty<string>()).Where(ids.Contains)));
            var result = new List<T>();
            while (result.Count < declared.Count)
            {
                // Declaration order breaks ties; the mod under test is declared last.
                var next = declared.FirstOrDefault(m => !result.Contains(m) && before[id(m)].All(required => result.Any(r => id(r) == required)));
                if (next == null)
                    throw new OfflineInitializationException("mod-load", "Mods have circular Requirements/LoadAfter: "
                        + string.Join(", ", declared.Where(m => !result.Contains(m)).Select(id)));
                result.Add(next);
            }
            return result;
        }

        public static void CheckRequirements(LoadedMod mod, IReadOnlyList<LoadedMod> loaded)
        {
            var ids = new HashSet<string>(loaded.Select(m => m.Id));
            foreach (var requirement in mod.Info.Requirements ?? Array.Empty<string>())
            {
                var (id, minimum) = Parse(requirement, ids);
                var dependency = loaded.FirstOrDefault(m => m.Id == id)
                    ?? throw new OfflineEnvironmentMissingException($"{mod.Id} requires {requirement}, which is not loaded. Declare it with WotrDependencyMod.");
                if (minimum != null && UnityModManagerNet.UnityModManager.ParseVersion(dependency.Version) < UnityModManagerNet.UnityModManager.ParseVersion(minimum))
                    throw new OfflineEnvironmentMissingException($"{mod.Id} requires {requirement}, but {id} {dependency.Version} is loaded.");
            }
        }

        /// <summary>Splits "Id-1.2.3" into Id and minimum version; a known Id that itself contains '-' is kept whole.</summary>
        private static (string id, string minimum) Parse(string requirement, ISet<string> knownIds)
        {
            if (knownIds.Contains(requirement)) return (requirement, null);
            var split = requirement.LastIndexOf('-');
            return split > 0 && split + 1 < requirement.Length && char.IsDigit(requirement[split + 1])
                ? (requirement.Substring(0, split), requirement.Substring(split + 1))
                : (requirement, null);
        }
    }

    public sealed class StageResult
    {
        public string Stage { get; set; }
        public string Description { get; set; }
        public bool Passed { get; set; }
        public long Milliseconds { get; set; }
        public string Error { get; set; }
    }
}
