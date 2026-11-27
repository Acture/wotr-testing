"""Opt-in battle fixture tests. They boot the offline session and need the local game."""

from __future__ import annotations

import json
import os
import re
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "tests/fixtures/BattleTests/WotR.Testing.BattleTests.csproj"
SOURCES = ROOT / "tests/fixtures/BattleTests"
RUNS = ROOT / "artifacts/obj/WotR.Testing.BattleTests/wotr/test-runs"
REPORTS = re.compile(r"WotR reports: (.+)")
FACTS = re.compile(r"\[Fact\]\s+public void (\w+)\(")


def run(
	*arguments: str, scenario: str = "", inputs: Path | None = None
) -> subprocess.CompletedProcess[str]:
	environment = os.environ.copy()
	environment["WOTR_TEST_SCENARIO"] = scenario
	if inputs is not None:
		environment["WOTR_INPUT_ROOT"] = str(inputs)
	return subprocess.run(
		["dotnet", "test", str(PROJECT), "--nologo", *arguments],
		cwd=ROOT,
		env=environment,
		capture_output=True,
		text=True,
		encoding="utf-8",
		errors="replace",
		timeout=900,
	)


def report_directory(result: subprocess.CompletedProcess[str]) -> Path:
	match = REPORTS.search(result.stdout)
	if match is None:
		raise AssertionError("No report directory in the output:\n" + result.stdout + result.stderr)
	return Path(match.group(1).strip())


def classifications(result: subprocess.CompletedProcess[str]) -> dict[str, str]:
	"""Each test's classification in the run's results, by method name."""
	results = RUNS / report_directory(result).name / "wotr-results.xml"
	return {
		test.attrib["name"].rsplit(".", 1)[-1]: test.attrib["classification"]
		for test in ET.parse(results).getroot().findall("Test")
	}


def installation() -> Path:
	"""The WotR installation GamePath.props names, as the build reads it."""
	props = (ROOT / "GamePath.props").read_text(encoding="utf-8-sig")
	match = re.search(r"<WrathInstallDir>([^<]+)</WrathInstallDir>", props)
	if match is None:
		raise AssertionError("GamePath.props names no WrathInstallDir")
	return Path(match.group(1).strip())


def battles(result: subprocess.CompletedProcess[str]) -> dict[str, Any]:
	environment = report_directory(result) / "environment.json"
	report = json.loads(environment.read_text(encoding="utf-8"))
	section = report.get("battles")
	if not isinstance(section, dict):
		raise AssertionError("environment.json has no battles section")
	return section


@unittest.skipUnless(
	os.name == "nt" and os.environ.get("WOTR_TEST_INTEGRATION") == "1",
	"Set WOTR_TEST_INTEGRATION=1 on Windows with local WotR inputs to run battle fixture tests.",
)
class BattleFixtureTests(unittest.TestCase):
	def test_battles_on_both_runtimes(self) -> None:
		for runtime in ("mono", "netfx"):
			with self.subTest(runtime=runtime):
				result = run(f"-p:WotrRuntime={runtime}")
				self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
				# Every fixture test ran and passed; unity-runtime-required skips do not count.
				facts = [
					fact
					for source in sorted(SOURCES.glob("*.cs"))
					for fact in FACTS.findall(source.read_text(encoding="utf-8"))
				]
				self.assertGreater(len(facts), 0)
				self.assertEqual(classifications(result), dict.fromkeys(facts, "passed"))
				section = battles(result)
				self.assertFalse(section["dirty"])
				self.assertGreater(section["builds"], section["battlesStarted"])
				self.assertEqual(section["failedCleanups"], 0)
				# One environment test injects a failure and one runs into the game's surprise-round
				# stall; every other episode step ran.
				self.assertEqual(section["environment"]["failed"], 2)
				self.assertGreater(section["environment"]["ended"], 3)
				# The installation has the area's mechanics scene, so A* takes its settings.
				for navigation in section["navigation"]:
					self.assertTrue(navigation["settings"].startswith("Bundles/"), navigation)

	def test_a_snapshot_gives_battles_the_scene_settings_of_a_star(self) -> None:
		# The snapshot has no scene bundles; the A* settings come from its navigation-settings.json.
		# The fallback test runs without the unit prefabs, which snapshots do not copy either.
		install = installation()
		fallback = ("--filter", "FullyQualifiedName~ACorpulenceFallbackAppliesOnlyWithoutTheBundle")
		with tempfile.TemporaryDirectory(prefix="wotr-snapshot-") as directory:
			snapshot = Path(directory) / "wotr"
			made = subprocess.run(
				[
					"pwsh",
					"-NoProfile",
					"-File",
					str(ROOT / "scripts/New-WotrSnapshot.ps1"),
					"-WrathInstallDir",
					str(install),
					"-Destination",
					str(snapshot),
				],
				cwd=ROOT,
				capture_output=True,
				text=True,
				encoding="utf-8",
				errors="replace",
				timeout=900,
			)
			self.assertEqual(made.returncode, 0, made.stdout + made.stderr)
			manifest = json.loads((snapshot / "manifest.json").read_text(encoding="utf-8-sig"))
			self.assertIn("navigation-settings.json", manifest["files"])
			self.assertFalse(list((snapshot / "Bundles").glob("*.scenes")))

			from_snapshot = run(*fallback, inputs=snapshot)
			self.assertEqual(
				from_snapshot.returncode, 0, from_snapshot.stdout + from_snapshot.stderr
			)
		from_installation = run(*fallback)
		self.assertEqual(
			from_installation.returncode, 0, from_installation.stdout + from_installation.stderr
		)
		[read] = battles(from_snapshot)["navigation"]
		[expected] = battles(from_installation)["navigation"]
		self.assertTrue(
			read["settings"].startswith("navigation-settings.json, read from Bundles/"), read
		)
		self.assertTrue(expected["settings"].startswith("Bundles/"), expected)
		# Same scene bundle, same settings.
		self.assertEqual(
			read["settings"].split("Bundles/", 1)[1], expected["settings"].split("Bundles/", 1)[1]
		)
		self.assertEqual(
			{key: value for key, value in read.items() if key != "settings"},
			{key: value for key, value in expected.items() if key != "settings"},
		)

	def test_residue_marks_the_worker_dirty_and_fails_the_run(self) -> None:
		result = run(
			"--filter",
			"FullyQualifiedName~UntrackedSubscriptionIsResidue",
			scenario="dirty-battle",
		)
		output = result.stdout + result.stderr
		self.assertNotEqual(result.returncode, 0, output)
		self.assertNotIn("[FAIL]", output)
		self.assertIn("Battle worker dirty", output)
		self.assertIn("CountingBuffHandler", output)
		section = battles(result)
		self.assertTrue(section["dirty"])
		self.assertEqual(section["failedCleanups"], 1)

	def test_an_undisposed_battle_marks_the_worker_dirty_and_fails_the_run(self) -> None:
		result = run(
			"--filter",
			"FullyQualifiedName~OnlyOneBattleRunsAtATime",
			scenario="undisposed-battle",
		)
		output = result.stdout + result.stderr
		self.assertNotEqual(result.returncode, 0, output)
		self.assertNotIn("[FAIL]", output)
		self.assertIn("Battle worker dirty", output)
		self.assertIn("never disposed", output)
		section = battles(result)
		self.assertTrue(section["dirty"])
		self.assertEqual(section["undisposedBattle"], "duel")
		self.assertEqual(section["failedCleanups"], 0)
