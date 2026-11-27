"""Keep the SDK template's pinned versions and the template package in step with the repository."""

from __future__ import annotations

import json
import re
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TEMPLATE = ROOT / "templates" / "WotR.Testing.Sdk"
TEMPLATE_PROJECT = TEMPLATE / "MyMod.OfflineTests.csproj"
TEMPLATE_PACKAGE = ROOT / "src" / "WotR.Testing.Sdk" / "WotR.Testing.Sdk.csproj"
TEMPLATE_FILES = frozenset(
	{
		"MyMod.OfflineTests.csproj",
		"MyModGame.cs",
		"ExampleTests.cs",
		"README.md",
		"ci/offline-tests.yml",
		".template.config/template.json",
	}
)


def repository_version() -> str:
	properties = ET.parse(ROOT / "Directory.Build.props").getroot()
	for group in properties.findall("PropertyGroup"):
		version = group.find("Version")
		if version is not None and version.text:
			return version.text.strip()
	raise AssertionError("Directory.Build.props declares no Version.")


def package_references(project: Path) -> dict[str, str]:
	references: dict[str, str] = {}
	for item in ET.parse(project).getroot().iter("PackageReference"):
		name = item.get("Include")
		version = item.get("Version")
		if name and version:
			references[name] = version
	return references


def package_entries(project: Path) -> list[str]:
	entries: list[str] = []
	for item in ET.parse(project).getroot().iter("WotrPackageEntry"):
		entries.extend(entry for entry in item.get("Include", "").split(";") if entry)
	return [entry.replace("\\", "/") for entry in entries]


class SdkTemplateTests(unittest.TestCase):
	def test_template_pins_the_repository_package_version(self) -> None:
		references = package_references(TEMPLATE_PROJECT)
		self.assertIn("WotR.Testing", references)
		self.assertEqual(f"[{repository_version()}]", references["WotR.Testing"])

	def test_template_dependencies_are_exact_package_references(self) -> None:
		references = package_references(TEMPLATE_PROJECT)
		self.assertIn("Microsoft.NET.Test.Sdk", references)
		self.assertIn("xunit", references)
		for name, version in references.items():
			with self.subTest(package=name):
				self.assertRegex(version, r"^\[[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?\]$")

	def test_template_references_no_assembly_by_hand(self) -> None:
		# The SDK's build targets add the game references; a test project must not add DLLs itself.
		project = ET.parse(TEMPLATE_PROJECT).getroot()
		references = [item for item in project.iter("Reference") if item.get("Include")]
		self.assertEqual([], references)

	def test_template_holds_the_expected_files_only(self) -> None:
		files = {
			path.relative_to(TEMPLATE).as_posix() for path in TEMPLATE.rglob("*") if path.is_file()
		}
		self.assertEqual(set(TEMPLATE_FILES), files)

	def test_template_package_packs_every_template_file(self) -> None:
		packed = [
			entry for entry in package_entries(TEMPLATE_PACKAGE) if entry.startswith("content/")
		]
		expected = sorted("content/WotR.Testing.Sdk/" + name for name in TEMPLATE_FILES)
		self.assertEqual(expected, sorted(packed))

	def test_template_package_allowlist_matches_the_packed_files(self) -> None:
		package = ET.parse(TEMPLATE_PACKAGE).getroot()
		packed = [
			item.get("PackagePath", "")
			for item in package.iter("None")
			if item.get("Pack") == "true" and item.get("PackagePath", "").startswith("content/")
		]
		allowed = [
			entry for entry in package_entries(TEMPLATE_PACKAGE) if entry.startswith("content/")
		]
		self.assertEqual(sorted(allowed), sorted(packed))

	def test_template_metadata_names_the_template(self) -> None:
		metadata_file = TEMPLATE / ".template.config" / "template.json"
		metadata = json.loads(metadata_file.read_text(encoding="utf-8"))
		self.assertEqual("WotR.Testing.Sdk", metadata["identity"])
		self.assertEqual("wotr-test", metadata["shortName"])
		self.assertEqual("MyMod", metadata["sourceName"])

	def test_template_workflow_pins_its_actions(self) -> None:
		workflow = (TEMPLATE / "ci" / "offline-tests.yml").read_text(encoding="utf-8")
		for action in re.findall(r"uses:\s*(\S+)", workflow):
			with self.subTest(action=action):
				self.assertRegex(action, r"^[0-9A-Za-z_.-]+/[0-9A-Za-z_.-]+@[0-9a-f]{40}$")

	def test_sdk_guide_documents_the_pinned_version(self) -> None:
		guide = (ROOT / "docs" / "sdk.md").read_text(encoding="utf-8")
		self.assertIn(repository_version(), guide)


if __name__ == "__main__":
	unittest.main()
