# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

from __future__ import annotations

import contextlib
import copy
import hashlib
import importlib.util
import io
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "correction_history_tool", ROOT / "eng" / "specification" / "manage.py"
)
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(tool)
CAPTURE = importlib.util.spec_from_file_location(
    "correction_capture_tool", ROOT / "eng" / "specification" / "capture_corrections.py"
)
assert CAPTURE is not None and CAPTURE.loader is not None
capture = importlib.util.module_from_spec(CAPTURE)
with mock.patch.dict(sys.modules, {"manage": tool}):
    CAPTURE.loader.exec_module(capture)


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


class CorrectionFixture(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory(prefix="xregistry-correction-history-test-")
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.checkout = self.root / "checkout"
        self.scope = {
            "schemaVersion": 1,
            "sourceRepository": "https://example.invalid/source",
            "upstreamRepository": "https://example.invalid/upstream",
            "commit": "a" * 40,
            "branch": "main",
            "includesUncommittedChanges": True,
            "baselineManifestSha256": "b" * 64,
            "normative": {"core/example.md": "core"},
            "excludeSuffixes": [".png"],
        }
        self.sources = {
            "LICENSE": b"Independent tooling fixture license.\n",
            "core/example.md": b"# Contract\n\nA reader MUST preserve exact bytes.\n",
            "tools/federation_examples.py": b"# Original tooling fixture, not an oracle.\n",
        }
        self.original = {
            path: {"path": path, "bytes": len(data), "sha256": digest(data)}
            for path, data in self.sources.items()
        }
        self.lock = {
            **{key: value for key, value in self.scope.items() if key != "excludeSuffixes"},
            "corpusPath": tool.CORPUS.as_posix(),
            "fileCount": len(self.sources),
            "files": list(self.original.values()),
            "excluded": [],
        }
        for path, data in self.sources.items():
            self.write_bytes(tool.CORPUS / path, data)
        self.write_json(Path("eng") / "specification" / "scope.json", self.scope)
        self.write_json(tool.LOCK, self.lock)

    def write_bytes(self, relative: Path, data: bytes) -> None:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def write_json(self, relative: Path, document: dict) -> None:
        self.write_bytes(relative, tool.encoded(document))

    def entry(
        self, case: str, path: str, data: bytes, *, predecessor: dict | None = None,
        legacy: bool = False, materialize: bool = True,
    ) -> dict:
        original_hash = self.original[path]["sha256"] if path in self.original else None
        entry = {
            "case": case,
            "path": path,
            "originalSha256": original_hash,
            "sha256": digest(data),
            "bytes": len(data),
            "file": (tool.CORRECTED_SOURCES / case / path).as_posix(),
            "sourceCommitBeforeCorrections": "c" * 40,
            "reason": "Independent correction history tooling fixture.",
            "testIds": ["Synthetic.CorrectionHistory.NotConformanceEvidence"],
        }
        if not legacy:
            entry.update(
                predecessorSha256=predecessor["sha256"] if predecessor else original_hash,
                predecessorCase=predecessor["case"] if predecessor else None,
            )
        if materialize:
            self.write_bytes(Path(entry["file"]), data)
        return entry

    def history(self, entries: list[dict], version: int = 2) -> dict:
        document = {
            "schemaVersion": version,
            "baselineManifestSha256": self.scope["baselineManifestSha256"],
            "entries": entries,
        }
        self.write_json(tool.CORRECTIONS, document)
        return document

    def active(self) -> dict[str, dict]:
        return tool.correction_records(self.root, self.original)

    def linked_history(self) -> tuple[dict, dict, dict]:
        first = self.entry("SPEC-004", "tools/federation_examples.py", b"prior correction\n")
        second = self.entry("SPEC-005", first["path"], b"successor correction\n", predecessor=first)
        return first, second, self.history([first, second])

    def inputs(self, files: dict[str, bytes]) -> None:
        for path, data in files.items():
            self.write_bytes(Path("checkout") / path, data)

    def snapshot(self) -> dict[str, bytes]:
        files = [
            path
            for directory in (tool.CORPUS, tool.CORRECTED_SOURCES)
            for path in (self.root / directory).rglob("*")
            if path.is_file()
        ]
        if (self.root / tool.CORRECTIONS).exists():
            files.append(self.root / tool.CORRECTIONS)
        return {path.relative_to(self.root).as_posix(): path.read_bytes() for path in files}

    def run_capture(
        self, case: str, paths: list[str], *, reason: str = "Reviewed fixture correction.",
        tests: tuple[str, ...] = ("Synthetic.Capture.NotConformanceEvidence",),
        revision: str = "d" * 40,
    ) -> tuple[int, str, str]:
        arguments = ["capture_corrections.py", "--source", str(self.checkout),
                     "--case", case, "--reason", reason]
        for test in tests:
            arguments.extend(("--test", test))
        arguments.extend(paths)
        stdout, stderr = io.StringIO(), io.StringIO()
        provenance = subprocess.CompletedProcess(["git"], 0, revision + "\n", "")
        with mock.patch.object(tool, "ROOT", self.root), \
                mock.patch.object(sys, "argv", arguments), \
                mock.patch.object(capture.subprocess, "run", return_value=provenance), \
                contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            status = capture.main()
        return status, stdout.getvalue(), stderr.getvalue()

    def run_manage(self, operation: str) -> tuple[int, str, str]:
        stdout, stderr = io.StringIO(), io.StringIO()
        with mock.patch.object(tool, "ROOT", self.root), \
                contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            status = tool.main([operation])
        return status, stdout.getvalue(), stderr.getvalue()


class CorrectionHistoryTests(CorrectionFixture):
    def test_original_legacy_manifests_still_load(self) -> None:
        first = self.entry("SPEC-004", "tools/federation_examples.py", b"legacy helper\n", legacy=True)
        added = self.entry("SPEC-001", "tools/new_helper.py", b"legacy addition\n", legacy=True)
        self.history([first, added], version=1)
        before = self.snapshot()

        self.assertEqual(self.active(), {first["path"]: first, added["path"]: added})
        self.assertEqual(
            tool.active_source(self.root, added["path"], self.original),
            (b"legacy addition\n", added["sha256"]),
        )
        self.assertEqual(self.snapshot(), before)

    def test_empty_or_absent_manifest_has_no_active_corrections(self) -> None:
        self.assertEqual(self.active(), {})
        for version in (1, 2):
            with self.subTest(version=version):
                self.history([], version)
                self.assertEqual(self.active(), {})

    def test_first_predecessor_must_match_original_or_absence(self) -> None:
        for path in ("core/example.md", "tools/new_helper.py"):
            first = self.entry("SPEC-001", path, b"first correction\n")
            self.history([first])
            self.assertEqual(self.active()[path], first)
            for field, value in (("predecessorSha256", "f" * 64), ("predecessorCase", "SPEC-000")):
                with self.subTest(path=path, field=field):
                    self.history([{**first, field: value}])
                    with self.assertRaisesRegex(tool.SpecificationError, "predecessor"):
                        self.active()
            (self.root / first["file"]).unlink()

    def test_wrong_prior_hash_is_rejected(self) -> None:
        first, second, _ = self.linked_history()
        self.history([first, {**second, "predecessorSha256": self.original[first["path"]]["sha256"]}])
        with self.assertRaisesRegex(tool.SpecificationError, "predecessor"):
            self.active()

    def test_wrong_prior_case_is_rejected(self) -> None:
        first, second, _ = self.linked_history()
        self.history([first, {**second, "predecessorCase": "SPEC-003"}])
        with self.assertRaisesRegex(tool.SpecificationError, "predecessor"):
            self.active()

    def test_successor_original_hash_remains_bound_to_the_frozen_source(self) -> None:
        first, second, _ = self.linked_history()
        self.history([first, {**second, "originalSha256": first["sha256"]}])
        with self.assertRaisesRegex(tool.SpecificationError, "original hash"):
            self.active()

    def test_missing_predecessor_fields_are_rejected(self) -> None:
        _, _, document = self.linked_history()
        for index in (0, 1):
            for field in ("predecessorSha256", "predecessorCase"):
                with self.subTest(index=index, field=field):
                    changed = copy.deepcopy(document)
                    del changed["entries"][index][field]
                    self.write_json(tool.CORRECTIONS, changed)
                    with self.assertRaises(tool.SpecificationError):
                        self.active()

    def test_reordered_entries_are_rejected(self) -> None:
        first, second, _ = self.linked_history()
        self.history([second, first])
        with self.assertRaisesRegex(tool.SpecificationError, "predecessor"):
            self.active()

    def test_duplicate_path_case_and_reused_case_forks_are_rejected(self) -> None:
        first, second, _ = self.linked_history()
        for entries in ([first, first, second], [first, second, {
            **first, "predecessorSha256": second["sha256"], "predecessorCase": second["case"],
        }]):
            with self.subTest(entries=entries):
                self.history(entries)
                with self.assertRaises(tool.SpecificationError):
                    self.active()

    def test_case_downgrade_is_rejected_even_with_the_active_predecessor(self) -> None:
        first, second, _ = self.linked_history()
        downgrade = self.entry("SPEC-003", first["path"], b"downgrade\n", predecessor=second)
        self.history([first, second, downgrade])
        with self.assertRaisesRegex(tool.SpecificationError, "advance"):
            self.active()

    def test_cycle_is_rejected(self) -> None:
        first, second, _ = self.linked_history()
        self.history([{
            **first, "predecessorSha256": second["sha256"], "predecessorCase": second["case"],
        }, second])
        with self.assertRaisesRegex(tool.SpecificationError, "predecessor"):
            self.active()

    def test_branch_cannot_skip_the_active_predecessor(self) -> None:
        first, second, _ = self.linked_history()
        branch = self.entry("SPEC-006", first["path"], b"branch\n", predecessor=first)
        self.history([first, second, branch])
        with self.assertRaisesRegex(tool.SpecificationError, "predecessor"):
            self.active()

    def test_schema_downgrade_does_not_discard_history_fields(self) -> None:
        first = self.entry("SPEC-004", "tools/federation_examples.py", b"correction\n")
        self.history([first], version=1)
        with self.assertRaisesRegex(tool.SpecificationError, "fields|entry"):
            self.active()

    def test_flat_manifest_cannot_hide_retained_history(self) -> None:
        _, second, _ = self.linked_history()
        flat = {key: value for key, value in second.items() if not key.startswith("predecessor")}
        self.history([flat], version=1)
        with self.assertRaisesRegex(tool.SpecificationError, "unlisted"):
            self.active()

    def test_every_historical_file_is_checked_for_missing_size_and_hash_changes(self) -> None:
        first, second, _ = self.linked_history()
        target = self.root / first["file"]
        for data in (None, b"short\n", b"PRIOR correction\n"):
            with self.subTest(data=data):
                if data is None:
                    target.unlink()
                else:
                    target.write_bytes(data)
                with self.assertRaisesRegex(tool.SpecificationError, "missing or changed"):
                    self.active()
                self.assertEqual((self.root / second["file"]).read_bytes(), b"successor correction\n")
                target.write_bytes(b"prior correction\n")

    def test_unlisted_history_file_is_rejected(self) -> None:
        first, _, _ = self.linked_history()
        self.entry("SPEC-003", first["path"], b"unlisted prior evidence\n")
        with self.assertRaisesRegex(tool.SpecificationError, "unlisted"):
            self.active()

    def test_missing_manifest_rejects_retained_correction_files(self) -> None:
        self.linked_history()
        (self.root / tool.CORRECTIONS).unlink()
        with self.assertRaisesRegex(tool.SpecificationError, "unlisted"):
            self.active()

    def test_manifest_and_entries_require_exact_fields(self) -> None:
        first = self.entry("SPEC-001", "tools/new_helper.py", b"")
        document = self.history([first])
        variants = [
            {**document, "unexpected": True},
            {**document, "schemaVersion": True},
            {**document, "schemaVersion": 3},
            {**document, "baselineManifestSha256": "e" * 64},
            {**document, "entries": {}},
            {**document, "entries": [None]},
            {**document, "entries": [{**first, "unexpected": True}]},
        ]
        for field in first:
            entry = {key: value for key, value in first.items() if key != field}
            variants.append({**document, "entries": [entry]})
        for changed in variants:
            with self.subTest(document=changed):
                self.write_json(tool.CORRECTIONS, changed)
                with self.assertRaises(tool.SpecificationError):
                    self.active()

    def test_invalid_provenance_size_hash_and_test_ids_are_rejected(self) -> None:
        first = self.entry("SPEC-001", "core/example.md", b"correction\n")
        for field, values in {
            "case": (None, "", "spec-001", "SPEC-0001", "SPEC-001\n", 1),
            "sourceCommitBeforeCorrections": (None, "a" * 39, "G" * 40, []),
            "reason": (None, "", " \t", []),
            "testIds": (None, [], "", [""], [" \t"], [1], ["duplicate", "duplicate"]),
            "bytes": (None, -1, True, 1.5, tool.MAX_JSON_BYTES + 1),
            "sha256": (None, "a" * 63, "A" * 64, []),
            "originalSha256": (None, "e" * 64),
        }.items():
            for value in values:
                with self.subTest(field=field, value=value):
                    self.history([{**first, field: value}])
                    with self.assertRaises(tool.SpecificationError):
                        self.active()

    def test_unsafe_and_opcua_source_paths_are_rejected(self) -> None:
        first = self.entry("SPEC-001", "tools/new_helper.py", b"correction\n")
        for path in (
            "../outside", "/outside", "C:\\outside", "a//b", "a/./b", "a/b:ads",
            "a/b.", "a/b ", "tools/opcua.py", "bindings/OPCUA/model.json",
        ):
            with self.subTest(path=path):
                self.history([{**first, "path": path}])
                with self.assertRaises(tool.SpecificationError):
                    self.active()

    def test_case_and_separator_aliases_cannot_create_new_history_roots(self) -> None:
        first = self.entry("SPEC-001", "core/example.md", b"correction\n")
        for path in ("CORE/example.md", "core\\example.md"):
            with self.subTest(path=path):
                alias = {
                    **first, "path": path, "originalSha256": None, "predecessorSha256": None,
                }
                self.history([alias])
                with self.assertRaisesRegex(tool.SpecificationError, "path"):
                    self.active()

    def test_duplicate_legacy_paths_are_rejected_case_insensitively(self) -> None:
        first = self.entry("SPEC-001", "tools/new_helper.py", b"one\n", legacy=True)
        second = self.entry("SPEC-002", "TOOLS/new_helper.py", b"two\n", legacy=True)
        for path in ("tools/new_helper.py", "TOOLS/new_helper.py"):
            with self.subTest(path=path):
                self.history([first, {**second, "path": path}], version=1)
                with self.assertRaisesRegex(tool.SpecificationError, "path"):
                    self.active()

    def test_historical_file_must_belong_to_its_declared_case_and_path(self) -> None:
        first, second, _ = self.linked_history()
        wrong = tool.CORRECTED_SOURCES / "SPEC-006" / second["path"]
        (self.root / wrong).parent.mkdir(parents=True)
        (self.root / second["file"]).rename(self.root / wrong)
        self.history([first, {**second, "file": wrong.as_posix()}])
        with self.assertRaisesRegex(tool.SpecificationError, "fields|file"):
            self.active()

    def test_two_records_cannot_share_an_evidence_file(self) -> None:
        first, second, _ = self.linked_history()
        self.history([first, {**second, "file": first["file"]}])
        with self.assertRaises(tool.SpecificationError):
            self.active()

    def test_evidence_file_paths_reject_escapes_exclusions_and_case_aliases(self) -> None:
        first = self.entry("SPEC-001", "core/example.md", b"correction\n")
        for file in (
            "../outside", "C:\\outside", "/outside",
            "tests/Conformance/Sources/core/example.md",
            "tests/Conformance/Corrections/SPEC-001/../outside",
            "tests/Conformance/Corrections/SPEC-001/tools/opcua.py",
            first["file"].upper(), first["file"].replace("/", "\\"),
        ):
            with self.subTest(file=file):
                self.history([{**first, "file": file}])
                with self.assertRaisesRegex(tool.SpecificationError, "fields"):
                    self.active()

    def test_manifest_links_and_history_junctions_are_rejected(self) -> None:
        first, _, _ = self.linked_history()
        for attribute, link in (
            ("is_symlink", self.root / tool.CORRECTIONS),
            ("is_junction", (self.root / first["file"]).parent),
        ):
            with self.subTest(attribute=attribute), \
                    mock.patch.object(Path, attribute, lambda candidate: candidate == link, create=True):
                with self.assertRaisesRegex(tool.SpecificationError, "link"):
                    self.active()

    def test_symlink_traversal_in_historical_evidence_is_rejected(self) -> None:
        first, _, _ = self.linked_history()
        target = self.root / first["file"]
        for link in (target, target.parent, self.root / tool.CORRECTED_SOURCES):
            with self.subTest(link=link), \
                    mock.patch.object(Path, "is_symlink", autospec=True,
                                      side_effect=lambda candidate: candidate == link):
                with self.assertRaisesRegex(tool.SpecificationError, "link"):
                    self.active()

    def test_unlisted_symlink_directory_is_rejected(self) -> None:
        self.linked_history()
        link = self.root / tool.CORRECTED_SOURCES / "unlisted-link"
        link.mkdir()
        with mock.patch.object(Path, "is_symlink", autospec=True,
                               side_effect=lambda candidate: candidate == link):
            with self.assertRaisesRegex(tool.SpecificationError, "link"):
                self.active()

    def test_nonregular_inventory_entries_are_not_silently_ignored(self) -> None:
        self.linked_history()
        special = self.root / tool.CORRECTED_SOURCES / "special"
        special.write_bytes(b"filesystem type fixture\n")
        is_file = Path.is_file
        with mock.patch.object(Path, "is_file", autospec=True,
                               side_effect=lambda candidate: candidate != special and is_file(candidate)):
            with self.assertRaisesRegex(tool.SpecificationError, "regular"):
                self.active()

    def test_inventory_enumeration_failure_is_not_treated_as_empty(self) -> None:
        (self.root / tool.CORRECTED_SOURCES).mkdir(parents=True)
        with mock.patch.object(tool.os, "scandir", side_effect=PermissionError("synthetic enumeration failure")):
            with self.assertRaisesRegex(tool.SpecificationError, "enumerate"):
                self.active()

    def test_ordered_history_preserves_prior_bytes_and_returns_only_active_records(self) -> None:
        first = self.entry("SPEC-004", "tools/federation_examples.py", b"prior correction\n")
        independent = self.entry("SPEC-001", "core/example.md", self.sources["core/example.md"])
        second = self.entry("SPEC-005", first["path"], b"successor correction\n", predecessor=first)
        self.history([first, independent, second])
        before = self.snapshot()

        self.assertEqual(self.active(), {first["path"]: second, independent["path"]: independent})
        self.assertEqual(
            tool.active_source(self.root, first["path"], self.original),
            (b"successor correction\n", second["sha256"]),
        )
        self.assertEqual(tool.verify_corpus(self.root)["fileCount"], 3)
        self.assertEqual((self.root / first["file"]).read_bytes(), b"prior correction\n")
        self.assertEqual(
            (self.root / tool.CORPUS / first["path"]).read_bytes(),
            b"# Original tooling fixture, not an oracle.\n",
        )
        self.assertEqual(self.snapshot(), before)


class CorrectionPreflightTests(CorrectionFixture):
    def test_preflight_selects_the_staged_head_without_writing_any_files(self) -> None:
        first, second, document = self.linked_history()
        next_entry = self.entry("SPEC-006", first["path"], b"staged\n",
                                predecessor=second, materialize=False)
        proposed = {**document, "entries": [first, second, next_entry]}
        before = self.snapshot()
        active = tool.validate_corrections(
            self.root, self.original, proposed, pending={next_entry["file"]: b"staged\n"},
        )

        self.assertEqual(active, {first["path"]: next_entry})
        self.assertEqual(self.snapshot(), before)
        self.assertEqual(self.active(), {first["path"]: second})
        self.assertFalse((self.root / next_entry["file"]).exists())

    def test_preflight_rejects_changed_unlisted_and_existing_pending_files(self) -> None:
        first, second, document = self.linked_history()
        next_entry = self.entry("SPEC-006", first["path"], b"staged\n",
                                predecessor=second, materialize=False)
        proposed = {**document, "entries": [first, second, next_entry]}
        before = self.snapshot()
        for pending in (
            {next_entry["file"]: b"short"},
            {next_entry["file"]: b"STAGED\n"},
            {next_entry["file"]: b"staged\n", "unlisted": b"surprise"},
            {next_entry["file"]: b"staged\n", first["file"]: b"prior correction\n"},
        ):
            with self.subTest(pending=pending):
                with self.assertRaises(tool.SpecificationError):
                    tool.validate_corrections(self.root, self.original, proposed, pending=pending)
                self.assertEqual(self.snapshot(), before)

    def test_staged_file_directory_collisions_are_rejected_in_either_order(self) -> None:
        first = self.entry("SPEC-001", "new/parent", b"parent\n", materialize=False)
        second = self.entry("SPEC-001", "new/parent/child", b"child\n", materialize=False)
        pending = {first["file"]: b"parent\n", second["file"]: b"child\n"}
        before = self.snapshot()
        for entries in ([first, second], [second, first]):
            proposed = {
                "schemaVersion": 2, "baselineManifestSha256": self.scope["baselineManifestSha256"],
                "entries": entries,
            }
            with self.subTest(entries=entries):
                with self.assertRaisesRegex(tool.SpecificationError, "collision"):
                    tool.validate_corrections(self.root, self.original, proposed, pending=pending)
                self.assertEqual(self.snapshot(), before)
                self.assertFalse((self.root / tool.CORRECTED_SOURCES).exists())


class CorrectionCaptureTests(CorrectionFixture):
    def test_first_capture_links_original_and_new_file(self) -> None:
        files = {"core/example.md": b"# Contract\n\nA reader MUST validate bytes.\n",
                 "tools/new_helper.py": b""}
        self.inputs(files)
        originals = self.snapshot()
        status, _, stderr = self.run_capture("SPEC-001", list(files))

        self.assertEqual((status, stderr), (0, ""))
        document = tool.read_json(self.root / tool.CORRECTIONS)
        self.assertEqual(set(document), {"schemaVersion", "baselineManifestSha256", "entries"})
        self.assertEqual(document["schemaVersion"], 2)
        self.assertEqual(document["baselineManifestSha256"], self.scope["baselineManifestSha256"])
        self.assertEqual([entry["path"] for entry in document["entries"]], list(files))
        for entry in document["entries"]:
            path = entry["path"]
            original_hash = self.original[path]["sha256"] if path in self.original else None
            expected = {
                "case": "SPEC-001", "path": path,
                "originalSha256": original_hash, "predecessorSha256": original_hash,
                "predecessorCase": None, "sha256": digest(files[path]), "bytes": len(files[path]),
                "file": f"tests/Conformance/Corrections/SPEC-001/{path}",
                "sourceCommitBeforeCorrections": "d" * 40,
                "reason": "Reviewed fixture correction.",
                "testIds": ["Synthetic.Capture.NotConformanceEvidence"],
            }
            self.assertEqual(entry, expected)
            self.assertEqual(
                tool.active_source(self.root, path, self.original),
                (files[path], digest(files[path])),
            )
        for path, data in originals.items():
            self.assertEqual((self.root / path).read_bytes(), data)
        self.assertFalse((self.root / tool.CORPUS / "tools" / "new_helper.py").exists())

    def test_successor_migrates_all_legacy_entries_and_preserves_prior_evidence(self) -> None:
        first = self.entry("SPEC-004", "tools/federation_examples.py", b"retained SPEC004\n", legacy=True)
        other = self.entry("SPEC-001", "tools/another.py", b"other retained correction\n", legacy=True)
        self.history([first, other], version=1)
        before = self.snapshot()
        self.inputs({first["path"]: b"successor SPEC005\n"})
        status, _, stderr = self.run_capture("SPEC-005", [first["path"]])

        self.assertEqual((status, stderr), (0, ""))
        document = tool.read_json(self.root / tool.CORRECTIONS)
        self.assertEqual(document["schemaVersion"], 2)
        self.assertEqual(document["entries"][:-1], [
            {**entry, "predecessorSha256": entry["originalSha256"], "predecessorCase": None}
            for entry in (first, other)
        ])
        successor = document["entries"][-1]
        self.assertEqual(
            (successor["case"], successor["path"], successor["predecessorSha256"],
             successor["predecessorCase"], successor["originalSha256"]),
            ("SPEC-005", first["path"], first["sha256"], "SPEC-004", first["originalSha256"]),
        )
        self.assertEqual(set(self.active()), {first["path"], other["path"]})
        self.assertEqual(self.active()[first["path"]], successor)
        self.assertEqual(
            tool.active_source(self.root, first["path"], self.original),
            (b"successor SPEC005\n", digest(b"successor SPEC005\n")),
        )
        for path, data in before.items():
            if path != tool.CORRECTIONS.as_posix():
                self.assertEqual((self.root / path).read_bytes(), data)

    def test_successor_appends_to_v2_without_reordering_existing_entries(self) -> None:
        first, second, _ = self.linked_history()
        other = self.entry("SPEC-001", "tools/another.py", b"unrelated\n")
        document = self.history([first, other, second])
        self.inputs({first["path"]: b"next correction\n"})
        status, _, stderr = self.run_capture("SPEC-006", [first["path"]])

        self.assertEqual((status, stderr), (0, ""))
        entries = tool.read_json(self.root / tool.CORRECTIONS)["entries"]
        self.assertEqual(entries[:-1], document["entries"])
        self.assertEqual(
            (entries[-1]["predecessorSha256"], entries[-1]["predecessorCase"]),
            (second["sha256"], "SPEC-005"),
        )
        self.assertEqual(self.active()[first["path"]], entries[-1])

    def test_added_file_successors_keep_a_null_original_hash(self) -> None:
        first = self.entry("SPEC-004", "tools/new_helper.py", b"new file\n")
        self.history([first])
        self.inputs({first["path"]: b"corrected new file\n"})
        status, _, stderr = self.run_capture("SPEC-005", [first["path"]])

        self.assertEqual((status, stderr), (0, ""))
        active = self.active()[first["path"]]
        self.assertIsNone(active["originalSha256"])
        self.assertEqual((active["predecessorSha256"], active["predecessorCase"]),
                         (first["sha256"], "SPEC-004"))
        self.assertEqual((self.root / first["file"]).read_bytes(), b"new file\n")
        self.assertFalse((self.root / tool.CORPUS / first["path"]).exists())

    def test_same_bytes_recapture_is_a_manifest_and_artifact_noop(self) -> None:
        for version in (1, 2):
            with self.subTest(version=version):
                first = self.entry("SPEC-004", "tools/federation_examples.py", b"retained\n",
                                   legacy=version == 1)
                self.history([first], version)
                self.inputs({first["path"]: b"retained\n"})
                before = self.snapshot()
                status, _, stderr = self.run_capture(
                    "SPEC-004", [first["path"]], reason="Must not replace the retained reason.",
                    tests=("Synthetic.DifferentMetadata",), revision="e" * 40,
                )
                self.assertEqual((status, stderr), (0, ""))
                self.assertEqual(self.snapshot(), before)
                self.assertEqual(self.active()[first["path"]], first)

    def test_changed_same_case_fails_without_replacing_manifest_or_artifact(self) -> None:
        for version in (1, 2):
            with self.subTest(version=version):
                first = self.entry("SPEC-004", "tools/federation_examples.py", b"retained\n",
                                   legacy=version == 1)
                self.history([first], version)
                self.inputs({first["path"]: b"different\n"})
                before = self.snapshot()
                status, stdout, stderr = self.run_capture("SPEC-004", [first["path"]])
                self.assertEqual(status, 1)
                self.assertEqual(stdout, "")
                self.assertRegex(stderr, "different bytes.*new case")
                self.assertEqual(self.snapshot(), before)

    def test_new_case_with_identical_content_still_links_distinct_provenance(self) -> None:
        first = self.entry("SPEC-004", "tools/federation_examples.py", b"retained\n")
        self.history([first])
        old_manifest = (self.root / tool.CORRECTIONS).read_bytes()
        self.inputs({first["path"]: b"retained\n"})
        status, _, stderr = self.run_capture("SPEC-005", [first["path"]])

        self.assertEqual((status, stderr), (0, ""))
        self.assertNotEqual((self.root / tool.CORRECTIONS).read_bytes(), old_manifest)
        active = self.active()[first["path"]]
        self.assertEqual(active["sha256"], first["sha256"])
        self.assertEqual((active["case"], active["predecessorCase"]), ("SPEC-005", "SPEC-004"))

    def test_historical_case_recapture_cannot_downgrade_the_active_head(self) -> None:
        first, second, _ = self.linked_history()
        self.inputs({first["path"]: b"prior correction\n"})
        before = self.snapshot()
        status, _, stderr = self.run_capture("SPEC-004", [first["path"]])

        self.assertEqual(status, 1)
        self.assertIn("advance", stderr)
        self.assertEqual(self.snapshot(), before)
        self.assertEqual(self.active()[first["path"]], second)

    def test_partial_multiple_input_failure_precedes_all_writes(self) -> None:
        first = self.entry("SPEC-004", "tools/federation_examples.py", b"retained\n", legacy=True)
        self.history([first], version=1)
        valid = "tools/first_new.py"
        self.inputs({valid: b"new\n", first["path"]: b"different bytes\n",
                     "tools/opcua.py": b"excluded\n", "core/example.md": b"case alias\n"})
        before = self.snapshot()
        for paths in (
            [valid, "tools/missing.py"],
            [valid, "tools/opcua.py"],
            [valid, "../outside"],
            [valid, "CORE/example.md"],
            [valid, first["path"]],
        ):
            with self.subTest(paths=paths):
                status, stdout, stderr = self.run_capture("SPEC-004", paths)
                self.assertEqual(status, 1)
                self.assertEqual(stdout, "")
                self.assertIn("Correction capture failed:", stderr)
                self.assertEqual(self.snapshot(), before)
                self.assertFalse((self.root / tool.CORRECTED_SOURCES / "SPEC-004" / valid).exists())

    def test_invalid_capture_metadata_fails_before_any_writes(self) -> None:
        first = self.entry("SPEC-004", "tools/federation_examples.py", b"retained\n", legacy=True)
        self.history([first], version=1)
        self.inputs({"tools/new_helper.py": b"new\n"})
        before = self.snapshot()
        for arguments in (
            {"reason": " \t"},
            {"tests": ("",)},
            {"tests": ("duplicate", "duplicate")},
            {"revision": "not-a-commit"},
        ):
            with self.subTest(arguments=arguments):
                status, stdout, stderr = self.run_capture("SPEC-005", ["tools/new_helper.py"], **arguments)
                self.assertEqual(status, 1)
                self.assertEqual(stdout, "")
                self.assertIn("Correction capture failed:", stderr)
                self.assertEqual(self.snapshot(), before)
                self.assertFalse((self.root / tool.CORRECTED_SOURCES / "SPEC-005").exists())

    def test_duplicate_capture_paths_are_rejected_after_normalization(self) -> None:
        self.inputs({"tools/new_helper.py": b"new\n"})
        before = self.snapshot()
        for alias in ("tools/new_helper.py", "tools\\new_helper.py", "TOOLS/new_helper.py"):
            with self.subTest(alias=alias):
                status, _, stderr = self.run_capture("SPEC-001", ["tools/new_helper.py", alias])
                self.assertEqual(status, 1)
                self.assertRegex(stderr, "[Dd]uplicate")
                self.assertEqual(self.snapshot(), before)

    def test_late_destination_directory_conflict_fails_before_first_artifact_write(self) -> None:
        self.inputs({"new/first.py": b"first\n", "new/second.py": b"second\n"})
        conflict = self.root / tool.CORRECTED_SOURCES / "SPEC-001" / "new" / "second.py"
        conflict.mkdir(parents=True)
        before = self.snapshot()
        status, stdout, stderr = self.run_capture("SPEC-001", ["new/first.py", "new/second.py"])

        self.assertEqual(status, 1)
        self.assertEqual(stdout, "")
        self.assertIn("Correction capture failed:", stderr)
        self.assertEqual(self.snapshot(), before)
        self.assertFalse((conflict.parent / "first.py").exists())
        self.assertTrue(conflict.is_dir())

    def test_input_symlink_traversal_fails_without_writing_corrections(self) -> None:
        self.inputs({"tools/new_helper.py": b"new\n"})
        before = self.snapshot()
        for link in (self.checkout, self.checkout / "tools", self.checkout / "tools" / "new_helper.py"):
            with self.subTest(link=link), \
                    mock.patch.object(Path, "is_symlink", autospec=True,
                                      side_effect=lambda candidate: candidate == link):
                status, _, stderr = self.run_capture("SPEC-001", ["tools/new_helper.py"])
                self.assertEqual(status, 1)
                self.assertIn("link", stderr)
                self.assertEqual(self.snapshot(), before)

    def test_capture_checks_inactive_history_before_accepting_new_inputs(self) -> None:
        first, _, _ = self.linked_history()
        self.inputs({"tools/new_helper.py": b"new\n"})
        (self.root / first["file"]).write_bytes(b"tampered\n")
        before = self.snapshot()
        status, _, stderr = self.run_capture("SPEC-006", ["tools/new_helper.py"])

        self.assertEqual(status, 1)
        self.assertIn("missing or changed", stderr)
        self.assertEqual(self.snapshot(), before)
        self.assertFalse((self.root / tool.CORRECTED_SOURCES / "SPEC-006").exists())

    def test_input_byte_limit_accepts_boundary_and_rejects_one_more_without_writes(self) -> None:
        self.inputs({"tools/new_helper.py": b"a" * 4096})
        with mock.patch.object(tool, "MAX_JSON_BYTES", 4096):
            status, _, stderr = self.run_capture("SPEC-004", ["tools/new_helper.py"])
            self.assertEqual((status, stderr), (0, ""))
            self.assertEqual(self.active()["tools/new_helper.py"]["bytes"], 4096)
            self.inputs({"tools/new_helper.py": b"b" * 4097})
            before = self.snapshot()
            status, _, stderr = self.run_capture("SPEC-005", ["tools/new_helper.py"])
            self.assertEqual(status, 1)
            self.assertIn("input byte limit exceeded", stderr)
            self.assertEqual(self.snapshot(), before)

    def test_manifest_byte_limit_failure_precedes_artifact_creation(self) -> None:
        self.inputs({"tools/new_helper.py": b"new\n"})
        before = self.snapshot()
        with mock.patch.object(tool, "MAX_JSON_BYTES", 4096):
            status, _, stderr = self.run_capture("SPEC-001", ["tools/new_helper.py"], reason="r" * 4096)
        self.assertEqual(status, 1)
        self.assertIn("manifest byte limit exceeded", stderr)
        self.assertEqual(self.snapshot(), before)
        self.assertFalse((self.root / tool.CORRECTED_SOURCES).exists())

    def test_concurrent_manifest_change_is_not_overwritten(self) -> None:
        first = self.entry("SPEC-004", "tools/federation_examples.py", b"retained\n", legacy=True)
        self.history([first], version=1)
        self.inputs({first["path"]: b"successor\n"})
        manifest = self.root / tool.CORRECTIONS
        concurrent_bytes = manifest.read_bytes() + b" "
        with mock.patch.object(capture.os, "fsync",
                               side_effect=lambda descriptor: manifest.write_bytes(concurrent_bytes)):
            status, _, stderr = self.run_capture("SPEC-005", [first["path"]])

        self.assertEqual(status, 1)
        self.assertIn("manifest changed during capture", stderr)
        self.assertEqual(manifest.read_bytes(), concurrent_bytes)
        self.assertEqual((self.root / first["file"]).read_bytes(), b"retained\n")
        self.assertFalse((self.root / tool.CORRECTED_SOURCES / "SPEC-005" / first["path"]).exists())
        self.assertEqual(self.active()[first["path"]], first)

    def test_failed_manifest_publication_removes_only_new_artifacts(self) -> None:
        first = self.entry("SPEC-004", "tools/federation_examples.py", b"retained\n", legacy=True)
        self.history([first], version=1)
        self.inputs({first["path"]: b"successor\n", "tools/new_helper.py": b"new\n"})
        before = self.snapshot()
        with mock.patch.object(tool.os, "replace", side_effect=OSError("synthetic publication failure")):
            status, _, stderr = self.run_capture("SPEC-005", [first["path"], "tools/new_helper.py"])

        self.assertEqual(status, 1)
        self.assertIn("synthetic publication failure", stderr)
        self.assertEqual(self.snapshot(), before)
        self.assertEqual(self.active()[first["path"]], first)


class CorrectionLedgerTests(CorrectionFixture):
    def reviewed_ledger(self, *, semantic: bool, review_status: str) -> tuple[dict, dict]:
        first = self.entry("SPEC-004", "tools/federation_examples.py", b"retained\n")
        self.history([first])
        ledger = tool.generate_ledger(self.root, self.lock)
        ledger["semanticCoverageReviewed"] = semantic
        ledger["requirements"][0]["review"] = {
            "status": review_status, "note": "Synthetic review state, not conformance evidence.",
            "roles": ["client"],
        }
        self.write_json(tool.LEDGER, ledger)
        return first, ledger

    def test_history_head_drives_ledger_without_creating_qualification_claims(self) -> None:
        first = self.entry("SPEC-004", "core/example.md", self.sources["core/example.md"])
        second = self.entry("SPEC-005", first["path"],
                            b"# Contract\n\nA reader MUST reject tampered evidence.\n", predecessor=first)
        self.history([first, second])
        ledger = tool.generate_ledger(self.root, self.lock)

        self.assertEqual(len(ledger["requirements"]), 1)
        row = ledger["requirements"][0]
        self.assertEqual(row["text"], "A reader MUST reject tampered evidence.")
        self.assertEqual(row["source"]["sha256"], second["sha256"])
        self.assertEqual(row["review"], {"status": "unreviewed", "note": "", "roles": []})
        self.assertEqual(row["implementation"], {"status": "pending", "testIds": [], "nativeEvidence": []})
        self.assertIs(ledger["semanticCoverageReviewed"], False)
        self.assertEqual(ledger["correctionsSha256"], digest((self.root / tool.CORRECTIONS).read_bytes()))

    def test_changed_history_invalidates_review_in_a_pending_ledger(self) -> None:
        for review_status in ("reviewed", "informative"):
            with self.subTest(review_status=review_status):
                first, _ = self.reviewed_ledger(semantic=False, review_status=review_status)
                before = (self.root / tool.LEDGER).read_bytes()
                self.inputs({first["path"]: b"retained\n"})
                status, _, stderr = self.run_capture("SPEC-005", [first["path"]])
                self.assertEqual((status, stderr), (0, ""))
                with self.assertRaisesRegex(tool.SpecificationError, "repeat.*review"):
                    tool.generate_ledger(self.root, self.lock)
                self.assertEqual((self.root / tool.LEDGER).read_bytes(), before)
                (self.root / tool.CORRECTED_SOURCES / "SPEC-005" / first["path"]).unlink()
                (self.root / tool.LEDGER).unlink()

    def test_manifest_only_history_change_invalidates_completed_semantic_review(self) -> None:
        first, _ = self.reviewed_ledger(semantic=True, review_status="reviewed")
        before = (self.root / tool.LEDGER).read_bytes()
        self.inputs({first["path"]: b"retained\n"})
        status, _, stderr = self.run_capture("SPEC-005", [first["path"]])

        self.assertEqual((status, stderr), (0, ""))
        with self.assertRaisesRegex(tool.SpecificationError, "repeat semantic coverage review"):
            tool.generate_ledger(self.root, self.lock)
        self.assertEqual((self.root / tool.LEDGER).read_bytes(), before)

    def test_unreviewed_ledger_requires_regeneration_after_history_changes(self) -> None:
        first = self.entry("SPEC-004", "tools/federation_examples.py", b"retained\n")
        self.history([first])
        status, _, stderr = self.run_manage("generate")
        self.assertEqual((status, stderr), (0, ""))
        old_ledger = (self.root / tool.LEDGER).read_bytes()
        old_report = (self.root / tool.REPORT).read_bytes()
        old_rows = tool.read_json(self.root / tool.LEDGER)["requirements"]
        self.inputs({first["path"]: b"retained\n"})
        status, _, stderr = self.run_capture("SPEC-005", [first["path"]])
        self.assertEqual((status, stderr), (0, ""))

        status, _, stderr = self.run_manage("check")
        self.assertEqual(status, 1)
        self.assertIn("stale", stderr)
        self.assertEqual((self.root / tool.LEDGER).read_bytes(), old_ledger)
        self.assertEqual((self.root / tool.REPORT).read_bytes(), old_report)
        status, _, stderr = self.run_manage("generate")
        self.assertEqual((status, stderr), (0, ""))
        ledger = tool.read_json(self.root / tool.LEDGER)
        self.assertEqual(ledger["requirements"], old_rows)
        self.assertIs(ledger["semanticCoverageReviewed"], False)
        self.assertEqual(ledger["correctionsSha256"], digest((self.root / tool.CORRECTIONS).read_bytes()))
        self.assertNotEqual((self.root / tool.LEDGER).read_bytes(), old_ledger)
        status, _, stderr = self.run_manage("check")
        self.assertEqual((status, stderr), (0, ""))
        with self.assertRaisesRegex(tool.SpecificationError, "Semantic coverage review is incomplete"):
            tool.release_check(self.root, ledger)


if __name__ == "__main__":
    unittest.main()
