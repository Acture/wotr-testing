# WotR.Testing.Fuzzing

Automatic fuzzing of explicitly selected mod interfaces. Target .NET Standard 2.0
with FsCheck 3.5.0. This package runs without game inputs and contains no game files.
Use the `WotR.Testing` meta-package and its offline fixture for APIs needing real
Wrath of the Righteous state; see the repository's [interface fuzzing guide](https://github.com/Acture/wotr-testing/blob/main/docs/fuzzing.md).

```csharp
using WotR.Testing.Fuzzing;

// IMyModApi is the interface your mod exposes; MyModApi implements it.
Fuzz.Check<IMyModApi>(() => new MyModApi(), new FuzzOptions
{
    Cases = 100,
    MaxCalls = 20,
    Seed = 12345,
    MaxShrinks = 1000
});
```

The library discovers public ordinary methods, including inherited methods and
overloads, and generates arguments and sequences automatically. Each sequence
shares one target; every case, shrinking attempt and replay constructs another.
`IDisposable.Dispose` is reserved for cleanup and runs after success or failure.
An `async void Dispose` implementation is rejected before API calls and is never
invoked because its completion and failures cannot be observed.
The factory and cleanup must reset any shared state changed by calls.

`Fuzz.Check` returns a successful `FuzzReport` or throws `FuzzFailureException`
with its `Report`. `Fuzz.Run` returns the report for inspection; the caller must
check `Succeeded`. Unexpected exceptions are findings. Factory, generator, codec,
classifier and cleanup exceptions are infrastructure failures. `CasesPassed`,
`CasesSkipped`, `CallsAttempted`, `CallsCompleted` and `ExpectedExceptions` describe
the generated cases; `ShrinkAttempts` counts candidate shrinking attempts
separately. `CallsCompleted` counts calls which returned successfully.

The options also support `IncludeMethod`, `IsExpectedException`, and
`IsUnavailable` predicates. Expected exceptions must be explicitly selected by
the author; they are counted separately. Availability checks inspect nested
exceptions, including every `AggregateException` branch, before checking expected
exceptions. An unavailable sequence is skipped, never passed. An all-skipped run
fails. Mixed runs report both counts and establish nothing about skipped paths.
Use `options.Clone()` to copy options and the typed registry before changing them.

Built-in argument generators include boundary and random values for `bool`,
`char`, all standard integral types, `float`, `double`, `decimal`, `string`, enums,
nullable supported values, and one-dimensional or jagged arrays of supported
types. Strings include null, empty, whitespace, Unicode and UTF-16 edge cases;
numbers include extrema and floating-point NaN/infinities. Float and double
arguments use hexadecimal IEEE bit patterns to preserve signed zero and NaN
signs and payloads during initial invocation and replay. Methods are validated
before running: generic methods, static methods, ref/out/pointer parameters,
ref/pointer returns, `ValueTask` returns, unsupported types and an empty selected
surface fail explicitly. Byref-like parameters and returns, including `Span<T>`
and `ReadOnlySpan<T>`, are also rejected before constructing a target because
reflection cannot invoke those signatures. Properties and events are excluded. `async void`
implementations fail before invocation; Task and Task&lt;T&gt; methods are awaited,
and returning a null Task is a finding.

Supply a domain-specific FsCheck arbitrary for constrained or custom values:

```csharp
using FsCheck.Fluent;

var options = new FuzzOptions();
options.Generators.Register(Arb.From(
    Gen.Choose(0, 30), value => new[] { 0, value / 2 }));

// Custom types require a lossless codec. The codec text is stored inside JSON.
options.Generators.Register<AbilityId>(
    Gen.Elements(new AbilityId("fire"), new AbilityId("ice")).ToArbitrary(),
    serialize: id => id.Value,
    deserialize: text => new AbilityId(text));
```

Registration applies to all parameters of the exact registered type, including
matching elements inside arrays and nullable values. A custom type must have an
explicit serializer/deserializer; the author is responsible for preserving all
relevant data. Codecs must reconstruct independent arguments, and generators,
codecs, predicates and shrinkers must finish. `MaxShrinks` bounds candidate
attempts even when a shrinker repeats or cycles; it cannot interrupt code that
never returns from generation or enumeration.

On a finding, save `failure.Report.ToJson()` and `failure.Report.ReplayCase`
locally. The latter records the interface assembly identity/version, exact
overload signatures, seed and minimized arguments. Arguments are reconstructed
before every attempt, so target mutations do not alter saved input values.

```csharp
var replay = Fuzz.Replay<IMyModApi>(
    () => new MyModApi(), File.ReadAllText("fuzz-case.json"), options);
Assert.True(replay.Succeeded, replay.ToJson());
```

Replay runs saved calls once without generation or shrinking. Use the same
interface version, runtime, selection and custom codecs; incompatible cases fail
before constructing a target. Shrinking preserves the failing overload and
exception type, within the configured budget; a global minimum is not promised.
A seed reproduces discovery only with unchanged code and generators.

This is bounded randomized exception testing. It does not infer gameplay rules or
assert on return values. Put invariants in an adapter that calls the real API, or
use explicit FsCheck properties. Methods can have side effects: select a surface
that is safe and resettable. The library has no unsafe in-process timeout; use
your test runner's process timeout (the offline runner supplies one). The
synchronous entry points should run without a synchronization context needed by
the target's asynchronous continuations. No background worker is used, preserving
the caller's thread for game APIs.

Reports and cases can contain author-supplied mod data. Keep game files and data
snapshots local; never publish them in reports, packages or test artifacts.

Licensed under AGPL-3.0-only, with the included `LICENSE-EXCEPTION.md` additional
permission for linking with the proprietary game and Unity engine assemblies.
