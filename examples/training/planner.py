"""
Random-rollout planning, compared with a random and an attack-the-nearest policy on the same seeds.

At each decision the planner scores every candidate of each ready unit by rollouts: the candidate,
then uniformly random actions for a few steps, summing the discounted per-step rewards
(rewards.step_reward). The environment is deterministic, so a rollout rebuilds the current state by
resetting to the episode's seed and replaying its choices.
"""

from __future__ import annotations

import argparse
import csv
import math
import statistics
import time

import numpy as np
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
	def __init__(
		self,
		client: BattleClient,
		rollouts: int,
		horizon: int,
		gamma: float,
		rng: np.random.Generator,
	) -> None:
		self.client, self.rollouts, self.horizon, self.gamma, self.rng = (
			client,
			rollouts,
			horizon,
			gamma,
			rng,
		)

	def replay(self, seed: int, history: list[list[int]]) -> dict:
		obs = self.client.reset(seed)
		for choices in history:
			obs = self.client.step(choices)["observation"]
		return obs

	def rollout(self, seed: int, history: list[list[int]], unit: str, candidate: int) -> float:
		obs = self.replay(seed, history)
		choices = [
			candidate if obs["candidates"][ix[0]]["unit"] == unit else int(self.rng.choice(ix))
			for ix in by_unit(obs).values()
		]
		total, discount = 0.0, 1.0
		for _ in range(self.horizon):
			result = self.client.step(choices)
			total += discount * step_reward(obs, result["observation"], result)
			discount *= self.gamma
			obs = result["observation"]
			if result["ended"]:
				break
			choices = random_choices(obs, self.rng)
		return total

	def choose(self, seed: int, history: list[list[int]], obs: dict) -> list[int]:
		choices = []
		for unit, ix in by_unit(obs).items():
			if len(ix) == 1:
				choices.append(ix[0])
				continue
			scores = {
				i: statistics.mean(
					self.rollout(seed, history, unit, i) for _ in range(self.rollouts)
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
	parser.add_argument("--rollouts", type=int, default=2)
	parser.add_argument("--horizon", type=int, default=6)
	parser.add_argument("--gamma", type=float, default=0.95)
	parser.add_argument("--log", default="planner.csv")
	args = parser.parse_args()

	client = BattleClient(args.port)
	client.start(args.scenario)
	rng = np.random.default_rng(0)
	planner = RolloutPlanner(
		client, args.rollouts, args.horizon, args.gamma, np.random.default_rng(1)
	)
	runs = [(p, s) for p in ("random", "nearest") for s in range(100, 100 + args.baseline_seeds)]
	runs += [("rollout", s) for s in range(100, 100 + args.seeds)]
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
		wins = sum(r["winner"] == SIDE for r in part)
		print(
			f"{policy:8s} win {wins}/{len(part)} "
			f"own health {statistics.mean(r['own_health'] for r in part):.2f} "
			f"enemies down {statistics.mean(r['enemies_down'] for r in part):.2f}"
		)


if __name__ == "__main__":
	main()
