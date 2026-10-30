# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

from __future__ import annotations

import contextlib
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "eng"))
import run_native_suites as tool


class NativeSuiteRunnerTests(unittest.TestCase):
    def setUp(self) -> None:
        programs = patch.object(tool.shutil, "which", return_value="fixture-program")
        programs.start()
        self.addCleanup(programs.stop)
        directory = tempfile.TemporaryDirectory(prefix="xregistry-native-suites-")
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.proof = self.root / "proof.json"
        self.native = self.root / "native.trx"
        self.managed = self.root / "managed.trx"
        self.rejected = self.root / "rejected.json"
        self.write_proof()
        self.write_trx(self.native)
        self.write_trx(self.managed, prefix="control")

    def write_proof(self, **overrides) -> None:
        value = {"nativeAot": True, "jitCompiledMethods": 0, "os": "win", "processArchitecture": "x64"}
        self.proof.write_text(json.dumps(value | overrides), encoding="utf-8")

    def write_trx(self, path: Path, *, prefix="native", cases=None) -> None:
        if cases is None:
            cases = [("Fixture.Proof", tool.PROOF_METHOD, "proof", "Passed"),
                     ("Fixture.Values", "Parameterized", "Parameterized(1)", "Passed"),
                     ("Fixture.Values", "Parameterized", "Parameterized(2)", "Passed")]
        root = ET.Element(tool.native_receipt.element("TestRun"))
        definitions = ET.SubElement(root, tool.native_receipt.element("TestDefinitions"))
        results = ET.SubElement(root, tool.native_receipt.element("Results"))
        for i, (class_name, method, display, outcome) in enumerate(cases):
            identifier = prefix + str(i)
            unit = ET.SubElement(definitions, tool.native_receipt.element("UnitTest"), id=identifier)
            ET.SubElement(unit, tool.native_receipt.element("TestMethod"), className=class_name, name=method)
            ET.SubElement(results, tool.native_receipt.element("UnitTestResult"),
                          testId=identifier, testName=display, outcome=outcome)
        ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)

    def test_native_receipt_retains_actual_case_and_method_counts(self) -> None:
        receipt, count = tool.verify_run(self.native, self.proof, "net10.0", "win-x64")
        self.assertEqual(count, 3)
        self.assertEqual(receipt["passedTestIds"], [tool.PROOF_METHOD, "Parameterized"])
        self.assertEqual(receipt["failedTestIds"], [])
        self.assertEqual(receipt["skippedTestIds"], [])

    def test_native_suite_rejects_failed_skipped_or_absent_proof_method(self) -> None:
        for outcome in ("Failed", "NotExecuted"):
            with self.subTest(outcome=outcome):
                self.write_trx(self.native, cases=[("Proof", tool.PROOF_METHOD, "proof", "Passed"),
                                                  ("Values", "Case", "case", outcome)])
                with self.assertRaisesRegex(ValueError, "failed or skipped"):
                    tool.verify_run(self.native, self.proof, "net10.0", "win-x64")
        self.write_trx(self.native, cases=[("Values", "Case", "case", "Passed")])
        with self.assertRaisesRegex(ValueError, "proof test"):
            tool.verify_run(self.native, self.proof, "net10.0", "win-x64")

    def test_native_suite_rejects_managed_and_wrong_process_evidence(self) -> None:
        for overrides in ({"nativeAot": False}, {"jitCompiledMethods": 1},
                          {"os": "linux"}, {"processArchitecture": "arm64"}):
            with self.subTest(overrides=overrides):
                self.write_proof(**overrides)
                with self.assertRaises(ValueError):
                    tool.verify_run(self.native, self.proof, "net10.0", "win-x64")

    def test_matching_full_managed_control_is_rejected_without_receipt(self) -> None:
        self.write_proof(nativeAot=False, jitCompiledMethods=100)
        with contextlib.redirect_stderr(io.StringIO()):
            tool.verify_control(self.managed, self.native, self.proof, "net10.0", "win-x64", self.rejected)
        self.assertFalse(self.rejected.exists())

    def test_managed_control_rejects_nonpositive_noninteger_or_native_proof(self) -> None:
        for overrides in ({"nativeAot": True, "jitCompiledMethods": 0},
                          {"nativeAot": False, "jitCompiledMethods": 0},
                          {"nativeAot": False, "jitCompiledMethods": True},
                          {"nativeAot": False, "jitCompiledMethods": -1}):
            with self.subTest(overrides=overrides):
                self.write_proof(**overrides)
                with self.assertRaisesRegex(ValueError, "positive JIT"):
                    tool.verify_control(self.managed, self.native, self.proof, "net10.0", "win-x64", self.rejected)
        self.assertFalse(self.rejected.exists())

    def test_case_comparison_rejects_missing_changed_duplicate_and_cross_class_cases(self) -> None:
        self.write_proof(nativeAot=False, jitCompiledMethods=100)
        proof = ("Fixture.Proof", tool.PROOF_METHOD, "proof", "Passed")
        first = ("Fixture.Values", "Parameterized", "Parameterized(1)", "Passed")
        second = ("Fixture.Values", "Parameterized", "Parameterized(2)", "Passed")
        variants = ([proof, first], [proof, first, first], [proof, first, second, second],
                    [proof, first, ("Another.Class", *second[1:])],
                    [proof, first, (second[0], second[1], "Parameterized(3)", second[3])])
        for cases in variants:
            with self.subTest(cases=cases):
                self.write_trx(self.managed, cases=cases)
                with self.assertRaisesRegex(ValueError, "same classes"):
                    tool.verify_control(self.managed, self.native, self.proof, "net10.0", "win-x64", self.rejected)

    def test_case_comparison_accepts_reordered_cases_and_different_trx_ids(self) -> None:
        inventory = tool.case_inventory(self.native)
        self.write_trx(self.managed, prefix="different", cases=[
            ("Fixture.Values", "Parameterized", "Parameterized(2)", "Passed"),
            ("Fixture.Values", "Parameterized", "Parameterized(1)", "Passed"),
            ("Fixture.Proof", tool.PROOF_METHOD, "proof", "Passed"),
        ])
        self.assertEqual(tool.case_inventory(self.managed), inventory)

    def test_failed_managed_baseline_and_existing_receipt_are_rejected(self) -> None:
        self.write_proof(nativeAot=False, jitCompiledMethods=100)
        self.write_trx(self.managed, cases=[("Proof", tool.PROOF_METHOD, "proof", "Failed")])
        with self.assertRaisesRegex(ValueError, "baseline method"):
            tool.verify_control(self.managed, self.native, self.proof, "net10.0", "win-x64", self.rejected)
        self.write_trx(self.managed)
        self.rejected.write_text("existing", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "fresh"):
            tool.verify_control(self.managed, self.native, self.proof, "net10.0", "win-x64", self.rejected)
        self.assertEqual(self.rejected.read_text(), "existing")

    def test_publication_requires_ilc_binary_and_zero_il_diagnostics(self) -> None:
        log, executable = self.root / "publish.log", self.root / "native.exe"
        log.write_text("Generating native code\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "executable is missing"):
            tool.verify_publish(log, executable)
        executable.write_bytes(b"fixture")
        for text, error in (("Build succeeded", "ILC execution"),
                            ("Generating native code\nwarning IL2026: fixture", "IL diagnostics"),
                            ("Generating native code\nerror IL3050: fixture", "IL diagnostics")):
            with self.subTest(text=text):
                log.write_text(text, encoding="utf-8")
                with self.assertRaisesRegex(ValueError, error):
                    tool.verify_publish(log, executable)
        log.write_text("Generating native code\nBuild succeeded\n", encoding="utf-8")
        tool.verify_publish(log, executable)

    def test_inventory_classifies_every_repository_test_project_and_rejects_new_ones(self) -> None:
        tool.validate_suite_inventory(ROOT)
        for name in (*tool.SUITES, *tool.EXCLUDED, "Unclassified.Tests"):
            project = self.root / "tests" / name / (name + ".csproj")
            project.parent.mkdir(parents=True)
            project.write_text("<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>")
        with self.assertRaisesRegex(ValueError, "explicitly classified"):
            tool.validate_suite_inventory(self.root)

    def test_dirty_source_is_rejected_before_hashing(self) -> None:
        with patch.object(tool.subprocess, "check_output", return_value=b" M src/example.cs"):
            with self.assertRaisesRegex(ValueError, "Commit source changes"):
                tool.source_inventory(self.root)

    def test_recorded_process_retains_stdout_stderr_and_checks_exit(self) -> None:
        log = self.root / "command.log"
        tool.run_recorded([sys.executable, "-c", "import sys; print('out'); print('err', file=sys.stderr)"],
                          log, dict(os.environ), timeout=30)
        self.assertIn("out", log.read_text())
        self.assertIn("err", log.read_text())
        with self.assertRaisesRegex(ValueError, "exited 7"):
            tool.run_recorded([sys.executable, "-c", "raise SystemExit(7)"], log, dict(os.environ), timeout=30)

    def arguments(self) -> list[str]:
        return ["--framework", "net10.0", "--rid", "win-x64", "--project", tool.SUITES[0],
                "--project", tool.SUITES[1], "--artifacts-root", str(self.root), "--work-root", str(self.root)]

    def test_cross_host_selection_is_rejected_before_running_or_creating_artifacts(self) -> None:
        with patch.object(tool, "native_host", return_value="linux-arm64"), \
                patch.object(tool, "execute_suite") as execute, contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(tool.main(self.arguments()), 1)
        execute.assert_not_called()
        self.assertEqual(list(self.root.glob("native-suites-*")), [])

    def test_missing_external_test_tool_is_rejected_before_creating_artifacts(self) -> None:
        with patch.object(tool, "native_host", return_value="win-x64"), \
                patch.object(tool.shutil, "which", side_effect=lambda name: None if name == "pwsh" else "present"), \
                patch.object(tool, "execute_suite") as execute, contextlib.redirect_stderr(io.StringIO()) as errors:
            self.assertEqual(tool.main(self.arguments()), 1)
        self.assertIn("missing from PATH: pwsh", errors.getvalue())
        execute.assert_not_called()
        self.assertEqual(list(self.root.glob("native-suites-*")), [])

    def test_suite_failure_is_retained_and_does_not_hide_other_selected_suites(self) -> None:
        source = {"commit": "abc", "sha256": "123", "files": []}
        with patch.object(tool, "native_host", return_value="win-x64"), \
                patch.object(tool, "source_inventory", return_value=source), \
                patch.object(tool, "execute_suite", side_effect=[
                    ValueError("IL diagnostic"),
                    {"project": tool.SUITES[1], "outcome": "passed"},
                ]) as execute, \
                contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(tool.main(self.arguments()), 1)
        self.assertEqual(execute.call_count, 2)
        summaries = list(self.root.glob("native-suites-*/summary.json"))
        self.assertEqual(len(summaries), 1)
        summary = json.loads(summaries[0].read_bytes())
        self.assertEqual(summary["outcome"], "failed")
        self.assertEqual([x["outcome"] for x in summary["suites"]], ["failed", "passed"])
        self.assertEqual(summary["suites"][0]["error"], "IL diagnostic")
        self.assertFalse(summary["releaseQualified"])

    def test_changed_source_aborts_remaining_suites_and_marks_summary_failed(self) -> None:
        before = {"commit": "abc", "sha256": "123", "files": []}
        with patch.object(tool, "native_host", return_value="win-x64"), \
                patch.object(tool, "source_inventory", side_effect=[before, before | {"sha256": "456"}]), \
                patch.object(tool, "execute_suite", return_value={"project": tool.SUITES[0], "outcome": "passed"}) as execute, \
                contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(tool.main(self.arguments()), 1)
        self.assertEqual(execute.call_count, 1)
        summary = json.loads(next(self.root.glob("native-suites-*/summary.json")).read_bytes())
        self.assertEqual(summary["outcome"], "failed")
        self.assertIn("Source inputs changed", summary["sourceError"])


if __name__ == "__main__":
    unittest.main()
