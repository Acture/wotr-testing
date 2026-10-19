using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WotR.Testing.Fuzzing;
using Xunit;

namespace WotR.Testing.Fuzzing.Tests;

// Registration counters are shared by the tests in this class, which xUnit runs one at a time.
public class FuzzTargetTests
{
    private const BindingFlags AllMethods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    [Fact]
    public void Discovery_uses_reflection_only_reports_every_registration_and_orders_by_id()
    {
        int factories = Registrations.Factories, providers = Registrations.Providers;
        var targets = FuzzTargets.Discover(typeof(FuzzTargetTests).Assembly);
        Assert.Equal(factories, Registrations.Factories);
        Assert.Equal(providers, Registrations.Providers);
        Assert.Equal(targets.Select(target => target.Id).OrderBy(id => id, StringComparer.Ordinal), targets.Select(target => target.Id));
        Assert.Equal(targets.Count, targets.Select(target => target.Id).Distinct().Count());
        foreach (var holder in new[] { typeof(Registrations), typeof(InvalidRegistrations), typeof(InstanceRegistrations), typeof(GenericHolder<>) })
            Assert.Equal(holder.GetMethods(AllMethods).Count(method => method.IsDefined(typeof(WotrFuzzAttribute), false)),
                targets.Count(target => target.Factory.DeclaringType == holder));
        Assert.Throws<ArgumentNullException>(() => FuzzTargets.Discover(null));
    }

    [Fact]
    public void Valid_registrations_expose_interface_modes_provider_and_stable_id()
    {
        var plain = Target(typeof(Registrations), nameof(Registrations.CreateCounter));
        Assert.Null(plain.Error);
        Assert.Equal(typeof(ICounter), plain.Interface);
        Assert.Equal(WotrTestModes.OffGame, plain.Modes);
        Assert.Null(plain.OptionsProvider);
        Assert.Equal("WotR.Testing.Fuzzing.Tests:" + typeof(Registrations).FullName + ".CreateCounter:" + typeof(ICounter).FullName, plain.Id);
        Assert.Equal("Registrations.CreateCounter (ICounter)", plain.DisplayName);

        var task = Target(typeof(Registrations), nameof(Registrations.CreateCounterAsync));
        Assert.Null(task.Error);
        Assert.Equal(typeof(ICounter), task.Interface);
        Assert.Equal(WotrTestModes.Both, task.Modes);
        Assert.Equal(nameof(Registrations.SharedOptions), task.OptionsProvider.Name);
        Assert.EndsWith(".CreateCounterAsync:" + typeof(ICounter).FullName, task.Id);
        Assert.Equal(WotrTestModes.InGame, Target(typeof(Registrations), nameof(Registrations.CreateInGame)).Modes);
    }

    [Fact]
    public async Task Run_async_calls_the_provider_for_every_run_and_applies_the_seed_override_to_a_copy()
    {
        int factories = Registrations.Factories;
        var plain = await Target(typeof(Registrations), nameof(Registrations.CreateCounter)).RunAsync();
        Assert.True(plain.Succeeded);
        Assert.Equal(100, plain.CasesAttempted);
        Assert.Equal(12345, plain.Seed);
        Assert.Equal(factories + 100, Registrations.Factories);

        var registered = Target(typeof(Registrations), nameof(Registrations.CreateCounterAsync));
        int providers = Registrations.Providers;
        var configured = await registered.RunAsync();
        Assert.True(configured.Succeeded);
        Assert.Equal(7, configured.CasesAttempted);
        Assert.Equal(4242, configured.Seed);
        var seeded = await registered.RunAsync(seed: 99);
        Assert.Equal(7, seeded.CasesAttempted);
        Assert.Equal(99, seeded.Seed);
        Assert.Equal(providers + 2, Registrations.Providers);
        Assert.Equal(4242, Registrations.Shared.Seed);
        var direct = await Fuzz.RunAsync<ICounter>(() => Task.FromResult<ICounter>(new Counter()), new FuzzOptions { Cases = 7, MaxCalls = 3, Seed = 99 });
        Assert.Equal(direct.ToJson(), seeded.ToJson());
    }

    [Fact]
    public async Task Registered_defect_is_found_and_replayed_exactly()
    {
        var target = Target(typeof(Registrations), nameof(Registrations.CreateLedger));
        var report = await target.RunAsync();
        Assert.Equal(FuzzStatus.Finding, report.Status);
        Assert.True(report.ShrinkAttempts > 0);
        var replay = await target.ReplayAsync(report.ReplayCase);
        Assert.Equal(FuzzStatus.Finding, replay.Status);
        Assert.Equal(report.ReplayCase, replay.ReplayCase);
        Assert.Equal(1, replay.CasesAttempted);
        Assert.Equal(0, replay.ShrinkAttempts);
        await Assert.ThrowsAsync<ArgumentException>(() => target.ReplayAsync("{}"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => target.ReplayAsync(null));
    }

    [Fact]
    public async Task Factory_exceptions_are_reported_unwrapped_as_infrastructure_failures()
    {
        var report = await Target(typeof(Registrations), nameof(Registrations.ThrowingFactory)).RunAsync();
        Assert.Equal(FuzzStatus.InfrastructureFailure, report.Status);
        Assert.Equal("factory", report.Failure.Kind);
        Assert.Equal(typeof(ApplicationException).FullName, report.Failure.ExceptionType);
        Assert.Equal(0, report.ShrinkAttempts);
    }

    [Fact]
    public async Task Options_provider_failures_are_thrown_before_any_target_is_constructed()
    {
        int factories = Registrations.Factories;
        var throwing = Target(typeof(Registrations), nameof(Registrations.WithThrowingOptions));
        Assert.Null(throwing.Error);
        var run = await Assert.ThrowsAsync<InvalidOperationException>(() => throwing.RunAsync());
        Assert.IsType<ApplicationException>(run.InnerException);
        Assert.Contains(nameof(Registrations.ThrowingOptions), run.Message);
        var replay = await Assert.ThrowsAsync<InvalidOperationException>(() => throwing.ReplayAsync("{}"));
        Assert.IsType<ApplicationException>(replay.InnerException);
        var nullOptions = await Assert.ThrowsAsync<InvalidOperationException>(() => Target(typeof(Registrations), nameof(Registrations.WithNullOptions)).RunAsync());
        Assert.Contains("returned null", nullOptions.Message);
        Assert.Equal(factories, Registrations.Factories);
    }

    [Theory]
    [InlineData(typeof(InvalidRegistrations), "NotPublic", "public static")]
    [InlineData(typeof(InstanceRegistrations), "Instance", "public static")]
    [InlineData(typeof(InvalidRegistrations), "WithParameter", "no parameters")]
    [InlineData(typeof(InvalidRegistrations), "Generic", "must not be generic")]
    [InlineData(typeof(GenericHolder<>), "Create", "open generic")]
    [InlineData(typeof(InvalidRegistrations), "Concrete", "interface or Task<interface>")]
    [InlineData(typeof(InvalidRegistrations), "ConcreteTask", "interface or Task<interface>")]
    [InlineData(typeof(InvalidRegistrations), "UntypedTask", "interface or Task<interface>")]
    [InlineData(typeof(InvalidRegistrations), "ValueTaskFactory", "interface or Task<interface>")]
    [InlineData(typeof(InvalidRegistrations), "Nothing", "interface or Task<interface>")]
    [InlineData(typeof(InvalidRegistrations), "NoModes", "Modes must be")]
    [InlineData(typeof(InvalidRegistrations), "UnknownMode", "Modes must be")]
    [InlineData(typeof(InvalidRegistrations), "MissingProvider", "was not found")]
    [InlineData(typeof(InvalidRegistrations), "AmbiguousProvider", "ambiguous")]
    [InlineData(typeof(InvalidRegistrations), "WrongProviderReturn", "returning FuzzOptions")]
    [InlineData(typeof(InvalidRegistrations), "ProviderWithParameter", "returning FuzzOptions")]
    [InlineData(typeof(InvalidRegistrations), "PrivateProvider", "returning FuzzOptions")]
    [InlineData(typeof(InstanceRegistrations), "InstanceProvider", "returning FuzzOptions")]
    public async Task Invalid_registrations_are_reported_with_an_error_instead_of_dropped(Type holder, string method, string reason)
    {
        var target = Target(holder, method);
        Assert.NotNull(target.Error);
        Assert.Contains(reason, target.Error);
        Assert.Null(target.Interface);
        Assert.Null(target.OptionsProvider);
        Assert.Contains(method, target.Id);
        var run = await Assert.ThrowsAsync<InvalidOperationException>(() => target.RunAsync());
        Assert.Contains(target.Error, run.Message);
        var replay = await Assert.ThrowsAsync<InvalidOperationException>(() => target.ReplayAsync("{}"));
        Assert.Contains(target.Error, replay.Message);
    }

    [Fact]
    public async Task Replay_file_round_trips_and_replays_the_registered_target()
    {
        var target = Target(typeof(Registrations), nameof(Registrations.CreateLedger));
        var report = await target.RunAsync();
        var path = TemporaryFile();
        try
        {
            new FuzzReplayFile { Target = target.Id, Mode = FuzzReplayFile.OffGame, Case = report.ReplayCase }.Write(path);
            var read = FuzzReplayFile.Read(path);
            Assert.Equal(1, read.FormatVersion);
            Assert.Equal(target.Id, read.Target);
            Assert.Equal("offgame", read.Mode);
            Assert.Equal(report.ReplayCase, read.Case);
            var found = Assert.Single(FuzzTargets.Discover(typeof(FuzzTargetTests).Assembly), item => item.Id == read.Target);
            var replay = await found.ReplayAsync(read.Case);
            Assert.Equal(FuzzStatus.Finding, replay.Status);
            Assert.Equal(report.ReplayCase, replay.ReplayCase);

            File.WriteAllText(path, "{\"FormatVersion\":1,\"Target\":\"t\",\"Mode\":\"ingame\",\"Case\":\"{}\"}");
            Assert.Equal("ingame", FuzzReplayFile.Read(path).Mode);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Target\":\"t\",\"Mode\":\"offgame\",\"Case\":\"{}\"}")]
    [InlineData("{\"FormatVersion\":2,\"Target\":\"t\",\"Mode\":\"offgame\",\"Case\":\"{}\"}")]
    [InlineData("{\"FormatVersion\":\"1\",\"Target\":\"t\",\"Mode\":\"offgame\",\"Case\":\"{}\"}")]
    [InlineData("{\"FormatVersion\":1,\"Mode\":\"offgame\",\"Case\":\"{}\"}")]
    [InlineData("{\"FormatVersion\":1,\"Target\":\" \",\"Mode\":\"offgame\",\"Case\":\"{}\"}")]
    [InlineData("{\"FormatVersion\":1,\"Target\":\"t\",\"Case\":\"{}\"}")]
    [InlineData("{\"FormatVersion\":1,\"Target\":\"t\",\"Mode\":\"both\",\"Case\":\"{}\"}")]
    [InlineData("{\"FormatVersion\":1,\"Target\":\"t\",\"Mode\":\"OffGame\",\"Case\":\"{}\"}")]
    [InlineData("{\"FormatVersion\":1,\"Target\":\"t\",\"Mode\":\"offgame\"}")]
    [InlineData("{\"FormatVersion\":1,\"Target\":\"t\",\"Mode\":\"offgame\",\"Case\":\"\"}")]
    [InlineData("{\"FormatVersion\":1,\"Target\":\"t\",\"Mode\":\"offgame\",\"Case\":\"not json\"}")]
    [InlineData("{\"FormatVersion\":1,\"Target\":\"t\",\"Mode\":\"offgame\",\"Case\":\"[]\"}")]
    [InlineData("{\"FormatVersion\":1,\"Target\":\"t\",\"Mode\":\"offgame\",\"Case\":{}}")]
    public void Replay_file_rejects_malformed_content(string json)
    {
        var path = TemporaryFile();
        try
        {
            File.WriteAllText(path, json);
            Assert.Throws<InvalidDataException>(() => FuzzReplayFile.Read(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Replay_file_requires_a_path_and_writes_only_complete_files()
    {
        Assert.Throws<ArgumentNullException>(() => FuzzReplayFile.Read(null));
        Assert.Throws<ArgumentException>(() => FuzzReplayFile.Read(" "));
        var path = TemporaryFile();
        Assert.Throws<InvalidOperationException>(() => new FuzzReplayFile { Target = "t", Mode = "both", Case = "{}" }.Write(path));
        Assert.Throws<InvalidOperationException>(() => new FuzzReplayFile { FormatVersion = 2, Target = "t", Mode = "offgame", Case = "{}" }.Write(path));
        Assert.False(File.Exists(path));
        Assert.Throws<ArgumentNullException>(() => new FuzzReplayFile { Target = "t", Mode = "offgame", Case = "{}" }.Write(null));
    }

    private static FuzzTarget Target(Type holder, string method) =>
        Assert.Single(FuzzTargets.Discover(holder.Assembly), target => target.Factory.DeclaringType == holder && target.Factory.Name == method);

    private static string TemporaryFile() => Path.Combine(Path.GetTempPath(), "wotr-fuzz-" + Guid.NewGuid().ToString("N") + ".json");

    public interface ICounter { Task Add(int value); int Read(); }
    public sealed class Counter : ICounter {
        private int total;
        public async Task Add(int value) { await Task.Yield(); total = unchecked(total + value); }
        public int Read() => total;
    }

    public static class Registrations
    {
        public static int Factories;
        public static int Providers;
        public static readonly FuzzOptions Shared = new() { Cases = 7, MaxCalls = 3, Seed = 4242 };

        [WotrFuzz]
        public static ICounter CreateCounter() { Interlocked.Increment(ref Factories); return new Counter(); }

        [WotrFuzz(Modes = WotrTestModes.Both, OptionsProvider = nameof(SharedOptions))]
        public static async Task<ICounter> CreateCounterAsync()
        {
            Interlocked.Increment(ref Factories);
            await Task.Yield();
            return new Counter();
        }

        [WotrFuzz(Modes = WotrTestModes.InGame)]
        public static ICounter CreateInGame() { Interlocked.Increment(ref Factories); return new Counter(); }

        [WotrFuzz(OptionsProvider = nameof(LedgerOptions))]
        public static Task<AsyncFuzzTests.IAsyncLedger> CreateLedger() => Task.FromResult<AsyncFuzzTests.IAsyncLedger>(new AsyncFuzzTests.AsyncLedger());

        [WotrFuzz]
        public static ICounter ThrowingFactory() => throw new ApplicationException("factory");

        [WotrFuzz(OptionsProvider = nameof(ThrowingOptions))]
        public static ICounter WithThrowingOptions() { Interlocked.Increment(ref Factories); return new Counter(); }

        [WotrFuzz(OptionsProvider = nameof(NullOptions))]
        public static ICounter WithNullOptions() { Interlocked.Increment(ref Factories); return new Counter(); }

        public static FuzzOptions SharedOptions() { Interlocked.Increment(ref Providers); return Shared; }
        public static FuzzOptions LedgerOptions() => new() { Cases = 100, MaxCalls = 10, Seed = 31 };
        public static FuzzOptions ThrowingOptions() { Interlocked.Increment(ref Providers); throw new ApplicationException("options"); }
        public static FuzzOptions NullOptions() => null;
    }

    public static class InvalidRegistrations
    {
        [WotrFuzz] internal static ICounter NotPublic() => new Counter();
        [WotrFuzz] public static ICounter WithParameter(int value) => new Counter();
        [WotrFuzz] public static ICounter Generic<T>() => new Counter();
        [WotrFuzz] public static Counter Concrete() => new Counter();
        [WotrFuzz] public static Task<Counter> ConcreteTask() => Task.FromResult(new Counter());
        [WotrFuzz] public static Task UntypedTask() => Task.CompletedTask;
        [WotrFuzz] public static ValueTask<ICounter> ValueTaskFactory() => new(new Counter());
        [WotrFuzz] public static void Nothing() { }
        [WotrFuzz(Modes = 0)] public static ICounter NoModes() => new Counter();
        [WotrFuzz(Modes = (WotrTestModes)4)] public static ICounter UnknownMode() => new Counter();
        [WotrFuzz(OptionsProvider = "Missing")] public static ICounter MissingProvider() => new Counter();
        [WotrFuzz(OptionsProvider = nameof(Overloaded))] public static ICounter AmbiguousProvider() => new Counter();
        [WotrFuzz(OptionsProvider = nameof(WrongReturn))] public static ICounter WrongProviderReturn() => new Counter();
        [WotrFuzz(OptionsProvider = nameof(WithSeed))] public static ICounter ProviderWithParameter() => new Counter();
        [WotrFuzz(OptionsProvider = nameof(Hidden))] public static ICounter PrivateProvider() => new Counter();

        public static FuzzOptions Overloaded() => new();
        public static FuzzOptions Overloaded(int seed) => new() { Seed = seed };
        public static object WrongReturn() => new FuzzOptions();
        public static FuzzOptions WithSeed(int seed) => new() { Seed = seed };
        private static FuzzOptions Hidden() => new();
    }

    public sealed class InstanceRegistrations
    {
        [WotrFuzz] public ICounter Instance() => new Counter();
        [WotrFuzz(OptionsProvider = nameof(InstanceOptions))] public static ICounter InstanceProvider() => new Counter();
        public FuzzOptions InstanceOptions() => new();
    }

    public static class GenericHolder<T>
    {
        [WotrFuzz] public static ICounter Create() => new Counter();
    }
}
