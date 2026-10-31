using FsCheck.Xunit;
using WotR.Testing.Fuzzing;
using Xunit;

namespace ScopeSamples
{
    // Offgame tests: untagged Fact, untagged Theory, an FsCheck property (a Fact subclass in another assembly) and a
    // custom attribute deriving from Fact in this assembly.
    public sealed class Offgame
    {
        [Fact] public void Untagged() { }
        [Theory, InlineData(1), InlineData(2)] public void Rows(int value) { }
        [Property] public bool Property(int value) => true;
        [Custom] public void CustomFact() { }
        public void NotATest() { }
    }

    public sealed class CustomAttribute : FactAttribute { }

    [Trait("WotrMode", "ingame")]
    public sealed class InGameClass
    {
        [Fact] public void ByClassTrait() { }
        [Fact, Trait("Category", "Other")] public void WithAnotherTrait() { }
    }

    public sealed class Mixed
    {
        [Fact, Trait("WotrMode", "both")] public void Both() { }
        [Fact, Trait("WotrMode", "offgame")] public void ExplicitOffgame() { }
    }

    public interface ISample { void Call(int value); }

    public sealed class Sample : ISample { public void Call(int value) { } }

    public static class Registrations
    {
        [WotrFuzz] public static ISample DefaultMode() => new Sample();
        [WotrFuzz(Modes = WotrTestModes.InGame)] public static ISample InGame() => new Sample();
        [WotrFuzz(Modes = WotrTestModes.Both)] public static ISample Both() => new Sample();
    }
}

namespace ScopeSamples.Invalid
{
    public sealed class Misspelled
    {
        [Fact, Trait("WotrMode", "inGame")] public void WrongCase() { }
    }
}
