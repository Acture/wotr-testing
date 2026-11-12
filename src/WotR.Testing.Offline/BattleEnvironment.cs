using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.View;
using TurnBased.Controllers;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// A decision loop over an <see cref="OfflineBattle"/> for an external decision maker, for example a training
    /// program: <see cref="Reset"/> rebuilds the scenario, <see cref="Observe"/> reports the battle and the actions the
    /// game allows now, and <see cref="Step(IEnumerable{BattleAction})"/> gives units the game's own player commands
    /// and runs game frames until the next decision point. The rules are the game's; the environment only chooses
    /// when to stop the frames and what to report.
    /// </summary>
    /// <remarks>
    /// The decision maker controls the units of the scenario's <see cref="BattleScenario.ControlledSides"/>; the game's
    /// AI decides for every other unit. A controlled unit is ready when it is conscious, the game lets it act and it has
    /// no command left. A step ends after the first frame in which a controlled unit that was busy becomes ready, after
    /// the frame that decides the battle, or after
    /// <see cref="BattleEnvironmentOptions.DecisionInterval"/>. An error during a step (the game logged an error, a
    /// frame failed) is an environment failure with the episode and step attached, never an outcome: the episode
    /// stops until <see cref="Reset"/>.
    /// </remarks>
    public sealed class BattleEnvironment : IDisposable
    {
        /// <summary>Source of the paths <see cref="Observe"/> requests from the game for ready units.</summary>
        public const string GamePathSource = "game:UnitMovementAgent.FindPath";

        /// <summary>Prefix of the ids of the game's paths, <c>game:unit-&gt;target</c>; scenario paths may not use it.</summary>
        public const string GamePathPrefix = "game:";

        private readonly OfflineBattle battle;
        private BattleEpisodeRecord record;
        private int step;
        private BattleOutcome outcome;
        private bool truncated;
        private BattleEnvironmentException failure;
        private bool disposed;

        private BattleEnvironment(OfflineBattle battle, BattleEnvironmentOptions options)
        {
            this.battle = battle;
            Options = options;
        }

        /// <summary>
        /// Builds the scenario as episode 1. The scenario needs <see cref="BattleScenario.Area"/>: steps run game frames.
        /// </summary>
        public static BattleEnvironment Start(BattleScenario scenario, int seed = GameRandom.DefaultSeed, BattleEnvironmentOptions options = null)
        {
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            if (scenario.Area == null)
                throw new ArgumentException($"Scenario {scenario.Name} has no Area. A battle environment runs game frames, which need the loaded area's navigation graph.", nameof(scenario));
            if (scenario.ControlledSides.Count == 0)
                throw new ArgumentException($"Scenario {scenario.Name} controls no side. Name the sides the decision maker controls in BattleScenario.ControlledSides; the game's AI decides for the others.", nameof(scenario));
            var reserved = scenario.Paths.FirstOrDefault(p => p?.Id != null && p.Id.StartsWith(GamePathPrefix, StringComparison.Ordinal));
            if (reserved != null)
                throw new ArgumentException($"Scenario {scenario.Name} path {reserved.Id}: ids starting with \"{GamePathPrefix}\" name the game's paths in observations.", nameof(scenario));
            options ??= new BattleEnvironmentOptions();
            options.Validate();
            var battle = OfflineBattle.Start(scenario, seed);
            try
            {
                var environment = new BattleEnvironment(battle, options);
                environment.BeginEpisode();
                return environment;
            }
            catch
            {
                battle.Dispose();
                throw;
            }
        }

        public BattleEnvironmentOptions Options { get; }

        /// <summary>The battle underneath, for checks a consumer makes with the game's own objects.</summary>
        public OfflineBattle Battle => battle;

        /// <summary>Number of the current episode: 1 after <see cref="Start"/>, one more after every <see cref="Reset"/>.</summary>
        public int Episode => battle.Generation;

        /// <summary>Steps taken in the current episode.</summary>
        public int StepCount => step;

        /// <summary>What the current episode did so far, for regression records and <see cref="Replay"/>.</summary>
        public BattleEpisodeRecord Record => record;

        /// <summary>
        /// Cleans up the episode and builds the scenario again, with the same seed unless another is given, and with
        /// another difficulty level (<see cref="BattleScenario.Difficulty"/>) when one is given, which the following episodes
        /// keep. A cleanup that leaves state behind throws <see cref="BattleCleanupException"/>; the process is then dirty
        /// and the environment cannot continue: start a new worker instead of trusting the reset.
        /// </summary>
        public BattleObservation Reset(int? seed = null, Kingmaker.Settings.GameDifficultyOption? difficulty = null)
        {
            ThrowIfDisposed();
            if (difficulty == Kingmaker.Settings.GameDifficultyOption.Custom)
                throw new ArgumentException("Choose a difficulty level; Custom is what the game shows after individual settings change.", nameof(difficulty));
            if (difficulty != null) battle.Scenario.Difficulty = difficulty;
            try
            {
                battle.Reset(seed);
            }
            catch
            {
                disposed = true;
                throw;
            }
            BeginEpisode();
            return Observe();
        }

        /// <summary>
        /// The battle now: every unit, the paths a ready unit can take and the actions the game allows. Paths come from
        /// the scenario (as supplied) and from the game: for each ready unit that can move, the path the game's
        /// <c>UnitMovementAgent.FindPath</c> finds to every other unit that is not dead, calculated at once with
        /// <c>AstarPath.BlockUntilCalculated</c> as the game's AI does. Reading paths changes no unit.
        /// </summary>
        public BattleObservation Observe()
        {
            ThrowIfDisposed();
            return Capture();
        }

        public BattleStepResult Step(params BattleAction[] actions) => Step((IEnumerable<BattleAction>)actions);

        /// <summary>
        /// Gives each listed ready unit its action with the game's player commands, then runs game frames until the next
        /// decision point. Units not listed keep what they are doing; a ready unit that waits stays idle. Actions that are
        /// not among <see cref="BattleObservation.Candidates"/> are rejected before anything changes
        /// (<see cref="BattleActionRejectedException"/>). An error while the step runs throws
        /// <see cref="BattleEnvironmentException"/>.
        /// </summary>
        public BattleStepResult Step(IEnumerable<BattleAction> actions) => Step(actions, null);

        /// <summary>
        /// <see cref="Step(IEnumerable{BattleAction})"/> on a new random stream when <paramref name="randomSeed"/> has a
        /// value: before the actions are given, the game's random numbers restart from that seed, so this step's dice, the
        /// game's AI and every later draw of the episode come from it instead of continuing the episode's own stream. A
        /// rollout rebuilds a state by replaying the episode, which reaches the same random state as the episode itself;
        /// without a new stream it would roll exactly the dice the episode's own next step rolls. The step record keeps the
        /// seed and <see cref="Replay"/> restarts the stream at the same step. The seed must not be negative.
        /// </summary>
        public BattleStepResult Step(IEnumerable<BattleAction> actions, int? randomSeed)
        {
            ThrowIfDisposed();
            if (actions == null) throw new ArgumentNullException(nameof(actions));
            if (randomSeed < 0) throw new ArgumentOutOfRangeException(nameof(randomSeed), randomSeed, "A random seed must not be negative.");
            if (failure != null)
                throw new InvalidOperationException($"Episode {Episode} of {battle.Scenario.Name} failed at step {failure.Step}; Reset before the next step.", failure);
            if (outcome != null || truncated)
                throw new InvalidOperationException($"Episode {Episode} of {battle.Scenario.Name} has ended ({(outcome != null ? outcome.Reason : "step limit reached")}); Reset before the next step.");
            var list = actions.ToList();
            var number = step + 1;
            var before = Capture();
            var problems = Reject(list, before);
            if (problems.Count == 0) problems.AddRange(PointProblems(list));
            if (problems.Count > 0) throw new BattleActionRejectedException(battle.Scenario.Name, Episode, number, list, problems);

            var beforeState = battle.Observe();
            var walked = battle.Units.ToDictionary(u => u.Id, _ => 0.0, StringComparer.Ordinal);
            var last = battle.Units.ToDictionary(u => u.Id, u => u.Unit.Position, StringComparer.Ordinal);
            void AfterFrame()
            {
                foreach (var entry in battle.Units)
                {
                    var position = entry.Unit.Position;
                    var previous = last[entry.Id];
                    walked[entry.Id] += new UnityEngine.Vector2(position.x - previous.x, position.z - previous.z).magnitude;
                    last[entry.Id] = position;
                }
            }
            string decision = null;
            TimeSpan elapsed;
            try
            {
                var start = Game.Instance.Player.GameTime;
                if (randomSeed is { } seed) GameRandom.Init(seed);
                var notes = new List<string>();
                // Toggles first, as the player switches them before giving the command.
                foreach (var action in list.Where(a => a.Kind == BattleActionKind.Toggle).Concat(list.Where(a => a.Kind != BattleActionKind.Toggle)))
                    if (Issue(action, before, AfterFrame) is { } note) notes.Add(note);
                // After its clicks the player moves the mouse off the units: until the next decision the pointer hovers
                // nothing, so the game's predictions for the unit under it do not follow a target that dies there.
                BattlePointer.Leave();
                // In real time a unit that waits, or only switches toggles, stays ready and idle until the decision interval;
                // a busy unit is one that runs a command or was given another action (a click the game refused decides
                // again after a frame). In turn-based
                // combat a unit given an action is busy until the game waits for it again, even when the game ignored it;
                // a unit that ends its turn waits for its next turn (one the player does not control stays idle until the
                // game ends its turn).
                var turnAtStart = InTurnBasedCombat ? Game.Instance.TurnBasedCombatController.CurrentTurn : null;
                var busy = battle.Units.Where(u => u.Controlled && u.Unit.Descriptor.State.IsConscious
                    && (turnAtStart == null
                        ? !IsReady(u.Unit) || list.Any(a => a.Unit == u.Id && a.Kind != BattleActionKind.Wait && a.Kind != BattleActionKind.Toggle)
                        : list.Any(a => a.Unit == u.Id && a.Kind != BattleActionKind.EndTurn))).ToList();
                var realTime = TimeSpan.Zero;
                var stalled = TimeSpan.Zero;
                battle.RunFrames(battle.Scenario.TurnBased ? Options.TurnBasedStepLimit : Options.DecisionInterval, () =>
                {
                    var ended = Decide(ObserveUnits());
                    if (ended != null)
                    {
                        outcome = ended;
                        decision = "the battle was decided: " + ended.Reason;
                        return true;
                    }
                    List<string> readyAgain;
                    if (InTurnBasedCombat)
                    {
                        // The game's own dead end: a second of frames is enough to tell it from a turn about to start.
                        if (SurpriseRoundStall() is { } stall)
                        {
                            stalled += TimeSpan.FromSeconds(Game.Instance.TimeController.DeltaTime);
                            if (stalled >= TimeSpan.FromSeconds(1)) throw new TurnBasedStallException(stall);
                        }
                        else
                        {
                            stalled = TimeSpan.Zero;
                        }
                        // A decision point when the game waits for a controlled unit's command: on a turn that began during
                        // this step, or again on the turn of a unit that acted in it.
                        var newTurn = !ReferenceEquals(Game.Instance.TurnBasedCombatController.CurrentTurn, turnAtStart);
                        readyAgain = battle.Units.Where(u => u.Controlled && IsReady(u.Unit) && (newTurn || busy.Contains(u))).Select(u => u.Id).ToList();
                    }
                    else
                    {
                        realTime += TimeSpan.FromSeconds(Game.Instance.TimeController.DeltaTime);
                        if (realTime >= Options.DecisionInterval) return true;
                        readyAgain = busy.Where(u => IsReady(u.Unit)).Select(u => u.Id).ToList();
                    }
                    if (readyAgain.Count == 0) return false;
                    decision = string.Join(", ", readyAgain) + (readyAgain.Count == 1 ? " is" : " are") + " ready";
                    return true;
                }, AfterFrame);
                elapsed = Game.Instance.Player.GameTime - start;
                if (decision == null && InTurnBasedCombat)
                    throw new TimeoutException($"No controlled unit's turn waited for a command within {Options.TurnBasedStepLimit} of frame time.");
                decision ??= "the decision interval passed";
                if (notes.Count > 0) decision = string.Join("; ", notes) + "; " + decision;
            }
            catch (Exception error)
            {
                failure = new BattleEnvironmentException(battle.Scenario.Name, Episode, number, list, error);
                record.Fail(number, list, randomSeed, failure.Message);
                BattleWorker.NoteEpisodeFailure();
                throw failure;
            }

            step = number;
            if (outcome == null && step >= Options.MaxSteps) truncated = true;
            var after = Capture();
            var afterState = battle.Observe();
            var effects = beforeState.Difference(afterState);
            var walkedDistances = walked.Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var reward = Options.Reward?.Invoke(new BattleTransition(before, list, after, effects)) ?? 0.0;
            var result = new BattleStepResult(Episode, step, list, elapsed, decision, effects, walkedDistances, after, outcome,
                truncated, truncated ? $"the step limit of {Options.MaxSteps} was reached" : null, reward);
            record.Add(new BattleStepRecord(step, list, randomSeed, elapsed, decision, afterState.Lines));
            if (outcome != null || truncated) record.End(outcome, truncated);
            BattleWorker.NoteEpisodeStep(outcome != null || truncated);
            return result;
        }

        /// <summary>
        /// Resets to the record's seed and takes its steps again, with their random seeds, comparing the battle's key state
        /// after each step. Returns the differences; an empty list means the episode was reproduced.
        /// </summary>
        public IReadOnlyList<string> Replay(BattleEpisodeRecord episode)
        {
            ThrowIfDisposed();
            if (episode == null) throw new ArgumentNullException(nameof(episode));
            if (episode.Scenario != battle.Scenario.Name)
                throw new ArgumentException($"The record is of scenario {episode.Scenario}, this environment runs {battle.Scenario.Name}.", nameof(episode));
            var differences = new List<string>();
            Reset(episode.Seed, episode.Difficulty);
            differences.AddRange(Compare("initial state", episode.InitialState, record.InitialState));
            foreach (var expected in episode.Steps)
            {
                if (differences.Count > 0) break;
                if (expected.Error != null)
                {
                    try
                    {
                        Step(expected.Actions, expected.RandomSeed);
                        differences.Add($"step {expected.Step}: the record failed with {expected.Error}, the replay did not fail");
                    }
                    catch (BattleEnvironmentException) { }
                    break;
                }
                BattleStepResult actual;
                try
                {
                    actual = Step(expected.Actions, expected.RandomSeed);
                }
                catch (Exception error)
                {
                    differences.Add($"step {expected.Step}: {error.GetType().Name}: {error.Message}");
                    break;
                }
                if (actual.Decision != expected.Decision) differences.Add($"step {expected.Step}: decision \"{actual.Decision}\", recorded \"{expected.Decision}\"");
                if (actual.Elapsed != expected.Elapsed) differences.Add($"step {expected.Step}: elapsed {actual.Elapsed}, recorded {expected.Elapsed}");
                differences.AddRange(Compare("step " + expected.Step, expected.State, record.Steps[record.Steps.Count - 1].State));
            }
            return differences;
        }

        private static IEnumerable<string> Compare(string where, IReadOnlyList<string> expected, IReadOnlyList<string> actual)
        {
            if (expected.SequenceEqual(actual, StringComparer.Ordinal)) yield break;
            foreach (var line in expected.Except(actual, StringComparer.Ordinal)) yield return where + ": - " + line;
            foreach (var line in actual.Except(expected, StringComparer.Ordinal)) yield return where + ": + " + line;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            battle.Dispose();
        }

        private void BeginEpisode()
        {
            step = 0;
            truncated = false;
            failure = null;
            outcome = Decide(ObserveUnits());
            record = new BattleEpisodeRecord(battle.Scenario.Name, Episode, battle.Seed,
                OfflineRuntime.Inputs?.Identity?.GameVersion, LibraryVersion, battle.InitialState.Lines, battle.Scenario.Difficulty);
            if (outcome != null) record.End(outcome, false);
            BattleWorker.NoteEpisode();
        }

        private static string LibraryVersion
            => typeof(BattleEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(BattleEnvironment).Assembly.GetName().Version?.ToString();

        private BattleOutcome Decide(IReadOnlyList<BattleUnitObservation> units)
        {
            if (Options.Outcome != null) return Options.Outcome(units);
            var standing = units.Where(u => u.Conscious).Select(u => u.Side).Distinct(StringComparer.Ordinal).ToList();
            if (standing.Count > 1) return null;
            return standing.Count == 1
                ? new BattleOutcome(standing[0], $"only side {standing[0]} has a conscious unit")
                : new BattleOutcome(null, "no side has a conscious unit");
        }

        /// <summary>True while the game runs combat in turns (its turn-based setting is on and the party is in combat).</summary>
        private static bool InTurnBasedCombat
            => CombatController.IsInTurnBasedCombat() && Game.Instance.TurnBasedCombatController.Initialized;

        private static readonly FieldInfo NextUnit = HarmonyLib.AccessTools.Field(typeof(CombatController), "m_NextUnit");

        /// <summary>
        /// Why the game's turn-based combat can start no turn, or null. In the surprise round the game's
        /// <c>CombatController.ChooseNextUnit</c> skips surprised units and units acting in the surprise round without an
        /// offensive command; when it skips every unit it still chooses the last one. If that unit is surprised, its turn
        /// waits for its initiative, which <c>UnitCombatCooldownsController</c> does not count down for surprised units, so
        /// no turn ever starts. This reads the game's state; the game's code, not the environment, ends in it.
        /// </summary>
        private static string SurpriseRoundStall()
        {
            var combat = Game.Instance.TurnBasedCombatController;
            if (combat.CurrentTurn != null || combat.RoundNumber != 0) return null;
            if (NextUnit.GetValue(combat) is not UnitEntityData next || !combat.IsSurprised(next) || next.GetTimeToNextTurn() <= 0f) return null;
            return $"the game's turn-based combat starts no turn: in the surprise round it skipped every unit and chose {next.CharacterName} ({next.UniqueId}), "
                + $"which is surprised, so its initiative ({next.GetTimeToNextTurn():0.##} s to its turn) does not count down "
                + "(CombatController.ChooseNextUnit, UnitCombatCooldownsController). Units acting in the surprise round are skipped without an offensive command. "
                + "The game lets the party surprise the enemies when the seconds part of the time since a party member's last offensive action out of combat is "
                + "below 6, which for a member that never made one holds in the first six seconds of every game minute (BattleScenario.StartTime); "
                + "only the main character and BattleScenario.Companions count as the party.";
        }

        /// <summary>
        /// In real time a unit is ready when it is conscious, the game lets it act and it has no command left. In
        /// turn-based combat only the unit whose turn it is acts: it is ready while the game's TurnController waits for its
        /// command (the turn is preparing or acting, and nothing of the unit runs).
        /// </summary>
        private static bool IsReady(UnitEntityData unit)
        {
            if (!unit.Descriptor.State.IsConscious || !unit.Commands.Empty) return false;
            if (!InTurnBasedCombat) return unit.Descriptor.State.CanAct;
            var turn = Game.Instance.TurnBasedCombatController.CurrentTurn;
            return turn != null && (turn.Rider == unit || turn.Mount == unit)
                && (turn.Status == TurnController.TurnStatus.Preparing || turn.Status == TurnController.TurnStatus.Acting)
                && !turn.HasRunningCommands;
        }

        private IReadOnlyList<BattleUnitObservation> ObserveUnits()
            => battle.Units.Select(entry => ObserveUnit(entry, entry.Controlled && IsReady(entry.Unit))).ToList().AsReadOnly();

        private BattleObservation Capture()
        {
            var units = ObserveUnits();
            var ended = outcome != null || truncated || failure != null;
            var scenarioPaths = battle.Paths.Select(path => ObserveScenarioPath(path, ended)).ToList();
            var paths = new List<BattlePathObservation>(scenarioPaths);
            var candidates = new List<BattleAction>();
            if (!ended)
            {
                var turnBased = InTurnBasedCombat;
                foreach (var entry in battle.Units.Where(u => u.Controlled && IsReady(u.Unit)))
                {
                    var unit = entry.Unit;
                    if (!turnBased) candidates.Add(BattleAction.Wait(entry.Id));
                    // The player ends a turn with the game's end-turn key when the turn allows it; a unit the player does not
                    // control ends its turn by giving no command.
                    else if (!unit.IsDirectlyControllable || Game.Instance.TurnBasedCombatController.CurrentTurn.CanEndTurnAndNoActing())
                        candidates.Add(BattleAction.EndTurn(entry.Id));
                    // The player's smart cursor in turn-based combat: attack variants, movement limits, delaying the turn.
                    var cursor = turnBased && unit.IsDirectlyControllable ? Game.Instance.TurnBasedCombatController.CurrentTurn : null;
                    foreach (var target in battle.Units)
                    {
                        if (!CanAttack(unit, target.Unit) || (turnBased && !CanStartInTurn(unit, target.Unit))) continue;
                        candidates.Add(BattleAction.Attack(entry.Id, target.Id));
                        if (cursor != null)
                            foreach (var variant in BattleSmartCursor.AttackCandidates(cursor, unit, WithinReach(unit, target.Unit)))
                                candidates.Add(BattleAction.Attack(entry.Id, target.Id, variant));
                    }
                    candidates.AddRange(AbilityCandidates(entry, turnBased));
                    if (cursor != null && cursor.CanDelay())
                    {
                        var tracker = Game.Instance.TurnBasedCombatController.SortedUnits.ToList();
                        foreach (var target in battle.Units.Where(t => t.Unit != unit && tracker.Contains(t.Unit)))
                            candidates.Add(BattleAction.Delay(entry.Id, target.Id));
                    }
                    if (!unit.Descriptor.State.CanMove) continue;
                    foreach (var target in battle.Units.Where(t => t != entry && !t.Unit.Descriptor.State.IsDead))
                    {
                        var path = FindGamePath(entry, target);
                        paths.Add(path);
                        // In turn-based combat a move is a click on the ground; the ground under a unit is the unit.
                        if (path.Usable && !turnBased) candidates.Add(BattleAction.Move(entry.Id, path.Id));
                    }
                    var limits = cursor == null ? new List<string>() : BattleSmartCursor.GroundLimits(cursor, unit).ToList();
                    foreach (var path in scenarioPaths.Where(p => p.Usable && p.Unit == entry.Id))
                    {
                        candidates.Add(BattleAction.Move(entry.Id, path.Id));
                        if (limits.Count > 1)
                            foreach (var limit in limits) candidates.Add(BattleAction.Move(entry.Id, path.Id, limit));
                    }
                    // Any point on the ground the game's pathfinding reaches, checked when it is given.
                    candidates.Add(BattleAction.MoveToAnyPoint(entry.Id));
                    if (limits.Count > 1)
                        foreach (var limit in limits) candidates.Add(BattleAction.MoveToAnyPoint(entry.Id, limit));
                }
            }
            return new BattleObservation(battle.Scenario.Name, Episode, step, battle.Seed, Game.Instance.Player.GameTime, units,
                paths.AsReadOnly(), candidates.AsReadOnly(), outcome, truncated, failure?.Message, BattleDifficultyObservation.Capture());
        }

        /// <summary>
        /// Turn-based combat: whether the command a click on the target gives (ClickUnitHandler.CreateCommand) can still act
        /// this turn, as the game decides. The game starts a turn-based command only when the unit can act and is not
        /// nauseated, and the command ignores cooldowns or the unit can act in combat and has the action the command takes
        /// (UnitCommandController.TickCommandTurnBased); otherwise it finishes the command unstarted. The target must also be
        /// within the command's reach, or the turn must leave the unit movement to approach it (TurnController.HasMovement),
        /// the rule the game's AI scores attacks by and its UI marks out of range. The command is created and initialized for
        /// the unit, never run.
        /// </summary>
        private static bool CanStartInTurn(UnitEntityData unit, UnitEntityData target)
            => CanStartInTurn(unit, Kingmaker.Controllers.Clicks.Handlers.ClickUnitHandler.CreateCommand(unit, target.View));

        /// <summary>Whether the command a click on the target gives needs no movement to act.</summary>
        private static bool WithinReach(UnitEntityData unit, UnitEntityData target)
        {
            var command = Kingmaker.Controllers.Clicks.Handlers.ClickUnitHandler.CreateCommand(unit, target.View);
            command.Init(unit);
            return command.IsUnitCloseEnough();
        }

        private static bool CanStartInTurn(UnitEntityData unit, Kingmaker.UnitLogic.Commands.Base.UnitCommand command)
        {
            var state = unit.Descriptor.State;
            if (!state.CanAct || state.HasCondition(Kingmaker.UnitLogic.UnitCondition.Nauseated)) return false;
            if (!command.IsIgnoreCooldown && (!unit.CombatState.CanActInCombat || unit.CombatState.HasCooldownForCommand(command))) return false;
            command.Init(unit);
            return command.IsUnitCloseEnough() || Game.Instance.TurnBasedCombatController.CurrentTurn.HasMovement(unit);
        }

        /// <summary>
        /// The unit's action bar entries (BattleAbilities.Collect) it can use now, each on every unit the ability accepts as
        /// a target (AbilityData.CanTarget, as a click with the selected ability checks it): for a unit the player controls,
        /// as the action bar allows it (IsPossibleActive, which in turn-based combat checks the turn's actions); for one the
        /// player does not control, by the slot's own availability. Abilities without target selection act on the caster.
        /// In turn-based combat the command must also be able to start this turn, as for attacks. Toggles switch on or off.
        /// </summary>
        private IEnumerable<BattleAction> AbilityCandidates(BattleUnit entry, bool turnBased)
        {
            var unit = entry.Unit;
            var asPlayer = unit.IsDirectlyControllable || !turnBased;
            foreach (var slot in BattleAbilities.Collect(unit))
            {
                if (!(asPlayer ? BattleAbilities.PlayerCanUse(slot) : BattleAbilities.AvailableToUnit(slot))) continue;
                if (slot.Toggle != null)
                {
                    yield return BattleAction.Toggle(entry.Id, slot.Id);
                    continue;
                }
                if (slot.Ability == null) continue;
                if (slot.Ability.TargetAnchor == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityTargetAnchor.Owner)
                {
                    if (!turnBased || CanStartInTurn(unit, BattleAbilities.CommandFor(slot.Ability, unit)))
                        yield return BattleAction.UseAbility(entry.Id, slot.Id);
                    continue;
                }
                // A point ability also takes any point on the ground, which the game checks when it is given.
                if (slot.Ability.TargetAnchor == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityTargetAnchor.Point)
                    yield return BattleAction.UseAbilityAtAnyPoint(entry.Id, slot.Id);
                foreach (var target in battle.Units)
                {
                    var wrapper = BattleAbilities.TargetOn(slot.Ability, target.Unit);
                    if (!slot.Ability.CanTarget(wrapper)) continue;
                    if (turnBased && !CanStartInTurn(unit, BattleAbilities.CommandFor(slot.Ability, wrapper))) continue;
                    yield return BattleAction.UseAbility(entry.Id, slot.Id, target.Id);
                }
            }
        }

        /// <summary>The game's player attack checks (ClickUnitHandler): a unit it may attack, and a weapon to attack with.</summary>
        private static bool CanAttack(UnitEntityData unit, UnitEntityData target)
            => target != unit && !target.Descriptor.State.IsDead && unit.CanAttack(target)
                && unit.CanAttack(u => u.GetThreatHand()?.Weapon);

        private static BattleUnitObservation ObserveUnit(BattleUnit entry, bool ready)
        {
            var unit = entry.Unit;
            var descriptor = unit.Descriptor;
            var state = descriptor.State;
            var now = Game.Instance.Player.GameTime;
            var buffs = unit.Buffs.RawFacts
                .Select(b => new BattleBuffObservation(b.Blueprint.name, b.Blueprint.AssetGuid.ToString(), b.GetRank(), b.IsPermanent ? null : b.EndTime - now))
                .OrderBy(b => b.Name, StringComparer.Ordinal).ToList().AsReadOnly();
            var resources = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var resource in descriptor.Resources) resources[resource.name] = descriptor.Resources.GetResourceAmount(resource);
            var abilities = unit.Abilities.RawFacts.Select(a => a.Blueprint.name).OrderBy(x => x, StringComparer.Ordinal).ToList().AsReadOnly();
            var spellSlots = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var book in descriptor.Spellbooks)
                for (var level = 1; level <= book.MaxSpellLevel; level++)
                    spellSlots[book.Blueprint.name + ":" + level] = book.Blueprint.Spontaneous
                        ? book.GetSpontaneousSlots(level)
                        : book.GetMemorizedSpells(level).Count(slot => slot.Available);
            var togglesOn = unit.ActivatableAbilities.RawFacts.Where(a => a.IsOn).Select(a => a.Blueprint.name).OrderBy(x => x, StringComparer.Ordinal).ToList().AsReadOnly();
            var command = unit.Commands.Raw.FirstOrDefault(c => c != null)?.GetType().Name;
            var cooldown = unit.CombatState.Cooldown;
            var position = unit.Position;
            return new BattleUnitObservation(entry.Id, entry.Side, entry.Controlled, unit.Blueprint.name, unit.Blueprint.AssetGuid.ToString(),
                descriptor.Faction?.name, unit.HPLeft, descriptor.MaxHP, descriptor.TemporaryHP, state.LifeState.ToString(),
                state.IsConscious, state.CanAct, state.CanMove, ready, unit.IsInCombat, new BattlePoint(position.x, position.y, position.z),
                unit.Orientation, unit.Corpulence, unit.CurrentSpeedMps, command, cooldown.StandardAction, cooldown.MoveAction,
                cooldown.SwiftAction, buffs, resources, abilities, spellSlots, togglesOn, BattleAbilities.ItemUses(unit));
        }

        /// <summary>
        /// A scenario path as supplied. It is usable by the unit at its start while that unit is ready and can move, and
        /// while it still starts where the unit stands (within the unit's corpulence, at least 0.5 m).
        /// </summary>
        private BattlePathObservation ObserveScenarioPath(BattlePath path, bool ended)
        {
            var points = path.Points;
            var destination = points[points.Count - 1];
            string problem = null;
            if (ended) problem = "the episode has ended";
            else if (path.From == null) problem = "the path names no unit at its start";
            else if (!BattleControl.IsControlled(path.From)) problem = $"{path.FromId} is decided by the game's AI";
            else if (!IsReady(path.From)) problem = $"{path.FromId} is not ready";
            else if (!path.From.Descriptor.State.CanMove) problem = $"{path.FromId} cannot move";
            else
            {
                var start = points[0];
                var position = path.From.Position;
                var offset = new UnityEngine.Vector2(position.x - start.X, position.z - start.Z).magnitude;
                var tolerance = Math.Max(0.5f, path.From.Corpulence);
                if (offset > tolerance)
                    problem = FormattableString.Invariant($"the path starts {offset:0.##} m from {path.FromId}, more than {tolerance:0.##} m");
            }
            return new BattlePathObservation(path.Id, path.Source, path.FromId, path.ToId, points, destination, path.Distance,
                path.Cost, path.Units, problem);
        }

        /// <summary>The path the game's agent finds from the unit to the target's position, as the game's AI requests it.</summary>
        private BattlePathObservation FindGamePath(BattleUnit entry, BattleUnit target)
        {
            var id = GamePathPrefix + entry.Id + "->" + target.Id;
            var targetPosition = target.Unit.Position;
            var destination = new BattlePoint(targetPosition.x, targetPosition.y, targetPosition.z);
            if (entry.Unit.View?.AgentASP is not UnitMovementAgent agent)
                return new BattlePathObservation(id, GamePathSource, entry.Id, target.Id, Array.Empty<BattlePoint>(), destination, null, null, "meters", $"{entry.Id} has no movement agent");
            var path = agent.FindPath(targetPosition, _ => { });
            if (path == null)
                return new BattlePathObservation(id, GamePathSource, entry.Id, target.Id, Array.Empty<BattlePoint>(), destination, null, null, "meters", $"{entry.Id}'s agent is already finding a path");
            AstarPath.BlockUntilCalculated(path);
            if (path.error || path.CompleteState != Pathfinding.PathCompleteState.Complete || path.vectorPath.Count < 2)
                return new BattlePathObservation(id, GamePathSource, entry.Id, target.Id, Array.Empty<BattlePoint>(), destination, null, null, "meters",
                    "the game's pathfinding found no path" + (string.IsNullOrEmpty(path.errorLog) ? "" : ": " + path.errorLog.Trim()));
            var points = path.vectorPath.Select(p => new BattlePoint(p.x, p.y, p.z)).ToList().AsReadOnly();
            return new BattlePathObservation(id, GamePathSource, entry.Id, target.Id, points, points[points.Count - 1], path.GetTotalLength(), null, "meters", null);
        }

        private static List<string> Reject(IReadOnlyList<BattleAction> actions, BattleObservation observation)
        {
            var problems = new List<string>();
            // A player switches toggles and gives a command in the same moment: a unit takes one action besides its toggles.
            foreach (var group in actions.Where(a => a != null).GroupBy(a => a.Unit, StringComparer.Ordinal))
            {
                var main = group.Count(a => a.Kind != BattleActionKind.Toggle);
                if (main > 1) problems.Add($"{group.Key} has {main} actions besides toggles; a step takes at most one per unit");
                foreach (var toggle in group.Where(a => a.Kind == BattleActionKind.Toggle).GroupBy(a => a.Ability, StringComparer.Ordinal).Where(g => g.Count() > 1))
                    problems.Add($"{group.Key} switches {toggle.Key} {toggle.Count()} times in one step");
            }
            foreach (var action in actions)
            {
                if (action == null)
                {
                    problems.Add("an action is null");
                    continue;
                }
                if (action.AnyPoint)
                {
                    problems.Add($"{action}: give the point with At(point)");
                    continue;
                }
                if (observation.Candidates.Contains(action) || observation.Candidates.Any(action.IsAt)) continue;
                var unit = observation.Units.FirstOrDefault(u => u.Id == action.Unit);
                if (unit == null) problems.Add($"{action}: the battle has no unit {action.Unit}");
                else if (observation.Outcome != null || observation.Truncated) problems.Add($"{action}: the episode has ended");
                else if (!unit.Controlled) problems.Add($"{action}: side {unit.Side} is not controlled; the game's AI decides for {action.Unit}");
                else if (!unit.Ready) problems.Add($"{action}: {action.Unit} is not ready ({Describe(unit)})");
                else if (action.Kind == BattleActionKind.Move && observation.Paths.FirstOrDefault(p => p.Id == action.Path && p.Unit == action.Unit) is { Usable: false } path)
                    problems.Add($"{action}: path {path.Id} is not usable: {path.Problem}");
                else problems.Add($"{action}: not among the actions the game allows {action.Unit} now");
            }
            return problems;
        }

        /// <summary>
        /// The game's checks of the points the decision maker gave for any-point candidates: a move needs a path the game's
        /// agent finds; an entry used at a point needs the ability to accept it (AbilityData.CanTarget, as a click on the
        /// ground checks it) and, in turn-based combat, a command that can start this turn there.
        /// </summary>
        private List<string> PointProblems(IEnumerable<BattleAction> actions)
        {
            var problems = new List<string>();
            foreach (var action in actions.Where(a => a.Point != null))
            {
                var unit = battle.Unit(action.Unit);
                var point = ToVector(action.Point.Value);
                if (action.Kind == BattleActionKind.MoveTo)
                {
                    if (FindForcedPath(unit, point, 0.3f, false) == null) problems.Add($"{action}: the game's pathfinding finds no path there");
                    continue;
                }
                var ability = BattleAbilities.Collect(unit).First(s => s.Id == action.Ability).Ability;
                var target = BattleAbilities.TargetAt(ability, point);
                if (!ability.CanTarget(target)) problems.Add($"{action}: the ability does not accept that point (AbilityData.CanTarget)");
                else if (InTurnBasedCombat && !CanStartInTurn(unit, BattleAbilities.CommandFor(ability, target)))
                    problems.Add($"{action}: the command cannot start there this turn");
            }
            return problems;
        }

        private static UnityEngine.Vector3 ToVector(BattlePoint point) => new(point.X, point.Y, point.Z);

        private static string Describe(BattleUnitObservation unit)
            => !unit.Conscious ? unit.LifeState.ToLowerInvariant()
                : !unit.CanAct ? "the game does not let it act"
                : unit.Command != null ? "running " + unit.Command
                : "not ready";

        /// <summary>
        /// Gives the action with the game's own input. Returns a note when the game did not take it (a turn-based click it
        /// ignored), otherwise null. <paramref name="afterFrame"/> sees the frame a turn-based click waits for.
        /// </summary>
        private string Issue(BattleAction action, BattleObservation observation, Action afterFrame)
        {
            if (action.Kind is BattleActionKind.UseAbility or BattleActionKind.Toggle) return IssueAbility(action, afterFrame);
            if (InTurnBasedCombat) return IssueTurnBased(action, observation, afterFrame);
            IssueRealTime(action, observation);
            return null;
        }

        /// <summary>
        /// Turn-based combat. A unit the player controls acts through the player's input: the end-turn key, or the pointer
        /// moved onto the target unit or the ground, a frame for the game to follow it (its predictions find the path and
        /// shorten it to the movement left), and a click, which the game's click handler turns into the command. A unit the
        /// player does not control (an enemy in self-play) gets the command the game's AI would give, with the path the
        /// game finds for it, as the AI's commands carry one in turn-based combat; it ends its turn by giving no command.
        /// </summary>
        private string IssueTurnBased(BattleAction action, BattleObservation observation, Action afterFrame)
        {
            var unit = battle.Unit(action.Unit);
            var player = unit.IsDirectlyControllable;
            switch (action.Kind)
            {
                case BattleActionKind.EndTurn:
                    // Game's end-turn key: CurrentTurn.ForceToEnd() when CanEndTurnAndNoActing().
                    if (player) Game.Instance.TurnBasedCombatController.CurrentTurn.ForceToEnd();
                    return null;
                case BattleActionKind.MoveTo:
                {
                    var destination = ToVector(action.Point.Value);
                    if (player && action.Variant != null)
                        return ClickWithVariant(action, null, destination, afterFrame,
                            turn => BattleSmartCursor.SelectLimit(turn, action.Variant), BattleSmartCursor.CurrentLimit,
                            turn => "it is at " + BattleSmartCursor.CurrentLimit(turn));
                    if (player) return Click(action, null, destination, afterFrame);
                    var command = new UnitMoveTo(destination, 0.3f);
                    command.ForcedPath = FindForcedPath(unit, destination, 0.3f, false);
                    unit.Commands.Run(command);
                    return null;
                }
                case BattleActionKind.Delay:
                    // The initiative tracker's delay (InitiativeTrackerVM.DelayInitiaive); its confirmation dialogs are UI.
                    Game.Instance.TurnBasedCombatController.CurrentTurn.DelayInitiaive(battle.Unit(action.Target));
                    return null;
                case BattleActionKind.Attack:
                {
                    var target = battle.Unit(action.Target);
                    if (player && action.Variant != null)
                        return ClickWithVariant(action, target.View.gameObject, target.Position, afterFrame,
                            turn => BattleSmartCursor.Select(turn, action.Variant), BattleSmartCursor.Current,
                            turn => "it offers " + (BattleSmartCursor.Enabled(turn) is { Count: > 0 } offered ? string.Join(", ", offered) : "none"));
                    if (player) return Click(action, target.View.gameObject, target.Position, afterFrame);
                    var command = Kingmaker.Controllers.Clicks.Handlers.ClickUnitHandler.CreateCommand(unit, target.View);
                    command.ForcedPath = FindForcedPath(unit, command.ApproachPoint, command.ApproachRadius, command.NeedLoS);
                    unit.Commands.Run(command);
                    unit.CombatState.ManualTarget = target;
                    return null;
                }
                case BattleActionKind.Move:
                {
                    var end = observation.Paths.First(p => p.Id == action.Path && p.Unit == action.Unit).Destination;
                    var destination = new UnityEngine.Vector3(end.X, end.Y, end.Z);
                    if (player && action.Variant != null)
                        return ClickWithVariant(action, null, destination, afterFrame,
                            turn => BattleSmartCursor.SelectLimit(turn, action.Variant), BattleSmartCursor.CurrentLimit,
                            turn => "it is at " + BattleSmartCursor.CurrentLimit(turn));
                    if (player) return Click(action, null, destination, afterFrame);
                    var command = new UnitMoveTo(destination, 0.3f);
                    command.ForcedPath = FindForcedPath(unit, destination, 0.3f, false);
                    unit.Commands.Run(command);
                    return null;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action.Kind, null);
            }
        }

        /// <summary>
        /// A unit the player controls uses the entry as the player does: the action bar slot's click, which casts an ability
        /// without target selection, switches a toggle or selects the ability; then the pointer on the target unit and a
        /// click, which the game's ClickWithSelectedAbilityHandler turns into the command (in turn-based combat after a frame
        /// for the game's predictions, as for attacks). A refused click leaves no ability selected, as the player's Escape. A
        /// unit the player does not control gets the command that click would give, with the game's path in turn-based
        /// combat, or its toggle switched as the slot switches it.
        /// </summary>
        private string IssueAbility(BattleAction action, Action afterFrame)
        {
            var unit = battle.Unit(action.Unit);
            var slot = BattleAbilities.Collect(unit).First(s => s.Id == action.Ability);
            var turnBased = InTurnBasedCombat;
            if (unit.IsDirectlyControllable)
            {
                slot.Slot.OnClick();
                if (slot.Toggle != null || slot.Ability.TargetAnchor == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityTargetAnchor.Owner) return null;
                var target = action.Point == null ? battle.Unit(action.Target) : null;
                var gameObject = target?.View.gameObject;
                var position = target?.Position ?? ToVector(action.Point.Value);
                string note;
                if (turnBased)
                {
                    note = Click(action, gameObject, position, afterFrame);
                }
                else
                {
                    BattlePointer.PointAt(gameObject, position);
                    note = BattlePointer.Click() ? null : $"the game ignored the click for {action}";
                }
                if (Game.Instance.SelectedAbilityHandler.SelectedAbility != null)
                {
                    Game.Instance.ClickEventsController.ClearPointerMode();
                    Game.Instance.SelectedAbilityHandler.DropAbility();
                }
                return note;
            }
            if (slot.Toggle != null)
            {
                slot.Toggle.IsOn = !slot.Toggle.IsOn;
                return null;
            }
            var wrapper = action.Point is { } point
                ? BattleAbilities.TargetAt(slot.Ability, ToVector(point))
                : BattleAbilities.TargetOn(slot.Ability, action.Target == null ? unit : battle.Unit(action.Target));
            var command = BattleAbilities.CommandFor(slot.Ability, wrapper);
            if (turnBased) command.ForcedPath = FindForcedPath(unit, command.ApproachPoint, command.ApproachRadius, command.NeedLoS);
            unit.Commands.Run(command);
            return null;
        }

        /// <summary>
        /// The player's smart cursor: the pointer on the target, a frame for the game's predictions (which enable the
        /// variants for that target), the player's key until the variant is current, a frame for the predictions of that
        /// variant, and the click. When the game does not offer the variant, nothing is clicked and the note says so.
        /// </summary>
        private string ClickWithVariant(BattleAction action, UnityEngine.GameObject target, UnityEngine.Vector3 position, Action afterFrame,
            Func<TurnController, bool> select, Func<TurnController, string> current, Func<TurnController, string> offered)
        {
            BattlePointer.PointAt(target, position);
            battle.RunFrames(TimeSpan.FromSeconds(1), () => true, afterFrame);
            var turn = Game.Instance.TurnBasedCombatController.CurrentTurn;
            if (turn == null || !select(turn))
                return $"the game does not offer {action.Variant} for {action}" + (turn == null ? "" : "; " + offered(turn));
            battle.RunFrames(TimeSpan.FromSeconds(1), () => true, afterFrame);
            if (current(turn) != action.Variant)
                return $"the game's predictions changed {action} to {current(turn)}; nothing was clicked";
            return BattlePointer.Click() ? null : $"the game ignored the click for {action}";
        }

        private string Click(BattleAction action, UnityEngine.GameObject target, UnityEngine.Vector3 position, Action afterFrame)
        {
            BattlePointer.PointAt(target, position);
            battle.RunFrames(TimeSpan.FromSeconds(1), () => true, afterFrame);
            return BattlePointer.Click() ? null : $"the game ignored the click for {action}";
        }

        /// <summary>The path the game's agent finds to the point, as the game's AI finds the path of its commands.</summary>
        private static Pathfinding.Path FindForcedPath(UnitEntityData unit, UnityEngine.Vector3 point, float approachRadius, bool needLos)
        {
            if (unit.View?.AgentASP is not UnitMovementAgent agent) return null;
            var path = agent.FindPath(point, _ => { }, approachRadius, needLos);
            if (path == null) return null;
            AstarPath.BlockUntilCalculated(path);
            return path.error || path.CompleteState != Pathfinding.PathCompleteState.Complete ? null : path;
        }

        /// <summary>The game's player commands: ClickUnitHandler's attack and approach, ClickGroundHandler's move.</summary>
        private void IssueRealTime(BattleAction action, BattleObservation observation)
        {
            var unit = battle.Unit(action.Unit);
            switch (action.Kind)
            {
                case BattleActionKind.Wait:
                    return;
                case BattleActionKind.Attack:
                {
                    var target = battle.Unit(action.Target);
                    // The command a click on the unit gives: its auto-use ability when it has one, else an attack.
                    unit.Commands.Run(Kingmaker.Controllers.Clicks.Handlers.ClickUnitHandler.CreateCommand(unit, target.View));
                    unit.CombatState.ManualTarget = target;
                    return;
                }
                case BattleActionKind.MoveTo:
                    // A click on the ground (ClickGroundHandler): the game's move command finds its own path there.
                    unit.Commands.Run(new UnitMoveTo(ToVector(action.Point.Value), 0.3f) { CreatedByPlayer = true });
                    return;
                case BattleActionKind.Move:
                {
                    var path = observation.Paths.First(p => p.Id == action.Path && p.Unit == action.Unit);
                    // In real time the game's move commands find their own path to the destination; only turn-based combat
                    // follows a precomputed path. Moving to a unit approaches it as a click on it does.
                    if (path.Target != null)
                    {
                        var target = battle.Unit(path.Target);
                        unit.Commands.Run(new UnitMoveTo(target.Position, target.Corpulence * 1.1f));
                    }
                    else
                    {
                        var end = path.Destination;
                        unit.Commands.Run(new UnitMoveTo(new UnityEngine.Vector3(end.X, end.Y, end.Z), 0.3f) { CreatedByPlayer = true });
                    }
                    return;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action.Kind, null);
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(BattleEnvironment), $"The environment of {battle.Scenario.Name} was disposed.");
        }
    }

    public sealed class BattleEnvironmentOptions
    {
        /// <summary>The longest game time one step runs without a decision point; the game's round is 6 seconds.</summary>
        public TimeSpan DecisionInterval { get; set; } = TimeSpan.FromSeconds(6);

        /// <summary>
        /// The longest frame time a turn-based step runs before a controlled unit's turn waits for a command; reaching it is
        /// an environment failure. In turn-based combat the game's clock stands still during turns, so the limit counts frame
        /// time.
        /// </summary>
        public TimeSpan TurnBasedStepLimit { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>Steps after which an episode is truncated (not decided) when it has not ended.</summary>
        public int MaxSteps { get; set; } = 200;

        /// <summary>
        /// Decides the episode from the units after each frame; null keeps it running. Without it, the episode ends when
        /// at most one side has a conscious unit.
        /// </summary>
        public Func<IReadOnlyList<BattleUnitObservation>, BattleOutcome> Outcome { get; set; }

        /// <summary>The reward of a step; without it every step's reward is 0. The training code decides what to optimize.</summary>
        public Func<BattleTransition, double> Reward { get; set; }

        internal void Validate()
        {
            if (DecisionInterval <= TimeSpan.Zero) throw new ArgumentException("DecisionInterval must be positive.");
            if (TurnBasedStepLimit <= TimeSpan.Zero) throw new ArgumentException("TurnBasedStepLimit must be positive.");
            if (MaxSteps < 1) throw new ArgumentException("MaxSteps must be at least 1.");
        }
    }

    public enum BattleActionKind
    {
        /// <summary>The unit stays idle until the next decision point.</summary>
        Wait,

        /// <summary>The player's attack on a unit (<c>UnitAttack.CreateAttackCommand</c>), which approaches it first when needed.</summary>
        Attack,

        /// <summary>The player's move along an observed path's destination (<c>UnitMoveTo</c>).</summary>
        Move,

        /// <summary>
        /// Turn-based combat: the end of the unit's turn, with the game's end-turn key for a unit the player controls; a unit
        /// the player does not control ends its turn by giving no command.
        /// </summary>
        EndTurn,

        /// <summary>
        /// A spell, ability or usable item from the unit's action bar (<see cref="BattleAction.Ability"/>), cast on a unit
        /// (<see cref="BattleAction.Target"/>; for point abilities, at its position) or, for abilities without target
        /// selection, on the caster (no target).
        /// </summary>
        UseAbility,

        /// <summary>Switches an ability of the unit's action bar on or off (<see cref="BattleAction.Ability"/>), such as Power Attack.</summary>
        Toggle,

        /// <summary>
        /// Turn-based combat: the player's unit delays its turn until after another unit's (<see cref="BattleAction.Target"/>),
        /// as the initiative tracker does (<c>TurnController.DelayInitiaive</c>).
        /// </summary>
        Delay,

        /// <summary>A move to a point on the ground (<see cref="BattleAction.Point"/>), as a click on the ground gives it.</summary>
        MoveTo,
    }

    /// <summary>An action of one unit; equal actions have the same kind, unit, target, path and ability.</summary>
    public sealed class BattleAction : IEquatable<BattleAction>
    {
        private BattleAction(BattleActionKind kind, string unit, string target, string path, string ability = null, string variant = null,
            BattlePoint? point = null, bool anyPoint = false)
        {
            if (string.IsNullOrWhiteSpace(unit)) throw new ArgumentException("A unit id is required.", nameof(unit));
            Kind = kind;
            Unit = unit;
            Target = target;
            Path = path;
            Ability = ability;
            Variant = variant;
            Point = point;
            AnyPoint = anyPoint;
        }

        /// <summary>The smart cursor variants of a turn-based attack: movement (a five-foot step or a move) and attack.</summary>
        public static readonly IReadOnlyList<string> AttackVariants = new[]
        {
            "five-foot-step+full-attack", "five-foot-step+single-attack", "five-foot-step+touch",
            "move+full-attack", "move+single-attack", "move+touch",
        };

        /// <summary>The movement limits of a turn-based move on the ground.</summary>
        public static readonly IReadOnlyList<string> MoveLimits = new[] { "two-actions", "one-action", "five-foot-step" };

        public static BattleAction Wait(string unit) => new(BattleActionKind.Wait, unit, null, null);

        public static BattleAction EndTurn(string unit) => new(BattleActionKind.EndTurn, unit, null, null);

        public static BattleAction Attack(string unit, string target)
            => new(BattleActionKind.Attack, unit, string.IsNullOrWhiteSpace(target) ? throw new ArgumentException("A target unit id is required.", nameof(target)) : target, null);

        /// <summary>A turn-based attack with one of the game's smart cursor variants (<see cref="AttackVariants"/>).</summary>
        public static BattleAction Attack(string unit, string target, string variant)
            => new(BattleActionKind.Attack, unit, string.IsNullOrWhiteSpace(target) ? throw new ArgumentException("A target unit id is required.", nameof(target)) : target, null, null,
                variant == null || AttackVariants.Contains(variant) ? variant : throw new ArgumentException($"Unknown attack variant {variant}.", nameof(variant)));

        public static BattleAction Move(string unit, string path)
            => new(BattleActionKind.Move, unit, null, string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("A path id is required.", nameof(path)) : path);

        /// <summary>A turn-based move on the ground with one of the game's movement limits (<see cref="MoveLimits"/>).</summary>
        public static BattleAction Move(string unit, string path, string limit)
            => new(BattleActionKind.Move, unit, null, string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("A path id is required.", nameof(path)) : path, null,
                limit == null || MoveLimits.Contains(limit) ? limit : throw new ArgumentException($"Unknown movement limit {limit}.", nameof(limit)));

        /// <summary>A move to a point on the ground, with a turn-based movement limit (<see cref="MoveLimits"/>) or the game's.</summary>
        public static BattleAction MoveTo(string unit, BattlePoint point, string limit = null)
            => new(BattleActionKind.MoveTo, unit, null, null, null, ValidLimit(limit), point);

        /// <summary>A spell, ability or item of the unit's action bar used at a point on the ground.</summary>
        public static BattleAction UseAbility(string unit, string ability, BattlePoint point)
            => new(BattleActionKind.UseAbility, unit, null, null,
                string.IsNullOrWhiteSpace(ability) ? throw new ArgumentException("An ability id is required.", nameof(ability)) : ability, null, point);

        /// <summary>The candidate that stands for moves to any point; give one with <see cref="At"/>.</summary>
        public static BattleAction MoveToAnyPoint(string unit, string limit = null)
            => new(BattleActionKind.MoveTo, unit, null, null, null, ValidLimit(limit), null, anyPoint: true);

        /// <summary>The candidate that stands for using the entry at any point on the ground; give one with <see cref="At"/>.</summary>
        public static BattleAction UseAbilityAtAnyPoint(string unit, string ability)
            => new(BattleActionKind.UseAbility, unit, null, null,
                string.IsNullOrWhiteSpace(ability) ? throw new ArgumentException("An ability id is required.", nameof(ability)) : ability, null, null, anyPoint: true);

        /// <summary>This any-point candidate at the point.</summary>
        public BattleAction At(BattlePoint point)
            => AnyPoint ? new(Kind, Unit, Target, Path, Ability, Variant, point) : throw new InvalidOperationException($"{this} is not an any-point candidate.");

        private static string ValidLimit(string limit)
            => limit == null || MoveLimits.Contains(limit) ? limit : throw new ArgumentException($"Unknown movement limit {limit}.", nameof(limit));

        public static BattleAction Delay(string unit, string target)
            => new(BattleActionKind.Delay, unit, string.IsNullOrWhiteSpace(target) ? throw new ArgumentException("A unit id to delay after is required.", nameof(target)) : target, null);

        /// <summary>Uses an action bar entry, by the id the observation's candidates give it, on a unit or (target null) on the caster.</summary>
        public static BattleAction UseAbility(string unit, string ability, string target = null)
            => new(BattleActionKind.UseAbility, unit, string.IsNullOrWhiteSpace(target) ? null : target, null,
                string.IsNullOrWhiteSpace(ability) ? throw new ArgumentException("An ability id is required.", nameof(ability)) : ability);

        public static BattleAction Toggle(string unit, string ability)
            => new(BattleActionKind.Toggle, unit, null, null, string.IsNullOrWhiteSpace(ability) ? throw new ArgumentException("An ability id is required.", nameof(ability)) : ability);

        public BattleActionKind Kind { get; }
        public string Unit { get; }

        /// <summary>The attacked unit's id, for <see cref="BattleActionKind.Attack"/>.</summary>
        public string Target { get; }

        /// <summary>The observed path's id, for <see cref="BattleActionKind.Move"/>.</summary>
        public string Path { get; }

        /// <summary>
        /// The action bar entry, for <see cref="BattleActionKind.UseAbility"/> and <see cref="BattleActionKind.Toggle"/>:
        /// spell:BOOK:LEVEL:SPELL[+METAMAGIC], item:QUICKSLOT:ITEM, ability:ABILITY or toggle:TOGGLE (blueprint names), with
        /// &gt;CONVERTED for a conversion the entry offers.
        /// </summary>
        public string Ability { get; }

        /// <summary>
        /// Turn-based combat: the smart cursor variant of an attack (<see cref="AttackVariants"/>) or the movement limit of a
        /// move (<see cref="MoveLimits"/>); null takes the one the game picks.
        /// </summary>
        public string Variant { get; }

        /// <summary>The point on the ground of a <see cref="BattleActionKind.MoveTo"/> or of an entry used at a point.</summary>
        public BattlePoint? Point { get; }

        /// <summary>A candidate that stands for every point: the decision maker gives one with <see cref="At"/>, which the game then checks.</summary>
        public bool AnyPoint { get; }

        /// <summary>Whether this action is the any-point candidate at a point.</summary>
        internal bool IsAt(BattleAction anyPoint)
            => anyPoint.AnyPoint && Point != null && Kind == anyPoint.Kind && Unit == anyPoint.Unit && Ability == anyPoint.Ability && Variant == anyPoint.Variant;

        public bool Equals(BattleAction other)
            => other != null && Kind == other.Kind && Unit == other.Unit && Target == other.Target && Path == other.Path && Ability == other.Ability
                && Variant == other.Variant && Nullable.Equals(Point, other.Point) && AnyPoint == other.AnyPoint;

        public override bool Equals(object obj) => Equals(obj as BattleAction);
        public override int GetHashCode() => (Kind, Unit, Target, Path, Ability, Variant, Point, AnyPoint).GetHashCode();

        private string Where => AnyPoint ? " at any point" : Point is { } p ? FormattableString.Invariant($" at ({p.X:0.##}, {p.Y:0.##}, {p.Z:0.##})") : "";

        public override string ToString() => Kind switch
        {
            BattleActionKind.Attack => Variant == null ? $"{Unit} attack {Target}" : $"{Unit} attack {Target} ({Variant})",
            BattleActionKind.Move => Variant == null ? $"{Unit} move {Path}" : $"{Unit} move {Path} ({Variant})",
            BattleActionKind.Delay => $"{Unit} delay after {Target}",
            BattleActionKind.EndTurn => $"{Unit} end turn",
            BattleActionKind.UseAbility => Target == null ? $"{Unit} use {Ability}{Where}" : $"{Unit} use {Ability} on {Target}",
            BattleActionKind.MoveTo => $"{Unit} move{Where}" + (Variant == null ? "" : $" ({Variant})"),
            BattleActionKind.Toggle => $"{Unit} toggle {Ability}",
            _ => $"{Unit} wait",
        };
    }

    /// <summary>What a decision maker sees at a decision point. It holds no game objects.</summary>
    public sealed class BattleObservation
    {
        internal BattleObservation(string scenario, int episode, int step, int seed, TimeSpan gameTime,
            IReadOnlyList<BattleUnitObservation> units, IReadOnlyList<BattlePathObservation> paths, IReadOnlyList<BattleAction> candidates,
            BattleOutcome outcome, bool truncated, string failure, BattleDifficultyObservation difficulty)
        {
            Difficulty = difficulty;
            Scenario = scenario;
            Episode = episode;
            Step = step;
            Seed = seed;
            GameTime = gameTime;
            Units = units;
            Paths = paths;
            Candidates = candidates;
            Outcome = outcome;
            Truncated = truncated;
            Failure = failure;
        }

        public string Scenario { get; }
        public int Episode { get; }

        /// <summary>Steps taken in the episode before this observation.</summary>
        public int Step { get; }

        public int Seed { get; }
        public TimeSpan GameTime { get; }

        /// <summary>The game's difficulty settings the battle's rules read (<see cref="BattleScenario.Difficulty"/>).</summary>
        public BattleDifficultyObservation Difficulty { get; }

        public IReadOnlyList<BattleUnitObservation> Units { get; }

        /// <summary>Scenario paths and the game's paths for ready units, usable or not, each with its source.</summary>
        public IReadOnlyList<BattlePathObservation> Paths { get; }

        /// <summary>Every action the game allows now, by ready unit: wait, attacks and moves along usable paths.</summary>
        public IReadOnlyList<BattleAction> Candidates { get; }

        /// <summary>Ids of the units that can take an action now.</summary>
        public IEnumerable<string> Ready => Units.Where(u => u.Ready).Select(u => u.Id);

        /// <summary>Set when the episode was decided.</summary>
        public BattleOutcome Outcome { get; }

        /// <summary>True when the episode reached its step limit without being decided.</summary>
        public bool Truncated { get; }

        /// <summary>The environment failure that stopped the episode, if one did.</summary>
        public string Failure { get; }

        public BattleUnitObservation Unit(string id)
            => Units.FirstOrDefault(u => u.Id == id) ?? throw new KeyNotFoundException($"The observation has no unit {id}.");
    }

    /// <summary>
    /// The game's difficulty settings as the rules read them: the level, the multiplier of enemy damage to the party, the
    /// enemies' stat adjustments and critical hits, whether the AI uses its additional behaviours, and limited AI for
    /// party units. Names are the game's enum values.
    /// </summary>
    public sealed class BattleDifficultyObservation
    {
        private BattleDifficultyObservation(string level, float damageToParty, string enemyDifficulty, string enemyCriticalHits,
            string statsAdjustments, bool additionalAiBehaviors, bool limitedAi)
        {
            Level = level;
            DamageToParty = damageToParty;
            EnemyDifficulty = enemyDifficulty;
            EnemyCriticalHits = enemyCriticalHits;
            StatsAdjustments = statsAdjustments;
            AdditionalAIBehaviors = additionalAiBehaviors;
            LimitedAI = limitedAi;
        }

        internal static BattleDifficultyObservation Capture()
        {
            var d = Kingmaker.Settings.SettingsRoot.Difficulty;
            return new BattleDifficultyObservation(d.GameDifficulty.GetValue().ToString(), d.DamageToParty.GetValue(),
                d.EnemyDifficulty.GetValue().ToString(), d.EnemyCriticalHits.GetValue().ToString(), d.StatsAdjustments.GetValue().ToString(),
                d.AdditionalAIBehaviors.GetValue(), d.LimitedAI.GetValue());
        }

        /// <summary><c>GameDifficultyOption</c>: Story, Casual, Normal, Daring, Core, Hard, Unfair or Custom.</summary>
        public string Level { get; }

        public float DamageToParty { get; }
        public string EnemyDifficulty { get; }
        public string EnemyCriticalHits { get; }
        public string StatsAdjustments { get; }
        public bool AdditionalAIBehaviors { get; }
        public bool LimitedAI { get; }
    }

    /// <summary>One unit as the game reports it.</summary>
    public sealed class BattleUnitObservation
    {
        internal BattleUnitObservation(string id, string side, bool controlled, string blueprint, string blueprintGuid, string faction, int hitPoints,
            int maxHitPoints, int temporaryHitPoints, string lifeState, bool conscious, bool canAct, bool canMove, bool ready,
            bool inCombat, BattlePoint position, float orientation, float corpulence, float speed, string command,
            float standardActionCooldown, float moveActionCooldown, float swiftActionCooldown, IReadOnlyList<BattleBuffObservation> buffs,
            IReadOnlyDictionary<string, int> resources, IReadOnlyList<string> abilities, IReadOnlyDictionary<string, int> spellSlots,
            IReadOnlyList<string> togglesOn, IReadOnlyDictionary<string, int> itemUses)
        {
            SpellSlots = spellSlots;
            ItemUses = itemUses;
            TogglesOn = togglesOn;
            Id = id;
            Side = side;
            Controlled = controlled;
            Blueprint = blueprint;
            BlueprintGuid = blueprintGuid;
            Faction = faction;
            HitPoints = hitPoints;
            MaxHitPoints = maxHitPoints;
            TemporaryHitPoints = temporaryHitPoints;
            LifeState = lifeState;
            Conscious = conscious;
            CanAct = canAct;
            CanMove = canMove;
            Ready = ready;
            InCombat = inCombat;
            Position = position;
            Orientation = orientation;
            Corpulence = corpulence;
            Speed = speed;
            Command = command;
            StandardActionCooldown = standardActionCooldown;
            MoveActionCooldown = moveActionCooldown;
            SwiftActionCooldown = swiftActionCooldown;
            Buffs = buffs;
            Resources = resources;
            Abilities = abilities;
        }

        public string Id { get; }
        public string Side { get; }

        /// <summary>True for units of a controlled side; the game's AI decides for the others.</summary>
        public bool Controlled { get; }

        public string Blueprint { get; }
        public string BlueprintGuid { get; }
        public string Faction { get; }

        /// <summary><c>UnitEntityData.HPLeft</c>: maximum hit points minus damage, including temporary hit points.</summary>
        public int HitPoints { get; }

        public int MaxHitPoints { get; }
        public int TemporaryHitPoints { get; }

        /// <summary>The game's <c>UnitLifeState</c>: Conscious, Unconscious or Dead.</summary>
        public string LifeState { get; }

        public bool Conscious { get; }

        /// <summary>The game's <c>UnitState.CanAct</c>.</summary>
        public bool CanAct { get; }

        /// <summary>The game's <c>UnitState.CanMove</c>.</summary>
        public bool CanMove { get; }

        /// <summary>Controlled, conscious, allowed to act and without a command: the unit can take an action now.</summary>
        public bool Ready { get; }

        public bool InCombat { get; }
        public BattlePoint Position { get; }

        /// <summary>Facing in degrees, as the game stores it.</summary>
        public float Orientation { get; }

        /// <summary>The radius in meters that reach, engagement and spacing use.</summary>
        public float Corpulence { get; }

        /// <summary>The game's current speed in meters per second.</summary>
        public float Speed { get; }

        /// <summary>Type name of the command the unit runs, or null.</summary>
        public string Command { get; }

        /// <summary>Seconds until the unit's standard action is available again in real-time combat.</summary>
        public float StandardActionCooldown { get; }

        public float MoveActionCooldown { get; }
        public float SwiftActionCooldown { get; }
        public IReadOnlyList<BattleBuffObservation> Buffs { get; }
        public IReadOnlyDictionary<string, int> Resources { get; }

        /// <summary>Names of the unit's abilities; the actions list what the unit can use now.</summary>
        public IReadOnlyList<string> Abilities { get; }

        /// <summary>
        /// Casts left per spellbook and spell level from 1 (BOOK:LEVEL): a spontaneous book's slots
        /// (Spellbook.GetSpontaneousSlots), a prepared book's memorized spells still available. Cantrips are at will.
        /// </summary>
        public IReadOnlyDictionary<string, int> SpellSlots { get; }

        /// <summary>Names of the unit's switched-on abilities (ActivatableAbility.IsOn), such as Power Attack.</summary>
        public IReadOnlyList<string> TogglesOn { get; }

        /// <summary>
        /// Uses left of the usable items in the unit's quick slots, by action bar id (item:QUICKSLOT:ITEM), as the action bar
        /// shows them: a single item's charges (a wand), a stack's count (potions, scrolls), -1 for an item that spends nothing.
        /// An item used up leaves its slot and this list.
        /// </summary>
        public IReadOnlyDictionary<string, int> ItemUses { get; }
    }

    public sealed class BattleBuffObservation
    {
        internal BattleBuffObservation(string name, string guid, int rank, TimeSpan? remaining)
        {
            Name = name;
            Guid = guid;
            Rank = rank;
            Remaining = remaining;
        }

        public string Name { get; }
        public string Guid { get; }
        public int Rank { get; }

        /// <summary>Game time until the buff ends; null for a buff without a duration.</summary>
        public TimeSpan? Remaining { get; }
    }

    /// <summary>A path a decision maker can choose, with its source and what the source supplied; nothing is computed by the environment.</summary>
    public sealed class BattlePathObservation
    {
        internal BattlePathObservation(string id, string source, string unit, string target, IReadOnlyList<BattlePoint> points,
            BattlePoint destination, double? distance, double? cost, string units, string problem)
        {
            Id = id;
            Source = source;
            Unit = unit;
            Target = target;
            Points = points;
            Destination = destination;
            Distance = distance;
            Cost = cost;
            Units = units;
            Problem = problem;
        }

        public string Id { get; }

        /// <summary><see cref="BattleEnvironment.GamePathSource"/> for the game's paths, the scenario's source otherwise.</summary>
        public string Source { get; }

        /// <summary>Id of the unit at the start of the path.</summary>
        public string Unit { get; }

        /// <summary>Id of the unit the path leads to, or null.</summary>
        public string Target { get; }

        public IReadOnlyList<BattlePoint> Points { get; }
        public BattlePoint Destination { get; }

        /// <summary>Length as the source gives it (the game's <c>Path.GetTotalLength</c>); null when the source gave none.</summary>
        public double? Distance { get; }

        /// <summary>Cost as the source gives it; the game's path interface gives none, so it is null for game paths.</summary>
        public double? Cost { get; }

        public string Units { get; }

        /// <summary>Why the path cannot be chosen now, or null.</summary>
        public string Problem { get; }

        public bool Usable => Problem == null;
    }

    /// <summary>How an episode was decided. <see cref="Winner"/> is a side, or null when no side won.</summary>
    public sealed class BattleOutcome
    {
        public BattleOutcome(string winner, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("An outcome needs a reason.", nameof(reason));
            Winner = winner;
            Reason = reason;
        }

        public string Winner { get; }
        public string Reason { get; }
        public override string ToString() => Reason;
    }

    /// <summary>What a reward function sees: the observations around one step, its actions and the state changes.</summary>
    public sealed class BattleTransition
    {
        internal BattleTransition(BattleObservation before, IReadOnlyList<BattleAction> actions, BattleObservation after, IReadOnlyList<string> effects)
        {
            Before = before;
            Actions = actions;
            After = after;
            Effects = effects;
        }

        public BattleObservation Before { get; }
        public IReadOnlyList<BattleAction> Actions { get; }
        public BattleObservation After { get; }
        public IReadOnlyList<string> Effects { get; }
    }

    public sealed class BattleStepResult
    {
        internal BattleStepResult(int episode, int step, IReadOnlyList<BattleAction> actions, TimeSpan elapsed, string decision,
            IReadOnlyList<string> effects, IReadOnlyDictionary<string, double> walked, BattleObservation observation, BattleOutcome outcome,
            bool truncated, string truncation, double reward)
        {
            Episode = episode;
            Step = step;
            Actions = actions;
            Elapsed = elapsed;
            Decision = decision;
            Effects = effects;
            Walked = walked;
            Observation = observation;
            Outcome = outcome;
            Truncated = truncated;
            Truncation = truncation;
            Reward = reward;
        }

        public int Episode { get; }
        public int Step { get; }
        public IReadOnlyList<BattleAction> Actions { get; }

        /// <summary>Game time the step's frames took.</summary>
        public TimeSpan Elapsed { get; }

        /// <summary>Why the frames stopped: a unit became ready, the battle was decided, or the decision interval passed.</summary>
        public string Decision { get; }

        /// <summary>Changes of the battle's key state (<see cref="BattleState"/> lines): - before, + after.</summary>
        public IReadOnlyList<string> Effects { get; }

        /// <summary>Horizontal distance in meters each unit that moved walked during the step, summed frame by frame.</summary>
        public IReadOnlyDictionary<string, double> Walked { get; }

        public BattleObservation Observation { get; }
        public BattleOutcome Outcome { get; }

        /// <summary>The episode ended at its step limit without being decided; not a result of the battle.</summary>
        public bool Truncated { get; }

        public string Truncation { get; }
        public double Reward { get; }
        public bool Ended => Outcome != null || Truncated;
    }

    /// <summary>What an episode did: enough to report a regression and to replay it with <see cref="BattleEnvironment.Replay"/>.</summary>
    public sealed class BattleEpisodeRecord
    {
        private readonly List<BattleStepRecord> steps = new();

        internal BattleEpisodeRecord(string scenario, int episode, int seed, string gameVersion, string libraryVersion, IReadOnlyList<string> initialState,
            Kingmaker.Settings.GameDifficultyOption? difficulty)
        {
            Scenario = scenario;
            Episode = episode;
            Seed = seed;
            Difficulty = difficulty;
            GameVersion = gameVersion;
            LibraryVersion = libraryVersion;
            InitialState = initialState;
        }

        public string Scenario { get; }
        public int Episode { get; }

        /// <summary>The seed the episode was built with. A seed alone is not a replay: the actions are needed too.</summary>
        public int Seed { get; }

        /// <summary>The difficulty level the scenario chose for the episode (<see cref="BattleScenario.Difficulty"/>), if any.</summary>
        public Kingmaker.Settings.GameDifficultyOption? Difficulty { get; }

        public string GameVersion { get; }
        public string LibraryVersion { get; }
        public IReadOnlyList<string> InitialState { get; }
        public IReadOnlyList<BattleStepRecord> Steps => steps;
        public BattleOutcome Outcome { get; private set; }
        public bool Truncated { get; private set; }

        /// <summary>The environment failure that stopped the episode, if one did.</summary>
        public string Error { get; private set; }

        internal void Add(BattleStepRecord stepRecord) => steps.Add(stepRecord);

        internal void Fail(int step, IReadOnlyList<BattleAction> actions, int? randomSeed, string error)
        {
            steps.Add(new BattleStepRecord(step, actions, randomSeed, TimeSpan.Zero, null, Array.Empty<string>(), error));
            Error = error;
        }

        internal void End(BattleOutcome outcome, bool truncated)
        {
            Outcome = outcome;
            Truncated = truncated;
        }

        public override string ToString()
            => string.Join(Environment.NewLine, new[]
                {
                    $"scenario {Scenario} episode {Episode} seed {Seed}{(Difficulty is { } level ? " difficulty " + level : "")} game {GameVersion ?? "unknown"} library {LibraryVersion ?? "unknown"}",
                }
                .Concat(steps.Select(s => s.ToString()))
                .Concat(new[] { Error != null ? "failed: " + Error : Outcome != null ? "outcome: " + Outcome : Truncated ? "truncated" : "running" }));
    }

    public sealed class BattleStepRecord
    {
        internal BattleStepRecord(int step, IReadOnlyList<BattleAction> actions, int? randomSeed, TimeSpan elapsed, string decision, IReadOnlyList<string> state, string error = null)
        {
            Step = step;
            Actions = actions;
            RandomSeed = randomSeed;
            Elapsed = elapsed;
            Decision = decision;
            State = state;
            Error = error;
        }

        public int Step { get; }
        public IReadOnlyList<BattleAction> Actions { get; }

        /// <summary>The seed the game's random numbers restarted from before the actions, if the step gave one.</summary>
        public int? RandomSeed { get; }

        public TimeSpan Elapsed { get; }
        public string Decision { get; }

        /// <summary>The battle's key state after the step.</summary>
        public IReadOnlyList<string> State { get; }

        public string Error { get; }

        public override string ToString()
            => FormattableString.Invariant($"step {Step}: {(RandomSeed is { } seed ? $"random seed {seed}, " : "")}[{string.Join("; ", Actions)}] {Elapsed.TotalSeconds:0.###} s, ") + (Error ?? Decision);
    }

    /// <summary>A step named actions the game does not allow now. Nothing was changed.</summary>
    public sealed class BattleActionRejectedException : ArgumentException
    {
        internal BattleActionRejectedException(string scenario, int episode, int step, IReadOnlyList<BattleAction> actions, IReadOnlyList<string> problems)
            : base($"Battle {scenario} episode {episode} step {step}: rejected [{string.Join("; ", actions)}]: {string.Join("; ", problems)}.")
        {
            Episode = episode;
            Step = step;
            Actions = actions;
            Problems = problems;
        }

        public int Episode { get; }
        public int Step { get; }
        public IReadOnlyList<BattleAction> Actions { get; }
        public IReadOnlyList<string> Problems { get; }
    }

    /// <summary>
    /// The environment failed during a step: the game logged an error, a frame failed, or a command could not be given.
    /// This is not a battle outcome and not a training sample; the episode stops until Reset.
    /// </summary>
    /// <summary>
    /// The game's turn-based combat can start no turn (for example the surprise-round dead end of its own code); the step
    /// fails as a <see cref="BattleEnvironmentException"/> with this as its cause, instead of running to the step limit.
    /// </summary>
    public sealed class TurnBasedStallException : InvalidOperationException
    {
        internal TurnBasedStallException(string message) : base(message) { }
    }

    public sealed class BattleEnvironmentException : Exception
    {
        internal BattleEnvironmentException(string scenario, int episode, int step, IReadOnlyList<BattleAction> actions, Exception inner)
            : base($"Battle {scenario} episode {episode} step {step} [{string.Join("; ", actions)}] failed: {inner.GetType().Name}: {inner.Message}", inner)
        {
            Episode = episode;
            Step = step;
            Actions = actions;
        }

        public int Episode { get; }
        public int Step { get; }
        public IReadOnlyList<BattleAction> Actions { get; }
    }
}
