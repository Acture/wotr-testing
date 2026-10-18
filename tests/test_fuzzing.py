"""Native FsCheck property discovery and generated samples on the real test runtimes."""

from __future__ import annotations

import xml.etree.ElementTree as ET
from pathlib import Path

from test_dotnet_test import ROOT, TRX_NS, DotnetTestHarness


class FsCheckIntegrationTests(DotnetTestHarness):
	project = ROOT / "tests/fixtures/FuzzTests/FuzzTests.csproj"

	def test_automatic_interface_fuzzer_on_both_runtimes(self) -> None:
		for runtime in ("mono", "netfx"):
			with self.subTest(runtime=runtime):
				results = self.results / runtime
				result = self.run_cli(
					"--filter",
					"FullyQualifiedName~InterfaceTests",
					f"-p:WotrRuntime={runtime}",
					expected_runtime="unity-mono" if runtime == "mono" else "netfx",
					results=results,
				)
				self.assert_success(
					result,
					3,
					expected_runtime="unity-mono" if runtime == "mono" else "netfx",
					results=results,
				)

	def test_property_executes_generated_samples(self) -> None:
		self.check_property("AddsCommutatively")

	def test_strings_and_arrays_can_be_generated(self) -> None:
		self.check_property("ReversesTwice")

	def test_custom_generator_stays_within_its_domain(self) -> None:
		self.check_property("UsesConstrainedGenerator")

	def check_property(self, name: str) -> None:
		for runtime in ("unity-mono", "netfx"):
			with self.subTest(runtime=runtime):
				results = self.results / runtime
				result = self.run_cli(
					"--filter",
					f"FullyQualifiedName~{name}",
					f"-p:WotrRuntime={'mono' if runtime == 'unity-mono' else 'netfx'}",
					expected_runtime=runtime,
					results=results,
				)
				self.assert_success(result, 1, expected_runtime=runtime, results=results)
				report = ET.parse(next(results.glob("*.trx")))
				self.assertIn("passed 200 tests", ET.tostring(report.getroot(), encoding="unicode"))

	def test_properties_use_native_discovery(self) -> None:
		result = self.run_cli("--list-tests")
		self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
		self.assertIn("AddsCommutatively", result.stdout)
		self.assertIn("UsesConstrainedGenerator", result.stdout)
		self.assertFalse(list(self.results.rglob("environment.json")))

	def failure_message(self, results: Path, expected: str) -> str:
		report = ET.parse(next(results.glob("*.trx")))
		counters = report.find("t:ResultSummary/t:Counters", TRX_NS)
		assert counters is not None
		self.assertEqual(int(counters.attrib["failed"]), 1)
		message = report.findtext(".//t:ErrorInfo/t:Message", namespaces=TRX_NS) or ""
		self.assertIn(expected, message)
		return message

	def test_counterexample_shrinks_and_replays_on_both_runtimes(self) -> None:
		for runtime in ("mono", "netfx"):
			messages: list[str] = []
			for attempt in ("first", "replay"):
				with self.subTest(runtime=runtime, attempt=attempt):
					results = self.results / runtime / attempt
					result = self.run_cli(
						"--filter",
						"FullyQualifiedName~ShrinksCounterexample",
						f"-p:WotrRuntime={runtime}",
						scenario="fuzz-failure",
						results=results,
					)
					self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
					message = self.failure_message(results, "Falsifiable")
					self.assertRegex(message, r"Shrunk:\s+PositiveInt 10")
					self.assertIn("123,457", message)
					messages.append(message)
			self.assertEqual(len(messages), 2)
			self.assertEqual(messages[0], messages[1])

	def test_exhausted_preconditions_fail(self) -> None:
		for runtime in ("mono", "netfx"):
			with self.subTest(runtime=runtime):
				results = self.results / runtime
				result = self.run_cli(
					"--filter",
					"FullyQualifiedName~RequiresAcceptedSamples",
					f"-p:WotrRuntime={runtime}",
					scenario="fuzz-exhaustion",
					results=results,
				)
				self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
				self.failure_message(results, "Arguments exhausted")

	def test_environment_error_marker_survives_fscheck(self) -> None:
		result = self.run_cli(
			"--filter",
			"FullyQualifiedName~PreservesEnvironmentError",
			scenario="fuzz-environment",
		)
		self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
		self.failure_message(self.results, "[ENV_MISSING]")

	def test_native_only_property_is_skipped(self) -> None:
		for runtime in ("mono", "netfx"):
			with self.subTest(runtime=runtime):
				results = self.results / runtime
				actual = "unity-mono" if runtime == "mono" else "netfx"
				result = self.run_cli(
					"--filter",
					"FullyQualifiedName~AddsCommutatively|FullyQualifiedName~NativeOnly",
					f"-p:WotrRuntime={runtime}",
					expected_runtime=actual,
					results=results,
				)
				self.assert_success(result, 1, expected_runtime=actual, results=results)
				report = ET.parse(next(results.glob("*.trx")))
				# FsCheck 3.5.0 emits a skipped result but its xUnit case summary omits
				# that result from the native TRX counters. Check the actual result.
				skipped = [
					result
					for result in report.findall("t:Results/t:UnitTestResult", TRX_NS)
					if result.attrib["testName"].endswith(".NativeOnly")
				]
				self.assertEqual(len(skipped), 1)
				self.assertEqual(skipped[0].attrib["outcome"], "NotExecuted")
				run_id = next(results.rglob("environment.json")).parent.name
				run_report = (
					ROOT / "artifacts/obj/FuzzTests/wotr/test-runs" / run_id / "wotr-results.xml"
				)
				recorded = [
					test
					for test in ET.parse(run_report).getroot().findall("Test")
					if test.attrib["name"].endswith(".NativeOnly")
				]
				self.assertEqual(len(recorded), 1)
				self.assertEqual(recorded[0].attrib["classification"], "unity-runtime-required")

	def test_all_skipped_properties_cannot_pass(self) -> None:
		for runtime in ("mono", "netfx"):
			with self.subTest(runtime=runtime):
				result = self.run_cli(
					"--filter",
					"FullyQualifiedName~NativeOnly",
					f"-p:WotrRuntime={runtime}",
					results=self.results / runtime,
				)
				self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
				mixed = self.run_cli(
					"--filter",
					"FullyQualifiedName~AddsCommutatively|FullyQualifiedName~NativeOnly",
					f"-p:WotrRuntime={runtime}",
					"-p:WotrMinimumPassed=2",
					expected_runtime="unity-mono" if runtime == "mono" else "netfx",
					results=self.results / runtime / "mixed",
				)
				self.assertNotEqual(mixed.returncode, 0, mixed.stdout + mixed.stderr)
				self.assertIn("Too few passed tests: 1 passed, at least 2 required", mixed.stdout)
