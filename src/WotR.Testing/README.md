# WotR.Testing

Test tooling for Pathfinder: Wrath of the Righteous mods. This package has no code of
its own; it references the components:

| Package | Use |
|---|---|
| [`WotR.Testing.Offline`](https://www.nuget.org/packages/WotR.Testing.Offline) | Runs UnityModManager mods against the real game assemblies, blueprint pack and settings on the game's own Unity Mono runtime (or .NET Framework), without starting the game |

In-game testing is planned and will be added here.

Reference `WotR.Testing` from a .NET Framework 4.8 xUnit v2 test project and follow the
[WotR.Testing.Offline guide](https://github.com/Acture/wotr-testing/blob/main/src/WotR.Testing.Offline/README.md).
The packages contain no game files: game assemblies are referenced at compile time
from your own installation. License: AGPL-3.0 with an
[additional permission](https://github.com/Acture/wotr-testing/blob/main/LICENSE-EXCEPTION.md)
for use together with the game and Unity assemblies.
