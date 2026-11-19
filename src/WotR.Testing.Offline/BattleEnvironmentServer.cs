using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// Serves a <see cref="BattleEnvironment"/> to a training program in another process, for example Python with an
    /// off-the-shelf trainer: one client on a local TCP port, one JSON object per line each way. The battle runs in the
    /// test process that calls <see cref="Serve"/>, inside the offline runtime; the client only sends decisions.
    /// </summary>
    /// <remarks>
    /// Requests (<c>op</c>): <c>start</c> (<c>scenario</c>, <c>seed</c>, <c>maxSteps</c>, <c>turnBased</c>) builds the
    /// scenario the host names; <c>reset</c> (<c>seed</c>, optionally <c>difficulty</c>, a <c>GameDifficultyOption</c> name)
    /// starts the next episode; <c>observe</c>; <c>step</c> with
    /// <c>choices</c>, indices into the last observation's candidates, or <c>actions</c>, objects with <c>kind</c>,
    /// <c>unit</c> and as needed <c>target</c>, <c>path</c>, <c>ability</c>, <c>variant</c> and <c>point</c> ([x, y, z],
    /// for any-point candidates), and optionally <c>randomSeed</c>, a seed the game's random numbers restart from before
    /// the actions (<see cref="BattleEnvironment.Step(IEnumerable{BattleAction}, int?)"/>); <c>describe</c> with
    /// <c>guids</c>, blueprint guids of abilities, toggles or buffs from an observation, answers <c>texts</c>, each
    /// guid's in-game <c>name</c> and <c>description</c> in the loaded localization pack (null for an unknown guid);
    /// <c>close</c>. A step answers with the observation, <c>ended</c>, <c>truncated</c>,
    /// <c>winner</c>, <c>reward</c> (the environment's reward hook), <c>elapsed</c> and <c>decision</c>. An error answers
    /// <c>{"error": ...}</c>; after an environment failure, reset. See examples/training for a client.
    /// </remarks>
    public static class BattleEnvironmentServer
    {
        /// <summary>
        /// Serves one client on the loopback port until it sends <c>close</c> or disconnects. <paramref name="scenario"/>
        /// builds the scenario a <c>start</c> request names, turn-based or not.
        /// </summary>
        public static void Serve(int port, Func<string, bool, BattleScenario> scenario)
        {
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            BattleEnvironment environment = null;
            try
            {
                using var client = listener.AcceptTcpClient();
                using var stream = client.GetStream();
                var reader = new StreamReader(stream, new UTF8Encoding(false));
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                BattleObservation observation = null;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    JsonObject response;
                    try
                    {
                        var request = JsonNode.Parse(line).AsObject();
                        switch ((string)request["op"])
                        {
                            case "start":
                                environment?.Dispose();
                                environment = BattleEnvironment.Start(scenario((string)request["scenario"], (bool?)request["turnBased"] ?? false),
                                    (int?)request["seed"] ?? 1, new BattleEnvironmentOptions { MaxSteps = (int?)request["maxSteps"] ?? 200 });
                                observation = environment.Observe();
                                response = new JsonObject { ["observation"] = Encode(observation) };
                                break;
                            case "reset":
                                var level = (string)request["difficulty"];
                                observation = Require(environment).Reset((int?)request["seed"],
                                    level == null ? null : (Kingmaker.Settings.GameDifficultyOption)Enum.Parse(typeof(Kingmaker.Settings.GameDifficultyOption), level));
                                response = new JsonObject { ["observation"] = Encode(observation) };
                                break;
                            case "observe":
                                observation = Require(environment).Observe();
                                response = new JsonObject { ["observation"] = Encode(observation) };
                                break;
                            case "step":
                                var actions = request["choices"] is JsonArray choices
                                    ? choices.Select(n => observation.Candidates[(int)n]).ToList()
                                    : request["actions"].AsArray().Select(Decode).ToList();
                                var result = Require(environment).Step(actions, (int?)request["randomSeed"]);
                                observation = result.Observation;
                                response = new JsonObject
                                {
                                    ["observation"] = Encode(observation),
                                    ["ended"] = result.Ended,
                                    ["truncated"] = result.Truncated,
                                    ["winner"] = result.Outcome?.Winner,
                                    ["reward"] = result.Reward,
                                    ["elapsed"] = result.Elapsed.TotalSeconds,
                                    ["decision"] = result.Decision,
                                };
                                break;
                            case "describe":
                                var texts = new JsonObject();
                                foreach (var node in request["guids"].AsArray())
                                {
                                    var guid = (string)node;
                                    texts[guid] = Describe(guid);
                                }
                                response = new JsonObject { ["texts"] = texts };
                                break;
                            case "close":
                                writer.WriteLine("{\"closed\":true}");
                                return;
                            default:
                                throw new ArgumentException("Unknown op " + (string)request["op"] + ".");
                        }
                    }
                    catch (Exception error)
                    {
                        response = new JsonObject { ["error"] = error.GetType().Name + ": " + error.Message };
                    }
                    writer.WriteLine(response.ToJsonString());
                }
            }
            finally
            {
                environment?.Dispose();
                listener.Stop();
            }
        }

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

        private static BattleEnvironment Require(BattleEnvironment environment)
            => environment ?? throw new InvalidOperationException("Send start first.");

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
            var paths = new JsonObject();
            foreach (var p in observation.Paths) paths[p.Id] = p.Distance;
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
                ["failure"] = observation.Failure,
            };
        }
    }
}
