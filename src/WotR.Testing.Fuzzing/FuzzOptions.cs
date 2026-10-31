using System;
using System.Reflection;

namespace WotR.Testing.Fuzzing
{
    public sealed class FuzzOptions
    {
        public int Cases { get; set; } = 100;
        public int MaxCalls { get; set; } = 20;
        public int Seed { get; set; } = 12345;
        public int MaxShrinks { get; set; } = 1000;
        public Func<MethodInfo, bool> IncludeMethod { get; set; }
        public Func<Exception, bool> IsUnavailable { get; set; }
        public Func<Exception, bool> IsExpectedException { get; set; }
        /// <summary>Runs before each case, shrink attempt and replay. Argument generation happens outside this hook.</summary>
        public Action BeforeExecution { get; set; }
        /// <summary>Runs after each case, shrink attempt and replay, including when <see cref="BeforeExecution"/> or the factory throws.</summary>
        public Action AfterExecution { get; set; }
        public FuzzGenerators Generators { get; private set; } = new FuzzGenerators();

        /// <summary>Copies configuration and generator registrations without sharing the mutable registry.</summary>
        public FuzzOptions Clone()
        {
            var copy = (FuzzOptions)MemberwiseClone();
            copy.Generators = Generators.Clone();
            return copy;
        }
    }
}
