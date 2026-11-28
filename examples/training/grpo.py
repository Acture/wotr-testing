"""
Group relative policy optimization (GRPO) with fixed dice on the battle environment.

Each update plays a group of episodes from each of a few training seeds with fixed dice
(gym_env.BattleEnv(fixed_dice=True)): every battle step restarts the game's random numbers from a
seed of the episode's seed and the step's number, so the episodes of a group meet the same dice at
the same step and differ mostly by what the policy chose. An episode's advantage is its return
against the group's (minus the mean, over the standard deviation), given to every decision it
made; no value network is learned. The update is the clipped policy gradient of PPO over a few
epochs, with an entropy bonus. Groups whose episodes all score the same teach nothing and are
dropped (counted as "flat").

Episodes run in parallel: one worker process per server port (--ports), each with its own copy of
the policy, which gets the weights before every batch. Each worker numbers ability and buff texts
in its own table; the main process maps them to its table by guid, so every observation it trains
on points to the same embeddings the worker saw.

The policy is ppo.CandidateScorer's choice scores. Evaluation plays the random and
attack-the-nearest baselines and the policy (greedy and sampled) on held-out seeds, all with fixed
dice, so every policy meets the same dice on an evaluation seed.
"""

from __future__ import annotations

import argparse
import multiprocessing
import statistics
import time

import gymnasium as gym
import numpy as np
import ppo
import seeds
import texts
import torch
from gym_env import (
	MAX_BUFFS,
	MAX_CHOICES,
	MAX_UNITS,
	ROW_SIZE,
	STATE_SIZE,
	UNIT_FEATURES,
	UNIT_WIDTH,
	BattleEnv,
)
from rewards import SIDE
from torch import nn

# Where an observation holds text table rows: each unit's buffs and the last column of each row.
ID_COLUMNS = np.array(
	[u * UNIT_WIDTH + UNIT_FEATURES + k for u in range(MAX_UNITS) for k in range(MAX_BUFFS)]
	+ [STATE_SIZE + r * ROW_SIZE + ROW_SIZE - 1 for r in range(MAX_CHOICES)]
)


class Policy(nn.Module):
	def __init__(self, observation_space) -> None:
		super().__init__()
		self.scorer = ppo.CandidateScorer(observation_space)

	def forward(self, obs: torch.Tensor, mask: torch.Tensor) -> torch.distributions.Categorical:
		logits = self.scorer(obs)[:, :MAX_CHOICES].masked_fill(~mask, float("-inf"))
		return torch.distributions.Categorical(logits=logits)


def episode(env: BattleEnv, policy: Policy, seed: int, generator: torch.Generator, greedy=False):
	"""One episode: its decisions (observation, mask, action, log-probability), return and win."""
	obs, _ = env.reset(seed=seed)
	steps, total, done = [], 0.0, False
	while not done:
		mask = env.action_masks()
		with torch.no_grad():
			dist = policy(torch.as_tensor(obs[None]), torch.as_tensor(mask[None]))
			probs = dist.probs[0]
			if greedy:
				action = int(probs.argmax())
			else:
				action = int(torch.multinomial(probs, 1, generator=generator))
			logp = float(dist.log_prob(torch.tensor([action]))[0])
		steps.append((obs, mask, action, logp, float(probs.max())))
		obs, r, terminated, truncated, _ = env.step(action)
		total += r
		done = terminated or truncated
	return steps, total, env.winner == SIDE


def task(env: BattleEnv, policy: Policy, kind: str, seed: int, k: int):
	"""
	One episode of a kind: "sample" (the k-th of its group, its own generator), "greedy" or
	"sampled" (evaluation), or a baseline ("random", "nearest"). Returns (decisions, return, won,
	combat rounds played).
	"""
	before = env.rounds
	if kind in ("random", "nearest"):
		won, total, _ = ppo.play(env, seed, None, ppo.baseline(env, kind, seed))
		return [], total, won, env.rounds - before
	generator = torch.Generator().manual_seed(
		seeds.branch(seed, 0, k) if kind == "sample" else seed
	)
	steps, total, won = episode(env, policy, seed, generator, greedy=kind == "greedy")
	return steps, total, won, env.rounds - before


def worker(port: int, scenario: str, difficulty: str, threads: int, conn) -> None:
	torch.set_num_threads(threads)
	env = BattleEnv(port, scenario, difficulties=[difficulty], fixed_dice=True)
	policy = Policy(env.observation_space)
	reported = 0
	while (message := conn.recv()) is not None:
		state, tasks = message
		policy.load_state_dict(state)
		results = [task(env, policy, *t) for t in tasks]
		guids, vectors = texts.TABLE.rows(reported)
		reported += len(guids)
		conn.send((results, guids, vectors))
	env.close()


class Workers:
	"""One worker process per server port; runs tasks in parallel with the current weights."""

	def __init__(self, ports: list[int], scenario: str, difficulty: str, threads: int) -> None:
		context = multiprocessing.get_context("spawn")
		self.pipes, self.processes = [], []
		for port in ports:
			parent, child = context.Pipe()
			process = context.Process(
				target=worker, args=(port, scenario, difficulty, threads, child), daemon=True
			)
			process.start()
			self.pipes.append(parent)
			self.processes.append(process)
		# Per worker: its text table rows, mapped to this process's rows (row 0 stays 0).
		self.maps = [np.zeros(texts.CAPACITY, dtype=np.int64) for _ in ports]
		self.known = [0] * len(ports)

	def run(self, policy: Policy, tasks: list[tuple]) -> list:
		state = {k: v.detach().cpu() for k, v in policy.state_dict().items()}
		n = len(self.pipes)
		for w, pipe in enumerate(self.pipes):
			pipe.send((state, tasks[w::n]))
		results: list = [None] * len(tasks)
		for w, pipe in enumerate(self.pipes):
			done, guids, vectors = pipe.recv()
			for guid, vector in zip(guids, vectors, strict=True):
				self.known[w] += 1
				self.maps[w][self.known[w]] = texts.TABLE.add(guid, vector)
			for j, result in enumerate(done):
				for obs, *_ in result[0]:
					obs[ID_COLUMNS] = self.maps[w][obs[ID_COLUMNS].astype(np.int64)]
				results[w + j * n] = result
		return results

	def close(self) -> None:
		for pipe in self.pipes:
			pipe.send(None)
		for process in self.processes:
			process.join(timeout=60)


def update(policy: Policy, optimizer, batch: list, epochs: int, clip: float, entropy: float):
	obs = torch.as_tensor(np.array([b[0] for b in batch]))
	mask = torch.as_tensor(np.array([b[1] for b in batch]))
	action = torch.as_tensor([b[2] for b in batch])
	old = torch.as_tensor([b[3] for b in batch])
	adv = torch.as_tensor([b[4] for b in batch], dtype=torch.float32)
	generator = torch.Generator().manual_seed(0)
	for _ in range(epochs):
		for part in torch.randperm(len(batch), generator=generator).split(256):
			dist = policy(obs[part], mask[part])
			ratio = torch.exp(dist.log_prob(action[part]) - old[part])
			gain = torch.min(ratio * adv[part], ratio.clamp(1 - clip, 1 + clip) * adv[part])
			loss = -gain.mean() - entropy * dist.entropy().mean()
			optimizer.zero_grad()
			loss.backward()
			nn.utils.clip_grad_norm_(policy.parameters(), 0.5)
			optimizer.step()


def evaluate(workers: Workers, policy: Policy, eval_seeds: range, kinds: list[str], label: str):
	for kind in kinds:
		results = workers.run(policy, [(kind, seed, 0) for seed in eval_seeds])
		rows = [
			(won, total, statistics.mean(s[4] for s in steps) if steps else float("nan"))
			for steps, total, won, _ in results
		]
		ppo.report(f"{label} {kind}", rows)


def main() -> None:
	parser = argparse.ArgumentParser(description=__doc__)
	parser.add_argument("--ports", default="47511", help="comma-separated server ports")
	parser.add_argument("--scenario", default="1v1")
	parser.add_argument("--difficulty", default="Core")
	parser.add_argument("--rounds", type=float, default=15000, help="budget in combat rounds")
	parser.add_argument("--group", type=int, default=8, help="episodes per seed")
	parser.add_argument("--seeds-per-update", type=int, default=4)
	parser.add_argument("--epochs", type=int, default=4)
	parser.add_argument("--clip", type=float, default=0.2)
	parser.add_argument("--entropy", type=float, default=0.01)
	parser.add_argument("--lr", type=float, default=3e-4)
	parser.add_argument("--eval-seeds", type=int, default=30)
	parser.add_argument("--threads", type=int, default=2, help="torch threads per process")
	args = parser.parse_args()

	torch.set_num_threads(args.threads)
	torch.manual_seed(0)
	ports = [int(p) for p in args.ports.split(",") if p]
	workers = Workers(ports, args.scenario, args.difficulty, args.threads)
	size = STATE_SIZE + MAX_CHOICES * ROW_SIZE
	policy = Policy(gym.spaces.Box(-np.inf, np.inf, shape=(size,), dtype=np.float32))
	optimizer = torch.optim.Adam(policy.parameters(), lr=args.lr)
	eval_seeds = seeds.evaluation(args.eval_seeds)
	evaluate(workers, policy, eval_seeds, ["random", "nearest", "greedy", "sampled"], "before")

	rounds, n, start, wins, flat, groups, next_seed = 0.0, 0, time.time(), [], 0, 0, 0
	while rounds < args.rounds:
		batch_seeds = [seeds.train(next_seed + s) for s in range(args.seeds_per_update)]
		next_seed += args.seeds_per_update
		tasks = [("sample", seed, g) for seed in batch_seeds for g in range(args.group)]
		results = workers.run(policy, tasks)
		n += len(results)
		batch = []
		for s in range(args.seeds_per_update):
			group = results[s * args.group : (s + 1) * args.group]
			wins += [won for _, _, won, _ in group]
			returns = np.array([total for _, total, _, _ in group])
			groups += 1
			if returns.std() < 1e-6:
				flat += 1
				continue
			advantages = (returns - returns.mean()) / (returns.std() + 1e-8)
			for (steps, _, _, _), a in zip(group, advantages, strict=True):
				batch += [(o, m, act, lp, float(a)) for o, m, act, lp, _ in steps]
		rounds += sum(played for _, _, _, played in results)
		if batch:
			update(policy, optimizer, batch, args.epochs, args.clip, args.entropy)
		recent = wins[-100:]
		print(
			f"episodes {n} rounds {rounds:.0f} last-100 win {sum(recent)}/{len(recent)} "
			f"flat groups {flat}/{groups} wall {time.time() - start:.0f}s",
			flush=True,
		)
	evaluate(workers, policy, eval_seeds, ["greedy", "sampled"], "after")
	torch.save(policy.state_dict(), f"grpo-{args.scenario}.pt")
	texts.TABLE.save(f"grpo-{args.scenario}.texts.npz")
	workers.close()


if __name__ == "__main__":
	main()
