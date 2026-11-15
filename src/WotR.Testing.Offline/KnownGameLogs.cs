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
            new RuleLogAllowance(
                "a command that waits more than 5 seconds for hands busy with a weapon set change: UnitViewHandsEquipment's change "
                + "holds the hands until a real-time unit has its move or standard action back (up to a round after a full attack), "
                + "and UnitCommandController.ShouldStartCommand logs this after 5 seconds of waiting, releases the view's interrupt "
                + "animations, starts counting again and keeps the command waiting. A weapon set change in combat followed by a "
                + "command while both actions are used logs it in the game; offline the hands are held only for the wait, not the "
                + "animation (boundary weapon-set-change), so never longer than in the game.",
                log => log.Message != null && log.Message.StartsWith("[REPORT ME] HandAnimation waits too long", StringComparison.Ordinal)),
            new RuleLogAllowance(
                "an offensive ability interrupted before it acts, followed by damage its caster deals the same target that no later "
                + "command of the caster accounts for (an attack of opportunity, for example): UnitImpatienceController records a "
                + "unit's standard commands against its target when they start (HandleUnitCommandDidStart), a UnitUseAbility gets its "
                + "ExecutionProcess only when it acts, and OnEventDidTrigger(RuleDealDamage) reads ExecutionProcess.Context of every "
                + "finished UnitUseAbility it passes, which throws for the interrupted one; the game's RulebookSubscribersList logs "
                + "the NullReferenceException and the damage stands. A player's stop key or new command during a cast reaches it "
                + "in the game.",
                log => log.Exception != null && log.Exception.StartsWith("System.NullReferenceException", StringComparison.Ordinal)
                    && ThrownIn(log.Exception, "Kingmaker.Controllers.UnitImpatienceController.OnEventDidTrigger", "RuleDealDamage")),
        };

        /// <summary>
        /// Whether the exception's first stack frame is the method, with a parameter of the type: Mono writes
        /// "Method (Namespace.Type evt)", .NET Framework "Method(Type evt)".
        /// </summary>
        private static bool ThrownIn(string exception, string method, string parameterType)
        {
            var at = exception.IndexOf(" at ", StringComparison.Ordinal);
            if (at < 0) return false;
            var end = exception.IndexOf('\n', at);
            var frame = end < 0 ? exception.Substring(at) : exception.Substring(at, end - at);
            var call = frame.IndexOf(method, StringComparison.Ordinal);
            if (call < 0) return false;
            var parameters = frame.IndexOf('(', call + method.Length);
            return parameters >= 0 && parameters <= call + method.Length + 1 && frame.IndexOf(parameterType, parameters, StringComparison.Ordinal) > 0;
        }
    }
}
