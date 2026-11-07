"""Opt-in battle fixture tests. They boot the offline session and need the local game."""

from __future__ import annotations

import json
import os
import re
import subprocess
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "tests/fixtures/BattleTests/WotR.Testing.BattleTests.csproj"
SOURCE = ROOT / "tests/fixtures/BattleTests/BattleTests.cs"
RUNS = ROOT / "artifacts/obj/WotR.Testing.BattleTests/wotr/test-runs"
REPORTS = re.compile(r"WotR reports: (.+)")
FACTS = re.compile(r"\[Fact\]\s+public void (\w+)\(")


def run(*arguments: str, scenario: str = "") -> subprocess.CompletedProcess[str]:
	environment = os.environ.copy()
	environment["WOTR_TEST_SCENARIO"] = scenario
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
				facts = FACTS.findall(SOURCE.read_text(encoding="utf-8"))
				self.assertGreater(len(facts), 0)
				self.assertEqual(classifications(result), dict.fromkeys(facts, "passed"))
				section = battles(result)
				self.assertFalse(section["dirty"])
				self.assertGreater(section["builds"], section["battlesStarted"])
				self.assertEqual(section["failedCleanups"], 0)

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
