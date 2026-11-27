using System;
using Kingmaker;
using Kingmaker.Assets.Code.UI.Overtip;
using Kingmaker.EntitySystem;
using Kingmaker.Localization;
using Kingmaker.UI.Overtip;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// The bark player of the game's UI, <c>Game.UI.BarkManager</c>, while a battle runs. The UI's overtips register it
    /// while they are shown; a battle loads no UI. Most of the game's code shows barks through <c>UIAccess.Bark</c>, which
    /// skips a missing player, but <c>UnitLifeController</c> calls it directly to show the "crippled" notice when a
    /// player-faction unit at death's door comes to. A battle gets a player that shows nothing and returns no handle.
    /// </summary>
    internal sealed class BarkPlayerStandIn : IBarkPlayer
    {
        private static BarkPlayerStandIn installed;

        internal static object Current => Game.Instance.UI.BarkManager;

        public IBarkHandle ShowBark(EntityDataBase entity, string text, float duration, VoiceOverStatus voiceOverStatus = null)
        {
            Boundaries.Hit("bark");
            return null;
        }

        internal static void Install()
        {
            if (Game.Instance.UI.BarkManager != null)
                throw new InvalidOperationException("The game already has a bark player; a battle runs without the game's UI.");
            installed = new BarkPlayerStandIn();
            Game.Instance.UI.BarkManager = installed;
        }

        internal static void Remove()
        {
            if (installed is null) return;
            if (ReferenceEquals(Game.Instance.UI.BarkManager, installed)) Game.Instance.UI.BarkManager = null;
            installed = null;
        }
    }
}
