"""
Warm start from demonstrations (demos.py): behavior cloning, a cross-entropy step towards each
choice the demonstrations made, before training starts.
"""

from __future__ import annotations

import json
from collections.abc import Iterator

import numpy as np
from client import by_unit
from policy import LinearPolicy, softmax


def load(path: str) -> list[dict]:
	with open(path, encoding="utf-8") as handle:
		return [json.loads(line) for line in handle if line.strip()]


def decisions(demos: list[dict]) -> Iterator[tuple[dict, str, list[int], int]]:
	"""
	Each decision of the demonstrations as (observation, unit, toggles it switched before in this
	step, chosen candidate index), in the order the gym environment asks for them: a unit's toggles
	before its command.
	"""
	for demo in demos:
		for step in demo["steps"]:
			obs = step["obs"]
			ordered = sorted(
				step["choices"], key=lambda i: obs["candidates"][i]["kind"] != "Toggle"
			)
			switched: dict[str, list[int]] = {}
			for choice in ordered:
				unit = obs["candidates"][choice]["unit"]
				before = list(switched.get(unit, []))
				yield obs, unit, before, choice
				if obs["candidates"][choice]["kind"] == "Toggle":
					switched.setdefault(unit, []).append(choice)


def clone_linear(
	policy: LinearPolicy,
	demos: list[dict],
	epochs: int = 30,
	lr: float = 0.1,
	rng: np.random.Generator | None = None,
) -> float:
	"""Trains the policy towards the demonstrations' choices; returns its accuracy on them."""
	rng = rng or np.random.default_rng(0)
	samples = []
	for obs, unit, _, choice in decisions(demos):
		index = by_unit(obs).get(unit, [])
		if choice in index and len(index) > 1:
			phi, _ = policy.probs(obs, index)
			samples.append((phi, index.index(choice)))
	if not samples:
		return 0.0
	for _ in range(epochs):
		for s in rng.permutation(len(samples)):
			phi, k = samples[s]
			target = np.zeros(len(phi))
			target[k] = 1.0
			policy.w += lr * phi.T @ (target - softmax(phi @ policy.w))
	return float(np.mean([int(np.argmax(phi @ policy.w)) == k for phi, k in samples]))
