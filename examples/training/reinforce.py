"""
REINFORCE with a linear softmax policy over candidate features (policy.LinearPolicy) and the per-
step reward (rewards.step_reward). Weights start at zero, so the first episodes are a uniform random
policy; the last episodes are played greedily on unseen seeds.
"""

from __future__ import annotations

import argparse
import csv
import time

import numpy as np
from client import BattleClient, by_unit
from policy import LinearPolicy
from rewards import SIDE, step_reward


def episode(
	client: BattleClient, obs: dict, policy: LinearPolicy, rng: np.random.Generator, greedy: bool
):
	steps = []
	while True:
		choices, grad = [], np.zeros_like(policy.w)
		for index in by_unit(obs).values():
			phi, p = policy.probs(obs, index)
			k = int(np.argmax(p)) if greedy else int(rng.choice(len(index), p=p))
			choices.append(index[k])
			grad += phi[k] - p @ phi
		result = client.step(choices)
		steps.append((grad, step_reward(obs, result["observation"], result)))
		obs = result["observation"]
		if result["ended"]:
			return steps, result


def main() -> None:
	parser = argparse.ArgumentParser(description=__doc__)
	parser.add_argument("--port", type=int, default=47511)
	parser.add_argument("--scenario", default="2v3")
	parser.add_argument("--episodes", type=int, default=200)
	parser.add_argument("--evaluate", type=int, default=30)
	parser.add_argument("--lr", type=float, default=0.05)
	parser.add_argument("--gamma", type=float, default=0.98)
	parser.add_argument("--turn-based", action="store_true")
	parser.add_argument("--log", default="reinforce.csv")
	args = parser.parse_args()

	client = BattleClient(args.port)
	obs = client.start(args.scenario, max_steps=300, turn_based=args.turn_based)
	rng = np.random.default_rng(0)
	policy = LinearPolicy()
	baseline = 0.0
	with open(args.log, "w", newline="", encoding="utf-8") as handle:
		log = csv.writer(handle)
		log.writerow(["phase", "episode", "return", "winner", "steps", "wall_s"])
		for n in range(args.episodes + args.evaluate):
			greedy = n >= args.episodes
			if n > 0:
				obs = client.reset(1 + n)
			t0 = time.time()
			steps, result = episode(client, obs, policy, rng, greedy)
			returns, g = [], 0.0
			for _, r in reversed(steps):
				g = r + args.gamma * g
				returns.append(g)
			returns.reverse()
			if not greedy:
				for (grad, _), gt in zip(steps, returns, strict=True):
					policy.w += args.lr * (gt - baseline) * grad
				baseline = 0.9 * baseline + 0.1 * float(np.mean(returns))
			total = sum(r for _, r in steps)
			log.writerow(
				[
					"eval" if greedy else "train",
					n,
					round(total, 3),
					result["winner"] or "",
					len(steps),
					round(time.time() - t0, 2),
				]
			)
			handle.flush()
			won = "won " if result["winner"] == SIDE else "lost"
			print(
				f"{'eval ' if greedy else 'train'} {n:4d} {won} "
				f"return {total:6.2f} steps {len(steps):3d}",
				flush=True,
			)
	print("weights:", policy.describe())
	client.close()


if __name__ == "__main__":
	main()
