using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FsCheck.Fluent;
using WotR.Testing.Fuzzing;
using Xunit;

namespace WotR.Testing.Fuzzing.Tests;

public class FuzzTests
{
    [Fact]
    public void Automatically_calls_inherited_overloads_and_cleans_each_case()
    {
        var instances = new List<Recorder>();
        var report = Fuzz.Check<IRecorder>(() => { var value = new Recorder(); instances.Add(value); return value; },
            new FuzzOptions { Cases = 60, MaxCalls = 8, Seed = 201 });
        Assert.True(report.Succeeded);
        Assert.Equal(60, report.CasesPassed);
        Assert.Equal(60, instances.Count);
        Assert.All(instances, instance => Assert.True(instance.Disposed));
        Assert.Contains(instances, instance => instance.Calls.Count > 1);
        Assert.Contains(instances.SelectMany(instance => instance.Calls), call => call.StartsWith("int:"));
        Assert.Contains(instances.SelectMany(instance => instance.Calls), call => call.StartsWith("string:"));
        Assert.Equal(2, report.SelectedMethods.Count);
    }

    [Fact]
    public void Finds_stateful_bug_minimizes_sequence_and_replays_exact_arguments()
    {
        var made = new List<BrokenState>();
        var failure = Assert.Throws<FuzzFailureException>(() => Fuzz.Check<IState>(() => {
            var value = new BrokenState(); made.Add(value); return value;
        }, new FuzzOptions { Cases = 100, MaxCalls = 12, Seed = 12345, MaxShrinks = 500 }));
        Assert.Equal(FuzzStatus.Finding, failure.Report.Status);
        Assert.Contains("Commit", failure.Report.Failure.Method);
        Assert.Equal(typeof(InvalidOperationException).FullName, failure.Report.Failure.ExceptionType);
        Assert.True(failure.Report.ShrinkAttempts > 0);
        Assert.All(made, value => Assert.True(value.Disposed));
        var replay = Fuzz.Replay<IState>(() => new BrokenState(), failure.Report.ReplayCase);
        Assert.Equal(FuzzStatus.Finding, replay.Status);
        Assert.Equal(failure.Report.ReplayCase, replay.ReplayCase);
        using var json = JsonDocument.Parse(replay.ReplayCase);
        Assert.Equal(2, json.RootElement.GetProperty("Calls").GetArrayLength());
        var repaired = Fuzz.Replay<IState>(() => new FixedState(), failure.Report.ReplayCase);
        Assert.True(repaired.Succeeded);
        Assert.Equal(1, repaired.CasesPassed);
    }

    [Fact]
    public void Seed_produces_the_same_calls_and_values()
    {
        List<string> Trace() {
            var trace = new List<string>();
            Fuzz.Check<IRecorder>(() => new Recorder(trace), new FuzzOptions { Cases = 10, MaxCalls = 5, Seed = 77 });
            return trace;
        }
        Assert.Equal(Trace(), Trace());
        Assert.NotEmpty(Trace());
    }

    [Fact]
    public void Async_failures_are_awaited_and_cleanup_runs()
    {
        var target = new AsyncFailure();
        var report = Fuzz.Run<IAsync>(() => target, new FuzzOptions { Cases = 1 });
        Assert.Equal(FuzzStatus.Finding, report.Status);
        Assert.Equal(typeof(ApplicationException).FullName, report.Failure.ExceptionType);
        Assert.True(target.Disposed);
    }

    [Fact]
    public void All_unavailable_cases_fail_without_claiming_a_pass()
    {
        var options = new FuzzOptions { Cases = 5, IsUnavailable = e => e.Message == "native" };
        var failure = Assert.Throws<FuzzFailureException>(() => Fuzz.Check<IAction>(() => new Unavailable(), options));
        Assert.Equal(FuzzStatus.AllSkipped, failure.Report.Status);
        Assert.Equal(5, failure.Report.CasesSkipped);
        Assert.Equal(0, failure.Report.CasesPassed);
        Assert.Equal(5, failure.Report.SkippedReasons.Count);
    }

    [Fact]
    public void Expected_exceptions_are_opt_in_and_do_not_hide_nested_unavailable()
    {
        Assert.Equal(FuzzStatus.Finding, Fuzz.Run<IAction>(() => new Expected()).Status);
        var options = new FuzzOptions { Cases = 2, IsExpectedException = e => e is ArgumentException };
        var result = Fuzz.Check<IAction>(() => new Expected(), options);
        Assert.True(result.ExpectedExceptions > 0);
        options.IsExpectedException = _ => true;
        options.IsUnavailable = e => e.Message == "native";
        Assert.Equal(FuzzStatus.AllSkipped, Fuzz.Run<IAction>(() => new Unavailable(), options).Status);
    }

    [Fact]
    public void Factory_and_dispose_failures_are_infrastructure_and_are_not_shrunk()
    {
        var factory = Fuzz.Run<IAction>(() => throw new ApplicationException("factory"));
        Assert.Equal(FuzzStatus.InfrastructureFailure, factory.Status);
        Assert.Equal("factory", factory.Failure.Kind);
        Assert.Equal(0, factory.ShrinkAttempts);
        var dispose = Fuzz.Run<IAction>(() => new BadDispose());
        Assert.Equal(FuzzStatus.InfrastructureFailure, dispose.Status);
        Assert.Equal("dispose", dispose.Failure.Kind);
        Assert.Equal(0, dispose.ShrinkAttempts);
    }

    [Fact]
    public void Custom_arbitrary_and_codec_support_exact_replay()
    {
        var options = new FuzzOptions { Cases = 1 };
        options.Generators.Register(Gen.Constant(new Token(123)).ToArbitrary(),
            value => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            text => new Token(int.Parse(text, System.Globalization.CultureInfo.InvariantCulture)));
        var original = Fuzz.Run<ICustom>(() => new CustomFailure(), options);
        Assert.Equal(FuzzStatus.Finding, original.Status);
        var seen = new List<int>();
        var replay = Fuzz.Replay<ICustom>(() => new CustomFailure(seen), original.ReplayCase, options);
        Assert.Equal(FuzzStatus.Finding, replay.Status);
        Assert.Equal(new[] { 123 }, seen);
    }

    [Fact]
    public void Selected_methods_only_are_validated_and_called()
    {
        var options = new FuzzOptions { Cases = 1, IncludeMethod = method => method.Name == "Safe" };
        Assert.True(Fuzz.Check<IUnsupported>(() => new Unsupported(), options).Succeeded);
        Assert.Throws<NotSupportedException>(() => Fuzz.Run<IUnsupported>(() => new Unsupported()));
        options.IncludeMethod = _ => false;
        Assert.Throws<ArgumentException>(() => Fuzz.Run<IUnsupported>(() => new Unsupported(), options));
    }

    [Fact]
    public void Rejects_invalid_limits_empty_interfaces_and_unsupported_signature()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Fuzz.Run<IAction>(() => new Expected(), new FuzzOptions { Cases = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fuzz.Run<IAction>(() => new Expected(), new FuzzOptions { MaxCalls = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fuzz.Run<IAction>(() => new Expected(), new FuzzOptions { MaxShrinks = -1 }));
        Assert.Throws<ArgumentException>(() => Fuzz.Run<IEmpty>(() => null));
        Assert.Throws<ArgumentException>(() => Fuzz.Run<Expected>(() => new Expected()));
        Assert.Throws<NotSupportedException>(() => Fuzz.Run<IRef>(() => null));
        Assert.Throws<NotSupportedException>(() => Fuzz.Run<IGeneric>(() => null));
        var options = new FuzzOptions();
        options.Generators.Register(Gen.Constant(new Token(1)).ToArbitrary());
        Assert.Throws<NotSupportedException>(() => Fuzz.Run<ICustom>(() => new CustomFailure(), options));
    }

    [Fact]
    public void Replay_rejects_wrong_interface_and_unknown_method_before_factory()
    {
        var failure = Fuzz.Run<IAction>(() => new Expected());
        Assert.Throws<ArgumentException>(() => Fuzz.Replay<IAsync>(() => throw new Exception("must not run"), failure.ReplayCase));
        var invalid = failure.ReplayCase.Replace("Act(", "Missing(");
        Assert.Throws<ArgumentException>(() => Fuzz.Replay<IAction>(() => throw new Exception("must not run"), invalid));
    }

    [Fact]
    public void Inherited_dispose_is_reserved_for_cleanup()
    {
        var instances = new List<DisposableApi>();
        var report = Fuzz.Check<IDisposableApi>(() => {
            var value = new DisposableApi(); instances.Add(value); return value;
        }, new FuzzOptions { Cases = 10, MaxCalls = 5 });
        Assert.Single(report.SelectedMethods);
        Assert.All(instances, value => Assert.Equal(1, value.DisposeCount));
    }

    [Fact]
    public void Async_void_is_rejected_before_invocation_and_still_disposed()
    {
        var target = new AsyncVoid();
        var report = Fuzz.Run<IAction>(() => target);
        Assert.Equal(FuzzStatus.InfrastructureFailure, report.Status);
        Assert.Equal("signature", report.Failure.Kind);
        Assert.False(target.Called);
        Assert.True(target.Disposed);
    }

    [Fact]
    public void Null_task_is_a_finding()
    {
        var result = Fuzz.Run<IAsync>(() => new NullTask(), new FuzzOptions { Cases = 1, MaxShrinks = 0 });
        Assert.Equal(FuzzStatus.Finding, result.Status);
        Assert.Contains("returned null", result.Failure.Message);
    }

    [Fact]
    public void Null_string_failure_remains_an_invocation_finding_during_shrinking()
    {
        var options = new FuzzOptions { Cases = 1 };
        options.Generators.Register(Gen.Constant<string>(null).ToArbitrary());
        var result = Fuzz.Run<IString>(() => new StringFailure(), options);
        Assert.Equal(FuzzStatus.Finding, result.Status);
        Assert.Equal("invocation", result.Failure.Kind);
    }

    [Fact]
    public void Shrinks_values_while_preserving_failure_method_and_exception_type()
    {
        var options = new FuzzOptions { Cases = 1, MaxCalls = 1 };
        options.Generators.Register(Arb.From(Gen.Constant(100), value => new[] { 0, value / 2, value - 1 }));
        var result = Fuzz.Run<IThreshold>(() => new ThresholdFailure(), options);
        Assert.Equal(FuzzStatus.Finding, result.Status);
        using var json = JsonDocument.Parse(result.ReplayCase);
        Assert.Equal("10", json.RootElement.GetProperty("Calls")[0].GetProperty("Arguments")[0].GetString());
        Assert.True(Fuzz.Replay<IThreshold>(() => new FixedThreshold(), result.ReplayCase, options).Succeeded);
    }

    [Fact]
    public void Mutable_arguments_are_fresh_for_every_shrink_and_replay()
    {
        var options = new FuzzOptions { Cases = 1, MaxCalls = 1 };
        options.Generators.Register(Gen.Constant(new[] { 123 }).ToArbitrary());
        var result = Fuzz.Run<IArray>(() => new MutatingFailure(), options);
        Assert.Equal(FuzzStatus.Finding, result.Status);
        Assert.Equal(FuzzStatus.Finding, Fuzz.Replay<IArray>(() => new MutatingFailure(), result.ReplayCase, options).Status);
        Assert.Equal(FuzzStatus.Finding, Fuzz.Replay<IArray>(() => new MutatingFailure(), result.ReplayCase, options).Status);
    }

    [Fact]
    public void Repeating_custom_shrinkers_are_bounded()
    {
        static IEnumerable<int> Repeat(int value) { while (true) yield return value; }
        var options = new FuzzOptions { Cases = 1, MaxCalls = 1, MaxShrinks = 7 };
        options.Generators.Register(Arb.From(Gen.Constant(100), Repeat));
        var result = Fuzz.Run<IThreshold>(() => new ThresholdFailure(), options);
        Assert.Equal(FuzzStatus.Finding, result.Status);
        Assert.Equal(7, result.ShrinkAttempts);
    }

    [Fact]
    public void Clone_has_an_independent_generator_registry()
    {
        var options = new FuzzOptions { Cases = 1, MaxCalls = 1 };
        options.Generators.Register(Gen.Constant(100).ToArbitrary());
        var clone = options.Clone();
        clone.Generators.Register(Gen.Constant(0).ToArbitrary());
        Assert.Equal(FuzzStatus.Finding, Fuzz.Run<IThreshold>(() => new ThresholdFailure(), options).Status);
        Assert.True(Fuzz.Check<IThreshold>(() => new ThresholdFailure(), clone).Succeeded);
    }

    [Fact]
    public void Built_in_scalar_codecs_preserve_boundary_values_exactly()
    {
        RoundTrip(true);
        RoundTrip(byte.MaxValue);
        RoundTrip(sbyte.MinValue);
        RoundTrip(short.MinValue);
        RoundTrip(ushort.MaxValue);
        RoundTrip(int.MinValue);
        RoundTrip(uint.MaxValue);
        RoundTrip(long.MinValue);
        RoundTrip(ulong.MaxValue);
        RoundTrip(float.NaN);
        RoundTrip(float.NegativeInfinity);
        RoundTrip(double.PositiveInfinity);
        RoundTrip(decimal.MinValue);
        RoundTrip('\ud800');
        RoundTrip("before\ud800after\0");
        RoundTrip<string>(null);
        RoundTrip<int?>(null);
        RoundTrip<int?>(int.MaxValue);
        RoundTrip((DayOfWeek)int.MinValue);
        RoundTrip(new[] { "\ud800", null, "" });
        RoundTrip(new[] { new[] { int.MaxValue }, null, new int[0] });

        static void RoundTrip<T>(T value)
        {
            var options = new FuzzOptions { Cases = 1, MaxCalls = 1, MaxShrinks = 0 };
            options.Generators.Register(Gen.Constant(value).ToArbitrary());
            var firstSeen = new List<T>();
            var first = Fuzz.Run<IValue<T>>(() => new ValueFailure<T>(firstSeen), options);
            Assert.Equal(FuzzStatus.Finding, first.Status);
            var secondSeen = new List<T>();
            var second = Fuzz.Replay<IValue<T>>(() => new ValueFailure<T>(secondSeen), first.ReplayCase, options);
            Assert.Equal(FuzzStatus.Finding, second.Status);
            Assert.Equivalent(value, Assert.Single(firstSeen), strict: true);
            Assert.Equivalent(value, Assert.Single(secondSeen), strict: true);
        }
    }

    [Fact]
    public void Default_generators_exercise_numeric_extrema_and_nullable_array_shapes()
    {
        var seen = new List<int>();
        Fuzz.Check<IValue<int>>(() => new ValueSink<int>(seen), new FuzzOptions { Cases = 100, MaxCalls = 3 });
        Assert.Contains(int.MinValue, seen);
        Assert.Contains(int.MaxValue, seen);
        Assert.Contains(0, seen);
        var arrays = new List<int?[]>();
        Fuzz.Check<IValue<int?[]>>(() => new ValueSink<int?[]>(arrays), new FuzzOptions { Cases = 100, MaxCalls = 3 });
        Assert.Contains(arrays, value => value == null);
        Assert.Contains(arrays, value => value?.Length == 0);
        Assert.Contains(arrays, value => value?.Any(item => item == null) == true);
    }

    [Fact]
    public void Cleanup_failure_overrides_expected_or_unavailable_invocation()
    {
        var options = new FuzzOptions { Cases = 1, IsExpectedException = _ => true, IsUnavailable = _ => true };
        var report = Fuzz.Run<IAction>(() => new UnavailableBadDispose(), options);
        Assert.Equal(FuzzStatus.InfrastructureFailure, report.Status);
        Assert.Equal("dispose", report.Failure.Kind);
        Assert.Equal(0, report.CasesPassed);
    }

    [Fact]
    public void Classifier_and_codec_failures_cannot_pass()
    {
        var classifier = Fuzz.Run<IAction>(() => new Expected(), new FuzzOptions {
            Cases = 1, IsExpectedException = _ => throw new InvalidOperationException("classifier")
        });
        Assert.Equal(FuzzStatus.InfrastructureFailure, classifier.Status);
        Assert.Equal("classifier", classifier.Failure.Kind);
        var options = new FuzzOptions { Cases = 1 };
        options.Generators.Register(Gen.Constant(new Token(1)).ToArbitrary(),
            _ => throw new InvalidOperationException("codec"), _ => new Token(1));
        var codec = Fuzz.Run<ICustom>(() => new CustomFailure(), options);
        Assert.Equal(FuzzStatus.InfrastructureFailure, codec.Status);
        Assert.Equal(0, codec.CasesPassed);
    }

    [Fact]
    public void Availability_checks_every_aggregate_branch_and_mixed_runs_keep_counts()
    {
        var options = new FuzzOptions { Cases = 2, MaxCalls = 1,
            IsUnavailable = exception => exception.Message == "native", IsExpectedException = _ => true };
        int factories = 0;
        var report = Fuzz.Check<IAction>(() => ++factories == 1 ? new AggregateUnavailable() : new NoFailure(), options);
        Assert.Equal(1, report.CasesPassed);
        Assert.Equal(1, report.CasesSkipped);
        Assert.Equal(0, report.ExpectedExceptions);
        using var json = JsonDocument.Parse(report.ToJson());
        Assert.Equal("Passed", json.RootElement.GetProperty("Status").GetString());
    }

    [Fact]
    public void Default_failure_message_exposes_the_minimized_replay_case()
    {
        var options = new FuzzOptions { Cases = 1, MaxCalls = 1 };
        options.Generators.Register(Gen.Constant(123).ToArbitrary());
        var failure = Assert.Throws<FuzzFailureException>(() => Fuzz.Check<IThreshold>(() => new ThresholdFailure(), options));
        Assert.Contains(failure.Report.ReplayCase, failure.Message);
        Assert.Contains("123", failure.Message);
    }

    [Fact]
    public void Byref_like_signatures_are_rejected_before_constructing_or_invoking_targets()
    {
        var target = new SpanApi();
        int factories = 0;
        var options = new FuzzOptions { Cases = 1, MaxCalls = 1 };
        var failure = Assert.Throws<NotSupportedException>(() => Fuzz.Run<IReadOnlySpanReturn>(() => {
            factories++; return target;
        }, options));
        Assert.Contains("byref-like", failure.Message);
        Assert.Throws<NotSupportedException>(() => Fuzz.Run<ISpanReturn>(() => { factories++; return target; }, options));
        Assert.Throws<NotSupportedException>(() => Fuzz.Run<ISpanParameter>(() => { factories++; return target; }, options));
        Assert.Equal(0, factories);
        Assert.Equal(0, target.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Async_void_cleanup_is_rejected_without_starting_calls_or_cleanup(bool explicitImplementation)
    {
        CleanupProbe target = explicitImplementation ? new ExplicitAsyncCleanup() : new AsyncCleanup();
        try
        {
            var report = Fuzz.Run<IAction>(() => target, new FuzzOptions { Cases = 1, MaxCalls = 1 });
            Assert.Equal(FuzzStatus.InfrastructureFailure, report.Status);
            Assert.Equal("signature", report.Failure.Kind);
            Assert.Equal(0, report.CallsAttempted);
            Assert.Equal(0, target.Calls);
            Assert.False(target.Started);
        }
        finally { target.Gate.TrySetResult(true); }
    }

    [Fact]
    public void Floating_point_nan_payloads_are_preserved_for_initial_invocation_and_replay()
    {
        const long doubleBits = 0x7ff8000000000001L;
        const int floatBits = 0x7fc00001;
        var doubles = new List<long>();
        var options = new FuzzOptions { Cases = 1, MaxCalls = 1, MaxShrinks = 0 };
        options.Generators.Register(Gen.Constant(BitConverter.Int64BitsToDouble(doubleBits)).ToArbitrary());
        var first = Fuzz.Run<IValue<double>>(() => new DoubleBitFailure(doubleBits, doubles), options);
        Assert.Equal(FuzzStatus.Finding, first.Status);
        Assert.Equal(new[] { doubleBits }, doubles);
        Assert.Equal(FuzzStatus.Finding, Fuzz.Replay<IValue<double>>(() => new DoubleBitFailure(doubleBits, doubles), first.ReplayCase, options).Status);
        Assert.Equal(new[] { doubleBits, doubleBits }, doubles);

        var singles = new List<int>();
        options.Generators.Register(Gen.Constant(BitConverter.Int32BitsToSingle(floatBits)).ToArbitrary());
        var singleFirst = Fuzz.Run<IValue<float>>(() => new FloatBitFailure(floatBits, singles), options);
        Assert.Equal(FuzzStatus.Finding, singleFirst.Status);
        Assert.Equal(new[] { floatBits }, singles);
        Assert.Equal(FuzzStatus.Finding, Fuzz.Replay<IValue<float>>(() => new FloatBitFailure(floatBits, singles), singleFirst.ReplayCase, options).Status);
        Assert.Equal(new[] { floatBits, floatBits }, singles);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MinValue)]
    [InlineData(unchecked((long)0xfff8000000000011UL))]
    [InlineData(0x7ff0000000000000L)]
    [InlineData(0x0000000000000001L)]
    public void Double_codec_preserves_signed_zero_nan_sign_and_other_bits(long bits)
    {
        var seen = new List<long>();
        var options = new FuzzOptions { Cases = 1, MaxCalls = 1, MaxShrinks = 0 };
        options.Generators.Register(Gen.Constant(BitConverter.Int64BitsToDouble(bits)).ToArbitrary());
        var report = Fuzz.Run<IValue<double>>(() => new DoubleBitFailure(bits, seen), options);
        Assert.Equal(FuzzStatus.Finding, report.Status);
        Assert.Equal(new[] { bits }, seen);
        Assert.Equal(FuzzStatus.Finding, Fuzz.Replay<IValue<double>>(() => new DoubleBitFailure(bits, seen), report.ReplayCase, options).Status);
        Assert.Equal(new[] { bits, bits }, seen);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    [InlineData(unchecked((int)0xffc00011U))]
    [InlineData(0x7f800000)]
    [InlineData(0x00000001)]
    public void Float_codec_preserves_signed_zero_nan_sign_and_other_bits(int bits)
    {
        var seen = new List<int>();
        var options = new FuzzOptions { Cases = 1, MaxCalls = 1, MaxShrinks = 0 };
        options.Generators.Register(Gen.Constant(BitConverter.Int32BitsToSingle(bits)).ToArbitrary());
        var report = Fuzz.Run<IValue<float>>(() => new FloatBitFailure(bits, seen), options);
        Assert.Equal(FuzzStatus.Finding, report.Status);
        Assert.Equal(new[] { bits }, seen);
        Assert.Equal(FuzzStatus.Finding, Fuzz.Replay<IValue<float>>(() => new FloatBitFailure(bits, seen), report.ReplayCase, options).Status);
        Assert.Equal(new[] { bits, bits }, seen);
    }

    public interface IBase { void Record(int value); }
    public interface IRecorder : IBase { void Record(string value); }
    public sealed class Recorder : IRecorder, IDisposable {
        public List<string> Calls { get; }
        public bool Disposed { get; private set; }
        public Recorder(List<string> calls = null) { Calls = calls ?? new List<string>(); }
        public void Record(int value) => Calls.Add("int:" + value);
        public void Record(string value) => Calls.Add("string:" + value);
        public void Dispose() => Disposed = true;
    }
    public interface IState { void Arm(int value); void Commit(); }
    public class BrokenState : IState, IDisposable {
        private bool armed;
        public bool Disposed { get; private set; }
        public void Arm(int value) { armed |= value != 0; }
        public virtual void Commit() { if (armed) throw new InvalidOperationException("armed state"); }
        public void Dispose() => Disposed = true;
    }
    public sealed class FixedState : BrokenState { public override void Commit() { } }
    public interface IAsync { Task Act(); }
    public sealed class AsyncFailure : IAsync, IDisposable {
        public bool Disposed { get; private set; }
        public async Task Act() { await Task.Yield(); throw new ApplicationException("after await"); }
        public void Dispose() => Disposed = true;
    }
    public interface IAction { void Act(); }
    public sealed class Unavailable : IAction { public void Act() => throw new ArgumentException("outer", new InvalidOperationException("native")); }
    public sealed class Expected : IAction { public void Act() => throw new ArgumentException("invalid input"); }
    public sealed class BadDispose : IAction, IDisposable { public void Act() { } public void Dispose() => throw new ApplicationException("dispose"); }
    public sealed class Token { public int Value { get; } public Token(int value) { Value = value; } }
    public interface ICustom { void Use(Token value); }
    public sealed class CustomFailure : ICustom {
        private readonly List<int> seen;
        public CustomFailure(List<int> seen = null) { this.seen = seen; }
        public void Use(Token value) { seen?.Add(value.Value); throw new ApplicationException("token"); }
    }
    public interface IUnsupported { void Safe(); void Bad(Token value); }
    public sealed class Unsupported : IUnsupported { public void Safe() { } public void Bad(Token value) => throw new Exception("excluded"); }
    public interface IEmpty { }
    public interface IRef { void Act(ref int value); }
    public interface IGeneric { void Act<T>(T value); }
    public interface IDisposableApi : IDisposable { void Act(); }
    public sealed class DisposableApi : IDisposableApi {
        public int DisposeCount { get; private set; }
        public void Act() { if (DisposeCount != 0) throw new InvalidOperationException("already disposed"); }
        public void Dispose() => DisposeCount++;
    }
    public sealed class AsyncVoid : IAction, IDisposable {
        public bool Called { get; private set; }
        public bool Disposed { get; private set; }
        public async void Act() { Called = true; await Task.Yield(); }
        public void Dispose() => Disposed = true;
    }
    public sealed class NullTask : IAsync { public Task Act() => null; }
    public interface IString { void Act(string value); }
    public sealed class StringFailure : IString { public void Act(string value) { if (value == null) throw new InvalidOperationException("null"); } }
    public interface IThreshold { void Act(int value); }
    public sealed class ThresholdFailure : IThreshold {
        public void Act(int value) { if (value < 0) throw new ApplicationException("different failure"); if (value >= 10) throw new InvalidOperationException("threshold"); }
    }
    public sealed class FixedThreshold : IThreshold { public void Act(int value) { } }
    public interface IArray { void Act(int[] values); }
    public sealed class MutatingFailure : IArray {
        public void Act(int[] values) { int original = values[0]; values[0] = 0; if (original == 123) throw new InvalidOperationException("original"); }
    }
    public interface IValue<T> { void Use(T value); }
    public sealed class ValueFailure<T> : IValue<T> {
        private readonly List<T> seen;
        public ValueFailure(List<T> seen) { this.seen = seen; }
        public void Use(T value) { seen.Add(value); throw new ApplicationException("value"); }
    }
    public sealed class ValueSink<T> : IValue<T> {
        private readonly List<T> seen;
        public ValueSink(List<T> seen) { this.seen = seen; }
        public void Use(T value) => seen.Add(value);
    }
    public sealed class UnavailableBadDispose : IAction, IDisposable {
        public void Act() => throw new InvalidOperationException("native");
        public void Dispose() => throw new ApplicationException("dispose");
    }
    public sealed class AggregateUnavailable : IAction {
        public void Act() => throw new AggregateException(new ArgumentException("validation"), new InvalidOperationException("native"));
    }
    public sealed class NoFailure : IAction { public void Act() { } }
    public interface IReadOnlySpanReturn { ReadOnlySpan<int> Read(); }
    public interface ISpanReturn { Span<int> Read(); }
    public interface ISpanParameter { void Use(ReadOnlySpan<int> value); }
    public sealed class SpanApi : IReadOnlySpanReturn, ISpanReturn, ISpanParameter {
        public int Calls { get; private set; }
        ReadOnlySpan<int> IReadOnlySpanReturn.Read() { Calls++; return new[] { 1 }; }
        Span<int> ISpanReturn.Read() { Calls++; return new[] { 1 }; }
        public void Use(ReadOnlySpan<int> value) { Calls++; }
    }
    public abstract class CleanupProbe : IAction {
        public int Calls { get; private set; }
        public bool Started { get; protected set; }
        public TaskCompletionSource<bool> Gate { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Act() => Calls++;
    }
    public sealed class AsyncCleanup : CleanupProbe, IDisposable {
        public async void Dispose() { Started = true; await Gate.Task; }
    }
    public sealed class ExplicitAsyncCleanup : CleanupProbe, IDisposable {
        async void IDisposable.Dispose() { Started = true; await Gate.Task; }
    }
    public sealed class DoubleBitFailure : IValue<double> {
        private readonly long failing;
        private readonly List<long> seen;
        public DoubleBitFailure(long failing, List<long> seen) { this.failing = failing; this.seen = seen; }
        public void Use(double value) {
            long bits = BitConverter.DoubleToInt64Bits(value); seen.Add(bits);
            if (bits == failing) throw new InvalidOperationException("exact double bits");
        }
    }
    public sealed class FloatBitFailure : IValue<float> {
        private readonly int failing;
        private readonly List<int> seen;
        public FloatBitFailure(int failing, List<int> seen) { this.failing = failing; this.seen = seen; }
        public void Use(float value) {
            int bits = BitConverter.SingleToInt32Bits(value); seen.Add(bits);
            if (bits == failing) throw new InvalidOperationException("exact float bits");
        }
    }
}
