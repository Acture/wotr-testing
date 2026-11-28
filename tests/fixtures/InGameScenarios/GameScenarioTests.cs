using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Classes;
using Kingmaker.Blueprints.Classes.Prerequisites;
using Kingmaker.Designers.Mechanics.Facts;
using UnityEngine;
using WotR.Testing.InGame;
using Xunit;

namespace InGameScenarios;

/// <summary>Settings from the integration tests; a file, because the game does not see their environment.</summary>
public static class Scenario
{
    private static string Text()
    {
        var file = Path.Combine(Path.GetDirectoryName(typeof(Scenario).Assembly.Location), "wotr-test-scenario.json");
        return File.Exists(file) ? File.ReadAllText(file) : "";
    }

    public static string Failure => Value("scenario");

    /// <summary>The local save to load: a save name, or "latest".</summary>
    public static string Save => Value("save");

    private static string Value(string key)
    {
        var match = Regex.Match(Text(), "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }
}

[CollectionDefinition("In-game scenarios")]
public sealed class ScenarioCollection : ICollectionFixture<InGameFixture> { }

[Trait("WotrMode", "ingame")]
[Collection("In-game scenarios")]
public sealed class GameScenarioTests
{
    // Power Attack and the standard wolf from the game's own blueprints.
    private const string PowerAttack = "9972f33f977fc724c838e59641b2fca5";
    private const string Wolf = "ea610d9e540af4243b1310a3e6833d9f";
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(5);

    private readonly InGameFixture game;
    public GameScenarioTests(InGameFixture game) => this.game = game;

    [Fact]
    public Task Blueprint_icon_is_a_native_sprite() => game.OnMainThread(context => {
        var icon = context.Blueprint<BlueprintFeature>(PowerAttack).Icon;
        Assert.True(icon != null, "Power Attack has no icon.");
        Assert.True(icon.texture != null, "The icon has no native texture.");
        Assert.True(icon.texture.width > 0 && icon.texture.height > 0);
        return Task.CompletedTask;
    });

    [Fact]
    public async Task Spawned_unit_has_a_live_view_and_is_destroyed_afterwards()
    {
        Kingmaker.EntitySystem.Entities.UnitEntityData spawned = null;
        GameObject view = null;
        await game.OnMainThread(async context => {
            await LoadScenarioSave(context);
            spawned = context.SpawnUnit(context.Blueprint<BlueprintUnit>(Wolf), new Vector3(3, 0, 3));
            await context.WaitUntil(() => spawned.View != null && spawned.View.gameObject.activeInHierarchy, TimeSpan.FromSeconds(30));
            view = spawned.View.gameObject;
            Assert.True(spawned.IsInGame);
        });
        await game.OnMainThread(context => {
            Assert.True(spawned.Destroyed, "The spawned unit still exists after its test.");
            Assert.True(view == null, "The spawned unit's view still exists after its test.");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task Prerequisite_is_evaluated_for_the_loaded_main_character() => game.OnMainThread(async context => {
        await LoadScenarioSave(context);
        context.RequirePlayer();
        var character = Game.Instance.Player.MainCharacter.Value.Descriptor;
        foreach (var prerequisite in context.Blueprint<BlueprintFeature>(PowerAttack).ComponentsArray.OfType<PrerequisiteStatValue>()) {
            var met = prerequisite.CheckUnit(character);
            // The game compares the permanent value unless a feature substitutes another stat for prerequisites.
            var replaced = character.Progression.Features.SelectFactComponents<ReplaceStatForPrerequisites>().Any(item => item.OldStat == prerequisite.Stat);
            if (!replaced) Assert.Equal(character.Stats.GetStat(prerequisite.Stat).PermanentValue >= prerequisite.Value, met);
        }
    });

    /// <summary>Each injected failure must fail with its own diagnosis: assertion, missing asset or dirty cleanup.</summary>
    [Fact]
    public Task Injected_failures_are_distinct() => game.OnMainThread(context => {
        switch (Scenario.Failure) {
            case "assert-failure": Assert.Equal(1, 2); break;
            case "missing-asset": context.Blueprint<BlueprintFeature>("00000000000000000000000000000000"); break;
            case "cleanup-failure": context.Defer(() => throw new InvalidOperationException("deliberate cleanup failure")); break;
        }
        return Task.CompletedTask;
    });

    private static async Task LoadScenarioSave(InGameContext context)
    {
        if (GameState.PlayerLoaded) return;
        if (Scenario.Save == null) throw new InGameScenarioException("No save configured; set \"save\" in wotr-test-scenario.json.");
        await context.LoadSave(Scenario.Save == "latest" ? null : Scenario.Save, LoadTimeout);
    }
}
