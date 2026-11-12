"""
Seeds of the battle episodes and of rollouts. The environment is deterministic: an episode's seed
and its actions decide every die. Training episodes and held-out evaluation episodes therefore use
separate seeds, and a rollout that leaves an episode draws its dice from a seed of its own.
"""

from __future__ import annotations

import numpy as np

EVALUATION_BASE = 1_000_000
"""Held-out evaluation episodes use seeds from here on; training episodes stay below."""


def train(n: int) -> int:
	"""Battle seed of the n-th training episode, counting from 0."""
	if not 0 <= n < EVALUATION_BASE - 1:
		raise ValueError(f"training episode {n} would use an evaluation seed")
	return 1 + n


def evaluation(count: int) -> range:
	"""Battle seeds of the held-out evaluation episodes; no training episode uses them."""
	return range(EVALUATION_BASE, EVALUATION_BASE + count)


def branch(seed: int, step: int, k: int) -> int:
	"""
	Random seed of the k-th rollout from the decision after `step` steps of the episode built with
	`seed`. A rollout rebuilds the state by replaying the episode, which also reaches the episode's
	random state; its first step restarts the game's random numbers from this seed, so it does not
	roll the dice the episode's own next step will. Every candidate of the decision gets the same
	k-th seed, so the candidates are compared on the same draws; the episode itself continues its
	own stream.
	"""
	return int(np.random.SeedSequence([seed, step, k]).generate_state(1)[0] & 0x7FFFFFFF)


def rollout_rng(seed: int, step: int, k: int) -> np.random.Generator:
	"""The k-th rollout's own generator for the actions it samples, equal for every candidate."""
	return np.random.default_rng([seed, step, k])
