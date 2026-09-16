# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Regression checks for repository-owned source headers."""

import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("header_policy", ROOT / "eng" / "check_headers.py")
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = tool
SPEC.loader.exec_module(tool)


class HeaderPolicyTests(unittest.TestCase):
    def fixture(self, relative: str, content: str) -> tuple[Path, Path]:
        directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        path = directory / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8", newline="\n")
        return directory, path

    def test_repository_owned_sources_have_exact_headers(self) -> None:
        sources = tool.owned_sources(ROOT)
        self.assertGreater(len(sources), 600)
        self.assertEqual([], tool.check(ROOT))
        self.assertIn(Path("src/XRegistry/Addressing/RegistryPath.cs"), {item.path for item in sources})
        self.assertIn(Path("eng/check_headers.py"), {item.path for item in sources})
        self.assertIn(Path(".github/workflows/ci.yml"), {item.path for item in sources})

    def test_fix_is_idempotent_for_slash_hash_shebang_and_xml_sources(self) -> None:
        cases = {
            "Source.cs": "namespace Test;\n",
            "script.py": "#!/usr/bin/env python3\n\"\"\"Module.\"\"\"\n",
            "build.ps1": "[CmdletBinding()]\nparam()\n",
            "file.Dockerfile": "# syntax=docker/dockerfile:1\nFROM scratch\n",
            "Project.csproj": "<?xml version=\"1.0\"?>\n<Project />\n",
            ".gitignore": "bin/\n",
        }
        directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        for relative, content in cases.items():
            path = directory / relative
            path.write_text(content, encoding="utf-8", newline="\n")
        changed = tool.apply(directory)
        self.assertEqual(set(map(Path, cases)), set(changed))
        self.assertEqual([], tool.check(directory))
        self.assertEqual([], tool.apply(directory))
        self.assertEqual("#!/usr/bin/env python3", (directory / "script.py").read_text().splitlines()[0])
        self.assertEqual("# syntax=docker/dockerfile:1", (directory / "file.Dockerfile").read_text().splitlines()[0])
        self.assertEqual("<?xml version=\"1.0\"?>", (directory / "Project.csproj").read_text().splitlines()[0])

    def test_check_rejects_missing_duplicate_partial_and_misplaced_headers(self) -> None:
        directory, source = self.fixture("Source.cs", "namespace Test;\n")
        self.assertIn("missing or misplaced header", "\n".join(tool.check(directory)))
        source.write_text(
            f"// {tool.COPYRIGHT}\n// {tool.SPDX}\n// {tool.SPDX}\nnamespace Test;\n",
            encoding="utf-8",
        )
        self.assertIn("SPDX identifier must appear exactly once", "\n".join(tool.check(directory)))
        source.write_text(f"// {tool.COPYRIGHT}\nnamespace Test;\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "partial or duplicate"):
            tool.apply(directory)
        source.write_text(f"namespace Test;\n// {tool.COPYRIGHT}\n// {tool.SPDX}\n", encoding="utf-8")
        self.assertIn("missing or misplaced header", "\n".join(tool.check(directory)))

    def test_protected_sources_and_generated_directories_are_excluded(self) -> None:
        directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        excluded = (
            "tests/Conformance/Sources/tool.py",
            "tests/Conformance/Corrections/SPEC-001/tool.py",
            "src/XRegistry.Models/Resources/Model.cs",
            "src/XRegistry/bin/Generated.cs",
            "src/XRegistry/obj/Generated.cs",
            "artifacts/Generated.cs",
        )
        for relative in excluded:
            path = directory / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("class Original {}\n", encoding="utf-8")
        self.assertEqual([], tool.owned_sources(directory))
        self.assertEqual([], tool.check(directory))
        self.assertEqual([], tool.apply(directory))

    def test_unrelated_binary_assets_are_ignored_without_decoding(self) -> None:
        directory, source = self.fixture("Source.cs", "namespace Test;\n")
        asset = directory / "logo.png"
        asset.write_bytes(b"\x89PNG\r\n\x1a\n\xff\x00")
        self.assertEqual([Path("Source.cs")], [item.path for item in tool.owned_sources(directory)])
        self.assertEqual([Path("Source.cs")], tool.apply(directory))
        self.assertEqual([], tool.check(directory))
        self.assertEqual(b"\x89PNG\r\n\x1a\n\xff\x00", asset.read_bytes())
        self.assertIn(tool.COPYRIGHT, source.read_text(encoding="utf-8"))

    def test_unsupported_non_xml_template_is_rejected(self) -> None:
        directory, _ = self.fixture("Consumer.template", "not xml\n")
        with self.assertRaisesRegex(ValueError, "Expected XML"):
            tool.owned_sources(directory)


if __name__ == "__main__":
    unittest.main()
