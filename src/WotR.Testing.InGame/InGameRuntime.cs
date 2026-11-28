using System;
using System.ComponentModel;
using System.Threading;

namespace WotR.Testing.InGame
{
    /// <summary>The in-game host's services for test code. Empty outside the game's player.</summary>
    public static class InGameRuntime
    {
        private static Action<string> markDirty;

        public static MainThreadDispatcher Dispatcher { get; private set; }

        /// <summary>Whether this code runs inside the game's player with the WotR.Testing host loaded.</summary>
        public static bool IsAvailable => Dispatcher != null;

        /// <summary>Canceled when the current test session is canceled or abandoned.</summary>
        public static CancellationToken SessionCancellation { get; [EditorBrowsable(EditorBrowsableState.Never)] set; }

        /// <summary>Called once by the in-game host.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void Attach(MainThreadDispatcher dispatcher, Action<string> dirty)
        {
            if (Dispatcher != null) throw new InvalidOperationException("The in-game host is already attached.");
            markDirty = dirty ?? throw new ArgumentNullException(nameof(dirty));
            Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        /// <summary>Records that game state may no longer be as tests expect; the host refuses further sessions.</summary>
        public static void MarkDirty(string reason) => markDirty?.Invoke(reason);

        internal static MainThreadDispatcher Require()
            => Dispatcher ?? throw new InvalidOperationException(
                "[INGAME_REQUIRED] In-game test APIs need the running game with the WotR.Testing host. Mark the test with [Trait(\"WotrMode\", \"ingame\")] and run it in-game.");
    }
}
