# WotR.Testing

Test tooling for Pathfinder: Wrath of the Righteous mods. This package has no code of
its own; it references the components:

| Package | Use |
|---|---|
| [`WotR.Testing.Offline`](https://www.nuget.org/packages/WotR.Testing.Offline) | Runs UnityModManager mods against the real game assemblies, blueprint pack and settings on the game's own Unity Mono runtime (or .NET Framework), without starting the game |
| [`WotR.Testing.Fuzzing`](https://www.nuget.org/packages/WotR.Testing.Fuzzing) | Automatically generates calls to selected mod interfaces, shrinks failures and replays saved cases |
| [`FsCheck.Xunit`](https://www.nuget.org/packages/FsCheck.Xunit/3.5.0) | Native xUnit v2 `[Property]` tests with generated inputs, shrinking and seed replay |

In-game testing is planned and will be added here.

Reference `WotR.Testing` from a .NET Framework 4.8 xUnit v2 test project and follow the
[WotR.Testing.Offline guide](https://github.com/Acture/wotr-testing/blob/main/src/WotR.Testing.Offline/README.md).
Run it with `dotnet test`; the game's Unity Mono runtime is selected automatically.
Register an interface and a factory with `Fuzz.Check<IMyApi>(() => new MyApi())`,
or `game.Fuzz<IMyApi>(...)` to include results in the offline environment report.
See [interface fuzzing](https://github.com/Acture/wotr-testing/blob/main/docs/fuzzing.md)
for automatic generation, shrinking, exact replay and custom types. Consumers
using the Offline component directly also receive the fuzzing library.
The packages contain no game files: game assemblies are referenced at compile time
from your own installation. License: AGPL-3.0 with an
[additional permission](https://github.com/Acture/wotr-testing/blob/main/LICENSE-EXCEPTION.md)
for use together with the game and Unity assemblies.
