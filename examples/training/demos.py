"""
Demonstrations for a warm start (warm.py): episodes on training seeds (seeds.train), of which those
in the best tenth by return (--top) that also won are kept.

The behavior policy is "random" (uniform over each unit's candidates, --per-seed episodes per seed)
or "planner" (planner.RolloutPlanner, one episode per seed). With --dice episode the planner's
rollouts roll the episode's own dice: a search that knows the dice of the fixed seed and finds wins
where an honest one does not. Its choices fit those dice, so a policy warm-started from them still
has to be trained, and is evaluated on held-out seeds (seeds.evaluation) whose dice it has not seen.

The environment is deterministic, so an episode is its seed and its choices. The kept episodes are
played again to record their observations, which also checks that they reproduce. Output: one JSON
object per line with seed, return, winner and steps (the observation before each step and the
choices taken).
"""

from __future__ import annotations

import argparse
import json
import math
import time

import numpy as np
import seeds
from client import BattleClient
from planner import RolloutPlanner, random_choices
from rewards import SIDE, step_reward


def play(
	client: BattleClient,
	seed: int,
	planner: RolloutPlanner | None,
	rng: np.random.Generator,
) -> dict:
	"""One episode: its choices, its return (sum of rewards.step_reward) and the winner."""
	obs = client.reset(seed)
	history: list[list[int]] = []
	total = 0.0
	while True:
		if planner is None:
			choices = random_choices(obs, rng)
		else:
			choices = planner.choose(seed, history, obs)
			obs = planner.replay(seed, history)
		result = client.step(choices)
		total += step_reward(obs, result["observation"], result)
		history.append(choices)
		obs = result["observation"]
		if result["ended"]:
			winner = result["winner"] or ""
			return {"seed": seed, "return": total, "winner": winner, "choices": history}


def record(client: BattleClient, episode: dict) -> dict:
	"""Plays a kept episode again and records the observation before each step."""
	obs = client.reset(episode["seed"])
	steps = []
	for choices in episode["choices"]:
		steps.append({"obs": obs, "choices": choices})
		result = client.step(choices)
		obs = result["observation"]
	if (result["winner"] or "") != episode["winner"]:
		raise RuntimeError(f"seed {episode['seed']} did not reproduce: {result['winner']}")
	return {k: episode[k] for k in ("seed", "return", "winner")} | {"steps": steps}


def main() -> None:
	parser = argparse.ArgumentParser(description=__doc__)
	parser.add_argument("--port", type=int, default=47511)
	parser.add_argument("--scenario", default="1v3")
	parser.add_argument("--turn-based", action="store_true")
	parser.add_argument("--policy", choices=["random", "planner"], default="random")
	parser.add_argument("--seeds", type=int, default=100, help="training seeds to play")
	parser.add_argument("--per-seed", type=int, default=5, help="random episodes per seed")
	parser.add_argument("--top", type=float, default=0.1, help="fraction kept, by return")
	parser.add_argument("--dice", choices=["own", "episode"], default="own")
	parser.add_argument("--rollouts", type=int, default=8)
	parser.add_argument("--horizon", type=int, default=6)
	parser.add_argument("--gamma", type=float, default=0.95)
	parser.add_argument("--out", default="demos.jsonl")
	args = parser.parse_args()

	client = BattleClient(args.port)
	client.start(args.scenario, seed=seeds.train(0), turn_based=args.turn_based)
	planner = None
	if args.policy == "planner":
		planner = RolloutPlanner(
			client, args.rollouts, args.horizon, args.gamma, own_dice=args.dice == "own"
		)
	per_seed = args.per_seed if planner is None else 1
	episodes = []
	t0 = time.time()
	for n in range(args.seeds):
		seed = seeds.train(n)
		for k in range(per_seed):
			episode = play(client, seed, planner, np.random.default_rng([seed, k]))
			episodes.append(episode)
			print(
				f"seed {seed} #{k} winner {episode['winner'] or '-':8s} "
				f"return {episode['return']:6.2f} steps {len(episode['choices']):3d} "
				f"wall {time.time() - t0:6.0f}s",
				flush=True,
			)
	episodes.sort(key=lambda e: e["return"], reverse=True)
	top = episodes[: math.ceil(args.top * len(episodes))]
	kept = [e for e in top if e["winner"] == SIDE]
	wins = sum(e["winner"] == SIDE for e in episodes)
	print(
		f"episodes {len(episodes)} won {wins}; best {len(top)} by return, {len(kept)} of them won",
		flush=True,
	)
	if not kept:
		raise SystemExit("No episode in the best fraction won; try another policy or more seeds.")
	with open(args.out, "w", encoding="utf-8") as handle:
		for episode in kept:
			handle.write(json.dumps(record(client, episode)) + "\n")
	print(f"wrote {len(kept)} demonstrations to {args.out}", flush=True)
	client.close()


if __name__ == "__main__":
	main()
