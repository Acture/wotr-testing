# Tests

`check_repository.py` checks required entries, public links, instruction aliases,
private-material boundaries and that no game files are tracked;
`test_check_repository.py` covers those rules. CI runs both.

## Test scopes

`WotR.Testing.slnx` lists the repository's test scopes, and `dotnet test` at the
repository root runs every scope it finds:

| Scope | Project | Needs |
|---|---|---|
| Fuzzing | `WotR.Testing.Fuzzing.Tests/` | nothing; .NET 8 |
| Offgame | `WotR.Testing.Offline.Tests/` | a WotR installation; the game's Unity Mono runtime |

The fuzzing scope covers automatic interface method discovery, generated call
sequences, custom argument generators, shrinking, exact replay, cleanup and
failure classification. The offgame scope runs the same fuzz targets and the
FsCheck properties on the game's own Unity Mono runtime, through the offline
harness. Without the game inputs the offgame scope reports itself as skipped:
the run neither fails nor passes silently.

```powershell
dotnet test
```

The offgame scope's dependencies follow the game inputs: the meta-project and
the offline library need them. Restore the scope again after the inputs
appear or disappear; a build without a restore then says so instead of failing
with missing types.

A failed fuzz run leaves its report, with the seed and the minimized replay
case, next to the run report in `artifacts/`.

## Repository checks

`test_dotnet_test.py` has opt-in CLI integration tests using the synthetic
`fixtures/MonoTests/` project and the locally installed game's Mono runtime. They
check discovery, Theory rows, filtering, runsettings, TRX, explicit netfx, host
dependency isolation, unchanged SDK method instructions, fresh reports,
initialization inputs, skips, crashes, timeouts and concurrent invocations. The
fixture contains no game rules or data. Public CI skips these tests because it
has no game installation.

On Windows with .NET SDK 8+, set the game path as for the library, then run:

```powershell
dotnet restore tests/fixtures/MonoTests/MonoTests.csproj
$env:WOTR_TEST_INTEGRATION = '1'
python -m unittest discover -s tests -p 'test_dotnet_test.py' -v
```

Diagnostics stay in ignored `artifacts/`; do not publish runtime or SDK caches.

`test_fuzzing.py` uses the offgame scope to check FsCheck.Xunit received through
the meta-project, plus the automatic interface fuzzer: native discovery,
generated samples, constrained generators, shrinking, replay, exhausted
preconditions, environment errors and native-only skips on Mono/netfx. Restore
that scope as well, then use the same opt-in flag:

```powershell
dotnet restore tests/WotR.Testing.Offline.Tests/WotR.Testing.Offline.Tests.csproj
$env:WOTR_TEST_INTEGRATION = '1'
python -m unittest discover -s tests -p 'test_*.py' -v
```

Real game behavior is verified through a consumer mod's offline tests (see
[CLAUDE.md](../CLAUDE.md)).
