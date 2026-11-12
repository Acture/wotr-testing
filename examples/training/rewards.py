"""Rewards the training code computes from observations; the environment reports state."""

from __future__ import annotations

import statistics

SIDE = "party"


def health(unit: dict) -> float:
	"""Hit points as a fraction of the maximum, clamped to [0, 1]."""
	return min(max(unit["hp"], 0), unit["maxHp"]) / max(1, unit["maxHp"])


def spent(before: dict, after: dict) -> float:
	"""Resources a unit spent: ability resources, spell slots weighted by spell level, item uses."""
	total = sum(max(0, n - after["resources"].get(k, 0)) for k, n in before["resources"].items())
	total += sum(
		int(k.rsplit(":", 1)[1]) * max(0, n - after["spellSlots"].get(k, 0))
		for k, n in before["spellSlots"].items()
	)
	total += sum(
		max(0, n - after["itemUses"].get(k, 0)) for k, n in before["itemUses"].items() if n > 0
	)
	return total


def step_reward(before: dict, after: dict, result: dict, side: str = SIDE) -> float:
	"""
	The reward of one step: damage dealt and own health lost (as fractions of maximum hit points),
	0.5 for each unit downed on either side, 0.1 for each resource spent, and +1 or -1 for the
	outcome. An episode cut off at the step limit counts as lost with every own unit downed, so
	dragging a battle out (fleeing, stalling) never pays better than fighting it to the end.
	"""
	b = {u["id"]: u for u in before["units"]}
	a = {u["id"]: u for u in after["units"]}
	r = 0.0
	for uid, ub in b.items():
		ua = a[uid]
		lost = health(ub) - health(ua)
		downed = float(ub["conscious"] and not ua["conscious"])
		if ub["side"] == side:
			r -= lost + 0.5 * downed + 0.1 * spent(ub, ua)
		else:
			r += lost + 0.5 * downed
	if result["ended"] and result["winner"]:
		r += 1.0 if result["winner"] == side else -1.0
	elif result["ended"] and result.get("truncated"):
		own = [u for u in a.values() if u["side"] == side]
		r -= 1.0 + sum(health(u) + 0.5 * float(u["conscious"]) for u in own)
	return r


def balance(obs: dict, side: str = SIDE) -> float:
	"""0.5 plus half the difference of mean health, own side minus the others, in [0, 1]."""
	own = statistics.mean(health(u) for u in obs["units"] if u["side"] == side)
	others = statistics.mean(health(u) for u in obs["units"] if u["side"] != side)
	return 0.5 + 0.5 * (own - others)


def win_value(result: dict | None, obs: dict, side: str = SIDE) -> float:
	"""
	Win-based value with partial credit: a win 0.75 to 1 (more health left is better), a loss 0 to
	0.25 (enemies closer to defeat is better), an undecided end 0.25 to 0.75 by the balance of
	health.
	"""
	if result is not None and result["ended"] and result["winner"]:
		if result["winner"] == side:
			return 0.75 + 0.25 * statistics.mean(
				health(u) for u in obs["units"] if u["side"] == side
			)
		return 0.25 * (1 - statistics.mean(health(u) for u in obs["units"] if u["side"] != side))
	return 0.25 + 0.5 * balance(obs, side)
