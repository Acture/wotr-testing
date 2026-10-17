# Tests

`check_repository.py` checks required entries, public links, instruction aliases,
private-material boundaries and that no game files are tracked;
`test_check_repository.py` covers those rules. CI runs both.

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
Real game behavior is verified through a consumer mod's offline tests (see
[CLAUDE.md](../CLAUDE.md)).
