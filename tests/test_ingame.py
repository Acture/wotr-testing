"""Opt-in integration tests for planned offgame, in-game and fuzz partitions.

Both `dotnet test` runners are covered. The offgame cases need local WotR inputs
(WOTR_TEST_INTEGRATION=1). The in-game cases also start the installed game with the test host
and close it again; they run only with WOTR_TEST_INGAME=1.
"""

from __future__ import annotations

import json
import os
import subprocess
import time
import unittest
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

from test_dotnet_test import ROOT, TRX_NS, DotnetTestHarness

FIXTURE = ROOT / "tests/fixtures/InGameTests/InGameTests.csproj"
OUTPUT = ROOT / "artifacts/bin/InGameTests/Debug/net48"


def game_running() -> bool:
	result = subprocess.run(
		["tasklist", "/FI", "IMAGENAME eq Wrath.exe", "/NH"],
		capture_output=True,
		text=True,
		encoding="utf-8",
		errors="replace",
		check=False,
	)
	return "wrath.exe" in result.stdout.lower()


def process_running(pid: int) -> bool:
	result = subprocess.run(
		["tasklist", "/FI", f"PID eq {pid}", "/FI", "IMAGENAME eq Wrath.exe", "/NH"],
		capture_output=True,
		text=True,
		encoding="utf-8",
		errors="replace",
		check=False,
	)
	return "wrath.exe" in result.stdout.lower()


def settled_host(run: subprocess.Popen[bytes], timeout: float) -> bool:
	"""While `run` runs, a game advertised the host 30 seconds ago and still runs.

	Steam replaces the process a run starts within about 20 seconds, after mods loaded in it.
	"""
	hosts = Path(os.environ["LOCALAPPDATA"]) / "WotR.Testing/hosts"
	since = time.time()
	deadline = time.monotonic() + timeout
	while run.poll() is None and time.monotonic() < deadline:
		for advertisement in hosts.glob("*.json"):
			try:
				written = advertisement.stat().st_mtime
			except OSError:
				continue
			pid = advertisement.stem
			fresh = since <= written <= time.time() - 30
			if fresh and pid.isdigit() and process_running(int(pid)):
				return True
		time.sleep(2)
	return False


def game_gone(timeout: float = 30) -> bool:
	"""An exited game stays listed for a few seconds while other processes (Steam) hold it."""
	deadline = time.monotonic() + timeout
	while game_running():
		if time.monotonic() > deadline:
			return False
		time.sleep(1)
	return True


class PlannedRunHarness(DotnetTestHarness):
	project = FIXTURE

	def setUp(self) -> None:
		self.assertEqual(
			self.build_result.returncode,
			0,
			self.build_result.stdout + self.build_result.stderr,
		)
		# Short: reports nest by project, run and partition; Unity Mono fails near 260 characters.
		output = ROOT / "artifacts/test-results/ingame"
		output.mkdir(parents=True, exist_ok=True)
		self.results = output / ("r " + uuid.uuid4().hex[:8])
		self.results.mkdir()

	def tearDown(self) -> None:
		(OUTPUT / "wotr-test-scenario.json").unlink(missing_ok=True)

	def scenario(self, name: str) -> None:
		# The game does not see the test environment: the fixture reads a file beside the assembly.
		(OUTPUT / "wotr-test-scenario.json").write_text(
			json.dumps({"scenario": name}), encoding="utf-8"
		)

	def run_platform(
		self, *arguments: str, results: Path | None = None, timeout: int = 120
	) -> subprocess.CompletedProcess[str]:
		"""Runs with Microsoft.Testing.Platform, which `dotnet test` selects from global.json."""
		results = results or self.results
		results.mkdir(exist_ok=True)
		(results / "global.json").write_text(
			'{"test": {"runner": "Microsoft.Testing.Platform"}}', encoding="utf-8"
		)
		return subprocess.run(
			[
				"dotnet",
				"test",
				"--project",
				str(self.project),
				"--no-build",
				"--no-restore",
				"-p:WotrTestingPlatform=true",
				"--results-directory",
				str(results / "results"),
				*(["--report-trx"] if "--list-tests" not in arguments else []),
				*arguments,
			],
			cwd=results,
			env=os.environ | {"DOTNET_CLI_UI_LANGUAGE": "en"},
			capture_output=True,
			text=True,
			encoding="utf-8",
			errors="replace",
			timeout=timeout,
		)

	def vstest_command(self, *arguments: str, results: Path | None = None) -> list[str]:
		return [
			"dotnet",
			"test",
			str(self.project),
			"--no-build",
			"--no-restore",
			"-m:1",
			"-nr:false",
			"--logger",
			"trx",
			"--results-directory",
			str((results or self.results) / "results"),
			*arguments,
		]

	def run_vstest(
		self, *arguments: str, results: Path | None = None, timeout: int = 120
	) -> subprocess.CompletedProcess[str]:
		return subprocess.run(
			self.vstest_command(*arguments, results=results),
			cwd=ROOT,
			capture_output=True,
			text=True,
			encoding="utf-8",
			errors="replace",
			timeout=timeout,
		)

	def assert_passed(
		self, result: subprocess.CompletedProcess[str], passed: int, results: Path | None = None
	) -> None:
		self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
		trx = next(((results or self.results) / "results").glob("*.trx"))
		counters = ET.parse(trx).find("t:ResultSummary/t:Counters", TRX_NS)
		assert counters is not None
		self.assertEqual(int(counters.attrib["passed"]), passed)
		self.assertEqual(int(counters.attrib["failed"]), 0)

	def run_report(self, results: Path | None = None) -> dict[str, Any]:
		reports = list(
			((results or self.results) / "results/wotr/InGameTests").glob("*/wotr-run.json")
		)
		self.assertEqual(len(reports), 1, reports)
		return json.loads(reports[0].read_text(encoding="utf-8-sig"))

	@staticmethod
	def names(entries: list[dict[str, Any]]) -> list[str]:
		return [entry["name"] for entry in entries]


class PlannedRunTests(PlannedRunHarness):
	def test_offgame_mode_runs_offgame_partitions_on_both_runners(self) -> None:
		# VSTest runs ordinary offgame tests with its own xUnit adapter, outside WotR partitions.
		for runner, partitions, by_vstest in (
			("vstest", ["offgame/fuzz"], ["offgame/tests"]),
			("platform", ["offgame/tests", "offgame/fuzz"], []),
		):
			with self.subTest(runner=runner):
				results = self.results / runner
				if runner == "vstest":
					result = self.run_vstest("-p:WotrTestMode=offgame", results=results)
				else:
					result = self.run_platform("--mode", "offgame", results=results)
				self.assert_passed(result, 3, results)
				report = self.run_report(results)
				self.assertTrue(report["succeeded"])
				self.assertEqual(self.names(report["partitions"]), partitions)
				self.assertEqual(self.names(report["runByVSTest"]), by_vstest)
				self.assertEqual(self.names(report["excluded"]), ["ingame/tests", "ingame/fuzz"])

	def test_platform_lists_every_declared_case_without_a_game(self) -> None:
		result = self.run_platform("--list-tests", "--mode", "offgame")
		self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
		self.assertIn("Untagged_cases_run_offgame_only", result.stdout)
		self.assertIn("CreateCounter", result.stdout)
		self.assertNotIn("Native_object_survives_a_frame_on_the_main_thread", result.stdout)

	def test_contradictory_requests_fail_before_anything_runs(self) -> None:
		cases = {
			"in-game options for an offgame run": (
				"-p:WotrTestMode=offgame",
				"-p:WotrInGameConnection=attach",
			),
			"a fuzz seed without fuzz targets": ("-p:WotrTestKind=tests", "-p:WotrFuzzSeed=5"),
			"an offgame runtime in the game": ("-p:WotrTestMode=ingame", "-p:WotrRuntime=netfx"),
			"an open game to use for a run that starts its own": ("-p:WotrInGamePid=1234",),
		}
		for name, arguments in cases.items():
			with self.subTest(name):
				result = self.run_vstest(*arguments)
				self.assertNotEqual(result.returncode, 0, result.stdout)
				self.assertIn("[PLAN]", result.stdout + result.stderr)
		# Platform option checks would show only an exit code; these appear as failed nodes.
		for arguments in (
			("--fuzz-seed", "5", "--tests"),
			("--fuzz", "--tests"),
			("--mode", "inGame"),
		):
			with self.subTest(arguments=arguments):
				results = self.results / arguments[1].strip("-")
				result = self.run_platform(*arguments, results=results)
				self.assertNotEqual(result.returncode, 0, result.stdout)
				self.assertIn("[PLAN]", result.stdout + result.stderr)
				# A problem node must not break the TRX report.
				self.assertNotIn("Unhandled exception", result.stdout + result.stderr)
				trx = next((results / "results").glob("*.trx"))
				counters = ET.parse(trx).find("t:ResultSummary/t:Counters", TRX_NS)
				assert counters is not None
				self.assertEqual(int(counters.attrib["failed"]), 1)

	def test_platform_tree_filter_without_matches_fails(self) -> None:
		result = self.run_platform("--mode", "offgame", "--treenode-filter", "/NoSuchAssembly/**")
		self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)

	def test_attach_without_a_running_game_fails(self) -> None:
		if game_running():
			self.skipTest("A game is running; attach would use it.")
		result = self.run_vstest("-p:WotrTestMode=ingame", "-p:WotrInGameConnection=attach")
		self.assertNotEqual(result.returncode, 0, result.stdout)
		self.assertIn("[no-game]", result.stdout + result.stderr)


@unittest.skipUnless(
	os.environ.get("WOTR_TEST_INGAME") == "1",
	"Set WOTR_TEST_INGAME=1 to start the installed game with the test host for in-game runs.",
)
class InGameRunTests(PlannedRunHarness):
	def setUp(self) -> None:
		super().setUp()
		if not game_gone():
			self.skipTest("A game is already running; these tests start and close their own.")

	def test_default_run_starts_its_own_game_refuses_an_open_one_and_closes_it(self) -> None:
		command = self.vstest_command()
		output = self.results / "first.log"
		second = None
		with output.open("wb") as log:
			first = subprocess.Popen(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
			try:
				# A second default run while the first one's game is open must not use that game.
				if settled_host(first, 600):
					second = self.run_vstest(
						"-p:WotrTestMode=ingame", results=self.results / "second"
					)
			finally:
				first.wait(timeout=900)
		text = output.read_text(encoding="utf-8", errors="replace")
		self.assertIsNotNone(second, "The run did not start a game with the host. " + text)
		assert second is not None
		self.assertNotEqual(second.returncode, 0, second.stdout)
		self.assertIn("[game-running]", second.stdout + second.stderr)
		self.assert_passed(subprocess.CompletedProcess(command, first.returncode, text, ""), 10)
		report = self.run_report()
		self.assertEqual(
			self.names(report["partitions"]), ["offgame/fuzz", "ingame/tests", "ingame/fuzz"]
		)
		self.assertEqual(self.names(report["runByVSTest"]), ["offgame/tests"])
		self.assertTrue(
			any("started by this run exited" in line for line in report["log"]), report["log"]
		)
		self.assertTrue(game_gone(), "The game this run started is still running.")

	def test_fuzz_finding_is_saved_and_replays_exactly(self) -> None:
		self.scenario("fuzz-defect")
		found = self.run_platform("--mode", "ingame", "--fuzz", timeout=900)
		self.assertNotEqual(found.returncode, 0, found.stdout)
		replays = list((self.results / "results").rglob("replays/*.json"))
		self.assertEqual(len(replays), 1, found.stdout)
		self.assertEqual(json.loads(replays[0].read_text(encoding="utf-8"))["Mode"], "ingame")

		replayed = self.run_platform(
			"--fuzz-replay", str(replays[0]), results=self.results / "replay", timeout=900
		)
		self.assertNotEqual(replayed.returncode, 0, replayed.stdout)
		self.assertIn("Overdrawn", replayed.stdout)

		(OUTPUT / "wotr-test-scenario.json").unlink()
		fixed = self.results / "fixed"
		self.assert_passed(
			self.run_platform("--fuzz-replay", str(replays[0]), results=fixed, timeout=900),
			1,
			fixed,
		)
		self.assertTrue(game_gone(), "The game a run started is still running.")
