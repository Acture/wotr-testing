using System;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// Errors the game's own code logs in normal play while battle frames run, each with the code path that logs it. They
    /// are allowed like a scenario's <see cref="BattleScenario.FrameLogAllowances"/> and counted in the report; any other
    /// error the game logs still fails the frames.
    /// </summary>
    internal static class KnownGameLogs
    {
        internal static readonly RuleLogAllowance[] Frames =
        {
            new RuleLogAllowance(
                "a touch spell's held charge: AbilityEffectStickyTouch adds UnitPartTouch with Ensure, and EntityPart.TurnOn subscribes the part to EventBus; "
                + "UnitPartTouch.Init then subscribes it again, which the game's SubscriptionManager logs and ignores, keeping one subscription. "
                + "Every touch spell delivered by a held charge logs it in the game.",
                log => log.Message != null && log.Message.StartsWith("Can't subscribe on events twice (UnitPartTouch: ", StringComparison.Ordinal)),
        };
    }
}
