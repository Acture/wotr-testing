"""
On-policy distillation: a linear student (policy.LinearPolicy) plays its own episodes, and at every
state it reaches a rollout teacher scores each ready unit's candidates; the student moves towards
the teacher's softmax with a cross-entropy step. Rollouts continue with the student's own policy.
The teacher's score is "dense", the discounted sum of per-step rewards over a short horizon
(rewards.step_reward), or "win", the win-based value at the rollout's end (rewards.win_value). The
environment is deterministic, so a rollout rebuilds the state by replaying the episode's choices
from its seed; its cost grows with the episode's length.
"""

from __future__ import annotations

import argparse
import csv
import statistics
import time

import numpy as np
from client import BattleClient, by_unit
from policy import LinearPolicy, softmax
from rewards import SIDE, balance, step_reward, win_value


class Teacher:
	def __init__(
		self,
		client: BattleClient,
		student: LinearPolicy,
		args: argparse.Namespace,
		rng: np.random.Generator,
	) -> None:
		self.client, self.student, self.args, self.rng = client, student, args, rng

	def replay(self, seed: int, history: list[list[int]]) -> dict:
		obs = self.client.reset(seed)
		for choices in history:
			obs = self.client.step(choices)["observation"]
		return obs

	def rollout(self, seed: int, history: list[list[int]], unit: str, candidate: int) -> float:
		obs = self.replay(seed, history)
		choices = []
		for index in by_unit(obs).values():
			if obs["candidates"][index[0]]["unit"] == unit:
				choices.append(candidate)
			else:
				_, p = self.student.probs(obs, index)
				choices.append(index[int(self.rng.choice(len(index), p=p))])
		total, discount = 0.0, 1.0
		for _ in range(self.args.horizon):
			result = self.client.step(choices)
			after = result["observation"]
			total += discount * step_reward(obs, after, result)
			discount *= self.args.gamma
			obs = after
			if result["ended"]:
				return total if self.args.teacher == "dense" else win_value(result, obs)
			choices = self.student.choose(obs, self.rng)
		return total if self.args.teacher == "dense" else win_value(None, obs)

	def scores(
		self, seed: int, history: list[list[int]], unit: str, subset: list[int]
	) -> np.ndarray:
		return np.array(
			[
				statistics.mean(
					self.rollout(seed, history, unit, i) for _ in range(self.args.rollouts)
				)
				for i in subset
			]
		)


def evaluate(
	client: BattleClient, student: LinearPolicy, seeds: range, rng: np.random.Generator, label: str
) -> None:
	for greedy in (True, False):
		wins, balances = 0, []
		for seed in seeds:
			obs = client.reset(seed)
			while True:
				result = client.step(student.choose(obs, rng, greedy))
				obs = result["observation"]
				if result["ended"]:
					break
			wins += result["winner"] == SIDE
			balances.append(balance(obs))
		print(
			f"eval {label:6s} {'greedy' if greedy else 'sampled':7s} win {wins}/{len(seeds)} "
			f"health balance {statistics.mean(balances):.3f}",
			flush=True,
		)


def main() -> None:
	parser = argparse.ArgumentParser(description=__doc__)
	parser.add_argument("--port", type=int, default=47511)
	parser.add_argument("--scenario", default="1v3")
	parser.add_argument("--episodes", type=int, default=12)
	parser.add_argument("--teacher", choices=["dense", "win"], default="dense")
	parser.add_argument("--rollouts", type=int, default=2)
	parser.add_argument("--horizon", type=int, default=8)
	parser.add_argument("--gamma", type=float, default=0.95)
	parser.add_argument(
		"--max-eval",
		type=int,
		default=6,
		help="candidates the teacher scores: the student's top ones and two others",
	)
	parser.add_argument("--temperature", type=float, default=0.3)
	parser.add_argument("--lr", type=float, default=0.3)
	parser.add_argument("--eval-seeds", type=int, default=20)
	parser.add_argument("--log", default="opd.csv")
	args = parser.parse_args()

	client = BattleClient(args.port)
	client.start(args.scenario)
	rng = np.random.default_rng(0)
	student = LinearPolicy()
	teacher = Teacher(client, student, args, np.random.default_rng(1))
	eval_seeds = range(500, 500 + args.eval_seeds)
	evaluate(client, student, eval_seeds, rng, "before")
	with open(args.log, "w", newline="", encoding="utf-8") as handle:
		log = csv.writer(handle)
		log.writerow(["episode", "seed", "winner", "steps", "teacher_scores", "wall_s"])
		for episode in range(args.episodes):
			seed = 300 + episode
			t0, queries = time.time(), 0
			history: list[list[int]] = []
			obs = client.reset(seed)
			while True:
				choices, targets = [], []
				for unit, index in by_unit(obs).items():
					phi, p = student.probs(obs, index)
					if len(index) > 1:
						order = [int(i) for i in np.argsort(-p)]
						if len(index) <= args.max_eval:
							subset = list(range(len(index)))
						else:
							top = order[: args.max_eval - 2]
							rest = [i for i in order if i not in top]
							subset = top + [int(i) for i in rng.choice(rest, size=2, replace=False)]
						q = teacher.scores(seed, history, unit, [index[i] for i in subset])
						queries += len(subset)
						targets.append((phi[subset], softmax(q / args.temperature)))
					choices.append(index[int(rng.choice(len(index), p=p))])
				if targets:
					obs = teacher.replay(seed, history)
				for phi_s, target in targets:
					student.w += args.lr * phi_s.T @ (target - softmax(phi_s @ student.w))
				result = client.step(choices)
				history.append(choices)
				obs = result["observation"]
				if result["ended"]:
					break
			log.writerow(
				[
					episode,
					seed,
					result["winner"] or "",
					len(history),
					queries,
					round(time.time() - t0, 1),
				]
			)
			handle.flush()
			print(
				f"train {episode:3d} winner {result['winner'] or '-':8s} steps {len(history):3d} "
				f"teacher scores {queries:4d} wall {time.time() - t0:6.1f}s",
				flush=True,
			)
	evaluate(client, student, eval_seeds, rng, "after")
	print("weights:", student.describe())
	client.close()


if __name__ == "__main__":
	main()
