# Randomized property tests

`WotR.Testing` includes FsCheck.Xunit 3.5.0 starting with preview.3. Write an xUnit
`[Property]` describing an invariant; FsCheck generates inputs and shrinks a
failing input into a smaller counterexample. It runs through the same `dotnet test`
entry point, Unity Mono runtime, fixtures, filters and loggers as ordinary tests.

Projects using `WotR.Testing.Offline` directly, or an earlier meta-package, add
FsCheck themselves:

```xml
<PackageReference Include="FsCheck.Xunit" Version="3.5.0" />
```

Keep the xUnit v2 and Microsoft.NET.Test.Sdk references from the
[project setup](../src/WotR.Testing.Offline/README.md). Source consumers can
reference `src/WotR.Testing/WotR.Testing.csproj` instead of the Offline project to
receive FsCheck transitively; retain the explicit Offline targets import.

## First property

```csharp
using System.Linq;
using FsCheck.Xunit;

public sealed class CollectionProperties
{
    [Property(MaxTest = 200)]
    public bool ReversingTwiceRestoresTheSequence(int[] values)
        => values == null || Enumerable.Reverse(Enumerable.Reverse(values)).SequenceEqual(values);
}
```

This illustrates the generator and assertion API. For a mod, replace the invariant
with checks against the real mod/game objects. Returning `false` or throwing an
assertion fails the property.

```powershell
dotnet test tests/MyMod.Tests/MyMod.Tests.csproj --filter FullyQualifiedName~CollectionProperties --logger trx
dotnet test tests/MyMod.Tests/MyMod.Tests.csproj -p:WotrRuntime=netfx
```

One property produces one VSTest result, even when it checks 200 generated samples.
The FsCheck output, including the sample count, is recorded in TRX. Use `Verbose =
true` to include every generated argument; keep normal output compact.

## Constrain inputs and shrink within the domain

Choose inputs that make sense for the operation under test. For example, a small
signed stat bonus can use this generator, including a shrinker that stays in range:

```csharp
using System;
using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;

public static class BonusInputs
{
    public static Arbitrary<int> Bonuses()
        => Arb.From(Gen.Choose(-5, 10),
            value => value == 0 ? Array.Empty<int>() : new[] { 0, value / 2 }.Distinct());
}

public sealed class BonusProperties
{
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(BonusInputs) })]
    public void BonusIsInTheDeclaredDomain(int bonus) => Assert.InRange(bonus, -5, 10);
}
```

The provider overrides `int` generation for this property. Use distinct input
types when different arguments need different domains. Generate settings and
identifiers, then construct game objects inside the property. A precondition that
rejects every input exhausts the run and fails; use a constrained generator when
the valid domain is small.

## Game state belongs to each sample

Use the same shared `WotrGameFixture` collection as ordinary mechanics tests and
call `RequireGame()` before touching the game. Inside the property:

1. Create a fresh unit from the chosen blueprint and set the generated inputs.
2. Record a baseline with `OfflineGame.StatSnapshot(unit)`.
3. Add the mod feature, buff or other operation and check the real game state.
4. Remove the operation in `finally`, then check the unit returns to its baseline.
5. Dispose the unit in an outer `finally` when its offline disposal path is supported.

FsCheck calls the method again while shrinking. An assertion failure must still
run cleanup. Class/collection fixtures live across samples; they do not reset
units, blueprint edits, RNG state or mod globals for each invocation. Restore any
shared state you mutate and keep game properties sequential; do not enable
FsCheck's `Parallelism` against a shared game session.

The AC's Homebrew consumer probe checked 50 generated Strength scores on each
runtime, added and removed the real Strength-to-Dexterity feat, checked stat
restoration and disposed each unit. The existing 10 regression tests also passed
(11 VSTest results per runtime, including the property). The probe used prebuilt
mod outputs with deployment disabled.

For a mod feature that can be added and removed, the same pattern looks like
this. It uses `MyModGame` and the `game` collection from the library guide. Replace
the unit and feature identifiers with your own verified blueprints:

```csharp
using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Kingmaker.Blueprints.Classes;
using Kingmaker.UnitLogic;
using WotR.Testing.Offline;
using Xunit;

public static class StrengthInputs
{
    public static Arbitrary<int> Scores()
        => Arb.From(Gen.Choose(3, 30), value => new[] { 3, 10, 20 }.Where(x => x < value));
}

[Collection("game")]
public sealed class FeatureProperties
{
    private readonly MyModGame game;
    public FeatureProperties(MyModGame game) => this.game = game;

    [Property(MaxTest = 50, Arbitrary = new[] { typeof(StrengthInputs) })]
    public void RemovingTheFeatureRestoresStats(int strength)
    {
        game.RequireGame();
        var unit = OfflineGame.CreateUnit("<vanilla unit blueprint guid>");
        try {
            unit.Stats.Strength.BaseValue = strength;
            var before = OfflineGame.StatSnapshot(unit);
            var feature = OfflineGame.Blueprint<BlueprintFeature>("<your feature guid>");
            var fact = unit.Progression.Features.AddFeature(feature);
            try {
                Assert.True(fact.IsActive && fact.IsTurnedOn);
            }
            finally {
                UnitHelper.RemoveFact(unit, fact);
            }
            Assert.False(unit.Progression.Features.HasFact(feature));
            Assert.Equal(before, OfflineGame.StatSnapshot(unit));
        }
        finally {
            unit.Dispose();
        }
    }
}
```

## Replay a failure

Keep the original arguments, shrunk arguments and seed from the FsCheck failure
message. Copy the seed tuple into the property and rerun the same filtered test:

```csharp
[Property(MaxTest = 200, Replay = "123,457")]
```

FsCheck also prints the last failing step's seed and size; its three-part `Replay`
form can replay that step. Preserve the failing test's generator and size settings.
Replay controls FsCheck's input sequence. It does not seed the game's own RNG or
restore mod/game state; use the same game/mod versions and clean initial state.
Leave `Replay` unset for new exploration, and keep known counterexamples as
ordinary regression cases.

## Native-only paths and reports

Mark a property known to need the Unity engine before executing it:

```csharp
[Property(Skip = "[UNITY_RUNTIME_REQUIRED] Requires an active scene.")]
```

FsCheck 3.5.0 reports this as a skipped test result (`NotExecuted` in TRX). Its
xUnit case summary omits that skipped case from native TRX summary counters;
inspect individual results. WotR's logger receives and classifies the skipped
result, and skipped properties do not count toward `WotrMinimumPassed`.

FsCheck catches per-sample exceptions as failures. Throwing a dynamic skip inside
a property is therefore unsuitable; use an ordinary test for paths that must
detect Unity availability dynamically. Do not discard an input after a startup,
native-code or assertion error. Missing inputs and initialization failures remain
visible, and all-skipped, exhausted, zero-test and missing-report runs fail.

Verified with FsCheck.Xunit 3.5.0, xUnit 2.9.2 and SDK 10.0.401 on the game's Unity
Mono and .NET Framework. See the upstream [FsCheck guide](https://fscheck.github.io/FsCheck/RunningTests.html)
for configuration and [input generation](https://fscheck.github.io/FsCheck/TestData.html).
