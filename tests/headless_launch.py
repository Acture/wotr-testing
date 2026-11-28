"""Muted, recoverable headless launch harness for in-game WotR test runs.

One command prepares a game installation (deploys the test assembly and its
dependency mods, quarantines every other mod, mutes the game's audio settings),
launches the game through Steam or directly, waits with a timeout, collects the
test report and restores everything the run touched. Cleanup runs on success,
failure, timeout and interruption, and the harness only manages processes it
started itself: game processes that already run are detected and never touched
(by default the run is refused while they exist).

The repository checks never launch the game: every process and Steam interaction
sits behind `ProcessGateway`, and the unit tests drive the harness with fakes.
Launching the real game needs the owner's authorization; tests/README.md lists
what is verified offline and what still needs an authorized launch.

Contract with the in-game test assembly: the harness appends
`--wotr-headless-report=<path>` and `--wotr-headless-run-id=<id>` to the game's
command line and sets the WOTR_HEADLESS_REPORT, WOTR_HEADLESS_RUN_ID and
WOTR_HEADLESS_RESULTS environment variables. The test assembly writes a JSON
report to that path; a run without a fresh report fails, and a report whose
"result" field is not "passed" fails. The test assembly is also expected to mute
Unity and Wwise audio from its entry method, as a second layer after the
settings-file mute this harness applies before launch.

Usage:
    python tests/headless_launch.py --game-dir <game> --test-assembly <dll> \
        --results-dir <dir> [--mod <dir>]... [--timeout 900] [--dry-run]
"""

from __future__ import annotations

import argparse
import hashlib
import importlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import uuid
import xml.etree.ElementTree as ET
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Protocol

DEFAULT_APP_ID = "2201390"
DEFAULT_EXE_NAME = "Wrath.exe"
DEFAULT_TIMEOUT_SECONDS = 900.0
DEFAULT_POLL_SECONDS = 0.5
DEFAULT_LAUNCH_GRACE_SECONDS = 180.0
DEFAULT_SETTLE_SECONDS = 10.0
DEFAULT_UNITY_ARGS: tuple[str, ...] = ("-nographics",)
QUARANTINE_DIR_NAME = ".wotr-headless-quarantine"
MANIFEST_NAME = "wotr-headless-manifest.json"
REPORT_NAME = "wotr-headless-report.json"
VOLUME_KEY = re.compile(r"volume|mute", re.IGNORECASE)
TERMINATE_WAIT_SECONDS = 15.0

EXIT_PASSED = 0
EXIT_FAILED = 1
EXIT_REFUSED = 3
EXIT_TIMEOUT = 124
EXIT_INTERRUPTED = 130


class HarnessError(Exception):
	"""A pre-flight or run failure with a user-facing message; cleanup still runs."""


class _RunStop(Exception):
	"""Ends the run early with a final outcome; cleanup and the manifest still run."""

	def __init__(self, outcome: str, exit_code: int, detail: str) -> None:
		super().__init__(detail)
		self.outcome = outcome
		self.exit_code = exit_code
		self.detail = detail


@dataclass(frozen=True)
class GameProcess:
	pid: int
	exe_path: str


class ProcessGateway(Protocol):
	"""The process edge of the harness; tests replace it with fakes."""

	def find_game_processes(self, game_dir: Path, exe_name: str) -> list[GameProcess]: ...
	def start(self, command: list[str], env: dict[str, str], cwd: Path) -> int: ...
	def is_running(self, pid: int) -> bool: ...
	def terminate(self, pid: int) -> None: ...


class NoProcessesGateway:
	"""A gateway for dry runs off Windows: nothing is found, launches are refused."""

	def find_game_processes(self, game_dir: Path, exe_name: str) -> list[GameProcess]:
		return []

	def start(self, command: list[str], env: dict[str, str], cwd: Path) -> int:
		raise HarnessError("Launching the game needs Windows.")

	def is_running(self, pid: int) -> bool:
		return False

	def terminate(self, pid: int) -> None:
		raise HarnessError("Launching the game needs Windows.")


def _kernel32() -> Any:
	import ctypes

	windll = getattr(ctypes, "windll", None)
	if windll is None:
		raise HarnessError("Headless game launches need Windows.")
	return windll.kernel32


def _get_last_error() -> int:
	import ctypes

	# GetLastError exists only on Windows; resolve it dynamically so the module
	# stays importable and type-checkable on any platform.
	get_last_error = getattr(ctypes, "GetLastError", None)
	if get_last_error is None:
		raise HarnessError("Headless game launches need Windows.")
	return int(get_last_error())


def _is_under(path: str, root: Path) -> bool:
	try:
		return Path(path).resolve().is_relative_to(root.resolve())
	except OSError:
		return False


def _snapshot_processes(kernel32: Any) -> list[tuple[int, str]]:
	import ctypes
	from ctypes import wintypes

	class ProcessEntry32(ctypes.Structure):
		_fields_ = [
			("dwSize", wintypes.DWORD),
			("cntUsage", wintypes.DWORD),
			("th32ProcessID", wintypes.DWORD),
			("th32DefaultHeapID", ctypes.c_size_t),
			("th32ModuleID", wintypes.DWORD),
			("cntThreads", wintypes.DWORD),
			("th32ParentProcessID", wintypes.DWORD),
			("pcPriClassBase", ctypes.c_long),
			("dwFlags", wintypes.DWORD),
			("szExeFile", wintypes.WCHAR * 260),
		]

	snapshot = kernel32.CreateToolhelp32Snapshot(0x2, 0)  # TH32CS_SNAPPROCESS
	if not snapshot or snapshot == -1:  # INVALID_HANDLE_VALUE
		raise HarnessError("Cannot list processes (CreateToolhelp32Snapshot failed).")
	processes: list[tuple[int, str]] = []
	try:
		entry = ProcessEntry32()
		entry.dwSize = ctypes.sizeof(ProcessEntry32)
		ok = kernel32.Process32FirstW(snapshot, ctypes.byref(entry))
		while ok:
			processes.append((int(entry.th32ProcessID), str(entry.szExeFile)))
			ok = kernel32.Process32NextW(snapshot, ctypes.byref(entry))
	finally:
		kernel32.CloseHandle(snapshot)
	return processes


def _process_image_path(kernel32: Any, pid: int) -> str | None:
	import ctypes
	from ctypes import wintypes

	handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
	if not handle:
		return None
	try:
		buffer = ctypes.create_unicode_buffer(1024)
		size = wintypes.DWORD(1024)
		if not kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
			return None
		return str(buffer.value)
	finally:
		kernel32.CloseHandle(handle)


class WindowsProcessGateway:
	"""Real process edge on Windows: Toolhelp snapshots, OpenProcess and Popen.

	Nothing Windows-only runs at import time, so the module stays importable on
	any platform; constructing the gateway off Windows raises HarnessError.
	"""

	def __init__(self) -> None:
		if sys.platform != "win32":
			raise HarnessError("Headless game launches need Windows.")
		self._children: dict[int, subprocess.Popen[bytes]] = {}

	def find_game_processes(self, game_dir: Path, exe_name: str) -> list[GameProcess]:
		kernel32 = _kernel32()
		processes: list[GameProcess] = []
		for pid, image in _snapshot_processes(kernel32):
			if image.lower() != exe_name.lower():
				continue
			exe_path = _process_image_path(kernel32, pid)
			# A process whose path cannot be read (for example an elevated game)
			# still counts: the run refuses it by default instead of ignoring it.
			if exe_path is not None and not _is_under(exe_path, game_dir):
				continue
			processes.append(GameProcess(pid, exe_path or image))
		return processes

	def start(self, command: list[str], env: dict[str, str], cwd: Path) -> int:
		child = subprocess.Popen(command, env=env, cwd=str(cwd))
		self._children[child.pid] = child
		return child.pid

	def is_running(self, pid: int) -> bool:
		import ctypes

		child = self._children.get(pid)
		if child is not None:
			return child.poll() is None
		kernel32 = _kernel32()
		handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
		if not handle:
			return _get_last_error() == 5  # ERROR_ACCESS_DENIED: exists but protected
		try:
			code = ctypes.c_ulong()
			if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
				return False
			return code.value == 259  # STILL_ACTIVE
		finally:
			kernel32.CloseHandle(handle)

	def terminate(self, pid: int) -> None:
		kernel32 = _kernel32()
		handle = kernel32.OpenProcess(0x0001, False, pid)  # PROCESS_TERMINATE
		if not handle:
			if not self.is_running(pid):
				return
			raise HarnessError(f"Cannot open process {pid} to terminate it.")
		try:
			if not kernel32.TerminateProcess(handle, 1):
				raise HarnessError(f"Cannot terminate process {pid}.")
		finally:
			kernel32.CloseHandle(handle)


@dataclass(frozen=True)
class HarnessConfig:
	game_dir: Path
	test_assembly: Path
	mods: tuple[Path, ...]
	results_dir: Path
	timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS
	launch_mode: str = "steam"
	steam_exe: Path | None = None
	app_id: str = DEFAULT_APP_ID
	exe_name: str = DEFAULT_EXE_NAME
	mod_name: str = ""
	entry_method: str | None = None
	settings_file: Path | None = None
	unity_args: tuple[str, ...] = DEFAULT_UNITY_ARGS
	allow_existing_game: bool = False
	keep_other_mods: bool = False
	mute: bool = True
	collect_player_log: bool = True
	dry_run: bool = False
	test_assembly_version: str | None = None
	poll_seconds: float = DEFAULT_POLL_SECONDS
	launch_grace_seconds: float = DEFAULT_LAUNCH_GRACE_SECONDS
	settle_seconds: float = DEFAULT_SETTLE_SECONDS


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
	parser = argparse.ArgumentParser(
		prog="headless_launch",
		description="Muted, recoverable headless launch of WotR for in-game test runs.",
	)
	parser.add_argument(
		"--game-dir",
		help="WotR installation; defaults to WOTR_GAME_DIR, WrathPath/WRATH_PATH, "
		"then WrathInstallDir from the untracked GamePath.props.",
	)
	parser.add_argument("--test-assembly", required=True, help="Built in-game test assembly (DLL).")
	parser.add_argument(
		"--mod",
		action="append",
		default=[],
		help="Dependency mod folder with its own Info.json; repeatable.",
	)
	parser.add_argument("--results-dir", required=True, help="Run output: report, manifest, logs.")
	parser.add_argument(
		"--timeout",
		type=float,
		default=DEFAULT_TIMEOUT_SECONDS,
		help="Seconds before the game is terminated and the run fails.",
	)
	parser.add_argument("--launch-mode", choices=("steam", "direct"), default="steam")
	parser.add_argument(
		"--steam-exe", help="steam.exe; defaults to WOTR_STEAM_EXE, then the registry."
	)
	parser.add_argument("--app-id", default=DEFAULT_APP_ID, help="Steam app id of the game.")
	parser.add_argument("--exe-name", default=DEFAULT_EXE_NAME, help="Game executable name.")
	parser.add_argument(
		"--mod-name",
		help="Mods folder name for the test assembly; defaults to the assembly name.",
	)
	parser.add_argument(
		"--entry-method",
		help="UMM entry method for the generated Info.json; unused when an Info.json "
		"sits next to the test assembly.",
	)
	parser.add_argument(
		"--settings-file",
		help="Game settings file to mute; defaults to WOTR_SETTINGS_FILE, then the "
		"per-user LocalLow settings file.",
	)
	parser.add_argument(
		"--unity-arg",
		action="append",
		default=None,
		help="Unity argument for the game; repeatable and replaces the default "
		"(-nographics) when given. Use the --unity-arg=-flag form for values that "
		"start with a dash.",
	)
	parser.add_argument(
		"--allow-existing-game",
		action="store_true",
		help="Run while game processes exist; they are still never managed.",
	)
	parser.add_argument(
		"--keep-other-mods",
		action="store_true",
		help="Do not quarantine the other mods in the game's Mods folder.",
	)
	parser.add_argument("--no-mute", dest="mute", action="store_false")
	parser.add_argument("--no-collect-player-log", dest="collect_player_log", action="store_false")
	parser.add_argument("--test-assembly-version", help="Version string recorded in the manifest.")
	parser.add_argument(
		"--dry-run", action="store_true", help="Prepare, print the command, restore."
	)
	parser.add_argument("--poll-seconds", type=float, default=DEFAULT_POLL_SECONDS)
	parser.add_argument("--launch-grace-seconds", type=float, default=DEFAULT_LAUNCH_GRACE_SECONDS)
	parser.add_argument("--settle-seconds", type=float, default=DEFAULT_SETTLE_SECONDS)
	return parser.parse_args(argv)


def _game_dir_from_props(props: Path) -> Path | None:
	if not props.is_file():
		return None
	try:
		root = ET.parse(props).getroot()
	except ET.ParseError:
		return None
	for element in root.iter():
		if element.tag.split("}")[-1] != "WrathInstallDir":
			continue
		if element.text and element.text.strip():
			candidate = Path(element.text.strip())
			if not candidate.is_absolute():
				candidate = (props.parent / candidate).resolve()
			return candidate
	return None


def _resolve_game_dir(explicit: str | None, environ: Mapping[str, str], repo_root: Path) -> Path:
	candidates: list[tuple[str, str | None]] = [
		("--game-dir", explicit),
		("WOTR_GAME_DIR", environ.get("WOTR_GAME_DIR")),
		("WrathPath", environ.get("WrathPath")),
		("WRATH_PATH", environ.get("WRATH_PATH")),
	]
	for source, value in candidates:
		if value:
			game_dir = Path(value)
			if not game_dir.is_dir():
				raise HarnessError(f"The game directory from {source} does not exist: {game_dir}")
			return game_dir
	from_props = _game_dir_from_props(repo_root / "GamePath.props")
	if from_props is not None:
		if not from_props.is_dir():
			raise HarnessError(
				f"The game directory from GamePath.props does not exist: {from_props}"
			)
		return from_props
	raise HarnessError(
		"No game directory: pass --game-dir, set WOTR_GAME_DIR, or keep WrathInstallDir "
		"in the untracked GamePath.props at the repository root."
	)


def _steam_exe_from_registry() -> Path | None:
	if sys.platform != "win32":
		return None
	try:
		winreg = importlib.import_module("winreg")
	except ImportError:
		return None
	try:
		with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
			value = winreg.QueryValueEx(key, "SteamExe")[0]
	except OSError:
		return None
	path = Path(str(value))
	return path if path.is_file() else None


def _resolve_steam_exe(explicit: str | None, environ: Mapping[str, str]) -> Path:
	candidates = [explicit, environ.get("WOTR_STEAM_EXE")]
	for value in candidates:
		if value:
			path = Path(value)
			if not path.is_file():
				raise HarnessError(f"steam.exe does not exist: {path}")
			return path
	from_registry = _steam_exe_from_registry()
	if from_registry is not None:
		return from_registry
	standard = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"))
	standard /= "Steam/steam.exe"
	if standard.is_file():
		return standard
	raise HarnessError("Steam not found: pass --steam-exe or set WOTR_STEAM_EXE.")


def _default_settings_file(environ: Mapping[str, str]) -> Path | None:
	override = environ.get("WOTR_SETTINGS_FILE")
	if override:
		return Path(override)
	local_app_data = environ.get("LOCALAPPDATA")
	if not local_app_data:
		return None
	return (
		Path(local_app_data).parent
		/ "LocalLow"
		/ "Owlcat Games"
		/ "Pathfinder Wrath of the Righteous"
		/ "Settings.json"
	)


def load_config(
	ns: argparse.Namespace,
	repo_root: Path | None = None,
	environ: Mapping[str, str] = os.environ,
) -> HarnessConfig:
	root = repo_root or Path(__file__).resolve().parents[1]
	game_dir = _resolve_game_dir(ns.game_dir, environ, root)
	version_file = game_dir / "Wrath_Data" / "StreamingAssets" / "Version.info"
	if not version_file.is_file():
		raise HarnessError(f"Not a WotR installation (Version.info missing): {game_dir}")
	test_assembly = Path(ns.test_assembly)
	if not test_assembly.is_file() or test_assembly.suffix.lower() != ".dll":
		raise HarnessError(f"The test assembly is not a built DLL: {test_assembly}")
	mods: list[Path] = []
	for mod in ns.mod:
		mod_dir = Path(mod)
		if not mod_dir.is_dir() or not (mod_dir / "Info.json").is_file():
			raise HarnessError(f"A dependency mod needs a folder with an Info.json: {mod_dir}")
		mods.append(mod_dir)
	results_dir = Path(ns.results_dir)
	mods_dir = (game_dir / "Mods").resolve()
	results_resolved = results_dir.resolve()
	if results_resolved == mods_dir or mods_dir in results_resolved.parents:
		raise HarnessError("The results directory must not be inside the game's Mods folder.")
	mod_name = ns.mod_name or test_assembly.stem
	if Path(mod_name).name != mod_name or mod_name == QUARANTINE_DIR_NAME:
		raise HarnessError(f"The mod name must be a single plain folder name: {mod_name}")
	steam_exe = _resolve_steam_exe(ns.steam_exe, environ) if ns.launch_mode == "steam" else None
	if ns.timeout <= 0:
		raise HarnessError("The timeout must be positive.")
	for name, value in (
		("poll", ns.poll_seconds),
		("launch grace", ns.launch_grace_seconds),
		("settle", ns.settle_seconds),
	):
		if value <= 0:
			raise HarnessError(f"The {name} interval must be positive.")
	settings_file = Path(ns.settings_file) if ns.settings_file else _default_settings_file(environ)
	return HarnessConfig(
		game_dir=game_dir,
		test_assembly=test_assembly,
		mods=tuple(mods),
		results_dir=results_dir,
		timeout_seconds=ns.timeout,
		launch_mode=ns.launch_mode,
		steam_exe=steam_exe,
		app_id=ns.app_id,
		exe_name=ns.exe_name,
		mod_name=mod_name,
		entry_method=ns.entry_method,
		settings_file=settings_file,
		unity_args=tuple(ns.unity_arg) if ns.unity_arg else DEFAULT_UNITY_ARGS,
		allow_existing_game=ns.allow_existing_game,
		keep_other_mods=ns.keep_other_mods,
		mute=ns.mute,
		collect_player_log=ns.collect_player_log,
		dry_run=ns.dry_run,
		test_assembly_version=ns.test_assembly_version,
		poll_seconds=ns.poll_seconds,
		launch_grace_seconds=ns.launch_grace_seconds,
		settle_seconds=ns.settle_seconds,
	)


def _game_version(game_dir: Path) -> str:
	version_file = game_dir / "Wrath_Data" / "StreamingAssets" / "Version.info"
	try:
		return version_file.read_text(encoding="utf-8-sig").strip()
	except OSError:
		return "unknown"


def _sha256(path: Path) -> str:
	digest = hashlib.sha256()
	with path.open("rb") as stream:
		for chunk in iter(lambda: stream.read(1024 * 1024), b""):
			digest.update(chunk)
	return digest.hexdigest()


def _tree_digest(root: Path) -> str:
	digest = hashlib.sha256()
	for path in sorted(item for item in root.rglob("*") if item.is_file()):
		digest.update(str(path.relative_to(root)).encode())
		digest.update(bytes.fromhex(_sha256(path)))
	return digest.hexdigest()


def _remove(path: Path) -> None:
	if path.is_dir() and not path.is_symlink():
		shutil.rmtree(path)
	elif path.exists() or path.is_symlink():
		path.unlink()


@dataclass
class _BackupEntry:
	original: Path
	stored: Path | None
	digest: str | None
	is_dir: bool


class RunBackup:
	"""Byte-exact record of everything the run replaces, edits or adds."""

	def __init__(self, directory: Path) -> None:
		self._directory = directory
		self._entries: list[_BackupEntry] = []
		self._restored = False

	@property
	def entries(self) -> int:
		return len(self._entries)

	def protect(self, path: Path) -> None:
		"""Copy a file aside; the original stays in place until it is edited."""
		stored = self._store(path)
		shutil.copy2(path, stored)
		self._entries.append(_BackupEntry(path, stored, _sha256(path), False))

	def stash(self, path: Path) -> None:
		"""Move a file or folder aside so deployment can replace it."""
		stored = self._store(path)
		is_dir = path.is_dir()
		digest = _tree_digest(path) if is_dir else _sha256(path)
		shutil.move(str(path), stored)
		self._entries.append(_BackupEntry(path, stored, digest, is_dir))

	def mark_new(self, path: Path) -> None:
		"""Record a path the run creates; restore removes it."""
		self._entries.append(_BackupEntry(path, None, None, path.is_dir()))

	def restore(self) -> list[str]:
		"""Put every entry back, byte for byte; idempotent. Returns problems."""
		if self._restored:
			return []
		self._restored = True
		problems: list[str] = []
		for entry in reversed(self._entries):
			try:
				self._restore_entry(entry)
			except OSError as error:
				problems.append(f"{entry.original}: {error}")
		return problems

	def _restore_entry(self, entry: _BackupEntry) -> None:
		_remove(entry.original)
		if entry.stored is None:
			return
		if entry.is_dir:
			shutil.move(str(entry.stored), entry.original)
		else:
			shutil.copy2(entry.stored, entry.original)
		digest = _tree_digest(entry.original) if entry.is_dir else _sha256(entry.original)
		if digest != entry.digest:
			raise OSError("restored content does not match the backup")

	def _store(self, path: Path) -> Path:
		self._directory.mkdir(parents=True, exist_ok=True)
		return self._directory / f"{len(self._entries):03d}-{path.name}"


class ModQuarantine:
	"""Moves every other mod out of the game's Mods folder for the run."""

	def __init__(self, mods_dir: Path) -> None:
		self._mods_dir = mods_dir
		self._quarantine = mods_dir / QUARANTINE_DIR_NAME
		self._moved: list[str] = []

	@property
	def moved(self) -> list[str]:
		return list(self._moved)

	def restore_leftovers(self) -> list[str]:
		"""Put back mods a crashed run left quarantined; returns problem texts."""
		problems: list[str] = []
		if not self._quarantine.is_dir():
			return problems
		for child in sorted(self._quarantine.iterdir()):
			target = self._mods_dir / child.name
			if target.exists():
				problems.append(
					f"{target} exists while a crashed run left {child.name} quarantined"
				)
				continue
			shutil.move(str(child), target)
		try:
			self._quarantine.rmdir()
		except OSError:
			pass
		return problems

	def quarantine(self, keep: set[str]) -> None:
		if not self._mods_dir.is_dir():
			return
		self._quarantine.mkdir(exist_ok=True)
		for child in sorted(self._mods_dir.iterdir()):
			if child.name == QUARANTINE_DIR_NAME or child.name in keep:
				continue
			shutil.move(str(child), self._quarantine / child.name)
			self._moved.append(child.name)

	def restore(self) -> list[str]:
		problems: list[str] = []
		for name in reversed(self._moved):
			stored = self._quarantine / name
			target = self._mods_dir / name
			try:
				if target.exists():
					problems.append(f"cannot restore {name}: {target} exists")
					continue
				shutil.move(str(stored), target)
			except OSError as error:
				problems.append(f"cannot restore {name}: {error}")
		self._moved = [name for name in self._moved if (self._quarantine / name).exists()]
		if not self._moved:
			try:
				self._quarantine.rmdir()
			except OSError:
				pass
		return problems


JsonScalar = str | int | float | bool | None


@dataclass(frozen=True)
class MuteEdit:
	key: str
	old: JsonScalar
	new: JsonScalar


def _is_number(value: object) -> bool:
	return isinstance(value, (int, float)) and not isinstance(value, bool)


def _silenced(value: JsonScalar) -> JsonScalar:
	if isinstance(value, bool):
		return False
	if isinstance(value, (int, float)):
		return type(value)(0)
	return value


def mute_audio_settings(data: object) -> list[MuteEdit]:
	"""Zero every volume/mute entry of a parsed settings document, in place.

	Handles both shapes a WotR settings file uses: a list of {"Name", "Value"}
	entries and plain nested objects. Anything else is left untouched.
	"""
	edits: list[MuteEdit] = []

	def visit(node: object, path: str) -> None:
		if isinstance(node, dict):
			name = node.get("Name", node.get("name"))
			if isinstance(name, str) and VOLUME_KEY.search(name):
				for value_key in ("Value", "value"):
					old = node.get(value_key)
					if _is_number(old) or isinstance(old, bool):
						new = _silenced(old)
						if new != old:
							node[value_key] = new
							edits.append(MuteEdit(f"{path}.{value_key} ({name})", old, new))
				return
			for key, value in node.items():
				if (
					isinstance(key, str)
					and VOLUME_KEY.search(key)
					and (_is_number(value) or isinstance(value, bool))
				):
					new = _silenced(value)
					if new != value:
						node[key] = new
						edits.append(MuteEdit(f"{path}.{key}", value, new))
				elif isinstance(value, (dict, list)):
					visit(value, f"{path}.{key}")
		elif isinstance(node, list):
			for index, item in enumerate(node):
				visit(item, f"{path}[{index}]")

	visit(data, "$")
	return edits


def apply_mute(settings_file: Path, backup: RunBackup) -> dict[str, object]:
	"""Mute the game's settings file before launch; the backup restores it after."""
	section: dict[str, object] = {"file": str(settings_file)}
	if not settings_file.is_file():
		section["status"] = "skipped"
		section["reason"] = "settings file not found"
		return section
	backup.protect(settings_file)
	try:
		data = json.loads(settings_file.read_text(encoding="utf-8-sig"))
	except json.JSONDecodeError as error:
		raise HarnessError(f"The settings file is not JSON: {settings_file}: {error}") from error
	edits = mute_audio_settings(data)
	settings_file.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
	section["status"] = "applied"
	section["edits"] = [{"key": edit.key, "old": edit.old, "new": edit.new} for edit in edits]
	return section


@dataclass(frozen=True)
class DeployedMod:
	name: str
	source: str
	files: int


def deploy_mods(config: HarnessConfig, backup: RunBackup) -> list[DeployedMod]:
	"""Deploy the test assembly and its dependency mods into the game's Mods folder."""
	mods_dir = config.game_dir / "Mods"
	mods_dir.mkdir(parents=True, exist_ok=True)
	deployed: list[DeployedMod] = []
	names: set[str] = set()

	def prepare_target(name: str) -> Path:
		if name in names or name == QUARANTINE_DIR_NAME:
			raise HarnessError(f"Duplicate mod name in this run: {name}")
		names.add(name)
		target = mods_dir / name
		if target.exists():
			backup.stash(target)
		else:
			backup.mark_new(target)
		return target

	target = prepare_target(config.mod_name)
	target.mkdir(parents=True)
	shutil.copy2(config.test_assembly, target / config.test_assembly.name)
	info = config.test_assembly.parent / "Info.json"
	if info.is_file():
		shutil.copy2(info, target / "Info.json")
	else:
		info_manifest: dict[str, str] = {
			"Id": config.mod_name,
			"Name": f"{config.mod_name} (wotr-testing headless run)",
			"Version": "0.0.0.0",
		}
		if config.entry_method:
			info_manifest["EntryMethod"] = config.entry_method
		(target / "Info.json").write_text(
			json.dumps(info_manifest, indent=2) + "\n", encoding="utf-8"
		)
	deployed.append(DeployedMod(config.mod_name, str(config.test_assembly), 2))

	for dependency in config.mods:
		target = prepare_target(dependency.name)
		shutil.copytree(dependency, target)
		files = sum(1 for path in target.rglob("*") if path.is_file())
		deployed.append(DeployedMod(dependency.name, str(dependency), files))
	return deployed


def build_launch_command(config: HarnessConfig, report_path: Path, run_id: str) -> list[str]:
	"""The launch command; Steam's -applaunch passes the arguments so no launch
	options dialog appears, and process tracking survives Steam re-launching the
	game because the game process is found by scanning, not by parentage."""
	contract = [
		f"--wotr-headless-report={report_path}",
		f"--wotr-headless-run-id={run_id}",
	]
	if config.launch_mode == "steam":
		if config.steam_exe is None:
			raise HarnessError(
				"Steam launch mode needs --steam-exe, WOTR_STEAM_EXE or the registry."
			)
		return [str(config.steam_exe), "-applaunch", config.app_id, *contract, *config.unity_args]
	return [str(config.game_dir / config.exe_name), *contract, *config.unity_args]


@dataclass(frozen=True)
class RunResult:
	outcome: str
	exit_code: int
	detail: str
	manifest_path: Path | None


def _utc_now() -> str:
	return datetime.now(UTC).isoformat()


def _wait_for_game(
	config: HarnessConfig,
	gateway: ProcessGateway,
	pre_existing: set[int],
	managed: set[int],
	sleep: Callable[[float], None],
	monotonic: Callable[[], float],
) -> str:
	"""Track the game process this run started until it exits, following Steam
	re-launches: the game is identified by appearing after the launch request,
	never by the launcher process, so a Steam-spawned replacement is followed.
	Every pid seen is added to ``managed`` so cleanup can terminate it.
	Returns "exited", "launch-failed" or "timeout"."""
	grace_deadline = monotonic() + config.launch_grace_seconds
	deadline = monotonic() + config.timeout_seconds
	settle_deadline: float | None = None
	scanned: set[int] = set()
	while True:
		current = {
			process.pid for process in gateway.find_game_processes(config.game_dir, config.exe_name)
		} - pre_existing
		if current:
			scanned |= current
			managed |= current
			settle_deadline = None
		if scanned and not current:
			if settle_deadline is None:
				settle_deadline = monotonic() + config.settle_seconds
			elif monotonic() >= settle_deadline:
				return "exited"
		if not scanned and monotonic() >= grace_deadline:
			return "launch-failed"
		if monotonic() >= deadline:
			return "timeout"
		sleep(config.poll_seconds)


def _terminate_all(
	gateway: ProcessGateway,
	pids: set[int],
	sleep: Callable[[float], None],
	monotonic: Callable[[], float],
) -> list[int]:
	"""Terminate processes this run manages; pre-existing processes never reach it."""
	terminated: list[int] = []
	for pid in sorted(pids):
		if not gateway.is_running(pid):
			continue
		gateway.terminate(pid)
		terminated.append(pid)
	deadline = monotonic() + TERMINATE_WAIT_SECONDS
	remaining = [pid for pid in terminated if gateway.is_running(pid)]
	while remaining and monotonic() < deadline:
		sleep(0.2)
		remaining = [pid for pid in remaining if gateway.is_running(pid)]
	return terminated


def _collect_report(report_path: Path, section: dict[str, object]) -> None:
	section["path"] = str(report_path)
	section["present"] = report_path.is_file()
	if not report_path.is_file():
		raise _RunStop(
			"failed",
			EXIT_FAILED,
			f"the game exited without a test report at {report_path}",
		)
	try:
		data = json.loads(report_path.read_text(encoding="utf-8-sig"))
	except json.JSONDecodeError as error:
		raise _RunStop("failed", EXIT_FAILED, f"the test report is not JSON: {error}") from error
	result = data.get("result") if isinstance(data, dict) else None
	section["result"] = result
	if result != "passed":
		raise _RunStop("failed", EXIT_FAILED, f"the test report result is {result!r}, not 'passed'")


def run_headless(
	config: HarnessConfig,
	gateway: ProcessGateway,
	sleep: Callable[[float], None] = time.sleep,
	monotonic: Callable[[], float] = time.monotonic,
) -> RunResult:
	"""Prepare, launch, wait, collect and clean up; cleanup always runs."""
	run_id = uuid.uuid4().hex[:12]
	config.results_dir.mkdir(parents=True, exist_ok=True)
	report_path = config.results_dir / REPORT_NAME
	manifest_path = config.results_dir / MANIFEST_NAME
	backup_dir = config.results_dir / f"backup-{run_id}"
	backup = RunBackup(backup_dir)
	quarantine = ModQuarantine(config.game_dir / "Mods")
	launch_section: dict[str, object] = {
		"mode": config.launch_mode,
		"unityArgs": list(config.unity_args),
		"steamExe": str(config.steam_exe) if config.steam_exe else None,
	}
	manifest: dict[str, object] = {
		"runId": run_id,
		"startedAt": _utc_now(),
		"game": {
			"dir": str(config.game_dir),
			"version": _game_version(config.game_dir),
			"exeName": config.exe_name,
			"appId": config.app_id,
		},
		"testAssembly": {
			"path": str(config.test_assembly),
			"sha256": _sha256(config.test_assembly),
			"sizeBytes": config.test_assembly.stat().st_size,
			"version": config.test_assembly_version or "unknown",
		},
		"launch": launch_section,
		"timeouts": {
			"timeoutSeconds": config.timeout_seconds,
			"launchGraceSeconds": config.launch_grace_seconds,
		},
	}
	pre_existing: set[int] = set()
	managed: set[int] = set()
	terminated: list[int] = []
	outcome = "failed"
	exit_code = EXIT_FAILED
	detail = ""
	try:
		pre_existing = {
			process.pid for process in gateway.find_game_processes(config.game_dir, config.exe_name)
		}
		if pre_existing and not config.allow_existing_game:
			raise _RunStop(
				"refused",
				EXIT_REFUSED,
				f"game processes already run (pid {sorted(pre_existing)}); close the game "
				"or pass --allow-existing-game (they are never managed by the harness)",
			)
		leftovers = quarantine.restore_leftovers()
		if leftovers:
			raise _RunStop(
				"refused",
				EXIT_REFUSED,
				"a crashed run left quarantined mods behind: " + "; ".join(leftovers),
			)
		if report_path.exists():
			report_path.unlink()
			manifest["staleReportRemoved"] = True
		deployed = deploy_mods(config, backup)
		if not config.keep_other_mods:
			quarantine.quarantine({mod.name for mod in deployed})
		if config.mute and config.settings_file is not None:
			mute_section = apply_mute(config.settings_file, backup)
		else:
			mute_section = {"status": "disabled" if not config.mute else "skipped"}
			mute_section["reason"] = (
				"--no-mute" if not config.mute else "no settings file configured"
			)
		manifest["mute"] = mute_section
		manifest["mods"] = {
			"deployed": [
				{"name": mod.name, "source": mod.source, "files": mod.files} for mod in deployed
			],
			"quarantined": quarantine.moved,
			"otherModsKept": config.keep_other_mods,
		}
		command = build_launch_command(config, report_path, run_id)
		launch_section["command"] = command
		if config.dry_run:
			raise _RunStop("dry-run", EXIT_PASSED, "prepared and restored: " + " ".join(command))
		env = dict(os.environ)
		env["WOTR_HEADLESS_REPORT"] = str(report_path)
		env["WOTR_HEADLESS_RUN_ID"] = run_id
		env["WOTR_HEADLESS_RESULTS"] = str(config.results_dir)
		try:
			launcher_pid = gateway.start(command, env, config.game_dir)
		except OSError as error:
			raise HarnessError(f"the launch failed: {error}") from error
		if config.launch_mode == "direct":
			# The launcher is the game itself; track it before the first scan so an
			# interruption cannot leak it. Under Steam the launcher is steam.exe and
			# the game process is found by scanning instead.
			managed.add(launcher_pid)
		phase = _wait_for_game(config, gateway, pre_existing, managed, sleep, monotonic)
		if phase == "timeout":
			terminated += _terminate_all(gateway, managed, sleep, monotonic)
			raise _RunStop(
				"timeout",
				EXIT_TIMEOUT,
				f"the game did not exit within {config.timeout_seconds:.0f} s "
				f"and was terminated (pid {sorted(managed)})",
			)
		if phase == "launch-failed":
			raise _RunStop(
				"failed",
				EXIT_FAILED,
				f"no {config.exe_name} process appeared within "
				f"{config.launch_grace_seconds:.0f} s of the launch request",
			)
		report_section: dict[str, object] = {}
		manifest["report"] = report_section
		_collect_report(report_path, report_section)
		if config.collect_player_log and config.settings_file is not None:
			player_log = config.settings_file.parent / "Player.log"
			if player_log.is_file():
				shutil.copy2(player_log, config.results_dir / "Player.log")
				manifest["playerLog"] = str(config.results_dir / "Player.log")
		outcome, exit_code = "passed", EXIT_PASSED
		detail = "the game exited and the test report passed"
	except _RunStop as stop:
		outcome, exit_code, detail = stop.outcome, stop.exit_code, stop.detail
	except KeyboardInterrupt:
		outcome, exit_code, detail = "interrupted", EXIT_INTERRUPTED, "interrupted"
	except HarnessError as error:
		outcome, exit_code, detail = "failed", EXIT_FAILED, str(error)
	finally:
		cleanup_problems: list[str] = []
		try:
			still_running = {pid for pid in managed if gateway.is_running(pid)}
		except Exception as error:  # cleanup must continue even if the edge fails
			still_running = set()
			cleanup_problems.append(f"cannot check managed processes: {error}")
		if still_running:
			try:
				terminated += _terminate_all(gateway, still_running, sleep, monotonic)
			except Exception as error:
				cleanup_problems.append(f"cannot terminate managed processes: {error}")
		cleanup_problems += quarantine.restore()
		cleanup_problems += backup.restore()
		if cleanup_problems and exit_code == EXIT_PASSED:
			outcome, exit_code = "failed", EXIT_FAILED
			detail = (
				(detail + "; " if detail else "") + "cleanup failed: " + "; ".join(cleanup_problems)
			)
		elif cleanup_problems:
			detail += "; cleanup problems: " + "; ".join(cleanup_problems)
		manifest["processes"] = {
			"preExisting": sorted(pre_existing),
			"managed": sorted(managed),
			"terminated": sorted(set(terminated)),
		}
		manifest["cleanup"] = {"problems": cleanup_problems, "backupEntries": backup.entries}
		manifest["finishedAt"] = _utc_now()
		manifest["outcome"] = outcome
		manifest["detail"] = detail
		manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
		if not cleanup_problems:
			shutil.rmtree(backup_dir, ignore_errors=True)
	return RunResult(outcome, exit_code, detail, manifest_path)


def main(argv: Sequence[str] | None = None) -> int:
	try:
		config = load_config(parse_args(argv))
	except HarnessError as error:
		print(f"headless-launch: {error}", file=sys.stderr)
		return EXIT_REFUSED
	gateway: ProcessGateway
	if sys.platform == "win32":
		gateway = WindowsProcessGateway()
	elif config.dry_run:
		gateway = NoProcessesGateway()
	else:
		print("headless-launch: launching the game needs Windows.", file=sys.stderr)
		return EXIT_REFUSED
	result = run_headless(config, gateway)
	print(f"headless-launch: {result.outcome} (exit {result.exit_code}): {result.detail}")
	if result.manifest_path is not None:
		print(f"headless-launch: manifest: {result.manifest_path}")
	return result.exit_code


if __name__ == "__main__":
	raise SystemExit(main())
