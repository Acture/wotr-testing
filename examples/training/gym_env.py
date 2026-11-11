"""
The battle environment as a Gymnasium environment with action masks, for off-the-shelf trainers.

One gym step decides for one ready unit of the controlled side: the action is an index into that
unit's candidates (action_masks marks the valid ones). Choosing a toggle keeps the same unit
deciding, as toggles go with the unit's command; when every ready unit has a command, the battle
takes its step and the step's reward (rewards.step_reward) is returned. The observation is a block
of unit features, relative to the deciding unit, and a block of candidate features
(features.candidate). Candidates that stand for any point on the ground are left out.
"""

from __future__ import annotations

import features
import gymnasium as gym
import numpy as np
from client import BattleClient
from rewards import SIDE, health, step_reward

MAX_UNITS = 8
UNIT_FEATURES = 9
MAX_CANDIDATES = 48


class BattleEnv(gym.Env):
	metadata = {"render_modes": []}

	def __init__(
		self, port: int, scenario: str = "1v3", max_steps: int = 200, turn_based: bool = False
	) -> None:
		self.client = BattleClient(port)
		self.client.start(scenario, max_steps=max_steps, turn_based=turn_based)
		self.episode = 0
		size = MAX_UNITS * UNIT_FEATURES + MAX_CANDIDATES * features.SIZE
		self.observation_space = gym.spaces.Box(-10.0, 10.0, shape=(size,), dtype=np.float32)
		self.action_space = gym.spaces.Discrete(MAX_CANDIDATES)
		self.obs: dict = {}
		self.pending: dict[str, int] = {}
		self.toggles: dict[str, list[int]] = {}
		self.winner = ""

	def _candidates(self, unit: str) -> list[int]:
		switched = self.toggles.get(unit, [])
		index = [
			i
			for i, c in enumerate(self.obs["candidates"])
			if c["unit"] == unit and not c.get("anyPoint") and i not in switched
		]
		return index[:MAX_CANDIDATES]

	def _unit(self) -> str | None:
		for unit in self.obs["ready"]:
			if unit not in self.pending and self._candidates(unit):
				return unit
		return None

	def action_masks(self) -> np.ndarray:
		mask = np.zeros(MAX_CANDIDATES, dtype=bool)
		unit = self._unit()
		mask[: max(1, len(self._candidates(unit)) if unit else 1)] = True
		return mask

	def _encode(self) -> np.ndarray:
		units = {u["id"]: u for u in self.obs["units"]}
		unit = self._unit()
		me = units.get(unit) if unit else None
		ox, oz = (me["x"], me["z"]) if me else (0.0, 0.0)
		rows = sorted(self.obs["units"], key=lambda u: (u["side"] != SIDE, u["id"]))[:MAX_UNITS]
		block = np.zeros((MAX_UNITS, UNIT_FEATURES), dtype=np.float32)
		for r, u in enumerate(rows):
			block[r] = [
				health(u),
				float(u["conscious"]),
				1.0 if u["side"] == SIDE else -1.0,
				(u["x"] - ox) / 10,
				(u["z"] - oz) / 10,
				float(u["id"] == unit),
				float(u["ready"]),
				sum(u["spellSlots"].values()) / 10,
				sum(n for n in u["itemUses"].values() if n > 0) / 10,
			]
		candidates = np.zeros((MAX_CANDIDATES, features.SIZE), dtype=np.float32)
		if unit is not None:
			for r, i in enumerate(self._candidates(unit)):
				candidates[r] = features.candidate(self.obs, self.obs["candidates"][i])
		return np.concatenate([block.ravel(), candidates.ravel()])

	def _advance(self, choices: list[int]) -> tuple[float, bool, bool]:
		"""Steps the battle until a controlled unit decides or the episode ends."""
		total = 0.0
		while True:
			result = self.client.step(choices)
			total += step_reward(self.obs, result["observation"], result)
			self.obs = result["observation"]
			self.pending, self.toggles = {}, {}
			if result["ended"]:
				self.winner = result["winner"] or ""
				return total, bool(result["winner"]), bool(result["truncated"])
			if self._unit() is not None:
				return total, False, False
			choices = []

	def reset(self, *, seed: int | None = None, options: dict | None = None):
		super().reset(seed=seed)
		self.episode += 1
		self.obs = self.client.reset(seed if seed is not None else self.episode)
		self.pending, self.toggles, self.winner = {}, {}, ""
		if self._unit() is None:
			self._advance([])
		return self._encode(), {}

	def step(self, action: int):
		unit = self._unit()
		candidates = self._candidates(unit)
		chosen = candidates[int(action)] if int(action) < len(candidates) else candidates[0]
		if self.obs["candidates"][chosen]["kind"] == "Toggle":
			self.toggles.setdefault(unit, []).append(chosen)
			return self._encode(), 0.0, False, False, {}
		self.pending[unit] = chosen
		if self._unit() is not None:
			return self._encode(), 0.0, False, False, {}
		choices = [i for switched in self.toggles.values() for i in switched] + list(
			self.pending.values()
		)
		total, terminated, truncated = self._advance(choices)
		info = {"winner": self.winner} if terminated or truncated else {}
		return self._encode(), float(total), terminated, truncated, info

	def close(self) -> None:
		self.client.close()
