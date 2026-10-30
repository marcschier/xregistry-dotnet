# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Offline contract checks for the managed CI scheduling and diagnostics."""

from pathlib import Path
import re
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
        self.assertIn("uses: actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9", WORKFLOW)
        self.assertIn("TestResults/**", WORKFLOW)
        self.assertIn("if-no-files-found: ignore", WORKFLOW)


class NativeSuiteCiWorkflowTests(unittest.TestCase):
    def setUp(self) -> None:
        workflow = (ROOT / ".github" / "workflows" / "aot.yml").read_text(encoding="utf-8")
        self.job = workflow.split("  native-module-suites:", 1)[1].split("  native-all-package-consumers:", 1)[0]

    def test_native_suite_workflow_declares_every_framework_rid_cell_once(self) -> None:
        cells = re.findall(r"rid: (\S+)\s+framework: (\S+)", self.job)
        expected = {(rid, framework) for rid in ("win-x64", "win-arm64", "linux-x64", "linux-arm64")
                    for framework in ("net8.0", "net10.0")}
        self.assertEqual(len(cells), 8)
        self.assertEqual(set(cells), expected)
        self.assertIn("fail-fast: false", self.job)
        self.assertIn("runs-on: ${{ matrix.os }}", self.job)
        self.assertIn("fetch-depth: 0", self.job)

    def test_native_suite_workflow_retains_failure_evidence_and_unmounts_its_own_volume(self) -> None:
        self.assertIn("python eng/run_native_suites.py --framework '${{ matrix.framework }}' --rid '${{ matrix.rid }}'", self.job)
        self.assertIn("if: always()\n        uses: actions/upload-artifact@", self.job)
        for path in ("*/*.json", "*/*/*.json", "*/*/*.log", "*/*/results/**", "*/*/control-results/**", "*/*/native/**"):
            self.assertIn("artifacts/native-suites/" + path, self.job)
        self.assertIn("if-no-files-found: error", self.job)
        self.assertIn('sudo umount "$RUNNER_TEMP/xregistry-suites-work"', self.job)
        self.assertIn("if: always() && runner.os == 'Linux'", self.job)

    def test_native_suite_workflow_installs_hashed_oracle_dependencies_before_execution(self) -> None:
        install = "python -m pip install --require-hashes --only-binary=:all: -c eng/requirements-oracles.lock 'jsonschema[format-nongpl]'"
        self.assertIn(install, self.job)
        self.assertLess(self.job.index(install), self.job.index("python eng/run_native_suites.py"))
        self.assertNotIn("-r eng/requirements-oracles.lock", self.job)

    def test_native_suite_workflow_retains_the_hashed_managed_control_assembly(self) -> None:
        upload = self.job.split("name: Retain native suite source, binary, case and rejected-control evidence", 1)[1]
        self.assertIn("if: always()", upload)
        paths = re.search(r"(?m)^          path: \|\n((?:            .+\n)+)", upload)
        self.assertIsNotNone(paths)
        self.assertIn("artifacts/native-suites/*/*/build/bin/**/XRegistry.*.Tests.dll",
                      [line.strip() for line in paths.group(1).splitlines()])


if __name__ == "__main__":
    unittest.main()
