using System;
using System.Threading.Tasks;
using FsCheck.Fluent;
using WotR.Testing.Fuzzing;
using WotR.Testing.IntegrationFixture;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FuzzTests
{
    public interface ICounterApi
    {
        void Add(int value);
        Task<int> Read();
    }

    public interface INativeApi
    {
        void Draw();
    }

    public sealed class CounterApi : ICounterApi, IDisposable
    {
        private readonly bool broken;
        private int value;
        public static int Created;
        public static int Disposed;
        public static int Added;
        public static int ReadCount;

        public CounterApi(bool broken = false)
        {
            this.broken = broken;
            Created++;
        }

        public void Add(int amount) { value += amount; Added++; }

        public async Task<int> Read()
        {
            await Task.Yield();
            ReadCount++;
            if (broken && value >= 10) throw new InvalidOperationException("stateful counter failure");
            return value;
        }

        public void Dispose() => Disposed++;
    }

    public sealed class NativeApi : INativeApi
    {
        public void Draw() => throw new NotSupportedException("[synthetic-native-edge]");
    }

    public sealed class InterfaceTests : IClassFixture<RuntimeReport>
    {
        private static FuzzOptions Options()
        {
            var options = new FuzzOptions { Cases = 100, MaxCalls = 12, Seed = 12345 };
            options.Generators.Register(Arb.From(Gen.Choose(0, 30), n => new[] { 0, n / 2 }));
            return options;
        }

        [Fact]
        public void AutomaticallyCallsRegisteredInterface()
        {
            CounterApi.Created = CounterApi.Disposed = CounterApi.Added = CounterApi.ReadCount = 0;
            var report = Fuzz.Check<ICounterApi>(() => new CounterApi(), Options());
            Assert.True(report.Succeeded);
            Assert.Equal(100, report.CasesPassed);
            Assert.Equal(2, report.SelectedMethods.Count);
            Assert.True(CounterApi.Added > 0 && CounterApi.ReadCount > 0);
            Assert.Equal(CounterApi.Created, CounterApi.Disposed);
        }

        [Fact]
        public void AutomaticallyShrinksAndReplaysStatefulFailure()
        {
            CounterApi.Created = CounterApi.Disposed = 0;
            var failure = Assert.Throws<FuzzFailureException>(() =>
                Fuzz.Check<ICounterApi>(() => new CounterApi(broken: true), Options()));
            Assert.Equal(FuzzStatus.Finding, failure.Report.Status);
            Assert.True(failure.Report.ShrinkAttempts > 0);
            Assert.Contains("stateful counter failure", failure.Report.Failure.Message);
            Assert.False(string.IsNullOrEmpty(failure.Report.ReplayCase));
            var replay = Fuzz.Replay<ICounterApi>(() => new CounterApi(broken: true), failure.Report.ReplayCase, Options());
            Assert.Equal(FuzzStatus.Finding, replay.Status);
            Assert.Equal(failure.Report.Failure.Method, replay.Failure.Method);
            Assert.Equal(failure.Report.Failure.ExceptionType, replay.Failure.ExceptionType);
            Assert.Equal(CounterApi.Created, CounterApi.Disposed);
        }

        [Fact]
        public void UnavailableSequencesCannotPass()
        {
            var options = Options();
            options.IsUnavailable = error => error.Message.Contains("[synthetic-native-edge]");
            var failure = Assert.Throws<FuzzFailureException>(() => Fuzz.Check<INativeApi>(() => new NativeApi(), options));
            Assert.Equal(FuzzStatus.AllSkipped, failure.Report.Status);
            Assert.Equal(0, failure.Report.CasesPassed);
            Assert.Equal(100, failure.Report.CasesSkipped);
        }
    }
}
