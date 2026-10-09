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

namespace WotR.OfflineTesting
{
    /// <summary>
    /// Starts the real game systems a mod depends on, in the order the game uses, then loads the mod through the
    /// entry method named in its Info.json. Each step is recorded; a failure stops the session with the stage name.
    /// </summary>
    public sealed class GameSession
    {
        public const string HarmonyId = "WotR.OfflineTesting";

        private Action<GameSession> verifyMod;

        public List<StageResult> Stages { get; } = new();

        /// <summary>UnityModManager log lines written by the mod (prefixed with its Id).</summary>
        public List<string> ModLog { get; private set; } = new();

        public string ModId { get; private set; }
        public string ModEntryPath { get; private set; }
        public Assembly ModAssembly { get; private set; }

        /// <param name="verifyModInitialized">
        /// Mod-specific check after the blueprint cache initialized, for example a log line the mod writes on success.
        /// Throw to fail; the failure is reported as [INIT_FAILED] mod-verification.
        /// </param>
        public void Start(Action<GameSession> verifyModInitialized = null)
        {
            verifyMod = verifyModInitialized;
            // Game types may only be touched after the runtime folder exists, so the remaining stages live in a separate method.
            Run("runtime", "Prepare rewritten runtime folder", OfflineRuntime.Prepare);
            StartGame();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void StartGame()
        {
            HarnessState.Inputs = OfflineRuntime.Inputs;
            HarnessState.PersistentDirectory = Path.Combine(OfflineRuntime.Inputs.WorkDirectory, "persistent", Process.GetCurrentProcess().Id.ToString());
            Run("harmony", "Apply declared environment boundaries", ApplyBoundaries);
            Run("application-paths", "Kingmaker.Utility.ApplicationPaths.Init (RuntimeInitializeOnLoadMethod)", Kingmaker.Utility.ApplicationPaths.Init);
            Run("type-cache", "StartGameLoader.PrepareTypeCache", () => new Kingmaker.Blueprints.JsonSystem.StartGameLoader().PrepareTypeCache());
            Run("asset-list", "Open blueprint.assets and install the referenced asset list", InstallAssetList);
            Run("unity-services", "Register audio and character atlas services without Unity objects", RegisterInertServices);
            Run("settings", "SettingsRoot.Initialize with SettingsValues read from blueprint.assets", InitializeSettings);
            Run("localization", "LocalizationManager.Init loads the real enGB pack (English selected in game settings)", LoadLocalization);
            Run("mod-load", "Mod entry method from Info.json through a UnityModManager ModEntry", LoadMod);
            Run("blueprints", "BlueprintsCache.Init (blueprints-pack.bbp) with the mod's patches", InitializeBlueprints);
            if (verifyMod != null) Run("mod-verification", "Mod-specific initialization check", () => verifyMod(this));
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

        private void LoadMod()
        {
            var info = JsonSerializer.Deserialize<UnityModManagerNet.UnityModManager.ModInfo>(
                File.ReadAllText(HarnessState.Inputs.ModInfo), new JsonSerializerOptions { IncludeFields = true });
            ModId = info.Id;
            ModEntryPath = Path.Combine(HarnessState.PersistentDirectory, "Mods", info.Id) + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(ModEntryPath);
            ModAssembly = Assembly.LoadFrom(Path.Combine(OfflineRuntime.RuntimeDirectory, Path.GetFileName(HarnessState.Inputs.ModAssembly)));

            // UnityModManager convention: "Namespace.Type.Method", a static method taking ModEntry and returning bool or void.
            var split = info.EntryMethod.LastIndexOf('.');
            var type = ModAssembly.GetType(info.EntryMethod.Substring(0, split), throwOnError: true);
            var method = type.GetMethod(info.EntryMethod.Substring(split + 1), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(UnityModManagerNet.UnityModManager.ModEntry) }, null)
                ?? throw new OfflineInitializationException("mod-load", $"Entry method {info.EntryMethod}(ModEntry) not found.");
            var entry = new UnityModManagerNet.UnityModManager.ModEntry(info, ModEntryPath);
            if (method.Invoke(null, new object[] { entry }) is false)
                throw new OfflineInitializationException("mod-load", $"{info.EntryMethod} returned false.");
        }

        private void InitializeBlueprints()
        {
            ResourcesLibrary.BlueprintsCache.Init();
            ModLog = ReadModLog(ModId);
        }

        private static List<string> ReadModLog(string modId)
        {
            var history = AccessTools.StaticFieldRefAccess<List<string>>(typeof(UnityModManagerNet.UnityModManager.Logger), "history");
            return history.Where(line => line.Contains($"[{modId}]")).ToList();
        }

    }

    /// <summary>
    /// Game-typed helpers. Kept out of <see cref="GameSession"/> because generic constraints load game assemblies
    /// when the declaring type loads, which must not happen before the runtime folder is prepared.
    /// </summary>
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

    public sealed class StageResult
    {
        public string Stage { get; set; }
        public string Description { get; set; }
        public bool Passed { get; set; }
        public long Milliseconds { get; set; }
        public string Error { get; set; }
    }
}
