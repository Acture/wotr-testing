# Training examples

Python training programs for the offline battle environment
([docs/offline.md](../../docs/offline.md#battle-environment)). The battle runs in a
test process inside the offline runtime; these programs connect to it over a local
TCP port (`BattleEnvironmentServer`, one JSON object per line) and only send
decisions. They need a local WotR installation, as the battle tests do.

## Running

Start the server: the battle fixture's `TrainingServer` test serves one client while
`WOTR_TRAIN_PORT` is set, and does nothing otherwise.

```powershell
$env:WOTR_TRAIN_PORT = 47511
pwsh -NoProfile -File scripts/Invoke-WotrOfflineTests.ps1 `
  -Project tests/fixtures/BattleTests/WotR.Testing.BattleTests.csproj `
  -Filter "FullyQualifiedName~TrainingServer" -TimeoutMinutes 300
```

Then, from this directory, run one program; it waits for the server and closes it
when it ends. The scenarios (`1v1`, `1v3`, `2v3`, `2v4`: party fighters against
cultists on the Kenabres mesh) are defined in
`tests/fixtures/BattleTests/TrainingServer.cs`.

```powershell
uv run --no-project --with numpy python reinforce.py --scenario 2v3
uv run --no-project --with numpy python planner.py --scenario 1v3
uv run --no-project --with numpy python opd.py --scenario 1v3
uv run --no-project --python 3.12 --with sb3-contrib==2.4.0 --with gymnasium==1.0.0 --with numpy python ppo.py --scenario 1v3
```

| File | What it is |
| --- | --- |
| `client.py` | The connection: `start`, `reset`, `step` with candidate indices, `close` |
| `rewards.py` | Rewards from observations: per-step reward (damage dealt, health lost, units downed, resources spent: ability resources, spell slots by level, item uses; the outcome; a step-limit cutoff counts as a loss) and a win-based value |
| `features.py` | Features of one candidate action relative to the unit that takes it |
| `policy.py` | A linear softmax policy over candidate features |
| `reinforce.py` | REINFORCE with the linear policy and the per-step reward |
| `planner.py` | Random-rollout planning compared with a random and an attack-the-nearest policy on the same seeds |
| `opd.py` | On-policy distillation: the linear student plays, a rollout teacher scores the candidates of the states it reaches |
| `gym_env.py` | A Gymnasium environment with action masks: one decision per ready unit, toggles go with the unit's command |
| `ppo.py` | MaskablePPO with a policy that scores each candidate with one shared network |

## Limits

- One client per server run; start the server again for the next program.
- The environment has no save and restore. Rollouts rebuild a state by resetting to
  the episode's seed and replaying its choices, which the environment's determinism
  makes exact, so a rollout costs more the longer the episode has run.
- Candidates that stand for any point on the ground (`MoveToAnyPoint`,
  `UseAbilityAtAnyPoint`) are left out: the policies here choose among listed
  candidates only.
- The candidates change from step to step, so policies score each candidate from its
  features; a trainer's default network over candidate positions does not fit them.
