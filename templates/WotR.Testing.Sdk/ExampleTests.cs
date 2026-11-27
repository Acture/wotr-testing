using System;
using Kingmaker.Blueprints.Root;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using WotR.Testing.Offline;
using Xunit;

namespace MyMod.OfflineTests
{
    /// <summary>
    /// The minimal example. The real game starts, the mod is loaded and the game's own rules are asserted; replace
    /// the examples with the mod's own mechanics and keep the fixture and the collection.
    /// </summary>
    [Collection(MyModGameCollection.Name)]
    public sealed class ExampleTests
    {
        // A vanilla pregen, MC_Human_M_Cavalier_Base. Its class levels come from the game's own AddClassLevels.
        private const string VanillaUnit = "5def1061f2e84909a1235ac8bb42578f";

        private readonly MyModGame game;

        public ExampleTests(MyModGame game) => this.game = game;

        /// <summary>The session started and the mod under test is loaded.</summary>
        [Fact]
        public void TheModIsLoaded()
        {
            game.RequireGame();
            Assert.False(string.IsNullOrEmpty(game.Session.ModId), "No mod was loaded.");
        }

        /// <summary>
        /// The vanilla blueprint pack and the game's own stat system work: the unit is built by the game's own
        /// UnitEntityData constructor and its stats are read through the game's own rules.
        /// </summary>
        [Fact]
        public void TheVanillaGameRulesApply()
        {
            game.RequireGame();
            var unit = OfflineGame.CreateUnit(VanillaUnit);
            var stats = OfflineGame.StatSnapshot(unit);
            Assert.NotEmpty(stats);
            Assert.True(stats.ContainsKey(StatType.Strength), "The unit has no Strength stat.");
        }

        /// <summary>
        /// A time-dependent effect. The offline session has no frame loop: nothing advances the game's clock between
        /// two statements of a test, so the assertion is about the duration the game computed when it applied the
        /// effect, not about a state a later frame would produce.
        /// </summary>
        [SkippableFact]
        public void ABuffKeepsTheDurationTheGameComputed()
        {
            game.RequireGame();
            var unit = OfflineGame.CreateUnit(VanillaUnit);
            var buff = BlueprintRoot.Instance.SystemMechanics.DirtyTrickSickenedBuff;
            try
            {
                var applied = unit.AddBuff(buff, unit, TimeSpan.FromSeconds(6), null);
                Assert.Equal(6, applied.TimeLeft.TotalSeconds);
            }
            catch (Exception error) when (UnityNative.IsUnavailable(error))
            {
                // The path reached Unity engine code: report it as skipped, never as passed.
                game.Observations["example.unityRuntimeRequired"] = error.ToString();
                Skip.If(true, UnityNative.SkipReason("the buff's effect", error));
            }
        }

        // The pattern for the mod's own mechanic: a blueprint of the mod, the game's own rules applied to a real unit
        // and the game's own numbers compared before and after. Copy this block and fill in the blanks.
        //
        // [Fact]
        // public void MyFeatureChangesTheGameNumbers()
        // {
        //     game.RequireGame();
        //     var feat = OfflineGame.Blueprint<BlueprintFeature>("<the feature blueprint guid>");   // needs "using Kingmaker.Blueprints.Classes;"
        //     var unit = OfflineGame.CreateUnit(VanillaUnit);
        //     var before = OfflineGame.StatSnapshot(unit);
        //     var fact = unit.Progression.Features.AddFeature(feat);
        //     Assert.True(fact.IsActive, "The feature was not added.");
        //     Assert.NotEqual(before, OfflineGame.StatSnapshot(unit));   // the effect changed a stat
        //     unit.Progression.Features.RemoveFact(fact);                 // the game's own removal API
        //     Assert.False(unit.Progression.Features.HasFact(feat));
        //     Assert.Equal(before, OfflineGame.StatSnapshot(unit));      // and the effect is gone again
        // }
    }
}
