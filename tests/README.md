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
