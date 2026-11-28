using Xunit;

namespace WotR.Testing.Offline.Tests
{
    /// <summary>
    /// Compiled instead of the offgame scope when the game is not available. The scope is reported as skipped,
    /// never as a failure and never as a silent pass.
    /// </summary>
    public sealed class ScopeUnavailable
    {
        [Fact(Skip = "[ENV_MISSING] The offgame scope needs the game's Unity Mono runtime on Windows: set WrathInstallDir, WotrInputRoot or WOTR_INPUT_ROOT.")]
        public void Offgame_scope_needs_the_game() { }
    }
}
