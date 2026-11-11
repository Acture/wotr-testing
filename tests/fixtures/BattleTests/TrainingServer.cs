using System;
using Kingmaker.EntitySystem.Stats;
using WotR.Testing.Offline;
using Xunit;

namespace WotR.Testing.BattleTests
{
    /// <summary>
    /// Serves the battle environment to examples/training while WOTR_TRAIN_PORT is set (BattleEnvironmentServer); otherwise
    /// it does nothing. Scenarios on the Kenabres mesh: 1v1, 1v3, 2v3 and 2v4 (fighters of the party against cultists).
    /// </summary>
    [Collection("battles")]
    public sealed class TrainingServer
    {
        private readonly BattleGame game;

        public TrainingServer(BattleGame game) => this.game = game;

        internal static BattleScenario Scenario(string name, bool turnBased)
        {
            var fighters = name switch { "1v1" or "1v3" => 1, "2v3" or "2v4" => 2, _ => throw new ArgumentException($"Unknown training scenario {name}; use 1v1, 1v3, 2v3 or 2v4.") };
            var cultists = name switch { "1v1" => 1, "1v3" or "2v3" => 3, _ => 4 };
            var scenario = new BattleScenario(name) { MainCharacter = "fighter", Area = Guids.KenabresBurning, TurnBased = turnBased, ControlledSides = { "party" } };
            scenario.Unit("fighter", "party", Guids.Fighter).WithBaseStat(StatType.Strength, 16)
                .Equip(BattleSlot.PrimaryHand, Guids.StandardLongsword).At(47.2f, 40, -65.2f);
            if (fighters == 2)
                scenario.Unit("fighter2", "party", Guids.Fighter).WithBaseStat(StatType.Strength, 16)
                    .Equip(BattleSlot.PrimaryHand, Guids.StandardLongsword).At(46.5f, 40, -67f);
            var positions = new[] { (52f, -68f), (52f, -70f), (53.5f, -69f), (53.5f, -67f) };
            for (var i = 0; i < cultists; i++)
                scenario.Unit(i == 0 ? "cultist" : "cultist" + (i + 1), "enemies", Guids.Cultist).At(positions[i].Item1, 40, positions[i].Item2);
            return scenario;
        }

        [Fact]
        public void Serve()
        {
            var port = Environment.GetEnvironmentVariable("WOTR_TRAIN_PORT");
            if (string.IsNullOrEmpty(port)) return;
            game.RequireGame();
            BattleEnvironmentServer.Serve(int.Parse(port), Scenario);
        }
    }
}
