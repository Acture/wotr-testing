using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Stats;
using WotR.Testing.BattleSupport;
using WotR.Testing.Offline;
using Xunit;

namespace WotR.Testing.BattleTests
{
    [Collection("battles")]
    public sealed class BattleEnvironmentTests
    {
        private readonly BattleGame game;

        public BattleEnvironmentTests(BattleGame game) => this.game = game;

        /// <summary>
        /// A fighter and a cultist about 5.6 m apart on the Kenabres navigation mesh, out of each other's reach. The decision
        /// maker controls the party; the game's AI fights for the cultist.
        /// </summary>
        private static BattleScenario Skirmish(params string[] controlled)
        {
            var scenario = new BattleScenario("skirmish") { MainCharacter = "fighter", Area = Guids.KenabresBurning };
            foreach (var side in controlled.Length == 0 ? new[] { "party" } : controlled) scenario.ControlledSides.Add(side);
            scenario.Unit("fighter", "party", Guids.Fighter)
                .WithBaseStat(StatType.Strength, 16)
                .Equip(BattleSlot.PrimaryHand, Guids.StandardLongsword)
                .At(47.2f, 40, -65.2f);
            scenario.Unit("cultist", "enemies", Guids.Cultist).At(52, 40, -68);
            scenario.Paths.Add(new BattlePathSpec("fighter-recorded", "consumer:battle-tests",
                new[] { new BattlePoint(47.2f, 40, -65.2f), new BattlePoint(49, 40, -66.5f) })
            {
                From = "fighter",
                Distance = 2.2,
                Units = "meters",
            });
            return scenario;
        }

        /// <summary>Every ready unit attacks an enemy the game allows, or waits.</summary>
        private static BattleAction[] Fight(BattleObservation observation)
            => observation.Ready
                .Select(id => observation.Candidates.FirstOrDefault(c => c.Unit == id && c.Kind == BattleActionKind.Attack) ?? BattleAction.Wait(id))
                .ToArray();

        [Fact]
        public void ThreeEpisodesRunFromResetToADecisionAgainstTheGameAi()
        {
            game.RequireGame();
            using var environment = BattleEnvironment.Start(Skirmish(), seed: 1, new BattleEnvironmentOptions { MaxSteps = 150 });
            var initial = environment.Battle.InitialState;
            for (var episode = 1; episode <= 3; episode++)
            {
                var observation = episode == 1 ? environment.Observe() : environment.Reset();
                var recorder = new CommandRecorder();
                environment.Battle.Subscribe(recorder);
                Assert.Equal(episode, observation.Episode);
                Assert.Equal(0, observation.Step);
                Assert.True(initial.Equals(environment.Battle.InitialState), string.Join(Environment.NewLine, initial.Difference(environment.Battle.InitialState)));
                // Only the controlled fighter is offered actions; the game's AI decides for the cultist.
                Assert.Equal(new[] { "fighter" }, observation.Ready.ToArray());
                Assert.True(observation.Unit("fighter").Controlled);
                Assert.False(observation.Unit("cultist").Controlled);
                Assert.Contains(BattleAction.Attack("fighter", "cultist"), observation.Candidates);
                Assert.DoesNotContain(observation.Candidates, c => c.Unit == "cultist");
                Assert.DoesNotContain(BattleAction.Attack("fighter", "fighter"), observation.Candidates);

                // The fighter takes the game's path towards the cultist.
                var path = observation.Paths.Single(p => p.Id == "game:fighter->cultist");
                Assert.Equal(BattleEnvironment.GamePathSource, path.Source);
                Assert.True(path.Usable, path.Problem);
                var moved = environment.Step(BattleAction.Move("fighter", path.Id));
                Assert.True(moved.Walked.TryGetValue("fighter", out var walked) && walked > 1, environment.Record.ToString());
                Assert.Contains(moved.Effects, line => line.StartsWith("+ unit fighter position ", StringComparison.Ordinal));
                // The recorded path started where the fighter stood; it no longer does.
                Assert.False(moved.Observation.Paths.Single(p => p.Id == "fighter-recorded").Usable);

                var result = moved;
                var damage = 0;
                while (!result.Ended)
                {
                    result = environment.Step(Fight(result.Observation));
                    damage += result.Effects.Count(line => line.StartsWith("+ unit ", StringComparison.Ordinal) && line.Contains(" hit-points "));
                }
                Assert.False(result.Truncated, environment.Record.ToString());
                Assert.NotNull(result.Outcome);
                Assert.True(damage > 0, "No step changed hit points.");
                var winner = result.Observation.Units.Single(u => u.Side == result.Outcome.Winner);
                var loser = result.Observation.Units.Single(u => u.Side != result.Outcome.Winner);
                Assert.True(winner.Conscious);
                Assert.False(loser.Conscious);
                Assert.True(loser.HitPoints <= 0, $"{loser.Id} has {loser.HitPoints} hit points");
                Assert.Empty(result.Observation.Candidates);
                Assert.Throws<InvalidOperationException>(() => environment.Step(BattleAction.Wait("fighter")));
                Assert.Same(result.Outcome, environment.Record.Outcome);
                Assert.Equal(result.Step, environment.Record.Steps.Count);

                // The cultist fought with the game's AI; every command of the fighter was the decision maker's.
                var cultist = environment.Battle.Unit("cultist");
                var fighter = environment.Battle.Unit("fighter");
                Assert.Contains(recorder.Acted, a => a.Executor == cultist && a.ByAi);
                Assert.DoesNotContain(recorder.Acted, a => a.Executor == fighter && a.ByAi);
                Assert.Contains(recorder.Acted, a => a.Executor == fighter);
            }
            environment.Reset();
            Assert.True(environment.Battle.LastCleanup.Clean, environment.Battle.LastCleanup.ToString());
        }

        [Fact]
        public void SelfPlayControlsBothSidesAndTheGameAiDecidesForNobody()
        {
            game.RequireGame();
            using var environment = BattleEnvironment.Start(Skirmish("party", "enemies"), seed: 8, new BattleEnvironmentOptions { MaxSteps = 150 });
            var recorder = new CommandRecorder();
            environment.Battle.Subscribe(recorder);
            var observation = environment.Observe();
            Assert.Equal(new[] { "fighter", "cultist" }, observation.Ready.ToArray());
            Assert.Contains(BattleAction.Attack("cultist", "fighter"), observation.Candidates);
            Assert.Contains(observation.Paths, p => p.Id == "game:cultist->fighter" && p.Usable);

            // One policy per side; here both attack whenever they can.
            var result = environment.Step(Fight(observation));
            while (!result.Ended) result = environment.Step(Fight(result.Observation));
            Assert.NotNull(result.Outcome);
            Assert.NotEmpty(recorder.Acted);
            Assert.DoesNotContain(recorder.Acted, a => a.ByAi);
            Assert.Contains(recorder.Acted, a => a.Executor == environment.Battle.Unit("cultist"));
        }

        [Fact]
        public void ObservingReadsPathsWithoutChangingTheBattle()
        {
            game.RequireGame();
            var clash = Skirmish();
            clash.Paths.Add(new BattlePathSpec("game:fighter->cultist", "consumer:battle-tests", new[] { new BattlePoint(0, 0, 0), new BattlePoint(1, 0, 0) }));
            Assert.Throws<ArgumentException>(() => BattleEnvironment.Start(clash));
            var uncontrolled = Skirmish();
            uncontrolled.ControlledSides.Clear();
            Assert.Contains("ControlledSides", Assert.Throws<ArgumentException>(() => BattleEnvironment.Start(uncontrolled)).Message);

            using var environment = BattleEnvironment.Start(Skirmish(), seed: 2);
            var state = environment.Battle.Observe();
            var first = environment.Observe();
            var second = environment.Observe();
            Assert.Equal(state, environment.Battle.Observe());

            var gamePath = first.Paths.Single(p => p.Id == "game:fighter->cultist");
            Assert.True(gamePath.Points.Count >= 2);
            Assert.InRange(gamePath.Distance.Value, 4.0, 12.0);
            Assert.Equal("meters", gamePath.Units);
            Assert.Null(gamePath.Cost);
            Assert.Equal("cultist", gamePath.Target);
            Assert.Equal(gamePath.Points, second.Paths.Single(p => p.Id == gamePath.Id).Points);
            // Paths are read for controlled units only.
            Assert.DoesNotContain(first.Paths, p => p.Unit == "cultist");

            var recorded = first.Paths.Single(p => p.Id == "fighter-recorded");
            Assert.Equal("consumer:battle-tests", recorded.Source);
            Assert.True(recorded.Usable, recorded.Problem);
            Assert.Equal(2.2, recorded.Distance);
            Assert.Contains(BattleAction.Move("fighter", "fighter-recorded"), first.Candidates);
            Assert.Equal(first.Candidates, second.Candidates);
        }

        [Fact]
        public void RejectedActionsChangeNothing()
        {
            game.RequireGame();
            using var environment = BattleEnvironment.Start(Skirmish(), seed: 3);
            var state = environment.Battle.Observe();
            var rejections = new[]
            {
                new[] { BattleAction.Attack("fighter", "fighter") },
                new[] { BattleAction.Move("fighter", "no-such-path") },
                new[] { BattleAction.Attack("cultist", "fighter") },
                new[] { BattleAction.Wait("fighter"), BattleAction.Attack("fighter", "cultist") },
                new[] { BattleAction.Wait("nobody") },
            };
            foreach (var actions in rejections)
            {
                var error = Assert.Throws<BattleActionRejectedException>(() => environment.Step(actions));
                Assert.Equal(1, error.Step);
                Assert.NotEmpty(error.Problems);
            }
            var uncontrolled = Assert.Throws<BattleActionRejectedException>(() => environment.Step(BattleAction.Wait("cultist")));
            Assert.Contains("is not controlled; the game's AI decides for cultist", uncontrolled.Message);
            Assert.Equal(0, environment.StepCount);
            Assert.Empty(environment.Record.Steps);
            Assert.Equal(state, environment.Battle.Observe());

            // A unit that is still running a command is not ready.
            var attack = environment.Step(BattleAction.Attack("fighter", "cultist"));
            if (!attack.Ended && !attack.Observation.Unit("fighter").Ready)
            {
                var busy = Assert.Throws<BattleActionRejectedException>(() => environment.Step(BattleAction.Wait("fighter")));
                Assert.Contains("is not ready", busy.Message);
            }
        }

        [Fact]
        public void TheSameSeedAndActionsReproduceTheEpisodeIncludingTheGameAi()
        {
            game.RequireGame();
            using var environment = BattleEnvironment.Start(Skirmish(), seed: 4);
            var result = environment.Step(BattleAction.Move("fighter", "game:fighter->cultist"));
            for (var i = 0; i < 12 && !result.Ended; i++) result = environment.Step(Fight(result.Observation));
            var record = environment.Record;
            Assert.True(record.Steps.Count >= 2);

            var differences = environment.Replay(record);
            Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
            Assert.Equal(record.Steps.Count, environment.Record.Steps.Count);
            Assert.Equal(record.Steps.Last().State, environment.Record.Steps.Last().State);
            Assert.Equal(4, environment.Record.Seed);
        }

        [Fact]
        public void AnErrorDuringAStepIsAnEnvironmentFailureOfThatStep()
        {
            game.RequireGame();
            using var environment = BattleEnvironment.Start(Skirmish(), seed: 5);
            environment.Step(BattleAction.Wait("fighter"));
            var handler = new ThrowingCommandHandler();
            environment.Battle.Subscribe(handler);

            // The first command that acts, the fighter's or the cultist's, fails the step.
            var failure = Assert.Throws<BattleEnvironmentException>(() => environment.Step(BattleAction.Attack("fighter", "cultist")));
            Assert.Equal(1, failure.Episode);
            Assert.Equal(2, failure.Step);
            Assert.Equal(BattleAction.Attack("fighter", "cultist"), failure.Actions[0]);
            Assert.IsType<RuleSettlementException>(failure.InnerException);
            Assert.Contains(ThrowingCommandHandler.Message, failure.Message);
            // The game's command controller catches each unit's exception and goes on, so both units' commands may act
            // in the failing frame; the step fails after it.
            var calls = handler.Calls;
            Assert.InRange(calls, 1, 2);
            Assert.NotNull(environment.Record.Error);
            Assert.Null(environment.Record.Outcome);
            Assert.Equal(failure.Message, environment.Observe().Failure);
            Assert.Empty(environment.Observe().Candidates);
            Assert.Throws<InvalidOperationException>(() => environment.Step(BattleAction.Wait("fighter")));

            var observation = environment.Reset();
            Assert.True(environment.Battle.LastCleanup.Clean, environment.Battle.LastCleanup.ToString());
            Assert.Null(observation.Failure);
            var next = environment.Step(BattleAction.Attack("fighter", "cultist"));
            Assert.Equal(calls, handler.Calls);
            Assert.Equal(1, next.Step);
        }

        [Fact]
        public void AnEpisodeIsTruncatedAtItsStepLimitWithoutAnOutcome()
        {
            game.RequireGame();
            using var environment = BattleEnvironment.Start(Skirmish(), seed: 6, new BattleEnvironmentOptions { MaxSteps = 2, DecisionInterval = TimeSpan.FromSeconds(1) });
            var first = environment.Step(BattleAction.Wait("fighter"));
            Assert.Equal("the decision interval passed", first.Decision);
            Assert.InRange(first.Elapsed.TotalSeconds, 1.0, 1.04);
            Assert.False(first.Ended);
            var second = environment.Step(BattleAction.Wait("fighter"));
            Assert.True(second.Truncated);
            Assert.Null(second.Outcome);
            Assert.Equal("the step limit of 2 was reached", second.Truncation);
            Assert.True(environment.Record.Truncated);
            Assert.Throws<InvalidOperationException>(() => environment.Step(BattleAction.Wait("fighter")));
        }

        [Fact]
        public void TheRewardAndOutcomeHooksDecideWhatTheTrainingCodeOptimizes()
        {
            game.RequireGame();
            var options = new BattleEnvironmentOptions
            {
                // End as soon as anyone is hurt, rewarding damage dealt to the enemies.
                Outcome = units => units.Any(u => u.HitPoints < u.MaxHitPoints) ? new BattleOutcome("first-blood", "a unit was hurt") : null,
                Reward = transition => transition.Before.Units.Where(u => u.Side == "enemies")
                    .Sum(u => u.HitPoints - transition.After.Unit(u.Id).HitPoints),
            };
            using var environment = BattleEnvironment.Start(Skirmish(), seed: 7, options);
            var result = environment.Step(BattleAction.Attack("fighter", "cultist"));
            var total = result.Reward;
            for (var i = 0; i < 20 && !result.Ended; i++)
            {
                result = environment.Step(Fight(result.Observation));
                total += result.Reward;
            }
            Assert.Equal("first-blood", result.Outcome?.Winner);
            Assert.Equal("a unit was hurt", result.Outcome.Reason);
            var cultist = result.Observation.Unit("cultist");
            Assert.Equal(cultist.MaxHitPoints - cultist.HitPoints, total);
        }

        /// <summary>Attacks the first enemy the game allows, or ends the turn.</summary>
        private static BattleAction[] FightInTurns(BattleObservation observation)
            => observation.Ready
                .Select(id => observation.Candidates.FirstOrDefault(c => c.Unit == id && c.Kind == BattleActionKind.Attack)
                    ?? observation.Candidates.First(c => c.Unit == id && c.Kind == BattleActionKind.EndTurn))
                .ToArray();

        [Fact]
        public void TurnBasedCombatStopsWhenTheGameWaitsForTheControlledUnitsTurn()
        {
            game.RequireGame();
            var scenario = Skirmish();
            scenario.TurnBased = true;
            using var environment = BattleEnvironment.Start(scenario, seed: 1, new BattleEnvironmentOptions { MaxSteps = 60 });
            var combat = Kingmaker.Game.Instance.TurnBasedCombatController;

            // Out of combat the game runs in real time.
            var observation = environment.Observe();
            Assert.False(combat.Initialized);
            Assert.Contains(BattleAction.Wait("fighter"), observation.Candidates);
            Assert.DoesNotContain(BattleAction.EndTurn("fighter"), observation.Candidates);

            // They see each other at once; the game starts turn-based combat and the step ends when the fighter's turn
            // waits for a command.
            var result = environment.Step(BattleAction.Wait("fighter"));
            Assert.True(combat.Initialized, environment.Record.ToString());
            Assert.Same(environment.Battle.Unit("fighter"), combat.CurrentTurn?.Rider);
            Assert.Equal(new[] { "fighter" }, result.Observation.Ready.ToArray());
            Assert.Contains(BattleAction.EndTurn("fighter"), result.Observation.Candidates);
            Assert.Contains(BattleAction.Attack("fighter", "cultist"), result.Observation.Candidates);
            Assert.DoesNotContain(BattleAction.Wait("fighter"), result.Observation.Candidates);
            // A move is a click on the ground; the scenario's path still starts where the fighter stands, the game's paths
            // end under other units, where a click is on the unit.
            Assert.Contains(BattleAction.Move("fighter", "fighter-recorded"), result.Observation.Candidates);
            Assert.DoesNotContain(result.Observation.Candidates, c => c.Kind == BattleActionKind.Move && c.Path.StartsWith(BattleEnvironment.GamePathPrefix, StringComparison.Ordinal));

            // Ending the turn hands it to the game's AI, and the next decision is the fighter's next turn.
            var round = combat.RoundNumber;
            result = environment.Step(BattleAction.EndTurn("fighter"));
            Assert.Same(environment.Battle.Unit("fighter"), combat.CurrentTurn?.Rider);
            Assert.Equal(round + 1, combat.RoundNumber);

            // The fighter attacks through the player's pointer and click until the game decides the battle.
            var attacks = 0;
            while (!result.Ended)
            {
                var actions = FightInTurns(result.Observation);
                attacks += actions.Count(a => a.Kind == BattleActionKind.Attack);
                result = environment.Step(actions);
                Assert.DoesNotContain("ignored", result.Decision);
            }
            Assert.False(result.Truncated, environment.Record.ToString());
            Assert.NotNull(result.Outcome);
            Assert.True(attacks > 0);
        }

        [Fact]
        public void TurnBasedSelfPlayDecidesEveryTurnOfBothSides()
        {
            game.RequireGame();
            var scenario = Skirmish("party", "enemies");
            scenario.TurnBased = true;
            using var environment = BattleEnvironment.Start(scenario, seed: 1, new BattleEnvironmentOptions { MaxSteps = 80 });
            var combat = Kingmaker.Game.Instance.TurnBasedCombatController;
            var recorder = new CommandRecorder();
            environment.Battle.Subscribe(recorder);

            var result = environment.Step(BattleAction.Wait("fighter"), BattleAction.Wait("cultist"));
            var turns = new HashSet<string>(StringComparer.Ordinal);
            while (!result.Ended)
            {
                // One unit acts at a time: the one whose turn the game runs.
                var ready = Assert.Single(result.Observation.Ready);
                Assert.Same(environment.Battle.Unit(ready), combat.CurrentTurn?.Rider);
                turns.Add(ready);
                result = environment.Step(FightInTurns(result.Observation));
            }
            Assert.False(result.Truncated, environment.Record.ToString());
            Assert.Equal(new[] { "cultist", "fighter" }, turns.OrderBy(id => id, StringComparer.Ordinal).ToArray());
            // The game's AI gave no command: the cultist's commands came from the decision maker.
            Assert.Contains(recorder.Acted, c => c.Executor == environment.Battle.Unit("cultist"));
            Assert.DoesNotContain(recorder.Acted, c => c.ByAi);
        }
    }
}
