using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// One client's conversation with a <see cref="BattleEnvironment"/>: a JSON request per line in, a JSON answer per line
    /// out. <see cref="BattleEnvironmentServer"/> speaks it over a local TCP port; a consumer that drives the battle in
    /// its own process uses it directly.
    /// </summary>
    /// <remarks>
    /// The battle runs in the process that owns the protocol, inside the offline runtime; the client only sends decisions.
    /// Requests (<c>op</c>): <c>start</c> (<c>scenario</c>, <c>seed</c>, <c>maxSteps</c>, <c>turnBased</c>) builds the
    /// scenario the host names; <c>reset</c> (<c>seed</c>, optionally <c>difficulty</c>, a <c>GameDifficultyOption</c> name)
    /// starts the next episode; <c>observe</c>; <c>step</c> with <c>choices</c>, indices into the last observation's
    /// candidates, or <c>actions</c>, objects with <c>kind</c>, <c>unit</c> and as needed <c>target</c>, <c>path</c>,
    /// <c>ability</c>, <c>variant</c> and <c>point</c> ([x, y, z], for any-point candidates), and optionally
    /// <c>randomSeed</c>, a seed the game's random numbers restart from before the actions
    /// (<see cref="BattleEnvironment.Step(IEnumerable{BattleAction}, int?)"/>); <c>describe</c> with
    /// <c>guids</c>, blueprint guids of abilities, toggles or buffs from an observation, answers <c>texts</c>, each
    /// guid's in-game <c>name</c> and <c>description</c> in the loaded localization pack (null for an unknown guid);
    /// <c>record</c>, the episode's record;
    /// <c>replay</c>, which resets to the record's seed and takes its steps again; <c>close</c>.
    /// A step answers with the observation, <c>ended</c>, <c>truncated</c>, <c>truncation</c> (why the episode was cut off),
    /// <c>outcome</c> (the winner and why), <c>reward</c> (the environment's reward hook), <c>elapsed</c>, <c>decision</c>,
    /// <c>effects</c> (the battle's key state changes) and <c>walked</c> (the meters each unit actually walked).
    /// An error answers <c>{"error": ...}</c>; after an environment failure, reset.
    /// </remarks>
    public sealed class BattleEnvironmentProtocol : IDisposable
    {
        private readonly Func<string, bool, BattleScenario> scenario;
        private BattleEnvironment environment;
        private BattleObservation observation;
        private bool disposed;

        /// <param name="scenario">Builds the scenario a <c>start</c> request names, turn-based or not.</param>
        public BattleEnvironmentProtocol(Func<string, bool, BattleScenario> scenario)
            => this.scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));

        /// <summary>True after a <c>close</c> request: the client is done with the battle.</summary>
        public bool Closed { get; private set; }

        /// <summary>Handles one request line and answers with one answer line.</summary>
        public string Handle(string request)
        {
            if (disposed) throw new ObjectDisposedException(nameof(BattleEnvironmentProtocol));
            try
            {
                return Respond(JsonNode.Parse(request).AsObject()).ToJsonString();
            }
            catch (Exception error)
            {
                return new JsonObject { ["error"] = error.GetType().Name + ": " + error.Message }.ToJsonString();
            }
        }

        private JsonObject Respond(JsonObject request)
        {
            switch ((string)request["op"])
            {
                case "start":
                    environment?.Dispose();
                    environment = null;
                    observation = null;
                    environment = BattleEnvironment.Start(scenario((string)request["scenario"], (bool?)request["turnBased"] ?? false),
                        (int?)request["seed"] ?? 1, new BattleEnvironmentOptions { MaxSteps = (int?)request["maxSteps"] ?? 200 });
                    observation = environment.Observe();
                    return new JsonObject { ["observation"] = Encode(observation) };
                case "reset":
                    var level = (string)request["difficulty"];
                    try
                    {
                        observation = Require(environment).Reset((int?)request["seed"],
                            level == null ? null : (Kingmaker.Settings.GameDifficultyOption)Enum.Parse(typeof(Kingmaker.Settings.GameDifficultyOption), level));
                    }
                    catch
                    {
                        // A reset that could not clean up leaves no environment to continue with: the client starts again.
                        environment = null;
                        observation = null;
                        throw;
                    }
                    return new JsonObject { ["observation"] = Encode(observation) };
                case "observe":
                    observation = Require(environment).Observe();
                    return new JsonObject { ["observation"] = Encode(observation) };
                case "step":
                    var current = Require(environment);
                    var last = observation ?? throw new InvalidOperationException("Send start first.");
                    var actions = request["choices"] is JsonArray choices
                        ? choices.Select(n => last.Candidates[(int)n]).ToList()
                        : request["actions"] is JsonArray given ? given.Select(Decode).ToList()
                        : throw new ArgumentException("A step needs choices or actions.");
                    var result = current.Step(actions, (int?)request["randomSeed"]);
                    observation = result.Observation;
                    return new JsonObject
                    {
                        ["observation"] = Encode(observation),
                        ["ended"] = result.Ended,
                        ["truncated"] = result.Truncated,
                        ["truncation"] = result.Truncation,
                        ["outcome"] = EncodeOutcome(result.Outcome),
                        ["reward"] = result.Reward,
                        ["elapsed"] = result.Elapsed.TotalSeconds,
                        ["decision"] = result.Decision,
                        ["effects"] = new JsonArray(result.Effects.Select(e => (JsonNode)e).ToArray()),
                        ["walked"] = Walked(result.Walked),
                    };
                case "describe":
                    var texts = new JsonObject();
                    foreach (var node in request["guids"].AsArray())
                    {
                        var guid = (string)node;
                        texts[guid] = Describe(guid);
                    }
                    return new JsonObject { ["texts"] = texts };
                case "record":
                    return new JsonObject { ["record"] = EncodeRecord(Require(environment).Record) };
                case "replay":
                    var differences = Require(environment).Replay(Require(environment).Record);
                    observation = Require(environment).Observe();
                    return new JsonObject { ["differences"] = new JsonArray(differences.Select(d => (JsonNode)d).ToArray()) };
                case "close":
                    Closed = true;
                    return new JsonObject { ["closed"] = true };
                default:
                    throw new ArgumentException("Unknown op " + (string)request["op"] + ".");
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            environment?.Dispose();
            environment = null;
            observation = null;
        }

        private static BattleEnvironment Require(BattleEnvironment environment)
            => environment ?? throw new InvalidOperationException("Send start first.");

        /// <summary>
        /// The in-game name and description of a unit fact blueprint (an ability, toggle or buff) in the loaded
        /// localization pack, as its tooltip takes them (<c>BlueprintUnitFact.Name</c> and <c>Description</c>); null for
        /// a guid that names no such blueprint.
        /// </summary>
        private static JsonObject Describe(string guid)
        {
            var fact = Kingmaker.Blueprints.ResourcesLibrary.TryGetBlueprint(Kingmaker.Blueprints.BlueprintGuid.Parse(guid))
                as Kingmaker.Blueprints.Facts.BlueprintUnitFact;
            return fact == null ? null : new JsonObject { ["name"] = fact.Name, ["description"] = fact.Description };
        }

        private static BattleAction Decode(JsonNode node)
        {
            string Text(string key) => (string)node[key];
            BattlePoint? point = node["point"] is JsonArray p ? new BattlePoint((float)p[0], (float)p[1], (float)p[2]) : null;
            var unit = Text("unit");
            return Text("kind") switch
            {
                "Wait" => BattleAction.Wait(unit),
                "EndTurn" => BattleAction.EndTurn(unit),
                "Attack" => BattleAction.Attack(unit, Text("target"), Text("variant")),
                "Move" => BattleAction.Move(unit, Text("path"), Text("variant")),
                "MoveTo" => BattleAction.MoveTo(unit, point ?? throw new ArgumentException("MoveTo needs a point."), Text("variant")),
                "UseAbility" => point is { } at ? BattleAction.UseAbility(unit, Text("ability"), at) : BattleAction.UseAbility(unit, Text("ability"), Text("target")),
                "Toggle" => BattleAction.Toggle(unit, Text("ability")),
                "Delay" => BattleAction.Delay(unit, Text("target")),
                "AutoUse" => BattleAction.AutoUse(unit, Text("ability")),
                "Hold" => BattleAction.Hold(unit),
                "Stop" => BattleAction.Stop(unit),
                "Stealth" => BattleAction.Stealth(unit),
                "WeaponSet" => BattleAction.WeaponSet(unit, int.Parse(Text("variant") ?? throw new ArgumentException("WeaponSet needs the set's index as variant."), System.Globalization.CultureInfo.InvariantCulture)),
                "Grip" => BattleAction.Grip(unit),
                var kind => throw new ArgumentException($"Unknown action kind {kind}."),
            };
        }

        private static JsonObject Counts(IReadOnlyDictionary<string, int> values)
            => new(values.Select(pair => new KeyValuePair<string, JsonNode>(pair.Key, pair.Value)));

        /// <summary>
        /// An action bar slot as the game's ability tooltip shows it: what the ability is and does, the values the game
        /// calculates now, the uses the action bar shows and whether the unit can use it.
        /// </summary>
        private static JsonObject Encode(BattleSlotObservation s) => new()
        {
            ["id"] = s.Id,
            ["blueprint"] = s.Blueprint,
            ["guid"] = s.BlueprintGuid,
            ["type"] = s.Type,
            ["action"] = s.Action,
            ["fullRound"] = s.FullRound,
            ["range"] = s.Range,
            ["rangeMeters"] = s.RangeMeters,
            ["targetsEnemies"] = s.TargetsEnemies,
            ["targetsAllies"] = s.TargetsAllies,
            ["targetsSelf"] = s.TargetsSelf,
            ["targetsPoint"] = s.TargetsPoint,
            ["effectOnEnemy"] = s.EffectOnEnemy,
            ["effectOnAlly"] = s.EffectOnAlly,
            ["areaOfEffect"] = s.AreaOfEffect,
            ["areaRadius"] = s.AreaRadius,
            ["spellLevel"] = s.SpellLevel,
            ["school"] = s.School,
            ["descriptors"] = new JsonArray(s.Descriptors.Select(d => (JsonNode)d).ToArray()),
            ["savingThrow"] = s.SavingThrow,
            ["spellResistance"] = s.SpellResistance,
            ["dc"] = s.DC,
            ["casterLevel"] = s.CasterLevel,
            ["provokes"] = s.ProvokesAttackOfOpportunity,
            ["uses"] = s.Uses,
            ["usable"] = s.Usable,
            ["on"] = s.On,
        };

        private static JsonObject Walked(IReadOnlyDictionary<string, double> values)
            => new(values.Select(pair => new KeyValuePair<string, JsonNode>(pair.Key, pair.Value)));

        /// <summary>
        /// The observation as the client sees it: the units, the actions the game allows now, the paths the battle has
        /// (each with its source, its waypoints and the distance and cost the source gave), and the state of the episode.
        /// </summary>
        private static JsonObject Encode(BattleObservation observation)
        {
            var units = new JsonArray();
            foreach (var u in observation.Units)
            {
                units.Add(new JsonObject
                {
                    ["id"] = u.Id,
                    ["side"] = u.Side,
                    ["controlled"] = u.Controlled,
                    ["hp"] = u.HitPoints,
                    ["maxHp"] = u.MaxHitPoints,
                    ["conscious"] = u.Conscious,
                    ["ready"] = u.Ready,
                    ["commandable"] = u.Commandable,
                    ["x"] = u.Position.X,
                    ["y"] = u.Position.Y,
                    ["z"] = u.Position.Z,
                    ["command"] = u.Command,
                    ["resources"] = Counts(u.Resources),
                    ["spellSlots"] = Counts(u.SpellSlots),
                    ["itemUses"] = Counts(u.ItemUses),
                    ["togglesOn"] = new JsonArray(u.TogglesOn.Select(t => (JsonNode)t).ToArray()),
                    ["buffs"] = new JsonArray(u.Buffs.Select(b => (JsonNode)b.Name).ToArray()),
                    ["buffGuids"] = new JsonArray(u.Buffs.Select(b => (JsonNode)b.Guid).ToArray()),
                    ["autoUse"] = u.Controls.AutoUseAbility,
                    ["hold"] = u.Controls.Hold,
                    ["stop"] = u.Controls.Stop,
                    ["stealth"] = u.Controls.InStealth,
                    ["weaponSet"] = u.Controls.WeaponSet,
                    ["weaponSets"] = new JsonArray(u.Controls.WeaponSets
                        .Select(w => (JsonNode)new JsonArray(w.Primary, w.Secondary, w.Grip)).ToArray()),
                    ["cooldown"] = new JsonArray(u.StandardActionCooldown, u.MoveActionCooldown, u.SwiftActionCooldown),
                    ["slots"] = new JsonArray(u.Slots.Select(s => (JsonNode)Encode(s)).ToArray()),
                });
            }
            var candidates = new JsonArray();
            foreach (var c in observation.Candidates)
            {
                candidates.Add(new JsonObject
                {
                    ["kind"] = c.Kind.ToString(),
                    ["unit"] = c.Unit,
                    ["target"] = c.Target,
                    ["path"] = c.Path,
                    ["ability"] = c.Ability,
                    ["variant"] = c.Variant,
                    ["anyPoint"] = c.AnyPoint,
                    ["switch"] = c.IsSwitch,
                });
            }
            var paths = new JsonArray();
            foreach (var p in observation.Paths) paths.Add(EncodePath(p));
            return new JsonObject
            {
                ["scenario"] = observation.Scenario,
                ["episode"] = observation.Episode,
                ["step"] = observation.Step,
                ["time"] = observation.GameTime.TotalSeconds,
                ["difficulty"] = new JsonObject
                {
                    ["level"] = observation.Difficulty.Level,
                    ["damageToParty"] = observation.Difficulty.DamageToParty,
                    ["enemyDifficulty"] = observation.Difficulty.EnemyDifficulty,
                    ["enemyCriticalHits"] = observation.Difficulty.EnemyCriticalHits,
                    ["statsAdjustments"] = observation.Difficulty.StatsAdjustments,
                    ["additionalAiBehaviors"] = observation.Difficulty.AdditionalAIBehaviors,
                    ["limitedAi"] = observation.Difficulty.LimitedAI,
                },
                ["ready"] = new JsonArray(observation.Ready.Select(r => (JsonNode)r).ToArray()),
                ["commandable"] = new JsonArray(observation.Commandable.Select(r => (JsonNode)r).ToArray()),
                ["units"] = units,
                ["candidates"] = candidates,
                ["paths"] = paths,
                ["outcome"] = EncodeOutcome(observation.Outcome),
                ["truncated"] = observation.Truncated,
                ["failure"] = observation.Failure,
            };
        }

        /// <summary>
        /// A path as its source gave it: the waypoints and the destination, the distance and cost with their unit, and
        /// why it cannot be chosen now. What the source gave none of stays null.
        /// </summary>
        private static JsonObject EncodePath(BattlePathObservation path) => new()
        {
            ["id"] = path.Id,
            ["source"] = path.Source,
            ["unit"] = path.Unit,
            ["target"] = path.Target,
            ["destination"] = EncodePoint(path.Destination),
            ["points"] = new JsonArray(path.Points.Select(p => (JsonNode)EncodePoint(p)).ToArray()),
            ["distance"] = Number(path.Distance),
            ["cost"] = Number(path.Cost),
            ["units"] = path.Units,
            ["usable"] = path.Usable,
            ["problem"] = path.Problem,
        };

        private static JsonObject EncodePoint(BattlePoint point) => new()
        {
            ["x"] = point.X,
            ["y"] = point.Y,
            ["z"] = point.Z,
        };

        private static JsonObject EncodeOutcome(BattleOutcome outcome) => outcome == null ? null : new JsonObject
        {
            ["winner"] = outcome.Winner,
            ["reason"] = outcome.Reason,
        };

        /// <summary>The episode's record: what it needs to be reproduced and to be regressed against.</summary>
        private static JsonObject EncodeRecord(BattleEpisodeRecord record) => new()
        {
            ["scenario"] = record.Scenario,
            ["episode"] = record.Episode,
            ["seed"] = record.Seed,
            ["difficulty"] = record.Difficulty?.ToString(),
            ["gameVersion"] = record.GameVersion,
            ["modId"] = record.ModId,
            ["modVersion"] = record.ModVersion,
            ["libraryVersion"] = record.LibraryVersion,
            ["initialState"] = new JsonArray(record.InitialState.Select(l => (JsonNode)l).ToArray()),
            ["steps"] = new JsonArray(record.Steps.Select(s => (JsonNode)EncodeStep(s)).ToArray()),
            ["outcome"] = EncodeOutcome(record.Outcome),
            ["truncated"] = record.Truncated,
            ["error"] = record.Error,
        };

        private static JsonObject EncodeStep(BattleStepRecord step) => new()
        {
            ["step"] = step.Step,
            ["actions"] = new JsonArray(step.Actions.Select(a => (JsonNode)EncodeAction(a)).ToArray()),
            ["randomSeed"] = step.RandomSeed.HasValue ? JsonValue.Create(step.RandomSeed.Value) : null,
            ["elapsed"] = step.Elapsed.TotalSeconds,
            ["decision"] = step.Decision,
            ["state"] = new JsonArray(step.State.Select(l => (JsonNode)l).ToArray()),
            ["error"] = step.Error,
        };

        private static JsonObject EncodeAction(BattleAction action) => new()
        {
            ["kind"] = action.Kind.ToString(),
            ["unit"] = action.Unit,
            ["target"] = action.Target,
            ["path"] = action.Path,
            ["ability"] = action.Ability,
            ["variant"] = action.Variant,
            ["point"] = action.Point.HasValue ? EncodePoint(action.Point.Value) : null,
            ["anyPoint"] = action.AnyPoint,
        };

        private static JsonNode Number(double? value) => value.HasValue ? JsonValue.Create(value.Value) : null;
    }
}
