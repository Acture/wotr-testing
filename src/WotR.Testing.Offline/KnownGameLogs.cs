using System;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// Errors the game's own code logs in normal play while a battle builds its units or runs frames, each with the code
    /// path that logs it. They are allowed like a scenario's <see cref="BattleScenario.SetupLogAllowances"/> and
    /// <see cref="BattleScenario.FrameLogAllowances"/> and counted in the report; any other error the game logs still fails.
    /// </summary>
    internal static class KnownGameLogs
    {
        internal static readonly RuleLogAllowance[] Setup =
        {
            new RuleLogAllowance(
                "a unit blueprint whose body puts a second weapon in a hand slot the game's rules refuse: UnitBody.Initialize "
                + "fills the hands from the blueprint, and when HandSlot.IsItemSupported refuses the off-hand weapon (a "
                + "creature with hands but no empty-hand weapon, holding a natural weapon in the main hand, such as a "
                + "dretch's claws), UnitBody.TryInsertItem logs it and puts the item in the unit's inventory. The body comes "
                + "from the blueprint alone, so every spawn of the unit logs it in the game.",
                log => log.Message != null && log.Message.EndsWith("' to slot 'HandSlot'", StringComparison.Ordinal)
                    && log.Message.Contains("' can't insert item '")),
        };

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
