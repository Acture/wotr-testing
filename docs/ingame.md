# In-game testing

`WotR.Testing.InGame` runs xUnit v2 tests and registered fuzz targets inside the
running Pathfinder: Wrath of the Righteous player. It covers what
[offline testing](offline.md) cannot: Unity native code, scenes, unit views, loaded
saves and work that spans game frames. One `dotnet test` command runs offgame and
in-game tests and fuzz targets together; by default it runs everything the test
assembly declares.

Verified with WotR 2.7.0 (Unity 2020.3.48f1, UnityModManager 0.32.4) on Windows,
.NET SDK 10.0.401 and xUnit 2.9.2.

## How it works

| Part | Where it runs | Role |
|---|---|---|
| `WotR.Testing.Host` | In the game, as a UnityModManager mod | Advertises itself, accepts one run at a time, loads the test assembly and runs cases on Unity's main thread |
| Runner and bridges | In the game, loaded by the host | xUnit v2 discovery and execution, `[WotrFuzz]` targets, per-test timeouts |
| Client | In `dotnet test` | Plans the run, finds or starts the game, sends the run and turns events into test results |
| `wotr-mono-host --worker` | A separate process on the offgame runtime (the game's Unity Mono by default) | Runs offgame fuzz targets (and, with Microsoft.Testing.Platform, offgame tests) through the same protocol |

The host writes `%LOCALAPPDATA%/WotR.Testing/hosts/<pid>.json` with a loopback
TCP port and a random token. The client connects only to `127.0.0.1` and must send
that token. Messages are length-prefixed UTF-8 JSON (1 MiB limit) with sequence
numbers; a session that loses events, reports mismatched counts or stops sending
heartbeats fails instead of passing.

The design draft used named pipes. The game's Mono cannot wait on an overlapped
pipe handle, and a synchronous pipe serializes a pending read with writes, so the
transport is loopback TCP. The token file is in the user's local profile, which
other non-administrator users cannot read by default.

The host copies the test assembly and its dependencies into
`%LOCALAPPDATA%/WotR.Testing/shadow/<pid>/` before loading them, so a rebuild never
collides with a locked file. Libraries the game already has (loaded, or in
`Wrath_Data/Managed`, with the same version or without a strong name) are not
copied; the test uses the game's copy. Test code cannot be unloaded from the game
process: when the test assembly changed since the game loaded it, the host refuses
the run with `restart-required`.

## Set up

Reference `WotR.Testing` (it includes `WotR.Testing.InGame`) from an xUnit v2 test
project targeting .NET Framework 4.8, as for [offline tests](../src/WotR.Testing.Offline/README.md).
Install the host once, while the game is closed:

```powershell
dotnet build MyMod.Tests.csproj -t:InstallWotrInGameHost
```

This writes only `Mods/WotR.Testing.Host` in `WrathInstallDir`; it refuses while
the game runs. A test run never installs or updates the host. The host is a
development mod: it waits for runs and does nothing else; players do not need it.

The mod under test must be installed in the game as usual. Before a run, the host
compares the loaded modules with `WotrInGameMod` items (by default
`WotrModAssembly` and `WotrDependencyMod`) by module version ID. A different build
fails with `mod-not-loaded`; install the build you test and restart the game.

## Write tests

Cases without a `WotrMode` trait stay offgame, so existing projects are unchanged.
`[Trait("WotrMode", "ingame")]` runs a case (or every case of a class) only in the
game; `both` runs it once in each environment. Any other value, including a
different spelling such as `inGame`, fails the plan rather than silently never
running.

```csharp
[CollectionDefinition("In-game")]
public sealed class GameCollection : ICollectionFixture<InGameFixture> { }

[Trait("WotrMode", "ingame")]
[Collection("In-game")]
public sealed class WolfTests
{
    private readonly InGameFixture game;
    public WolfTests(InGameFixture game) => this.game = game;

    [Fact]
    public Task Wolf_spawns_with_a_view() => game.OnMainThread(async context => {
        await context.LoadSave(null, TimeSpan.FromMinutes(3)); // the latest local save
        var wolf = context.SpawnUnit(context.Blueprint<BlueprintUnit>("ea610d9e540af4243b1310a3e6833d9f"),
            new Vector3(3, 0, 3));
        await context.WaitUntil(() => wolf.View != null, TimeSpan.FromSeconds(30));
        Assert.True(wolf.IsInGame);
    });
}
```

`OnMainThread` runs the body on Unity's main thread; awaits inside it return to the
main thread. `NextFrame` and `WaitUntil` wait for later frames. `Own` (Unity
objects) and `Defer` (any action) register cleanup that runs in reverse order after
the body, also when it fails. `SpawnUnit` registers the unit's removal. A failing
cleanup fails the test with `[CLEANUP_FAILED]` and marks the session dirty; the
host refuses further runs until the game restarts.

| Helper | Use |
|---|---|
| `LoadSave(name, timeout)` | Loads a local save by name (`null`: the latest) through the main menu, or through the game when an area is loaded, and waits for the party. Tests never change the loaded game unless they call it |
| `RequireArea()` / `RequirePlayer()` | Fail with `[SCENARIO_REQUIRED]` when no area or party is loaded |
| `Blueprint<T>(guid)` | A loaded blueprint; a missing one fails with `[ASSET_MISSING]` |
| `SpawnUnit(blueprint, offset)` | Creates a unit near the main character through the game's entity creator and destroys it after the test |
| `AssertMainThread()` | Fails when code runs off the main thread |

`WotrGameFixture` (offline) fails with `[OFFGAME_ONLY]` when used in the game;
in-game cases use `InGameFixture`.

### Fuzz targets

Register an interface factory with `[WotrFuzz]`; `Modes` selects the environments
(`OffGame` by default, `InGame` or `Both`). See
[interface fuzzing](fuzzing.md#run-registered-targets-from-dotnet-test).

```csharp
public static class FuzzRegistrations
{
    [WotrFuzz(Modes = WotrTestModes.InGame)]
    public static IMyModApi CreateApi() => new MyModApi();
}
```

In the game, factories, calls and disposal run on Unity's main thread, and
`Task` methods may wait for frames.

## Run

```powershell
dotnet test                                    # every environment and kind the assembly declares
dotnet test -p:WotrTestMode=ingame             # in the game only
dotnet test -p:WotrTestKind=fuzz               # registered fuzz targets only
dotnet test -p:WotrInGameConnection=attach     # use a running game, never start one
dotnet test -p:WotrFuzzReplay=path/to/case.json
```

With .NET 10 and `"test": { "runner": "Microsoft.Testing.Platform" }` in
`global.json` (above the test project), `dotnet test` uses the WotR test
application and the short options:

```powershell
dotnet test --mode ingame --fuzz
dotnet test --tests --treenode-filter "/*/*/PlayerTests/**"
dotnet test --fuzz-seed 1234
dotnet test --fuzz-replay path/to/case.json
```

| Option | MSBuild property | Meaning |
|---|---|---|
| `--mode auto\|offgame\|ingame` | `WotrTestMode` | Environments (default `auto`: every environment the assembly declares) |
| `--tests` / `--fuzz` | `WotrTestKind=tests\|fuzz` | Only ordinary tests or only registered fuzz targets (default both) |
| `--fuzz-seed <n>` | `WotrFuzzSeed` | Seed for every fuzz target |
| `--fuzz-replay <file>` | `WotrFuzzReplay` | Replays one saved case exactly, in the environment it was recorded in |
| | `WotrInGameConnection` | `auto` (default), `attach` or `launch` |
| | `WotrInGamePid` | The game to attach to when several run |
| | `WotrInGameLaunchInBackground` | `true` (default): a started game gives the focus back while it loads |
| | `WotrInGameTestTimeoutSeconds` | Per case or fuzz target (default 60) |
| | `WotrInGameStartupTimeoutSeconds` | Game start and loading (default 300) |
| | `WotrInGameMod` (items) | Builds the host requires loaded (default: the mods under test) |

The selection only narrows the declared scope. A selection that leaves nothing to
run, contradicts itself (a fuzz seed or replay with tests only, `WotrRuntime=netfx`
for an in-game-only run, connection options for an offgame-only run) or names an
unknown value fails before anything runs, with a `[PLAN]` message. With
Microsoft.Testing.Platform, these messages appear as a failed test node, because
`dotnet test` does not show a test application's own option errors.

With VSTest, ordinary offgame tests keep VSTest's own xUnit adapter, filters,
loggers and the [offline checks](offline.md#running). In-game cases and fuzz
targets come from a generated `.wotrtests` source and appear in the same results.
`--filter` works on names and traits within the selected environments; select
environments with `WotrTestMode`, not with a `WotrMode` filter.

### Connection and focus

`auto` attaches to a running game with the host and starts `Wrath.exe` only when
no game runs. A running game without the host, a busy host, a dirty session or a
different mod build fails with its cause; `auto` never starts a second game.
`attach` fails with `[no-game]` when no game with the host runs. `launch` always
starts a game.

A game the run starts is closed afterwards (a close request, then termination
after 15 seconds), and the run waits until the process has exited; a game that was
already running stays open. When Steam replaces the started process, the client
follows the new game process that advertises the host. A game process without a
window is starting or exiting; it gets up to 20 seconds to advertise the host or
exit before it counts as a game without the host.

Started games load in the background: the host hands the focus back to the
window that had it, the way Alt+Tab would, and keeps the game updating while
unfocused during a session. It never minimizes the game; minimizing during startup
once stalled the player. `WotrInGameLaunchInBackground=false` lets the game keep
the focus.

The host accepts a run only when the game is ready: at the main menu or in a
loaded area. Until then it answers `not-ready`, and the client waits up to
`WotrInGameStartupTimeoutSeconds`.

Listing in-game cases (`--list-tests`) asks the host as well, so it attaches to or
starts a game like a run; `--mode offgame` lists without one.

### Results and reports

Each run writes `wotr/<project>/<run-id>/wotr-run.json` below `WotrReportDirectory`,
else below `--results-directory`, else in `wotr-reports` beside the test assembly.
It lists the requested scope, every partition with its counts, the partitions the
selection excluded and, with VSTest, the partitions VSTest's own adapter ran
(`runByVSTest`, reported in its TRX and `environment.json` instead), and a `log` of
how the run reached and left the game (`dotnet test` does not show these messages
when the run passes). Each partition
writes `<mode>-<kind>/partition.json`. In-game partitions include the host's
environment: game and Unity versions, readiness, loaded mods with their module
version IDs, the test assembly copy and its hash, and whether the session is dirty.

A fuzz finding is shrunk and saved as `<mode>-<kind>/replays/<target>.json`
(`FuzzReplayFile` format: `FormatVersion`, `Target`, `Mode`, `Case`). The failure
message names the file and the replay command.

Failures keep their cause:

| Result | Meaning |
|---|---|
| Assertion or exception | The test failed in the game |
| `[ASSET_MISSING]` | A blueprint the test needs is not loaded |
| `[SCENARIO_REQUIRED]` | The test needs an area, a party or a save the game does not have |
| `[CLEANUP_FAILED]` | A cleanup failed; the session is dirty |
| Timeout | A case ran longer than `WotrInGameTestTimeoutSeconds`; the session is dirty because the main thread cannot be interrupted safely |
| `restart-required` | The test assembly changed since the game loaded it |
| `mod-not-loaded` | A mod under test is missing or a different build |
| `session-busy` / `dirty` | Another run holds the host, or an earlier run left the session dirty |
| `[no-game]` | `attach` found no game with the host |

Zero selected cases, a missing final event, mismatched counts or a lost connection
fail the run.

## Limits

- **One game, one run.** The host accepts one run at a time. In-game cases share
  the game process and its state; cleanup through `Own`, `Defer` and `SpawnUnit`
  is the only isolation. Restart the game when a run marks the session dirty.
- **No unloading.** Changed test assemblies and mod builds need a game restart.
- **xUnit v2 only** in the game. Theory rows, collection and class fixtures work;
  other test frameworks are not supported.
- **Desktop session.** The game needs a desktop, its installed mods and usually
  Steam, so public CI cannot run in-game tests.
- **Long paths.** Reports nest by project, run and partition. Unity Mono (offgame
  partitions and the Mono test host) fails on paths near 260 characters; use a
  short checkout or results path, or set `WotrReportDirectory`.
- **Windows only**, like the offline runtime.
