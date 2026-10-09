"""Check public documentation, game-file and optional private submodule boundaries."""

from __future__ import annotations

import configparser
import re
import subprocess
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import unquote, urlsplit

REQUIRED: tuple[str, ...] = (
	"README.md",
	"CLAUDE.md",
	"AGENTS.md",
	".github/copilot-instructions.md",
	"CONTRIBUTING.md",
	"LICENSE",
	"STATUS.md",
	"docs/README.md",
	"data/README.md",
	"src/README.md",
	"tests/README.md",
	".github/workflows/ci.yml",
)
ALIASES: dict[str, str] = {
	"AGENTS.md": "CLAUDE.md",
	".github/copilot-instructions.md": "../CLAUDE.md",
}
PRIVATE_MODULES: frozenset[str] = frozenset({"notes"})
# Licensed WotR files and the local install path must never be tracked.
GAME_SUFFIXES: frozenset[str] = frozenset({".dll", ".exe", ".bbp", ".assets", ".ress", ".bundle"})
GAME_DIRECTORIES: frozenset[str] = frozenset({"wrath_data", "bundles", "vendor"})
LINK: re.Pattern[str] = re.compile(r"\[[^\]\n]*\]\((<[^>\n]+>|[^\s)]+)(?:\s+\"[^\"]*\")?\)")


@dataclass(frozen=True)
class Entry:
	mode: str
	path: str


def boundary_errors(entries: list[Entry]) -> list[str]:
	errors: list[str] = []
	for entry in entries:
		parts: tuple[str, ...] = Path(entry.path).parts
		if any(part in {".obsidian", ".smart-env", ".aws"} for part in parts):
			errors.append(f"Local configuration is tracked: {entry.path}")
		if "credentials-backup" in entry.path.lower():
			errors.append(f"Credential backup is tracked: {entry.path}")
		if any(
			part.startswith(".env") and part not in {".env.example", ".env.template"}
			for part in parts
		):
			errors.append(f"Environment settings are tracked: {entry.path}")
		if parts[0] in PRIVATE_MODULES and (len(parts) != 1 or entry.mode != "160000"):
			errors.append(f"Private material must use an optional gitlink: {entry.path}")
		if parts[0] == "docs" and entry.mode in {"120000", "160000"}:
			errors.append(f"Public docs must be code-owned files: {entry.path}")
		if (
			Path(entry.path).suffix.lower() in GAME_SUFFIXES
			or any(part.lower() in GAME_DIRECTORIES for part in parts)
			or Path(entry.path).name == "GamePath.props"
		):
			errors.append(f"Game files or local game paths are tracked: {entry.path}")
	return errors


def tracked_entries(root: Path) -> list[Entry]:
	result: str = subprocess.check_output(
		["git", "-C", str(root), "ls-files", "--stage", "-z"], text=True
	)
	entries: list[Entry] = []
	for record in result.split("\0"):
		if not record:
			continue
		metadata, path = record.split("\t", 1)
		mode, _oid, stage = metadata.split()
		if stage != "0":
			raise ValueError(f"Resolve the index conflict before checking: {path}")
		entries.append(Entry(mode, path))
	return entries


def check_repository(root: Path) -> list[str]:
	entries: list[Entry] = tracked_entries(root)
	tracked: dict[str, Entry] = {entry.path: entry for entry in entries}
	errors: list[str] = boundary_errors(entries)
	for filename in REQUIRED:
		if filename not in tracked:
			errors.append(f"Required entry is missing: {filename}")
	for filename, target in ALIASES.items():
		entry: Entry | None = tracked.get(filename)
		file: Path = root / filename
		if (
			entry is None
			or entry.mode != "120000"
			or not file.is_symlink()
			or file.readlink().as_posix() != target
		):
			errors.append(f"{filename} must point to {target}")
	modules: configparser.ConfigParser = configparser.ConfigParser(interpolation=None)
	modules.read(root / ".gitmodules")
	for entry in entries:
		if entry.mode != "160000" or entry.path not in PRIVATE_MODULES:
			continue
		sections: list[str] = [
			section
			for section in modules.sections()
			if modules.get(section, "path", fallback="") == entry.path
		]
		if len(sections) != 1:
			errors.append(f"Configure the gitlink in .gitmodules: {entry.path}")
			continue
		branch: str = modules.get(sections[0], "branch", fallback="")
		if not re.fullmatch(r"project/[a-z0-9]+(?:-[a-z0-9]+)*", branch):
			errors.append(f"Private gitlink must track project/<slug>: {entry.path}")
	for entry in entries:
		if entry.mode in {"120000", "160000"} or not entry.path.endswith(".md"):
			continue
		file = root / entry.path
		fence: str | None = None
		for line in file.read_text(encoding="utf-8-sig").splitlines():
			marker: str = line.lstrip()[:3]
			if marker in {"```", "~~~"}:
				fence = marker if fence is None else None if fence == marker else fence
				continue
			if fence is not None:
				continue
			for match in LINK.finditer(line):
				url = urlsplit(match[1].strip("<>"))
				if url.scheme or url.netloc or not url.path or url.path.startswith("/"):
					continue
				target_path: Path = (file.parent / unquote(url.path)).resolve()
				if target_path.is_relative_to(root):
					relative_parts: tuple[str, ...] = target_path.relative_to(root).parts
					module_entry: Entry | None = (
						tracked.get(relative_parts[0]) if relative_parts else None
					)
					if module_entry and module_entry.mode == "160000":
						continue
				if not target_path.exists():
					errors.append(f"Broken public link: {entry.path} -> {url.path}")
	return errors


def main() -> None:
	root: Path = Path(__file__).resolve().parents[1]
	errors: list[str] = check_repository(root)
	if errors:
		raise SystemExit("\n".join(errors))
	print("Repository entries, public links, instruction aliases and private boundaries passed.")


if __name__ == "__main__":
	main()
