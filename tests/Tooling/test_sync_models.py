from __future__ import annotations

import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("model_sync", ROOT / "eng" / "sync_models.py")
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(tool)


class ModelResourceSynchronizationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.sources = {
            path: f'{{"source":"{name}"}}\n'.encode()
            for name, path in tool.MODELS.items()
        }
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

    def test_copy_and_check_preserve_every_source_byte_and_license(self) -> None:
        with mock.patch.object(tool.source_tool, "verify_corpus", return_value=self.lock):
            tool.synchronize(self.root, False)
            tool.synchronize(self.root, True)
        directory = self.root / "src" / "XRegistry.Models" / "Resources"
        for name, path in tool.MODELS.items():
            with self.subTest(name=name):
                self.assertEqual((directory / (name + ".json")).read_bytes(), self.sources[path])
        self.assertEqual((directory / "LICENSE").read_bytes(), b"Independent fixture license.\r\n")

    def test_check_rejects_changed_resource_without_overwriting_it(self) -> None:
        with mock.patch.object(tool.source_tool, "verify_corpus", return_value=self.lock):
            tool.synchronize(self.root, False)
            model = self.root / "src/XRegistry.Models/Resources/core.json"
            model.write_bytes(b"changed")
            with self.assertRaisesRegex(tool.source_tool.SpecificationError, "stale"):
                tool.synchronize(self.root, True)
            self.assertEqual(model.read_bytes(), b"changed")

    def test_missing_pinned_model_and_unclassified_resource_are_errors(self) -> None:
        missing = {
            "fileCount": len(self.sources) - 1,
            "files": [entry for entry in self.lock["files"] if entry["path"] != "core/model.json"],
        }
        with mock.patch.object(tool.source_tool, "verify_corpus", return_value=missing):
            with self.assertRaisesRegex(tool.source_tool.SpecificationError, "missing"):
                tool.synchronize(self.root, False)
        with mock.patch.object(tool.source_tool, "verify_corpus", return_value=self.lock):
            tool.synchronize(self.root, False)
            other = self.root / "src/XRegistry.Models/Resources/unclassified.json"
            other.write_bytes(b"{}")
            with self.assertRaisesRegex(tool.source_tool.SpecificationError, "unclassified"):
                tool.synchronize(self.root, True)


if __name__ == "__main__":
    unittest.main()
