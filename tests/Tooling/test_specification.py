# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

from __future__ import annotations

import importlib.util
from pathlib import Path
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "specification_tool", ROOT / "eng" / "specification" / "manage.py"
)
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(tool)


class RequirementExtractionTests(unittest.TestCase):
    def test_sha256_matches_independent_empty_digest(self) -> None:
        self.assertEqual(
            tool.sha256(b""),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        )

    def test_multiline_normative_prose_retains_exact_source_lines(self) -> None:
        text = "# Contract\n\nClients MUST retain\nexact document bytes.\n\n"
        rows = tool.extract_requirements("core/example.md", text, "a" * 64, "core")
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["text"], "Clients MUST retain exact document bytes.")
        self.assertEqual(rows[0]["strengths"], ["MUST"])
        self.assertEqual(rows[0]["source"]["firstLine"], 3)
        self.assertEqual(rows[0]["source"]["lastLine"], 4)
        self.assertEqual(rows[0]["source"]["section"], "Contract")

    def test_fenced_examples_and_comments_are_not_normative_statements(self) -> None:
        text = (
            "# Contract\n\n```json\n{\"MUST\":\"example\"}\n```\n\n"
            "<!-- MUST NOT count -->\n\nA reader SHOULD verify size.\n"
        )
        rows = tool.extract_requirements("core/example.md", text, "b" * 64, "core")
        self.assertEqual([row["text"] for row in rows], ["A reader SHOULD verify size."])

    def test_table_rows_and_repeated_prose_get_distinct_stable_ids(self) -> None:
        text = (
            "# Contract\n\n| Operation | Rule |\n| --- | --- |\n"
            "| Read | MUST preserve bytes |\n\nA reader MAY cache.\n\nA reader MAY cache.\n"
        )
        rows = tool.extract_requirements("core/example.md", text, "c" * 64, "core")
        self.assertEqual(len(rows), 3)
        self.assertEqual(len({row["id"] for row in rows}), 3)
        shifted = tool.extract_requirements(
            "core/example.md", "\n" + text, "d" * 64, "core"
        )
        self.assertEqual([row["id"] for row in rows], [row["id"] for row in shifted])
        self.assertEqual(rows[0]["strengths"], ["MUST"])
        self.assertEqual(rows[1]["strengths"], ["MAY"])

    def test_negated_strength_is_not_split_into_positive_strength(self) -> None:
        text = "# Contract\n\nA consumer MUST NOT retry and SHOULD NOT guess.\n"
        rows = tool.extract_requirements("example.md", text, "d" * 64, "core")
        self.assertEqual(rows[0]["strengths"], ["MUST NOT", "SHOULD NOT"])

    def test_unclosed_fence_or_comment_rejects_ambiguous_extraction(self) -> None:
        for text in ("# Contract\n```\nMUST be an example\n", "<!-- MUST stay hidden\n"):
            with self.subTest(text=text), self.assertRaises(tool.SpecificationError):
                tool.extract_requirements("example.md", text, "a" * 64, "core")


class ProvenanceAndReviewTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
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
        self.files = {
            "LICENSE": b"Independent fixture license.\n",
            "core/example.md": b"# Contract\n\nA reader MUST preserve exact bytes.\n",
        }
        self.lock = {
            **{key: value for key, value in self.scope.items() if key != "excludeSuffixes"},
            "corpusPath": tool.CORPUS.as_posix(),
            "fileCount": len(self.files),
            "files": [
                {"path": path, "bytes": len(data), "sha256": tool.sha256(data)}
                for path, data in self.files.items()
            ],
            "excluded": [],
        }
        for path, data in self.files.items():
            destination = self.root / tool.CORPUS / path
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)
        self.write_json(Path("eng/specification/scope.json"), self.scope)
        self.write_json(tool.LOCK, self.lock)

    def write_json(self, relative: Path, document) -> None:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(tool.encoded(document))

    def test_corpus_verifies_and_modified_bytes_are_rejected(self) -> None:
        self.assertEqual(tool.verify_corpus(self.root)["fileCount"], 2)
        path = self.root / tool.CORPUS / "core/example.md"
        path.write_bytes(path.read_bytes().replace(b"bytes", b"bytex"))
        with self.assertRaisesRegex(tool.SpecificationError, "hash mismatch"):
            tool.verify_corpus(self.root)

    def test_import_is_idempotent_and_refuses_overwriting_changed_evidence(self) -> None:
        snapshot = self.root / "snapshot"
        receiver = self.root / "receiver"
        for path, data in self.files.items():
            target = snapshot / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        baseline = {
            "commit": self.scope["commit"],
            "branch": "main",
            "fileCount": len(self.files),
            "files": self.lock["files"],
        }
        raw_manifest = tool.encoded(baseline)
        (snapshot / "baseline-manifest.json").write_bytes(raw_manifest)
        scope = {**self.scope, "baselineManifestSha256": tool.sha256(raw_manifest)}
        scope_file = receiver / "eng" / "specification" / "scope.json"
        scope_file.parent.mkdir(parents=True)
        scope_file.write_bytes(tool.encoded(scope))
        tool.import_snapshot(receiver, snapshot)
        self.assertEqual(tool.verify_corpus(receiver)["fileCount"], 2)
        tool.import_snapshot(receiver, snapshot)
        target = receiver / tool.CORPUS / "core/example.md"
        target.write_bytes(b"changed evidence")
        with self.assertRaisesRegex(tool.SpecificationError, "Refusing to overwrite"):
            tool.import_snapshot(receiver, snapshot)
        self.assertEqual(target.read_bytes(), b"changed evidence")

    def correction(self, data: bytes) -> dict:
        path = "core/example.md"
        entry = {
            "case": "SPEC-001",
            "path": path,
            "originalSha256": self.lock["files"][1]["sha256"],
            "sha256": tool.sha256(data),
            "bytes": len(data),
            "file": "tests/Conformance/Corrections/SPEC-001/core/example.md",
            "sourceCommitBeforeCorrections": "c" * 40,
            "reason": "Independently demonstrated correction.",
            "testIds": ["ARealRegressionCase"],
        }
        target = self.root / entry["file"]
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        self.write_json(tool.CORRECTIONS, {
            "schemaVersion": 1,
            "baselineManifestSha256": self.scope["baselineManifestSha256"],
            "entries": [entry],
        })
        return entry

    def test_explicit_correction_preserves_original_and_selects_verified_successor(self) -> None:
        data = b"# Contract\n\nA reader MUST reject inconsistent bytes.\n"
        self.correction(data)
        verified = tool.verify_corpus(self.root)
        active, digest = tool.active_source(self.root, "core/example.md", tool.source_records(verified))
        self.assertEqual(active, data)
        self.assertEqual(digest, tool.sha256(data))
        self.assertEqual((self.root / tool.CORPUS / "core/example.md").read_bytes(), self.files["core/example.md"])
        ledger = tool.generate_ledger(self.root, verified)
        self.assertEqual(ledger["requirements"][0]["text"], "A reader MUST reject inconsistent bytes.")
        self.assertEqual(ledger["requirements"][0]["source"]["sha256"], digest)

    def test_correction_requires_original_hash_and_unchanged_successor_bytes(self) -> None:
        entry = self.correction(b"# Changed\n\nA reader MUST check.\n")
        manifest = tool.read_json(self.root / tool.CORRECTIONS)
        manifest["entries"][0]["originalSha256"] = "f" * 64
        self.write_json(tool.CORRECTIONS, manifest)
        with self.assertRaisesRegex(tool.SpecificationError, "original hash"):
            tool.verify_corpus(self.root)
        manifest["entries"][0]["originalSha256"] = entry["originalSha256"]
        self.write_json(tool.CORRECTIONS, manifest)
        (self.root / entry["file"]).write_bytes(b"tampered")
        with self.assertRaisesRegex(tool.SpecificationError, "missing or changed"):
            tool.verify_corpus(self.root)

    def test_changed_artifact_baseline_cannot_reuse_a_completed_semantic_review(self) -> None:
        ledger = tool.generate_ledger(self.root, self.lock)
        ledger["semanticCoverageReviewed"] = True
        self.write_json(tool.LEDGER, ledger)
        self.correction(self.files["core/example.md"])
        with self.assertRaisesRegex(tool.SpecificationError, "repeat semantic coverage review"):
            tool.generate_ledger(self.root, self.lock)

    def test_missing_unlisted_and_wrong_revision_sources_are_rejected(self) -> None:
        extra = self.root / tool.CORPUS / "extra.json"
        extra.write_text("{}")
        with self.assertRaisesRegex(tool.SpecificationError, "unlisted"):
            tool.verify_corpus(self.root)
        extra.unlink()
        self.lock["commit"] = "f" * 40
        self.write_json(tool.LOCK, self.lock)
        with self.assertRaisesRegex(tool.SpecificationError, "lock/scope mismatch"):
            tool.verify_corpus(self.root)
        self.lock["commit"] = self.scope["commit"]
        self.write_json(tool.LOCK, self.lock)
        (self.root / tool.CORPUS / "LICENSE").unlink()
        with self.assertRaisesRegex(tool.SpecificationError, "Missing source"):
            tool.verify_corpus(self.root)

    def test_source_paths_reject_absolute_traversal_and_ambiguous_components(self) -> None:
        for value in ("../outside", "/outside", "C:\\outside", "a//b", "a/./b", "a/b:ads", "a/b."):
            with self.subTest(path=value), self.assertRaises(tool.SpecificationError):
                tool.contained(self.root, value)
        self.assertEqual(tool.contained(self.root, "core\\example.md"), self.root / "core" / "example.md")

    def test_record_validation_rejects_duplicate_excluded_wrong_hash_and_boolean_count(self) -> None:
        record = {"path": "core/example.md", "bytes": 1, "sha256": "a" * 64}
        for data in (
            {"fileCount": True, "files": [record]},
            {"fileCount": 2, "files": [record, {**record, "path": "CORE/example.md"}]},
            {"fileCount": 1, "files": [{**record, "path": "bindings/opcua.md"}]},
            {"fileCount": 1, "files": [{**record, "sha256": "invalid"}]},
            {"fileCount": 1, "files": [{**record, "bytes": True}]},
        ):
            with self.subTest(data=data), self.assertRaises(tool.SpecificationError):
                tool.source_records(data)

    def test_generation_preserves_review_and_explicit_semantic_review_state(self) -> None:
        ledger = tool.generate_ledger(self.root, self.lock)
        row = ledger["requirements"][0]
        row["review"] = {"status": "reviewed", "note": "Public reader preserves exact bytes.", "roles": ["client"]}
        ledger["semanticCoverageReviewed"] = True
        self.write_json(tool.LEDGER, ledger)
        generated = tool.generate_ledger(self.root, self.lock)
        self.assertEqual(generated["requirements"][0]["review"], row["review"])
        self.assertIs(generated["semanticCoverageReviewed"], True)

    def test_removed_ids_and_changed_reviewed_sources_require_explicit_review(self) -> None:
        ledger = tool.generate_ledger(self.root, self.lock)
        original = ledger["requirements"][0]["id"]
        ledger["requirements"][0]["id"] = "no-longer-present"
        self.write_json(tool.LEDGER, ledger)
        with self.assertRaisesRegex(tool.SpecificationError, "disappeared"):
            tool.generate_ledger(self.root, self.lock)
        row = ledger["requirements"][0]
        row["id"] = original
        row["review"] = {"status": "reviewed", "note": "Reviewed before drift.", "roles": ["client"]}
        row["source"]["sha256"] = "c" * 64
        self.write_json(tool.LEDGER, ledger)
        with self.assertRaisesRegex(tool.SpecificationError, "Reviewed source changed"):
            tool.generate_ledger(self.root, self.lock)

    def test_wrong_review_discriminators_and_unsupported_claims_fail_explicitly(self) -> None:
        row = tool.generate_ledger(self.root, self.lock)["requirements"][0]
        for status in ("passed", [], None, True):
            row["review"]["status"] = status
            with self.subTest(status=status), self.assertRaises(tool.SpecificationError):
                tool.validate_review(row)
        row["review"]["status"] = "unreviewed"
        row["implementation"]["status"] = "implemented"
        row["implementation"]["testIds"] = ["ActualTest"]
        with self.assertRaisesRegex(tool.SpecificationError, "reviewed tests"):
            tool.validate_review(row)

    def test_check_mode_rejects_stale_output_without_overwriting_it(self) -> None:
        path = self.root / "output.json"
        tool.write_output(path, b"original")
        with self.assertRaisesRegex(tool.SpecificationError, "stale"):
            tool.write_output(path, b"changed", check=True)
        self.assertEqual(path.read_bytes(), b"original")
        tool.write_output(path, b"original", check=True)

    def test_release_requires_semantic_review_nonempty_requirements_and_qualified_evidence(self) -> None:
        ledger = tool.generate_ledger(self.root, self.lock)
        with self.assertRaisesRegex(tool.SpecificationError, "Semantic"):
            tool.release_check(self.root, ledger)
        ledger["semanticCoverageReviewed"] = True
        with self.assertRaisesRegex(tool.SpecificationError, "not qualified"):
            tool.release_check(self.root, ledger)
        with self.assertRaises(tool.SpecificationError):
            tool.release_check(self.root, {**ledger, "requirements": []})

    def test_accounting_summarizes_review_implementation_and_evidence_gaps(self) -> None:
        ledger = tool.generate_ledger(self.root, self.lock)
        row = ledger["requirements"][0]
        row["review"] = {"status": "reviewed", "note": "Fixture reviewed.", "roles": ["client"]}
        row["implementation"]["status"] = "implemented"
        row["implementation"]["testIds"] = ["ReaderPreservesExactBytes"]

        summary = tool.accounting(ledger, self.lock)

        self.assertEqual(summary["pinnedFiles"], 2)
        self.assertEqual(summary["normativeDocuments"], 1)
        self.assertFalse(summary["semanticCoverageReviewed"])
        self.assertEqual(summary["totals"]["requirements"], 1)
        self.assertEqual(summary["totals"]["dispositioned"], 1)
        self.assertEqual(summary["totals"]["reviewed"], 1)
        self.assertEqual(summary["totals"]["implemented"], 1)
        self.assertEqual(summary["totals"]["qualified"], 0)
        self.assertEqual(summary["totals"]["testMapped"], 1)
        self.assertEqual(summary["totals"]["nativeEvidenceMapped"], 0)
        self.assertEqual(summary["totals"]["qualificationGaps"], 1)
        self.assertEqual(summary["byOwner"]["core"]["qualificationGaps"], 1)

    def test_alpha_bypasses_semantic_coverage_and_per_requirement_qualification(self) -> None:
        ledger = tool.generate_ledger(self.root, self.lock)
        row = ledger["requirements"][0]
        row["review"] = {"status": "reviewed", "note": "Alpha fixture, not yet fully qualified.", "roles": ["client"]}
        row["implementation"]["status"] = "implemented"
        row["implementation"]["testIds"] = ["ActualTest"]
        self.assertIs(ledger.get("semanticCoverageReviewed"), False)
        with self.assertRaisesRegex(tool.SpecificationError, "Semantic"):
            tool.release_check(self.root, ledger)
        with self.assertRaisesRegex(tool.SpecificationError, "not qualified"):
            tool.release_check(self.root, {**ledger, "semanticCoverageReviewed": True})
        tool.release_check(self.root, ledger, alpha=True)

    def test_alpha_does_not_relax_a_nonempty_requirement_inventory_or_row_structure(self) -> None:
        ledger = tool.generate_ledger(self.root, self.lock)
        with self.assertRaises(tool.SpecificationError):
            tool.release_check(self.root, {**ledger, "requirements": []}, alpha=True)
        row = ledger["requirements"][0]
        row["review"]["status"] = "not-a-real-status"
        with self.assertRaises(tool.SpecificationError):
            tool.release_check(self.root, ledger, alpha=True)

    def test_source_version_is_alpha_reads_the_given_root_and_fails_closed(self) -> None:
        self.assertFalse(tool.source_version_is_alpha(self.root))
        self.write_json(tool.VERSION_FILE, {"version": "0.1.0-alpha"})
        self.assertTrue(tool.source_version_is_alpha(self.root))
        self.write_json(tool.VERSION_FILE, {"version": "1.0.0-rc9"})
        self.assertFalse(tool.source_version_is_alpha(self.root))
        (self.root / tool.VERSION_FILE).write_bytes(b"{not json")
        self.assertFalse(tool.source_version_is_alpha(self.root))

    def test_arbitrary_hashed_files_do_not_prove_native_execution(self) -> None:
        ledger = tool.generate_ledger(self.root, self.lock)
        ledger["semanticCoverageReviewed"] = True
        row = ledger["requirements"][0]
        row["review"] = {"status": "reviewed", "note": "Checked contract.", "roles": ["client"]}
        row["implementation"]["status"] = "qualified"
        row["implementation"]["testIds"] = ["ReaderPreservesExactBytes"]
        report = b'{"not":"native test evidence"}'
        (self.root / "receipt.json").write_bytes(report)
        row["implementation"]["nativeEvidence"] = [
            {
                "framework": framework,
                "rid": rid,
                "report": "receipt.json",
                "sha256": tool.sha256(report),
            }
            for framework in tool.FRAMEWORKS
            for rid in tool.RIDS
        ]
        with self.assertRaisesRegex(tool.SpecificationError, "receipt"):
            tool.release_check(self.root, ledger)

    def test_release_checks_each_receipt_tfm_rid_tests_and_failure_counts(self) -> None:
        ledger = tool.generate_ledger(self.root, self.lock)
        ledger["semanticCoverageReviewed"] = True
        row = ledger["requirements"][0]
        row["review"] = {"status": "reviewed", "note": "Fixture reviewed.", "roles": ["client"]}
        row["implementation"]["status"] = "qualified"
        row["implementation"]["testIds"] = ["ReaderPreservesExactBytes"]
        for framework in ("net8.0", "net10.0"):
            for rid in ("win-x64", "win-arm64", "linux-x64", "linux-arm64"):
                receipt = {
                    "schemaVersion": 1,
                    "framework": framework,
                    "rid": rid,
                    "nativeExecutable": True,
                    "passedTestIds": ["ReaderPreservesExactBytes"],
                    "failedTestIds": [],
                    "skippedTestIds": [],
                }
                path = f"{framework}-{rid}.json"
                data = tool.encoded(receipt)
                (self.root / path).write_bytes(data)
                row["implementation"]["nativeEvidence"].append(
                    {"framework": framework, "rid": rid, "report": path, "sha256": tool.sha256(data)}
                )
        tool.release_check(self.root, ledger)
        entry = row["implementation"]["nativeEvidence"][0]
        receipt = tool.read_json(self.root / entry["report"])
        for key, value in (
            ("framework", "net7.0"),
            ("rid", "linux-riscv64"),
            ("nativeExecutable", False),
            ("passedTestIds", []),
            ("failedTestIds", ["FailedTest"]),
            ("skippedTestIds", ["ReaderPreservesExactBytes"]),
        ):
            data = tool.encoded({**receipt, key: value})
            (self.root / entry["report"]).write_bytes(data)
            entry["sha256"] = tool.sha256(data)
            with self.subTest(key=key), self.assertRaisesRegex(tool.SpecificationError, "receipt"):
                tool.release_check(self.root, ledger)


if __name__ == "__main__":
    unittest.main()
