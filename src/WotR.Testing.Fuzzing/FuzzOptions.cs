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
