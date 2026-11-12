using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Controllers.Projectiles;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using WotR.Testing.Offline;
using Xunit;

namespace WotR.Testing.BattleTests
{
    /// <summary>One against one with the game's AI on both sides, for builds and enemies whose actions reach more of the game.</summary>
    [Collection("battles")]
    public sealed class GameAiVariantTests
    {
        // CR2_SkeletalChampionArcher1: Mobs, Undead 2 / Fighter 6, longbow.
        private const string SkeletonArcher = "4d5fb0b57d1842b48ecb0107735a3cf2";
        // DLC3_BARBARIAN_TRAILER_LOW: Player faction, Barbarian 2, greataxe; its brain rages.
        private const string Barbarian = "93b94a70dcf64280887b0aaec91fcaea";
        private const string RageBuff = "da8ce41ac3cd74742b80984ccc3c9613";

        private readonly BattleGame game;

        public GameAiVariantTests(BattleGame game) => this.game = game;

        /// <summary>A unit of the party and an enemy about 5.6 m apart on the Kenabres mesh; no side is controlled.</summary>
        private static BattleScenario OneOnOne(string name, string hero, string foe)
        {
            var scenario = new BattleScenario(name) { MainCharacter = "hero", Area = Guids.KenabresBurning, StartTime = new TimeSpan(12, 0, 30) };
            scenario.Unit("hero", "party", hero).At(47.2f, 40, -65.2f);
            scenario.Unit("foe", "enemies", foe).WithFaction(Guids.MobsFaction).At(52, 40, -68);
            return scenario;
        }

        private static long Hits(string boundary) => Boundaries.Snapshot().Single(b => b.Id == boundary).Hits;

        private sealed class ProjectileRecorder : IProjectileHitHandler
        {
            public readonly List<(UnitEntityData Launcher, UnitEntityData Target, bool FromWeapon)> Hits = new();

            public void HandleProjectileHit(Projectile projectile)
                => Hits.Add((projectile.Launcher?.Unit, projectile.Target?.Unit, projectile.IsFromWeapon));
        }

        [Fact]
        public void AnArcherOfTheGameAiShootsArrowsThatReachTheirTargetWithoutTheirHitEffects()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(OneOnOne("archer", Guids.Fighter, SkeletonArcher), seed: 1);
            var archer = battle.Unit("foe");
            var fighter = battle.Unit("hero");
            var projectiles = new ProjectileRecorder();
            battle.Subscribe(projectiles);
            var effects = Hits("unit-hit-effects");
            var hitPoints = fighter.HPLeft;

            for (var second = 0; second < 30 && !projectiles.Hits.Any(h => h.Launcher == archer); second++)
                battle.Run(TimeSpan.FromSeconds(1));

            // The arrow flew and reached the fighter: Projectile.OnHit ran its rules after the skipped hit effects.
            Assert.Contains(projectiles.Hits, h => h.Launcher == archer && h.Target == fighter && h.FromWeapon);
            Assert.True(Hits("unit-hit-effects") > effects);
            Assert.True(fighter.HPLeft <= hitPoints);
        }

        // DLC3_PALADIN_TRAILER_LOW: Player faction, Paladin 2; its blueprint's brain (CultistWizard_Brain_Standard) has no
        // action a paladin can take. DLC3_ARCANIST_TRAILER_LOW: Sorcerer 2 who knows Magic Missile.
        private const string Paladin = "8e32f5f0db9c4ba6adad110aa76ff2e6";
        private const string Arcanist = "38d1cc56076c43499b447d3e4c81db2f";
        private const string MagicMissile = "4ac47ddb9fa1eaf43a1b6809980cfbd2";

        [Fact]
        public void AUnitGivenAnotherBrainActsOnItAsTheGamesChangeBrainMakesIt()
        {
            game.RequireGame();
            string characterBrain;
            using (var source = OfflineBattle.Start(OneOnOne("brain-source", Guids.Fighter, Guids.Cultist), seed: 1))
                characterBrain = source.Unit("hero").Brain.Blueprint.AssetGuid.ToString();

            // Commands the game's AI gave the paladin in the first seconds of the fight.
            int AiCommands(BattleScenario scenario)
            {
                using var battle = OfflineBattle.Start(scenario, seed: 1);
                var paladin = battle.Unit("hero");
                var commands = new WotR.Testing.BattleSupport.CommandRecorder();
                battle.Subscribe(commands);
                for (var second = 0; second < 15 && paladin.Descriptor.State.IsConscious && !commands.Acted.Any(c => c.Executor == paladin && c.ByAi); second++)
                    battle.Run(TimeSpan.FromSeconds(1));
                return commands.Acted.Count(c => c.Executor == paladin && c.ByAi);
            }

            Assert.Equal(0, AiCommands(OneOnOne("own-brain", Paladin, Guids.Cultist)));
            var swapped = OneOnOne("swapped-brain", Paladin, Guids.Cultist);
            swapped.Units.First(u => u.Id == "hero").WithBrain(characterBrain);
            Assert.True(AiCommands(swapped) > 0);
        }

        [Fact]
        public void AnAutoUseAbilityIsChosenAsThePlayerChoosesItOnTheActionBar()
        {
            game.RequireGame();
            var scenario = OneOnOne("auto-use", Arcanist, Guids.Cultist);
            scenario.Units.First(u => u.Id == "hero").WithAutoUseAbility(MagicMissile);
            using (var battle = OfflineBattle.Start(scenario, seed: 1))
                Assert.Equal(MagicMissile, battle.Unit("hero").Brain.AutoUseAbility?.Blueprint.AssetGuid.ToString());

            var fighter = OneOnOne("auto-use-without-slot", Guids.Fighter, Guids.Cultist);
            fighter.Units.First(u => u.Id == "hero").WithAutoUseAbility(MagicMissile);
            var error = Assert.Throws<BattleSetupException>(() => OfflineBattle.Start(fighter, seed: 1));
            Assert.Contains("has no action bar slot for MagicMissile", error.ToString());
        }

        // KenabresMansion_Dretch: a CR2 demon whose blueprint body puts a claw in each hand.
        private const string Dretch = "71be6e442f8afbb4bbf6a5af6ecf4e4c";

        [Fact]
        public void ADretchGetsTheBodyTheGameGivesItWithTheRefusedClawInItsInventory()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(OneOnOne("dretch", Guids.Fighter, Dretch), seed: 1);
            var dretch = battle.Unit("foe");

            // UnitBody.Initialize refuses the off-hand claw, logs it (a known game log) and puts the claw in the inventory.
            Assert.Equal("Claw1d4", dretch.Body.PrimaryHand.MaybeWeapon?.Blueprint.name);
            Assert.Null(dretch.Body.SecondaryHand.MaybeItem);
            Assert.Contains(dretch.Inventory, item => item.Blueprint.name == "Claw1d4");
            battle.Run(TimeSpan.FromSeconds(3));
        }

        // CR5_ZombieLordFighterLevel2: its blueprint's own prefab is in no bundle; it spawns as a variation of its preset.
        private const string ZombieLord = "6fb0daac7b491914999525b426369d4f";

        [Fact]
        public void AUnitWithACustomizationPresetSpawnsAsOneOfItsVariations()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(OneOnOne("variation", Guids.Fighter, ZombieLord), seed: 1);
            var zombie = battle.Unit("foe");
            var preset = zombie.Blueprint.CustomizationPreset;

            Assert.NotNull(preset);
            Assert.False(string.IsNullOrEmpty(zombie.Descriptor.CustomPrefabGuid));
            Assert.Contains(preset.UnitVariations.SelectMany(v => v.Variations), v => v.Prefab.AssetId == zombie.Descriptor.CustomPrefabGuid && v.Race == zombie.Progression.Race);
            battle.Run(TimeSpan.FromSeconds(3));
        }

        [Fact]
        public void AnEnchantedWeaponKeepsItsEnchantmentWithoutItsGlow()
        {
            game.RequireGame();
            var effects = Hits("fx");
            // DLC3_BARBARIAN_TRAILER_MID: Barbarian 8 with a +1 shock greataxe.
            using var battle = OfflineBattle.Start(OneOnOne("enchanted", "cc6a64ab9dad47d0868bd59d283ca7df", Guids.Cultist), seed: 1);
            var weapon = battle.Unit("hero").Body.PrimaryHand.MaybeWeapon;

            // ItemEnchantment.RespawnFx loads the glow from the asset bundles when the enchantment activates, while the battle
            // builds the unit; the battle skips the glow, and the game's enchantment stays on the weapon.
            Assert.NotNull(weapon);
            Assert.NotEmpty(weapon.Enchantments);
            Assert.True(Hits("fx") > effects);
            battle.Run(TimeSpan.FromSeconds(3));
        }

        [Fact]
        public void TheBattlesDifficultyIsTheGamesPresetWhichItsRulesReadAndCleanupRestoresTheSettings()
        {
            game.RequireGame();
            var settings = Kingmaker.Settings.SettingsRoot.Difficulty;
            var before = (settings.GameDifficulty.GetValue(), settings.DamageToParty.GetValue(), settings.LimitedAI.GetValue(), settings.AdditionalAIBehaviors.GetValue());
            var damages = new Dictionary<Kingmaker.Settings.GameDifficultyOption, int>();
            foreach (var level in new[] { Kingmaker.Settings.GameDifficultyOption.Story, Kingmaker.Settings.GameDifficultyOption.Unfair })
            {
                var scenario = OfflineBattleTests.Duel();
                scenario.Difficulty = level;
                scenario.LimitedAI = false;
                using var battle = OfflineBattle.Start(scenario, seed: 1);
                Assert.Equal(level, settings.GameDifficulty.GetValue());
                Assert.False(settings.LimitedAI.GetValue());
                // The cultist's fixed 20 points of damage reach the fighter through RuleDealDamage, which multiplies an
                // enemy's damage to the party by the preset's DamageToParty.
                var fighter = battle.Unit("fighter");
                var hitPoints = fighter.HPLeft;
                RuleScope.Run("cultist hits the fighter", () => Kingmaker.RuleSystem.Rulebook.Trigger(new Kingmaker.RuleSystem.Rules.Damage.RuleDealDamage(battle.Unit("cultist"), fighter,
                    new Kingmaker.RuleSystem.Rules.Damage.PhysicalDamage(new Kingmaker.RuleSystem.Rules.ModifiableDiceFormula(new Kingmaker.RuleSystem.DiceFormula(0, Kingmaker.RuleSystem.DiceType.Zero)), 20, Kingmaker.Enums.Damage.PhysicalDamageForm.Bludgeoning))));
                damages[level] = hitPoints - fighter.HPLeft;
                Assert.Equal((int)(20 * settings.DamageToParty.GetValue()), damages[level]);
            }
            Assert.True(damages[Kingmaker.Settings.GameDifficultyOption.Unfair] > damages[Kingmaker.Settings.GameDifficultyOption.Story],
                $"Story dealt {damages[Kingmaker.Settings.GameDifficultyOption.Story]}, Unfair {damages[Kingmaker.Settings.GameDifficultyOption.Unfair]}");
            Assert.Equal(before, (settings.GameDifficulty.GetValue(), settings.DamageToParty.GetValue(), settings.LimitedAI.GetValue(), settings.AdditionalAIBehaviors.GetValue()));
        }

        [Fact]
        public void ABarbarianOfTheGameAiRagesWithoutTheRagesVisualEffect()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(OneOnOne("rage", Barbarian, Guids.Cultist), seed: 1);
            var barbarian = battle.Unit("hero");
            var rage = OfflineGame.Blueprint<BlueprintBuff>(RageBuff);
            var effects = Hits("fx");

            for (var second = 0; second < 20 && barbarian.Buffs.GetBuff(rage) == null; second++)
                battle.Run(TimeSpan.FromSeconds(1));

            // The game's AI switched rage on; the buff's ContextActionSpawnFx loads a prefab from the asset bundles, which a
            // battle skips as when effects are disabled.
            Assert.NotNull(barbarian.Buffs.GetBuff(rage));
            Assert.True(Hits("fx") > effects);
        }
    }
}
