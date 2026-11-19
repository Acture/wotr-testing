"""
Text embeddings of abilities, toggles and buffs. Each blueprint's in-game name and description
(the server's describe request, read from the game's localization pack) is embedded once by a
local sentence embedding model, so a policy tells abilities apart by what their text says and
places one it has never seen next to the ones its text resembles. An observation carries row ids
of TABLE (0 for none); the policy looks the vectors up.

Game text and the vectors made from it stay on this machine: the model runs locally, nothing is
sent anywhere, and saved tables (TextTable.save) must not be committed or published.
"""

from __future__ import annotations

import re
from collections.abc import Callable, Iterable

import numpy as np

MODEL = "sentence-transformers/all-MiniLM-L6-v2"
DIM = 384
CAPACITY = 8192

# The game's text markup: {g|Encyclopedia:Spell}spell{/g}, {d|...}, <b>...</b> and the like.
MARKUP = re.compile(r"\{[^{}]*\}|<[^<>]*>")


def text(entry: dict | None) -> str:
	"""A blueprint's name and description as one plain text; empty when the server knew none."""
	if not entry:
		return ""
	parts = [MARKUP.sub("", entry.get(key) or "").strip() for key in ("name", "description")]
	return ". ".join(p for p in parts if p)


class TextTable:
	"""Rows of text embeddings by blueprint guid; row 0 is the zero vector (none)."""

	def __init__(self, capacity: int = CAPACITY, dim: int = DIM) -> None:
		self.ids: dict[str, int] = {}
		self.vectors = np.zeros((capacity, dim), dtype=np.float32)
		self.version = 0
		self._model = None
		self._tensors: dict = {}

	def id(self, guid: str | None) -> int:
		return self.ids.get(guid, 0) if guid else 0

	def ensure(self, guids: Iterable[str | None], describe: Callable[[list[str]], dict]) -> None:
		"""Adds the guids not in the table: their texts from `describe`, embedded in one batch."""
		new = sorted({g for g in guids if g and g not in self.ids})
		if not new:
			return
		if len(self.ids) + len(new) >= len(self.vectors):
			raise ValueError(f"text table of {len(self.vectors)} rows is full")
		described = describe(new)
		texts = [text(described.get(g)) for g in new]
		vectors = self.embed(texts)
		for guid, words, vector in zip(new, texts, vectors, strict=True):
			row = len(self.ids) + 1
			self.ids[guid] = row
			self.vectors[row] = vector if words else 0.0
		self.version += 1

	def add(self, guid: str, vector: np.ndarray) -> int:
		"""The guid's row, adding it with an embedding made elsewhere (another worker's table)."""
		known = self.ids.get(guid)
		if known is not None:
			return known
		if len(self.ids) + 1 >= len(self.vectors):
			raise ValueError(f"text table of {len(self.vectors)} rows is full")
		row = len(self.ids) + 1
		self.ids[guid] = row
		self.vectors[row] = vector
		self.version += 1
		return row

	def rows(self, after: int = 0) -> tuple[list[str], np.ndarray]:
		"""The guids of the rows after row `after`, in row order, and their embeddings."""
		guids = sorted(self.ids, key=self.ids.__getitem__)[after:]
		return guids, self.vectors[after + 1 : after + 1 + len(guids)].copy()

	def embed(self, texts: list[str]) -> np.ndarray:
		if self._model is None:
			from sentence_transformers import SentenceTransformer

			self._model = SentenceTransformer(MODEL, device="cpu")
		return self._model.encode(texts, normalize_embeddings=True, convert_to_numpy=True)

	def tensor(self, device):
		"""The table as a torch tensor on `device`, copied again only after rows were added."""
		import torch

		key = str(device)
		cached = self._tensors.get(key)
		if cached is None or cached[0] != self.version:
			cached = (self.version, torch.as_tensor(self.vectors, device=device))
			self._tensors[key] = cached
		return cached[1]

	def save(self, path: str) -> None:
		"""The rows and their guids (no text), to play a saved model; keep the file local."""
		guids = sorted(self.ids, key=self.ids.__getitem__)
		np.savez_compressed(path, guids=np.array(guids), vectors=self.vectors[1 : len(guids) + 1])

	def load(self, path: str) -> None:
		saved = np.load(path)
		self.ids = {str(g): k + 1 for k, g in enumerate(saved["guids"])}
		self.vectors[:] = 0.0
		self.vectors[1 : len(self.ids) + 1] = saved["vectors"]
		self.version += 1


TABLE = TextTable()


def guids(obs: dict) -> list[str]:
	"""Every ability, toggle and buff guid in an observation: the slots and buffs of all units."""
	found = []
	for u in obs["units"]:
		found += [s.get("guid") for s in u.get("slots", [])]
		found += u.get("buffGuids", [])
	return [g for g in found if g]
