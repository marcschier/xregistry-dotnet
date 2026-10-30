# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("model_sync", ROOT / "eng" / "sync_models.py")
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(tool)


class ModelResourceSynchronizationTests(unittest.TestCase):
    def test_retained_draft_sources_disable_git_text_conversion(self) -> None:
        manifest = json.loads((ROOT / tool.DRAFT_MODELS).read_text(encoding="utf-8"))
        for entry in manifest["models"].values():
            path = entry["file"]
            with self.subTest(path=path):
                result = subprocess.run(
                    ["git", "check-attr", "text", "--", path], cwd=ROOT,
                    capture_output=True, text=True, check=True, timeout=10,
                )
                self.assertEqual(f"{path}: text: unset", result.stdout.strip())

    def test_staged_draft_model_bytes_match_the_pinned_hashes(self) -> None:
        manifest = json.loads((ROOT / tool.DRAFT_MODELS).read_text(encoding="utf-8"))
        for entry in manifest["models"].values():
            with self.subTest(path=entry["file"]):
                result = subprocess.run(
                    ["git", "show", ":" + entry["file"]], cwd=ROOT,
                    capture_output=True, check=True, timeout=10,
                )
                self.assertEqual(entry["sha256"], tool.source_tool.sha256(result.stdout))

    def test_durable_http_probe_requires_every_scoped_model_group(self) -> None:
        spec = importlib.util.spec_from_file_location("file_server_probe", ROOT / "eng" / "verify_file_server.py")
        assert spec is not None and spec.loader is not None
        probe = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(probe)
        expected = set()
        for path in (ROOT / "src" / "XRegistry.Models" / "Resources").glob("*.json"):
            model = json.loads(path.read_text(encoding="utf-8"))
            expected.update(group for group in model.get("groups", {}) if not group.startswith("$"))
        self.assertIn("apigroups", expected)
        self.assertIn("publications", expected)
        self.assertIn("thingmodelgroups", expected)
        self.assertIn("thingdescriptiongroups", expected)
        self.assertEqual(expected, probe.GROUPS)

    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.sources = {
            path: f'{{"source":"{name}"}}\n'.encode()
            for name, path in tool.MODELS.items()
        }
        self.api_source = b'{"source":"api"}\n'
        self.api_source_path = "tests/Conformance/DraftSources/api/model.json"
        self.write_draft_manifest()
        self.sources["LICENSE"] = b"Independent fixture license.\r\n"
        self.lock = {
            "fileCount": len(self.sources),
            "files": [
                {"path": path, "bytes": len(data), "sha256": tool.source_tool.sha256(data)}
                for path, data in self.sources.items()
            ],
        }
        for path, data in self.sources.items():
            target = self.root / tool.source_tool.CORPUS / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)

    def write_draft_manifest(self) -> None:
        draft_path = self.root / self.api_source_path
        draft_path.parent.mkdir(parents=True, exist_ok=True)
        draft_path.write_bytes(self.api_source)
        manifest_path = self.root / tool.DRAFT_MODELS
        manifest_path.parent.mkdir(parents=True, exist_ok=True)
        manifest_path.write_text(
            json.dumps({
                "schemaVersion": 1,
                "models": {
                    "api": {
                        "sourceRepository": "https://github.com/marcschier/xregistry-drafts",
                        "sourceCommit": "5f9d0134407927c0fae3b95e0aa844548e3756d6",
                        "sourcePath": "workingdrafts/models/api/model.json",
                        "file": self.api_source_path,
                        "sha256": tool.source_tool.sha256(self.api_source),
                    }
                },
            }),
            encoding="utf-8",
        )

    def synchronize(self, check: bool) -> None:
        with (
            mock.patch.object(tool.source_tool, "verify_corpus", return_value=self.lock),
        ):
            tool.synchronize(self.root, check)

    def test_copy_and_check_preserve_every_source_byte_and_license(self) -> None:
        self.synchronize(False)
        self.synchronize(True)
        directory = self.root / "src" / "XRegistry.Models" / "Resources"
        for name, path in tool.MODELS.items():
            with self.subTest(name=name):
                self.assertEqual((directory / (name + ".json")).read_bytes(), self.sources[path])
        self.assertEqual((directory / "api.json").read_bytes(), self.api_source)
        self.assertEqual((directory / "LICENSE").read_bytes(), b"Independent fixture license.\r\n")

    def test_check_rejects_changed_resource_without_overwriting_it(self) -> None:
        self.synchronize(False)
        model = self.root / "src/XRegistry.Models/Resources/core.json"
        model.write_bytes(b"changed")
        with self.assertRaisesRegex(tool.source_tool.SpecificationError, "stale"):
            self.synchronize(True)
        self.assertEqual(model.read_bytes(), b"changed")

    def test_missing_pinned_model_and_unclassified_resource_are_errors(self) -> None:
        missing = {
            "fileCount": len(self.sources) - 1,
            "files": [entry for entry in self.lock["files"] if entry["path"] != "core/model.json"],
        }
        with mock.patch.object(tool.source_tool, "verify_corpus", return_value=missing):
            with self.assertRaisesRegex(tool.source_tool.SpecificationError, "missing"):
                tool.synchronize(self.root, False)
        self.synchronize(False)
        other = self.root / "src/XRegistry.Models/Resources/unclassified.json"
        other.write_bytes(b"{}")
        with self.assertRaisesRegex(tool.source_tool.SpecificationError, "unclassified"):
            self.synchronize(True)

    def test_added_model_requires_valid_draft_provenance(self) -> None:
        (self.root / self.api_source_path).write_bytes(b'{"changed":"api"}\n')
        with mock.patch.object(tool.source_tool, "verify_corpus", return_value=self.lock):
            with self.assertRaisesRegex(tool.source_tool.SpecificationError, "hash mismatch"):
                tool.synchronize(self.root, False)

    def test_draft_repository_must_be_a_canonical_github_repository_url(self) -> None:
        manifest_path = self.root / tool.DRAFT_MODELS
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["models"]["api"]["sourceRepository"] = "https://example.com/owner/repo"
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        with mock.patch.object(tool.source_tool, "verify_corpus", return_value=self.lock):
            with self.assertRaisesRegex(tool.source_tool.SpecificationError, "provenance"):
                tool.synchronize(self.root, False)


if __name__ == "__main__":
    unittest.main()
