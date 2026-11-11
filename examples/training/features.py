"""Features of a candidate action, for policies that score each candidate on its own."""

from __future__ import annotations

import math

import numpy as np
from rewards import health

KINDS = ["Wait", "Attack", "Move", "EndTurn", "UseAbility", "Toggle", "Delay", "MoveTo"]
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
	]
)
SIZE = len(NAMES)


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
	f[base + 6] = (obs["paths"].get(cand.get("path")) or 0) / 10
	f[base + 7] = float(cand.get("variant") is not None)
	f[base + 8] = float(">" in ability)
	return f
