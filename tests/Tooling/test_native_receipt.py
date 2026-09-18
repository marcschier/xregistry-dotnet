# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "native_receipt_tool", ROOT / "eng" / "specification" / "native_receipt.py"
)
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(tool)

NAMESPACE = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"


def trx(cases: list[tuple[str, str, str]]) -> bytes:
    """Builds a minimal, real-shaped TRX: cases is (testId, methodName, outcome)."""
    definitions = "".join(
        f'<UnitTest id="{test_id}"><TestMethod className="Fixture.{method}" name="{method}" /></UnitTest>'
        for test_id, method, _ in cases
    )
    results = "".join(
        f'<UnitTestResult testId="{test_id}" outcome="{outcome}" />'
        for test_id, _, outcome in cases
    )
    return (
        f'<?xml version="1.0" encoding="UTF-8"?>'
        f'<TestRun xmlns="{NAMESPACE}">'
        f"<TestDefinitions>{definitions}</TestDefinitions>"
        f"<Results>{results}</Results>"
        f"</TestRun>"
    ).encode("utf-8")


def proof(*, native: bool = True, jit_compiled: int = 0) -> bytes:
    return json.dumps({"nativeAot": native, "jitCompiledMethods": jit_compiled}).encode("utf-8")


class NativeReceiptTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory(prefix="xregistry-native-receipt-test-")
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.trx_path = self.root / "results.trx"
        self.proof_path = self.root / "native-proof.json"
        self.output_path = self.root / "receipt.json"

    def test_all_passed_cases_produce_a_clean_receipt(self) -> None:
        self.trx_path.write_bytes(trx([
            ("1", "FirstRequirementTest", "Passed"),
            ("2", "SecondRequirementTest", "Passed"),
        ]))
        self.proof_path.write_bytes(proof())
        receipt = tool.build_receipt(self.trx_path, self.proof_path, "net10.0", "win-x64")
        self.assertEqual(receipt, {
            "schemaVersion": 1, "framework": "net10.0", "rid": "win-x64",
            "nativeExecutable": True,
            "passedTestIds": ["FirstRequirementTest", "SecondRequirementTest"],
            "failedTestIds": [], "skippedTestIds": [],
        })

    def test_one_failing_parameterized_execution_taints_the_whole_method(self) -> None:
        self.trx_path.write_bytes(trx([
            ("1", "ParameterizedTest", "Passed"),
            ("2", "ParameterizedTest", "Failed"),
            ("3", "ParameterizedTest", "Passed"),
        ]))
        self.proof_path.write_bytes(proof())
        receipt = tool.build_receipt(self.trx_path, self.proof_path, "net8.0", "linux-x64")
        self.assertEqual(receipt["passedTestIds"], [])
        self.assertEqual(receipt["failedTestIds"], ["ParameterizedTest"])

    def test_skipped_execution_is_reported_as_skipped_not_passed(self) -> None:
        self.trx_path.write_bytes(trx([("1", "SkippedTest", "NotExecuted")]))
        self.proof_path.write_bytes(proof())
        receipt = tool.build_receipt(self.trx_path, self.proof_path, "net8.0", "win-arm64")
        self.assertEqual(receipt["passedTestIds"], [])
        self.assertEqual(receipt["failedTestIds"], [])
        self.assertEqual(receipt["skippedTestIds"], ["SkippedTest"])

    def test_unrecognized_outcome_is_conservatively_treated_as_failed(self) -> None:
        self.trx_path.write_bytes(trx([("1", "InconclusiveTest", "Inconclusive")]))
        self.proof_path.write_bytes(proof())
        receipt = tool.build_receipt(self.trx_path, self.proof_path, "net10.0", "linux-arm64")
        self.assertEqual(receipt["passedTestIds"], [])
        self.assertEqual(receipt["failedTestIds"], ["InconclusiveTest"])

    def test_missing_empty_oversized_or_malformed_trx_is_rejected(self) -> None:
        self.proof_path.write_bytes(proof())
        with self.assertRaisesRegex(tool.ReceiptError, "missing"):
            tool.build_receipt(self.root / "absent.trx", self.proof_path, "net10.0", "win-x64")
        self.trx_path.write_bytes(b"")
        with self.assertRaisesRegex(tool.ReceiptError, "byte limit"):
            tool.build_receipt(self.trx_path, self.proof_path, "net10.0", "win-x64")
        self.trx_path.write_bytes(b"<not-xml")
        with self.assertRaisesRegex(tool.ReceiptError, "well-formed"):
            tool.build_receipt(self.trx_path, self.proof_path, "net10.0", "win-x64")
        with tempfile.TemporaryDirectory() as directory:
            oversized = Path(directory) / "oversized.trx"
            with oversized.open("wb") as stream:
                stream.seek(tool.MAX_TRX_BYTES)
                stream.write(b"0")
            with self.assertRaisesRegex(tool.ReceiptError, "byte limit"):
                tool.build_receipt(oversized, self.proof_path, "net10.0", "win-x64")

    def test_doctype_or_entity_declarations_are_rejected_before_parsing(self) -> None:
        self.proof_path.write_bytes(proof())
        for payload in (
            b'<?xml version="1.0"?><!DOCTYPE TestRun><TestRun xmlns="' + NAMESPACE.encode() + b'" />',
            b'<?xml version="1.0"?><!DOCTYPE TestRun [<!ENTITY x "y">]><TestRun xmlns="'
            + NAMESPACE.encode() + b'" />',
        ):
            with self.subTest(payload=payload):
                self.trx_path.write_bytes(payload)
                with self.assertRaisesRegex(tool.ReceiptError, "DOCTYPE or entity"):
                    tool.build_receipt(self.trx_path, self.proof_path, "net10.0", "win-x64")

    def test_missing_test_definitions_results_or_unmapped_result_ids_are_rejected(self) -> None:
        self.proof_path.write_bytes(proof())
        unmapped = (
            f'<TestRun xmlns="{NAMESPACE}"><TestDefinitions />'
            f'<Results><UnitTestResult testId="missing" outcome="Passed" /></Results></TestRun>'
        ).encode()
        for payload, message in (
            (f'<TestRun xmlns="{NAMESPACE}"><Results /></TestRun>'.encode(), "TestDefinitions"),
            (unmapped, "Unmapped"),
        ):
            with self.subTest(message=message):
                self.trx_path.write_bytes(payload)
                with self.assertRaisesRegex(tool.ReceiptError, message):
                    tool.build_receipt(self.trx_path, self.proof_path, "net10.0", "win-x64")
        self.trx_path.write_bytes(
            f'<TestRun xmlns="{NAMESPACE}"><TestDefinitions>'
            f'<UnitTest id="1"><TestMethod className="F.A" name="A" /></UnitTest>'
            f"</TestDefinitions><Results /></TestRun>".encode()
        )
        with self.assertRaisesRegex(tool.ReceiptError, "no test results"):
            tool.build_receipt(self.trx_path, self.proof_path, "net10.0", "win-x64")

    def test_jit_or_malformed_native_proof_is_never_accepted(self) -> None:
        self.trx_path.write_bytes(trx([("1", "SomeTest", "Passed")]))
        for payload, message in (
            (proof(native=False, jit_compiled=42), "nativeAot=true"),
            (proof(native=True, jit_compiled=1), "nativeAot=true"),
            (b"{not json", "not valid JSON"),
            (b"", "empty or too large"),
        ):
            with self.subTest(payload=payload):
                self.proof_path.write_bytes(payload)
                with self.assertRaisesRegex(tool.ReceiptError, message):
                    tool.build_receipt(self.trx_path, self.proof_path, "net10.0", "win-x64")

    def test_unsupported_framework_or_rid_is_rejected(self) -> None:
        self.trx_path.write_bytes(trx([("1", "SomeTest", "Passed")]))
        self.proof_path.write_bytes(proof())
        with self.assertRaisesRegex(tool.ReceiptError, "Unsupported framework"):
            tool.build_receipt(self.trx_path, self.proof_path, "net9.0", "win-x64")
        with self.assertRaisesRegex(tool.ReceiptError, "Unsupported RID"):
            tool.build_receipt(self.trx_path, self.proof_path, "net10.0", "osx-arm64")

    def test_cli_writes_output_once_and_never_overwrites_an_existing_receipt(self) -> None:
        self.trx_path.write_bytes(trx([("1", "SomeTest", "Passed")]))
        self.proof_path.write_bytes(proof())
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            code = tool.main([
                "--trx", str(self.trx_path), "--native-proof", str(self.proof_path),
                "--framework", "net10.0", "--rid", "win-x64", "--output", str(self.output_path),
            ])
        self.assertEqual(code, 0)
        self.assertIn("1 passed, 0 failed, 0 skipped", stdout.getvalue())
        document = json.loads(self.output_path.read_bytes())
        self.assertEqual(document["passedTestIds"], ["SomeTest"])

        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            code = tool.main([
                "--trx", str(self.trx_path), "--native-proof", str(self.proof_path),
                "--framework", "net10.0", "--rid", "win-x64", "--output", str(self.output_path),
            ])
        self.assertEqual(code, 1)
        self.assertIn("already exists", stderr.getvalue())

    def test_cli_warns_when_the_receipt_can_never_satisfy_a_release_row(self) -> None:
        self.trx_path.write_bytes(trx([("1", "FailingTest", "Failed")]))
        self.proof_path.write_bytes(proof())
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            code = tool.main([
                "--trx", str(self.trx_path), "--native-proof", str(self.proof_path),
                "--framework", "net10.0", "--rid", "win-x64", "--output", str(self.output_path),
            ])
        self.assertEqual(code, 0)
        self.assertIn("can never satisfy a release row", stdout.getvalue())


if __name__ == "__main__":
    unittest.main()
