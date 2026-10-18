using System;
using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using WotR.Testing.Offline;
using Xunit;

namespace WotR.Testing.IntegrationFixture
{
    public sealed class FuzzProperties : IClassFixture<RuntimeReport>
    {
        [Property(MaxTest = 200, Replay = "123,457")]
        public bool AddsCommutatively(int left, int right)
        {
            var expected = Environment.GetEnvironmentVariable("WOTR_TEST_EXPECT_RUNTIME") ?? "unity-mono";
            Assert.Equal(expected, Type.GetType("Mono.Runtime") != null ? "unity-mono" : "netfx");
            return unchecked(left + right) == unchecked(right + left);
        }

        [Property(MaxTest = 200, Replay = "123,457")]
        public bool ReversesTwice(string value, int[] values)
            => (value == null || new string(value.Reverse().Reverse().ToArray()) == value)
                && (values == null || Enumerable.Reverse(Enumerable.Reverse(values)).SequenceEqual(values));

        [Property(MaxTest = 200, Replay = "123,457", Arbitrary = new[] { typeof(BonusGenerators) })]
        public void UsesConstrainedGenerator(int bonus) => Assert.InRange(bonus, -5, 10);

        [Property(MaxTest = 100, StartSize = 50, EndSize = 50, Replay = "123,457")]
        public bool ShrinksCounterexample(PositiveInt value)
            => Environment.GetEnvironmentVariable("WOTR_TEST_SCENARIO") != "fuzz-failure" || value.Get < 10;

        [Property(MaxTest = 10, MaxRejected = 20, Replay = "123,457")]
        public Property RequiresAcceptedSamples(int value)
            => Prop.When(Environment.GetEnvironmentVariable("WOTR_TEST_SCENARIO") != "fuzz-exhaustion", () => true);

        [Property(MaxTest = 1, Replay = "123,457")]
        public void PreservesEnvironmentError(int value)
        {
            if (Environment.GetEnvironmentVariable("WOTR_TEST_SCENARIO") == "fuzz-environment")
                throw new OfflineEnvironmentMissingException("Synthetic input is missing.");
        }

        [Property(Skip = "[UNITY_RUNTIME_REQUIRED] Synthetic native-only property.")]
        public bool NativeOnly(int value) => throw new InvalidOperationException("A skipped property must not execute.");
    }

    public static class BonusGenerators
    {
        public static Arbitrary<int> Bonuses()
            => Arb.From(Gen.Choose(-5, 10), value => value == 0 ? Array.Empty<int>() : new[] { 0, value / 2 }.Distinct());
    }
}
