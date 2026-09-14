from __future__ import annotations

import base64
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("reverse_interop", ROOT / "eng" / "verify-upstream-to-dotnet.py")
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = tool
SPEC.loader.exec_module(tool)

VALID_CHECKER = (
    "PASS: http://127.0.0.1:12345/registry\n"
    "+- PASS: TestSniff\n"
    "+- PASS: TestModel\n"
    "+- PASS: TestCapabilities\n"
    "+- PASS: TestRegistryRoot\n"
    "+- PASS: TestGroups\n"
    "+- PASS: TestResources\n"
    "Pass: 101   Fail: 0   Warn: 0   Skip: 0\n"
)


def completed(stdout: bytes, code: int = 0, stderr: bytes = b"") -> subprocess.CompletedProcess:
    return subprocess.CompletedProcess(["synthetic-accounting-input"], code, stdout, stderr)


class ReverseAccountingTests(unittest.TestCase):
    """Synthetic negative accounting inputs, never independent interoperability evidence."""

    def test_native_proof_requires_flag_zero_jit_count_architecture_and_clean_exit(self) -> None:
        value = {"nativeAot": True, "jitCompiledMethods": 0, "architecture": "X64"}
        self.assertEqual(tool.validate_native_runtime(completed(json.dumps(value).encode()), "win-x64"), value)
        for mutation in ("flag", "jit", "boolean-jit", "missing-jit", "architecture", "exit", "stderr"):
            changed = copy.deepcopy(value)
            code, stderr = 0, b""
            if mutation == "flag":
                changed["nativeAot"] = False
            elif mutation == "jit":
                changed["jitCompiledMethods"] = 1
            elif mutation == "boolean-jit":
                changed["jitCompiledMethods"] = False
            elif mutation == "missing-jit":
                changed.pop("jitCompiledMethods")
            elif mutation == "architecture":
                changed["architecture"] = "Arm64"
            elif mutation == "exit":
                code = 1
            else:
                stderr = b"late native failure"
            with self.subTest(mutation=mutation), self.assertRaises(tool.ReverseInteropError):
                tool.validate_native_runtime(completed(json.dumps(changed).encode(), code, stderr), "win-x64")

    def test_malformed_duplicate_nonfinite_trailing_and_oversized_json_cannot_pass(self) -> None:
        for raw in (
            b"", b"{}", b"\xff", b'{"nativeAot":true,"nativeAot":false}',
            b'{"jitCompiledMethods":NaN}', b"{}{}", b" " * (tool.MAX_OUTPUT_BYTES + 1),
        ):
            with self.subTest(raw=raw[:60]), self.assertRaises(tool.ReverseInteropError):
                tool.validate_native_runtime(completed(raw), "win-x64")

    def test_pinned_cli_version_is_exact_and_failure_exit_does_not_count_as_version_proof(self) -> None:
        self.assertEqual(tool.validate_cli_version(completed(b"Version: 5854af0130db\n"), "Version: 5854af0130db"),
                         "Version: 5854af0130db")
        for result in (
            completed(b"Version: 5854af0130db\n", 1),
            completed(b"Version: different\n"),
            completed(b"Version: 5854af0130db\nextra\n"),
            completed(b"Version: 5854af0130db\n", stderr=b"warning"),
        ):
            with self.subTest(result=result), self.assertRaises(tool.ReverseInteropError):
                tool.validate_cli_version(result, "Version: 5854af0130db")

    def test_checker_requires_six_named_suites_exact_totals_zero_errors_and_zero_process_exit(self) -> None:
        self.assertEqual(tool.validate_checker(completed(VALID_CHECKER.encode()), 101)["passes"], 101)
        for old, new in (
            ("Pass: 101", "Pass: 30"), ("Fail: 0", "Fail: 10"),
            ("Warn: 0", "Warn: 1"), ("Skip: 0", "Skip: 1"),
            ("+- PASS: TestResources\n", ""), ("TestResources", "TestTDAllPass"),
        ):
            with self.subTest(new=new), self.assertRaises(tool.ReverseInteropError):
                tool.validate_checker(completed(VALID_CHECKER.replace(old, new).encode()), 101)
        with self.assertRaises(tool.ReverseInteropError):
            tool.validate_checker(completed(VALID_CHECKER.encode(), 2), 101)
        with self.assertRaises(tool.ReverseInteropError):
            tool.validate_checker(completed(VALID_CHECKER.encode(), stderr=b"failure"), 101)

    def test_known_mutable_capability_failure_is_classified_but_not_accepted(self) -> None:
        text = (
            "FAIL: root\n+- FAIL: TestCapabilities\n"
            "Unknown capability specified: mutable.\n"
            "Pass: 30   Fail: 10   Warn: 0   Skip: 0\n"
        )
        self.assertEqual(tool.classify_checker_failure(text), ["XR-CAPABILITIES-MUTABLE"])
        self.assertEqual(tool.observed_checker_totals(text), {"passes": 30, "failures": 10, "warnings": 0, "skips": 0})
        with self.assertRaises(tool.ReverseInteropError):
            tool.validate_checker(completed(text.encode(), 2), 101)
        self.assertEqual(tool.classify_checker_failure("unrelated error"), [])

    def test_empty_or_changed_fixtures_are_rejected_before_a_server_can_start(self) -> None:
        fixture = json.loads((ROOT / "interop" / "reverse-fixture.json").read_bytes())
        tool.validate_fixture(fixture)
        for mutation in ("empty-import", "empty-model", "bytes", "default", "versions", "count"):
            value = copy.deepcopy(fixture)
            if mutation == "empty-import":
                value["importBody"] = {}
            elif mutation == "empty-model":
                value["modelSource"]["groups"] = {}
            elif mutation == "bytes":
                value["expected"]["binaryBase64"] = base64.b64encode(b"changed").decode()
            elif mutation == "default":
                value["importBody"]["team"]["files"]["item"]["meta"]["defaultversionid"] = "v2"
            elif mutation == "versions":
                value["importBody"]["team"]["files"]["item"]["versions"] = {}
            else:
                value["checker"]["expectedPasses"] = 0
            with self.subTest(mutation=mutation), self.assertRaises(tool.ReverseInteropError):
                tool.validate_fixture(value)

    def test_seed_observation_requires_the_actual_document_model_group_resource_and_versions(self) -> None:
        fixture = json.loads((ROOT / "interop" / "reverse-fixture.json").read_bytes())
        model = fixture["modelSource"]
        groups = {"team": {"dirid": "team", "xid": "/dirs/team"}}
        resources = {"item": {"fileid": "item", "xid": "/dirs/team/files/item"}}
        versions = {"v1": {"versionid": "v1", "isdefault": True}, "v2": {"versionid": "v2", "isdefault": False}}
        tool.validate_seed_observation(fixture, model, groups, resources, versions)
        for place in range(4):
            args = [copy.deepcopy(model), copy.deepcopy(groups), copy.deepcopy(resources), copy.deepcopy(versions)]
            args[place] = {}
            with self.subTest(place=place), self.assertRaises(tool.ReverseInteropError):
                tool.validate_seed_observation(fixture, *args)

    def test_binary_stdout_is_exact_and_an_empty_document_is_not_an_error_exit(self) -> None:
        data = bytes.fromhex("00FF7F0D0A41")
        tool.validate_document(completed(data), data)
        tool.validate_document(completed(b""), b"")
        for result, expected in (
            (completed(data + b"\n"), data), (completed(data[:-1]), data),
            (completed(b"", 1), b""), (completed(data, stderr=b"warning"), data),
        ):
            with self.subTest(result=result), self.assertRaises(tool.ReverseInteropError):
                tool.validate_document(result, expected)

    def test_missing_native_executable_leaves_failed_evidence_not_a_success_shaped_report(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "output"
            code = tool.main(["--server", str(Path(temporary) / "absent.exe"),
                              "--oracle-build", str(Path(temporary) / "absent-build.json"),
                              "--data-root", str(Path(temporary) / "data"), "--output", str(output)])
            self.assertNotEqual(code, 0)
            report = json.loads((output / "evidence.json").read_bytes())
            self.assertEqual(report["status"], "failed")
            self.assertFalse(report["qualified"])
            self.assertEqual(report["passed"], 0)


class ReverseProcessBoundaryTests(unittest.TestCase):
    def test_native_server_exit_before_readiness_is_an_explicit_failure(self) -> None:
        process = mock.Mock()
        process.poll.return_value = 23
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with mock.patch.object(tool.server_helper.subprocess, "Popen", return_value=process), \
                    mock.patch.object(tool.server_helper, "request") as request:
                with self.assertRaisesRegex(RuntimeError, "exited during startup"):
                    tool.Server(root / "synthetic-native-server", root / "data", True, root / "server.log")
                request.assert_not_called()
                process.kill.assert_not_called()
            (root / "server.log").unlink()

    def test_owned_command_failure_and_output_limits_cannot_be_success(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            result = tool.run_command([sys.executable, "-c", "import sys; sys.exit(7)"], directory, {}, 10)
            self.assertEqual(result.returncode, 7)
            with self.assertRaises(tool.ReverseInteropError):
                tool.run_command([sys.executable, "-c", "print('x'*10000)"], directory, {}, 10, max_output=100)

    def test_owned_command_timeout_is_reported_and_process_is_reaped(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaises(tool.ReverseInteropError):
                tool.run_command([sys.executable, "-c", "import time; time.sleep(60)"],
                                 Path(temporary), {}, 0.1)

    def test_unreachable_loopback_endpoint_fails_instead_of_becoming_empty_metadata(self) -> None:
        port = tool.free_port()
        with self.assertRaises(tool.ReverseInteropError):
            tool.checked_request(port, "GET", "/registry", expected_status=200)

    def test_unexpected_http_status_retains_its_exact_wire_response_before_failure(self) -> None:
        captured = []
        response = (400, {"content-type": "application/problem+json"}, b'{"error":"real-mismatch"}')
        with mock.patch.object(tool.server_helper, "request", return_value=response):
            with self.assertRaises(tool.ReverseInteropError):
                tool.checked_request(12345, "POST", "/registry/dirs", expected_status=200, capture=captured.append)
        self.assertEqual(captured, [response])


if __name__ == "__main__":
    unittest.main()
