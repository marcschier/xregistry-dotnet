# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

from __future__ import annotations

import importlib.util
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "upstream_accounting", ROOT / "eng" / "check_upstream_output.py"
)
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(tool)

VALID = (
    "PASS: http://localhost/xreg\n"
    "+- PASS: TestSniff\n"
    "+- PASS: TestModel\n"
    "|  +- PASS: TestSniff (cached)\n"
    "+- PASS: TestCapabilities\n"
    "+- PASS: TestRegistryRoot\n"
    "+- PASS: TestGroups\n"
    "+- PASS: TestResources\n"
    "Pass: 101   Fail: 0   Warn: 0   Skip: 0\n"
)


class CheckerAccountingTests(unittest.TestCase):
    def test_seeded_success_requires_exact_totals_and_six_named_suites(self) -> None:
        result = tool.check_output(VALID, 101)
        self.assertEqual(result["passes"], 101)
        self.assertEqual(result["failures"], 0)
        self.assertEqual(result["warnings"], 0)
        self.assertEqual(result["skips"], 0)
        self.assertEqual(
            result["suites"],
            ["TestCapabilities", "TestGroups", "TestModel", "TestRegistryRoot", "TestResources", "TestSniff"],
        )

    def test_zero_skipped_warning_failure_or_changed_totals_cannot_pass(self) -> None:
        for old, new in (
            ("Pass: 101", "Pass: 0"),
            ("Pass: 101", "Pass: 100"),
            ("Pass: 101", "Pass: 102"),
            ("Fail: 0", "Fail: 1"),
            ("Warn: 0", "Warn: 1"),
            ("Skip: 0", "Skip: 1"),
        ):
            with self.subTest(new=new), self.assertRaises(tool.CheckerError):
                tool.check_output(VALID.replace(old, new), 101)

    def test_missing_duplicate_or_different_suites_are_not_coverage(self) -> None:
        for text in (
            VALID.replace("+- PASS: TestResources\n", ""),
            VALID.replace("+- PASS: TestResources\n", "+- PASS: TestSniff\n"),
            VALID.replace("TestResources", "TestTDAllPass"),
            VALID.replace("+- PASS: TestGroups\n", "+- PASS: TestGroups\n+- PASS: TestGroups\n"),
        ):
            with self.subTest(text=text), self.assertRaises(tool.CheckerError):
                tool.check_output(text, 101)

    def test_truncation_appended_data_and_multiple_summaries_fail(self) -> None:
        for text in (
            "",
            VALID.rsplit("Pass: 101", 1)[0],
            VALID + "unexpected trailing output\n",
            VALID + "Pass: 101   Fail: 0   Warn: 0   Skip: 0\n",
        ):
            with self.subTest(text=text), self.assertRaises(tool.CheckerError):
                tool.check_output(text, 101)

    def test_bad_tree_result_cannot_hide_behind_zero_summary(self) -> None:
        for word in ("FAIL", "WARN", "SKIP"):
            text = VALID.replace("+- PASS: TestGroups", f"|  +- {word}: actual case\n+- PASS: TestGroups")
            with self.subTest(word=word), self.assertRaises(tool.CheckerError):
                tool.check_output(text, 101)

    def test_invalid_expected_count_fails_instead_of_disabling_the_gate(self) -> None:
        for count in (0, -1, True, 100001, "101"):
            with self.subTest(count=count), self.assertRaises(tool.CheckerError):
                tool.check_output(VALID, count)


if __name__ == "__main__":
    unittest.main()
