"""
The battle environment as a Gymnasium environment with action masks, for off-the-shelf trainers.

One gym step decides for one unit of the controlled side that takes a command (client.deciders; in
real time also busy units), in two levels: first what to do (waiting, attacking, using one action
bar slot, moving, delaying, a switch), then, when that has several targets, on whom or where. The
action is an index into the choices of the level (choices(); action_masks marks the valid ones);
choosing what to do with several targets is a gym step that takes no game time. Every choice the
unit has is offered: a level with more than MAX_CHOICES raises instead of leaving any out. Choosing
a switch (a toggle, the hold, stop or stealth key, auto use, a weapon set, the grip) keeps the same
unit deciding, as switches go with the unit's command; when every such unit has a command, the
battle takes its step and the step's reward (rewards.step_reward) is returned.

The observation is a block of unit features relative to the deciding unit (health, position,
readiness, casts and item uses left, controls, action cooldowns, and its buffs as rows of the
text table, texts.TABLE), the battle's difficulty settings (features.difficulty) and a block with
one row per choice of the level (features.option for what to do, with its targets summed up;
features.target for one target: the action, its target and, for an action bar slot, what the slot
does), each row ending with the slot's text table row (0 for none). With fixed dice, every battle
step restarts the game's random numbers from a seed of the episode's seed and the step's number,
so episodes from one seed meet the same dice at the same step whatever was chosen before.
Candidates that stand for any point on the ground are left out. With several difficulty levels,
each episode's level is drawn from its seed; reset(options={"difficulty": level}) chooses one.
A step that ends the episode at the battle's step limit reports the state it was cut off in
(`terminal_observation`), whose value the trainer bootstraps the step with.
"""

from __future__ import annotations

import features
import gymnasium as gym
import numpy as np
import seeds
from client import BattleClient, deciders, is_switch, winner
from rewards import SIDE, health, step_reward
from texts import TABLE, guids

ROUND_SECONDS = 6.0
MAX_UNITS = 8
UNIT_FEATURES = 16
MAX_BUFFS = 64
UNIT_WIDTH = UNIT_FEATURES + MAX_BUFFS
MAX_CHOICES = 64
STATE_SIZE = MAX_UNITS * UNIT_WIDTH + features.DIFFICULTY_SIZE
ROW_SIZE = features.SIZE + 1


# Kinds whose candidates differ only in their target, path or point: one choice of what to do.
TARGETED = {"Attack", "UseAbility", "Move", "Delay"}


def groups(obs: dict, unit: str, switched: list[int]) -> list[list[int]]:
	"""
	What the unit can do, each as the indices of its candidates that do it on different targets
	(an attack, a slot's use, a move, a delay: one per kind, slot and variant), in the server's
	order; every other candidate is a choice of its own. Any-point candidates are left out, and so
	are the switches the unit already set: a toggle once, and every other kind of switch (one
	weapon set, one auto use) once.
	"""
	candidates = obs["candidates"]
	kinds = {candidates[i]["kind"] for i in switched if candidates[i]["kind"] != "Toggle"}
	options: dict[tuple, list[int]] = {}
	for i, c in enumerate(candidates):
		if c["unit"] != unit or c.get("anyPoint") or i in switched or c["kind"] in kinds:
			continue
		key = (c["kind"], c.get("ability"), c.get("variant")) if c["kind"] in TARGETED else (i,)
		options.setdefault(key, []).append(i)
	return list(options.values())


def choices(obs: dict, unit: str, switched: list[int], group: list[int] | None) -> list[list[int]]:
	"""
	The choices of the level: what to do (groups), or, once `group` is chosen, its candidates one
	by one. Raises when there are more than MAX_CHOICES, which would leave some out.
	"""
	rows = groups(obs, unit, switched) if group is None else [[i] for i in group]
	if len(rows) > MAX_CHOICES:
		level = "things to do" if group is None else "targets"
		raise ValueError(f"{unit} has {len(rows)} {level}, more than MAX_CHOICES ({MAX_CHOICES})")
	return rows


def encode(
	obs: dict,
	unit: str | None,
	switched: list[int],
	group: list[int] | None = None,
) -> np.ndarray:
	"""
	The gym observation when `unit` decides, having set the switches in `switched`: what to do,
	or, once `group` is chosen, on which of its targets. The texts of its abilities and buffs must
	be in texts.TABLE (TABLE.ensure); missing ones are row 0.
	"""

	units = {u["id"]: u for u in obs["units"]}
	me = units.get(unit) if unit else None
	ox, oz = (me["x"], me["z"]) if me else (0.0, 0.0)
	rows = sorted(obs["units"], key=lambda u: (u["side"] != SIDE, u["id"]))[:MAX_UNITS]
	block = np.zeros((MAX_UNITS, UNIT_WIDTH), dtype=np.float32)
	for r, u in enumerate(rows):
		block[r, :UNIT_FEATURES] = [
			health(u),
			float(u["conscious"]),
			1.0 if u["side"] == SIDE else -1.0,
			(u["x"] - ox) / 10,
			(u["z"] - oz) / 10,
			float(u["id"] == unit),
			float(u["ready"]),
			sum(u["spellSlots"].values()) / 10,
			sum(n for n in u["itemUses"].values() if n > 0) / 10,
			float(u.get("hold", False)),
			float(u.get("stop", False)),
			float(u.get("stealth", False)),
			u.get("weaponSet", 0) / 3,
			*(min(c, ROUND_SECONDS) / ROUND_SECONDS for c in u.get("cooldown", [0.0, 0.0, 0.0])),
		]
		buffs = u.get("buffGuids") or u["buffs"]
		if len(buffs) > MAX_BUFFS:
			raise ValueError(f"{u['id']} has {len(buffs)} buffs, more than MAX_BUFFS ({MAX_BUFFS})")
		for k, buff in enumerate(buffs):
			block[r, UNIT_FEATURES + k] = TABLE.id(buff)
	choice_rows = np.zeros((MAX_CHOICES, ROW_SIZE), dtype=np.float32)
	if unit is not None:
		for r, row in enumerate(choices(obs, unit, switched, group)):
			cands = [obs["candidates"][i] for i in row]
			if group is None:
				choice_rows[r, :-1] = features.option(obs, cands)
			else:
				choice_rows[r, :-1] = features.target(obs, cands[0])
			choice_rows[r, -1] = TABLE.id(features.slot_key(obs, cands[0]))
	return np.concatenate([block.ravel(), features.difficulty(obs), choice_rows.ravel()])


def mask(count: int) -> np.ndarray:
	"""Action mask for a level with `count` choices (at least one action stays valid)."""
	valid = np.zeros(MAX_CHOICES, dtype=bool)
	valid[: max(1, count)] = True
	return valid


class BattleEnv(gym.Env):
	metadata = {"render_modes": []}

	def __init__(
		self,
		port: int,
		scenario: str = "1v3",
		max_steps: int = 200,
		turn_based: bool = False,
		difficulties: list[str] | None = None,
		fixed_dice: bool = False,
	) -> None:
		self.client = BattleClient(port)
		self.client.start(scenario, max_steps=max_steps, turn_based=turn_based)
		self.episode = 0
		self.difficulties = difficulties or []
		size = STATE_SIZE + MAX_CHOICES * ROW_SIZE
		# Unbounded: unit blocks and rows hold text table row ids.
		self.observation_space = gym.spaces.Box(-np.inf, np.inf, shape=(size,), dtype=np.float32)
		self.action_space = gym.spaces.Discrete(MAX_CHOICES)
		self.obs: dict = {}
		self.pending: dict[str, int] = {}
		self.toggles: dict[str, list[int]] = {}
		# What the deciding unit chose to do, while it chooses on whom or where; None otherwise.
		self.group: list[int] | None = None
		# Fixed dice: each battle step's random numbers restart from (episode seed, step number).
		self.fixed_dice = fixed_dice
		self.seed = 0
		self.battle_steps = 0
		self.winner = ""
		# Combat rounds played (game time / 6 s), the budget a trainer can count in, and the rounds
		# the last gym step took (0 for a decision that did not run the battle, such as a switch).
		self.rounds = 0.0
		self.last_rounds = 0.0
		# The observation of a step that ended the episode at the step limit: the state the battle
		# was cut off in, whose value the trainer bootstraps the step with.
		self.terminal_observation: np.ndarray | None = None

	def _unit(self) -> str | None:
		if self.group is not None:
			return self.obs["candidates"][self.group[0]]["unit"]
		for unit in deciders(self.obs):
			if unit not in self.pending and groups(self.obs, unit, self.toggles.get(unit, [])):
				return unit
		return None

	def choices(self) -> list[list[int]]:
		"""The deciding unit's choices now, as candidate indices (see choices())."""
		unit = self._unit()
		if unit is None:
			return []
		return choices(self.obs, unit, self.toggles.get(unit, []), self.group)

	def action_masks(self) -> np.ndarray:
		return mask(len(self.choices()))

	def _encode(self) -> np.ndarray:
		unit = self._unit()
		switched = self.toggles.get(unit, []) if unit else []
		TABLE.ensure(guids(self.obs), self.client.describe)
		return encode(self.obs, unit, switched, self.group)

	def _advance(self, choices: list[int]) -> tuple[float, bool, bool]:
		"""Steps the battle until a controlled unit decides or the episode ends."""
		total = 0.0
		while True:
			dice = seeds.dice(self.seed, self.battle_steps) if self.fixed_dice else None
			self.battle_steps += 1
			result = self.client.step(choices, dice)
			taken = (result["observation"]["time"] - self.obs["time"]) / ROUND_SECONDS
			self.rounds += taken
			self.last_rounds += taken
			total += step_reward(self.obs, result["observation"], result)
			self.obs = result["observation"]
			self.pending, self.toggles, self.group = {}, {}, None
			if result["ended"]:
				self.winner = winner(result)
				# Ended without truncation is terminal even without a winner (no side has a
				# conscious unit, such as two units downing each other in one frame).
				truncated = bool(result["truncated"])
				return total, not truncated, truncated
			if self._unit() is not None:
				return total, False, False
			choices = []

	def reset(self, *, seed: int | None = None, options: dict | None = None):
		"""Builds the battle with the given seed, or with the next training seed (seeds.train)."""
		super().reset(seed=seed)
		if seed is None:
			seed = seeds.train(self.episode)
			self.episode += 1
		level = (options or {}).get("difficulty")
		if level is None and self.difficulties:
			pick = np.random.default_rng([seed, 1]).integers(len(self.difficulties))
			level = self.difficulties[int(pick)]
		self.obs = self.client.reset(seed, level)
		self.seed, self.battle_steps = seed, 0
		self.pending, self.toggles, self.group, self.winner = {}, {}, None, ""
		if self._unit() is None:
			self._advance([])
		return self._encode(), {}

	def step(self, action: int):
		self.last_rounds = 0.0
		self.terminal_observation = None
		unit = self._unit()
		row = self.choices()[int(action)]
		if self.group is None and len(row) > 1:
			# What to do, with several targets: on whom or where is the next decision.
			self.group = row
			return self._encode(), 0.0, False, False, {}
		chosen, self.group = row[0], None
		if is_switch(self.obs["candidates"][chosen]):
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
		observation = self._encode()
		if truncated:
			self.terminal_observation = observation
		return observation, float(total), terminated, truncated, info

	def close(self) -> None:
		self.client.close()
