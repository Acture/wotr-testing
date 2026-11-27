# MyMod offline tests

Offline tests for the mod: the mod runs against the real game assemblies, the
vanilla blueprint pack and the game's settings on the game's own Unity Mono
runtime. The game is never started.

## Before the first run

Set two things in `MyMod.OfflineTests.csproj`:

| Setting | Meaning |
| --- | --- |
| `WotrModAssembly` | The built mod DLL. Its `Info.json` and its private libraries are read from the same directory. |
| `WrathInstallDir` | The game directory. Leave it empty and set the `WOTR_INPUT_ROOT` environment variable instead, or point `WotrSnapshotDirectory` at a snapshot. |

The game files are licensed: never commit them, never upload them, and keep them
out of packages and workflow artifacts.

## Run

```powershell
dotnet test tests/MyMod.OfflineTests/MyMod.OfflineTests.csproj
dotnet test tests/MyMod.OfflineTests/MyMod.OfflineTests.csproj --filter "FullyQualifiedName~MyFeature" --logger trx
```

The run ends with the path of the environment report. It holds the loaded
assemblies and their hashes, the startup stages, the environment boundaries the
run reached, the mod's log and the test observations; `logs/` holds every
UnityModManager log line. A test that needs the Unity engine is reported as
`unity-runtime-required` and never as passed.

## Continuous integration

`ci/offline-tests.yml` is a workflow for a self-hosted Windows runner with the
game installed. Copy it to `.github/workflows/` and adapt the paths. The runner
is a machine with the game on its disk, so the workflow never runs a fork's
code: pull requests from forks are skipped, and a maintainer tests a fork
contribution by pushing its commits to a branch of this repository first.

## More

- SDK guide: https://github.com/Acture/wotr-testing/blob/main/docs/sdk.md
- How the offline environment works and what it cannot do:
  https://github.com/Acture/wotr-testing/blob/main/docs/offline.md
- Automatic interface fuzzing: https://github.com/Acture/wotr-testing/blob/main/docs/fuzzing.md
