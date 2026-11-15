"""
MaskablePPO (sb3-contrib) on the battle environment through the Gymnasium interface
(gym_env.BattleEnv).

The candidates change from step to step, so the policy scores each candidate with one shared network
over its features and a state embedding (CandidateScorer); the action logits are those scores, and a
candidate's score does not depend on where it stands in the list. The value head reads the state
embedding. --policy mlp uses the trainer's default network for comparison. --warm first trains the
policy towards the choices of demonstrations (demos.py) by behavior cloning. --rounds sets the
training budget in combat rounds played (game time / 6 s, evaluation not counted) instead of
decisions, which depend on how many units decide and how many switches they set. Rewards are
discounted per combat round, not per decision (RoundDiscountBuffer): a decision that does not run
the battle, such as a switch, is not discounted, so piling up switches does not push a loss away.
"""

from __future__ import annotations

import argparse
import statistics
import time

import features
import numpy as np
import seeds
import texts
import torch
import warm
from gym_env import (
	MAX_BUFFS,
	MAX_CHOICES,
	MAX_UNITS,
	ROW_SIZE,
	STATE_SIZE,
	UNIT_FEATURES,
	UNIT_WIDTH,
	BattleEnv,
	encode,
	groups,
	mask,
)
from rewards import SIDE
from sb3_contrib import MaskablePPO
from sb3_contrib.common.maskable.buffers import MaskableRolloutBuffer
from sb3_contrib.common.maskable.policies import MaskableActorCriticPolicy
from stable_baselines3.common.callbacks import BaseCallback
from stable_baselines3.common.monitor import Monitor
from stable_baselines3.common.torch_layers import BaseFeaturesExtractor
from torch import nn

STATE = 64
EMBED = 16


class CandidateScorer(BaseFeaturesExtractor):
	"""
	The choices' scores from one shared network, followed by the state embedding. Abilities and
	buffs are their text embeddings (texts.TABLE rows, looked up by the ids in the observation; 0
	is none) through learned projections without bias, so none adds nothing; a unit's buffs are
	summed.
	"""

	def __init__(self, observation_space) -> None:
		super().__init__(observation_space, features_dim=MAX_CHOICES + STATE)
		self.abilities = nn.Linear(texts.DIM, EMBED, bias=False)
		self.buffs = nn.Linear(texts.DIM, EMBED, bias=False)
		self.unit_block = MAX_UNITS * UNIT_WIDTH
		state_in = MAX_UNITS * (UNIT_FEATURES + EMBED) + features.DIFFICULTY_SIZE
		self.state = nn.Sequential(
			nn.Linear(state_in, STATE), nn.ReLU(), nn.Linear(STATE, STATE), nn.ReLU()
		)
		self.score = nn.Sequential(
			nn.Linear(features.SIZE + EMBED + STATE, 64), nn.ReLU(), nn.Linear(64, 1)
		)

	def forward(self, observations: torch.Tensor) -> torch.Tensor:
		units = observations[:, : self.unit_block].reshape(-1, MAX_UNITS, UNIT_WIDTH)
		table = texts.TABLE.tensor(observations.device)
		buff_ids = units[..., UNIT_FEATURES:].long()
		assert buff_ids.shape[-1] == MAX_BUFFS
		buffs = self.buffs(table[buff_ids]).sum(dim=2)
		difficulty = observations[:, self.unit_block : STATE_SIZE]
		per_unit = torch.cat([units[..., :UNIT_FEATURES], buffs], -1).flatten(1)
		state = self.state(torch.cat([per_unit, difficulty], -1))
		rows = observations[:, STATE_SIZE:].reshape(-1, MAX_CHOICES, ROW_SIZE)
		joined = torch.cat(
			[
				rows[..., :-1],
				self.abilities(table[rows[..., -1].long()]),
				state.unsqueeze(1).expand(-1, MAX_CHOICES, -1),
			],
			dim=-1,
		)
		return torch.cat([self.score(joined).squeeze(-1), state], dim=-1)


class RoundDiscountBuffer(MaskableRolloutBuffer):
	"""
	Generalized advantage estimation with gamma and lambda per combat round: a step that took r
	rounds (BattleEnv.last_rounds) is discounted by gamma**r, so steps that take no game time are
	not discounted. Set `env` before collecting.
	"""

	env: BattleEnv | None = None

	def reset(self) -> None:
		super().reset()
		self.rounds = np.zeros((self.buffer_size, self.n_envs), dtype=np.float32)

	def add(self, *args, **kwargs) -> None:
		assert self.env is not None
		self.rounds[self.pos] = self.env.last_rounds
		super().add(*args, **kwargs)

	def compute_returns_and_advantage(self, last_values: torch.Tensor, dones: np.ndarray) -> None:
		values = last_values.clone().cpu().numpy().flatten()
		advantage = 0.0
		for step in reversed(range(self.buffer_size)):
			if step == self.buffer_size - 1:
				going_on = 1.0 - dones.astype(np.float32)
				next_values = values
			else:
				going_on = 1.0 - self.episode_starts[step + 1]
				next_values = self.values[step + 1]
			gamma = self.gamma ** self.rounds[step]
			lam = self.gae_lambda ** self.rounds[step]
			delta = self.rewards[step] + gamma * next_values * going_on - self.values[step]
			advantage = delta + gamma * lam * going_on * advantage
			self.advantages[step] = advantage
		self.returns = self.advantages + self.values


class ScoringPolicy(MaskableActorCriticPolicy):
	def __init__(self, *args, **kwargs) -> None:
		kwargs.update(
			features_extractor_class=CandidateScorer,
			net_arch=dict(pi=[], vf=[64]),
			share_features_extractor=True,
		)
		super().__init__(*args, **kwargs)

	def _get_action_dist_from_latent(self, latent_pi: torch.Tensor):
		return self.action_dist.proba_distribution(action_logits=latent_pi[:, :MAX_CHOICES])


def play(env: BattleEnv, seed: int, difficulty: str | None, choose) -> tuple[bool, float, float]:
	"""One evaluation episode; `choose(obs)` gives the action and the policy's top probability."""
	obs, _ = env.reset(seed=seed, options={"difficulty": difficulty} if difficulty else None)
	total, done, tops = 0.0, False, []
	while not done:
		action, top = choose(obs)
		tops.append(top)
		obs, r, terminated, truncated, _ = env.step(int(action))
		total += r
		done = terminated or truncated
	return env.winner == SIDE, total, float(np.nanmean(tops)) if tops else float("nan")


def report(label: str, results: list[tuple[bool, float, float]]) -> None:
	wins = sum(won for won, _, _ in results)
	ret = statistics.mean(r for _, r, _ in results)
	tops = [t for _, _, t in results if not np.isnan(t)]
	decisive = f" top-prob {statistics.mean(tops):.2f}" if tops else ""
	print(f"eval {label}: win {wins}/{len(results)} return {ret:.2f}{decisive}", flush=True)


def evaluate(
	model: MaskablePPO, env: BattleEnv, seeds: range, label: str, difficulty: str | None = None
) -> None:
	"""
	The policy on the held-out seeds, played two ways: greedy (its most probable candidate) and
	sampled (as it trains; torch seeded per episode, so repeatable). Greedy alone says little while
	the policy is nearly uniform: the most probable candidate is then whichever its initial weights
	favour. top-prob is the mean probability of the policy's most probable candidate per decision:
	how decisive it is (about 1/candidates while uniform).
	"""
	where = f" {difficulty}" if difficulty else ""

	def chooser(deterministic: bool):
		def choose(obs):
			masks = env.action_masks()
			action, _ = model.predict(obs, action_masks=masks, deterministic=deterministic)
			with torch.no_grad():
				x = torch.as_tensor(obs[None], device=model.policy.device)
				dist = model.policy.get_distribution(x, action_masks=masks[None])
			return int(action), float(dist.distribution.probs.max())

		return choose

	for mode in ("greedy", "sampled"):
		results = []
		for seed in seeds:
			torch.manual_seed(seed)
			results.append(play(env, seed, difficulty, chooser(mode == "greedy")))
		report(f"{label}{where} {mode}", results)


def baseline(env: BattleEnv, name: str, seed: int):
	"""
	A baseline's choice function for one episode: "random" (uniform over the level's choices, its
	generator seeded by the episode's seed) or "nearest" (the plain attack, then its nearest target;
	waiting, or ending the turn, without one).
	"""
	if name == "random":
		rng = np.random.default_rng(seed)
		return lambda obs: (int(rng.integers(max(1, len(env.choices())))), float("nan"))

	def nearest(obs):
		cands = env.obs["candidates"]
		units = {u["id"]: u for u in env.obs["units"]}
		me = units[env._unit()]

		def distance(i: int) -> float:
			target = units[cands[i]["target"]]
			return float(np.hypot(target["x"] - me["x"], target["z"] - me["z"]))

		rows = env.choices()
		attacks = [
			(min(distance(i) for i in row), k)
			for k, row in enumerate(rows)
			if all(cands[i]["kind"] == "Attack" and cands[i].get("variant") is None for i in row)
		]
		if attacks:
			return min(attacks)[1], float("nan")
		waits = [k for k, row in enumerate(rows) if cands[row[0]]["kind"] in ("Wait", "EndTurn")]
		return (waits[0] if waits else 0), float("nan")

	return nearest


def baselines(env: BattleEnv, seeds: range, difficulty: str | None = None) -> None:
	"""A uniformly random policy and attack-the-nearest-enemy on the same held-out seeds."""
	where = f" {difficulty}" if difficulty else ""
	for name in ("random", "nearest"):
		results = [play(env, seed, difficulty, baseline(env, name, seed)) for seed in seeds]
		report(f"baseline{where} {name}", results)


def warm_start(model: MaskablePPO, demos: list[dict], epochs: int, lr: float) -> float:
	"""
	Behavior cloning: cross-entropy of the policy's masked action distribution towards each choice
	of the demonstrations, as the gym environment would have asked for it. Returns the share of
	those choices the policy then ranks first.
	"""
	xs, masks, actions = [], [], []

	def add(x: np.ndarray, count: int, action: int) -> None:
		if count > 1:
			xs.append(x)
			masks.append(mask(count))
			actions.append(action)

	for obs, unit, switched, choice in warm.decisions(demos):
		options = groups(obs, unit, switched)
		group = next((g for g in options if choice in g), None)
		if group is None:
			continue
		# What to do, then on whom: the two decisions the gym environment asks for.
		add(encode(obs, unit, switched), len(options), options.index(group))
		add(encode(obs, unit, switched, group), len(group), group.index(choice))
	if not xs:
		return 0.0
	policy = model.policy
	x = torch.as_tensor(np.array(xs), device=policy.device)
	m = np.array(masks)
	a = torch.as_tensor(actions, device=policy.device)
	optimizer = torch.optim.Adam(policy.parameters(), lr=lr)
	generator = torch.Generator().manual_seed(0)
	for _ in range(epochs):
		for batch in torch.randperm(len(x), generator=generator).split(64):
			dist = policy.get_distribution(x[batch], action_masks=m[batch.numpy()])
			loss = -dist.log_prob(a[batch]).mean()
			optimizer.zero_grad()
			loss.backward()
			optimizer.step()
	with torch.no_grad():
		dist = policy.get_distribution(x, action_masks=m)
		return float((dist.distribution.probs.argmax(-1) == a).float().mean())


class Progress(BaseCallback):
	"""Reports every 100 episodes; with a round budget, stops training once it is played."""

	def __init__(self, env: BattleEnv, rounds: float | None) -> None:
		super().__init__()
		self.env = env
		self.rounds = rounds
		self.winners: list[str] = []
		self.t0 = time.time()

	def report(self) -> None:
		recent = self.winners[-100:]
		wins = sum(w == SIDE for w in recent)
		print(
			f"episodes {len(self.winners)} rounds {self.env.rounds:.0f} "
			f"decisions {self.num_timesteps} last-{len(recent)} win {wins}/{len(recent)} "
			f"wall {time.time() - self.t0:.0f}s",
			flush=True,
		)

	def _on_step(self) -> bool:
		for info in self.locals.get("infos", []):
			if "episode" in info:
				self.winners.append(info.get("winner", ""))
				if len(self.winners) % 100 == 0:
					self.report()
		if self.rounds is not None and self.env.rounds >= self.rounds:
			self.report()
			return False
		return True


def main() -> None:
	parser = argparse.ArgumentParser(description=__doc__)
	parser.add_argument("--port", type=int, default=47511)
	parser.add_argument("--scenario", default="1v3")
	parser.add_argument("--timesteps", type=int, default=20000, help="budget in decisions")
	parser.add_argument(
		"--rounds", type=float, help="budget in combat rounds played; replaces --timesteps"
	)
	parser.add_argument("--eval-seeds", type=int, default=20)
	parser.add_argument("--policy", choices=["scoring", "mlp"], default="scoring")
	parser.add_argument("--turn-based", action="store_true")
	parser.add_argument(
		"--difficulties",
		default="Unfair",
		help="comma-separated game difficulty levels; each episode draws one from its seed",
	)
	parser.add_argument("--gamma", type=float, default=0.95, help="discount per combat round")
	parser.add_argument("--warm", help="demonstrations to start from (demos.py)")
	parser.add_argument("--warm-epochs", type=int, default=30)
	parser.add_argument(
		"--fixed-dice",
		action="store_true",
		help="restart the game's random numbers at every battle step from (episode seed, step)",
	)
	parser.add_argument("--threads", type=int, default=2, help="torch threads")
	args = parser.parse_args()
	torch.set_num_threads(args.threads)

	levels = [level for level in args.difficulties.split(",") if level]
	env = BattleEnv(
		args.port,
		args.scenario,
		turn_based=args.turn_based,
		difficulties=levels,
		fixed_dice=args.fixed_dice,
	)
	model = MaskablePPO(
		ScoringPolicy if args.policy == "scoring" else "MlpPolicy",
		Monitor(env, info_keywords=("winner",)),
		n_steps=256,
		batch_size=64,
		learning_rate=3e-4,
		gamma=args.gamma,
		ent_coef=0.01,
		seed=0,
		verbose=0,
		rollout_buffer_class=RoundDiscountBuffer,
	)
	model.rollout_buffer.env = env
	eval_seeds = seeds.evaluation(args.eval_seeds)
	for level in levels:
		baselines(env, eval_seeds, level)
		evaluate(model, env, eval_seeds, "before", level)
	if args.warm:
		accuracy = warm_start(model, warm.load(args.warm), args.warm_epochs, 1e-3)
		print(f"warm start: ranks {accuracy:.0%} of the demonstrations' choices first", flush=True)
		for level in levels:
			evaluate(model, env, eval_seeds, "warm", level)
	env.rounds = 0.0
	budget = 10**9 if args.rounds is not None else args.timesteps
	model.learn(total_timesteps=budget, callback=Progress(env, args.rounds))
	for level in levels:
		evaluate(model, env, eval_seeds, "after", level)
	model.save(f"ppo-{args.scenario}")
	texts.TABLE.save(f"ppo-{args.scenario}.texts.npz")
	env.close()


if __name__ == "__main__":
	main()
