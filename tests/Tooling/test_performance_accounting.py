# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("registry_performance", ROOT / "eng" / "measure-registry.py")
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = tool
SPEC.loader.exec_module(tool)


class PerformanceAccountingTests(unittest.TestCase):
    """Deterministic accounting tests, not server performance measurements."""

    def test_exact_sample_count_nearest_rank_percentiles_and_wall_throughput(self) -> None:
        samples = [i * 1_000_000 for i in range(1, 21)]
        stats = tool.summarize(samples, 20, 420_000_000)
        self.assertEqual(stats["samples"], 20)
        self.assertEqual(stats["p50Ms"], 10)
        self.assertEqual(stats["p95Ms"], 19)
        self.assertEqual(stats["p99Ms"], 20)
        self.assertEqual(stats["meanMs"], 10.5)
        self.assertAlmostEqual(stats["validatedOperationsPerSecond"], 20 / 0.42)

    def test_empty_truncated_nonfinite_negative_or_boolean_samples_fail(self) -> None:
        for samples, count, elapsed in (
            ([], 0, 1), ([1], 2, 10), ([0], 1, 10), ([-1], 1, 10),
            ([True], 1, 10), ([float("nan")], 1, 10), ([1], 1, 0),
            ([100], 1, 50),
        ):
            with self.subTest(samples=samples), self.assertRaises(tool.PerformanceError):
                tool.summarize(samples, count, elapsed)

    def test_native_proof_requires_true_flag_zero_jit_count_and_actual_exit(self) -> None:
        native = {"nativeAot": True, "jitCompiledMethods": 0, "architecture": "X64"}
        completed = subprocess.CompletedProcess([], 0, json.dumps(native).encode(), b"")
        self.assertEqual(tool.native_proof(completed), native)
        for change in ({"nativeAot": False}, {"jitCompiledMethods": 59}, {"jitCompiledMethods": False}, {"architecture": "Arm64"}):
            value = native | change
            with self.subTest(change=change), self.assertRaises(tool.PerformanceError):
                tool.native_proof(subprocess.CompletedProcess([], 0, json.dumps(value).encode(), b""))
        with self.assertRaises(tool.PerformanceError):
            tool.native_proof(subprocess.CompletedProcess([], 1, json.dumps(native).encode(), b""))

    def test_managed_negative_control_requires_positive_jit_count(self) -> None:
        managed = {"nativeAot": False, "jitCompiledMethods": 59, "architecture": "X64"}
        tool.managed_proof(subprocess.CompletedProcess([], 0, json.dumps(managed).encode(), b""))
        for change in ({"nativeAot": True}, {"jitCompiledMethods": 0}, {"jitCompiledMethods": True}):
            with self.subTest(change=change), self.assertRaises(tool.PerformanceError):
                tool.managed_proof(subprocess.CompletedProcess([], 0, json.dumps(managed | change).encode(), b""))

    def test_profile_is_single_concurrency_and_bounded_by_default(self) -> None:
        profile = json.loads((ROOT / "perf" / "developer-profile.json").read_bytes())
        tool.validate_profile(profile)
        for mutate in (
            lambda p: p.update(resourceCounts=[]),
            lambda p: p.update(resourceCounts=[100, 100]),
            lambda p: p.update(resourceCounts=[10000]),
            lambda p: p.update(rounds=0),
            lambda p: p.update(rounds=True),
            lambda p: p.update(concurrency=True),
            lambda p: p.update(warmupIterations=True),
            lambda p: p.update(concurrency=20),
            lambda p: p.update(readIterations=10000),
            lambda p: p.update(largeDocumentBytes=1000000000),
            lambda p: p["limits"].update(maxElapsedSeconds=0),
        ):
            value = copy.deepcopy(profile)
            mutate(value)
            with self.assertRaises(tool.PerformanceError):
                tool.validate_profile(value)

    def test_streamed_document_validation_checks_status_headers_length_digest_and_identity(self) -> None:
        data = bytes(range(256))
        expected = hashlib.sha256(data).hexdigest()
        response = tool.Response(200, {
            "content-type": "application/octet-stream", "content-length": "256",
            "xregistry-epoch": "0", "xregistry-versionid": "v1",
            "xregistry-xid": "/dirs/perf/files/small/versions/v1",
        }, b"", 256, expected, 1)
        tool.validate_document(response, 256, expected, "/dirs/perf/files/small/versions/v1")
        for field, value in (("status", 500), ("size", 255), ("sha256", "0" * 64)):
            changed = copy.deepcopy(response)
            setattr(changed, field, value)
            with self.subTest(field=field), self.assertRaises(tool.PerformanceError):
                tool.validate_document(changed, 256, expected, "/dirs/perf/files/small/versions/v1")
        changed = copy.deepcopy(response)
        changed.headers["xregistry-epoch"] = "1"
        with self.assertRaises(tool.PerformanceError):
            tool.validate_document(changed, 256, expected, "/dirs/perf/files/small/versions/v1")

    def test_metadata_epoch_identity_and_payload_must_match_exactly(self) -> None:
        value = {"recordid": "r000000", "versionid": "v1", "xid": "/dirs/perf/records/r000000",
                 "self": "http://127.0.0.1:1/registry/dirs/perf/records/r000000",
                 "ordinal": 0, "payload": "expected", "epoch": 4, "isdefault": True}
        tool.validate_record(value, "r000000", 0, "expected", 4, "http://127.0.0.1:1/registry")
        for name, changed in (("epoch", 3), ("payload", "old"), ("ordinal", 1), ("recordid", "wrong"), ("versionid", "v2")):
            with self.subTest(name=name), self.assertRaises(tool.PerformanceError):
                tool.validate_record(value | {name: changed}, "r000000", 0, "expected", 4, "http://127.0.0.1:1/registry")

    def test_wrong_metadata_status_or_truncated_body_cannot_enter_statistics(self) -> None:
        response = tool.Response(413, {"content-type": "application/problem+json"}, b'{"code":"too_large"}',
                                 20, "unused", 1, "/registry/dirs/perf/records", "POST")
        with self.assertRaisesRegex(tool.PerformanceError, "HTTP 413"):
            tool.json_response(response)
        response = tool.Response(200, {"content-type": "application/json"}, b'{"recordid":', 12, "unused", 1)
        with self.assertRaises(tool.PerformanceError):
            tool.json_response(response)

    def test_failed_startup_has_no_measured_success_or_synthetic_samples(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            code = tool.main(["--server", str(root / "missing.exe"),
                              "--managed-control", str(root / "missing.dll"),
                              "--data-root", str(root / "data"), "--output", str(root / "output")])
            self.assertNotEqual(code, 0)
            report = json.loads((root / "output" / "report.json").read_bytes())
            self.assertEqual(report["status"], "failed")
            self.assertFalse(report["measured"])
            self.assertEqual(report["rounds"], [])
            self.assertEqual(report["aggregates"], [])

    def test_complete_collection_rejects_missing_extra_and_duplicate_page_members(self) -> None:
        expected = {"r000000", "r000001"}
        tool.validate_collection_ids([{"r000000"}, {"r000001"}], expected)
        for pages in ([set()], [{"r000000"}], [{"r000000", "r000001", "extra"}], [{"r000000"}, {"r000000", "r000001"}]):
            with self.subTest(pages=pages), self.assertRaises(tool.PerformanceError):
                tool.validate_collection_ids(pages, expected)

    def test_pagination_continuation_is_opaque_and_same_origin_collection_only(self) -> None:
        current = "http://127.0.0.1:1234/registry/dirs/perf/records?limit=64"
        link = '<http://127.0.0.1:1234/registry/dirs/perf/records?cursor=a%2Bb&token=X>;rel=next;count=100'
        self.assertEqual(tool.next_link(link, current),
                         "http://127.0.0.1:1234/registry/dirs/perf/records?cursor=a%2Bb&token=X")
        self.assertIsNone(tool.next_link('<http://127.0.0.1:1234/registry>;rel=xregistry-root', current))
        for link in (
            '<http://remote.invalid/records?cursor=x>;rel=next',
            '<http://127.0.0.1:1234/other?cursor=x>;rel=next',
            '<http://user:password@127.0.0.1:1234/registry/dirs/perf/records>;rel=next',
            '<http://127.0.0.1:1234/registry/dirs/perf/records?x=1>;rel=next, <http://127.0.0.1:1234/registry/dirs/perf/records?x=2>;rel=next',
        ):
            with self.subTest(link=link), self.assertRaises(tool.PerformanceError):
                tool.next_link(link, current)

    def test_regression_trigger_is_investigation_not_a_fake_hard_gate(self) -> None:
        change = tool.compare_metric(100.0, 111.0, lower_is_better=True)
        self.assertAlmostEqual(change["regressionPercent"], 11)
        self.assertTrue(change["investigateIfRepeatable"])
        self.assertFalse(change["hardGate"])
        self.assertFalse(tool.compare_metric(100.0, 110.0, lower_is_better=True)["investigateIfRepeatable"])


if __name__ == "__main__":
    unittest.main()
