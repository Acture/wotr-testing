using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
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
}
