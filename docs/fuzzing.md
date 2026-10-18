# Interface fuzzing

Mod authors select an interface and provide a factory. `WotR.Testing.Fuzzing`
discovers its methods, generates arguments and sequences of calls, then shrinks
unexpected exceptions into a saved case that can be replayed. Authors do not need
to write a property for every method.

Reference `WotR.Testing` from the mod's test project for offline game testing.
`WotR.Testing.Fuzzing` is also available on its own for interfaces that need no
game inputs; it targets .NET Standard 2.0. See the
[offline setup](../src/WotR.Testing.Offline/README.md) for the project and game
inputs. Run tests with ordinary `dotnet test`.

## Register an interface

```csharp
using WotR.Testing.Fuzzing;
using Xunit;

public interface IMyModApi
{
    void SetBonus(int value);
    void Reset();
    int ReadBonus();
}

public sealed class ApiTests
{
    [Fact]
    public void FuzzMyApi() =>
        Fuzz.Check<IMyModApi>(() => new MyModApi());
}
```

`MyModApi` implements the selected interface. The factory returns a **new target
for every generated sequence**, and again for every shrinking attempt or replay.
Calls in one sequence share that instance so the fuzzer can find failures that
depend on previous calls. Targets implementing synchronous `IDisposable` are
disposed on both success and failure. An `async void Dispose` implementation is
rejected before API calls and is not invoked. The factory and cleanup must also
reset any shared mod state that those methods change.

Inherited interface methods and overloads are included. Arguments are generated
with FsCheck. Task-returning methods are awaited so asynchronous exceptions are
observed. Restrict the surface when necessary:

```csharp
var options = new FuzzOptions
{
    Cases = 200,       // default 100 sequences
    MaxCalls = 12,     // up to 20 calls per sequence by default
    Seed = 12345,
    MaxShrinks = 1000,
    IncludeMethod = method => method.Name != "WriteSettings"
};
Fuzz.Check<IMyModApi>(() => new MyModApi(), options);
```

The library fuzzes only the selected interface. Generic methods, ref/out
parameters and parameter types without a supported generator produce a clear
configuration error. An empty selected surface or invalid limits also fail.

## Constrain arguments and register domain types

Generated values may be outside the domain accepted by a method. Register a
constrained FsCheck arbitrary for such parameters:

```csharp
using FsCheck.Fluent;

var options = new FuzzOptions { Cases = 200 };
options.Generators.Register(
    Arb.From(Gen.Choose(3, 30), value => new[] { 3, 10 }.Where(x => x < value)));
Fuzz.Check<IMyModApi>(() => new MyModApi(), options);
```

This registration applies to all `int` parameters on the selected surface. Use a
domain type when different parameters need different domains. Custom types need
an explicit codec so a saved argument can be reconstructed exactly:

```csharp
public sealed class AbilityId
{
    public string Value { get; }
    public AbilityId(string value) => Value = value;
}

options.Generators.Register<AbilityId>(
    Arb.From(Gen.Elements(new AbilityId("fire"), new AbilityId("ice"))),
    serialize: id => id.Value,
    deserialize: value => new AbilityId(value));
```

Prefer stable IDs or small input values to live game objects. The codec must
preserve meaning and the factory must reconstruct state needed by each sequence.
Generated game objects are real inputs; the library does not substitute mocked
game rules. Keep arbitrary shrinkers finite and deterministic, and supply only
values valid for the declared parameter type.

Unexpected exceptions are findings by default, including `ArgumentException`.
An API that deliberately rejects invalid inputs can declare the exact errors it
expects:

```csharp
options.IsExpectedException = error =>
    error is ArgumentOutOfRangeException range && range.ParamName == "bonus";
```

Expected exceptions have a separate count in the report. Avoid broad exception
predicates that hide defects. Factory, generator, codec and disposal failures are
infrastructure failures rather than successful calls.

## Use real offline game state

Use the existing collection fixture so the game and mod initialize before the
factory is called:

```csharp
[Collection("My mod offline game")]
public sealed class GameApiTests
{
    private readonly MyModGame game;
    public GameApiTests(MyModGame game) => this.game = game;

    [Fact]
    public void FuzzMyApi() =>
        game.Fuzz<IMyModApi>(() => new MyModApi());
}
```

`game.Fuzz` calls `RequireGame`, classifies Unity native exceptions and records
the report in `environment.json.observations` under a `fuzz-` key, including the
seed, selected methods, case/call counts, failure and replay case. A sequence
that reaches unavailable Unity code is counted as skipped; it never counts as a
passing sequence. An all-skipped run fails. A mixed run reports both counts;
passing sequences do not establish anything about the skipped paths.

The generic `Fuzz.Check` entry point does not initialize the game. When it is
used directly with offline APIs, configure
`options.IsUnavailable = UnityNative.IsUnavailable` and manage the fixture
lifecycle yourself. Initialization errors must propagate.

## Inspect, shrink and replay

`Fuzz.Check` returns a successful `FuzzReport` or throws `FuzzFailureException`
whose `Report` contains the finding. `Fuzz.Run` returns the report without
throwing for findings, so tests using it must assert `Succeeded`.

The fuzzer removes unnecessary calls and shrinks arguments while retaining the
failing method and exception type. Shrinking is bounded by `MaxShrinks`; a
counterexample is smaller, not guaranteed globally minimal. Shrink attempts use
fresh instances and are counted separately from generated cases.

```csharp
try
{
    Fuzz.Check<IMyModApi>(() => new MyModApi(), options);
}
catch (FuzzFailureException failure)
{
    File.WriteAllText("fuzz-report.json", failure.Report.ToJson());
    File.WriteAllText("fuzz-case.json", failure.Report.ReplayCase);
    throw;
}
```

Replay executes the saved calls and arguments, rather than generating another
random sequence. Use the same runtime, interface version, method selection and
custom codecs; overload identities include runtime assembly names:

```csharp
var replay = Fuzz.Replay<IMyModApi>(
    () => new MyModApi(), File.ReadAllText("fuzz-case.json"), options);
Assert.True(replay.Succeeded, replay.ToJson());
```

That assertion fails until the saved defect is fixed. Changed interfaces or
missing codecs fail explicitly. A fixed seed is useful for repeating discovery
with unchanged code and generators; a saved case is the precise regression input.
Reports and cases may contain mod data supplied by the author. Keep game data
local; do not upload game files or snapshots in reports or release artifacts.

## Limits

The default finding criterion is an unexpected exception. The fuzzer cannot
infer intended gameplay semantics from an interface or return value. Put
invariants in an adapter around the real API when needed, or use FsCheck.Xunit
`[Property]` tests for those assertions; the meta-package includes FsCheck.Xunit
3.5.0. An adapter must still invoke the real mod/game methods, without replacing
their results with expected values.

This is bounded randomized testing, without coverage guidance or a game-world
model. It does not make arbitrary methods safe to call: select a test surface
whose side effects can be reset. A factory cannot isolate static state on its
own. Methods must finish; in-process synchronous calls cannot be forcibly
cancelled safely. The offline host's `WotrTestTimeoutSeconds` session timeout
(default 900 seconds) fails a hung test process. Standalone users should set
their test runner's process timeout.

Unity rendering, scenes and other native paths require in-game testing. The
offline framework records those paths as unavailable and never replaces their
effects with a successful result. See [offline limits](offline.md).
