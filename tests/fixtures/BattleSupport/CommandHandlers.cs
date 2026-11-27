using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.RuleSystem.Rules;
using Kingmaker.UnitLogic.Commands.Base;

namespace WotR.Testing.BattleSupport
{
    /// <summary>Throws when the game reports that a command acted, after its action already took effect.</summary>
    public sealed class ThrowingCommandHandler : IUnitCommandActHandler
    {
        public const string Message = "handler failed while the command was acting";
        public int Calls;

        public void HandleUnitCommandDidAct(UnitCommand command)
        {
            Calls++;
            throw new InvalidOperationException(Message);
        }
    }

    /// <summary>Records every attack of opportunity the game reports.</summary>
    public sealed class AttackOfOpportunityRecorder : IAttackOfOpportunityHandler
    {
        public readonly List<KeyValuePair<UnitEntityData, UnitEntityData>> Attacks = new();

        public void HandleAttackOfOpportunity(UnitEntityData attacker, UnitEntityData target)
            => Attacks.Add(new KeyValuePair<UnitEntityData, UnitEntityData>(attacker, target));
    }

    /// <summary>Records every weapon attack the game's rulebook settles, with its attacker, target and whether it was part of a full attack.</summary>
    public sealed class AttackRecorder : IGlobalRulebookHandler<RuleAttackWithWeapon>
    {
        public readonly List<(UnitEntityData Attacker, UnitEntityData Target, bool FullAttack)> Attacks = new();

        public void OnEventAboutToTrigger(RuleAttackWithWeapon evt)
        {
        }

        public void OnEventDidTrigger(RuleAttackWithWeapon evt) => Attacks.Add((evt.Initiator, evt.Target, evt.IsFullAttack));
    }

    /// <summary>Records every command the game reports as acted, with its executor and whether the game's AI created it.</summary>
    public sealed class CommandRecorder : IUnitCommandActHandler
    {
        public readonly List<(UnitEntityData Executor, string Command, bool ByAi)> Acted = new();

        public void HandleUnitCommandDidAct(UnitCommand command)
            => Acted.Add((command.Executor, command.GetType().Name, command.AiAction != null));
    }
}
