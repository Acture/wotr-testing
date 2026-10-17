# Implementation

| Path | Contents |
| --- | --- |
| [`WotR.Testing/`](WotR.Testing/README.md) | `WotR.Testing` meta-package: no code, depends on the components below |
| [`WotR.Testing.Offline/`](WotR.Testing.Offline/README.md) | Offline test library and its MSBuild targets (`build/`, `buildTransitive/`) |
| `WotR.Testing.MonoHost/` | `wotr-mono-host`: starts the game's own Unity Mono runtime from the inputs to run the tests ([how](../docs/offline.md#runtimes)); packed into the NuGet package's `tools/` |
| `WotR.Testing.Build/` | MSBuild tasks merging runsettings and validating native VSTest results and environment reports |
| `WotR.Testing.VSTest/` | Unity Mono runtime provider, SDK host dependency isolation and result logger |
| `WotR.Testing.VSTestBootstrap/` | Registers the runtime provider before entering the installed SDK's unmodified VSTest CLI |

Planned: `WotR.Testing.InGame/` for tests that need the Unity engine, added to the
`WotR.Testing` meta-package, and a shared library both use once they need one. The test runner and snapshot scripts live in
the top-level `scripts/`, because consumers call them by path through the
submodule.

Projects reference game assemblies at compile time only and contain no game files.
Build outputs go to the ignored `artifacts/` directory.
