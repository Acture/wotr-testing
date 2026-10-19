# Tests

`check_repository.py` checks required entries, public links, instruction aliases,
private-material boundaries and that no game files are tracked;
`test_check_repository.py` covers those rules. CI runs both.

`WotR.Testing.Fuzzing.Tests/` tests automatic interface method discovery, generated
call sequences, custom argument generators, shrinking, exact replay, cleanup and
failure classification on .NET 8. It needs no game files and runs in public CI:

```powershell
dotnet test tests/WotR.Testing.Fuzzing.Tests/WotR.Testing.Fuzzing.Tests.csproj
```

`WotR.Testing.InGame.Tests/` tests the in-progress in-game infrastructure without
the game: message framing (partial reads, combined messages, invalid lengths,
versions and JSON, disconnects), session checks (sequence gaps and duplicates,
missing final events, unfinished tests, count mismatches, dirty cleanup) and the
main-thread dispatcher driven by a simulated update loop (main-thread
continuations, next-frame semantics, wall-clock waits, the per-update budget,
cancellation and shutdown). It runs on .NET 8, and also on .NET Framework 4.8 on
Windows, whose serializer implementation the game's Mono shares. It does not show
that anything works inside the player:

```powershell
dotnet test tests/WotR.Testing.InGame.Tests/WotR.Testing.InGame.Tests.csproj
```

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

`test_fuzzing.py` uses `fixtures/FuzzTests/` to check FsCheck.Xunit received through
the meta-project, plus the automatic interface fuzzer: native discovery, generated samples, constrained generators,
shrinking, replay, exhausted preconditions, environment errors and native-only
skips on Mono/netfx. Restore that fixture as well, then use the same opt-in flag:

```powershell
dotnet restore tests/fixtures/FuzzTests/FuzzTests.csproj
$env:WOTR_TEST_INTEGRATION = '1'
python -m unittest discover -s tests -p 'test_*.py' -v
```

Real game behavior is verified through a consumer mod's offline tests (see
[CLAUDE.md](../CLAUDE.md)).
