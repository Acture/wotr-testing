# Implementation

| Path | Contents |
| --- | --- |
| [`WotR.Testing/`](WotR.Testing/README.md) | `WotR.Testing` meta-package: no code, depends on the components below |
| [`WotR.Testing.Offline/`](WotR.Testing.Offline/README.md) | Offline test library and its MSBuild targets (`build/`, `buildTransitive/`) |
| [`WotR.Testing.Fuzzing/`](WotR.Testing.Fuzzing/README.md) | Automatic interface fuzzing, FsCheck argument generation, shrinking and saved-case replay; independent of game files |
| [`WotR.Testing.InGame/`](WotR.Testing.InGame/README.md) | In-game test API (`InGameFixture`, `InGameContext`, main-thread dispatcher) and the `InstallWotrInGameHost` target; packed by `WotR.Testing.InGame.Package/` with the host ([how](../docs/ingame.md)) |
| `WotR.Testing.InGame.Host/` | `WotR.Testing.Host`, the UnityModManager development mod that advertises itself, checks loaded mod builds and runs leases inside the game |
| `WotR.Testing.InGame.Protocol/` | Versioned messages, framing, session checks, host advertisement and the loopback TCP client |
| `WotR.Testing.Runner/`, `.Runner.Xunit/`, `.Runner.Fuzzing/` | The run server shared by the in-game host and the offgame worker, and the bridges to xUnit v2 and registered fuzz targets, loaded beside the test's own copies |
| `WotR.Testing.Client/` | Plans runs from metadata (`TestScope`, `ExecutionPlan`), finds or starts the game, runs partitions and writes run reports |
| `WotR.Testing.MonoHost/` | `wotr-mono-host`: starts the game's own Unity Mono runtime from the inputs to run the tests ([how](../docs/offline.md#runtimes)), and with `--worker` serves offgame partitions; packed into the NuGet package's `tools/` |
| `WotR.Testing.Build/` | MSBuild tasks planning the run, merging runsettings, installing the host and validating native VSTest results and environment reports |
| `WotR.Testing.VSTest/` | Unity Mono runtime provider, SDK host dependency isolation and result logger |
| `WotR.Testing.VSTestBootstrap/` | Registers the runtime provider and the planned sources before entering the installed SDK's unmodified VSTest CLI |
| `WotR.Testing.TestAdapter/` | VSTest adapter for the generated `.wotrtests` source: in-game cases and fuzz targets |
| `WotR.Testing.Platform/` | `wotr-testing-platform`, the Microsoft.Testing.Platform application `dotnet test` starts when `global.json` selects that runner |

The test runner and snapshot scripts live in the top-level `scripts/`, because
consumers call them by path through the submodule.

Projects reference game assemblies at compile time only and contain no game files.
Build outputs go to the ignored `artifacts/` directory.
