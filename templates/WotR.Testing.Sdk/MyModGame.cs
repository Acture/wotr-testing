using System;
using WotR.Testing.Offline;
using Xunit;

namespace MyMod.OfflineTests
{
    /// <summary>
    /// One offline game session with the mod loaded, shared by every test in the collection.
    /// The session starts the real game systems in the order the game starts them and then loads the mod through the
    /// entry method named in its Info.json. A startup failure is kept and rethrown by RequireGame(), so every test
    /// reports the classified cause instead of passing vacuously.
    /// </summary>
    public sealed class MyModGame : WotrGameFixture
    {
        /// <summary>
        /// Optional: fail initialization unless the mod reports success. Throw to fail; the failure is reported as
        /// [INIT_FAILED] mod-verification and fails the run.
        /// </summary>
        protected override void VerifyModInitialized(GameSession session)
        {
            if (string.IsNullOrEmpty(session.ModId))
                throw new InvalidOperationException("No mod was loaded. Check WotrModAssembly and the mod's Info.json.");

            // Stronger check: the mod writes a line when it initialized. Needs "using System.Linq;".
            // if (!session.ModLog.Any(line => line.Contains("MyMod: initialized.")))
            //     throw new InvalidOperationException("The mod did not report a successful initialization:\n" + string.Join("\n", session.ModLog));
        }
    }

    /// <summary>
    /// Every test in this collection shares one game session. Keep the mod's tests in one collection so the game
    /// starts once per run.
    /// </summary>
    [CollectionDefinition(Name)]
    public sealed class MyModGameCollection : ICollectionFixture<MyModGame>
    {
        public const string Name = "MyMod offline game";
    }
}
