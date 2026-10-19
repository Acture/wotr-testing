# Implementation

| Path | Contents |
| --- | --- |
| [`WotR.Testing/`](WotR.Testing/README.md) | `WotR.Testing` meta-package: no code, depends on the components below |
| [`WotR.Testing.Offline/`](WotR.Testing.Offline/README.md) | Offline test library and its MSBuild targets (`build/`, `buildTransitive/`) |
| [`WotR.Testing.Fuzzing/`](WotR.Testing.Fuzzing/README.md) | Automatic interface fuzzing, FsCheck argument generation, shrinking and saved-case replay; independent of game files |
| `WotR.Testing.MonoHost/` | `wotr-mono-host`: starts the game's own Unity Mono runtime from the inputs to run the tests ([how](../docs/offline.md#runtimes)); packed into the NuGet package's `tools/` |
| `WotR.Testing.Build/` | MSBuild tasks merging runsettings and validating native VSTest results and environment reports |
| `WotR.Testing.VSTest/` | Unity Mono runtime provider, SDK host dependency isolation and result logger |
| `WotR.Testing.VSTestBootstrap/` | Registers the runtime provider before entering the installed SDK's unmodified VSTest CLI |
| `WotR.Testing.InGame/` | In progress, not packaged: tests inside the running game. So far only the Unity-independent main-thread dispatcher |
| `WotR.Testing.InGame.Protocol/` | In progress, not packaged: versioned messages, framing and session checks between the external adapter and the in-game host |

Not yet implemented: the in-game UMM host, its external test adapter and the
`WotR.Testing` meta-package wiring; nothing has run inside the game. The test runner and snapshot scripts live in
the top-level `scripts/`, because consumers call them by path through the
submodule.

Projects reference game assemblies at compile time only and contain no game files.
Build outputs go to the ignored `artifacts/` directory.
