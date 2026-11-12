"""
MaskablePPO (sb3-contrib) on the battle environment through the Gymnasium interface
(gym_env.BattleEnv).

The candidates change from step to step, so the policy scores each candidate with one shared network
over its features and a state embedding (CandidateScorer); the action logits are those scores, and a
candidate's score does not depend on where it stands in the list. The value head reads the state
embedding. --policy mlp uses the trainer's default network for comparison. --warm first trains the
policy towards the choices of demonstrations (demos.py) by behavior cloning.
"""

from __future__ import annotations

import argparse
import statistics
import time

import features
import numpy as np
import seeds
import torch
import warm
from gym_env import (
	MAX_CANDIDATES,
	STATE_SIZE,
	BattleEnv,
	encode,
	mask,
	unit_candidates,
)
from rewards import SIDE
from sb3_contrib import MaskablePPO
from sb3_contrib.common.maskable.policies import MaskableActorCriticPolicy
from stable_baselines3.common.callbacks import BaseCallback
from stable_baselines3.common.monitor import Monitor
from stable_baselines3.common.torch_layers import BaseFeaturesExtractor
from torch import nn

STATE = 64


class CandidateScorer(BaseFeaturesExtractor):
	"""The candidates' scores from one shared network, followed by the state embedding."""

	def __init__(self, observation_space) -> None:
		super().__init__(observation_space, features_dim=MAX_CANDIDATES + STATE)
		self.units = STATE_SIZE
		self.state = nn.Sequential(
			nn.Linear(self.units, STATE), nn.ReLU(), nn.Linear(STATE, STATE), nn.ReLU()
		)
		self.score = nn.Sequential(
			nn.Linear(features.SIZE + STATE, 64), nn.ReLU(), nn.Linear(64, 1)
		)

	def forward(self, observations: torch.Tensor) -> torch.Tensor:
		state = self.state(observations[:, : self.units])
		candidates = observations[:, self.units :].reshape(-1, MAX_CANDIDATES, features.SIZE)
		joined = torch.cat([candidates, state.unsqueeze(1).expand(-1, MAX_CANDIDATES, -1)], dim=-1)
		return torch.cat([self.score(joined).squeeze(-1), state], dim=-1)


class ScoringPolicy(MaskableActorCriticPolicy):
	def __init__(self, *args, **kwargs) -> None:
		kwargs.update(
			features_extractor_class=CandidateScorer,
			net_arch=dict(pi=[], vf=[64]),
			share_features_extractor=True,
		)
		super().__init__(*args, **kwargs)

	def _get_action_dist_from_latent(self, latent_pi: torch.Tensor):
		return self.action_dist.proba_distribution(action_logits=latent_pi[:, :MAX_CANDIDATES])


def evaluate(
	model: MaskablePPO, env: BattleEnv, seeds: range, label: str, difficulty: str | None = None
) -> None:
	wins, returns = 0, []
	for seed in seeds:
		obs, _ = env.reset(seed=seed, options={"difficulty": difficulty} if difficulty else None)
		total, done = 0.0, False
		while not done:
			action, _ = model.predict(obs, action_masks=env.action_masks(), deterministic=True)
			obs, r, terminated, truncated, _ = env.step(int(action))
			total += r
			done = terminated or truncated
		wins += env.winner == SIDE
		returns.append(total)
	where = f" {difficulty}" if difficulty else ""
	print(
		f"eval {label}{where}: win {wins}/{len(seeds)} return {statistics.mean(returns):.2f}",
		flush=True,
	)


def warm_start(model: MaskablePPO, demos: list[dict], epochs: int, lr: float) -> float:
	"""
	Behavior cloning: cross-entropy of the policy's masked action distribution towards each choice
	of the demonstrations, as the gym environment would have asked for it. Returns the share of
	those choices the policy then ranks first.
	"""
	xs, masks, actions = [], [], []
	for obs, unit, switched, choice in warm.decisions(demos):
		index = unit_candidates(obs, unit, switched)
		if choice in index and len(index) > 1:
			xs.append(encode(obs, unit, switched))
			masks.append(mask(len(index)))
			actions.append(index.index(choice))
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
	def __init__(self) -> None:
		super().__init__()
		self.winners: list[str] = []
		self.t0 = time.time()

	def _on_step(self) -> bool:
		for info in self.locals.get("infos", []):
			if "episode" in info:
				self.winners.append(info.get("winner", ""))
				if len(self.winners) % 100 == 0:
					recent = self.winners[-100:]
					print(
						f"episodes {len(self.winners)} decisions {self.num_timesteps} "
						f"last-100 win {sum(w == SIDE for w in recent)}/100 "
						f"wall {time.time() - self.t0:.0f}s",
						flush=True,
					)
		return True


def main() -> None:
	parser = argparse.ArgumentParser(description=__doc__)
	parser.add_argument("--port", type=int, default=47511)
	parser.add_argument("--scenario", default="1v3")
	parser.add_argument("--timesteps", type=int, default=20000)
	parser.add_argument("--eval-seeds", type=int, default=20)
	parser.add_argument("--policy", choices=["scoring", "mlp"], default="scoring")
	parser.add_argument("--turn-based", action="store_true")
	parser.add_argument(
		"--difficulties",
		default="Unfair",
		help="comma-separated game difficulty levels; each episode draws one from its seed",
	)
	parser.add_argument("--warm", help="demonstrations to start from (demos.py)")
	parser.add_argument("--warm-epochs", type=int, default=30)
	args = parser.parse_args()

	levels = [level for level in args.difficulties.split(",") if level]
	env = BattleEnv(args.port, args.scenario, turn_based=args.turn_based, difficulties=levels)
	model = MaskablePPO(
		ScoringPolicy if args.policy == "scoring" else "MlpPolicy",
		Monitor(env, info_keywords=("winner",)),
		n_steps=256,
		batch_size=64,
		learning_rate=3e-4,
		gamma=0.98,
		ent_coef=0.01,
		seed=0,
		verbose=0,
	)
	eval_seeds = seeds.evaluation(args.eval_seeds)
	for level in levels:
		evaluate(model, env, eval_seeds, "before", level)
	if args.warm:
		accuracy = warm_start(model, warm.load(args.warm), args.warm_epochs, 1e-3)
		print(f"warm start: ranks {accuracy:.0%} of the demonstrations' choices first", flush=True)
		for level in levels:
			evaluate(model, env, eval_seeds, "warm", level)
	model.learn(total_timesteps=args.timesteps, callback=Progress())
	for level in levels:
		evaluate(model, env, eval_seeds, "after", level)
	model.save(f"ppo-{args.scenario}")
	env.close()


if __name__ == "__main__":
	main()
