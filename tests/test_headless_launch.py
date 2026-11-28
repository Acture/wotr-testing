"""Offline tests for the headless launch harness: fakes replace processes and Steam."""

from __future__ import annotations

import contextlib
import dataclasses
import hashlib
import io
import json
import tempfile
import unittest
from collections.abc import Callable
from pathlib import Path

import headless_launch as hl


class FakeGateway:
	"""Scripted process edge: after start, each process scan runs the next step."""

	def __init__(self, game_dir: Path, exe_name: str = "Wrath.exe") -> None:
		self.game_dir = game_dir
		self.exe_name = exe_name
		self.processes: dict[int, hl.GameProcess] = {}
		self.started: list[tuple[list[str], dict[str, str], Path]] = []
		self.terminated: list[int] = []
		self.steps: list[Callable[[], None]] = []
		self.on_start: Callable[[int], None] | None = None
		self.start_error: Exception | None = None
		self._next_pid = 1000

	def find_game_processes(self, game_dir: Path, exe_name: str) -> list[hl.GameProcess]:
		if self.started and self.steps:
			self.steps.pop(0)()
		return list(self.processes.values())

	def start(self, command: list[str], env: dict[str, str], cwd: Path) -> int:
		if self.start_error is not None:
			raise self.start_error
		self.started.append((command, env, cwd))
		pid = self._next_pid
		self._next_pid += 1
		if self.on_start is not None:
			self.on_start(pid)
		return pid

	def is_running(self, pid: int) -> bool:
		return pid in self.processes

	def terminate(self, pid: int) -> None:
		self.terminated.append(pid)
		self.processes.pop(pid, None)

	def appear(self, pid: int) -> None:
		self.processes[pid] = hl.GameProcess(pid, str(self.game_dir / self.exe_name))

	def vanish(self, pid: int) -> None:
		self.processes.pop(pid, None)


def write_report(path: Path, result: str) -> None:
	path.parent.mkdir(parents=True, exist_ok=True)
	path.write_text(json.dumps({"result": result, "tests": {"passed": 1}}), encoding="utf-8")


class HeadlessRunTests(unittest.TestCase):
	def setUp(self) -> None:
		self._tmp = tempfile.TemporaryDirectory()
		self.addCleanup(self._tmp.cleanup)
		self.tmp = Path(self._tmp.name)
		self.game = self.tmp / "game"
		streaming = self.game / "Wrath_Data" / "StreamingAssets"
		streaming.mkdir(parents=True)
		(streaming / "Version.info").write_text("2.7.0\n", encoding="utf-8")
		(self.game / "Wrath.exe").write_bytes(b"MZ")
		other = self.game / "Mods" / "OtherMod"
		other.mkdir(parents=True)
		(other / "Info.json").write_text('{"Id": "OtherMod"}', encoding="utf-8")
		self.settings = self.tmp / "settings" / "Settings.json"
		self.settings.parent.mkdir(parents=True)
		self.settings_original = json.dumps(
			{
				"Settings": [
					{"Name": "VolumeMaster", "Value": 0.8},
					{"Name": "MuteInBackground", "Value": True},
					{"Name": "Language", "Value": "enGB"},
				]
			}
		)
		self.settings.write_text(self.settings_original, encoding="utf-8")
		self.assembly = self.tmp / "build" / "MyMod.InGameTests.dll"
		self.assembly.parent.mkdir(parents=True)
		self.assembly.write_bytes(b"fake-assembly")
		self.dependency = self.tmp / "deps" / "DepMod"
		self.dependency.mkdir(parents=True)
		(self.dependency / "Info.json").write_text('{"Id": "DepMod"}', encoding="utf-8")
		(self.dependency / "DepMod.dll").write_bytes(b"dep")
		self.results = self.tmp / "results"
		self.report = self.results / hl.REPORT_NAME

	def make_config(self, **overrides: object) -> hl.HarnessConfig:
		base = hl.HarnessConfig(
			game_dir=self.game,
			test_assembly=self.assembly,
			mods=(self.dependency,),
			results_dir=self.results,
			timeout_seconds=5.0,
			launch_mode="direct",
			mod_name="MyMod.InGameTests",
			settings_file=self.settings,
			poll_seconds=0.01,
			launch_grace_seconds=2.0,
			settle_seconds=0.05,
		)
		return dataclasses.replace(base, **overrides)

	def make_gateway(self) -> FakeGateway:
		return FakeGateway(self.game)

	def assert_restored(self, test_mod_preexisting: bool = False) -> None:
		self.assertEqual(self.settings.read_text(encoding="utf-8"), self.settings_original)
		mods = self.game / "Mods"
		self.assertEqual((mods / "OtherMod" / "Info.json").read_text(), '{"Id": "OtherMod"}')
		if not test_mod_preexisting:
			self.assertFalse((mods / "MyMod.InGameTests").exists())
		self.assertFalse((mods / "DepMod").exists())
		self.assertFalse((mods / hl.QUARANTINE_DIR_NAME).exists())

	def test_successful_run_deploys_mutes_reports_and_restores(self) -> None:
		pre_existing = self.game / "Mods" / "MyMod.InGameTests"
		pre_existing.mkdir(parents=True)
		(pre_existing / "Info.json").write_text('{"Id": "Sentinel"}', encoding="utf-8")
		gateway = self.make_gateway()
		gateway.on_start = gateway.appear

		def finish() -> None:
			write_report(self.report, "passed")
			gateway.vanish(1000)

		gateway.steps = [lambda: None, finish]
		result = hl.run_headless(self.make_config(), gateway)
		self.assertEqual(result.outcome, "passed", result.detail)
		self.assertEqual(result.exit_code, hl.EXIT_PASSED)
		command, env, cwd = gateway.started[0]
		self.assertEqual(cwd, self.game)
		self.assertEqual(command[0], str(self.game / "Wrath.exe"))
		self.assertIn(f"--wotr-headless-report={self.report}", command)
		self.assertEqual(env["WOTR_HEADLESS_REPORT"], str(self.report))
		self.assertEqual(env["WOTR_HEADLESS_RUN_ID"], env["WOTR_HEADLESS_RUN_ID"])
		self.assert_restored(test_mod_preexisting=True)
		self.assertEqual(
			(pre_existing / "Info.json").read_text(encoding="utf-8"), '{"Id": "Sentinel"}'
		)
		manifest = json.loads((self.results / hl.MANIFEST_NAME).read_text(encoding="utf-8"))
		self.assertEqual(manifest["game"]["version"], "2.7.0")
		self.assertEqual(
			manifest["testAssembly"]["sha256"],
			hashlib.sha256(b"fake-assembly").hexdigest(),
		)
		self.assertEqual(manifest["launch"]["mode"], "direct")
		self.assertEqual(manifest["report"]["result"], "passed")
		self.assertEqual(manifest["cleanup"]["problems"], [])
		self.assertEqual(manifest["mute"]["status"], "applied")
		mute_keys = [edit["key"] for edit in manifest["mute"]["edits"]]
		self.assertTrue(any("VolumeMaster" in key for key in mute_keys))
		self.assertTrue(any("MuteInBackground" in key for key in mute_keys))
		self.assertFalse(any("Language" in key for key in mute_keys))

	def test_steam_launch_uses_applaunch_and_follows_the_relaunched_process(self) -> None:
		steam = self.tmp / "steam.exe"
		steam.write_bytes(b"MZ")
		gateway = self.make_gateway()

		def relaunch() -> None:
			gateway.vanish(2000)
			gateway.appear(2001)

		def finish() -> None:
			write_report(self.report, "passed")
			gateway.vanish(2001)

		gateway.steps = [lambda: gateway.appear(2000), relaunch, finish]
		config = self.make_config(launch_mode="steam", steam_exe=steam)
		result = hl.run_headless(config, gateway)
		self.assertEqual(result.outcome, "passed", result.detail)
		command = gateway.started[0][0]
		self.assertEqual(command[:3], [str(steam), "-applaunch", "2201390"])
		self.assertIn("-nographics", command)
		manifest = json.loads((self.results / hl.MANIFEST_NAME).read_text(encoding="utf-8"))
		self.assertEqual(manifest["processes"]["managed"], [2000, 2001])
		self.assertEqual(manifest["launch"]["mode"], "steam")
		self.assert_restored()

	def test_missing_report_after_exit_fails(self) -> None:
		gateway = self.make_gateway()
		gateway.on_start = gateway.appear
		gateway.steps = [lambda: None, lambda: gateway.vanish(1000)]
		result = hl.run_headless(self.make_config(), gateway)
		self.assertEqual(result.outcome, "failed")
		self.assertEqual(result.exit_code, hl.EXIT_FAILED)
		self.assertIn("without a test report", result.detail)
		manifest = json.loads((self.results / hl.MANIFEST_NAME).read_text(encoding="utf-8"))
		self.assertFalse(manifest["report"]["present"])
		self.assert_restored()

	def test_stale_report_cannot_satisfy_the_run(self) -> None:
		write_report(self.report, "passed")
		gateway = self.make_gateway()
		gateway.on_start = gateway.appear
		gateway.steps = [lambda: None, lambda: gateway.vanish(1000)]
		result = hl.run_headless(self.make_config(), gateway)
		self.assertEqual(result.exit_code, hl.EXIT_FAILED)
		manifest = json.loads((self.results / hl.MANIFEST_NAME).read_text(encoding="utf-8"))
		self.assertTrue(manifest["staleReportRemoved"])

	def test_failed_report_result_fails_the_run(self) -> None:
		gateway = self.make_gateway()
		gateway.on_start = gateway.appear

		def finish() -> None:
			write_report(self.report, "failed")
			gateway.vanish(1000)

		gateway.steps = [lambda: None, finish]
		result = hl.run_headless(self.make_config(), gateway)
		self.assertEqual(result.exit_code, hl.EXIT_FAILED)
		self.assertIn("'failed'", result.detail)
		self.assert_restored()

	def test_timeout_terminates_only_processes_the_run_started(self) -> None:
		gateway = self.make_gateway()
		gateway.appear(50)
		gateway.on_start = gateway.appear
		config = self.make_config(allow_existing_game=True, timeout_seconds=0.2)
		result = hl.run_headless(config, gateway)
		self.assertEqual(result.outcome, "timeout")
		self.assertEqual(result.exit_code, hl.EXIT_TIMEOUT)
		self.assertEqual(gateway.terminated, [1000])
		self.assertIn(50, gateway.processes)
		self.assert_restored()

	def test_pre_existing_game_process_refuses_the_run_by_default(self) -> None:
		gateway = self.make_gateway()
		gateway.appear(50)
		result = hl.run_headless(self.make_config(), gateway)
		self.assertEqual(result.outcome, "refused")
		self.assertEqual(result.exit_code, hl.EXIT_REFUSED)
		self.assertEqual(gateway.started, [])
		self.assertEqual(gateway.terminated, [])
		self.assertEqual(self.settings.read_text(encoding="utf-8"), self.settings_original)
		self.assertFalse((self.game / "Mods" / "MyMod.InGameTests").exists())

	def test_launch_failure_still_restores_everything(self) -> None:
		for error in (hl.HarnessError("steam.exe missing"), OSError("not a valid executable")):
			with self.subTest(error=error):
				gateway = self.make_gateway()
				gateway.start_error = error
				result = hl.run_headless(self.make_config(), gateway)
				self.assertEqual(result.outcome, "failed")
				self.assertEqual(result.exit_code, hl.EXIT_FAILED)
				self.assertIn(str(error), result.detail)
				self.assert_restored()

	def test_keyboard_interrupt_still_restores_everything(self) -> None:
		gateway = self.make_gateway()
		gateway.on_start = gateway.appear

		def interrupt() -> None:
			raise KeyboardInterrupt

		gateway.steps = [interrupt]
		result = hl.run_headless(self.make_config(), gateway)
		self.assertEqual(result.outcome, "interrupted")
		self.assertEqual(result.exit_code, hl.EXIT_INTERRUPTED)
		self.assertEqual(gateway.terminated, [1000])
		self.assert_restored()

	def test_game_that_never_appears_fails_after_the_launch_grace(self) -> None:
		gateway = self.make_gateway()
		config = self.make_config(launch_grace_seconds=0.1, timeout_seconds=5.0)
		result = hl.run_headless(config, gateway)
		self.assertEqual(result.outcome, "failed")
		self.assertIn("no Wrath.exe process appeared", result.detail)
		self.assert_restored()

	def test_dry_run_prepares_and_restores_without_launching(self) -> None:
		gateway = self.make_gateway()
		result = hl.run_headless(self.make_config(dry_run=True), gateway)
		self.assertEqual(result.outcome, "dry-run")
		self.assertEqual(result.exit_code, hl.EXIT_PASSED)
		self.assertEqual(gateway.started, [])
		self.assertIn("Wrath.exe", result.detail)
		self.assert_restored()
		manifest = json.loads((self.results / hl.MANIFEST_NAME).read_text(encoding="utf-8"))
		self.assertEqual(manifest["outcome"], "dry-run")
		self.assertIn("command", manifest["launch"])

	def test_crashed_run_leftovers_are_restored_before_the_run(self) -> None:
		quarantine = self.game / "Mods" / hl.QUARANTINE_DIR_NAME / "CrashedMod"
		quarantine.mkdir(parents=True)
		(quarantine / "Info.json").write_text('{"Id": "CrashedMod"}', encoding="utf-8")
		gateway = self.make_gateway()
		gateway.on_start = gateway.appear

		def finish() -> None:
			write_report(self.report, "passed")
			gateway.vanish(1000)

		gateway.steps = [lambda: None, finish]
		result = hl.run_headless(self.make_config(), gateway)
		self.assertEqual(result.outcome, "passed", result.detail)
		mods = self.game / "Mods"
		self.assertEqual(
			(mods / "CrashedMod" / "Info.json").read_text(encoding="utf-8"),
			'{"Id": "CrashedMod"}',
		)
		self.assertFalse((mods / hl.QUARANTINE_DIR_NAME).exists())

	def test_no_mute_and_keep_other_mods_flags(self) -> None:
		gateway = self.make_gateway()
		gateway.on_start = gateway.appear

		def finish() -> None:
			write_report(self.report, "passed")
			gateway.vanish(1000)

		gateway.steps = [lambda: None, finish]
		config = self.make_config(mute=False, keep_other_mods=True)
		result = hl.run_headless(config, gateway)
		self.assertEqual(result.outcome, "passed", result.detail)
		manifest = json.loads((self.results / hl.MANIFEST_NAME).read_text(encoding="utf-8"))
		self.assertEqual(manifest["mute"]["status"], "disabled")
		self.assertEqual(manifest["mods"]["quarantined"], [])
		self.assert_restored()


class MuteTests(unittest.TestCase):
	def test_mute_list_shape_zeroes_volumes_and_keeps_other_entries(self) -> None:
		data = {
			"Settings": [
				{"Name": "VolumeMaster", "Value": 1.0},
				{"Name": "VolumeMusic", "Value": 7},
				{"Name": "MuteInBackground", "Value": True},
				{"Name": "Language", "Value": "enGB"},
				{"Name": "VolumeLabel", "Value": "loud"},
			]
		}
		edits = hl.mute_audio_settings(data)
		settings = data["Settings"]
		self.assertEqual(settings[0]["Value"], 0.0)
		self.assertEqual(settings[1]["Value"], 0)
		self.assertIs(settings[2]["Value"], False)
		self.assertEqual(settings[3]["Value"], "enGB")
		self.assertEqual(settings[4]["Value"], "loud")
		self.assertEqual(len(edits), 3)

	def test_mute_nested_shape_zeroes_matching_keys_recursively(self) -> None:
		data = {"audio": {"masterVolume": 0.9, "muted": True}, "video": {"brightness": 1.0}}
		edits = hl.mute_audio_settings(data)
		self.assertEqual(data["audio"]["masterVolume"], 0.0)
		self.assertIs(data["audio"]["muted"], False)
		self.assertEqual(data["video"]["brightness"], 1.0)
		self.assertEqual(len(edits), 2)

	def test_apply_mute_skips_a_missing_settings_file(self) -> None:
		with tempfile.TemporaryDirectory() as tmp:
			section = hl.apply_mute(Path(tmp) / "Settings.json", hl.RunBackup(Path(tmp) / "b"))
			self.assertEqual(section["status"], "skipped")

	def test_apply_mute_rejects_a_non_json_settings_file(self) -> None:
		with tempfile.TemporaryDirectory() as tmp:
			settings = Path(tmp) / "Settings.json"
			settings.write_text("not json", encoding="utf-8")
			with self.assertRaises(hl.HarnessError):
				hl.apply_mute(settings, hl.RunBackup(Path(tmp) / "b"))


class BackupTests(unittest.TestCase):
	def test_restore_roundtrip_removes_new_and_restores_stashed(self) -> None:
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp)
			original = root / "keep.txt"
			original.write_text("original", encoding="utf-8")
			backup = hl.RunBackup(root / "backup")
			backup.stash(original)
			original.write_text("replacement", encoding="utf-8")
			created = root / "created.txt"
			created.write_text("new", encoding="utf-8")
			backup.mark_new(created)
			self.assertEqual(backup.restore(), [])
			self.assertEqual(original.read_text(encoding="utf-8"), "original")
			self.assertFalse(created.exists())
			self.assertEqual(backup.restore(), [])

	def test_restore_reports_tampered_backups(self) -> None:
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp)
			original = root / "keep.txt"
			original.write_text("original", encoding="utf-8")
			backup = hl.RunBackup(root / "backup")
			backup.protect(original)
			stored = next((root / "backup").iterdir())
			stored.write_text("tampered", encoding="utf-8")
			problems = backup.restore()
			self.assertEqual(len(problems), 1)
			self.assertIn("does not match", problems[0])


class CommandAndConfigTests(unittest.TestCase):
	def setUp(self) -> None:
		self._tmp = tempfile.TemporaryDirectory()
		self.addCleanup(self._tmp.cleanup)
		self.tmp = Path(self._tmp.name)
		self.game = self.tmp / "game"
		streaming = self.game / "Wrath_Data" / "StreamingAssets"
		streaming.mkdir(parents=True)
		(streaming / "Version.info").write_text("2.7.0\n", encoding="utf-8")
		(self.game / "Wrath.exe").write_bytes(b"MZ")
		self.assembly = self.tmp / "MyMod.InGameTests.dll"
		self.assembly.write_bytes(b"fake-assembly")

	def parse(self, *extra: str, environ: dict[str, str] | None = None) -> hl.HarnessConfig:
		ns = hl.parse_args(
			[
				"--test-assembly",
				str(self.assembly),
				"--results-dir",
				str(self.tmp / "results"),
				*extra,
			]
		)
		return hl.load_config(ns, repo_root=self.tmp, environ=environ or {})

	def test_game_dir_comes_from_the_argument(self) -> None:
		config = self.parse("--game-dir", str(self.game), "--launch-mode", "direct")
		self.assertEqual(config.game_dir, self.game)
		self.assertEqual(config.mod_name, "MyMod.InGameTests")
		self.assertEqual(config.unity_args, ("-nographics",))

	def test_game_dir_falls_back_to_the_environment(self) -> None:
		config = self.parse("--launch-mode", "direct", environ={"WOTR_GAME_DIR": str(self.game)})
		self.assertEqual(config.game_dir, self.game)

	def test_game_dir_falls_back_to_gamepath_props(self) -> None:
		(self.tmp / "GamePath.props").write_text(
			"<Project><PropertyGroup>"
			f"<WrathInstallDir>{self.game}</WrathInstallDir>"
			"</PropertyGroup></Project>",
			encoding="utf-8",
		)
		config = self.parse("--launch-mode", "direct")
		self.assertEqual(config.game_dir, self.game)

	def test_missing_game_dir_is_refused(self) -> None:
		with self.assertRaises(hl.HarnessError):
			self.parse("--launch-mode", "direct")

	def test_results_dir_inside_mods_is_refused(self) -> None:
		with self.assertRaises(hl.HarnessError):
			self.parse(
				"--game-dir",
				str(self.game),
				"--launch-mode",
				"direct",
				"--results-dir",
				str(self.game / "Mods" / "results"),
			)

	def test_steam_mode_requires_a_steam_exe(self) -> None:
		with self.assertRaises(hl.HarnessError):
			self.parse("--game-dir", str(self.game), "--steam-exe", str(self.tmp / "none.exe"))
		steam = self.tmp / "steam.exe"
		steam.write_bytes(b"MZ")
		config = self.parse("--game-dir", str(self.game), "--steam-exe", str(steam))
		command = hl.build_launch_command(config, self.tmp / "r" / "report.json", "abc")
		self.assertEqual(command[:3], [str(steam), "-applaunch", "2201390"])
		self.assertIn("--wotr-headless-report=" + str(self.tmp / "r" / "report.json"), command)
		self.assertIn("--wotr-headless-run-id=abc", command)

	def test_custom_unity_args_replace_the_default(self) -> None:
		config = self.parse(
			"--game-dir",
			str(self.game),
			"--launch-mode",
			"direct",
			"--unity-arg=-batchmode",
			"--unity-arg=-nographics",
		)
		command = hl.build_launch_command(config, self.tmp / "r" / "report.json", "abc")
		self.assertEqual(command[-2:], ["-batchmode", "-nographics"])

	def test_main_dry_run_succeeds_without_windows(self) -> None:
		with (
			contextlib.redirect_stdout(io.StringIO()),
			contextlib.redirect_stderr(io.StringIO()),
		):
			exit_code = hl.main(
				[
					"--game-dir",
					str(self.game),
					"--launch-mode",
					"direct",
					"--test-assembly",
					str(self.assembly),
					"--results-dir",
					str(self.tmp / "results"),
					"--settings-file",
					str(self.tmp / "none.json"),
					"--dry-run",
				]
			)
		self.assertEqual(exit_code, hl.EXIT_PASSED)

	def test_main_refuses_a_missing_game_dir(self) -> None:
		with contextlib.redirect_stderr(io.StringIO()):
			exit_code = hl.main(
				[
					"--game-dir",
					str(self.tmp / "none"),
					"--test-assembly",
					str(self.assembly),
					"--results-dir",
					str(self.tmp / "results"),
				]
			)
		self.assertEqual(exit_code, hl.EXIT_REFUSED)


if __name__ == "__main__":
	unittest.main()
