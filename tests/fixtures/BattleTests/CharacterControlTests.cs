using System;
using System.Linq;
using Kingmaker;
using Kingmaker.UnitLogic.Commands;
using WotR.Testing.Offline;
using Xunit;

namespace WotR.Testing.BattleTests
{
    /// <summary>
    /// What a character exposes to the player besides its commands, given to the decision maker as switches: the stop,
    /// hold and stealth keys, the right click that chooses the auto-use ability, the weapon sets and their grip; and in
    /// real time, commands for a unit that is still busy.
    /// </summary>
    [Collection("battles")]
    public sealed class CharacterControlTests
    {
        // DLC3_ARCANIST_TRAILER_LOW: Player faction, Sorcerer 2 who knows Magic Missile, a light crossbow and nothing in
        // the off hand.
        private const string Arcanist = "38d1cc56076c43499b447d3e4c81db2f";

        // StandardHeavyMace: a simple one-handed weapon that can be held in two hands.
        private const string HeavyMace = "766689ccb57d9eb4f8fc88e2e85a919f";

        private readonly BattleGame game;

        public CharacterControlTests(BattleGame game) => this.game = game;

        /// <summary>The hero, the player's main character and controlled, about 5.6 m from a cultist the game's AI fights for.</summary>
        private static BattleScenario Duel(string name, string hero, bool turnBased = false)
        {
            var scenario = new BattleScenario(name)
            {
                MainCharacter = "hero",
                Area = Guids.KenabresBurning,
                TurnBased = turnBased,
                StartTime = new TimeSpan(12, 0, 30),
                ControlledSides = { "party" },
            };
            scenario.Unit("hero", "party", hero).At(47.2f, 40, -65.2f);
            scenario.Unit("cultist", "enemies", Guids.Cultist).At(52, 40, -68);
            return scenario;
        }

        [Fact]
        public void InRealTimeABusyUnitTakesCommandsAndTheStopHoldAndStealthKeysAsThePlayersDo()
        {
            game.RequireGame();
            using var environment = BattleEnvironment.Start(Duel("stop-and-hold", Guids.Fighter), seed: 2,
                new BattleEnvironmentOptions { DecisionInterval = TimeSpan.FromSeconds(0.5), MaxSteps = 100 });
            var observation = environment.Observe();
            // The party member's keys and buttons; the game's AI decides for the cultist, which is offered nothing.
            foreach (var button in new[] { BattleAction.Hold("hero"), BattleAction.Stop("hero"), BattleAction.Stealth("hero") })
                Assert.Contains(button, observation.Candidates);
            Assert.DoesNotContain(observation.Candidates, c => c.Unit == "cultist");

            // Half a second into its walk the hero is busy: not ready, but it takes a command, which replaces the walk.
            var path = observation.Paths.Single(p => p.Id == "game:hero->cultist");
            var result = environment.Step(BattleAction.Move("hero", path.Id));
            var hero = result.Observation.Unit("hero");
            Assert.False(hero.Ready, environment.Record.ToString());
            Assert.True(hero.Commandable);
            Assert.NotNull(hero.Command);
            Assert.Contains("hero", result.Observation.Commandable);
            Assert.Contains(BattleAction.Attack("hero", "cultist"), result.Observation.Candidates);
            Assert.Contains(BattleAction.Wait("hero"), result.Observation.Candidates);

            // The stop key drops the walk; the hero stays stopped until its next command.
            result = environment.Step(BattleAction.Stop("hero"));
            hero = result.Observation.Unit("hero");
            Assert.True(hero.Controls.Stop);
            Assert.Null(hero.Command);
            Assert.Contains("+ unit hero hold off stop on stealth out", result.Effects);
            result = environment.Step(BattleAction.Attack("hero", "cultist"));
            Assert.False(result.Observation.Unit("hero").Controls.Stop);

            // The hold key switches holding on and off, together with a command or on its own.
            result = environment.Step(BattleAction.Hold("hero"), BattleAction.Attack("hero", "cultist"));
            Assert.True(result.Observation.Unit("hero").Controls.Hold);
            result = environment.Step(BattleAction.Hold("hero"));
            Assert.False(result.Observation.Unit("hero").Controls.Hold);

            // The stealth button asks the game's stealth controller; in sight of an enemy the hero stays visible.
            result = environment.Step(BattleAction.Stealth("hero"));
            Assert.Equal(environment.Battle.Unit("hero").Descriptor.State.IsInStealth, result.Observation.Unit("hero").Controls.InStealth);
        }

        [Fact]
        public void InRealTimeAWeaponSetChangeInCombatTakesTheMoveActionAndHoldsTheHandsUntilItCan()
        {
            game.RequireGame();
            using var environment = BattleEnvironment.Start(Duel("weapon-sets", Guids.Fighter), seed: 1,
                new BattleEnvironmentOptions { DecisionInterval = TimeSpan.FromSeconds(0.2), MaxSteps = 200 });
            var observation = environment.Observe();
            var hero = observation.Unit("hero");
            Assert.Equal(0, hero.Controls.WeaponSet);
            Assert.Equal(4, hero.Controls.WeaponSets.Count);
            Assert.NotNull(hero.Controls.WeaponSets[0].Primary);
            Assert.Null(hero.Controls.WeaponSets[1].Primary);
            Assert.Contains(BattleAction.WeaponSet("hero", 1), observation.Candidates);
            Assert.DoesNotContain(BattleAction.WeaponSet("hero", 0), observation.Candidates);

            // They see each other at once and the fight starts; the hero acts in combat once its initiative comes.
            var unit = environment.Battle.Unit("hero");
            var result = environment.Step(BattleAction.Wait("hero"));
            Assert.True(result.Observation.Unit("hero").InCombat, environment.Record.ToString());
            for (var i = 0; i < 50 && !unit.CombatState.CanActInCombat; i++) result = environment.Step(BattleAction.Wait("hero"));
            Assert.True(unit.CombatState.CanActInCombat, environment.Record.ToString());
            Assert.Equal(0f, result.Observation.Unit("hero").MoveActionCooldown);

            // The first change takes the move action.
            result = environment.Step(BattleAction.WeaponSet("hero", 1));
            hero = result.Observation.Unit("hero");
            Assert.Equal(1, hero.Controls.WeaponSet);
            Assert.InRange(hero.MoveActionCooldown, 2.5f, 3f);
            Assert.Equal(0f, hero.StandardActionCooldown);
            Assert.Contains(result.Effects, line => line.StartsWith("+ unit hero weapon-set 1 ", StringComparison.Ordinal));

            // With the move action used, the next one takes the standard action.
            result = environment.Step(BattleAction.WeaponSet("hero", 0));
            hero = result.Observation.Unit("hero");
            Assert.Equal(0, hero.Controls.WeaponSet);
            Assert.InRange(hero.StandardActionCooldown, 2.5f, 3f);

            // With neither left the weapons change, but the action waits with the hands busy, and is taken once the move
            // action is back.
            result = environment.Step(BattleAction.WeaponSet("hero", 1));
            Assert.Equal(1, result.Observation.Unit("hero").Controls.WeaponSet);
            Assert.True(unit.AreHandsBusyWithAnimation);
            Assert.True(result.Observation.Unit("hero").MoveActionCooldown < 2.5f);
            for (var i = 0; i < 30 && unit.AreHandsBusyWithAnimation && !result.Ended; i++) result = environment.Step();
            Assert.False(unit.AreHandsBusyWithAnimation, environment.Record.ToString());
            Assert.True(result.Observation.Unit("hero").MoveActionCooldown > 2f);
        }

        [Fact]
        public void InTurnBasedCombatAWeaponSetChangeTakesTheMoveActionThenTheStandardAction()
        {
            game.RequireGame();
            using var environment = BattleEnvironment.Start(Duel("turn-weapon-sets", Guids.Fighter, turnBased: true), seed: 1,
                new BattleEnvironmentOptions { MaxSteps = 60 });
            var result = environment.Step(BattleAction.Wait("hero"));
            var unit = environment.Battle.Unit("hero");
            Assert.Same(unit, Game.Instance.TurnBasedCombatController.CurrentTurn?.Rider);
            Assert.Contains(BattleAction.WeaponSet("hero", 1), result.Observation.Candidates);

            // The change is a switch: the turn goes on and waits for the hero again.
            result = environment.Step(BattleAction.WeaponSet("hero", 1));
            Assert.Equal(new[] { "hero" }, result.Observation.Ready.ToArray());
            Assert.Equal(1, result.Observation.Unit("hero").Controls.WeaponSet);
            Assert.True(unit.UsedOneMoveAction());
            Assert.False(unit.UsedStandardAction());

            result = environment.Step(BattleAction.WeaponSet("hero", 0));
            Assert.Equal(0, result.Observation.Unit("hero").Controls.WeaponSet);
            Assert.True(unit.UsedStandardAction());
            // Neither action is left, so the weapon set bar takes no click; the turn can end.
            Assert.DoesNotContain(result.Observation.Candidates, c => c.Kind == BattleActionKind.WeaponSet);
            Assert.Contains(BattleAction.EndTurn("hero"), result.Observation.Candidates);
        }

        [Fact]
        public void ARightClickChoosesTheAutoUseAbilityThatAClickOnAnEnemyGives()
        {
            game.RequireGame();
            using var environment = BattleEnvironment.Start(Duel("auto-use", Arcanist), seed: 1, new BattleEnvironmentOptions { MaxSteps = 60 });
            var given = new GivenCommands();
            environment.Battle.Subscribe(given);
            var observation = environment.Observe();
            Assert.Null(observation.Unit("hero").Controls.AutoUseAbility);
            var autoUse = observation.Candidates.First(c => c.Kind == BattleActionKind.AutoUse && c.Ability.EndsWith(":MagicMissile", StringComparison.Ordinal));

            var result = environment.Step(autoUse);
            Assert.Equal("MagicMissile", result.Observation.Unit("hero").Controls.AutoUseAbility);
            Assert.Contains("+ unit hero auto-use MagicMissile", result.Effects);

            // A click on the enemy now casts it, as the game's click handler gives the auto-use ability.
            var hero = environment.Battle.Unit("hero");
            result = environment.Step(BattleAction.Attack("hero", "cultist"));
            Assert.Contains(given.Given, c => c.Executor == hero && c is UnitUseAbility use && use.Ability.Blueprint.name == "MagicMissile");

            // Right-clicking the entry again clears it.
            Assert.Contains(autoUse, result.Observation.Candidates);
            result = environment.Step(autoUse);
            Assert.Null(result.Observation.Unit("hero").Controls.AutoUseAbility);
        }

        [Fact]
        public void TheGripButtonTakesAOneHandedWeaponInTwoHandsOrOne()
        {
            game.RequireGame();
            var scenario = Duel("grip", Arcanist);
            scenario.Units.First(u => u.Id == "hero").Equip(BattleSlot.PrimaryHand, HeavyMace);
            using var environment = BattleEnvironment.Start(scenario, seed: 1, new BattleEnvironmentOptions { MaxSteps = 60 });
            var observation = environment.Observe();
            var set = observation.Unit("hero").Controls.WeaponSets[0];
            Assert.Equal("StandardHeavyMace", set.Primary);
            Assert.Null(set.Secondary);
            var other = set.Grip == "TwoHanded" ? "OneHanded" : "TwoHanded";
            Assert.Contains(BattleAction.Grip("hero"), observation.Candidates);

            var result = environment.Step(BattleAction.Grip("hero"));
            Assert.Equal(other, result.Observation.Unit("hero").Controls.WeaponSets[0].Grip);
            Assert.Contains("+ unit hero weapon-set 0 grip " + other, result.Effects);
            result = environment.Step(BattleAction.Grip("hero"));
            Assert.Equal(set.Grip, result.Observation.Unit("hero").Controls.WeaponSets[0].Grip);
        }

        [Fact]
        public void EachActionBarSlotShowsWhatTheGamesAbilityTooltipShows()
        {
            game.RequireGame();
            using (var environment = BattleEnvironment.Start(Duel("slots-spell", Arcanist), seed: 1, new BattleEnvironmentOptions { MaxSteps = 10 }))
            {
                var observation = environment.Observe();
                Assert.Empty(observation.Unit("cultist").Slots);
                var hero = observation.Unit("hero");
                var missile = hero.Slots.Single(s => s.Id.StartsWith("spell:", StringComparison.Ordinal) && s.Blueprint == "MagicMissile");
                Assert.Equal("Spell", missile.Type);
                Assert.Equal("Standard", missile.Action);
                Assert.False(missile.FullRound);
                Assert.Equal("Medium", missile.Range);
                Assert.Equal(new Kingmaker.Utility.Feet(40).Meters, missile.RangeMeters);
                Assert.True(missile.TargetsEnemies);
                Assert.Equal("Harmful", missile.EffectOnEnemy);
                Assert.Equal(1, missile.SpellLevel);
                Assert.Equal("Evocation", missile.School);
                Assert.Contains("Force", missile.Descriptors);
                Assert.Null(missile.SavingThrow);
                Assert.True(missile.SpellResistance);
                Assert.True(missile.ProvokesAttackOfOpportunity);
                Assert.NotNull(missile.DC);
                Assert.Equal(2, missile.CasterLevel);
                Assert.True(missile.Usable);
                // The uses the action bar shows are the sorcerer's casts of that level left.
                Assert.Equal(hero.SpellSlots.Single(s => s.Key.EndsWith(":1", StringComparison.Ordinal)).Value, missile.Uses);
                Assert.Contains(observation.Candidates, c => c.Kind == BattleActionKind.UseAbility && c.Ability == missile.Id);
            }

            using (var environment = BattleEnvironment.Start(Duel("slots-toggle", Guids.Fighter), seed: 1, new BattleEnvironmentOptions { MaxSteps = 10 }))
            {
                var fighter = environment.Observe().Unit("hero");
                var potion = fighter.Slots.First(s => s.Id.StartsWith("item:", StringComparison.Ordinal) && s.Id.Contains("PotionOfCure"));
                Assert.Contains("Cure", potion.Descriptors);
                // The blueprint's own value, not a guess: the potion's ability leaves EffectOnAlly unset.
                Assert.Equal("None", potion.EffectOnAlly);
                Assert.Equal(fighter.ItemUses[potion.Id], potion.Uses);
                var toggle = fighter.Slots.Single(s => s.Id == "toggle:FightDefensivelyToggleAbility");
                Assert.Equal("FightDefensivelyToggleAbility", toggle.Blueprint);
                Assert.False(toggle.On);
                var result = environment.Step(BattleAction.Toggle("hero", toggle.Id));
                Assert.True(result.Observation.Unit("hero").Slots.Single(s => s.Id == toggle.Id).On);
            }
        }
    }
}
