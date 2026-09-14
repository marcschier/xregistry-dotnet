from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import stat
import sys
import tempfile
import time
import unittest
from unittest import mock
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "corrected_oracles_tool", ROOT / "eng" / "specification" / "run_corrected_oracles.py"
)
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = tool
SPEC.loader.exec_module(tool)


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def cleanup_fixture(directory: tempfile.TemporaryDirectory) -> None:
    for attempt in range(11):
        try:
            directory.cleanup()
            return
        except PermissionError as error:
            if getattr(error, "winerror", None) != 32 or attempt == 10:
                raise
            if attempt == 0:
                print("Retrying fixture cleanup while Windows releases a working-directory handle.", file=sys.stderr)
            time.sleep(0.05)


class FixtureCleanupTests(unittest.TestCase):
    def test_only_transient_windows_sharing_violations_are_retried(self) -> None:
        error = PermissionError("Sharing violation")
        error.winerror = 32
        directory = mock.Mock()
        directory.cleanup.side_effect = [error, None]
        with mock.patch.object(time, "sleep") as sleep:
            cleanup_fixture(directory)
        self.assertEqual(directory.cleanup.call_count, 2)
        sleep.assert_called_once_with(0.05)

    def test_permanent_access_denial_is_not_hidden(self) -> None:
        error = PermissionError("Access denied")
        error.winerror = 5
        directory = mock.Mock()
        directory.cleanup.side_effect = error
        with mock.patch.object(time, "sleep") as sleep, self.assertRaises(PermissionError):
            cleanup_fixture(directory)
        self.assertEqual(directory.cleanup.call_count, 1)
        sleep.assert_not_called()

    def test_persistent_sharing_violation_exhausts_a_finite_retry_budget(self) -> None:
        error = PermissionError("Sharing violation")
        error.winerror = 32
        directory = mock.Mock()
        directory.cleanup.side_effect = error
        with mock.patch.object(time, "sleep") as sleep, self.assertRaises(PermissionError):
            cleanup_fixture(directory)
        self.assertEqual(directory.cleanup.call_count, 11)
        self.assertEqual(sleep.call_count, 10)


class OracleFixture(unittest.TestCase):
    def setUp(self) -> None:
        fixtures = ROOT / "artifacts" / "corrected-oracle-tooling-fixtures"
        fixtures.mkdir(parents=True, exist_ok=True)
        directory = tempfile.TemporaryDirectory(prefix="case-", dir=fixtures)
        self.addCleanup(cleanup_fixture, directory)
        self.root = Path(directory.name)
        self.output = "artifacts/specification-oracles/fixture"
        self.sources = {
            "LICENSE": b"Independent oracle tooling fixture license.\n",
            "core/spec.md": b"# Contract\n\nA reader MUST preserve evidence.\n",
            "tools/federation_examples.py": b"VALUE = 'original'\n",
            **{path: b"def test_fixture():\n    assert bytes.fromhex('00ff') == b'\\x00\\xff'\n"
               for path in tool.STANDARD_MODULES},
        }
        self.scope = {
            "schemaVersion": 1, "sourceRepository": "https://example.invalid/source",
            "upstreamRepository": "https://example.invalid/upstream", "commit": "a" * 40,
            "branch": "main", "includesUncommittedChanges": True,
            "baselineManifestSha256": "b" * 64, "normative": {"core/spec.md": "core"},
            "excludeSuffixes": [".png"],
        }
        self.original = {
            path: {"path": path, "bytes": len(data), "sha256": digest(data)}
            for path, data in self.sources.items()
        }
        self.lock = {
            **{key: value for key, value in self.scope.items() if key != "excludeSuffixes"},
            "corpusPath": tool.manage.CORPUS.as_posix(), "fileCount": len(self.sources),
            "files": list(self.original.values()), "excluded": [],
        }
        for path, data in self.sources.items():
            self.write(tool.manage.CORPUS / path, data)
        self.write_json(Path("eng") / "specification" / "scope.json", self.scope)
        self.write_json(tool.manage.LOCK, self.lock)
        self.first = self.correction("SPEC-004", "tools/federation_examples.py", b"VALUE = 'prior'\n")
        self.head = self.correction("SPEC-005", self.first["path"], b"VALUE = 'active'\n", self.first)
        self.regression = self.correction(
            "SPEC-005", "tools/test_implementation_fixture.py",
            b"from federation_examples import VALUE\n\ndef test_fixture():\n    assert VALUE == 'active'\n",
        )
        self.document = {
            "schemaVersion": 2, "baselineManifestSha256": "b" * 64,
            "entries": [self.first, self.head, self.regression],
        }
        self.write_json(tool.manage.CORRECTIONS, self.document)

    def write(self, relative: Path, data: bytes) -> None:
        destination = self.root / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)

    def write_json(self, relative: Path, data: dict) -> None:
        self.write(relative, tool.manage.encoded(data))

    def correction(self, case: str, path: str, data: bytes, prior: dict | None = None) -> dict:
        original_hash = self.original[path]["sha256"] if path in self.original else None
        record = {
            "case": case, "path": path, "originalSha256": original_hash,
            "predecessorSha256": prior["sha256"] if prior else original_hash,
            "predecessorCase": prior["case"] if prior else None,
            "sha256": digest(data), "bytes": len(data),
            "file": (tool.manage.CORRECTED_SOURCES / case / path).as_posix(),
            "sourceCommitBeforeCorrections": "c" * 40,
            "reason": "Synthetic runner fixture, not specification conformance evidence.",
            "testIds": ["Not.An.Execution.Selector"],
        }
        self.write(Path(record["file"]), data)
        return record

    def protected(self) -> dict[str, bytes]:
        paths = [tool.manage.CORPUS / path for path in self.sources]
        paths += [Path(entry["file"]) for entry in self.document["entries"]]
        paths += [tool.manage.LOCK, tool.manage.CORRECTIONS]
        return {path.as_posix(): (self.root / path).read_bytes() for path in paths}

    def pytest_evidence(self, directory: Path, modules: list[str]) -> tuple[dict, ET.Element]:
        selected = [{"nodeId": path + "::test_fixture", "module": path, "name": "test_fixture"}
                    for path in modules]
        account = {
            "schemaVersion": 1, "framework": "pytest", "frameworkVersion": "synthetic-fixture",
            "pythonVersion": "synthetic-fixture", "modules": modules, "selector": "not opcua",
            "selected": selected, "deselected": [modules[0] + "::test_fixture[opcua]"],
            "sessionTestsCollected": len(selected), "exitCode": 0,
            "reports": [{"nodeId": item["nodeId"], "when": phase, "outcome": "passed"}
                        for item in selected for phase in ("setup", "call", "teardown")],
        }
        xml = ET.Element("testsuites")
        suite = ET.SubElement(xml, "testsuite", name="pytest", tests=str(len(selected)),
                              failures="0", errors="0", skipped="0")
        for item in selected:
            case = ET.SubElement(suite, "testcase", name=item["name"],
                                 classname=item["module"][:-3].replace("/", "."))
            props = ET.SubElement(case, "properties")
            ET.SubElement(props, "property", name="oracleNodeId", value=item["nodeId"])
        self.save_evidence(directory, account, xml)
        return account, xml

    def save_evidence(self, directory: Path, account: dict, xml: ET.Element) -> None:
        directory.mkdir(parents=True, exist_ok=True)
        (directory / "accounting.json").write_bytes(tool.manage.encoded(account))
        (directory / "results.xml").write_bytes(ET.tostring(xml, encoding="utf-8"))


class MaterializationTests(OracleFixture):
    def test_verified_overlay_uses_active_heads_and_new_regressions_with_license(self) -> None:
        before = self.protected()
        report = tool.materialize(self.root, self.output, "corrected")
        overlay = self.root / self.output / "overlay"

        self.assertEqual((overlay / self.first["path"]).read_bytes(), b"VALUE = 'active'\n")
        self.assertEqual((overlay / self.regression["path"]).read_bytes(),
                         (self.root / self.regression["file"]).read_bytes())
        self.assertEqual((overlay / "LICENSE").read_bytes(), self.sources["LICENSE"])
        self.assertEqual(set(item["path"] for item in report["overlayFiles"]),
                         set(self.sources) | {self.regression["path"]})
        self.assertEqual(report["modules"], [*tool.STANDARD_MODULES, self.regression["path"]])
        self.assertEqual(report["correctionsSha256"], digest((self.root / tool.manage.CORRECTIONS).read_bytes()))
        self.assertEqual(self.protected(), before)

    def test_original_mode_remains_distinct_and_does_not_select_corrections(self) -> None:
        report = tool.materialize(self.root, self.output, "original")
        overlay = self.root / self.output / "overlay"
        self.assertEqual((overlay / self.first["path"]).read_bytes(), b"VALUE = 'original'\n")
        self.assertFalse((overlay / self.regression["path"]).exists())
        self.assertEqual(report["mode"], "original")
        self.assertEqual(report["modules"], list(tool.STANDARD_MODULES))
        self.assertEqual(len(report["overlayFiles"]), len(self.sources))

    def test_tampered_or_unlisted_history_fails_before_materialization(self) -> None:
        target = self.root / self.first["file"]
        for tamper in ("changed", "unlisted"):
            with self.subTest(tamper=tamper):
                if tamper == "changed":
                    target.write_bytes(b"changed\n")
                else:
                    self.write(tool.manage.CORRECTED_SOURCES / "unlisted.py", b"unlisted\n")
                before = self.protected()
                with self.assertRaises(tool.manage.SpecificationError):
                    tool.materialize(self.root, self.output, "corrected")
                self.assertFalse((self.root / self.output).exists())
                self.assertEqual(self.protected(), before)
                target.write_bytes(b"VALUE = 'prior'\n")

    def test_source_and_destination_traversal_are_rejected_before_writes(self) -> None:
        for output in ("../outside", "/outside", "C:\\outside",
                       "tests/Conformance/Sources/new", "artifacts/specification-oracles/../escape"):
            with self.subTest(output=output), self.assertRaises(tool.manage.SpecificationError):
                tool.materialize(self.root, output, "corrected")
        changed = copy.deepcopy(self.document)
        changed["entries"][0]["path"] = "../outside"
        self.write_json(tool.manage.CORRECTIONS, changed)
        with self.assertRaises(tool.manage.SpecificationError):
            tool.materialize(self.root, self.output, "corrected")
        self.assertFalse((self.root / self.output).exists())

    def test_symlinks_in_sources_history_and_output_are_rejected(self) -> None:
        for linked in (
            self.root / tool.manage.CORPUS,
            (self.root / self.first["file"]).parent,
            self.root / "artifacts",
        ):
            with self.subTest(linked=linked), \
                    mock.patch.object(Path, "is_symlink", autospec=True,
                                      side_effect=lambda path: path == linked):
                with self.assertRaisesRegex(tool.manage.SpecificationError, "link"):
                    tool.materialize(self.root, self.output, "corrected")
                self.assertFalse((self.root / self.output).exists())

    def test_manifest_test_ids_never_become_execution_arguments(self) -> None:
        for entry in self.document["entries"]:
            entry["testIds"] = ["../outside.py", "--override-ini=addopts=-k nothing", "tools/test_opcua.py"]
        self.write_json(tool.manage.CORRECTIONS, self.document)
        report = tool.materialize(self.root, self.output, "corrected")
        self.assertEqual(report["modules"], [*tool.STANDARD_MODULES, self.regression["path"]])
        self.assertEqual(report["selector"], "not opcua")

    def test_existing_output_is_not_overwritten(self) -> None:
        self.write(Path(self.output) / "owner.txt", b"owner bytes\n")
        with self.assertRaisesRegex(tool.manage.SpecificationError, "exists"):
            tool.materialize(self.root, self.output, "corrected")
        self.assertEqual((self.root / self.output / "owner.txt").read_bytes(), b"owner bytes\n")

    def test_readonly_source_permissions_are_not_propagated_or_changed(self) -> None:
        source = self.root / self.head["file"]
        original_mode = source.stat().st_mode
        source.chmod(stat.S_IREAD)
        self.addCleanup(source.chmod, original_mode)
        tool.materialize(self.root, self.output, "corrected")
        copy_path = self.root / self.output / "overlay" / self.head["path"]
        copy_path.write_bytes(b"mutable isolated copy\n")
        self.assertEqual(source.read_bytes(), b"VALUE = 'active'\n")
        self.assertFalse(source.stat().st_mode & stat.S_IWUSR)


class AccountingTests(OracleFixture):
    def test_exact_selected_nodes_modules_phases_and_xml_counts_are_required(self) -> None:
        modules = [*tool.STANDARD_MODULES, self.regression["path"]]
        account, _ = self.pytest_evidence(self.root / "evidence", modules)
        junit = tool.read_junit(self.root / "evidence" / "results.xml")
        result = tool.validate_accounting(account, junit, modules, 0)
        self.assertEqual(result, {
            "collected": 9, "selected": 8, "deselected": 1, "passed": 8,
            "tests": 8, "failures": 0, "errors": 0, "skipped": 0,
        })
        self.assertEqual(junit["sha256"], digest((self.root / "evidence" / "results.xml").read_bytes()))

    def test_empty_skipped_failed_or_errored_xml_cannot_be_green(self) -> None:
        modules = list(tool.STANDARD_MODULES)
        for outcome in ("empty", "skipped", "failure", "error"):
            with self.subTest(outcome=outcome):
                account, xml = self.pytest_evidence(self.root / "evidence", modules)
                suite = xml[0]
                if outcome == "empty":
                    for case in list(suite):
                        suite.remove(case)
                    suite.set("tests", "0")
                else:
                    ET.SubElement(suite[0], outcome)
                    suite.set({"skipped": "skipped", "failure": "failures", "error": "errors"}[outcome], "1")
                self.save_evidence(self.root / "evidence", account, xml)
                with self.assertRaises(tool.OracleError):
                    junit = tool.read_junit(self.root / "evidence" / "results.xml")
                    tool.validate_accounting(account, junit, modules, 0)

    def test_wrong_framework_missing_report_and_forged_xml_totals_fail(self) -> None:
        with self.assertRaisesRegex(tool.OracleError, "XML"):
            tool.read_junit(self.root / "missing.xml")
        for field, value in (("name", "MSTest"), ("tests", "99"), ("skipped", "1"), ("tests", "-1")):
            with self.subTest(field=field, value=value):
                account, xml = self.pytest_evidence(self.root / "evidence", list(tool.STANDARD_MODULES))
                xml[0].set(field, value)
                self.save_evidence(self.root / "evidence", account, xml)
                with self.assertRaises(tool.OracleError):
                    tool.read_junit(self.root / "evidence" / "results.xml")
        account, _ = self.pytest_evidence(self.root / "evidence", list(tool.STANDARD_MODULES))
        account["framework"] = "unittest"
        with self.assertRaisesRegex(tool.OracleError, "framework"):
            tool.validate_accounting(account, tool.read_junit(self.root / "evidence" / "results.xml"),
                                     list(tool.STANDARD_MODULES), 0)

    def test_missing_duplicate_or_hidden_failure_events_and_wrong_selection_fail(self) -> None:
        modules = list(tool.STANDARD_MODULES)
        baseline, _ = self.pytest_evidence(self.root / "evidence", modules)
        junit = tool.read_junit(self.root / "evidence" / "results.xml")
        for mutation in ("missing-call", "duplicate", "hidden-failure", "selector", "deselection",
                         "unknown-module", "exit", "collected", "empty", "wrong-framework"):
            account = copy.deepcopy(baseline)
            if mutation == "missing-call":
                del account["reports"][1]
            elif mutation == "duplicate":
                account["reports"].append(account["reports"][0])
            elif mutation == "hidden-failure":
                account["reports"][0]["outcome"] = "failed"
            elif mutation == "selector":
                account["selector"] = "test_fixture"
            elif mutation == "deselection":
                account["deselected"] = ["tools/test_registry_model.py::ordinary_test"]
            elif mutation == "unknown-module":
                account["selected"][0]["module"] = "tools/test_opcua.py"
            elif mutation == "exit":
                account["exitCode"] = 1
            elif mutation == "collected":
                account["sessionTestsCollected"] = 1
            elif mutation == "empty":
                account["selected"] = []
            else:
                account["framework"] = "junit"
            with self.subTest(mutation=mutation), self.assertRaises(tool.OracleError):
                tool.validate_accounting(account, junit, modules, 0)
        with self.assertRaises(tool.OracleError):
            tool.validate_accounting(baseline, junit, modules, 1)

    def test_missing_standard_module_and_duplicate_xml_case_ids_are_rejected(self) -> None:
        with self.assertRaises(tool.OracleError):
            tool.validate_modules(list(tool.STANDARD_MODULES[:-1]))
        account, xml = self.pytest_evidence(self.root / "evidence", list(tool.STANDARD_MODULES))
        xml[0][1][0][0].set("value", account["selected"][0]["nodeId"])
        self.save_evidence(self.root / "evidence", account, xml)
        with self.assertRaises(tool.OracleError):
            tool.validate_accounting(account, tool.read_junit(self.root / "evidence" / "results.xml"),
                                     list(tool.STANDARD_MODULES), 0)

    def test_one_empty_module_cannot_hide_behind_other_passing_modules(self) -> None:
        modules = list(tool.STANDARD_MODULES)
        account, xml = self.pytest_evidence(self.root / "evidence", modules)
        missing = account["selected"].pop()
        account["reports"] = [report for report in account["reports"]
                              if report["nodeId"] != missing["nodeId"]]
        account["sessionTestsCollected"] -= 1
        xml[0].remove(xml[0][-1])
        xml[0].set("tests", str(len(account["selected"])))
        self.save_evidence(self.root / "evidence", account, xml)
        junit = tool.read_junit(self.root / "evidence" / "results.xml")
        with self.assertRaisesRegex(tool.OracleError, "Every requested oracle module"):
            tool.validate_accounting(account, junit, modules, 0)


class ExecutionTests(OracleFixture):
    def simulated_process(self, command, cwd, environment, log, timeout_seconds, max_log_bytes) -> int:
        directory = log.parent
        control = json.loads((directory / "control.json").read_bytes())
        self.pytest_evidence(directory, control["modules"])
        log.write_text("Synthetic process-boundary fixture, not real oracle execution.\n", encoding="utf-8")
        return 0

    def test_success_report_binds_exact_overlay_and_protected_source_hashes(self) -> None:
        before = self.protected()
        with mock.patch.object(tool, "execute", side_effect=self.simulated_process):
            path = tool.run_oracles(self.root, self.output, "corrected")
        report = json.loads(path.read_bytes())
        self.assertEqual(report["status"], "passed")
        self.assertEqual(report["counts"]["tests"], 8)
        self.assertEqual(report["counts"]["deselected"], 1)
        self.assertEqual(report["sourcesBefore"], report["sourcesAfter"])
        self.assertEqual(report["preserved"], {"sources": True, "overlay": True})
        self.assertEqual(report["modules"], [*tool.STANDARD_MODULES, self.regression["path"]])
        self.assertEqual(self.protected(), before)
        self.assertEqual(report["junit"]["sha256"],
                         digest((path.parent / "results.xml").read_bytes()))

    def test_failed_run_and_missing_report_preserve_original_and_historical_bytes(self) -> None:
        before = self.protected()
        for exit_code in (0, 1):
            output = self.output + str(exit_code)
            with self.subTest(exit_code=exit_code), \
                    mock.patch.object(tool, "execute", return_value=exit_code):
                with self.assertRaisesRegex(tool.OracleError, "report.json"):
                    tool.run_oracles(self.root, output, "corrected")
                report = json.loads((self.root / output / "report.json").read_bytes())
                self.assertEqual(report["status"], "failed")
                self.assertTrue(report["errors"])
                self.assertEqual(report["preserved"], {"sources": True, "overlay": True})
                self.assertEqual(self.protected(), before)

    def test_overlay_mutation_fails_without_modifying_protected_sources(self) -> None:
        def mutate(*args, **kwargs):
            result = self.simulated_process(*args, **kwargs)
            (kwargs["cwd"] / "core" / "spec.md").write_bytes(b"changed overlay\n")
            return result

        before = self.protected()
        with mock.patch.object(tool, "execute", side_effect=mutate):
            with self.assertRaises(tool.OracleError):
                tool.run_oracles(self.root, self.output, "corrected")
        report = json.loads((self.root / self.output / "report.json").read_bytes())
        self.assertEqual(report["preserved"], {"sources": True, "overlay": False})
        self.assertEqual(report["status"], "failed")
        self.assertEqual(self.protected(), before)

    def test_protected_drift_is_reported_without_rewriting_retained_evidence(self) -> None:
        def mutate(*args, **kwargs):
            result = self.simulated_process(*args, **kwargs)
            (self.root / self.first["file"]).write_bytes(b"externally changed evidence\n")
            return result

        with mock.patch.object(tool, "execute", side_effect=mutate):
            with self.assertRaises(tool.OracleError):
                tool.run_oracles(self.root, self.output, "corrected")
        report = json.loads((self.root / self.output / "report.json").read_bytes())
        self.assertEqual(report["preserved"], {"sources": False, "overlay": True})
        self.assertEqual(report["status"], "failed")
        self.assertEqual((self.root / self.first["file"]).read_bytes(), b"externally changed evidence\n")
        self.assertTrue(any("Protected source verification" in error for error in report["errors"]))

    def test_missing_pytest_dependency_fails_without_a_success_fallback(self) -> None:
        execute = tool.execute

        def no_site_packages(command, **kwargs):
            return execute([command[0], "-S", *command[1:]], **kwargs)

        before = self.protected()
        with mock.patch.object(tool, "execute", side_effect=no_site_packages):
            with self.assertRaises(tool.OracleError):
                tool.run_oracles(self.root, self.output, "corrected")
        directory = self.root / self.output
        self.assertIn("No module named 'pytest'", (directory / "pytest.log").read_text(encoding="utf-8"))
        self.assertEqual(json.loads((directory / "report.json").read_bytes())["status"], "failed")
        self.assertEqual(self.protected(), before)

    def test_invalid_process_budgets_fail_before_creating_output(self) -> None:
        for budget in (0, -1, float("inf"), float("nan"), True, 901):
            with self.subTest(budget=budget), self.assertRaises(tool.OracleError):
                tool.run_oracles(self.root, self.output, "corrected", timeout_seconds=budget)
        self.assertFalse((self.root / self.output).exists())

    def test_process_deadline_and_log_byte_limit_are_enforced(self) -> None:
        for program, timeout, limit, message in (
            ("import time; time.sleep(30)", 0.3, 1024, "deadline"),
            ("import os\nwhile True: os.write(1, b'x' * 4096)", 10, 1024, "output byte limit"),
        ):
            log = self.root / (message.replace(" ", "-") + ".log")
            started = time.monotonic()
            with self.subTest(message=message), self.assertRaisesRegex(tool.OracleError, message):
                tool.execute([sys.executable, "-B", "-c", program], self.root, dict(tool.os.environ),
                             log, timeout, limit)
            self.assertLess(time.monotonic() - started, 20)
            self.assertLessEqual(log.stat().st_size, limit)


if __name__ == "__main__":
    unittest.main()
