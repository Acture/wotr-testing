"""Opt-in rule settlement tests. They boot the offline session and need the local game."""

from __future__ import annotations

import os
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "tests/fixtures/RuleTests/WotR.Testing.RuleTests.csproj"


@unittest.skipUnless(
	os.name == "nt" and os.environ.get("WOTR_TEST_INTEGRATION") == "1",
	"Set WOTR_TEST_INTEGRATION=1 on Windows with local WotR inputs to run rule settlement tests.",
)
class RuleSettlementTests(unittest.TestCase):
	def test_real_rules_and_game_random(self) -> None:
		result = subprocess.run(
			["dotnet", "test", str(PROJECT), "--nologo"],
			cwd=ROOT,
			capture_output=True,
			text=True,
			encoding="utf-8",
			errors="replace",
			timeout=900,
		)
		self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
