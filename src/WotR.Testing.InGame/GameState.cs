using System;
using System.Linq;
using System.Threading.Tasks;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence;
using UnityEngine;

namespace WotR.Testing.InGame
{
    /// <summary>A required game state was not present; the test cannot run here, which is a failure, not a pass.</summary>
    public sealed class InGameScenarioException : Exception
    {
        public InGameScenarioException(string message) : base("[SCENARIO_REQUIRED] " + message) { }
    }

    /// <summary>A blueprint or other game asset the test asked for does not exist; never replaced by a substitute.</summary>
    public sealed class InGameAssetMissingException : Exception
    {
        public InGameAssetMissingException(string message) : base("[ASSET_MISSING] " + message) { }
    }

    /// <summary>The game's readiness, read on the main thread.</summary>
    public static class GameState
    {
        public static bool Loading => LoadingProcess.Instance != null && LoadingProcess.Instance.IsLoadingInProcess;

        /// <summary>An area is loaded and no loading screen is active.</summary>
        public static bool AreaLoaded => Game.HasInstance && !Loading && Game.Instance.CurrentlyLoadedArea != null && Game.Instance.LoadedAreaState != null;

        /// <summary>An area is loaded with a player party.</summary>
        public static bool PlayerLoaded => AreaLoaded && Game.Instance.Player?.MainCharacter.Value != null;
    }

    public sealed partial class InGameContext
    {
        public void RequireArea()
        {
            AssertMainThread();
            if (!GameState.AreaLoaded)
                throw new InGameScenarioException("This test needs a loaded area. Load a save first with LoadSave, or start the run with the game in an area.");
        }

        public void RequirePlayer()
        {
            RequireArea();
            if (!GameState.PlayerLoaded) throw new InGameScenarioException("This test needs a loaded player party.");
        }

        /// <summary>The blueprint with this GUID; a missing one fails the test rather than giving null.</summary>
        public T Blueprint<T>(string guid) where T : BlueprintScriptableObject
        {
            AssertMainThread();
            return ResourcesLibrary.TryGetBlueprint<T>(guid)
                ?? throw new InGameAssetMissingException("No " + typeof(T).Name + " with GUID " + guid + ".");
        }

        /// <summary>
        /// Loads a local save by name (or the latest save when <paramref name="name"/> is null) and waits until its area and
        /// party are loaded. Only on explicit request: tests in the current state never change the user's game this way.
        /// </summary>
        public async Task LoadSave(string name, TimeSpan timeout)
        {
            AssertMainThread();
            var saves = Game.Instance.SaveManager;
            saves.UpdateSaveListIfNeeded(true);
            var save = name == null ? saves.GetLatestSave() : saves.FirstOrDefault(item => item.Name == name || item.FileName == name);
            if (save == null) throw new InGameScenarioException(name == null ? "There is no local save to load." : "There is no local save named " + name + ".");
            if (GameState.AreaLoaded) Game.Instance.LoadGame(save);
            else {
                // The menu's own entry prepares the transition out of the main menu; loading around it breaks the scene load.
                var menu = UnityEngine.Object.FindObjectOfType<Kingmaker.MainMenu>()
                    ?? throw new InGameScenarioException("Neither an area nor the main menu is loaded; wait for the game to finish loading.");
                menu.LoadGame(save);
            }
            // The loading screen starts on a later frame; wait for it to begin before waiting for it to end.
            await WaitUntil(() => GameState.Loading || !GameState.AreaLoaded, TimeSpan.FromSeconds(30));
            await WaitUntil(() => GameState.PlayerLoaded, timeout);
        }

        /// <summary>Spawns a unit near the main character; it is destroyed after the test, which checks it is gone.</summary>
        public UnitEntityData SpawnUnit(BlueprintUnit blueprint, Vector3 offset)
        {
            if (blueprint == null) throw new ArgumentNullException(nameof(blueprint));
            RequirePlayer();
            var position = Game.Instance.Player.MainCharacter.Value.Position + offset;
            var unit = Game.Instance.EntityCreator.SpawnUnit(blueprint, position, Quaternion.identity, Game.Instance.LoadedAreaState.MainState, null)
                ?? throw new InvalidOperationException("The game did not spawn " + blueprint.name + ".");
            Defer(async () => {
                if (unit.Destroyed) return;
                unit.MarkForDestroy();
                await WaitUntil(() => unit.Destroyed, TimeSpan.FromSeconds(15));
            });
            return unit;
        }
    }
}
