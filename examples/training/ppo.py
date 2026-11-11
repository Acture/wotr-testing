"""
MaskablePPO (sb3-contrib) on the battle environment through the Gymnasium interface
(gym_env.BattleEnv).

The candidates change from step to step, so the policy scores each candidate with one shared network
over its features and a state embedding (CandidateScorer); the action logits are those scores, and a
candidate's score does not depend on where it stands in the list. The value head reads the state
embedding. --policy mlp uses the trainer's default network for comparison.
"""

from __future__ import annotations

import argparse
import statistics
import time

import features
import torch
from gym_env import MAX_CANDIDATES, MAX_UNITS, UNIT_FEATURES, BattleEnv
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
		self.units = MAX_UNITS * UNIT_FEATURES
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


def evaluate(model: MaskablePPO, env: BattleEnv, seeds: range, label: str) -> None:
	wins, returns = 0, []
	for seed in seeds:
		obs, _ = env.reset(seed=seed)
		total, done = 0.0, False
		while not done:
			action, _ = model.predict(obs, action_masks=env.action_masks(), deterministic=True)
			obs, r, terminated, truncated, _ = env.step(int(action))
			total += r
			done = terminated or truncated
		wins += env.winner == SIDE
		returns.append(total)
	print(
		f"eval {label}: win {wins}/{len(seeds)} return {statistics.mean(returns):.2f}", flush=True
	)


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
	args = parser.parse_args()

	env = BattleEnv(args.port, args.scenario, turn_based=args.turn_based)
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
	eval_seeds = range(500, 500 + args.eval_seeds)
	evaluate(model, env, eval_seeds, "before")
	model.learn(total_timesteps=args.timesteps, callback=Progress())
	evaluate(model, env, eval_seeds, "after")
	model.save(f"ppo-{args.scenario}")
	env.close()


if __name__ == "__main__":
	main()
