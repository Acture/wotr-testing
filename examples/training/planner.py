"""
Random-rollout planning, compared with a random and an attack-the-nearest policy on the same seeds.

At each decision the planner scores every candidate of each ready unit by rollouts: the candidate,
then uniformly random actions for a few steps, summing the discounted per-step rewards
(rewards.step_reward). The environment is deterministic, so a rollout rebuilds the current state by
resetting to the episode's seed and replaying its choices. Each rollout then rolls its own dice
(seeds.branch): the k-th rollouts of all candidates share a seed, and the episode's real step rolls
from none of them. The policies play the same held-out seeds (seeds.evaluation).
"""

from __future__ import annotations

import argparse
import csv
import math
import statistics
import time

import numpy as np
import seeds
from client import BattleClient, by_unit
from rewards import SIDE, health, step_reward


def random_choices(obs: dict, rng: np.random.Generator) -> list[int]:
	return [int(rng.choice(ix)) for ix in by_unit(obs).values()]


def nearest_attack_choices(obs: dict, rng: np.random.Generator) -> list[int]:
	"""Attack the nearest enemy when the game allows an attack, else wait."""
	units = {u["id"]: u for u in obs["units"]}
	choices = []
	for unit, ix in by_unit(obs).items():
		me = units[unit]
		attacks = [i for i in ix if obs["candidates"][i]["kind"] == "Attack"]
		if attacks:

			def distance(i: int, me: dict = me) -> float:
				target = units[obs["candidates"][i]["target"]]
				return math.hypot(target["x"] - me["x"], target["z"] - me["z"])

			choices.append(min(attacks, key=distance))
		else:
			waits = [i for i in ix if obs["candidates"][i]["kind"] in ("Wait", "EndTurn")]
			choices.append(waits[0] if waits else ix[0])
	return choices


class RolloutPlanner:
	"""
	Scores candidates by rollouts. With own_dice=False the rollouts continue the episode's random
	stream: a search that knows the dice of the episode's fixed seed, for demonstrations (demos.py)
	only, never for comparing or evaluating policies.
	"""

	def __init__(
		self,
		client: BattleClient,
		rollouts: int,
		horizon: int,
		gamma: float,
		own_dice: bool = True,
	) -> None:
		self.client, self.rollouts, self.horizon, self.gamma = client, rollouts, horizon, gamma
		self.own_dice = own_dice

	def replay(self, seed: int, history: list[list[int]]) -> dict:
		obs = self.client.reset(seed)
		for choices in history:
			obs = self.client.step(choices)["observation"]
		return obs

	def rollout(
		self, seed: int, history: list[list[int]], unit: str, candidate: int, k: int
	) -> float:
		"""The k-th rollout of a candidate, on the k-th rollout's dice and random actions."""
		rng = seeds.rollout_rng(seed, len(history), k)
		random_seed = seeds.branch(seed, len(history), k) if self.own_dice else None
		obs = self.replay(seed, history)
		choices = [
			candidate if obs["candidates"][ix[0]]["unit"] == unit else int(rng.choice(ix))
			for ix in by_unit(obs).values()
		]
		total, discount = 0.0, 1.0
		for _ in range(self.horizon):
			result = self.client.step(choices, random_seed)
			random_seed = None
			total += discount * step_reward(obs, result["observation"], result)
			discount *= self.gamma
			obs = result["observation"]
			if result["ended"]:
				break
			choices = random_choices(obs, rng)
		return total

	def choose(self, seed: int, history: list[list[int]], obs: dict) -> list[int]:
		choices = []
		for unit, ix in by_unit(obs).items():
			if len(ix) == 1:
				choices.append(ix[0])
				continue
			scores = {
				i: statistics.mean(
					self.rollout(seed, history, unit, i, k) for k in range(self.rollouts)
				)
				for i in ix
			}
			choices.append(max(scores, key=scores.__getitem__))
		return choices


def play(
	client: BattleClient, seed: int, policy: str, planner: RolloutPlanner, rng: np.random.Generator
) -> dict:
	t0 = time.time()
	obs = first = client.reset(seed)
	history: list[list[int]] = []
	while True:
		if policy == "rollout":
			choices = planner.choose(seed, history, obs)
			obs = planner.replay(seed, history)
		elif policy == "nearest":
			choices = nearest_attack_choices(obs, rng)
		else:
			choices = random_choices(obs, rng)
		result = client.step(choices)
		history.append(choices)
		obs = result["observation"]
		if result["ended"]:
			break
	own = [u for u in obs["units"] if u["side"] == SIDE]
	return {
		"policy": policy,
		"seed": seed,
		"winner": result["winner"] or "",
		"own_health": round(statistics.mean(health(u) for u in own), 3),
		"enemies_down": sum(not u["conscious"] for u in obs["units"] if u["side"] != SIDE),
		"steps": len(history),
		"game_s": round(obs["time"] - first["time"], 1),
		"wall_s": round(time.time() - t0, 1),
	}


def main() -> None:
	parser = argparse.ArgumentParser(description=__doc__)
	parser.add_argument("--port", type=int, default=47511)
	parser.add_argument("--scenario", default="1v3")
	parser.add_argument("--seeds", type=int, default=10, help="episodes of the planner")
	parser.add_argument("--baseline-seeds", type=int, default=30, help="episodes of each baseline")
	parser.add_argument("--rollouts", type=int, default=8, help="rollouts per candidate")
	parser.add_argument("--horizon", type=int, default=6)
	parser.add_argument("--gamma", type=float, default=0.95)
	parser.add_argument("--difficulty", help="game difficulty level (default: the scenario's)")
	parser.add_argument("--log", default="planner.csv")
	args = parser.parse_args()

	client = BattleClient(args.port)
	client.start(args.scenario)
	if args.difficulty:
		client.reset(seeds.train(0), args.difficulty)
	rng = np.random.default_rng(0)
	planner = RolloutPlanner(client, args.rollouts, args.horizon, args.gamma)
	runs = [(p, s) for p in ("random", "nearest") for s in seeds.evaluation(args.baseline_seeds)]
	runs += [("rollout", s) for s in seeds.evaluation(args.seeds)]
	rows = []
	with open(args.log, "w", newline="", encoding="utf-8") as handle:
		writer = None
		for policy, seed in runs:
			row = play(client, seed, policy, planner, rng)
			rows.append(row)
			writer = writer or csv.DictWriter(handle, fieldnames=list(row))
			if handle.tell() == 0:
				writer.writeheader()
			writer.writerow(row)
			handle.flush()
			print(row, flush=True)
	client.close()
	for policy in ("random", "nearest", "rollout"):
		part = [r for r in rows if r["policy"] == policy]
		if not part:
			continue
		wins = sum(r["winner"] == SIDE for r in part)
		print(
			f"{policy:8s} win {wins}/{len(part)} "
			f"own health {statistics.mean(r['own_health'] for r in part):.2f} "
			f"enemies down {statistics.mean(r['enemies_down'] for r in part):.2f}"
		)


if __name__ == "__main__":
	main()
