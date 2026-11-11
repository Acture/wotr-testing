"""Client of BattleEnvironmentServer: one JSON object per line over a local TCP connection."""

from __future__ import annotations

import json
import socket
import time
from typing import Any


class BattleClient:
	"""Connects to the server a battle fixture test runs (WOTR_TRAIN_PORT); sends its requests."""

	def __init__(self, port: int, wait_seconds: float = 900) -> None:
		deadline = time.time() + wait_seconds
		while True:
			try:
				self.sock = socket.create_connection(("127.0.0.1", port))
				break
			except OSError:
				if time.time() > deadline:
					raise
				time.sleep(1)
		self.file = self.sock.makefile("rw", encoding="utf-8", newline="\n")

	def call(self, **request: Any) -> dict:
		self.file.write(json.dumps(request) + "\n")
		self.file.flush()
		line = self.file.readline()
		if not line:
			raise ConnectionError("The battle server closed the connection.")
		response = json.loads(line)
		if "error" in response:
			raise RuntimeError(response["error"])
		return response

	def start(
		self, scenario: str, seed: int = 1, max_steps: int = 200, turn_based: bool = False
	) -> dict:
		return self.call(
			op="start", scenario=scenario, seed=seed, maxSteps=max_steps, turnBased=turn_based
		)["observation"]

	def reset(self, seed: int | None = None) -> dict:
		return self.call(op="reset", seed=seed)["observation"]

	def step(self, choices: list[int]) -> dict:
		return self.call(op="step", choices=choices)

	def close(self) -> None:
		self.call(op="close")
		self.sock.close()


def by_unit(obs: dict) -> dict[str, list[int]]:
	"""Candidate indices of each ready unit, without the ones that stand for any point."""
	options: dict[str, list[int]] = {}
	for i, c in enumerate(obs["candidates"]):
		if c["unit"] in obs["ready"] and not c.get("anyPoint"):
			options.setdefault(c["unit"], []).append(i)
	return options
