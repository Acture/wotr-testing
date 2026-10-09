# Tests

`check_repository.py` checks required entries, public links, instruction aliases,
private-material boundaries and that no game files are tracked;
`test_check_repository.py` covers those rules. CI runs both.

The library has no behavior tests of its own yet; it is verified through a consumer
mod's offline tests (see [CLAUDE.md](../CLAUDE.md)). Add library tests and small
synthetic fixtures (under `fixtures/`, never game data) here.
