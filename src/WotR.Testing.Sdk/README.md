# WotR.Testing.Sdk

The developer SDK as a template package. It contains the test project template
for a UnityModManager mod of Pathfinder: Wrath of the Righteous; the template
itself is in `templates/WotR.Testing.Sdk/` in the
[source repository](https://github.com/Acture/wotr-testing).

```powershell
# Build and install the package locally: it is not on a public feed, so a plain
# `dotnet new install WotR.Testing.Sdk` cannot find it. From the root of a clone
# of the source repository:
dotnet pack src/WotR.Testing.Sdk/WotR.Testing.Sdk.csproj -o artifacts/packages
dotnet new install artifacts/packages/WotR.Testing.Sdk.0.1.0-preview.4.nupkg

# Generate the test project, from the root of the mod's repository. The name
# replaces the mod's name in the namespace, the fixture class and the paths, so
# it must be a plain identifier without dots; the template adds the
# .OfflineTests suffix itself.
dotnet new wotr-test -n MyMod -o tests/MyMod.OfflineTests
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
