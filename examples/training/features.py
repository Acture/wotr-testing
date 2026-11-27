"""Features of a candidate action, for policies that score each candidate on its own."""

from __future__ import annotations

import math

import numpy as np
from client import paths
from rewards import health

KINDS = [
	"Wait",
	"Attack",
	"Move",
	"EndTurn",
	"UseAbility",
	"Toggle",
	"Delay",
	"MoveTo",
	"AutoUse",
	"Hold",
	"Stop",
	"Stealth",
	"WeaponSet",
	"Grip",
]
ENTRY_TYPES = ["spell", "item", "ability", "toggle"]
NAMES = (
	[k.lower() for k in KINDS]
	+ ["entry-" + e for e in ENTRY_TYPES]
	+ [
		"target-health",
		"target-ally",
		"target-distance/10",
		"target-self",
		"target-weakest",
		"target-closest",
		"path-distance/10",
		"variant",
		"conversion",
		"weapon-set/3",
		"auto-use-active",
		"choice-targets/10",
		"choice-target",
	]
)
# What an action bar slot does (the unit's "slots", as the game's ability tooltip shows it), for
# the candidates that use, auto-use or switch one; which ability it is goes by its text (texts.py).
TYPES = ["Spell", "SpellLike", "Supernatural", "Extraordinary", "Special", "Physical"]
ACTIONS = ["Free", "Standard", "Move", "Swift"]
SAVES = ["Fortitude", "Reflex", "Will"]
# The game's SpellDescriptor flags.
DESCRIPTORS = (
	"Fire Acid Cold Electricity MindAffecting Fear Compulsion Emotion Poison Summoning Good Evil "
	"Chaos Law Disease Charm Daze Sickened Shaken Fatigue Staggered Nauseated Frightened Exhausted "
	"Stun Paralysis Confusion Blindness Curse Death Sonic Cure Sleep Polymorph Trample Force "
	"StatDebuff RestoreHP TemporaryHP Bomb BreathWeapon Bleed VilderavnBleed SightBased Ground "
	"Petrified NegativeEmotion GazeAttack Metal MovementImpairing Hex FearImmunity UndeadControl "
	"ChannelPositiveHeal ChannelNegativeHeal ChannelPositiveHarm ChannelNegativeHarm Arcane Divine "
	"Trap Siege Stratagem NegativeLevel AnomalyDistortion"
).split()
SLOT_NAMES = (
	["slot-type-" + t.lower() for t in TYPES]
	+ ["slot-action-" + a.lower() for a in ACTIONS]
	+ [
		"slot-full-round",
		"slot-range/30",
		"slot-range-unlimited",
		"slot-targets-enemies",
		"slot-targets-allies",
		"slot-targets-self",
		"slot-targets-point",
		"slot-enemy-harmful",
		"slot-enemy-helpful",
		"slot-ally-harmful",
		"slot-ally-helpful",
		"slot-area",
		"slot-area-radius/10",
		"slot-spell-level/9",
		"slot-dc/30",
		"slot-caster-level/20",
	]
	+ ["slot-save-" + v.lower() for v in SAVES]
	+ ["slot-spell-resistance", "slot-provokes", "slot-uses/10", "slot-uses-unlimited", "slot-on"]
	+ ["slot-descriptor-" + d for d in DESCRIPTORS]
)
NAMES += SLOT_NAMES
SIZE = len(NAMES)

LEVELS = ["Story", "Casual", "Normal", "Daring", "Core", "Hard", "Unfair"]
STATS = [
	"ExtraDecline",
	"StrongDecline",
	"Decline",
	"SlightDecline",
	"None",
	"Improvement",
	"StrongImprovement",
]
CRITS = ["Off", "Weak", "Normal"]
DIFFICULTY_NAMES = ["level", "damage-to-party", "enemy-stats", "enemy-crits", "additional-ai"]
DIFFICULTY_SIZE = len(DIFFICULTY_NAMES)


def difficulty(obs: dict) -> np.ndarray:
	"""The game's difficulty settings the battle runs on, each scaled to about [0, 1]."""
	d = obs["difficulty"]
	level = LEVELS.index(d["level"]) / (len(LEVELS) - 1) if d["level"] in LEVELS else 0.5
	return np.array(
		[
			level,
			d["damageToParty"] / 2,
			STATS.index(d["statsAdjustments"]) / (len(STATS) - 1),
			CRITS.index(d["enemyCriticalHits"]) / (len(CRITS) - 1),
			float(d["additionalAiBehaviors"]),
		],
		dtype=np.float32,
	)


def candidate(obs: dict, cand: dict) -> np.ndarray:
	"""One candidate's features, relative to the unit that takes it."""
	units = {u["id"]: u for u in obs["units"]}
	me = units[cand["unit"]]
	f = np.zeros(SIZE, dtype=np.float32)
	f[KINDS.index(cand["kind"])] = 1.0
	ability = cand.get("ability") or ""
	entry = ability.split(":", 1)[0]
	if entry in ENTRY_TYPES:
		f[len(KINDS) + ENTRY_TYPES.index(entry)] = 1.0
	base = len(KINDS) + len(ENTRY_TYPES)
	target = units.get(cand.get("target"))
	if target is not None:
		enemies = [u for u in obs["units"] if u["side"] != me["side"] and u["conscious"]]
		f[base] = health(target)
		f[base + 1] = 1.0 if target["side"] == me["side"] else -1.0
		f[base + 2] = math.hypot(target["x"] - me["x"], target["z"] - me["z"]) / 10
		f[base + 3] = float(target["id"] == me["id"])
		if enemies:
			f[base + 4] = float(min(enemies, key=health)["id"] == target["id"])
			closest = min(enemies, key=lambda u: math.hypot(u["x"] - me["x"], u["z"] - me["z"]))
			f[base + 5] = float(closest["id"] == target["id"])
	f[base + 6] = ((paths(obs).get(cand.get("path")) or {}).get("distance") or 0) / 10
	f[base + 7] = float(cand.get("variant") is not None)
	f[base + 8] = float(">" in ability)
	if cand["kind"] == "WeaponSet":
		# The set the candidate switches to, so the policy tells the sets apart.
		f[base + 9] = (int(cand["variant"]) if cand.get("variant") is not None else 0) / 3
	slot = next((s for s in me.get("slots", []) if s["id"] == ability), None) if ability else None
	if slot is not None:
		f[SIZE - len(SLOT_NAMES) :] = slot_features(slot)
	if cand["kind"] == "AutoUse" and slot is not None:
		# The ability the unit's AI uses on its own: the attack it makes is that ability's cast.
		if slot.get("blueprint") == me.get("autoUse"):
			f[base + 10] = 1.0
	return f


TARGET = len(KINDS) + len(ENTRY_TYPES)
CHOICE = NAMES.index("choice-targets/10")


def option(obs: dict, cands: list[dict]) -> np.ndarray:
	"""
	One choice of what to do: candidates that differ only in their target (gym_env.groups). The
	first one's features, with its targets summed up: the lowest health among them, the share of
	allies, the nearest distance, whether one is the unit itself, the weakest or the closest enemy,
	the shortest path; and how many targets there are.
	"""
	f = candidate(obs, cands[0])
	if len(cands) > 1:
		fs = np.array([candidate(obs, c) for c in cands])
		t = TARGET
		f[t] = fs[:, t].min()
		f[t + 1] = fs[:, t + 1].mean()
		f[t + 2] = fs[:, t + 2].min()
		f[t + 3 : t + 6] = fs[:, t + 3 : t + 6].max(axis=0)
		f[t + 6] = fs[:, t + 6].min()
	f[CHOICE] = len(cands) / 10
	return f


def target(obs: dict, cand: dict) -> np.ndarray:
	"""One target of a chosen thing to do: the candidate's features, marked as a target choice."""
	f = candidate(obs, cand)
	f[CHOICE + 1] = 1.0
	return f


def slot_key(obs: dict, cand: dict) -> str | None:
	"""Blueprint guid of the action bar slot the candidate uses, auto-uses or switches, if any."""
	ability = cand.get("ability")
	if not ability:
		return None
	me = next(u for u in obs["units"] if u["id"] == cand["unit"])
	slot = next((s for s in me.get("slots", []) if s["id"] == ability), None)
	return slot.get("guid") if slot else None


def slot_features(slot: dict) -> np.ndarray:
	"""One action bar slot's features (SLOT_NAMES)."""
	f = np.zeros(len(SLOT_NAMES), dtype=np.float32)
	k = 0

	def one_hot(value, values: list[str]) -> None:
		nonlocal k
		if value in values:
			f[k + values.index(value)] = 1.0
		k += len(values)

	def put(*values: float) -> None:
		nonlocal k
		for v in values:
			f[k] = v
			k += 1

	one_hot(slot.get("type"), TYPES)
	one_hot(slot.get("action"), ACTIONS)
	meters = slot.get("rangeMeters")
	unlimited = slot.get("range") == "Unlimited" or (slot.get("range") and meters is None)
	put(float(slot.get("fullRound", False)), 1.0 if unlimited else min((meters or 0) / 30, 1.0))
	put(float(bool(unlimited)))
	targets = ("targetsEnemies", "targetsAllies", "targetsSelf", "targetsPoint")
	put(*(float(slot.get(key, False)) for key in targets))
	for side in ("effectOnEnemy", "effectOnAlly"):
		put(float(slot.get(side) == "Harmful"), float(slot.get(side) == "Helpful"))
	put(float(slot.get("areaOfEffect", False)), slot.get("areaRadius", 0) / 10)
	put(slot.get("spellLevel", 0) / 9, (slot.get("dc") or 0) / 30)
	put((slot.get("casterLevel") or 0) / 20)
	one_hot(slot.get("savingThrow"), SAVES)
	uses = slot.get("uses", -1)
	put(float(slot.get("spellResistance", False)), float(slot.get("provokes", False)))
	put(min(max(uses, 0), 10) / 10, float(uses < 0), float(slot.get("on", False)))
	for d in slot.get("descriptors", []):
		if d in DESCRIPTORS:
			f[k + DESCRIPTORS.index(d)] = 1.0
	return f
