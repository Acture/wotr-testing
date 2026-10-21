# WotR.Testing.InGame

Runs xUnit v2 tests and registered fuzz targets inside the running Pathfinder:
Wrath of the Righteous player, from `dotnet test`. Tests reach the real Unity
engine: native assets, unit views, loaded saves and work across game frames.
Game files come from your own installation and are never part of the package.

Usually referenced through the `WotR.Testing` meta-package, which also brings the
offline runner and interface fuzzing. Full documentation: [in-game testing](https://github.com/Acture/wotr-testing/blob/main/docs/ingame.md).

## Install the host once

The host is a small UnityModManager development mod that waits for test runs. It
never runs tests by itself and is not needed by players.

```powershell
dotnet build MyMod.Tests.csproj -t:InstallWotrInGameHost
```

This writes only `Mods/WotR.Testing.Host` in `WrathInstallDir` and refuses while
the game runs. Enable it in UnityModManager if needed and restart the game. A test
run never installs or updates the host.

## Write tests

Mark in-game cases with the `WotrMode` trait (`ingame`, or `both` for cases that
also run offline) and run game code through `InGameFixture.OnMainThread`:

```csharp
[CollectionDefinition("In-game")]
public sealed class GameCollection : ICollectionFixture<InGameFixture> { }

[Trait("WotrMode", "ingame")]
[Collection("In-game")]
public sealed class ViewTests
{
    private readonly InGameFixture game;
    public ViewTests(InGameFixture game) => this.game = game;

    [Fact]
    public Task Object_survives_a_frame() => game.OnMainThread(async context => {
        var marker = context.Own(new GameObject("Marker"));
        var frame = Time.frameCount;
        await context.NextFrame();
        Assert.True(Time.frameCount > frame);
    });
}
```

Bodies run on Unity's main thread; awaits return to it. `Own` and `Defer` register
cleanup that runs in reverse order after the body, also when it fails. A failed
cleanup fails the test and marks the session dirty: the game must be restarted
before the next run. `LoadSave`, `SpawnUnit`, `RequireArea` and `Blueprint` cover
common game state; a missing blueprint fails as `[ASSET_MISSING]`.

## Run

```powershell
dotnet test                                  # every environment the tests declare
dotnet test -p:WotrTestMode=ingame           # only in the game
dotnet test -p:WotrTestMode=ingame -p:WotrInGameConnection=attach
```

With the .NET 10 SDK and `"test": { "runner": "Microsoft.Testing.Platform" }` in
`global.json`, the short forms work: `dotnet test --mode ingame --fuzz`.

By default a run starts its own game, which gives the focus back while it loads
and is closed afterwards. When the game is already open, the run fails with
`[game-running]` instead of using it. `WotrInGameConnection=attach` runs in the
open game (unsaved progress is lost when a case loads a save); `auto` attaches or
starts. A running game without the host, a different build of a mod under test, a
busy or dirty host fail with their cause instead of starting another game. Changed
test assemblies need a game restart; they are not reloaded in place.
