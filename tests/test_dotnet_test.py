"""Opt-in CLI integration tests using the local game's Mono, without loading game rules."""

from __future__ import annotations

import json
import os
import subprocess
import time
import unittest
import uuid
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "tests/fixtures/MonoTests/MonoTests.csproj"
TRX_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


@unittest.skipUnless(
	os.name == "nt" and os.environ.get("WOTR_TEST_INTEGRATION") == "1",
	"Set WOTR_TEST_INTEGRATION=1 on Windows with local WotR inputs to run CLI integration tests.",
)
class DotnetTestIntegrationTests(unittest.TestCase):
	@classmethod
	def setUpClass(cls) -> None:
		result = subprocess.run(
			[
				"dotnet",
				"build",
				str(PROJECT),
				"--no-restore",
				"-m:1",
				"-nr:false",
				"-p:UseSharedCompilation=false",
			],
			cwd=ROOT,
			capture_output=True,
			text=True,
			encoding="utf-8",
			errors="replace",
			timeout=180,
		)
		if result.returncode:
			raise RuntimeError(result.stdout + result.stderr)

	def setUp(self) -> None:
		output = ROOT / "artifacts/test-results/dotnet-test-integration"
		output.mkdir(parents=True, exist_ok=True)
		# Keep diagnostics in ignored artifacts. mkdir inherits workspace ACLs on Windows.
		# Python 3.14's mkdtemp creates a private ACL that isolated test hosts cannot enter.
		self.results = output / ("run with spaces " + uuid.uuid4().hex)
		self.results.mkdir()

	def run_cli(
		self,
		*arguments: str,
		scenario: str = "",
		expected_runtime: str = "unity-mono",
		results: Path | None = None,
		environment_overrides: dict[str, str] | None = None,
		project: Path = PROJECT,
	) -> subprocess.CompletedProcess[str]:
		environment = os.environ.copy()
		environment.update(WOTR_TEST_SCENARIO=scenario, WOTR_TEST_EXPECT_RUNTIME=expected_runtime)
		if environment_overrides:
			environment.update(environment_overrides)
		return subprocess.run(
			[
				"dotnet",
				"test",
				str(project),
				"--no-build",
				"--no-restore",
				"-m:1",
				"-nr:false",
				"--logger",
				"trx",
				"--results-directory",
				str(results or self.results),
				*arguments,
			],
			cwd=ROOT,
			env=environment,
			capture_output=True,
			text=True,
			encoding="utf-8",
			errors="replace",
			timeout=60,
		)

	def assert_success(self, result: subprocess.CompletedProcess[str], passed: int) -> None:
		self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
		report = next(self.results.glob("*.trx"))
		counters = ET.parse(report).find("t:ResultSummary/t:Counters", TRX_NS)
		assert counters is not None
		self.assertEqual(int(counters.attrib["passed"]), passed)
		environment_reports = list(self.results.rglob("environment.json"))
		self.assertEqual(len(environment_reports), 1)
		data = json.loads(environment_reports[0].read_text(encoding="utf-8-sig"))
		self.assertTrue(data["synthetic"])
		self.assertEqual(data["process"]["runtimeKind"], "unity-mono")

	def test_default_mono_theory_rows_native_skip_and_trx(self) -> None:
		self.assert_success(self.run_cli("--filter", "Category!=UnexplainedSkip"), 3)

	def test_list_tests_uses_standard_discovery(self) -> None:
		result = self.run_cli("--list-tests")
		self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
		self.assertIn("UsesSelectedRuntime", result.stdout)
		self.assertIn("DiscoversTheoryRows(value: 1)", result.stdout)
		self.assertFalse(list(self.results.rglob("environment.json")))

	def test_trait_filter_and_no_build(self) -> None:
		self.assert_success(self.run_cli("--filter", "Category=Theory"), 2)

	def test_game_and_test_platform_json_have_separate_identities(self) -> None:
		self.assert_success(
			self.run_cli("--filter", "Category=Runtime", scenario="json-isolation"), 1
		)

	def test_user_settings_keep_filter_and_environment_variables(self) -> None:
		self.assert_success(
			self.run_cli("--settings", str(PROJECT.parent / "custom.runsettings")), 1
		)

	def test_settings_input_root_is_used_by_the_mono_host(self) -> None:
		settings = self.results / "input-root.runsettings"
		missing = str(self.results / "missing game")
		root = ET.Element("RunSettings")
		config = ET.SubElement(root, "RunConfiguration")
		variables = ET.SubElement(config, "EnvironmentVariables")
		ET.SubElement(variables, "WOTR_INPUT_ROOT").text = missing
		ET.ElementTree(root).write(settings, encoding="utf-8", xml_declaration=True)
		result = self.run_cli("--filter", "Category=Runtime", "--settings", str(settings))
		self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
		self.assertIn("[ENV_MISSING]", result.stdout + result.stderr)
		self.assertIn(missing, result.stdout + result.stderr)

	def test_empty_settings_input_root_clears_the_parent_variable(self) -> None:
		settings = self.results / "empty-root.runsettings"
		settings.write_text(
			"<RunSettings><RunConfiguration><EnvironmentVariables>"
			"<WOTR_INPUT_ROOT/></EnvironmentVariables></RunConfiguration></RunSettings>"
		)
		inputs = ROOT / "artifacts/bin/MonoTests/Debug/net48/wotr-offline-inputs.json"
		input_root = json.loads(inputs.read_text(encoding="utf-8-sig"))["inputRoot"]
		self.assert_success(
			self.run_cli(
				"--filter",
				"Category=Runtime",
				"--settings",
				str(settings),
				f"-p:WotrInputRoot={input_root}",
				environment_overrides={"WOTR_INPUT_ROOT": str(self.results / "wrong parent")},
			),
			1,
		)

	def test_netfx_is_an_explicit_runtime(self) -> None:
		result = self.run_cli(
			"-p:WotrRuntime=netfx",
			"--filter",
			"Category=Runtime",
			expected_runtime="netfx",
		)
		self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
		report = next(self.results.rglob("environment.json"))
		self.assertEqual(json.loads(report.read_text())["process"]["runtimeKind"], "netfx")

	def test_assertion_failure_returns_nonzero(self) -> None:
		result = self.run_cli("--filter", "Category=Runtime", scenario="failure")
		self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
		counters = ET.parse(next(self.results.glob("*.trx"))).find(
			"t:ResultSummary/t:Counters", TRX_NS
		)
		assert counters is not None
		self.assertEqual(int(counters.attrib["failed"]), 1)

	def test_missing_environment_report_cannot_reuse_an_earlier_report(self) -> None:
		self.assert_success(self.run_cli("--filter", "Category=Runtime"), 1)
		result = self.run_cli("--filter", "Category=Runtime", scenario="missing-report")
		self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
		self.assertIn("Missing report: environment.json", result.stdout)

	def test_zero_tests_fails(self) -> None:
		result = self.run_cli("--filter", "FullyQualifiedName~DoesNotExist")
		self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)

	def test_missing_test_sdk_does_not_succeed_without_running_tests(self) -> None:
		project = self.results / "NoTestSdk.csproj"
		root = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
		properties = ET.SubElement(root, "PropertyGroup")
		ET.SubElement(properties, "TargetFramework").text = "net48"
		ET.SubElement(
			root,
			"Import",
			Project=str(ROOT / "src/WotR.Testing.Offline/build/WotR.Testing.Offline.targets"),
		)
		ET.ElementTree(root).write(project, encoding="utf-8", xml_declaration=True)
		result = self.run_cli(project=project)
		self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
		self.assertIn("[TEST_PROJECT_REQUIRED]", result.stdout + result.stderr)

	def test_all_native_skips_fails(self) -> None:
		result = self.run_cli("--filter", "FullyQualifiedName~NativeBoundaryIsSkipped")
		self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
		self.assertIn("Too few passed tests", result.stdout)

	def test_unexplained_skip_fails_even_with_passing_tests(self) -> None:
		result = self.run_cli()
		self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
		self.assertIn("skipped-without-reason", result.stdout)

	def test_minimum_passed_count_is_enforced(self) -> None:
		result = self.run_cli("--filter", "Category=Theory", "-p:WotrMinimumPassed=3")
		self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
		self.assertIn("Too few passed tests", result.stdout)

	def test_abrupt_host_exit_fails(self) -> None:
		result = self.run_cli("--filter", "Category=Runtime", scenario="crash")
		self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)

	def test_timeout_fails_without_waiting_for_the_default_timeout(self) -> None:
		started = time.monotonic()
		result = self.run_cli(
			"--filter", "Category=Runtime", "-p:WotrTestTimeoutSeconds=2", scenario="hang"
		)
		self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
		self.assertLess(time.monotonic() - started, 30)

	def test_report_runtime_is_checked(self) -> None:
		result = self.run_cli("--filter", "Category=Runtime", scenario="wrong-report-runtime")
		self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
		self.assertIn("Runtime mismatch", result.stdout)

	def test_malformed_report_fails(self) -> None:
		result = self.run_cli("--filter", "Category=Runtime", scenario="invalid-report")
		self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)

	def test_concurrent_runs_keep_reports_separate(self) -> None:
		def execute(name: str) -> subprocess.CompletedProcess[str]:
			return self.run_cli("--filter", "Category=Runtime", results=self.results / name)

		with ThreadPoolExecutor(max_workers=2) as pool:
			results = list(pool.map(execute, ("first", "second")))
		for result in results:
			self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
		self.assertEqual(len(list(self.results.rglob("environment.json"))), 2)


if __name__ == "__main__":
	unittest.main()
