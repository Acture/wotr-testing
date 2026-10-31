using System;
using WotR.Testing.InGame;
using Xunit;

namespace InGameTests;

public sealed class ModeTests : IClassFixture<WotR.Testing.IntegrationFixture.RuntimeReport>
{
    [Fact]
    public void Untagged_cases_run_offgame_only() => Assert.False(InGameRuntime.IsAvailable);

    [Fact]
    [Trait("WotrMode", "both")]
    public void Both_cases_run_once_in_each_mode()
        => Assert.Equal(InGameRuntime.IsAvailable, AppDomain.CurrentDomain.GetData("WotR.Testing.Mode") as string == "ingame");
}
