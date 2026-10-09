"""Boundary regressions for public code and optional private notes."""

from __future__ import annotations

import unittest

from check_repository import Entry, boundary_errors


class BoundaryTests(unittest.TestCase):
	def test_public_documents_and_environment_examples_are_allowed(self) -> None:
		entries: list[Entry] = [Entry("100644", "docs/usage.md"), Entry("100644", ".env.example")]
		self.assertEqual(boundary_errors(entries), [])

	def test_private_submodule_does_not_require_private_content_in_ci(self) -> None:
		self.assertEqual(boundary_errors([Entry("160000", "notes")]), [])
		self.assertTrue(boundary_errors([Entry("100644", "notes/draft.md")]))

	def test_docs_cannot_alias_private_notes(self) -> None:
		for mode in ("120000", "160000"):
			with self.subTest(mode=mode):
				self.assertTrue(boundary_errors([Entry(mode, "docs")]))

	def test_local_configuration_and_credential_backups_are_rejected(self) -> None:
		for path in (
			".env",
			"src/.env.production",
			".obsidian/plugins/data.json",
			"credentials-backup.json",
		):
			with self.subTest(path=path):
				self.assertTrue(boundary_errors([Entry("100644", path)]))

	def test_game_files_and_local_game_path_are_rejected(self) -> None:
		for path in (
			"vendor/wotr/Wrath_Data/Managed/Assembly-CSharp.dll",
			"Bundles/blueprints-pack.bbp",
			"tests/fixtures/blueprint.assets",
			"external/Wrath_Data/StreamingAssets/Localization/enGB.json",
			"GamePath.props",
		):
			with self.subTest(path=path):
				self.assertTrue(boundary_errors([Entry("100644", path)]))

	def test_library_sources_and_build_files_are_allowed(self) -> None:
		entries: list[Entry] = [
			Entry("100644", "src/WotR.Testing.Offline/OfflineRuntime.cs"),
			Entry("100644", "src/WotR.Testing.Offline/build/WotR.Testing.Offline.targets"),
			Entry("100644", "Directory.Build.props"),
		]
		self.assertEqual(boundary_errors(entries), [])


if __name__ == "__main__":
	unittest.main()
