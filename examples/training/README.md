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

A warm start takes two server runs: one to collect demonstrations, one to train from
them.

```powershell
uv run --no-project --with numpy python demos.py --scenario 2v3 --policy random --seeds 100 --per-seed 5
uv run --no-project --python 3.12 --with sb3-contrib==2.4.0 --with gymnasium==1.0.0 --with numpy python ppo.py --scenario 2v3 --warm demos.jsonl
```

| File | What it is |
| --- | --- |
| `client.py` | The connection: `start`, `reset` (optionally on another difficulty level), `step` with candidate indices and an optional random seed, `close` |
| `seeds.py` | Separate seeds for training and held-out evaluation episodes, and each rollout's own random seed |
| `rewards.py` | Rewards from observations: per-step reward (damage dealt, health lost, units downed, resources spent: ability resources, spell slots by level, item uses; the outcome; a step-limit cutoff counts as a loss with every own unit downed) and a win-based value |
| `features.py` | Features of one candidate action relative to the unit that takes it |
| `policy.py` | A linear softmax policy over candidate features |
| `reinforce.py` | REINFORCE with the linear policy and the per-step reward |
| `planner.py` | Random-rollout planning compared with a random and an attack-the-nearest policy on the same held-out seeds |
| `opd.py` | On-policy distillation: the linear student plays, a rollout teacher scores the candidates of the states it reaches |
| `demos.py` | Demonstrations for a warm start: episodes on training seeds, of which those in the best tenth by return that won are kept |
| `warm.py` | Behavior cloning from demonstrations; `reinforce.py`, `opd.py` and `ppo.py` take `--warm` |
| `gym_env.py` | A Gymnasium environment with action masks: one decision per ready unit, toggles go with the unit's command; the observation includes the battle's difficulty settings, and with several levels each episode draws one from its seed |
| `ppo.py` | MaskablePPO with a policy that scores each candidate with one shared network; `--difficulties` mixes game difficulty levels in training and evaluates each |

## Limits

- One client per server run; start the server again for the next program.
- The environment has no save and restore. Rollouts rebuild a state by resetting to
  the episode's seed and replaying its choices, which the environment's determinism
  makes exact, so a rollout costs more the longer the episode has run.
- The replay also reaches the episode's random state. A rollout's first step
  therefore restarts the game's random numbers from a seed of its own
  (`seeds.branch`); otherwise it would roll the dice the episode's next step rolls
  and the planner or teacher would know which attack hits. The k-th rollouts of all
  candidates share a seed and their sampled actions, so candidates are compared on
  the same draws; the episode's real step rolls from none of them.
- Training episodes use seeds from 1 up, evaluation episodes seeds from 1,000,000
  up (`seeds.py`), so an evaluation never repeats a training battle.
- Rollouts cost time in proportion to their number (`--rollouts`, 8 per candidate by
  default), and each one replays the episode so far.
- Demonstrations from a planner with `--dice episode` come from a search that knows
  the dice of each fixed seed. It finds wins where honest rollouts find none, but its
  choices fit those dice; a policy warm-started from them still has to be trained,
  and is evaluated on held-out seeds.
- Candidates that stand for any point on the ground (`MoveToAnyPoint`,
  `UseAbilityAtAnyPoint`) are left out: the policies here choose among listed
  candidates only.
- The candidates change from step to step, so policies score each candidate from its
  features; a trainer's default network over candidate positions does not fit them.
