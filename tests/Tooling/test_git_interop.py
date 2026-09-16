# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

from __future__ import annotations

import copy
import contextlib
import hashlib
import http.client
import importlib.util
import io
import json
import re
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
from types import SimpleNamespace
import unittest
from unittest import mock
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("git_http_interop", ROOT / "eng" / "verify_git_http.py")
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = tool
SPEC.loader.exec_module(tool)


class ProbeResultTests(unittest.TestCase):
    """Synthetic accounting inputs only; these do not provide Git interoperability evidence."""

    def test_native_success_requires_zero_exit_complete_identity_and_exact_oracle_bytes(self) -> None:
        expected = {
            "objectFormat": "sha256", "selectedId": "a" * 64,
            "commitId": "b" * 64, "treeId": "c" * 64,
            "documents": [
                {"path": "xregistry/registry.json", "objectId": "d" * 64, "hex": "7B7D0A"},
                {"path": "xregistry/nested/raw.bin", "objectId": "e" * 64, "hex": "00FF0D0A41007F"},
            ],
        }
        case = tool.Case("sha256-v2-head", "sha256", "v2", "refs/heads/main", None, expected, None)
        repository = "http://127.0.0.1:12345/sha256-v2-head/repo.git"
        evidence = {
            "schemaVersion": 1, "nativeAot": True, "jitCompiledMethods": 0, "framework": "net10.0", "rid": "win-x64",
            "repository": repository, "revision": case.revision, "status": "snapshot", "snapshot": expected,
        }
        completed = subprocess.CompletedProcess([], 0, json.dumps(evidence).encode(), b"")
        self.assertEqual(tool.validate_probe_result(completed, case, repository, "net10.0", "win-x64"), evidence)
        for mutation in ("exit", "stderr", "empty", "jit", "jit-count", "partial", "bytes", "commit", "rid", "extra"):
            value = copy.deepcopy(evidence)
            code, stderr = 0, b""
            if mutation == "exit":
                code = 1
            elif mutation == "stderr":
                stderr = b"error after success output"
            elif mutation == "jit":
                value["nativeAot"] = False
            elif mutation == "jit-count":
                value["jitCompiledMethods"] = 1
            elif mutation == "partial":
                value["snapshot"].pop("documents")
            elif mutation == "bytes":
                value["snapshot"]["documents"][1]["hex"] = "00FF0D0A42007F"
            elif mutation == "commit":
                value["snapshot"]["commitId"] = "f" * 64
            elif mutation == "rid":
                value["rid"] = "linux-x64"
            elif mutation == "extra":
                value["ignoredError"] = "not allowed"
            raw = b"" if mutation == "empty" else json.dumps(value).encode()
            result = subprocess.CompletedProcess([], code, raw, stderr)
            with self.subTest(mutation=mutation), self.assertRaises(tool.InteropError):
                tool.validate_probe_result(result, case, repository, "net10.0", "win-x64")

    def test_expected_negative_requires_error_exit_and_exact_category_without_a_snapshot(self) -> None:
        case = tool.Case("sha1-v0-untrusted", "sha1", "v0", "refs/heads/main", None, None, "PolicyDenied")
        repository = "http://127.0.0.1:12345/sha1-v0-untrusted/repo.git"
        evidence = {
            "schemaVersion": 1, "nativeAot": True, "jitCompiledMethods": 0, "framework": "net10.0", "rid": "win-x64",
            "repository": repository, "revision": case.revision, "status": "git-error", "failure": "PolicyDenied",
        }
        self.assertEqual(
            tool.validate_probe_result(subprocess.CompletedProcess([], 3, json.dumps(evidence).encode(), b""),
                                       case, repository, "net10.0", "win-x64"),
            evidence,
        )
        for mutation in ("success-exit", "crash-exit", "wrong-category", "snapshot", "empty-json", "bool-schema"):
            value = copy.deepcopy(evidence)
            code = 3
            if mutation == "success-exit":
                code = 0
            elif mutation == "crash-exit":
                code = 1
            elif mutation == "wrong-category":
                value["failure"] = "RemoteFatal"
            elif mutation == "snapshot":
                value["snapshot"] = {}
            elif mutation == "empty-json":
                value = {}
            else:
                value["schemaVersion"] = True
            with self.subTest(mutation=mutation), self.assertRaises(tool.InteropError):
                tool.validate_probe_result(subprocess.CompletedProcess([], code, json.dumps(value).encode(), b""),
                                           case, repository, "net10.0", "win-x64")

    def test_duplicate_non_json_trailing_and_oversized_probe_output_is_rejected(self) -> None:
        case = tool.Case("sha1-v0-untrusted", "sha1", "v0", "refs/heads/main", None, None, "PolicyDenied")
        repository = "http://127.0.0.1:12345/sha1-v0-untrusted/repo.git"
        evidence = {
            "schemaVersion": 1, "nativeAot": True, "jitCompiledMethods": 0, "framework": "net10.0", "rid": "win-x64",
            "repository": repository, "revision": case.revision, "status": "git-error", "failure": "PolicyDenied",
        }
        valid = json.dumps(evidence).encode()
        exact = valid + b" " * (tool.MAX_PROBE_OUTPUT - len(valid))
        self.assertEqual(tool.validate_probe_result(subprocess.CompletedProcess([], 3, exact, b""), case, repository, "net10.0", "win-x64"), evidence)
        for raw in (
            exact + b" ", valid + b"{}", b'{"nativeAot":true,"nativeAot":false}',
            b'{"schemaVersion":NaN}', b'{"schemaVersion":Infinity}', b"\xff",
        ):
            with self.subTest(raw=raw[:40]), self.assertRaises(tool.InteropError):
                tool.validate_probe_result(subprocess.CompletedProcess([], 3, raw, b""), case, repository, "net10.0", "win-x64")

    def test_jit_negative_control_requires_specific_guard_rejection_not_usage_crash_or_network_failure(self) -> None:
        valid = subprocess.CompletedProcess([], 2, b"", tool.JIT_REJECTION + b"\r\n")
        tool.validate_jit_control(valid)
        for code, stdout, stderr in (
            (0, b"", tool.JIT_REJECTION), (1, b"", tool.JIT_REJECTION), (3, b"", tool.JIT_REJECTION),
            (2, b"unexpected success", tool.JIT_REJECTION), (2, b"", b"Usage: bad args"),
            (2, b"", b"Network refused connection"), (2.0, b"", tool.JIT_REJECTION),
        ):
            with self.subTest(code=code, stderr=stderr), self.assertRaises(tool.InteropError):
                tool.validate_jit_control(subprocess.CompletedProcess([], code, stdout, stderr))


def synthetic_references() -> dict:
    result = {}
    for algorithm, length in (("sha1", 40), ("sha256", 64)):
        def snapshot(commit):
            return {
                "objectFormat": algorithm, "selectedId": commit * length, "commitId": commit * length,
                "treeId": "c" * length,
                "documents": [
                    {"path": "xregistry/registry.json", "objectId": "d" * length, "hex": "7B7D0A"},
                    {"path": "xregistry/nested/raw.bin", "objectId": "e" * length, "hex": "00FF0D0A41007F"},
                ],
            }
        result[algorithm] = {"head": snapshot("a"), "historical": snapshot("b"), "tagObject": "f" * length}
    return result


class CasePlanTests(unittest.TestCase):
    def test_plan_contains_both_formats_protocols_exact_history_tag_oid_and_negative_policies(self) -> None:
        references = synthetic_references()
        cases = tool.build_cases(references)
        self.assertEqual(len(cases), 26)
        self.assertEqual(len({case.name for case in cases}), 26)
        self.assertEqual(sum(case.failure is None for case in cases), 16)
        self.assertEqual(sum(case.failure is not None for case in cases), 10)
        for algorithm in ("sha1", "sha256"):
            for protocol in ("v2", "v0"):
                selected = {case.name.rsplit("-", 1)[1]: case for case in cases
                            if case.object_format == algorithm and case.protocol == protocol}
                self.assertEqual(set(selected), {"head", "history", "tag", "oid", "missing", "wrongroot"}
                                 | ({"untrusted"} if algorithm == "sha1" else set()))
                self.assertEqual(selected["head"].snapshot, references[algorithm]["head"])
                self.assertEqual(selected["history"].revision, "refs/heads/history")
                self.assertEqual(selected["history"].snapshot["commitId"], references[algorithm]["historical"]["commitId"])
                self.assertEqual(selected["tag"].revision, "refs/tags/historical")
                self.assertEqual(selected["tag"].snapshot["selectedId"], references[algorithm]["tagObject"])
                self.assertEqual(selected["tag"].snapshot["commitId"], references[algorithm]["historical"]["commitId"])
                self.assertEqual(selected["oid"].revision, references[algorithm]["historical"]["commitId"])
                self.assertEqual(selected["missing"].failure, "PathNotFound")
                self.assertEqual(selected["wrongroot"].failure, "IntegrityMismatch")
                self.assertEqual(selected["head"].trusted_root is None, algorithm == "sha256")
        self.assertEqual(references, synthetic_references())


def packet(data: bytes) -> bytes:
    return f"{len(data) + 4:04x}".encode() + data


class WireEvidenceTests(unittest.TestCase):
    def test_cgi_success_preserves_binary_bytes_and_refuses_truncated_redirected_or_error_output(self) -> None:
        body = b"0008NAK\n0007\x01\x00\xff0000"
        raw = b"Expires: Fri, 01 Jan 1980 00:00:00 GMT\r\nContent-Type: application/x-git-upload-pack-result\r\n\r\n" + body
        self.assertEqual(tool.parse_cgi(raw, "application/x-git-upload-pack-result"), body)
        for value in (
            body,
            b"Status: 500 Failed\r\nContent-Type: application/x-git-upload-pack-result\r\n\r\n" + body,
            b"Location: https://elsewhere.invalid/\r\nContent-Type: application/x-git-upload-pack-result\r\n\r\n" + body,
            b"Content-Type: text/html\r\n\r\n" + body,
            b"Content-Type: application/x-git-upload-pack-result\r\nContent-Type: text/html\r\n\r\n" + body,
            b"Content-Type: application/x-git-upload-pack-result\r\nTransfer-Encoding: chunked\r\n\r\n" + body,
        ):
            with self.subTest(value=value[:60]), self.assertRaises(tool.InteropError):
                tool.parse_cgi(value, "application/x-git-upload-pack-result")

    def test_protocol_evidence_is_parsed_from_packets_not_requested_header_alone(self) -> None:
        v2 = packet(b"version 2\n") + packet(b"ls-refs=unborn\n") + packet(b"fetch=shallow\n") + packet(b"object-format=sha256\n") + b"0000"
        legacy = packet(b"# service=git-upload-pack\n") + b"0000" + packet(b"a" * 64 + b" HEAD\x00side-band-64k object-format=sha256\n") + b"0000"
        self.assertEqual(tool.advertisement_details(v2, "sha256"), {"advertisedProtocol": "v2", "advertisedObjectFormat": "sha256"})
        self.assertEqual(tool.advertisement_details(legacy, "sha256"), {"advertisedProtocol": "v0", "advertisedObjectFormat": "sha256"})
        with self.assertRaises(tool.InteropError):
            tool.advertisement_details(v2, "sha1")
        fetch = packet(b"command=fetch\n") + packet(b"object-format=sha256\n") + b"0001" + packet(b"want " + b"a" * 64 + b"\n") + packet(b"done\n") + b"0000"
        self.assertEqual(tool.request_details(fetch), {"command": "fetch", "wireProtocol": "v2", "wants": ["a" * 64], "refPrefixes": []})
        old_fetch = packet(b"want " + b"a" * 40 + b" side-band-64k\n") + b"0000" + packet(b"done\n")
        self.assertEqual(tool.request_details(old_fetch), {"command": "fetch", "wireProtocol": "v0", "wants": ["a" * 40], "refPrefixes": []})
        for raw in (b"", b"0003", b"00xx", b"0009shor", packet(b"command=fetch\n")[:-1]):
            with self.subTest(raw=raw), self.assertRaises(tool.InteropError):
                tool.packets(raw)

    def test_reference_pack_evidence_requires_real_pack_header_checksum_and_completion(self) -> None:
        for algorithm in ("sha1", "sha256"):
            content = b"PACK\x00\x00\x00\x02\x00\x00\x00\x01synthetic-tooling-payload"
            pack = content + hashlib.new(algorithm, content).digest()
            response = packet(b"packfile\n") + packet(b"\x01" + pack[:20]) + packet(b"\x02progress\n") + packet(b"\x01" + pack[20:]) + b"0000"
            self.assertEqual(tool.pack_details(response, algorithm), {
                "packBytes": len(pack), "packObjects": 1, "packSha256": hashlib.sha256(pack).hexdigest(),
            })
            for mutation in ("missing-pack", "bad-checksum", "truncated", "fatal", "too-many-objects"):
                if mutation == "missing-pack":
                    raw = packet(b"NAK\n") + b"0000"
                elif mutation == "truncated":
                    raw = response[:-4]
                elif mutation == "fatal":
                    raw = packet(b"packfile\n") + packet(b"\x03fatal\n") + b"0000"
                elif mutation == "too-many-objects":
                    bad = content[:8] + (257).to_bytes(4, "big") + content[12:]
                    raw = packet(b"packfile\n") + packet(b"\x01" + bad + hashlib.new(algorithm, bad).digest()) + b"0000"
                else:
                    raw = packet(b"packfile\n") + packet(b"\x01" + pack[:-1] + bytes([pack[-1] ^ 1])) + b"0000"
                with self.subTest(algorithm=algorithm, mutation=mutation), self.assertRaises(tool.InteropError):
                    tool.pack_details(raw, algorithm)


COMMANDS = {
    "v2": {
        "head": ["advertise", "ls-refs", "fetch"], "history": ["advertise", "ls-refs", "fetch"],
        "tag": ["advertise", "ls-refs", "fetch"], "oid": ["advertise", "fetch"],
        "missing": ["advertise", "ls-refs"], "wrongroot": ["advertise", "ls-refs", "fetch"],
        "untrusted": ["advertise"],
    },
    "v0": {
        "head": ["advertise", "fetch"], "history": ["advertise", "fetch"],
        "tag": ["advertise", "fetch"], "oid": ["advertise", "fetch"],
        "missing": ["advertise"], "wrongroot": ["advertise", "fetch"], "untrusted": ["advertise"],
    },
}


def synthetic_trace(case) -> list[dict]:
    records = []
    for command in COMMANDS[case.protocol][case.name.rsplit("-", 1)[1]]:
        record = {
            "caseId": case.name, "objectFormat": case.object_format,
            "command": command, "method": "GET" if command == "advertise" else "POST",
            "requestedProtocol": "version=2", "forwardedProtocol": "version=2" if case.protocol == "v2" else None,
            "status": 200, "backendExitCode": 0, "backendStderrBytes": 0, "error": None,
            "responseBytes": 100, "responseSha256": "a" * 64,
            "requestBytes": 0 if command == "advertise" else 100,
            "requestSha256": hashlib.sha256(b"").hexdigest() if command == "advertise" else "b" * 64,
        }
        if command == "advertise":
            record.update(advertisedProtocol=case.protocol, advertisedObjectFormat=case.object_format)
        else:
            record.update(wireProtocol=case.protocol, wants=[case.wanted] if command == "fetch" else [],
                          refPrefixes=[case.revision] if command == "ls-refs" else [])
            if command == "fetch":
                record.update(packBytes=123, packObjects=8, packSha256="c" * 64)
        records.append(record)
    return records


class CompletionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.cases = tool.build_cases(synthetic_references())
        self.base = "http://127.0.0.1:12345"
        self.requests = [record for case in self.cases for record in synthetic_trace(case)]
        self.observations = []
        for case in self.cases:
            document = {
                "schemaVersion": 1, "nativeAot": True, "jitCompiledMethods": 0, "framework": "net10.0", "rid": "win-x64",
                "repository": f"{self.base}/{case.name}/repo.git", "revision": case.revision,
                "status": "snapshot" if case.failure is None else "git-error",
            }
            if case.failure is None:
                document["snapshot"] = case.snapshot
            else:
                document["failure"] = case.failure
            self.observations.append({
                "caseId": case.name, "exitCode": 0 if case.failure is None else 3,
                "stdout": json.dumps(document), "stderr": "",
            })

    def verify(self, *, cases=None, observations=None, requests=None, errors=None):
        return tool.validate_completion(
            self.cases if cases is None else cases,
            self.observations if observations is None else observations,
            self.requests if requests is None else requests, [] if errors is None else errors,
            self.base, "net10.0", "win-x64",
        )

    def test_completion_requires_all_26_exact_cases_and_56_actual_protocol_exchanges(self) -> None:
        self.assertEqual(self.verify(), {
            "expectedCases": 26, "passedCases": 26, "successfulSnapshots": 16, "expectedRejections": 10,
            "httpRequests": 56, "v2Cases": 13, "v0Cases": 13, "sha1Cases": 14, "sha256Cases": 12,
        })

    def test_empty_missing_duplicate_extra_and_server_error_evidence_never_becomes_green(self) -> None:
        for observations in ([], self.observations[:-1], self.observations + [self.observations[0]],
                             self.observations[:-1] + [self.observations[0]]):
            with self.subTest(count=len(observations)), self.assertRaises(tool.InteropError):
                self.verify(observations=observations)
        for requests in ([], self.requests[:-1], self.requests + [self.requests[0]],
                         self.requests[:-1] + [self.requests[0]]):
            with self.subTest(count=len(requests)), self.assertRaises(tool.InteropError):
                self.verify(requests=requests)
        for cases in ((), self.cases[:-1], self.cases[:-1] + (self.cases[0],)):
            with self.subTest(count=len(cases)), self.assertRaises(tool.InteropError):
                self.verify(cases=cases)
        with self.assertRaisesRegex(tool.InteropError, "service errors"):
            self.verify(errors=["CGI child timed out"])

    def test_success_shaped_json_with_error_exit_or_missing_native_case_body_is_rejected(self) -> None:
        for field, value in (("exitCode", 1), ("exitCode", False), ("stdout", ""), ("stderr", "backend error")):
            observations = copy.deepcopy(self.observations)
            observations[0][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(tool.InteropError):
                self.verify(observations=observations)

    def test_protocol_headers_alone_wrong_ref_want_and_failed_cgi_never_qualify(self) -> None:
        case = next(case for case in self.cases if case.name == "sha1-v2-history")
        for index, field, value in (
            (0, "advertisedProtocol", "v0"), (0, "advertisedObjectFormat", "sha256"),
            (0, "forwardedProtocol", None), (0, "requestedProtocol", None), (0, "backendExitCode", 1),
            (0, "backendStderrBytes", 10), (0, "status", 500), (0, "error", "CGI failed"),
            (0, "responseSha256", ""), (0, "requestSha256", ""), (0, "requestBytes", 1),
            (1, "refPrefixes", ["refs/heads/main"]), (1, "wireProtocol", "v0"),
            (2, "wants", ["a" * 40]), (2, "packSha256", ""), (2, "packBytes", 0),
            (2, "requestBytes", tool.MAX_REQUEST + 1),
        ):
            requests = synthetic_trace(case)
            requests[index][field] = value
            with self.subTest(field=field, index=index), self.assertRaises(tool.InteropError):
                tool.validate_requests(case, requests)
        missing = next(case for case in self.cases if case.name == "sha256-v0-missing")
        with self.assertRaisesRegex(tool.InteropError, "fallback"):
            tool.validate_requests(missing, synthetic_trace(missing) + synthetic_trace(case)[-1:])

    def test_forced_legacy_must_strip_protocol_header_and_emit_legacy_commands(self) -> None:
        case = next(case for case in self.cases if case.name == "sha256-v0-tag")
        tool.validate_requests(case, synthetic_trace(case))
        for field, value in (("forwardedProtocol", "version=2"), ("wireProtocol", "v2")):
            requests = synthetic_trace(case)
            requests[1][field] = value
            with self.subTest(field=field), self.assertRaises(tool.InteropError):
                tool.validate_requests(case, requests)


class IsolationTests(unittest.TestCase):
    def test_environment_drops_ambient_git_config_credentials_hooks_proxy_and_runtime_injection(self) -> None:
        with mock.patch.dict(os.environ, {
            "GIT_DIR": "outside", "GIT_CONFIG_PARAMETERS": "'core.hooksPath=outside'",
            "GIT_EXEC_PATH": "outside", "GIT_CONFIG_COUNT": "999", "GIT_CONFIG_GLOBAL": "outside",
            "GIT_ALTERNATE_OBJECT_DIRECTORIES": "outside", "GIT_PROTOCOL": "version=1",
            "HTTP_PROXY": "http://outside", "SSH_AUTH_SOCK": "outside", "LD_PRELOAD": "outside",
            "DOTNET_STARTUP_HOOKS": "outside", "PATH": "outside", "HOME": "outside",
        }):
            environment = tool.minimal_environment(Path("isolated-home"), "no-executables")
        self.assertEqual(environment["PATH"], "no-executables")
        self.assertEqual(environment["HOME"], "isolated-home")
        self.assertEqual(environment["USERPROFILE"], "isolated-home")
        self.assertEqual(environment["TMPDIR"], "isolated-home")
        self.assertEqual(set(environment) - {"SYSTEMROOT", "WINDIR", "SYSTEMDRIVE"},
                         {"PATH", "HOME", "USERPROFILE", "XDG_CONFIG_HOME", "TMP", "TEMP", "TMPDIR", "LANG", "LC_ALL"})

    def test_routes_cannot_escape_fixture_roots_invoke_receive_pack_or_change_service(self) -> None:
        cases = {case.name: case for case in tool.build_cases(synthetic_references())}
        selected, resource = tool.route_request("/sha256-v2-head/repo.git/info/refs?service=git-upload-pack", "GET", cases)
        self.assertEqual(selected.name, "sha256-v2-head")
        self.assertEqual(resource, "info/refs")
        for target, method in (
            ("/sha256-v2-head/repo.git/info/refs?service=git-receive-pack", "GET"),
            ("/sha256-v2-head/repo.git/git-receive-pack", "POST"),
            ("/sha256-v2-head/repo.git/git-upload-pack?service=git-upload-pack", "POST"),
            ("/sha256-v2-head/repo.git/git-upload-pack", "GET"),
            ("/sha256-v2-head/repo.git/../config", "GET"),
            ("/sha256-v2-head/repo.git/%2e%2e/config", "GET"),
            ("/unknown/repo.git/info/refs?service=git-upload-pack", "GET"),
            ("http://outside/sha256-v2-head/repo.git/git-upload-pack", "POST"),
            ("/sha256-v2-head/repo.git/git-upload-pack#extra", "POST"),
        ):
            with self.subTest(target=target), self.assertRaises(tool.InteropError):
                tool.route_request(target, method, cases)

    def test_process_execution_is_bounded_preserves_failure_and_does_not_use_a_shell(self) -> None:
        with tempfile.TemporaryDirectory(prefix="git-http-process-test-") as directory:
            root = Path(directory)
            environment = tool.minimal_environment(root, str(root))
            runner = tool.ProcessBudget(timeout=10)
            result = runner.run([sys.executable, "-c", "print('bounded oracle output')"], root, environment, timeout=5)
            self.assertEqual(result.returncode, 0)
            self.assertEqual(result.stdout.strip(), b"bounded oracle output")
            self.assertEqual(result.stderr, b"")
            result = runner.run([sys.executable, "-c", "import sys; sys.exit(7)"], root, environment, timeout=5)
            self.assertEqual(result.returncode, 7)
            with self.assertRaisesRegex(tool.InteropError, "process budget"):
                runner.run([sys.executable, "-c", "import time; time.sleep(60)"], root, environment, timeout=0.2)
            with self.assertRaisesRegex(tool.InteropError, "output budget"):
                runner.run([sys.executable, "-c", "print('too much output')"], root, environment, timeout=5, limit=4)
            with self.assertRaisesRegex(tool.InteropError, "absolute"):
                runner.run(["python", "-c", "pass"], root, environment)
            with self.assertRaisesRegex(tool.InteropError, "stdin budget"):
                runner.run([sys.executable, "-c", "pass"], root, environment, data=b"x" * (tool.MAX_REQUEST + 1))

    def test_run_and_process_count_deadlines_are_hard_limits(self) -> None:
        runner = tool.ProcessBudget(timeout=1)
        runner.deadline = time.monotonic() - 1
        with self.assertRaisesRegex(tool.InteropError, "overall deadline"):
            runner.remaining(10)
        runner = tool.ProcessBudget()
        runner.count = tool.MAX_PROCESSES
        with self.assertRaisesRegex(tool.InteropError, "count budget"):
            runner.run([sys.executable], ROOT, {})

    def test_setup_failure_writes_failed_evidence_without_reusing_an_old_pass(self) -> None:
        with tempfile.TemporaryDirectory(prefix="git-http-failure-test-") as directory:
            root = Path(directory)
            probe = root / ("probe.exe" if os.name == "nt" else "probe")
            probe.write_bytes(b"MZsynthetic" if os.name == "nt" else b"\x7fELFsynthetic")
            output = root / "reports"
            output.mkdir()
            prior = output / "old-evidence.json"
            prior.write_text('{"status":"passed","synthetic":true}')
            with mock.patch.object(tool, "ReferenceGit", side_effect=tool.InteropError("Reference Git unavailable")), \
                    self.assertRaisesRegex(tool.InteropError, "Reference Git unavailable"):
                tool.qualify(probe, "net10.0", tool.native_rid(), output, managed_control=probe)
            reports = list(output.glob("*\\evidence.json")) if os.name == "nt" else list(output.glob("*/evidence.json"))
            self.assertEqual(len(reports), 1)
            evidence = json.loads(reports[0].read_bytes())
            self.assertEqual(evidence["status"], "failed")
            self.assertEqual(evidence["caseResults"], [])
            self.assertEqual(evidence["errors"], ["Reference Git unavailable"])
            self.assertNotIn("totals", evidence)
            self.assertEqual(prior.read_text(), '{"status":"passed","synthetic":true}')

    def test_cli_reports_failure_with_nonzero_exit_instead_of_success_summary(self) -> None:
        stdout, stderr = io.StringIO(), io.StringIO()
        with mock.patch.object(tool, "qualify", side_effect=tool.InteropError("Native case incomplete")), \
                contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            code = tool.main(["--probe", "missing", "--managed-control", "missing", "--framework", "net10.0", "--rid", "win-x64"])
        self.assertEqual(code, 1)
        self.assertEqual(stdout.getvalue(), "")
        self.assertIn("Native case incomplete", stderr.getvalue())

    def test_http_readiness_and_invalid_bodies_fail_without_starting_git_and_server_is_joined(self) -> None:
        cases = tool.build_cases(synthetic_references())
        for mutation in ("oversized", "chunked", "duplicate-length", "wrong-media", "receive-pack"):
            with tempfile.TemporaryDirectory(prefix="git-http-server-test-") as directory:
                budget = mock.Mock()
                oracle = SimpleNamespace(root=Path(directory), budget=budget)
                service = tool.RunningServer(oracle, cases)
                try:
                    service.ready()
                    self.assertTrue(service.thread.is_alive())
                    connection = http.client.HTTPConnection("127.0.0.1", service.server.server_port, timeout=3)
                    target = "/sha256-v2-head/repo.git/" + ("git-receive-pack" if mutation == "receive-pack" else "git-upload-pack")
                    connection.putrequest("POST", target)
                    connection.putheader("Git-Protocol", "version=2")
                    connection.putheader("Content-Type", "text/plain" if mutation == "wrong-media" else "application/x-git-upload-pack-request")
                    connection.putheader("Content-Length", str(tool.MAX_REQUEST + 1) if mutation == "oversized" else "4")
                    if mutation == "duplicate-length":
                        connection.putheader("Content-Length", "4")
                    elif mutation == "chunked":
                        connection.putheader("Transfer-Encoding", "chunked")
                    connection.endheaders(b"0000")
                    response = connection.getresponse()
                    with self.subTest(mutation=mutation):
                        self.assertEqual(response.status, 502)
                        self.assertIn(b"no qualification", response.read(256))
                        budget.run.assert_not_called()
                        self.assertEqual(len(service.server.errors), 1)
                    connection.close()
                finally:
                    service.close()
                self.assertFalse(service.thread.is_alive())


class ConfigurationTests(unittest.TestCase):
    def test_probe_is_a_nonpackable_native_test_console_for_both_library_frameworks_not_a_sample(self) -> None:
        directory = ROOT / "tests" / "XRegistry.Git.InteropProbe"
        project = ET.fromstring((directory / "XRegistry.Git.InteropProbe.csproj").read_bytes())
        properties = {child.tag: child.text for group in project.findall("PropertyGroup") for child in group}
        self.assertEqual(properties["TargetFrameworks"], "net8.0;net10.0")
        self.assertNotIn("TargetFramework", properties)
        self.assertEqual(properties["RuntimeIdentifiers"], "win-x64;linux-x64")
        self.assertEqual(properties["OutputType"], "Exe")
        self.assertEqual(properties["IsPackable"], "false")
        self.assertEqual(properties["IsAotCompatible"], "true")
        self.assertEqual(properties["PublishAot"], "true")
        self.assertEqual(properties["JsonSerializerIsReflectionEnabledByDefault"], "false")
        self.assertEqual([reference.attrib["Include"] for group in project.findall("ItemGroup") for reference in group],
                         ["..\\..\\src\\XRegistry.Bindings.Git\\XRegistry.Bindings.Git.csproj"])
        self.assertFalse((directory / "packages.lock.json").exists())

    def test_ci_git_job_is_independent_native_linux_both_tfms_pinned_and_without_privilege_or_docker(self) -> None:
        text = (ROOT / ".github" / "workflows" / "interop.yml").read_text()
        peer, following = text.split("\n  native-git-to-reference:", 1)
        job = re.split(r"\n  [A-Za-z0-9_-]+:", following, maxsplit=1)[0]
        self.assertIn("native-client-to-peer:", peer)
        self.assertIn("qualify-upstream.ps1", peer)
        self.assertNotIn("needs:", job)
        self.assertIn("framework: [net8.0, net10.0]", job)
        self.assertIn("fail-fast: false", job)
        self.assertIn("runs-on: ubuntu-latest", job)
        self.assertIn("persist-credentials: false", job)
        self.assertIn("-RuntimeIdentifier linux-x64 -Framework $env:PROBE_FRAMEWORK", job)
        self.assertIn("if-no-files-found: error", job)
        self.assertIn("test_git_interop*.py", job)
        self.assertIn("actions/upload-artifact@b7c566a772e6b6bfb58ed0dc250532a479d7789f", job)
        permissions = re.search(r"(?m)^    permissions:\n((?:      [^\n]*\n)+)", job)
        self.assertIsNotNone(permissions)
        self.assertEqual(permissions.group(1).strip(), "contents: read")
        for forbidden in ("docker", "continue-on-error", "id-token:", "secrets.", "pull_request_target"):
            self.assertNotIn(forbidden, job)

    def test_probe_uses_normal_restore_without_source_tree_lockfiles(self) -> None:
        self.assertFalse((ROOT / "tests" / "XRegistry.Git.InteropProbe" / "InteropRestore.props").exists())
        script = (ROOT / "eng" / "test-git-interop.ps1").read_text()
        self.assertNotIn("RestoreLockedMode", script)
        self.assertNotIn("NuGetLockFilePath", script)
        self.assertNotIn("packages.lock.json", script)
        self.assertIn("dotnet publish", script)
        self.assertIn("-o $nativeOutput", script)
        self.assertNotIn("vcvars", script)
        self.assertNotIn("slnx", script)


if __name__ == "__main__":
    unittest.main()
