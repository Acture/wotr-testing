using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.Enums.Damage;
using Kingmaker.Items;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;
using Kingmaker.RuleSystem.Rules.Damage;
using WotR.Testing.Fuzzing;
using WotR.Testing.Offline;
using Xunit;

namespace WotR.Testing.RuleTests
{
    [CollectionDefinition("rules")]
    public sealed class RuleCollection : ICollectionFixture<RuleGame> { }

    public sealed class RuleGame : WotrGameFixture { }

    [Collection("rules")]
    public sealed class OfflineRuleTests
    {
        // MC_Human_M_Cavalier_Base, the vanilla human pregen used by the AttributeFeats offline tests.
        private const string UnitGuid = "5def1061f2e84909a1235ac8bb42578f";

        private readonly RuleGame game;

        public OfflineRuleTests(RuleGame game) => this.game = game;

        [Fact]
        public void SameSeedRepeatsAndRestoreContinuesTheSequence()
        {
            game.RequireGame();
            var first = Roll(4);
            var second = Roll(4);
            Assert.Equal(Text(first), Text(second));
            Assert.NotEqual(Text(first), Text(Roll(5)));

            GameRandom.Init(9);
            var saved = GameRandom.Save();
            var expected = UnityEngine.Random.Range(0, 1_000_000);
            UnityEngine.Random.Range(0, 1_000_000);
            GameRandom.Restore(saved);
            Assert.Equal(expected, UnityEngine.Random.Range(0, 1_000_000));

            GameRandom.Init(8);
            var state = UnityEngine.Random.state;
            var fromState = UnityEngine.Random.Range(0, 1_000_000);
            UnityEngine.Random.Range(0, 1_000_000);
            UnityEngine.Random.state = state;
            Assert.Equal(fromState, UnityEngine.Random.Range(0, 1_000_000));
        }

        [Fact]
        public void IntegerAndFloatRangesStayInsideTheOfflineContract()
        {
            game.RequireGame();
            GameRandom.Init(1);
            for (var i = 0; i < 40; i++) Assert.Equal(0, UnityEngine.Random.Range(0, 1));
            for (var i = 0; i < 40; i++) Assert.InRange(UnityEngine.Random.Range(1, 3), 1, 2);
            for (var i = 0; i < 40; i++)
            {
                var sample = UnityEngine.Random.Range(0f, 1f);
                Assert.True(sample >= 0f && sample < 1f, sample.ToString());
                var value = UnityEngine.Random.value;
                Assert.True(value >= 0f && value < 1f, value.ToString());
            }
        }

        [Fact]
        public void ActionSamplesAndFuzzDoNotConsumeGameRolls()
        {
            game.RequireGame();
            GameRandom.Init(2);
            var sample = new ActionSampleRandom(99);
            var expected = UnityEngine.Random.Range(0, 1_000_000);
            sample.Next(0, 1_000_000);
            GameRandom.Init(2);
            Assert.Equal(expected, UnityEngine.Random.Range(0, 1_000_000));

            GameRandom.Init(3);
            var beforeFuzz = UnityEngine.Random.Range(0, 1_000_000);
            GameRandom.Init(3);
            var callsBeforeFuzz = GameRandom.CallsSinceInit;
            game.Fuzz<IRoll>(() => new Roller(), new FuzzOptions { Cases = 2, MaxCalls = 2, Seed = 6 });
            Assert.Equal(callsBeforeFuzz, GameRandom.CallsSinceInit);
            Assert.Equal(beforeFuzz, UnityEngine.Random.Range(0, 1_000_000));
        }

        [Fact]
        public void RealRulesRepeatForOneSeedAndDifferForAnother()
        {
            game.RequireGame();
            var first = Settle(1);
            var second = Settle(1);
            Assert.Equal(first.Summary, second.Summary);
            Assert.Equal(Text(first.Calls), Text(second.Calls));
            Assert.NotEqual(Text(first.Calls), Text(Settle(2).Calls));
            Assert.True(first.HitPointsChanged);
            Assert.Equal(0, first.Unseeded);
        }

        [Fact]
        public void SwallowedLogErrorFailsAfterHitPointsChange()
        {
            game.RequireGame();
            var unit = OfflineGame.CreateUnit(UnitGuid);
            var before = unit.HPLeft;
            unit.Descriptor.Damage += 1;
            Assert.NotEqual(before, unit.HPLeft);
            var error = Assert.Throws<RuleSettlementException>(() => RuleScope.Run("swallowed", () =>
            {
                try { throw new InvalidOperationException("settlement blew up"); }
                catch (InvalidOperationException ex) { UnityEngine.Debug.LogException(ex); }
            }));
            Assert.Contains("swallowed", error.Message);
            Assert.Contains("settlement blew up", error.Message);
        }

        [Fact]
        public void BlankAllowanceIsRejected()
        {
            var error = Assert.Throws<ArgumentException>(() => RuleScope.Run("blank", () => { }, new RuleLogAllowance(" ", _ => true)));
            Assert.Contains("reason", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<GameRandomCall> Roll(int seed)
        {
            GameRandom.Init(seed);
            return GameRandom.Record(() =>
            {
                UnityEngine.Random.Range(1, 21);
                UnityEngine.Random.Range(0f, 1f);
                _ = UnityEngine.Random.value;
            });
        }

        private static Settlement Settle(int seed)
        {
            var outer = GameRandom.Save();
            try
            {
                GameRandom.Init(seed);
                var attacker = OfflineGame.CreateUnit(UnitGuid);
                var defender = OfflineGame.CreateUnit(UnitGuid);
                var weapon = attacker.Body.EmptyHandWeapon;
                Assert.NotNull(weapon);
                Assert.All(attacker.Body.CurrentEquipmentSlots, slot => Assert.NotSame(weapon, slot.MaybeItem));
                Settlement result = null;
                var calls = GameRandom.Record(() => result = Resolve(attacker, defender, weapon));
                result.Calls = calls;
                return result;
            }
            finally
            {
                GameRandom.Restore(outer);
            }
        }

        private static Settlement Resolve(UnitEntityData attacker, UnitEntityData defender, ItemEntityWeapon weapon)
        {
            RuleSkillCheck skillRule = null;
            var skill = RuleScope.Run("skill-check", () => skillRule = Rulebook.Trigger(new RuleSkillCheck(attacker, StatType.SkillPerception, 15)));
            RuleSavingThrow saveRule = null;
            var save = RuleScope.Run("saving-throw", () => saveRule = Rulebook.Trigger(new RuleSavingThrow(defender, SavingThrowType.Fortitude, 15)));
            RuleAttackRoll attackRule = null;
            var attack = RuleScope.Run("attack", () => attackRule = Rulebook.Trigger(new RuleAttackRoll(attacker, defender, weapon, 0)));
            var before = defender.HPLeft;
            RuleDealDamage damageRule = null;
            var damage = RuleScope.Run("damage", () =>
            {
                var dealt = new PhysicalDamage(new ModifiableDiceFormula(new DiceFormula(1, DiceType.D6)), 0, PhysicalDamageForm.Bludgeoning);
                damageRule = Rulebook.Trigger(new RuleDealDamage(attacker, defender, dealt));
            });
            Assert.InRange(skillRule.BaseRollResult, 1, 20);
            Assert.InRange(saveRule.BaseRollResult, 1, 20);
            Assert.InRange(attackRule.Roll, 1, 20);
            Assert.NotNull(damageRule);
            return new Settlement
            {
                Summary = skillRule.BaseRollResult + ":" + skillRule.Success
                    + "/" + saveRule.BaseRollResult + ":" + saveRule.Success
                    + "/" + attackRule.Roll + ":" + attackRule.IsHit
                    + "/" + (before - defender.HPLeft),
                HitPointsChanged = defender.HPLeft != before,
                Unseeded = skill.UnseededSystemRandom + save.UnseededSystemRandom + attack.UnseededSystemRandom + damage.UnseededSystemRandom,
            };
        }

        private static string Text(IReadOnlyList<GameRandomCall> calls) =>
            string.Join(";", calls.Select(call => call.Method + "(" + call.Arguments + ")=" + call.Result));

        public interface IRoll { void Roll(); }

        private sealed class Roller : IRoll
        {
            public void Roll() => UnityEngine.Random.Range(1, 7);
        }

        private sealed class Settlement
        {
            public string Summary;
            public IReadOnlyList<GameRandomCall> Calls;
            public bool HitPointsChanged;
            public long Unseeded;
        }
    }
}
