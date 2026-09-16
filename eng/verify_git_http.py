#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Qualify a native managed Git probe against an isolated reference Git HTTP backend."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
import argparse
import copy
import ctypes
import hashlib
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time
from typing import Any
from urllib.parse import urlsplit
import uuid


MAX_PROBE_OUTPUT = 32 * 1024
MAX_REQUEST = 16 * 1024
MAX_RESPONSE = 4 * 1024 * 1024
MAX_REQUESTS = 64
MAX_PROCESSES = 256
RUN_TIMEOUT = 300
CGI_TIMEOUT = 15
PROBE_TIMEOUT = 30
ROOT_BYTES = {
    "historical": b'{"registryid":"git-http-oracle","fixture":"historical"}\n',
    "head": b'{"registryid":"git-http-oracle","fixture":"head"}\n',
}
BINARY_BYTES = {
    "historical": b"\x00\xff\r\nA\x00\x7f\x80binary\n",
    "head": b"\x00\xff\r\nB\x00\x7f\x80new-head\n",
}
DOCUMENT_PATHS = ("xregistry/registry.json", "xregistry/nested/raw.bin")
JIT_REJECTION = b"Managed Git interoperability requires the published NativeAOT executable, not a JIT apphost."


class InteropError(ValueError):
    """The controlled interoperability experiment did not qualify."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise InteropError(message)


def unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        require(key not in result, f"Duplicate evidence property: {key}")
        result[key] = value
    return result


def invalid_constant(value: str) -> None:
    raise InteropError(f"Non-JSON evidence value: {value}")


@dataclass(frozen=True)
class Case:
    name: str
    object_format: str
    protocol: str
    revision: str
    trusted_root: str | None
    snapshot: dict[str, Any] | None
    failure: str | None
    wanted: str | None = None


CASE_KINDS = ("head", "history", "tag", "oid", "missing", "wrongroot")
EXPECTED_CASE_IDS = frozenset(
    f"{algorithm}-{protocol}-{kind}"
    for algorithm in ("sha1", "sha256")
    for protocol in ("v2", "v0")
    for kind in CASE_KINDS + (("untrusted",) if algorithm == "sha1" else ())
)


def build_cases(references: dict[str, Any]) -> tuple[Case, ...]:
    cases = []
    for algorithm in ("sha1", "sha256"):
        reference = references[algorithm]
        for protocol in ("v2", "v0"):
            for kind in CASE_KINDS + (("untrusted",) if algorithm == "sha1" else ()):
                historical = kind in ("history", "tag", "oid")
                snapshot = copy.deepcopy(reference["historical" if historical else "head"])
                revision = {
                    "head": "refs/heads/main", "history": "refs/heads/history",
                    "tag": "refs/tags/historical", "oid": reference["historical"]["commitId"],
                    "missing": "refs/heads/absent", "wrongroot": "refs/heads/main",
                    "untrusted": "refs/heads/main",
                }[kind]
                if kind == "tag":
                    snapshot["selectedId"] = reference["tagObject"]
                trust = hashlib.sha256(bytes.fromhex(snapshot["documents"][0]["hex"])).hexdigest() if algorithm == "sha1" else None
                failure = {"missing": "PathNotFound", "wrongroot": "IntegrityMismatch", "untrusted": "PolicyDenied"}.get(kind)
                if kind == "wrongroot":
                    trust = "0" * 64
                elif kind == "untrusted":
                    trust = None
                wanted = snapshot["selectedId"] if kind not in ("missing", "untrusted") else None
                cases.append(Case(
                    f"{algorithm}-{protocol}-{kind}", algorithm, protocol, revision, trust,
                    snapshot if failure is None else None, failure, wanted,
                ))
    require(len(cases) == 26 and {case.name for case in cases} == EXPECTED_CASE_IDS, "Incomplete Git interoperability case plan.")
    return tuple(cases)


def validate_probe_result(
    result: subprocess.CompletedProcess[bytes], case: Case, repository: str, framework: str, rid: str
) -> dict[str, Any]:
    expected_exit = 0 if case.failure is None else 3
    require(type(result.returncode) is int and result.returncode == expected_exit, f"{case.name}: native exit {result.returncode}, expected {expected_exit}.")
    require(result.stderr == b"", f"{case.name}: unexpected native stderr.")
    require(0 < len(result.stdout) <= MAX_PROBE_OUTPUT, f"{case.name}: native evidence is empty or oversized.")
    try:
        document = json.loads(
            result.stdout.decode("utf-8"),
            object_pairs_hook=unique_object, parse_constant=invalid_constant,
        )
    except (UnicodeError, json.JSONDecodeError) as error:
        raise InteropError(f"{case.name}: malformed native JSON evidence.") from error
    common = {
        "schemaVersion": 1, "nativeAot": True, "jitCompiledMethods": 0, "framework": framework, "rid": rid,
        "repository": repository, "revision": case.revision,
        "status": "snapshot" if case.failure is None else "git-error",
    }
    expected = {**common, "snapshot": case.snapshot} if case.failure is None else {**common, "failure": case.failure}
    require(
        isinstance(document, dict) and type(document.get("schemaVersion")) is int
        and document.get("nativeAot") is True and type(document.get("jitCompiledMethods")) is int
        and document == expected,
        f"{case.name}: native identity, exact oracle objects/bytes or failure category did not match.",
    )
    return document


def validate_jit_control(result: subprocess.CompletedProcess[bytes]) -> None:
    require(
        type(result.returncode) is int and result.returncode == 2
        and result.stdout == b"" and result.stderr.strip() == JIT_REJECTION,
        "The JIT negative control was not explicitly rejected by the native-only guard.",
    )


def packets(raw: bytes) -> list[bytes | int]:
    require(0 < len(raw) <= MAX_RESPONSE, "Git packet evidence is empty or oversized.")
    result: list[bytes | int] = []
    offset = 0
    while offset < len(raw):
        require(len(result) < 1024, "Git packet count exceeded.")
        header = raw[offset:offset + 4]
        require(re.fullmatch(b"[0-9a-fA-F]{4}", header) is not None, "Invalid or truncated pkt-line header.")
        size = int(header, 16)
        offset += 4
        if size in (0, 1, 2):
            result.append(size)
        else:
            require(4 <= size <= 65520 and offset + size - 4 <= len(raw), "Invalid or truncated pkt-line body.")
            result.append(raw[offset:offset + size - 4])
            offset += size - 4
    return result


def parse_cgi(raw: bytes, media_type: str) -> bytes:
    require(0 < len(raw) <= MAX_RESPONSE + 8192, "CGI response byte budget exceeded.")
    header, separator, body = raw.partition(b"\r\n\r\n")
    require(bool(separator) and len(header) <= 8192, "CGI headers are missing or oversized.")
    fields = {}
    for line in header.split(b"\r\n"):
        name, separator, value = line.partition(b":")
        require(bool(separator) and re.fullmatch(b"[A-Za-z-]+", name) is not None, "Malformed CGI header.")
        key = name.decode("ascii").lower()
        require(key not in fields, "Duplicate CGI header.")
        fields[key] = value.strip().decode("ascii")
    require(fields.get("status", "200 OK") == "200 OK", "Git CGI returned a non-success status.")
    require(not ({"location", "transfer-encoding", "content-encoding"} & fields.keys()), "Unexpected CGI redirect or encoding.")
    require(fields.get("content-type") == media_type, "Wrong Git CGI content type.")
    if "content-length" in fields:
        require(fields["content-length"] == str(len(body)), "CGI Content-Length mismatch.")
    require(0 < len(body) <= MAX_RESPONSE, "Git CGI body is empty or oversized.")
    return body


def advertisement_details(raw: bytes, algorithm: str) -> dict[str, str]:
    framed = packets(raw)
    require(framed[-1] in (0, 2), "Incomplete Git advertisement.")
    lines = [item.rstrip(b"\n") for item in framed if isinstance(item, bytes)]
    if lines and lines[0] == b"# service=git-upload-pack":
        lines = lines[1:]
    require(bool(lines), "No Git advertisement data.")
    if lines[0] == b"version 2":
        protocol = "v2"
        capabilities = lines[1:]
    else:
        protocol = "v1" if lines[0] == b"version 1" else "v0"
        if protocol == "v1":
            lines = lines[1:]
        require(bool(lines), "No legacy Git refs.")
        first_oid = lines[0].split(b" ", 1)[0]
        require(re.fullmatch(b"[0-9a-f]{40}|[0-9a-f]{64}", first_oid) is not None, "Invalid advertised Git object ID.")
        _, separator, capability_bytes = lines[0].partition(b"\0")
        require(bool(separator), "No legacy capabilities.")
        capabilities = capability_bytes.split(b" ")
    formats = [item.removeprefix(b"object-format=").decode("ascii")
               for item in capabilities if item.startswith(b"object-format=")]
    actual_format = formats[0] if len(formats) == 1 else "sha1" if not formats else None
    require(actual_format == algorithm, "The reference Git advertised a different object format.")
    return {"advertisedProtocol": protocol, "advertisedObjectFormat": algorithm}


def request_details(raw: bytes) -> dict[str, Any]:
    require(len(raw) <= MAX_REQUEST, "Git request body budget exceeded.")
    framed = packets(raw)
    lines = [item.decode("ascii").rstrip("\n") for item in framed if isinstance(item, bytes)]
    require(bool(lines), "Empty Git command.")
    if lines[0].startswith("command="):
        protocol, command = "v2", lines[0].removeprefix("command=")
        require(command in ("ls-refs", "fetch") and framed[-1] == 0, "Unsupported or incomplete Git v2 command.")
    else:
        protocol, command = "v0", "fetch"
        require(lines[0].startswith("want ") and lines[-1] == "done", "Unsupported or incomplete legacy fetch.")
    wants = [line.split(" ")[1] for line in lines if line.startswith("want ")]
    require(all(re.fullmatch("[0-9a-f]{40}|[0-9a-f]{64}", oid) is not None for oid in wants), "Malformed requested object ID.")
    return {
        "command": command, "wireProtocol": protocol, "wants": wants,
        "refPrefixes": [line.removeprefix("ref-prefix ") for line in lines if line.startswith("ref-prefix ")],
    }


def pack_details(raw: bytes, algorithm: str) -> dict[str, Any]:
    framed = packets(raw)
    require(framed[-1] in (0, 2), "Reference pack response did not terminate.")
    pack = bytearray()
    started = False
    for item in framed:
        if not isinstance(item, bytes):
            continue
        if item in (b"packfile\n", b"NAK\n") or item.startswith(b"ACK "):
            started = True
        elif started and item[:1] == b"\x01":
            pack.extend(item[1:])
        elif started and item[:1] == b"\x03":
            raise InteropError("Reference upload-pack returned a fatal sideband.")
    width = 20 if algorithm == "sha1" else 32
    require(len(pack) > 12 + width and pack[:4] == b"PACK", "No actual reference Git pack data.")
    require(int.from_bytes(pack[4:8], "big") in (2, 3), "Unsupported reference pack version.")
    objects = int.from_bytes(pack[8:12], "big")
    require(0 < objects <= 256, "Reference pack object-count budget exceeded.")
    require(hashlib.new(algorithm, pack[:-width]).digest() == pack[-width:], "Reference Git pack checksum mismatch.")
    return {"packBytes": len(pack), "packObjects": objects, "packSha256": hashlib.sha256(pack).hexdigest()}


class WindowsJob:
    """Contain each suspended oracle/probe process before it can create descendants."""

    def __init__(self) -> None:
        from ctypes import wintypes

        class ThreadEntry(ctypes.Structure):
            _fields_ = [
                ("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD),
                ("th32ThreadID", wintypes.DWORD), ("th32OwnerProcessID", wintypes.DWORD),
                ("tpBasePri", wintypes.LONG), ("tpDeltaPri", wintypes.LONG), ("dwFlags", wintypes.DWORD),
            ]

        self.entry_type = ThreadEntry
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        signatures = {
            "CreateJobObjectW": ([ctypes.c_void_p, wintypes.LPCWSTR], wintypes.HANDLE),
            "OpenProcess": ([wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE),
            "AssignProcessToJobObject": ([wintypes.HANDLE, wintypes.HANDLE], wintypes.BOOL),
            "CreateToolhelp32Snapshot": ([wintypes.DWORD, wintypes.DWORD], wintypes.HANDLE),
            "Thread32First": ([wintypes.HANDLE, ctypes.POINTER(ThreadEntry)], wintypes.BOOL),
            "Thread32Next": ([wintypes.HANDLE, ctypes.POINTER(ThreadEntry)], wintypes.BOOL),
            "OpenThread": ([wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE),
            "ResumeThread": ([wintypes.HANDLE], wintypes.DWORD),
            "TerminateJobObject": ([wintypes.HANDLE, wintypes.UINT], wintypes.BOOL),
            "CloseHandle": ([wintypes.HANDLE], wintypes.BOOL),
        }
        for name, (arguments, result) in signatures.items():
            function = getattr(self.kernel, name)
            function.argtypes, function.restype = arguments, result
        self.handle = self.kernel.CreateJobObjectW(None, None)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())

    def attach_and_resume(self, pid: int) -> None:
        process = self.kernel.OpenProcess(0x0101, False, pid)  # SET_QUOTA | TERMINATE
        if not process:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            if not self.kernel.AssignProcessToJobObject(self.handle, process):
                raise ctypes.WinError(ctypes.get_last_error())
        finally:
            self.kernel.CloseHandle(process)
        snapshot = self.kernel.CreateToolhelp32Snapshot(0x00000004, 0)  # SNAPTHREAD
        if snapshot == ctypes.c_void_p(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())
        thread_ids = []
        try:
            entry = self.entry_type()
            entry.dwSize = ctypes.sizeof(entry)
            more = self.kernel.Thread32First(snapshot, ctypes.byref(entry))
            while more:
                if entry.th32OwnerProcessID == pid:
                    thread_ids.append(entry.th32ThreadID)
                more = self.kernel.Thread32Next(snapshot, ctypes.byref(entry))
        finally:
            self.kernel.CloseHandle(snapshot)
        require(len(thread_ids) == 1, "The suspended child must have exactly one initial thread.")
        thread = self.kernel.OpenThread(0x0002, False, thread_ids[0])  # SUSPEND_RESUME
        if not thread:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            require(self.kernel.ResumeThread(thread) == 1, "Could not resume the contained child process.")
        finally:
            self.kernel.CloseHandle(thread)

    def close(self) -> None:
        try:
            if not self.kernel.TerminateJobObject(self.handle, 1):
                raise ctypes.WinError(ctypes.get_last_error())
        finally:
            self.kernel.CloseHandle(self.handle)


class ProcessBudget:
    def __init__(self, timeout: float = RUN_TIMEOUT) -> None:
        self.deadline = time.monotonic() + timeout
        self.count = 0
        self.lock = threading.Lock()

    def remaining(self, maximum: float) -> float:
        remaining = min(maximum, self.deadline - time.monotonic())
        require(remaining > 0, "The interoperability experiment exceeded its overall deadline.")
        return remaining

    def run(
        self, arguments: list[str], cwd: Path, environment: dict[str, str], *,
        data: bytes = b"", timeout: float = CGI_TIMEOUT, limit: int = MAX_RESPONSE,
    ) -> subprocess.CompletedProcess[bytes]:
        require(Path(arguments[0]).is_absolute(), "Child executable must be an absolute resolved path.")
        require(len(data) <= MAX_REQUEST, "Subprocess stdin budget exceeded.")
        with self.lock:
            self.count += 1
            require(self.count <= MAX_PROCESSES, "Subprocess count budget exceeded.")
        timeout = self.remaining(timeout)
        with tempfile.TemporaryFile() as stdout, tempfile.TemporaryFile() as stderr:
            job = WindowsJob() if os.name == "nt" else None
            process = None
            try:
                process = subprocess.Popen(
                    arguments, cwd=cwd, env=environment, stdin=subprocess.PIPE, stdout=stdout, stderr=stderr,
                    start_new_session=os.name != "nt", creationflags=0x00000004 if os.name == "nt" else 0,
                )
                if job is not None:
                    job.attach_and_resume(process.pid)
                try:
                    process.communicate(data, timeout=timeout)
                except subprocess.TimeoutExpired as error:
                    raise InteropError(f"{Path(arguments[0]).name} exceeded its {timeout:.1f}s process budget.") from error
            finally:
                try:
                    if job is not None:
                        job.close()
                    if process is not None and process.poll() is None:
                        if os.name == "nt":
                            process.kill()
                        else:
                            os.killpg(process.pid, signal.SIGKILL)
                        process.wait(timeout=5)
                finally:
                    if process is not None and process.stdin is not None:
                        process.stdin.close()
            stdout.seek(0)
            stderr.seek(0)
            out, err = stdout.read(limit + 1), stderr.read(8193)
            require(len(out) <= limit and len(err) <= 8192, "Subprocess output budget exceeded.")
            return subprocess.CompletedProcess(arguments, process.returncode, out, err)


def minimal_environment(home: Path, path: str) -> dict[str, str]:
    environment = {
        key: value for key, value in os.environ.items()
        if key.upper() in {"SYSTEMROOT", "WINDIR", "SYSTEMDRIVE"}
    }
    environment.update({
        "PATH": path, "HOME": str(home), "USERPROFILE": str(home),
        "XDG_CONFIG_HOME": str(home), "TMP": str(home), "TEMP": str(home), "TMPDIR": str(home),
        "LANG": "C", "LC_ALL": "C",
    })
    return environment


def hash_file(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


class ReferenceGit:
    def __init__(self, root: Path, budget: ProcessBudget) -> None:
        executable = shutil.which("git")
        require(executable is not None, "Installed reference Git is required; it is only a test oracle.")
        self.git = Path(executable).resolve(strict=True)
        require(self.git.is_file(), "Reference Git executable is missing.")
        self.root, self.budget = root, budget
        home, empty = root / "home", root / "empty"
        home.mkdir()
        empty.mkdir()
        config = home / "empty.gitconfig"
        config.write_bytes(b"")
        self.environment = minimal_environment(home, str(self.git.parent))
        self.environment.update({
            "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": str(config), "GIT_ATTR_NOSYSTEM": "1",
            "GIT_TERMINAL_PROMPT": "0", "GCM_INTERACTIVE": "never",
            "GIT_AUTHOR_NAME": "Git Interoperability Oracle", "GIT_AUTHOR_EMAIL": "git-oracle@example.invalid",
            "GIT_COMMITTER_NAME": "Git Interoperability Oracle", "GIT_COMMITTER_EMAIL": "git-oracle@example.invalid",
            "GIT_AUTHOR_DATE": "1700000000 +0000", "GIT_COMMITTER_DATE": "1700000000 +0000",
        })
        settings = {
            "core.hooksPath": str(empty), "core.autocrlf": "false", "core.attributesFile": str(config),
            "commit.gpgSign": "false", "tag.gpgSign": "false", "credential.helper": "",
            "user.name": "Git Interoperability Oracle", "user.email": "git-oracle@example.invalid",
            "http.receivepack": "false", "protocol.version": "2", "gc.auto": "0",
        }
        self.environment["GIT_CONFIG_COUNT"] = str(len(settings))
        for index, (key, value) in enumerate(settings.items()):
            self.environment[f"GIT_CONFIG_KEY_{index}"] = key
            self.environment[f"GIT_CONFIG_VALUE_{index}"] = value
        self.version = self.command("--version").decode("ascii").strip()
        require(re.fullmatch(r"git version [0-9][0-9A-Za-z.+-]*", self.version) is not None, "Unexpected Git version response.")
        self.exec_path = Path(self.command("--exec-path").decode("utf-8").strip()).resolve(strict=True)
        require(self.exec_path.is_dir(), "Git's executable directory is missing.")
        self.backend = (self.exec_path / ("git-http-backend.exe" if os.name == "nt" else "git-http-backend")).resolve(strict=True)
        require(self.backend.is_file() and self.backend.parent == self.exec_path, "The exact reference Git CGI backend is missing.")
        oracle_path = [str(self.git.parent), str(self.exec_path)]
        if os.name == "nt":
            runtime_bin = self.exec_path.parent.parent / "bin"
            require(runtime_bin.is_dir(), "Git for Windows runtime directory is missing.")
            oracle_path.append(str(runtime_bin))
        else:
            oracle_path.extend(("/usr/bin", "/bin"))
        self.environment["PATH"] = os.pathsep.join(oracle_path)
        self.environment["GIT_EXEC_PATH"] = str(self.exec_path)
        self.empty = empty
        self.identities = {
            "gitPath": str(self.git), "gitVersion": self.version, "gitSha256": hash_file(self.git),
            "httpBackendPath": str(self.backend), "httpBackendSha256": hash_file(self.backend),
            "configuration": "isolated; no system/global config, hooks, signing, filters or credentials",
        }

    def command(self, *arguments: str, data: bytes = b"", repository: Path | None = None) -> bytes:
        command = [str(self.git)]
        if repository is not None:
            require(repository.parent == self.root and repository.name in ("sha1.git", "sha256.git"), "Oracle command escaped its fixture repository.")
            command.extend(("-C", str(repository)))
        result = self.budget.run(command + list(arguments), self.root, self.environment, data=data)
        require(result.returncode == 0, f"Reference Git {arguments[0]} failed ({result.returncode}): {result.stderr.decode('utf-8', 'replace')[:2048]}")
        return result.stdout

    def create(self, algorithm: str) -> dict[str, Any]:
        require(algorithm in ("sha1", "sha256"), "Unsupported fixture object format.")
        repository = self.root / f"{algorithm}.git"
        self.command("init", "--bare", f"--object-format={algorithm}", "--initial-branch=main",
                     "--template", str(self.empty), str(repository))
        require(self.command("rev-parse", "--show-object-format", repository=repository).strip() == algorithm.encode(), "Git initialized the wrong object format.")
        width = 40 if algorithm == "sha1" else 64

        def oid(*arguments: str, data: bytes = b"") -> str:
            value = self.command(*arguments, data=data, repository=repository).decode("ascii").strip()
            require(re.fullmatch(f"[0-9a-f]{{{width}}}", value) is not None, "Reference Git returned an invalid object ID.")
            return value

        def blob(data: bytes) -> str:
            return oid("hash-object", "-w", "--stdin", data=data)

        def tree(entries: list[tuple[str, str, str, str]]) -> str:
            return oid("mktree", data="".join(f"{mode} {kind} {value}\t{name}\n" for mode, kind, value, name in entries).encode("ascii"))

        decoy_root = blob(b'{"wrongRoot":true}\n')
        decoy_binary = blob(b"NOT-THE-XREGISTRY-ROOT")
        decoy_tree = tree([("100644", "blob", decoy_binary, "raw.bin")])
        reference = {}
        previous = None
        for index, name in enumerate(("historical", "head")):
            root_blob, binary_blob = blob(ROOT_BYTES[name]), blob(BINARY_BYTES[name])
            nested = tree([("100644", "blob", binary_blob, "raw.bin")])
            registry = tree([("100644", "blob", root_blob, "registry.json"), ("040000", "tree", nested, "nested")])
            root_tree = tree([
                ("100644", "blob", decoy_root, "registry.json"), ("040000", "tree", decoy_tree, "nested"),
                ("040000", "tree", registry, "xregistry"),
            ])
            self.environment["GIT_AUTHOR_DATE"] = self.environment["GIT_COMMITTER_DATE"] = f"{1700000000 + index * 60} +0000"
            arguments = ["commit-tree", root_tree, "-m", f"Controlled Git HTTP {name}"]
            if previous is not None:
                arguments.extend(("-p", previous))
            commit = oid(*arguments)
            ref = "refs/heads/history" if name == "historical" else "refs/heads/main"
            self.command("update-ref", ref, commit, repository=repository)
            require(oid("rev-parse", "--verify", ref) == commit, "Reference Git ref selection differs.")
            require(oid("rev-parse", "--verify", commit + "^{tree}") == root_tree, "Reference Git tree selection differs.")
            documents = []
            for path, expected in zip(DOCUMENT_PATHS, (ROOT_BYTES[name], BINARY_BYTES[name]), strict=True):
                object_id = oid("rev-parse", "--verify", commit + ":" + path)
                actual = self.command("cat-file", "blob", object_id, repository=repository)
                require(actual == expected, "Reference Git did not preserve the literal fixture bytes.")
                documents.append({"path": path, "objectId": object_id, "hex": actual.hex().upper()})
            reference[name] = {
                "objectFormat": algorithm, "selectedId": commit, "commitId": commit,
                "treeId": root_tree, "documents": documents,
            }
            previous = commit
        self.command("tag", "--annotate", "--no-sign", "--message", "Controlled historical annotated tag",
                     "historical", reference["historical"]["commitId"], repository=repository)
        reference["tagObject"] = oid("rev-parse", "--verify", "refs/tags/historical")
        require(self.command("cat-file", "-t", reference["tagObject"], repository=repository).strip() == b"tag", "The reference tag is not annotated.")
        require(oid("rev-parse", "--verify", "refs/tags/historical^{commit}") == reference["historical"]["commitId"], "Reference tag peeling differs.")
        require(oid("rev-parse", "--verify", "HEAD") == reference["head"]["commitId"] != reference["historical"]["commitId"], "The two-commit no-HEAD-substitution fixture is invalid.")
        require(self.command("rev-list", "--count", "HEAD", repository=repository).strip() == b"2", "The fixture must contain exactly two historical commits.")
        return reference


def expected_commands(case: Case) -> list[str]:
    commands = ["advertise"]
    if case.failure == "PolicyDenied":
        return commands
    if case.protocol == "v2" and case.revision.startswith("refs/"):
        commands.append("ls-refs")
    if case.failure != "PathNotFound":
        commands.append("fetch")
    return commands


def validate_requests(case: Case, requests: list[dict[str, Any]]) -> None:
    require([item.get("command") for item in requests] == expected_commands(case), f"{case.name}: incomplete, extra or fallback HTTP operations.")
    for request in requests:
        require(
            request.get("caseId") == case.name and request.get("objectFormat") == case.object_format
            and request.get("requestedProtocol") == "version=2"
            and request.get("forwardedProtocol") == ("version=2" if case.protocol == "v2" else None)
            and request.get("status") == 200 and type(request.get("backendExitCode")) is int
            and request["backendExitCode"] == 0 and type(request.get("backendStderrBytes")) is int
            and request["backendStderrBytes"] == 0
            and request.get("error") is None,
            f"{case.name}: the reference HTTP exchange failed or has the wrong protocol identity.",
        )
        require(
            type(request.get("responseBytes")) is int and 0 < request["responseBytes"] <= MAX_RESPONSE
            and re.fullmatch("[0-9a-f]{64}", request.get("responseSha256", "")) is not None
            and re.fullmatch("[0-9a-f]{64}", request.get("requestSha256", "")) is not None,
            f"{case.name}: missing bounded reference response evidence.",
        )
        if request["command"] == "advertise":
            require(
                request.get("method") == "GET" and request.get("advertisedProtocol") == case.protocol
                and request.get("advertisedObjectFormat") == case.object_format
                and type(request.get("requestBytes")) is int and request["requestBytes"] == 0
                and request["requestSha256"] == hashlib.sha256(b"").hexdigest(),
                f"{case.name}: the actual advertisement did not negotiate the expected protocol/format.",
            )
        else:
            require(
                request.get("method") == "POST" and request.get("wireProtocol") == case.protocol
                and type(request.get("requestBytes")) is int and 0 < request["requestBytes"] <= MAX_REQUEST,
                f"{case.name}: wrong or absent wire command evidence.",
            )
            if request["command"] == "ls-refs":
                require(request.get("refPrefixes") == [case.revision] and request.get("wants") == [], f"{case.name}: the exact requested ref was not queried.")
            else:
                require(request.get("wants") == [case.wanted], f"{case.name}: the fetch substituted a different wanted object.")
                require(
                    type(request.get("packBytes")) is int and request["packBytes"] > 12
                    and type(request.get("packObjects")) is int and 0 < request["packObjects"] <= 256
                    and re.fullmatch("[0-9a-f]{64}", request.get("packSha256", "")) is not None,
                    f"{case.name}: no independently checked reference pack evidence.",
                )


class GitServer(ThreadingHTTPServer):
    daemon_threads = False
    block_on_close = True
    allow_reuse_address = False
    request_queue_size = 4

    def __init__(self, oracle: ReferenceGit, cases: tuple[Case, ...]) -> None:
        self.oracle = oracle
        self.cases = {case.name: case for case in cases}
        self.requests: list[dict[str, Any]] = []
        self.errors: list[str] = []
        self.lock = threading.Lock()
        self.slots = threading.BoundedSemaphore(4)
        self.request_count = 0
        super().__init__(("127.0.0.1", 0), GitHandler)

    def error(self, message: str) -> None:
        with self.lock:
            if len(self.errors) < MAX_REQUESTS:
                self.errors.append(message[:1024])

    def process_request(self, request, client_address) -> None:
        if not self.slots.acquire(blocking=False):
            self.error("The reference service connection budget was exceeded.")
            self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except (OSError, RuntimeError):
            self.slots.release()
            raise

    def process_request_thread(self, request, client_address) -> None:
        try:
            super().process_request_thread(request, client_address)
        finally:
            self.slots.release()

    def handle_error(self, request, client_address) -> None:
        self.error("Unhandled reference request failure; no qualification credit.")

    def take_request(self) -> None:
        with self.lock:
            self.request_count += 1
            require(self.request_count <= MAX_REQUESTS, "Reference HTTP request count exceeded.")
        self.oracle.budget.remaining(CGI_TIMEOUT)


def route_request(target: str, method: str, cases: dict[str, Case]) -> tuple[Case, str]:
    parsed = urlsplit(target)
    require(not parsed.scheme and not parsed.netloc and not parsed.fragment, "Absolute or fragmented reference request target.")
    match = re.fullmatch(r"/([a-z0-9-]+)/repo\.git/(info/refs|git-upload-pack)", parsed.path)
    require(match is not None and match[1] in cases, "Unknown or unsafe reference Git route.")
    resource = match[2]
    require(
        (method == "GET" and resource == "info/refs" and parsed.query == "service=git-upload-pack")
        or (method == "POST" and resource == "git-upload-pack" and parsed.query == ""),
        "Only exact smart-HTTP upload-pack requests are allowed.",
    )
    return cases[match[1]], resource


class GitHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "XRegistryControlledGitOracle/1"
    sys_version = ""

    def setup(self) -> None:
        super().setup()
        self.connection.settimeout(5)

    def log_message(self, format: str, *args) -> None:
        pass

    def log_error(self, format: str, *args) -> None:
        self.server.error(format % args)

    def do_GET(self) -> None:
        self.exchange()

    def do_POST(self) -> None:
        self.exchange()

    def respond(self, status: int, media: str, body: bytes) -> None:
        self.close_connection = True
        self.send_response(status)
        self.send_header("Content-Type", media)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(body)

    def exchange(self) -> None:
        record = None
        try:
            self.server.take_request()
            require(self.client_address[0] == "127.0.0.1", "Non-loopback reference connection.")
            if self.command == "GET" and self.path == "/health":
                self.respond(200, "text/plain", b"controlled-git-http-ready\n")
                return
            case, resource = route_request(self.path, self.command, self.server.cases)
            require(self.headers.get_all("Git-Protocol") == ["version=2"], "Expected one bounded Git-Protocol header.")
            require(self.headers.get_all("Transfer-Encoding") is None and self.headers.get_all("Expect") is None, "Chunked/expect request bodies are not accepted.")
            lengths = self.headers.get_all("Content-Length", [])
            if self.command == "POST":
                require(len(lengths) == 1 and re.fullmatch("[0-9]{1,6}", lengths[0]) is not None, "Invalid request Content-Length.")
                length = int(lengths[0])
                require(0 < length <= MAX_REQUEST, "Reference request body budget exceeded.")
                require(self.headers.get_all("Content-Type") == ["application/x-git-upload-pack-request"], "Unexpected upload-pack request content type.")
                body = self.rfile.read(length)
                require(len(body) == length, "Truncated upload-pack request body.")
                details = request_details(body)
            else:
                require(not lengths or lengths == ["0"], "An advertisement request cannot have a body.")
                body, details = b"", {"command": "advertise"}
            forwarded = "version=2" if case.protocol == "v2" else None
            record = {
                "caseId": case.name, "objectFormat": case.object_format, "method": self.command,
                "target": self.path, "requestedProtocol": "version=2", "forwardedProtocol": forwarded,
                "requestBytes": len(body), "requestSha256": hashlib.sha256(body).hexdigest(),
                **details, "status": 502, "error": None,
            }
            environment = {
                **self.server.oracle.environment,
                "GIT_PROJECT_ROOT": str(self.server.oracle.root), "GIT_HTTP_EXPORT_ALL": "1",
                "GIT_HTTP_MAX_REQUEST_BUFFER": str(MAX_REQUEST),
                "PATH_INFO": f"/{case.object_format}.git/{resource}",
                "REQUEST_METHOD": self.command, "QUERY_STRING": "service=git-upload-pack" if self.command == "GET" else "",
                "CONTENT_TYPE": "application/x-git-upload-pack-request" if self.command == "POST" else "",
                "CONTENT_LENGTH": str(len(body)), "REMOTE_ADDR": "127.0.0.1",
                "SERVER_NAME": "127.0.0.1", "SERVER_PORT": str(self.server.server_port),
                "SERVER_PROTOCOL": "HTTP/1.1", "GATEWAY_INTERFACE": "CGI/1.1",
            }
            if forwarded is not None:
                environment["HTTP_GIT_PROTOCOL"] = forwarded
            result = self.server.oracle.budget.run(
                [str(self.server.oracle.backend)], self.server.oracle.root, environment,
                data=body, limit=MAX_RESPONSE + 8192,
            )
            record.update(backendExitCode=result.returncode, backendStderrBytes=len(result.stderr))
            require(result.returncode == 0 and not result.stderr, f"Reference git http-backend failed ({result.returncode}): {result.stderr.decode('utf-8', 'replace')[:2048]}")
            media = "application/x-git-upload-pack-advertisement" if self.command == "GET" else "application/x-git-upload-pack-result"
            response = parse_cgi(result.stdout, media)
            if details["command"] == "advertise":
                record.update(advertisement_details(response, case.object_format))
            elif details["command"] == "fetch":
                record.update(pack_details(response, case.object_format))
            else:
                require(packets(response)[-1] in (0, 2), "Reference ls-refs response did not terminate.")
            record.update(responseBytes=len(response), responseSha256=hashlib.sha256(response).hexdigest(), status=200)
            with self.server.lock:
                self.server.requests.append(record)
            self.respond(200, media, response)
        except (InteropError, OSError, UnicodeError, subprocess.SubprocessError) as error:
            self.server.error(str(error))
            if record is not None:
                record["error"] = str(error)[:1024]
                with self.server.lock:
                    if record not in self.server.requests:
                        self.server.requests.append(record)
            try:
                self.respond(502, "text/plain", b"Controlled reference Git service failed; no qualification.\n")
            except OSError as send_error:
                self.server.error(f"Could not return reference error: {send_error}")


class RunningServer:
    def __init__(self, oracle: ReferenceGit, cases: tuple[Case, ...]) -> None:
        self.server = GitServer(oracle, cases)
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": 0.1}, name="controlled-git-http")
        self.thread.start()
        self.base = f"http://127.0.0.1:{self.server.server_port}"

    def ready(self) -> None:
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=1)
            try:
                connection.request("GET", "/health")
                response = connection.getresponse()
                require(response.status == 200 and response.read(128) == b"controlled-git-http-ready\n", "Reference readiness returned the wrong identity.")
                require(self.thread.is_alive(), "Reference HTTP thread exited during startup.")
                return
            except (ConnectionError, TimeoutError):
                time.sleep(0.1)
            finally:
                connection.close()
        raise InteropError("Reference Git HTTP service did not become responsive within five seconds.")

    def close(self) -> None:
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)
        require(not self.thread.is_alive(), "Reference Git HTTP thread did not stop.")


def validate_completion(
    cases: tuple[Case, ...], observations: list[dict[str, Any]], requests: list[dict[str, Any]],
    errors: list[str], base: str, framework: str, rid: str,
) -> dict[str, int]:
    require(not errors, "Reference service errors prevent qualification.")
    require(len(cases) == 26 and {case.name for case in cases} == EXPECTED_CASE_IDS, "The exact 26-case plan is required.")
    require(
        len(observations) == 26 and {item.get("caseId") for item in observations} == EXPECTED_CASE_IDS,
        "Incomplete, duplicate or extra native case evidence.",
    )
    require(len(requests) == 56 and {item.get("caseId") for item in requests} == EXPECTED_CASE_IDS, "Incomplete or extra reference protocol evidence.")
    by_name = {item["caseId"]: item for item in observations}
    for case in cases:
        observed = by_name[case.name]
        result = subprocess.CompletedProcess(
            [], observed["exitCode"], observed["stdout"].encode("utf-8"), observed["stderr"].encode("utf-8"),
        )
        validate_probe_result(result, case, f"{base}/{case.name}/repo.git", framework, rid)
        validate_requests(case, [item for item in requests if item["caseId"] == case.name])
    return {
        "expectedCases": 26, "passedCases": 26, "successfulSnapshots": 16, "expectedRejections": 10,
        "httpRequests": 56, "v2Cases": 13, "v0Cases": 13, "sha1Cases": 14, "sha256Cases": 12,
    }


def native_rid() -> str:
    system = {"win32": "win", "linux": "linux"}.get(sys.platform)
    architecture = {"amd64": "x64", "x86_64": "x64", "aarch64": "arm64", "arm64": "arm64"}.get(platform.machine().lower())
    require(system is not None and architecture is not None, "This lane requires native Windows/Linux x64/ARM64, not emulation.")
    return f"{system}-{architecture}"


def qualify(probe: Path, framework: str, rid: str, output: Path, *, managed_control: Path) -> Path:
    require(native_rid() == rid, "Requested RID differs from the actual oracle/native-process host.")
    require(framework in ("net8.0", "net10.0"), "Unsupported probe target framework.")
    probe = probe.resolve(strict=True)
    require(probe.is_file(), "Native probe executable is missing.")
    managed_control = managed_control.resolve(strict=True)
    require(managed_control.is_file(), "The managed/JIT negative-control assembly is missing.")
    with probe.open("rb") as stream:
        magic = stream.read(4)
    require(magic.startswith(b"MZ") if os.name == "nt" else magic == b"\x7fELF", "Probe is not a platform executable.")
    destination = output.resolve() / f"{rid}-{framework}-{uuid.uuid4().hex}"
    destination.mkdir(parents=True, exist_ok=False)
    report = destination / "evidence.json"
    evidence: dict[str, Any] = {
        "schemaVersion": 1, "scope": "managed-git-smart-http-only", "status": "running",
        "startedAtUtc": datetime.now(timezone.utc).isoformat(),
        "framework": framework, "rid": rid, "probePath": str(probe), "probeSha256": hash_file(probe),
        "driverSha256": hash_file(Path(__file__)),
        "caseResults": [], "requests": [], "errors": [],
        "budgets": {
            "runSeconds": RUN_TIMEOUT, "cgiSeconds": CGI_TIMEOUT, "probeSeconds": PROBE_TIMEOUT,
            "requestBodyBytes": MAX_REQUEST, "responseBytes": MAX_RESPONSE,
            "maxHttpRequests": MAX_REQUESTS, "maxProcesses": MAX_PROCESSES,
        },
        "limits": "Literal-loopback controlled Git acquisition only; not xRegistry server interoperability, full conformance, SHA1DC or all-RID qualification.",
    }
    budget = ProcessBudget()
    try:
        with tempfile.TemporaryDirectory(prefix="xregistry-git-http-") as directory:
            root = Path(directory)
            oracle = ReferenceGit(root, budget)
            evidence["oracle"] = oracle.identities
            references = {algorithm: oracle.create(algorithm) for algorithm in ("sha1", "sha256")}
            evidence["referenceSnapshots"] = references
            cases = build_cases(references)
            home, path = root / "native-home", root / "no-executables"
            home.mkdir()
            path.mkdir()
            environment = minimal_environment(home, str(path))
            require(shutil.which("git", path=environment["PATH"]) is None, "Reference Git leaked onto the native probe PATH.")
            evidence["nativeGitOnPath"] = False
            evidence["nativeWorkingDirectory"] = "isolated empty directory"
            service = RunningServer(oracle, cases)
            try:
                service.ready()
                evidence["origin"] = service.base
                dotnet = shutil.which("dotnet")
                require(dotnet is not None, "The installed dotnet host is required for the JIT negative control.")
                runtime_config = managed_control.with_suffix(".runtimeconfig.json")
                with runtime_config.open("rb") as stream:
                    config_bytes = stream.read(MAX_PROBE_OUTPUT + 1)
                require(len(config_bytes) <= MAX_PROBE_OUTPUT, "JIT-control runtime configuration is oversized.")
                config = json.loads(config_bytes, object_pairs_hook=unique_object, parse_constant=invalid_constant)
                require(isinstance(config, dict) and isinstance(config.get("runtimeOptions"), dict), "Malformed JIT-control runtime configuration.")
                options = config["runtimeOptions"]
                require(options.get("tfm") == framework, "JIT-control target framework mismatch.")
                properties = options.get("configProperties")
                require(
                    isinstance(properties, dict)
                    and properties.get("System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported") is False,
                    "The JIT control must exercise native-publish settings with dynamic code disabled.",
                )
                before = service.server.request_count
                control = budget.run(
                    [str(Path(dotnet).resolve(strict=True)), str(managed_control), "--repository",
                     service.base + "/jit-control/repo.git", "--revision", "refs/heads/main"],
                    home, environment, timeout=PROBE_TIMEOUT, limit=MAX_PROBE_OUTPUT,
                )
                validate_jit_control(control)
                require(service.server.request_count == before and not service.server.errors, "The JIT negative control made an HTTP request.")
                evidence["jitNegativeControl"] = {
                    "managedAssemblySha256": hash_file(managed_control),
                    "runtimeConfigurationSha256": hashlib.sha256(config_bytes).hexdigest(),
                    "dynamicCodeDisabledByRuntimeConfig": True, "exitCode": control.returncode,
                    "stderr": control.stderr.decode("utf-8"), "httpRequests": service.server.request_count - before,
                }
                print("JIT negative control: native-only guard rejected execution before any HTTP request.")
                for case in cases:
                    uri = f"{service.base}/{case.name}/repo.git"
                    arguments = [str(probe), "--repository", uri, "--revision", case.revision]
                    if case.trusted_root is not None:
                        arguments.extend(("--trusted-root", case.trusted_root))
                    start = time.monotonic()
                    result = budget.run(arguments, home, environment, timeout=PROBE_TIMEOUT, limit=MAX_PROBE_OUTPUT)
                    observation = {
                        "caseId": case.name, "exitCode": result.returncode,
                        "stdout": result.stdout.decode("utf-8"), "stderr": result.stderr.decode("utf-8"),
                        "stdoutSha256": hashlib.sha256(result.stdout).hexdigest(),
                        "elapsedMilliseconds": round((time.monotonic() - start) * 1000),
                        "trustedRegistryRootSha256": case.trusted_root,
                    }
                    evidence["caseResults"].append(observation)
                    validate_probe_result(result, case, uri, framework, rid)
                    with service.server.lock:
                        requests = [dict(item) for item in service.server.requests if item["caseId"] == case.name]
                    validate_requests(case, requests)
                    print(f"{case.name}: matched independent oracle ({'snapshot' if case.failure is None else case.failure}).")
            finally:
                service.close()
                evidence["requests"] = service.server.requests
                evidence["errors"].extend(service.server.errors)
            totals = validate_completion(
                cases, evidence["caseResults"], evidence["requests"], evidence["errors"],
                service.base, framework, rid,
            )
            require(hash_file(probe) == evidence["probeSha256"], "Native executable changed during qualification.")
            require(hash_file(Path(__file__)) == evidence["driverSha256"], "Interop driver changed during qualification.")
            require(
                hash_file(oracle.git) == oracle.identities["gitSha256"]
                and hash_file(oracle.backend) == oracle.identities["httpBackendSha256"],
                "Reference Git executables changed during qualification.",
            )
            evidence["subprocessCount"] = budget.count
        evidence["temporaryRepositoriesRemoved"] = True
        evidence["totals"] = totals
        evidence["status"] = "passed"
    except (InteropError, OSError, UnicodeError, json.JSONDecodeError, subprocess.SubprocessError) as error:
        evidence["status"] = "failed"
        evidence["errors"].append(str(error))
        raise InteropError(f"{error} Evidence: {report}") from error
    finally:
        evidence["completedAtUtc"] = datetime.now(timezone.utc).isoformat()
        report.write_text(json.dumps(evidence, ensure_ascii=True, indent=2) + "\n", encoding="utf-8")
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--probe", type=Path, required=True)
    parser.add_argument("--managed-control", type=Path, required=True)
    parser.add_argument("--framework", choices=("net8.0", "net10.0"), required=True)
    parser.add_argument("--rid", choices=("win-x64", "win-arm64", "linux-x64", "linux-arm64"), required=True)
    parser.add_argument("--output", type=Path, default=Path("artifacts") / "git-interop" / "runs")
    args = parser.parse_args(argv)
    try:
        report = qualify(args.probe, args.framework, args.rid, args.output, managed_control=args.managed_control)
        print(f"Managed Git native/reference smart-HTTP slice: 26/26 cases, 56 HTTP exchanges. Evidence: {report}")
        print("Not full xRegistry server interoperability or release/native-matrix qualification.")
        return 0
    except (InteropError, OSError) as error:
        print(f"Git interoperability error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
