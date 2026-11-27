"""Keep the SDK template's pinned versions and the template package in step with the repository."""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TEMPLATE = ROOT / "templates" / "WotR.Testing.Sdk"
TEMPLATE_PROJECT = TEMPLATE / "MyMod.OfflineTests.csproj"
TEMPLATE_PACKAGE = ROOT / "src" / "WotR.Testing.Sdk" / "WotR.Testing.Sdk.csproj"
TEMPLATE_PACKAGE_README = ROOT / "src" / "WotR.Testing.Sdk" / "README.md"
SDK_GUIDE = ROOT / "docs" / "sdk.md"
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
# The generation command every public document shows; its name must stay a plain identifier (the
# template replaces the name in the namespace and the fixture class too) and its output directory
# is the one the documented test commands use.
DOCUMENTED_GENERATION = "dotnet new wotr-test -n MyMod -o tests/MyMod.OfflineTests"
DOTNET = shutil.which("dotnet")


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
		guide = SDK_GUIDE.read_text(encoding="utf-8")
		self.assertIn(repository_version(), guide)

	def test_documents_install_the_locally_built_package(self) -> None:
		# The package is on no public feed, so an install by package id cannot work; every document
		# must install the locally built .nupkg by path instead.
		install = (
			f"dotnet new install artifacts/packages/WotR.Testing.Sdk.{repository_version()}.nupkg"
		)
		for document in (SDK_GUIDE, ROOT / "README.md", TEMPLATE_PACKAGE_README):
			text = document.read_text(encoding="utf-8")
			with self.subTest(document=document.name):
				self.assertIn(install, text)
				self.assertNotRegex(text, r"dotnet new install WotR\.Testing\.Sdk( --version)?\n")

	def test_documents_generate_with_a_plain_name_into_the_documented_directory(self) -> None:
		for document in (SDK_GUIDE, TEMPLATE_PACKAGE_README):
			text = document.read_text(encoding="utf-8")
			with self.subTest(document=document.name):
				self.assertIn(DOCUMENTED_GENERATION, text)
				# A dotted -n name doubles the .OfflineTests suffix and puts a dot in the
				# fixture class name, which is not compilable C#.
				self.assertNotRegex(text, r"dotnet new wotr-test -n \S*\.")

	def test_template_workflow_never_runs_fork_code(self) -> None:
		# The workflow targets a self-hosted runner with the licensed game on its disk; a fork's
		# pull request must never execute there.
		workflow = (TEMPLATE / "ci" / "offline-tests.yml").read_text(encoding="utf-8")
		self.assertIn("if: github.event.pull_request.head.repo.fork != true", workflow)

	def test_template_project_imports_a_local_gamepath_props_first(self) -> None:
		# The guide promises that an untracked GamePath.props next to the project is enough; the
		# import must be conditional (the file is optional) and precede the settings it feeds.
		project = ET.parse(TEMPLATE_PROJECT).getroot()
		children = list(project)
		imports = [
			index
			for index, child in enumerate(children)
			if child.tag == "Import" and "GamePath.props" in (child.get("Project") or "")
		]
		self.assertEqual(1, len(imports))
		self.assertIn("Exists", children[imports[0]].get("Condition") or "")
		settings = next(
			index
			for index, child in enumerate(children)
			if child.tag == "PropertyGroup" and child.find("WotrModAssembly") is not None
		)
		self.assertLess(imports[0], settings)


@unittest.skipIf(DOTNET is None, "the dotnet SDK is not installed")
class SdkTemplateGenerationTests(unittest.TestCase):
	"""Install the template, generate a project as documented, and check the output."""

	@staticmethod
	def dotnet(arguments: list[str], cwd: Path, home: Path) -> subprocess.CompletedProcess[str]:
		# DOTNET_CLI_HOME keeps the template hive in the temporary directory: the user's installed
		# templates are not touched.
		command = shutil.which("dotnet")
		assert command is not None
		environment = os.environ.copy()
		environment.update(
			DOTNET_CLI_HOME=str(home),
			DOTNET_NOLOGO="1",
			DOTNET_CLI_TELEMETRY_OPTOUT="1",
			DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1",
		)
		return subprocess.run(
			[command, *arguments],
			cwd=cwd,
			env=environment,
			capture_output=True,
			text=True,
			timeout=120,
			check=False,
		)

	def generate(self, root: Path) -> Path:
		(root / "home").mkdir()
		(root / "mod").mkdir()
		installed = self.dotnet(["new", "install", str(TEMPLATE)], cwd=root, home=root / "home")
		self.assertEqual(0, installed.returncode, installed.stderr or installed.stdout)
		generated = self.dotnet(
			["new", "wotr-test", "-n", "MyMod", "-o", "tests/MyMod.OfflineTests"],
			cwd=root / "mod",
			home=root / "home",
		)
		self.assertEqual(0, generated.returncode, generated.stderr or generated.stdout)
		return root / "mod" / "tests" / "MyMod.OfflineTests"

	def test_generation_creates_the_documented_layout(self) -> None:
		with tempfile.TemporaryDirectory() as scratch:
			project = self.generate(Path(scratch))
			for name in TEMPLATE_FILES - {".template.config/template.json"}:
				self.assertTrue((project / name).is_file(), f"{name} was not generated")
			for source in project.rglob("*"):
				if source.is_file():
					text = source.read_text(encoding="utf-8")
					self.assertNotIn(
						"OfflineTests.OfflineTests",
						text,
						f"{source.name} doubles the .OfflineTests suffix",
					)
			fixture = (project / "MyModGame.cs").read_text(encoding="utf-8")
			self.assertIn("namespace MyMod.OfflineTests", fixture)
			self.assertIn("public sealed class MyModGame : WotrGameFixture", fixture)

	def test_generated_project_reads_a_gamepath_props_next_to_it(self) -> None:
		with tempfile.TemporaryDirectory() as scratch:
			project = self.generate(Path(scratch))
			project_file = project / "MyMod.OfflineTests.csproj"
			query = ["msbuild", str(project_file), "-getProperty:WrathInstallDir"]
			without_file = self.dotnet(query, cwd=project, home=Path(scratch) / "home")
			self.assertEqual(0, without_file.returncode, without_file.stderr)
			self.assertEqual("", without_file.stdout.strip())
			(project / "GamePath.props").write_text(
				"<Project><PropertyGroup>"
				"<WrathInstallDir>C:/Games/Pathfinder Second Adventure</WrathInstallDir>"
				"</PropertyGroup></Project>",
				encoding="utf-8",
			)
			with_file = self.dotnet(query, cwd=project, home=Path(scratch) / "home")
			self.assertEqual(0, with_file.returncode, with_file.stderr)
			self.assertEqual("C:/Games/Pathfinder Second Adventure", with_file.stdout.strip())
			overridden = self.dotnet(
				[*query, "-p:WrathInstallDir=C:/Elsewhere"],
				cwd=project,
				home=Path(scratch) / "home",
			)
			self.assertEqual(0, overridden.returncode, overridden.stderr)
			self.assertEqual("C:/Elsewhere", overridden.stdout.strip())


if __name__ == "__main__":
	unittest.main()
