using System;
using Kingmaker.PubSubSystem;
using Kingmaker.UnitLogic.Buffs;

namespace WotR.Testing.BattleSupport
{
    /// <summary>Counts buff events it receives while subscribed.</summary>
    public sealed class CountingBuffHandler : IUnitBuffHandler
    {
        public int Calls;
        public void HandleBuffDidAdded(Buff buff) => Calls++;
        public void HandleBuffDidRemoved(Buff buff) => Calls++;
    }

    /// <summary>Throws while the game raises the buff-added event, after the buff is already applied.</summary>
    public sealed class ThrowingBuffHandler : IUnitBuffHandler
    {
        public const string Message = "handler failed while the buff was activating";
        public int Calls;

        public void HandleBuffDidAdded(Buff buff)
        {
            Calls++;
            throw new InvalidOperationException(Message);
        }

        public void HandleBuffDidRemoved(Buff buff) { }
    }
}
