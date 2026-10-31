using System;

namespace WotR.Testing.Offline
{
    /// <summary>A caller's own sample stream. It does not read or write <see cref="GameRandom"/>.</summary>
    public sealed class ActionSampleRandom
    {
        private readonly Random random;

        public ActionSampleRandom(int seed) => random = new Random(seed);

        public int Next(int minInclusive, int maxExclusive) => random.Next(minInclusive, maxExclusive);
    }
}
