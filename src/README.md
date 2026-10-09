# Implementation

| Path | Contents |
| --- | --- |
| [`WotR.Testing.Offline/`](WotR.Testing.Offline/README.md) | Offline test library and its MSBuild targets (`build/`) |

Planned: `WotR.Testing.InGame/` for tests that need the Unity engine, and a shared
`WotR.Testing` layer once both need it. The test runner and snapshot scripts live in
the top-level `scripts/`, because consumers call them by path through the
submodule.

Projects reference game assemblies at compile time only. Build outputs go to the
ignored `artifacts/` directory.
