using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using WotR.Testing.Offline;
using Xunit;

namespace WotR.Testing.BattleTests
{
    /// <summary>
    /// The training entry point a program outside the test process speaks: one JSON request per line in, one JSON answer
    /// per line out. The client reads the observation, chooses and steps, three episodes in a row.
    /// </summary>
    [Collection("battles")]
    public sealed class BattleEnvironmentProtocolTests
    {
        private readonly BattleGame game;

        public BattleEnvironmentProtocolTests(BattleGame game) => this.game = game;

        [Fact]
        public void AClientRunsThreeEpisodesAndMovesAlongTheGamesPath()
        {
            game.RequireGame();
            using var client = new Client();
            for (var episode = 1; episode <= 3; episode++)
            {
                var observation = episode == 1 ? client.Start() : client.Reset();
                Assert.Equal("skirmish", (string)observation["scenario"]);
                Assert.Equal(0, (int)observation["step"]);

                // The paths the battle has: the game's own and the one the consumer recorded, each with its source.
                var paths = observation["paths"].AsArray().Select(p => p.AsObject()).ToDictionary(p => (string)p["id"]);
                Assert.Contains(BattleEnvironment.GamePathPrefix + "fighter->cultist", paths.Keys);
                var gamePath = paths[BattleEnvironment.GamePathPrefix + "fighter->cultist"];
                Assert.Equal(BattleEnvironment.GamePathSource, (string)gamePath["source"]);
                Assert.Equal("fighter", (string)gamePath["unit"]);
                Assert.Equal("cultist", (string)gamePath["target"]);
                Assert.True((bool)gamePath["usable"], (string)gamePath["problem"]);
                Assert.InRange((double)gamePath["distance"], 4.0, 12.0);
                Assert.Equal("meters", (string)gamePath["units"]);
                Assert.NotEmpty(gamePath["points"].AsArray());
                Assert.NotNull(gamePath["destination"]);
                // The game's path interface gives no cost: the client is told so instead of a number of its own.
                Assert.Null(gamePath["cost"]);
                var recorded = paths["fighter-recorded"];
                Assert.Equal("consumer:battle-tests", (string)recorded["source"]);
                Assert.Equal(2.2, (double)recorded["distance"]);

                var damage = 0;
                var moved = false;
                JsonObject step;
                while (true)
                {
                    step = client.Step(Decide(observation));
                    observation = (JsonObject)step["observation"];
                    damage += step["effects"].AsArray().Count(e => ((string)e).Contains(" hit-points "));
                    if (!moved && step["walked"].AsObject().ContainsKey("fighter"))
                    {
                        moved = true;
                        Assert.True((double)step["walked"]["fighter"] > 1, step.ToJsonString());
                        Assert.Contains(step["effects"].AsArray(),
                            e => ((string)e).StartsWith("+ unit fighter position ", StringComparison.Ordinal));
                    }
                    if ((bool)step["ended"]) break;
                }

                // The episode ended with the game's own decision, not with a cut-off or a failure.
                Assert.False((bool)step["truncated"], step.ToJsonString());
                Assert.Null(step["truncation"]);
                Assert.NotNull(step["outcome"]);
                Assert.False(string.IsNullOrWhiteSpace((string)step["outcome"]["reason"]));
                Assert.True(damage > 0, "No step changed hit points.");
                Assert.True(moved, "The fighter never walked.");

                // The record says what an external program needs to report a regression and to replay the episode.
                var record = (JsonObject)client.Record()["record"];
                Assert.Equal("skirmish", (string)record["scenario"]);
                Assert.Equal((int)observation["episode"], (int)record["episode"]);
                Assert.NotEmpty(record["initialState"].AsArray());
                Assert.NotNull(record["gameVersion"]);
                Assert.NotNull(record["libraryVersion"]);
                var recordedSteps = record["steps"].AsArray();
                Assert.NotEmpty(recordedSteps);
                var firstAction = recordedSteps[0].AsObject()["actions"].AsArray()[0].AsObject();
                Assert.Equal("Move", (string)firstAction["kind"]);
                Assert.Equal(BattleEnvironment.GamePathPrefix + "fighter->cultist", (string)firstAction["path"]);
                Assert.NotEmpty(recordedSteps[0].AsObject()["state"].AsArray());

                // Resetting to the record's seed and taking its steps again reproduces the episode.
                Assert.Empty(client.Replay()["differences"].AsArray());
            }
        }

        [Fact]
        public void AnErrorAnswersWithoutEndingTheConversation()
        {
            using var client = new Client();
            var error = client.Send(new JsonObject { ["op"] = "nonsense" });
            Assert.StartsWith("ArgumentException: ", (string)error["error"]);

            // A step before the battle exists is an error too, not an outcome.
            var early = client.Send(new JsonObject { ["op"] = "step", ["actions"] = new JsonArray() });
            Assert.StartsWith("InvalidOperationException: ", (string)early["error"]);

            // The conversation is still open: the client can close it.
            var closed = client.Send(new JsonObject { ["op"] = "close" });
            Assert.True((bool)closed["closed"]);
        }

        /// <summary>
        /// Moves along the game's path while the cultist is out of reach, then attacks it: the client reads the paths
        /// from the observation and picks the one the game gave.
        /// </summary>
        private static JsonObject[] Decide(JsonObject observation)
        {
            var units = observation["units"].AsArray().Select(u => u.AsObject()).ToDictionary(u => (string)u["id"]);
            var paths = observation["paths"].AsArray().Select(p => p.AsObject()).ToDictionary(p => (string)p["id"]);
            var fighter = units["fighter"];
            var cultist = units["cultist"];
            var distance = Math.Sqrt(Math.Pow((double)fighter["x"] - (double)cultist["x"], 2)
                + Math.Pow((double)fighter["z"] - (double)cultist["z"], 2));
            JsonObject attack = null;
            foreach (var candidate in observation["candidates"].AsArray().Select(c => c.AsObject()))
            {
                if ((string)candidate["unit"] != "fighter") continue;
                var kind = (string)candidate["kind"];
                if (kind == "Move" && distance > 3.5
                    && paths.TryGetValue((string)candidate["path"], out var path) && (string)path["source"] == BattleEnvironment.GamePathSource)
                {
                    return new[] { new JsonObject { ["kind"] = "Move", ["unit"] = "fighter", ["path"] = (string)candidate["path"] } };
                }
                if (kind == "Attack" && attack == null) attack = candidate;
            }
            if (attack != null)
            {
                return new[] { new JsonObject { ["kind"] = "Attack", ["unit"] = "fighter", ["target"] = (string)attack["target"] } };
            }
            return new[] { new JsonObject { ["kind"] = "Wait", ["unit"] = "fighter" } };
        }

        /// <summary>The client side of the protocol: it speaks the same lines the Python examples speak.</summary>
        private sealed class Client : IDisposable
        {
            private readonly BattleEnvironmentProtocol protocol =
                new((name, turnBased) => BattleEnvironmentTests.Skirmish());

            public JsonObject Start() => Ok((JsonObject)Send(new JsonObject
            {
                ["op"] = "start",
                ["scenario"] = "skirmish",
                ["seed"] = 1,
                ["maxSteps"] = 150,
            })["observation"]);

            public JsonObject Reset() => Ok((JsonObject)Send(new JsonObject { ["op"] = "reset", ["seed"] = 2 })["observation"]);

            public JsonObject Step(JsonObject[] actions) => Ok(Send(new JsonObject
            {
                ["op"] = "step",
                ["actions"] = new JsonArray(actions.Cast<JsonNode>().ToArray()),
            }));

            public JsonObject Record() => Ok(Send(new JsonObject { ["op"] = "record" }));

            public JsonObject Replay() => Ok(Send(new JsonObject { ["op"] = "replay" }));

            /// <summary>Sends a request and answers with the raw answer, errors included.</summary>
            public JsonObject Send(JsonObject request)
                => JsonNode.Parse(protocol.Handle(request.ToJsonString())).AsObject();

            private static JsonObject Ok(JsonObject answer)
            {
                Assert.Null(answer["error"]);
                return answer;
            }

            public void Dispose() => protocol.Dispose();
        }
    }
}
