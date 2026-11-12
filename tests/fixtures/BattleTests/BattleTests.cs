using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Items;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.Enums.Damage;
using Kingmaker.PubSubSystem;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;
using Kingmaker.RuleSystem.Rules.Damage;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Buffs;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Commands.Base;
using WotR.Testing.BattleSupport;
using WotR.Testing.Offline;
using Xunit;

namespace WotR.Testing.BattleTests
{
    /// <summary>Every command the game gives a unit to run, with its AI action if the game's AI gave it.</summary>
    internal sealed class GivenCommands : IUnitRunCommandHandler
    {
        public readonly System.Collections.Generic.List<UnitCommand> Given = new();

        public void HandleUnitRunCommand(UnitCommand command) => Given.Add(command);
    }

    [CollectionDefinition("battles")]
    public sealed class BattleCollection : ICollectionFixture<BattleGame> { }

    public sealed class BattleGame : WotrGameFixture { }

    /// <summary>Vanilla 2.7.0 blueprints used by the battle tests (names as the game's blueprint pack has them).</summary>
    internal static class Guids
    {
        // PlayerFighterPregenMale: Player faction, AddClassLevels Fighter 5, masterwork longsword, light shield, chain shirt +1.
        public const string Fighter = "c41015dbcf456b942b472d626d9b26b0";
        // DH_Cultist_Deskari_MeleeFighter: Mobs, Fighter 3 / Cleric 1, masterwork scythe, breastplate, channel energy.
        public const string Cultist = "2428b6ad294beaa468f408d32d32cf63";
        // CR3_GuardWarriorTankLevel5: NeutralGuards, Warrior 5, longsword, chainmail.
        public const string Guard = "5eab8ef04a662b54e936f43f94da1805";
        // CR14_Prebuff_GlabrezuStandard: Mobs, a Huge demon with pincers.
        public const string Glabrezu = "70dbe81f5abca6b458931e697a6e0939";
        // The Glabrezu's view prefab: Bundles/<id>.unit in the installation.
        public const string GlabrezuView = "5a3c5e188506c0645b9f23eb5af1e7dc";
        // MC_Human_M_Cavalier_Base: a character-creation pregen with ClassLevelLimit 0.
        public const string ChargenPregen = "5def1061f2e84909a1235ac8bb42578f";
        public const string MobsFaction = "0f539babafb47fe4586b719d02aff7c4";
        public const string KenabresBurning = "92180b58582ec5f43a756071cd339f52";
        public const string FighterClass = "48ac8db94d5de7645906c7d0ad3bcfbd";
        public const string StandardLongsword = "6fd0a849531617844b195f452661b2cd";
        public const string StandardBastardSword = "07531989333442348b7d0102b24af236";
        public const string ChainShirtPlus1 = "91bf657f26eb80f4ba05b0b8440b1e8c";
        public const string Toughness = "d09b20029e9abfe4480b356c92095623";
        // BurningHands: a cone aimed at a point on the ground; no projectile and no lasting area.
        public const string BurningHands = "4783c3709a74a794dbe7c8e7e0b1b038";
        public const string CureLightWounds = "47808d23c67033d4bbab86a1070fd62f";
        public const string ShieldOfFaithBuff = "5274ddc289f4a7447b7ace68ad8bebb0";
        public const string BlessBuff = "87b8c6270ea85c743afc734dfe99afee";
        public const string BullsStrengthBuff = "b175001b42b1a02479881b72fe132116";
        public const string EnlargePersonBuff = "4f139d125bb602f48bfaec3d3e1937cb";
        // WildShapeIVBearBuff: a Polymorph into a Large bear with its own view prefab, bite and claws.
        public const string WildShapeBearBuff = "d5640d96888dd20489922045fde35059";
        public const string ChannelEnergyResource = "5e2bba3e07c37be42909a12945c27de7";
    }

    [Collection("battles")]
    public sealed class OfflineBattleTests
    {
        private readonly BattleGame game;

        public OfflineBattleTests(BattleGame game) => this.game = game;

        internal static BattleScenario Duel()
        {
            // The mechanics tests give every command themselves, so both sides are controlled and the game's AI decides
            // for nobody (TheGameAiFightsForUnitsOfUncontrolledSides runs it).
            var scenario = new BattleScenario("duel") { MainCharacter = "fighter", Area = Guids.KenabresBurning, ControlledSides = { "party", "enemies" } };
            scenario.Unit("fighter", "party", Guids.Fighter)
                .WithBaseStat(StatType.Strength, 16)
                .Equip(BattleSlot.PrimaryHand, Guids.StandardLongsword)
                .WithFact(Guids.Toughness)
                .At(50, 40, -70);
            scenario.Unit("cultist", "enemies", Guids.Cultist)
                .WithAbility(Guids.CureLightWounds)
                .WithBuff(Guids.ShieldOfFaithBuff, rounds: 10)
                .WithResource(Guids.ChannelEnergyResource, 1)
                .At(52, 40, -70);
            scenario.Unit("guard", "enemies", Guids.Guard)
                .WithFaction(Guids.MobsFaction)
                .WithClassLevels(Guids.FighterClass, 1)
                .At(52, 40, -68);
            scenario.Paths.Add(new BattlePathSpec("fighter-to-cultist", "consumer:battle-tests",
                new[] { new BattlePoint(50, 40, -70), new BattlePoint(51, 40, -69.5f), new BattlePoint(52, 40, -70) })
            {
                From = "fighter",
                To = "cultist",
                Distance = 2.236,
                Units = "meters",
            });
            return scenario;
        }

        [Fact]
        public void BuildsBothSidesFromRealBlueprintsAndSettlesARealWeaponAttack()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel(), seed: 3);
            var fighter = battle.Unit("fighter");
            var cultist = battle.Unit("cultist");
            var guard = battle.Unit("guard");

            Assert.True(fighter.Descriptor.IsPlayerFaction);
            Assert.Equal(5, fighter.Descriptor.Progression.CharacterLevel);
            Assert.Equal(Guids.StandardLongsword, fighter.Body.PrimaryHand.MaybeWeapon.Blueprint.AssetGuid.ToString());
            Assert.Equal(Guids.ChainShirtPlus1, fighter.Body.Armor.MaybeArmor.Blueprint.AssetGuid.ToString());
            Assert.NotNull(fighter.Body.SecondaryHand.MaybeShield);
            Assert.Equal(16, fighter.Stats.Strength.BaseValue);
            Assert.True(fighter.HasFact(OfflineGame.Blueprint<Kingmaker.Blueprints.Classes.BlueprintFeature>(Guids.Toughness)));

            Assert.False(cultist.Descriptor.IsPlayerFaction);
            Assert.NotNull(cultist.Body.PrimaryHand.MaybeWeapon);
            Assert.NotNull(cultist.Body.Armor.MaybeArmor);
            Assert.Equal(1, cultist.Descriptor.Resources.GetResourceAmount(OfflineGame.Blueprint<BlueprintAbilityResource>(Guids.ChannelEnergyResource)));
            Assert.NotNull(cultist.Buffs.GetBuff(OfflineGame.Blueprint<BlueprintBuff>(Guids.ShieldOfFaithBuff)));
            Assert.NotNull(cultist.Abilities.GetAbility(OfflineGame.Blueprint<Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility>(Guids.CureLightWounds)));

            Assert.Equal(Guids.MobsFaction, guard.Descriptor.Faction.AssetGuid.ToString());
            Assert.Equal(1, guard.Descriptor.Progression.GetClassLevel(OfflineGame.Blueprint<Kingmaker.Blueprints.Classes.BlueprintCharacterClass>(Guids.FighterClass)));
            Assert.Equal(6, guard.Descriptor.Progression.CharacterLevel);

            Assert.True(fighter.IsEnemy(cultist));
            Assert.True(cultist.IsEnemy(fighter));
            Assert.True(fighter.IsEnemy(guard));
            Assert.Equal(new UnityEngine.Vector3(52, 40, -70), cultist.Position);

            var before = cultist.HPLeft;
            RuleAttackWithWeapon attack = null;
            RuleScope.Run("fighter attacks cultist", () =>
                attack = Rulebook.Trigger(new RuleAttackWithWeapon(fighter, cultist, fighter.Body.PrimaryHand.Weapon, 0)));
            Assert.Same(fighter.Body.PrimaryHand.Weapon, attack.Weapon);
            Assert.InRange((int)attack.AttackRoll.D20, 1, 20);
            if (attack.AttackRoll.IsHit) Assert.True(cultist.HPLeft < before, "A hit with a longsword dealt no damage.");
            else Assert.Equal(before, cultist.HPLeft);
        }

        [Fact]
        public void DamageFromThePartyNeedsTheMainCharacterAndReportsItsAbsence()
        {
            game.RequireGame();
            var scenario = Duel();
            scenario.MainCharacter = null;
            using var battle = OfflineBattle.Start(scenario);
            var fighter = battle.Unit("fighter");
            var cultist = battle.Unit("cultist");
            var before = cultist.HPLeft;
            // RuleDealDamage asks GameHelper.IsAttackingGreenNPC, which reads the player character's group.
            var error = Assert.Throws<RuleSettlementException>(() => RuleScope.Run("damage without a main character", () =>
                Rulebook.Trigger(new RuleDealDamage(fighter, cultist, new PhysicalDamage(new ModifiableDiceFormula(new DiceFormula(1, DiceType.D6)), 0, PhysicalDamageForm.Slashing)))));
            Assert.Contains(error.Errors, log => log.Exception != null && log.Exception.Contains("IsAttackingGreenNPC"));
            Assert.Equal(before, cultist.HPLeft);
            Assert.Equal("main-character none", battle.InitialState.Lines.Single(line => line.StartsWith("main-character ")));

            battle.Dispose();
            using var withMain = OfflineBattle.Start(Duel());
            Assert.Same(withMain.Unit("fighter"), Kingmaker.Designers.GameHelper.GetPlayerCharacter());
            var target = withMain.Unit("cultist");
            var hitPoints = target.HPLeft;
            RuleScope.Run("damage with a main character", () =>
                Rulebook.Trigger(new RuleDealDamage(withMain.Unit("fighter"), target, new PhysicalDamage(new ModifiableDiceFormula(new DiceFormula(1, DiceType.D6)), 0, PhysicalDamageForm.Slashing))));
            Assert.True(target.HPLeft < hitPoints);
        }

        [Fact]
        public void ThreeResetsWithOneSeedRebuildTheSameStateWithoutEarlierEffects()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel(), seed: 11);
            var initial = battle.InitialState;
            var bless = OfflineGame.Blueprint<BlueprintBuff>(Guids.BlessBuff);
            var channel = OfflineGame.Blueprint<BlueprintAbilityResource>(Guids.ChannelEnergyResource);
            for (var round = 1; round <= 3; round++)
            {
                var fighter = battle.Unit("fighter");
                var cultist = battle.Unit("cultist");
                RuleScope.Run("round " + round, () =>
                {
                    fighter.AddBuff(bless, fighter, TimeSpan.FromMinutes(1));
                    cultist.Descriptor.Resources.Spend(channel, 1);
                    var damage = new PhysicalDamage(new ModifiableDiceFormula(new DiceFormula(2, DiceType.D6)), 0, PhysicalDamageForm.Slashing);
                    Rulebook.Trigger(new RuleDealDamage(fighter, cultist, damage));
                    Rulebook.Trigger(new RuleAttackWithWeapon(cultist, fighter, cultist.Body.PrimaryHand.Weapon, 0));
                });
                battle.Subscribe(new CountingBuffHandler());
                battle.AdvanceGameTime(TimeSpan.FromMinutes(5));
                Assert.NotEqual(initial, battle.Observe());

                battle.Reset();
                Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
                Assert.Equal(round + 1, battle.Generation);
                Assert.True(initial.Equals(battle.InitialState), string.Join(Environment.NewLine, initial.Difference(battle.InitialState)));
                Assert.NotSame(fighter, battle.Unit("fighter"));
                Assert.Null(battle.Unit("fighter").Buffs.GetBuff(bless));
                Assert.Equal(1, battle.Unit("cultist").Descriptor.Resources.GetResourceAmount(channel));
            }
        }

        [Fact]
        public void AnotherSeedChangesOnlyTheRandomStateAndTheVariationsTheGameSelects()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel(), seed: 1);
            var first = battle.InitialState;
            battle.Reset(seed: 2);
            Assert.Contains(battle.InitialState.Lines, line => line.StartsWith("game-random seed 2 calls ", StringComparison.Ordinal));
            // The guard's customization preset lists it, so it spawns as a variation the game selects at random, with the
            // variation's race and its racial stat bonuses; the fighter and the cultist roll nothing while they are built.
            Assert.All(first.Difference(battle.InitialState), line => Assert.True(line.Contains("game-random seed") || line.Contains("unit guard "), line));
            battle.Reset(seed: 1);
            Assert.Equal(first, battle.InitialState);
        }

        [Fact]
        public void FailureWhileAnEffectActivatesLeavesTheNextBuildClean()
        {
            game.RequireGame();
            BattleState initial;
            var bless = OfflineGame.Blueprint<BlueprintBuff>(Guids.BlessBuff);
            using (var battle = OfflineBattle.Start(Duel(), seed: 5))
            {
                initial = battle.InitialState;
                var fighter = battle.Unit("fighter");
                var handler = new ThrowingBuffHandler();
                battle.Subscribe(handler);
                // The game's EventBus catches the handler's exception and logs it; the buff stays applied.
                var error = Assert.Throws<RuleSettlementException>(() => RuleScope.Run("bless with a failing handler", () => fighter.AddBuff(bless, fighter, TimeSpan.FromMinutes(1))));
                Assert.Contains(ThrowingBuffHandler.Message, error.Message);
                Assert.True(handler.Calls > 0);
                Assert.NotNull(fighter.Buffs.GetBuff(bless));

                battle.Reset();
                Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
                Assert.True(initial.Equals(battle.InitialState), string.Join(Environment.NewLine, initial.Difference(battle.InitialState)));
                var calls = handler.Calls;
                RuleScope.Run("bless after reset", () => battle.Unit("fighter").AddBuff(bless, battle.Unit("fighter"), TimeSpan.FromMinutes(1)));
                Assert.Equal(calls, handler.Calls);
            }

            // An exception thrown out of the test body midway through an effect: Dispose still cleans up.
            var thrown = Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var battle = OfflineBattle.Start(Duel(), seed: 5);
                var fighter = battle.Unit("fighter");
                battle.Subscribe(new CountingBuffHandler());
                fighter.AddBuff(bless, fighter, TimeSpan.FromMinutes(1));
                UnityEngine.Random.Range(0, 100);
                battle.AdvanceGameTime(TimeSpan.FromHours(1));
                throw new InvalidOperationException("stopped in the middle of an effect");
            }));
            Assert.Equal("stopped in the middle of an effect", thrown.Message);
            Assert.False(BattleWorker.IsDirty, BattleWorker.DirtyReason);
            using (var next = OfflineBattle.Start(Duel(), seed: 5))
                Assert.True(initial.Equals(next.InitialState), string.Join(Environment.NewLine, initial.Difference(next.InitialState)));
        }

        [Fact]
        public void AnAttackCommandRunsThroughTheGameCommandLoop()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel(), seed: 9);
            var fighter = battle.Unit("fighter");
            var cultist = battle.Unit("cultist");
            var reach = UnitAttack.GetApproachRadius(fighter.GetFirstWeapon(), fighter, cultist);
            var hitPoints = cultist.HPLeft;
            var attack = new UnitAttack(cultist);
            fighter.Commands.Run(attack);

            // The cultist stands just outside melee reach: the fighter walks into reach frame by frame, then
            // UnitCommandController starts the command. With no animation (HasAnimation is false), UnitCommand.Tick acts
            // on its first tick, and the command ends there.
            var elapsed = battle.RunUntilIdle(TimeSpan.FromSeconds(12));
            Assert.True(fighter.Commands.Empty);
            Assert.True(attack.IsActed);
            Assert.Equal(UnitCommand.ResultType.Success, attack.Result);
            Assert.NotNull(attack.LastAttackRule);
            Assert.InRange(elapsed.TotalSeconds, 0.1, 1.0);
            Assert.True(Distance(fighter.Position, cultist.Position) <= reach);
            if (attack.LastAttackRule.AttackRoll.IsHit) Assert.True(cultist.HPLeft < hitPoints);
            else Assert.Equal(hitPoints, cultist.HPLeft);
        }

        [Fact]
        public void AnAttackOutOfReachApproachesTheTargetFirst()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel(), seed: 9);
            var initial = battle.InitialState;
            var fighter = battle.Unit("fighter");
            var guard = battle.Unit("guard");
            var reach = UnitAttack.GetApproachRadius(fighter.GetFirstWeapon(), fighter, guard);
            Assert.True(Distance(fighter.Position, guard.Position) > reach);
            var attack = new UnitAttack(guard);
            fighter.Commands.Run(attack);

            battle.RunUntilIdle(TimeSpan.FromSeconds(12));
            Assert.True(attack.IsActed);
            Assert.NotNull(attack.LastAttackRule);
            // The game's movement agent stops the fighter just inside the approach radius.
            Assert.InRange(Distance(fighter.Position, guard.Position), reach - 0.1f, reach);
            Assert.Contains("unit fighter position (50, 40, -70)", initial.Lines);
            Assert.DoesNotContain("unit fighter position (50, 40, -70)", battle.Observe().Lines);

            battle.Reset();
            Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
            Assert.True(initial.Equals(battle.InitialState), string.Join(Environment.NewLine, initial.Difference(battle.InitialState)));
        }

        [Fact]
        public void AMoveCommandEndsWithinItsApproachRadius()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel(), seed: 9);
            var cultist = battle.Unit("cultist");
            var target = new UnityEngine.Vector3(47.2f, 40f, -65.2f);
            var move = new UnitMoveTo(target, 0.5f);
            cultist.Commands.Run(move);

            var distance = Distance(cultist.Position, target);
            var speed = cultist.CurrentSpeedMps;

            // The game's UnitMovementAgent walks a path the game's A* found on the area's navigation mesh.
            var elapsed = battle.RunUntilIdle(TimeSpan.FromSeconds(12));
            Assert.True(cultist.Commands.Empty);
            Assert.Equal(UnitCommand.ResultType.Success, move.Result);
            Assert.True(Distance(cultist.Position, target) <= 0.5f);
            Assert.True(elapsed.TotalSeconds >= distance / speed, $"walked {distance} m at {speed} m/s in {elapsed.TotalSeconds} s");
        }

        [Fact]
        public void AMoveWhoseStraightLineIsBlockedWalksAroundTheObstacle()
        {
            game.RequireGame();
            var scenario = new BattleScenario("detour") { Area = Guids.KenabresBurning };
            scenario.Unit("walker", "enemies", Guids.Cultist).At(52, 40, -70);
            using var battle = OfflineBattle.Start(scenario, seed: 9);
            var walker = battle.Unit("walker");
            var start = walker.Position;

            // Outside turn-based combat the game's movement agent searches the first graph, the area's navigation mesh
            // (UnitMovementAgent.PathTo, graph mask 1). The target is the nearest point of the mesh, reachable from the
            // start, that the mesh's own linecast says cannot be reached in a straight line.
            var graph = AstarPath.active.data.recastGraph;
            Assert.Same(AstarPath.active.data.graphs[0], graph);
            var startNode = graph.GetNearest(start, Pathfinding.NNConstraint.Default).node;
            var nodes = new System.Collections.Generic.List<Pathfinding.GraphNode>();
            graph.GetNodes(node => { if (node.Walkable && node.Area == startNode.Area) nodes.Add(node); });
            var target = nodes
                .Select(node => (UnityEngine.Vector3)node.position)
                .Where(point => Math.Abs(point.y - start.y) < 1f && Distance(start, point) is >= 5f and <= 15f)
                .OrderBy(point => Distance(start, point)).ThenBy(point => point.x).ThenBy(point => point.z)
                .FirstOrDefault(point => graph.Linecast(start, point));
            Assert.NotEqual(default(UnityEngine.Vector3), target);
            var straight = Distance(start, target);

            var move = new UnitMoveTo(target, 0.3f);
            walker.Commands.Run(move);
            var walked = 0f;
            var previous = walker.Position;
            for (var step = 0; step < 300 && !walker.Commands.Empty; step++)
            {
                battle.Run(TimeSpan.FromSeconds(0.1));
                walked += Distance(previous, walker.Position);
                previous = walker.Position;
            }
            Assert.True(walker.Commands.Empty);
            Assert.Equal(UnitCommand.ResultType.Success, move.Result);
            Assert.True(Distance(walker.Position, target) <= 0.3f + 0.05f, $"stopped {Distance(walker.Position, target)} m from {target}");
            // In 2.7.0 the target lies 5.0 m away and the unit walks about 10.8 m around the obstacle.
            Assert.True(walked > straight * 1.5f, $"walked {walked} m to a point {straight} m away in a straight line");
        }

        [Fact]
        public void AFrameSearchesAndReturnsEveryQueuedPathHoweverSlowTheMachine()
        {
            game.RequireGame();
            var scenario = new BattleScenario("slow-machine") { Area = Guids.KenabresBurning };
            scenario.Unit("walker", "enemies", Guids.Cultist).At(52, 40, -70);
            using var battle = OfflineBattle.Start(scenario, seed: 9);
            var start = battle.Unit("walker").Position;
            var graph = AstarPath.active.data.recastGraph;
            var startNode = graph.GetNearest(start, Pathfinding.NNConstraint.Default).node;
            var reachable = new System.Collections.Generic.List<UnityEngine.Vector3>();
            graph.GetNodes(node => { if (node.Walkable && node.Area == startNode.Area) reachable.Add((UnityEngine.Vector3)node.position); });
            // The farthest reachable point, then three near ones.
            var targets = reachable.OrderByDescending(point => Distance(start, point)).ThenBy(point => point.x).ThenBy(point => point.z).Take(1)
                .Concat(reachable.Where(point => Distance(start, point) is >= 3f and <= 10f).OrderBy(point => point.x).ThenBy(point => point.z).Take(3))
                .ToList();
            Assert.Equal(4, targets.Count);

            // The game's A* stops searching for the frame after maxFrameTime (10 ms in this scene) on the computer's clock,
            // checked after each search and every 500 nodes within one, and hands paths back in 0.5 ms slices. Here every
            // search starts 25 ms late and every hand-back takes 1 ms, as on a slow or busy machine.
            var returned = new System.Collections.Generic.List<(Pathfinding.Path Path, Pathfinding.PathCompleteState State, int Searched)>();
            var searchedNodes = typeof(Pathfinding.Path).GetField("searchedNodes", BindingFlags.NonPublic | BindingFlags.Instance);
            var paths = targets
                .Select(target => Pathfinding.ABPath.Construct(start, target, path =>
                {
                    returned.Add((path, path.CompleteState, (int)searchedNodes.GetValue(path)));
                    Thread.Sleep(1);
                }))
                .ToList();
            using (new SlowPathSearches(TimeSpan.FromMilliseconds(25)))
            {
                foreach (var path in paths) AstarPath.StartPath(path);
                battle.Run(TimeSpan.FromMilliseconds(1));
            }

            // One frame searched and returned them all, in the order they were requested.
            Assert.Equal(paths.Cast<Pathfinding.Path>(), returned.Select(r => r.Path));
            Assert.All(returned, r => Assert.Equal(Pathfinding.PathCompleteState.Complete, r.State));
            Assert.True(returned[0].Searched > 500, $"the long path searched {returned[0].Searched} nodes");
        }

        /// <summary>Holds up every search of the active AstarPath's path processor before it starts.</summary>
        private sealed class SlowPathSearches : IDisposable
        {
            private readonly object processor;
            private readonly EventInfo searchStarts;
            private readonly Action<Pathfinding.Path> handler;

            internal SlowPathSearches(TimeSpan delay)
            {
                processor = typeof(AstarPath).GetField("pathProcessor", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(AstarPath.active);
                searchStarts = processor.GetType().GetEvent("OnPathPreSearch");
                handler = _ => Thread.Sleep(delay);
                searchStarts.AddEventHandler(processor, handler);
            }

            public void Dispose() => searchStarts.RemoveEventHandler(processor, handler);
        }

        [Fact]
        public void FramesFollowTheGameTimeScalesAndRefuseAPausedGame()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel());
            var time = Kingmaker.Game.Instance.TimeController;
            var scale = time.PlayerTimeScale;
            try
            {
                // TimeController.Tick: min(PlayerTimeScale * DebugTimeScale / 12, Time.deltaTime), where Unity scales the
                // 1/30 s frame by the time scale after the first frame.
                time.PlayerTimeScale = 2f;
                battle.Run(TimeSpan.FromSeconds(1));
                Assert.Equal(2f / 30f, time.DeltaTime, 5);

                time.PlayerTimeScale = 0f;
                var paused = Assert.Throws<InvalidOperationException>(() => battle.Run(TimeSpan.FromSeconds(1)));
                Assert.Contains("set its time scale to 0", paused.Message);
            }
            finally
            {
                time.PlayerTimeScale = scale;
            }
        }

        [Fact]
        public void StandInViewsAreDistinctLiveUnityObjects()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel());
            var fighter = battle.Unit("fighter").View;
            var cultist = battle.Unit("cultist").View;

            // The game's Unity compares live objects by reference, so stand-ins are told apart as the game's own views are.
            Assert.True(fighter);
            Assert.False(fighter == cultist);
            Assert.False(fighter.Equals(cultist));
            Assert.False(fighter.transform == cultist.transform);
            Assert.False(fighter.SphereBoundsCollider == cultist.SphereBoundsCollider);
            Assert.Equal(2, new System.Collections.Generic.HashSet<UnityEngine.Object> { fighter, cultist }.Count);
            // An instance id needs Unity's native code, which the game's battle code never asks for.
            Assert.True(UnityNative.IsUnavailable(Record.Exception(() => fighter.GetInstanceID())));
        }

        [Fact]
        public void ThePhysicsSceneAndFrameClockAnswerOnlyWhileABattleRuns()
        {
            game.RequireGame();
            // Outside a battle nothing answers for the missing level: these native calls fail as every path that needs
            // the Unity engine does, so a test that depends on them is reported as unity-runtime-required.
            Assert.True(UnityNative.IsUnavailable(Record.Exception(() => UnityEngine.Physics.Raycast(UnityEngine.Vector3.up, UnityEngine.Vector3.down, 10f))));
            Assert.True(UnityNative.IsUnavailable(Record.Exception(() => { _ = UnityEngine.Time.frameCount; })));

            using (OfflineBattle.Start(Duel()))
            {
                // A battle's scene holds no level colliders, so a ray finds no ground.
                Assert.False(UnityEngine.Physics.Raycast(new UnityEngine.Vector3(50f, 42f, -70f), UnityEngine.Vector3.down, 10f));
                Assert.True(UnityEngine.Time.frameCount > 0);
            }

            Assert.True(UnityNative.IsUnavailable(Record.Exception(() => UnityEngine.Physics.Raycast(UnityEngine.Vector3.up, UnityEngine.Vector3.down, 10f))));
            Assert.True(UnityNative.IsUnavailable(Record.Exception(() => { _ = UnityEngine.Time.realtimeSinceStartup; })));
        }

        [Fact]
        public void WalkingOutOfAThreatenedAreaProvokesAnAttackOfOpportunity()
        {
            game.RequireGame();
            var scenario = Duel();
            scenario.Units.Single(u => u.Id == "cultist").At(51.2f, 40f, -70f);
            using var battle = OfflineBattle.Start(scenario, seed: 21);
            var initial = battle.InitialState;
            var fighter = battle.Unit("fighter");
            var cultist = battle.Unit("cultist");
            var attacks = new AttackOfOpportunityRecorder();
            battle.Subscribe(attacks);

            // Nobody is told to fight: the game's vision, memory and combat join controllers bring both units into combat,
            // and its engagement controller engages the cultist standing in the fighter's reach.
            battle.Run(TimeSpan.FromSeconds(1));
            Assert.True(fighter.IsInCombat);
            Assert.True(cultist.IsInCombat);
            Assert.Contains(cultist, fighter.CombatState.EngagedUnits);
            Assert.Empty(attacks.Attacks);

            // Before its initiative comes up, a unit surprised into combat makes no attack of opportunity
            // (UnitCombatState.AttackOfOpportunity requires CanActInCombat, as the rules for flat-footed units do).
            for (var waited = 0; waited < 80 && fighter.CombatState.IsWaitingInitiative; waited++) battle.Run(TimeSpan.FromSeconds(0.1));
            Assert.False(fighter.CombatState.IsWaitingInitiative);

            // The cultist walks away along the navigation mesh; leaving the fighter's reach disengages it, and the
            // engagement controller makes the fighter attack.
            cultist.Commands.Run(new UnitMoveTo(new UnityEngine.Vector3(54f, 40f, -68.1f), 0.3f));
            battle.RunUntilIdle(TimeSpan.FromSeconds(12));
            Assert.Contains(attacks.Attacks, a => a.Key == fighter && a.Value == cultist);
            Assert.DoesNotContain(cultist, fighter.CombatState.EngagedUnits);

            battle.Reset();
            Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
            Assert.True(initial.Equals(battle.InitialState), string.Join(Environment.NewLine, initial.Difference(battle.InitialState)));
        }

        [Fact]
        public void AHugeDemonThreatensAsFarAsItsOwnViewPrefabReaches()
        {
            game.RequireGame();
            var scenario = new BattleScenario("demon") { MainCharacter = "fighter", Area = Guids.KenabresBurning, ControlledSides = { "party", "enemies" } };
            scenario.Unit("fighter", "party", Guids.Fighter).At(50, 40, -70);
            scenario.Unit("demon", "enemies", Guids.Glabrezu).At(57, 40, -70);
            using var battle = OfflineBattle.Start(scenario, seed: 23);
            var fighter = battle.Unit("fighter");
            var demon = battle.Unit("demon");

            // Corpulence comes from each unit's own view prefab: the demon's is larger than the 0.5 m the game gives a
            // unit without a model, and larger than the fighter's.
            Assert.True(demon.View.Corpulence > 0.75f, $"demon corpulence {demon.View.Corpulence}");
            Assert.True(demon.View.Corpulence > fighter.View.Corpulence, $"demon {demon.View.Corpulence}, fighter {fighter.View.Corpulence}");

            // Stand the demon farther away than its reach with model-less sizes, but within its reach with the real ones.
            var reach = demon.GetThreatRange(demon.GetThreatHand()).Value;
            var withoutModels = 0.5f + 0.5f + reach;
            var withModels = demon.View.Corpulence + fighter.View.Corpulence + reach;
            demon.Position = fighter.Position + new UnityEngine.Vector3((withoutModels + withModels) / 2, 0f, 0f);
            battle.Run(TimeSpan.FromSeconds(1));
            Assert.True(demon.IsInCombat);
            Assert.Contains(fighter, demon.CombatState.EngagedUnits);
            // The game's EntityBoundsController moved the demon's bound sphere to its new position and sized it.
            Assert.Equal(demon.Position, demon.View.SphereBoundsCollider.transform.position);
            Assert.Equal(demon.View.Corpulence, demon.View.SphereBoundsCollider.radius);

            battle.Reset();
            Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
        }

        private static BattleScenario Demon(string name)
        {
            var scenario = new BattleScenario(name) { MainCharacter = "fighter", Area = Guids.KenabresBurning, ControlledSides = { "party", "enemies" } };
            scenario.Unit("fighter", "party", Guids.Fighter).At(50, 40, -70);
            scenario.Unit("demon", "enemies", Guids.Glabrezu).At(57, 40, -70);
            return scenario;
        }

        /// <summary>Whether the inputs have the unit view bundles, as the installation does and a snapshot does not.</summary>
        private static bool InputsHaveUnitBundles
            => System.IO.File.Exists(System.IO.Path.Combine(OfflineRuntime.Inputs.Bundles, Guids.GlabrezuView + ".unit"));

        private static float Acceleration(Kingmaker.EntitySystem.Entities.UnitEntityData unit)
            => (float)typeof(Kingmaker.View.UnitMovementAgentBase)
                .GetField("m_Acceleration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .GetValue(unit.View.AgentASP);

        [Fact]
        public void ACorpulenceOverrideReplacesOnlyTheRadius()
        {
            game.RequireGame();
            float? prefabAcceleration = null;
            if (InputsHaveUnitBundles)
            {
                using var plain = OfflineBattle.Start(Demon("demon plain"), seed: 37);
                var plainDemon = plain.Unit("demon");
                Assert.NotEqual(1.5f, plainDemon.View.Corpulence);
                prefabAcceleration = Acceleration(plainDemon);
            }
            var scenario = Demon("demon override");
            scenario.CorpulenceOverrideBySize[Kingmaker.Enums.Size.Medium] = 0.6f;
            scenario.CorpulenceOverrideBySize[Kingmaker.Enums.Size.Huge] = 1.5f;
            using var battle = OfflineBattle.Start(scenario, seed: 37);
            var fighter = battle.Unit("fighter");
            var demon = battle.Unit("demon");

            Assert.Equal(0.6f, fighter.View.Corpulence);
            Assert.Equal(1.5f, demon.View.Corpulence);
            // Only the radius changes: the movement agent is the prefab's when the bundle is there, else the class's.
            Assert.Equal(prefabAcceleration ?? 20f, Acceleration(demon));
            // The game still scales the overridden value when the size changes.
            fighter.AddBuff(OfflineGame.Blueprint<BlueprintBuff>(Guids.EnlargePersonBuff), fighter, TimeSpan.FromMinutes(1));
            Assert.Equal(0.6f / 0.66f, fighter.View.Corpulence, 4);
            fighter.Commands.Run(new UnitMoveTo(new UnityEngine.Vector3(47.2f, 40f, -65.2f), 0.3f));
            battle.RunUntilIdle(TimeSpan.FromSeconds(12));
            Assert.InRange(Distance(fighter.Position, new UnityEngine.Vector3(47.2f, 40f, -65.2f)), 0f, 0.5f);

            battle.Reset();
            Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
        }

        [Fact]
        public void ACorpulenceFallbackAppliesOnlyWithoutTheBundle()
        {
            game.RequireGame();
            var scenario = Demon("demon fallback");
            scenario.CorpulenceFallbackBySize[Kingmaker.Enums.Size.Medium] = 0.6f;
            scenario.CorpulenceFallbackBySize[Kingmaker.Enums.Size.Huge] = 1.5f;
            using var battle = OfflineBattle.Start(scenario, seed: 41);
            var fighter = battle.Unit("fighter");
            var demon = battle.Unit("demon");

            if (InputsHaveUnitBundles)
            {
                // The prefabs are read, so the fallback is not used.
                Assert.NotEqual(0.6f, fighter.View.Corpulence);
                Assert.True(demon.View.Corpulence > 0.75f && demon.View.Corpulence != 1.5f, $"demon corpulence {demon.View.Corpulence}");
            }
            else
            {
                Assert.Equal(0.6f, fighter.View.Corpulence);
                Assert.Equal(1.5f, demon.View.Corpulence);
                Assert.Equal(20f, Acceleration(demon));
            }

            battle.Reset();
            Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
        }

        [Fact]
        public void EnlargingAUnitScalesItsCorpulenceAndItsBoundSphere()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel(), seed: 29);
            var fighter = battle.Unit("fighter");
            var medium = fighter.View.Corpulence;
            Assert.Equal(medium, fighter.View.SphereBoundsCollider.radius);

            // The game scales the prefab's corpulence with the size step; its EntityBoundsController resizes the bound
            // sphere on its next tick.
            fighter.AddBuff(OfflineGame.Blueprint<BlueprintBuff>(Guids.EnlargePersonBuff), fighter, TimeSpan.FromMinutes(1));
            Assert.Equal(Kingmaker.Enums.Size.Large, fighter.State.Size);
            Assert.True(fighter.View.Corpulence > medium * 1.4f, $"enlarged corpulence {fighter.View.Corpulence}");
            Assert.Equal(medium, fighter.View.SphereBoundsCollider.radius);
            battle.Run(TimeSpan.FromSeconds(0.1));
            Assert.Equal(fighter.View.Corpulence, fighter.View.SphereBoundsCollider.radius);

            battle.Reset();
            Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
        }

        [Fact]
        public void APolymorphSwapsTheViewForItsOwnPrefabAndBack()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel(), seed: 31);
            var initial = battle.InitialState;
            var fighter = battle.Unit("fighter");
            var cultist = battle.Unit("cultist");
            var own = fighter.View;
            var bear = OfflineGame.Blueprint<BlueprintBuff>(Guids.WildShapeBearBuff);
            var attack = new UnitAttack(cultist);
            fighter.Commands.Run(attack);

            // Polymorph.TryReplaceView: the bear's own view, whose corpulence is not scaled by size, and every command
            // but movement is interrupted.
            fighter.AddBuff(bear, fighter, TimeSpan.FromMinutes(1));
            Assert.NotSame(own, fighter.View);
            Assert.True(fighter.View.DisableSizeScaling);
            Assert.True(fighter.View.Corpulence > 1f, $"bear corpulence {fighter.View.Corpulence}");
            Assert.Equal(UnitCommand.ResultType.Interrupt, attack.Result);
            battle.Run(TimeSpan.FromSeconds(0.1));
            Assert.Equal(fighter.View.Corpulence, fighter.View.SphereBoundsCollider.radius);

            // Polymorph.RestoreView: a view of the unit's own prefab again. The bear's bite and claws, left without an
            // owner, are disposed by the game's GarbageEntityController.
            fighter.Buffs.RemoveFact(bear);
            Assert.False(fighter.View.DisableSizeScaling);
            Assert.Equal(own.Corpulence, fighter.View.Corpulence);
            battle.Run(TimeSpan.FromSeconds(0.1));

            battle.Reset();
            Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
            Assert.True(initial.Equals(battle.InitialState), string.Join(Environment.NewLine, initial.Difference(battle.InitialState)));
        }

        [Fact]
        public void UnitsJoinCombatAndRecoverTheirActionsOverTime()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel(), seed: 13);
            var initial = battle.InitialState;
            var fighter = battle.Unit("fighter");
            var cultist = battle.Unit("cultist");
            fighter.JoinCombat(notSurprised: true);
            cultist.JoinCombat(notSurprised: true);
            Assert.True(fighter.IsInCombat);
            Assert.Contains("unit fighter combat in", battle.Observe().Lines);

            var attack = new UnitAttack(cultist);
            fighter.Commands.Run(attack);
            battle.RunUntilIdle(TimeSpan.FromSeconds(12));
            Assert.True(attack.IsActed);
            // Entering combat, the game's UnitCombatPrepareController rolls initiative and staggers when each unit's
            // first round starts (Cooldown.Initiative). A unit that is not surprised may act before then; the attack
            // charges the standard action for the round (UnitEntityData.SpendAction).
            var state = fighter.CombatState;
            Assert.True(state.Prepared);
            var cooldown = state.Cooldown.StandardAction;
            Assert.InRange(cooldown, 3f, 6f);
            // While a unit waits for its initiative, UnitCombatCooldownsController counts down only that wait.
            if (state.IsWaitingInitiative)
            {
                var wait = state.Cooldown.Initiative;
                battle.Run(TimeSpan.FromSeconds(wait + 0.05));
                Assert.False(state.IsWaitingInitiative);
                Assert.InRange(state.Cooldown.StandardAction, cooldown - 0.1f, cooldown);
                cooldown = state.Cooldown.StandardAction;
            }
            battle.Run(TimeSpan.FromSeconds(1));
            Assert.InRange(state.Cooldown.StandardAction, cooldown - 1.1f, cooldown - 0.9f);
            battle.Run(TimeSpan.FromSeconds(6));
            Assert.Equal(0f, state.Cooldown.StandardAction);

            // The game's own disposal leaves combat (UnitEntityData.OnDispose), so the reset needs no extra step.
            battle.Reset();
            Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
            Assert.True(initial.Equals(battle.InitialState), string.Join(Environment.NewLine, initial.Difference(battle.InitialState)));
        }

        [Fact]
        public void TheGameAiFightsForUnitsOfUncontrolledSides()
        {
            game.RequireGame();
            var scenario = Duel();
            scenario.ControlledSides.Remove("enemies");
            using var battle = OfflineBattle.Start(scenario, seed: 17);
            var initial = battle.InitialState;
            Assert.Contains("unit fighter control controlled", initial.Lines);
            Assert.Contains("unit cultist control game-ai", initial.Lines);
            var fighter = battle.Unit("fighter");
            var recorder = new CommandRecorder();
            battle.Subscribe(recorder);
            var given = new GivenCommands();
            battle.Subscribe(given);

            // Nobody commands anyone: the enemies see the fighter, join combat and attack it with the game's AI, which
            // never decides for the controlled fighter. The guard comes from behind the cultist; whether it gets past to
            // strike depends on the path, so the test checks that the game's AI gave it its commands.
            battle.Run(TimeSpan.FromSeconds(12));
            Assert.Contains(recorder.Acted, a => a.ByAi && a.Executor == battle.Unit("cultist"));
            Assert.Contains(given.Given, c => c.AiAction != null && c.Executor == battle.Unit("guard"));
            Assert.DoesNotContain(recorder.Acted, a => a.Executor == fighter);
            Assert.DoesNotContain(given.Given, c => c.Executor == fighter);
            Assert.True(fighter.IsInCombat);

            battle.Reset();
            Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
            Assert.True(initial.Equals(battle.InitialState), string.Join(Environment.NewLine, initial.Difference(battle.InitialState)));
        }

        [Fact]
        public void LethalDamageKillsTheUnitThroughTheGameLifeController()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel(), seed: 5);
            var initial = battle.InitialState;
            var fighter = battle.Unit("fighter");
            var cultist = battle.Unit("cultist");
            fighter.JoinCombat(notSurprised: true);
            cultist.JoinCombat(notSurprised: true);
            cultist.Commands.Run(new UnitAttack(fighter));
            RuleScope.Run("lethal damage", () =>
                Rulebook.Trigger(new RuleDealDamage(fighter, cultist, new PhysicalDamage(new ModifiableDiceFormula(new DiceFormula(20, DiceType.D20)), 200, PhysicalDamageForm.Slashing))));
            // The rule only records the damage; the game's UnitLifeController decides the life state on the next frame.
            Assert.Equal(Kingmaker.UnitLogic.UnitLifeState.Conscious, cultist.Descriptor.State.LifeState);

            battle.Run(TimeSpan.FromSeconds(0.1));
            Assert.Equal(Kingmaker.UnitLogic.UnitLifeState.Dead, cultist.Descriptor.State.LifeState);
            Assert.True(cultist.Descriptor.State.IsFinallyDead);
            Assert.False(cultist.IsInCombat);
            Assert.True(cultist.Commands.Empty);
            Assert.Equal(Kingmaker.UnitLogic.UnitLifeState.Conscious, fighter.Descriptor.State.LifeState);

            battle.Reset();
            Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
            Assert.True(initial.Equals(battle.InitialState), string.Join(Environment.NewLine, initial.Difference(battle.InitialState)));
        }

        [Fact]
        public void BuffDurationsRunOutInGameFrames()
        {
            game.RequireGame();
            var scenario = Duel();
            scenario.Units.Single(u => u.Id == "fighter").WithBuff(Guids.BlessBuff, rounds: 1);
            using var battle = OfflineBattle.Start(scenario, seed: 5);
            var bless = OfflineGame.Blueprint<BlueprintBuff>(Guids.BlessBuff);
            var shield = OfflineGame.Blueprint<BlueprintBuff>(Guids.ShieldOfFaithBuff);
            Assert.NotNull(battle.Unit("fighter").Buffs.GetBuff(bless));

            battle.Run(TimeSpan.FromSeconds(5.5));
            Assert.NotNull(battle.Unit("fighter").Buffs.GetBuff(bless));
            battle.Run(TimeSpan.FromSeconds(1));
            // The game's UnitBuffsController ends a one-round buff after 6 seconds of game time; ten rounds still run.
            Assert.Null(battle.Unit("fighter").Buffs.GetBuff(bless));
            Assert.NotNull(battle.Unit("cultist").Buffs.GetBuff(shield));

            battle.Reset();
            Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
        }

        [Fact]
        public void FailureWhileACommandActsLeavesTheNextBuildClean()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel(), seed: 9);
            var initial = battle.InitialState;
            var handler = new ThrowingCommandHandler();
            battle.Subscribe(handler);
            var attack = new UnitAttack(battle.Unit("cultist"));
            battle.Unit("fighter").Commands.Run(attack);

            // The game catches the handler's exception and logs it; the frames fail as a rule settlement does.
            var error = Assert.Throws<RuleSettlementException>(() => battle.RunUntilIdle(TimeSpan.FromSeconds(12)));
            Assert.Contains(ThrowingCommandHandler.Message, error.Message);
            Assert.Equal(1, handler.Calls);
            Assert.True(attack.IsActed);

            battle.Reset();
            Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
            Assert.True(initial.Equals(battle.InitialState), string.Join(Environment.NewLine, initial.Difference(battle.InitialState)));
        }

        [Fact]
        public void ResetWithACommandStillInItsSlotLeavesTheNextBuildClean()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel(), seed: 9);
            var initial = battle.InitialState;
            var fighter = battle.Unit("fighter");
            // UnitCommands.Run places and runs the command; no frame has started it yet.
            fighter.Commands.Run(new UnitAttack(battle.Unit("cultist")));
            Assert.Contains("unit fighter commands running", battle.Observe().Lines);

            battle.Reset();
            Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
            Assert.True(initial.Equals(battle.InitialState), string.Join(Environment.NewLine, initial.Difference(battle.InitialState)));
            Assert.True(battle.Unit("fighter").Commands.Empty);
        }

        private static float Distance(UnityEngine.Vector3 a, UnityEngine.Vector3 b)
            => new UnityEngine.Vector2(a.x - b.x, a.z - b.z).magnitude;

        [Fact]
        public void AddingAnEffectRecalculatesRealStatsAndRemovingItRestoresThem()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel());
            var fighter = battle.Unit("fighter");
            var weapon = fighter.Body.PrimaryHand.Weapon;
            var strength = fighter.Stats.Strength.ModifiedValue;
            var attack = Rulebook.Trigger(new RuleCalculateAttackBonusWithoutTarget(fighter, weapon, 0)).Result;
            var before = OfflineGame.StatSnapshot(fighter);

            Buff buff = null;
            RuleScope.Run("bull's strength", () => buff = fighter.AddBuff(OfflineGame.Blueprint<BlueprintBuff>(Guids.BullsStrengthBuff), fighter, TimeSpan.FromMinutes(1)));
            Assert.Equal(strength + 4, fighter.Stats.Strength.ModifiedValue);
            Assert.Equal(attack + 2, Rulebook.Trigger(new RuleCalculateAttackBonusWithoutTarget(fighter, weapon, 0)).Result);

            RuleScope.Run("remove bull's strength", buff.Remove);
            Assert.Equal(before, OfflineGame.StatSnapshot(fighter));
            Assert.Equal(attack, Rulebook.Trigger(new RuleCalculateAttackBonusWithoutTarget(fighter, weapon, 0)).Result);
        }

        [Fact]
        public void PathsAreObservedAsSuppliedAndFollowTheUnitsAcrossResets()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel());
            var path = battle.Path("fighter-to-cultist");
            Assert.Equal("consumer:battle-tests", path.Source);
            Assert.Equal(new[] { new BattlePoint(50, 40, -70), new BattlePoint(51, 40, -69.5f), new BattlePoint(52, 40, -70) }, path.Points);
            Assert.Equal(2.236, path.Distance);
            Assert.Null(path.Cost);
            Assert.Equal("meters", path.Units);
            Assert.Same(battle.Unit("fighter"), path.From);
            Assert.Same(battle.Unit("cultist"), path.To);

            var oldFighter = path.From;
            battle.Reset();
            var rebuilt = battle.Path("fighter-to-cultist");
            Assert.Equal(path.Points, rebuilt.Points);
            Assert.Equal(path.Distance, rebuilt.Distance);
            Assert.Same(battle.Unit("fighter"), rebuilt.From);
            Assert.NotSame(oldFighter, rebuilt.From);
        }

        [Fact]
        public void TemporaryBlueprintsLastOneBuildAndNeverReplaceExistingOnes()
        {
            game.RequireGame();
            using var battle = OfflineBattle.Start(Duel());
            var guid = BlueprintGuid.Parse("0b5f1a7e0c3d4e2f9a8b7c6d5e4f3a21");
            Assert.Null(ResourcesLibrary.TryGetBlueprint(guid));
            var feature = battle.AddBlueprint(new Kingmaker.Blueprints.Classes.BlueprintFeature { AssetGuid = guid, name = "BattleTestsTemporaryFeature" });
            Assert.Same(feature, ResourcesLibrary.TryGetBlueprint(guid));
            battle.Unit("fighter").AddFact(feature);
            Assert.True(battle.Unit("fighter").HasFact(feature));
            var existing = OfflineGame.Blueprint<Kingmaker.Blueprints.Classes.BlueprintFeature>(Guids.Toughness);
            Assert.Throws<InvalidOperationException>(() => battle.AddBlueprint(new Kingmaker.Blueprints.Classes.BlueprintFeature { AssetGuid = existing.AssetGuid }));
            Assert.Same(existing, ResourcesLibrary.TryGetBlueprint(existing.AssetGuid));

            battle.Reset();
            Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
            Assert.Null(ResourcesLibrary.TryGetBlueprint(guid));
            Assert.False(battle.Unit("fighter").HasFact(feature));
        }

        [Fact]
        public void PathEndpointsMustBeScenarioUnits()
        {
            var scenario = Duel();
            scenario.Paths.Add(new BattlePathSpec("nowhere", "consumer:battle-tests", new[] { new BattlePoint(0, 0, 0), new BattlePoint(1, 0, 0) }) { To = "missing" });
            var error = Assert.Throws<ArgumentException>(() => OfflineBattle.Start(scenario));
            Assert.Contains("missing", error.Message);
        }

        [Fact]
        public void ChargenPregenStartingGearFailureIsReportedNotHidden()
        {
            game.RequireGame();
            var scenario = new BattleScenario("chargen-pregen");
            scenario.Unit("pregen", "party", Guids.ChargenPregen);
            var error = Assert.Throws<BattleSetupException>(() => OfflineBattle.Start(scenario));
            var settlement = Assert.IsType<RuleSettlementException>(error.InnerException);
            Assert.Contains(settlement.Errors, log => log.Message != null && log.Message.Contains("can't insert item"));
            Assert.True(error.Cleanup.Clean, error.Cleanup.ToString());
        }

        [Fact]
        public void EquipmentTheGameRefusesFailsTheBuild()
        {
            game.RequireGame();
            var scenario = new BattleScenario("exotic-weapon");
            // A bastard sword one-handed is an exotic weapon; a player-faction Fighter 5 is not proficient with it.
            scenario.Unit("fighter", "party", Guids.Fighter).Equip(BattleSlot.PrimaryHand, Guids.StandardBastardSword);
            var error = Assert.Throws<BattleSetupException>(() => OfflineBattle.Start(scenario));
            Assert.Contains("CanBeEquippedBy", error.Message);
            Assert.True(error.Cleanup.Clean, error.Cleanup.ToString());
            using var next = OfflineBattle.Start(Duel());
            Assert.NotNull(next.Unit("fighter"));
        }

        /// <summary>
        /// With WOTR_TEST_SCENARIO=undisposed-battle the test returns without disposing its battle; the worker report then
        /// counts the process as dirty and the run fails validation.
        /// </summary>
        [Fact]
        public void OnlyOneBattleRunsAtATime()
        {
            game.RequireGame();
            var keep = Environment.GetEnvironmentVariable("WOTR_TEST_SCENARIO") == "undisposed-battle";
            var battle = OfflineBattle.Start(Duel());
            try
            {
                var error = Assert.Throws<InvalidOperationException>(() => OfflineBattle.Start(Duel()));
                Assert.Contains("still active", error.Message);
            }
            finally
            {
                if (!keep) battle.Dispose();
            }
        }

        /// <summary>
        /// With WOTR_TEST_SCENARIO=dirty-battle the leaked subscription stays, the worker turns dirty and refuses the next
        /// battle, and the run fails validation. Otherwise the test removes it itself and the cleanup stays clean.
        /// </summary>
        [Fact]
        public void UntrackedSubscriptionIsResidue()
        {
            game.RequireGame();
            var dirty = Environment.GetEnvironmentVariable("WOTR_TEST_SCENARIO") == "dirty-battle";
            var handler = new CountingBuffHandler();
            var battle = OfflineBattle.Start(Duel());
            EventBus.Subscribe(handler);
            if (!dirty) EventBus.Unsubscribe(handler);
            battle.Dispose();
            if (!dirty)
            {
                Assert.True(battle.LastCleanup.Clean, battle.LastCleanup.ToString());
                return;
            }
            Assert.False(battle.LastCleanup.Clean);
            Assert.Contains(battle.LastCleanup.Residue, line => line.Contains(typeof(CountingBuffHandler).FullName));
            Assert.True(BattleWorker.IsDirty);
            var refused = Assert.Throws<InvalidOperationException>(() => OfflineBattle.Start(Duel()));
            Assert.Contains(BattleWorker.DirtyMarker, refused.Message);
        }
    }
}
