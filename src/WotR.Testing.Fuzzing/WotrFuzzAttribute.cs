using System;

namespace WotR.Testing.Fuzzing
{
    /// <summary>Where a registered test runs.</summary>
    [Flags]
    public enum WotrTestModes
    {
        /// <summary>Outside the game process.</summary>
        OffGame = 1,
        /// <summary>Inside the running game, on Unity's main thread.</summary>
        InGame = 2,
        Both = OffGame | InGame
    }

    /// <summary>
    /// Registers a fuzz target: a public static parameterless non-generic method returning an interface or
    /// <c>Task&lt;interface&gt;</c>. It is called for a fresh target for every case, shrink attempt and replay.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class WotrFuzzAttribute : Attribute
    {
        public WotrTestModes Modes { get; set; } = WotrTestModes.OffGame;

        /// <summary>
        /// Optional name of a public static parameterless method in the same declaring type that returns
        /// <see cref="FuzzOptions"/>. It is called before every run and replay.
        /// </summary>
        public string OptionsProvider { get; set; }
    }
}
