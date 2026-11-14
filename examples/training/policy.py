"""A linear softmax policy over candidate features, one decision per ready unit."""

from __future__ import annotations

import features
import numpy as np
from client import by_unit


def softmax(x: np.ndarray) -> np.ndarray:
	e = np.exp(x - x.max())
	return e / e.sum()


class LinearPolicy:
	def __init__(self) -> None:
		self.w = np.zeros(features.SIZE)

	def probs(self, obs: dict, index: list[int]) -> tuple[np.ndarray, np.ndarray]:
		"""The candidates' features and the policy's probabilities over them."""
		phi = np.array([features.candidate(obs, obs["candidates"][i]) for i in index])
		return phi, softmax(phi @ self.w)

	def choose(self, obs: dict, rng: np.random.Generator, greedy: bool = False) -> list[int]:
		"""One candidate index per ready unit."""
		choices = []
		for index in by_unit(obs).values():
			_, p = self.probs(obs, index)
			choices.append(
				index[int(np.argmax(p))] if greedy else index[int(rng.choice(len(index), p=p))]
			)
		return choices

	def describe(self) -> dict[str, float]:
		return {name: round(float(v), 3) for name, v in zip(features.NAMES, self.w, strict=True)}
