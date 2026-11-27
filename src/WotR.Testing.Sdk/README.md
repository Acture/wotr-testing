# WotR.Testing.Sdk

The developer SDK as a template package. It contains the test project template
for a UnityModManager mod of Pathfinder: Wrath of the Righteous; the template
itself is in `templates/WotR.Testing.Sdk/` in the
[source repository](https://github.com/Acture/wotr-testing).

```powershell
dotnet new install WotR.Testing.Sdk --version 0.1.0-preview.4
dotnet new wotr-test -n MyMod.OfflineTests
```

The generated project references `WotR.Testing` with pinned versions and the
pinned test framework, so no dependency is downloaded, unzipped or copied by
hand. Set `WotrModAssembly` and the game path, then run:

```powershell
dotnet test tests/MyMod.OfflineTests/MyMod.OfflineTests.csproj
```

The package holds no game files. Game assemblies are referenced at compile time
from the author's own installation and copied and rewritten on the test machine.

The guide is [docs/sdk.md](https://github.com/Acture/wotr-testing/blob/main/docs/sdk.md);
the library it uses is described in
[WotR.Testing.Offline](https://github.com/Acture/wotr-testing/blob/main/src/WotR.Testing.Offline/README.md).
License: AGPL-3.0 with an
[additional permission](https://github.com/Acture/wotr-testing/blob/main/LICENSE-EXCEPTION.md)
for use together with the game and Unity assemblies.
