# Project status

`WotR.Testing.Offline` is pre-release. Tasks and decisions live in the Linear
project `wotr-testing`; this file records verified behavior and its limits.

| Scope | Evidence | Limitation |
| --- | --- | --- |
| Library build | `dotnet build WotR.Testing.slnx` against WotR 2.7.0: 0 warnings, 0 errors | Needs a local game installation; public CI cannot build it |
| Offline mechanics | AttributeFeats offline tests through the submodule: 9/9 passed on WotR 2.7.0 | One UnityModManager mod using BlueprintCore; other mod structures not yet verified (OSS-358) |
| Game versions | 2.7.0 verified | Other versions report `[ENV_MISSING]` unless explicitly allowed |
| Library tests | None of its own | Verified only through a consumer mod |
| Repository structure | `tests/check_repository.py` and its unit tests, run in CI | Checks layout, links and tracked-file boundaries, not behavior |

Known limits are listed in [docs/offline.md](docs/offline.md#known-limits).
