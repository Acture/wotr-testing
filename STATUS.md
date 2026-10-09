# Project status

`WotR.Testing.Offline` is pre-release. Tasks and decisions live in the Linear
project `wotr-testing`; this file records verified behavior and its limits.

| Scope | Evidence | Limitation |
| --- | --- | --- |
| Library build | `dotnet build WotR.Testing.slnx` against WotR 2.7.0: 0 warnings, 0 errors | Needs a local game installation; public CI cannot build it |
| Offline mechanics, Unity Mono (default) | AttributeFeats offline tests: 10/10; TabletopTweaks-Base with TabletopTweaks-Core as a dependency mod (startup and an Improved Natural Armor stacking test): 2/2; WotR 2.7.0, game's Mono 5.11 | TTT checked from a local probe project, not a maintained suite; no save loaded, DLC treated as unavailable |
| Offline mechanics, .NET Framework | AttributeFeats: 9/10 (its byte-identical mod copy assertion fails by design since mod copies get `beforefieldinit` cleared); TabletopTweaks-Core fails at `mod-load` (Harmony cannot patch a generic type method) | Use the Unity Mono runtime for mods like TTT |
| DLC policies | TabletopTweaks-Base + Core pass 2/2 with `WotrDlc` `all`, `none`, `Dlc4` and `local`; reported queries follow the policy (Dlc4, Dlc6); no store contacted | `local` reads Steam's installed depots only |
| Mod libraries | FeatOrganizer (BlueprintCore 2.8.6) with DragonLibrary (2.8.7) in both load orders: the first copy is used, with warnings | First-loaded-wins verified on the game's Mono with two test mods |
| Game versions | 2.7.0 verified | Other versions report `[ENV_MISSING]` unless explicitly allowed |
| Library tests | None of its own | Verified only through a consumer mod |
| Repository structure | `tests/check_repository.py` and its unit tests, run in CI | Checks layout, links and tracked-file boundaries, not behavior |

Known limits are listed in [docs/offline.md](docs/offline.md#known-limits).
