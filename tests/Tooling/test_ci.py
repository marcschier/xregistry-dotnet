# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Offline contract checks for the managed CI scheduling and diagnostics."""

from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")


class ManagedCiWorkflowTests(unittest.TestCase):
    def test_windows_managed_tests_use_single_module_scheduling(self) -> None:
        self.assertIn("if: matrix.os == 'windows-latest'", WORKFLOW)
        self.assertIn("run: ./eng/test.ps1 -NoBuild -MaxParallelTestModules 1", WORKFLOW)
        self.assertIn("if: matrix.os != 'windows-latest'", WORKFLOW)
        self.assertIn("run: ./eng/test.ps1 -NoBuild", WORKFLOW)

    def test_managed_test_diagnostics_are_uploaded_even_after_failure(self) -> None:
        self.assertIn("name: Upload managed test diagnostics", WORKFLOW)
        self.assertIn("if: always()", WORKFLOW)
        self.assertIn("uses: actions/upload-artifact@b7c566a772e6b6bfb58ed0dc250532a479d7789f", WORKFLOW)
        self.assertIn("TestResults/**", WORKFLOW)
        self.assertIn("if-no-files-found: ignore", WORKFLOW)


if __name__ == "__main__":
    unittest.main()
