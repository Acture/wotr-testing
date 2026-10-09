# Implementation

| Path | Contents |
| --- | --- |
| [`WotR.Testing.Offline/`](WotR.Testing.Offline/README.md) | Offline test library and its MSBuild targets (`build/`) |
| `WotR.Testing.MonoHost/` | `wotr-mono-host`: starts the game's own Unity Mono runtime from the inputs to run the tests ([how](../docs/offline.md#runtimes)) |

Planned: `WotR.Testing.InGame/` for tests that need the Unity engine, and a shared
`WotR.Testing` layer once both need it. The test runner and snapshot scripts live in
the top-level `scripts/`, because consumers call them by path through the
submodule.

Projects reference game assemblies at compile time only and contain no game files.
Build outputs go to the ignored `artifacts/` directory.
