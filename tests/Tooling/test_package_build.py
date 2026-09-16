# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("package_build_test_tool", ROOT / "eng" / "package_build.py")
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(tool)


class PackageBuildTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)

    def package(self, suffix, version="1.0.0-rc4", commit="1" * 40, frameworks=("net8.0", "net10.0")):
        name = f"XRegistry.{version}.{suffix}"
        with zipfile.ZipFile(self.root / name, "w") as archive:
            archive.writestr("XRegistry.nuspec", f"""
                <package><metadata><id>XRegistry</id><version>{version}</version>
                <repository url="https://github.com/marcschier/xregistry-dotnet" commit="{commit}" />
                </metadata></package>
                """)
            for framework in frameworks:
                archive.writestr(f"lib/{framework}/XRegistry.dll", b"fixture")
        return self.root / name

    def test_exact_rc4_package_and_symbol_pairs_retain_hashes(self):
        self.package("nupkg")
        self.package("snupkg")
        files = tool.verify_packages(self.root, ("XRegistry",), "1.0.0-rc4", "1" * 40)
        self.assertEqual(len(files), 2)
        for record in files:
            self.assertEqual(record["sha256"], tool.release.file_hash(self.root / record["name"]))
            self.assertEqual(record["bytes"], (self.root / record["name"]).stat().st_size)

    def test_missing_symbols_and_stale_extra_packages_fail(self):
        self.package("nupkg")
        with self.assertRaises(ValueError):
            tool.verify_packages(self.root, ("XRegistry",), "1.0.0-rc4", "1" * 40)
        self.package("snupkg")
        self.package("nupkg", version="1.0.0-rc3")
        with self.assertRaises(ValueError):
            tool.verify_packages(self.root, ("XRegistry",), "1.0.0-rc4", "1" * 40)

    def test_wrong_source_commit_and_missing_framework_fail(self):
        package = self.package("nupkg", commit="2" * 40)
        self.package("snupkg")
        with self.assertRaises(ValueError):
            tool.verify_packages(self.root, ("XRegistry",), "1.0.0-rc4", "1" * 40)
        package.unlink()
        self.package("nupkg", frameworks=("net10.0",))
        with self.assertRaises(ValueError):
            tool.verify_packages(self.root, ("XRegistry",), "1.0.0-rc4", "1" * 40)

    def test_version_uses_the_same_canonical_release_grammar(self):
        (self.root / "version.json").write_text(json.dumps({"version": "1.0.0-rc4"}))
        self.assertEqual(tool.source_version(self.root), "1.0.0-rc4")
        for value in ("v1.0.0-rc4", "1.0.0+metadata", "1.0.0-RC4", "1.0.0;echo", "1.0.0-rc5"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                tool.source_version(self.root, value)

    def test_artifact_workflow_has_no_publication_authority(self):
        workflow = (ROOT / ".github" / "workflows" / "packages.yml").read_text()
        self.assertIn("eng/package_build.py", workflow)
        self.assertIn("contents: read", workflow)
        for forbidden in ("id-token: write", "packages: write", "contents: write", "secrets.", "NuGet/login", "release.py push"):
            self.assertNotIn(forbidden, workflow)


if __name__ == "__main__":
    unittest.main()
