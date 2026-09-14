#!/usr/bin/env python3
"""Run independent pytest oracles on an isolated frozen or active-corrected corpus."""

from __future__ import annotations

import argparse
from collections import Counter
import importlib.util
import math
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import threading
import time
from typing import Any
import uuid
import xml.etree.ElementTree as ET


SPEC = importlib.util.spec_from_file_location(
    "corrected_oracle_specification", Path(__file__).with_name("manage.py")
)
assert SPEC is not None and SPEC.loader is not None
manage = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(manage)

STANDARD_MODULES = tuple("tools/" + name for name in (
    "test_registry_model.py",
    "test_federation_examples.py",
    "test_federation_resolution_examples.py",
    "test_mapping_examples.py",
    "test_git_examples.py",
    "test_oci_examples.py",
    "test_http_examples.py",
))
REGRESSION_MODULE = re.compile(r"tools/test_implementation_[a-z0-9_]+\.py")
SELECTOR = "not opcua"
ARTIFACTS = Path("artifacts") / "specification-oracles"
MAX_REPORT_BYTES = 16 * 1024 * 1024
MAX_LOG_BYTES = 8 * 1024 * 1024
DEFAULT_TIMEOUT = 600


class OracleError(manage.SpecificationError):
    """Oracle execution or evidence accounting failed."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise OracleError(message)


def inventory(root: Path, paths: set[str]) -> list[dict[str, Any]]:
    result = []
    for relative in sorted(paths):
        path = manage.contained(root, relative)
        require(path.is_file(), f"Missing regular evidence file: {relative}")
        data = path.read_bytes()
        result.append({"path": relative, "bytes": len(data), "sha256": manage.sha256(data)})
    return result


def source_state(root: Path) -> tuple[dict, dict, dict, list[dict]]:
    lock = manage.verify_corpus(root)
    original = manage.source_records(lock)
    heads = manage.correction_records(root, original)
    protected = {(manage.CORPUS / path).as_posix() for path in original}
    protected.update((manage.LOCK.as_posix(), "eng/specification/scope.json"))
    manifest = manage.contained(root, manage.CORRECTIONS.as_posix())
    if manifest.exists():
        protected.add(manage.CORRECTIONS.as_posix())
        protected.update(entry["file"] for entry in manage.read_json(manifest)["entries"])
    if (root / manage.LEDGER).exists():
        protected.add(manage.LEDGER.as_posix())
    return lock, original, heads, inventory(root, protected)


def validate_modules(modules: list[str]) -> None:
    require(isinstance(modules, list) and all(isinstance(path, str) for path in modules),
            "Oracle modules must be an explicit list of paths.")
    require(modules[:len(STANDARD_MODULES)] == list(STANDARD_MODULES),
            "All seven standard oracle modules are required in their explicit order.")
    require(len(set(modules)) == len(modules), "Duplicate oracle modules.")
    for path in modules[len(STANDARD_MODULES):]:
        require(REGRESSION_MODULE.fullmatch(path) is not None and "opcua" not in path.casefold(),
                f"Unsupported corrected regression module: {path}")


def materialize(root: Path, output: str, mode: str) -> dict[str, Any]:
    require(mode in {"original", "corrected"}, "Oracle mode must be original or corrected.")
    lock, original, heads, protected = source_state(root)
    output = output.replace("\\", "/")
    directory = manage.contained(root, output)
    require(Path(output).parts[:2] == ARTIFACTS.parts and len(Path(output).parts) == 3,
            "Oracle output must be a new run directory under artifacts/specification-oracles.")
    require(not directory.exists(), f"Oracle output already exists: {output}")
    effective = original | heads if mode == "corrected" else original
    modules = list(STANDARD_MODULES)
    if mode == "corrected":
        modules += sorted(path for path in heads if REGRESSION_MODULE.fullmatch(path))
    validate_modules(modules)
    require(all(path in effective for path in modules), "A required oracle module is missing.")
    buffers: dict[str, bytes] = {}
    files = []
    for path, entry in sorted(effective.items()):
        corrected = mode == "corrected" and path in heads
        source = entry["file"] if corrected else (manage.CORPUS / path).as_posix()
        data = manage.verify_source(root, source, entry)
        manage.contained(directory / "overlay", path)
        buffers[path] = data
        files.append({
            "path": path, "bytes": len(data), "sha256": manage.sha256(data),
            "sourceFile": source, "case": entry["case"] if corrected else None,
        })
    directory.mkdir(parents=True, exist_ok=False)
    for path, data in buffers.items():
        destination = manage.contained(directory / "overlay", path)
        destination.parent.mkdir(parents=True, exist_ok=True)
        with destination.open("xb") as stream:
            stream.write(data)
    with (directory / "LICENSE").open("xb") as stream:
        stream.write(buffers["LICENSE"])
    manifest = next((entry for entry in protected if entry["path"] == manage.CORRECTIONS.as_posix()), None)
    return {
        "schemaVersion": 1, "kind": "independent-python-oracles", "mode": mode,
        "framework": "pytest", "output": output, "selector": SELECTOR, "modules": modules,
        "baselineManifestSha256": lock["baselineManifestSha256"],
        "correctionsSha256": manifest["sha256"] if manifest else None,
        "originalFileCount": len(original), "activeCorrectionCount": len(heads) if mode == "corrected" else 0,
        "sourcesBefore": protected, "overlayFiles": files,
    }


def read_junit(path: Path) -> dict[str, Any]:
    require(path.is_file(), f"Missing pytest XML report: {path}")
    with path.open("rb") as stream:
        data = stream.read(MAX_REPORT_BYTES + 1)
    require(len(data) <= MAX_REPORT_BYTES, "Pytest XML byte limit exceeded.")
    text = data.decode("utf-8")
    require("<!DOCTYPE" not in text.upper() and "<!ENTITY" not in text.upper(),
            "Pytest XML must not contain entity declarations.")
    document = ET.fromstring(text)
    require(document.tag == "testsuites" and len(document) == 1 and document[0].tag == "testsuite",
            "Expected one pytest XML test suite.")
    suite = document[0]
    require(suite.get("name") == "pytest", "Unexpected XML test framework; pytest is required.")
    counts = {}
    for name in ("tests", "failures", "errors", "skipped"):
        value = suite.get(name, "")
        require(re.fullmatch(r"0|[1-9][0-9]*", value) is not None, f"Invalid XML {name} count.")
        counts[name] = int(value)
    cases = []
    for case in suite:
        require(case.tag == "testcase", "Unexpected pytest XML suite content.")
        properties = case.findall("./properties/property[@name='oracleNodeId']")
        require(len(properties) == 1 and bool(properties[0].get("value")),
                "Each pytest XML case must identify its collected oracle node.")
        outcomes = [child.tag for child in case if child.tag in {"failure", "error", "skipped"}]
        require(len(outcomes) <= 1, "Ambiguous pytest XML case outcomes.")
        cases.append({
            "nodeId": properties[0].get("value"), "name": case.get("name"),
            "classname": case.get("classname"), "outcome": outcomes[0] if outcomes else "passed",
        })
    observed = Counter(case["outcome"] for case in cases)
    require(counts == {
        "tests": len(cases), "failures": observed["failure"],
        "errors": observed["error"], "skipped": observed["skipped"],
    }, "Pytest XML totals disagree with actual case outcomes.")
    return {**counts, "sha256": manage.sha256(data), "cases": cases}


def validate_accounting(account: Any, junit: dict, modules: list[str], exit_code: int) -> dict[str, int]:
    validate_modules(modules)
    require(isinstance(account, dict) and set(account) == {
        "schemaVersion", "framework", "frameworkVersion", "pythonVersion", "modules", "selector",
        "selected", "deselected", "sessionTestsCollected", "exitCode", "reports",
    }, "Invalid pytest accounting fields.")
    require(type(account["schemaVersion"]) is int and account["schemaVersion"] == 1
            and account["framework"] == "pytest", "Unexpected accounting framework.")
    require(all(isinstance(account[key], str) and account[key].strip()
                for key in ("frameworkVersion", "pythonVersion")), "Missing oracle runtime versions.")
    require(account["modules"] == modules and account["selector"] == SELECTOR,
            "Pytest accounting changed the requested module inventory or selector.")
    require(type(account["exitCode"]) is int and account["exitCode"] == exit_code == 0,
            "The pytest process did not exit successfully.")
    selected = account["selected"]
    require(isinstance(selected, list) and bool(selected), "No selected oracle tests.")
    expected = {}
    for item in selected:
        require(isinstance(item, dict) and set(item) == {"nodeId", "module", "name"}
                and all(isinstance(value, str) and value for value in item.values()),
                "Malformed collected oracle node.")
        require(item["module"] in modules and item["nodeId"].startswith(item["module"] + "::")
                and "opcua" not in item["nodeId"].casefold()
                and item["nodeId"] not in expected, "Duplicate, excluded or unexpected selected oracle node.")
        expected[item["nodeId"]] = item
    require({item["module"] for item in selected} == set(modules),
            "Every requested oracle module must execute nonempty in-scope tests.")
    deselected = account["deselected"]
    require(isinstance(deselected, list)
            and all(isinstance(node, str) and "opcua" in node.casefold()
                    and node.split("::", 1)[0] in modules for node in deselected),
            "Only explicitly OPC-UA-named cases may be deselected.")
    require(len(set(deselected)) == len(deselected) and not set(deselected) & expected.keys(),
            "Duplicate or overlapping deselected oracle nodes.")
    require(type(account["sessionTestsCollected"]) is int
            and account["sessionTestsCollected"] == len(expected), "Incorrect pytest collection accounting.")
    require(isinstance(account["reports"], list), "Missing pytest phase reports.")
    phases = set()
    for report in account["reports"]:
        require(isinstance(report, dict) and set(report) == {"nodeId", "when", "outcome"}
                and all(isinstance(value, str) for value in report.values()), "Invalid pytest phase report.")
        key = (report["nodeId"], report["when"])
        require(report["nodeId"] in expected and report["when"] in {"setup", "call", "teardown"}
                and key not in phases and report["outcome"] == "passed",
                "Missing, duplicate, skipped or failing pytest phases.")
        phases.add(key)
    require(phases == {(node, phase) for node in expected for phase in ("setup", "call", "teardown")},
            "Not every selected oracle completed all three pytest phases.")
    actual = set()
    for case in junit["cases"]:
        require(case["nodeId"] in expected and case["nodeId"] not in actual,
                "XML contains a duplicate or unexpected oracle node.")
        item = expected[case["nodeId"]]
        module_class = item["module"][:-3].replace("/", ".")
        require(case["name"] == item["name"] and isinstance(case["classname"], str)
                and (case["classname"] == module_class or case["classname"].startswith(module_class + ".")),
                "XML case identity does not match the collected pytest module.")
        actual.add(case["nodeId"])
    require(actual == expected.keys() and junit["tests"] == len(expected)
            and junit["failures"] == junit["errors"] == junit["skipped"] == 0,
            "Oracle XML requires exact, nonempty, unskipped passing coverage.")
    return {
        "collected": len(expected) + len(deselected), "selected": len(expected),
        "deselected": len(deselected), "passed": len(expected),
        **{key: junit[key] for key in ("tests", "failures", "errors", "skipped")},
    }


def windows_job(process: subprocess.Popen):
    """Keep the owned pytest process and its generator/Git children in a kill-on-close job."""
    import ctypes
    from ctypes import wintypes

    class BasicLimits(ctypes.Structure):
        _fields_ = [
            ("processTime", ctypes.c_longlong), ("jobTime", ctypes.c_longlong),
            ("flags", wintypes.DWORD), ("minimumWorkingSet", ctypes.c_size_t),
            ("maximumWorkingSet", ctypes.c_size_t), ("activeProcesses", wintypes.DWORD),
            ("affinity", ctypes.c_size_t), ("priority", wintypes.DWORD), ("scheduling", wintypes.DWORD),
        ]

    class ExtendedLimits(ctypes.Structure):
        _fields_ = [
            ("basic", BasicLimits), ("ioCounters", ctypes.c_ulonglong * 6),
            ("processMemory", ctypes.c_size_t), ("jobMemory", ctypes.c_size_t),
            ("peakProcessMemory", ctypes.c_size_t), ("peakJobMemory", ctypes.c_size_t),
        ]

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    kernel.CreateJobObjectW.restype = wintypes.HANDLE
    kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    kernel.SetInformationJobObject.restype = wintypes.BOOL
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
    kernel.AssignProcessToJobObject.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    job = kernel.CreateJobObjectW(None, None)
    if not job:
        raise ctypes.WinError(ctypes.get_last_error())
    limits = ExtendedLimits()
    limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    child = None
    try:
        if not kernel.SetInformationJobObject(job, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
            raise ctypes.WinError(ctypes.get_last_error())
        child = kernel.OpenProcess(0x0101, False, process.pid)  # SET_QUOTA | TERMINATE
        if not child or not kernel.AssignProcessToJobObject(job, child):
            raise ctypes.WinError(ctypes.get_last_error())
    except OSError:
        kernel.CloseHandle(job)
        raise
    finally:
        if child:
            kernel.CloseHandle(child)

    def close() -> None:
        if not kernel.CloseHandle(job):
            raise ctypes.WinError(ctypes.get_last_error())

    return close


def execute(
    command: list[str], cwd: Path, environment: dict[str, str], log: Path,
    timeout_seconds: float, max_log_bytes: int,
) -> int:
    require(type(timeout_seconds) in (int, float) and math.isfinite(timeout_seconds)
            and 0 < timeout_seconds <= 900, "Process deadline must be finite and within 900 seconds.")
    require(type(max_log_bytes) is int and 0 < max_log_bytes <= MAX_REPORT_BYTES,
            "Process output byte budget is invalid.")
    exceeded = threading.Event()
    failures: list[OSError] = []
    with log.open("xb") as output:
        process = subprocess.Popen(
            command, cwd=cwd, env=environment, stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, start_new_session=os.name != "nt",
        )
        close_job = None
        reader = None
        timed_out = False
        try:
            if os.name == "nt":
                close_job = windows_job(process)

            def drain() -> None:
                written = 0
                try:
                    with process.stdout:
                        while chunk := process.stdout.read1(65536):
                            remaining = max_log_bytes - written
                            output.write(chunk[:remaining])
                            written += min(len(chunk), remaining)
                            if len(chunk) > remaining:
                                exceeded.set()
                                break
                except OSError as error:
                    failures.append(error)
                    exceeded.set()

            reader = threading.Thread(target=drain, daemon=True)
            reader.start()
            deadline = time.monotonic() + timeout_seconds
            while process.poll() is None and not exceeded.is_set():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    timed_out = True
                    break
                exceeded.wait(min(0.05, remaining))
        finally:
            if close_job is not None:
                close_job()
            elif os.name != "nt":
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            if process.poll() is None:
                process.kill()
            process.wait(timeout=15)
            if reader is not None:
                reader.join(timeout=15)
            elif process.stdout is not None:
                process.stdout.close()
            if os.name == "nt":
                # Release the process object before callers remove its working directory.
                process._handle.Close()
        require(not timed_out, "Oracle process exceeded its deadline; its owned process tree was terminated.")
        require(not failures, f"Oracle log capture failed: {failures}")
        require(not exceeded.is_set(), "Oracle process exceeded its output byte limit.")
        require(reader is not None and not reader.is_alive(), "Oracle log capture did not finish.")
    return process.returncode


def overlay_inventory(root: Path) -> list[dict]:
    paths: set[str] = set()

    def scan_error(error: OSError) -> None:
        raise OracleError(f"Cannot enumerate the oracle overlay: {error}") from error

    for parent, directories, files in os.walk(root, onerror=scan_error, followlinks=False):
        for name in directories + files:
            relative = (Path(parent) / name).relative_to(root).as_posix()
            path = manage.contained(root, relative)
            if path.is_file():
                paths.add(relative)
            else:
                require(path.is_dir(), f"Nonregular overlay entry: {relative}")
    return inventory(root, paths)


def worker(directory: Path) -> int:
    directory = directory.absolute()
    relative = directory.relative_to(manage.ROOT)
    manage.contained(manage.ROOT, relative.as_posix())
    require(relative.parts[0] == "artifacts", "Oracle workers must stay under repository artifacts.")
    control = manage.read_json(directory / "control.json")
    require(isinstance(control, dict) and set(control) == {"modules", "selector"}
            and control["selector"] == SELECTOR, "Invalid oracle worker control.")
    modules = control["modules"]
    validate_modules(modules)
    overlay = directory / "overlay"
    for module in modules:
        require(manage.contained(overlay, module).is_file(), f"Missing oracle module: {module}")
    import pytest

    account = {
        "schemaVersion": 1, "framework": "pytest", "frameworkVersion": pytest.__version__,
        "pythonVersion": sys.version, "modules": modules, "selector": SELECTOR,
        "selected": [], "deselected": [], "sessionTestsCollected": 0, "exitCode": None, "reports": [],
    }

    class AccountingPlugin:
        def pytest_deselected(self, items):
            account["deselected"].extend(item.nodeid for item in items)

        def pytest_collection_finish(self, session):
            for item in session.items:
                module = item.path.resolve().relative_to(overlay.resolve()).as_posix()
                require(module in modules, f"Collected a module outside the oracle inventory: {module}")
                account["selected"].append({"nodeId": item.nodeid, "module": module, "name": item.name})
                item.user_properties.append(("oracleNodeId", item.nodeid))

        def pytest_runtest_logreport(self, report):
            account["reports"].append({
                "nodeId": report.nodeid, "when": report.when, "outcome": report.outcome,
            })

        def pytest_sessionfinish(self, session, exitstatus):
            account["sessionTestsCollected"] = session.testscollected

    arguments = [
        "-c", str(directory / "pytest.ini"), "--rootdir", str(overlay), "--noconftest",
        "-p", "no:cacheprovider", "-o", "junit_family=xunit2", "-o", "junit_suite_name=pytest",
        "-o", "xfail_strict=true", "--basetemp", str(directory / "pytest-tmp"),
        "-k", SELECTOR, "-q", "--junitxml", str(directory / "results.xml"), *modules,
    ]
    exit_code = int(pytest.main(arguments, plugins=[AccountingPlugin()]))
    account["exitCode"] = exit_code
    manage.write_output(directory / "accounting.json", manage.encoded(account))
    return exit_code


def run_oracles(
    root: Path, output: str | None = None, mode: str = "corrected",
    *, timeout_seconds: float = DEFAULT_TIMEOUT, max_log_bytes: int = MAX_LOG_BYTES,
) -> Path:
    require(type(timeout_seconds) in (int, float) and math.isfinite(timeout_seconds)
            and 0 < timeout_seconds <= 900, "Oracle deadline must be finite and within 900 seconds.")
    require(type(max_log_bytes) is int and 0 < max_log_bytes <= MAX_REPORT_BYTES,
            "Oracle output byte budget is invalid.")
    output = output if output is not None else (ARTIFACTS / f"{mode}-{uuid.uuid4().hex}").as_posix()
    report = materialize(root, output, mode)
    directory = manage.contained(root, report["output"])
    overlay = directory / "overlay"
    command = [sys.executable, "-B", str(Path(__file__).resolve()), "--worker", str(directory)]
    report.update(
        status="failed", errors=[], preserved={"sources": False, "overlay": False},
        execution={"command": command, "timeoutSeconds": timeout_seconds, "maxLogBytes": max_log_bytes},
        runnerSha256=manage.sha256(Path(__file__).read_bytes()),
    )
    try:
        manage.write_output(directory / "control.json", manage.encoded({
            "modules": report["modules"], "selector": SELECTOR,
        }))
        manage.write_output(directory / "pytest.ini", b"[pytest]\n")
        temporary = directory / "tmp"
        temporary.mkdir()
        environment = {
            key: value for key, value in os.environ.items()
            if not key.upper().startswith(("PYTEST_", "PYTHON"))
        }
        environment.update({
            "PYTHONDONTWRITEBYTECODE": "1", "PYTEST_DISABLE_PLUGIN_AUTOLOAD": "1",
            "TMPDIR": str(temporary), "TEMP": str(temporary), "TMP": str(temporary),
        })
        exit_code = execute(
            command, cwd=overlay, environment=environment, log=directory / "pytest.log",
            timeout_seconds=timeout_seconds, max_log_bytes=max_log_bytes,
        )
        report["execution"]["exitCode"] = exit_code
        junit = read_junit(manage.contained(root, report["output"] + "/results.xml"))
        report["junit"] = {key: value for key, value in junit.items() if key != "cases"}
        account = manage.read_json(manage.contained(root, report["output"] + "/accounting.json"))
        report["counts"] = validate_accounting(account, junit, report["modules"], exit_code)
        report["frameworkVersion"] = account["frameworkVersion"]
        report["pythonVersion"] = account["pythonVersion"]
    except (manage.SpecificationError, OSError, ValueError, ET.ParseError, subprocess.SubprocessError) as error:
        report["errors"].append(f"Oracle execution/accounting: {error}")
    finally:
        try:
            report["sourcesAfter"] = source_state(root)[3]
            require(report["sourcesAfter"] == report["sourcesBefore"], "Protected source provenance changed.")
            report["preserved"]["sources"] = True
        except (manage.SpecificationError, OSError, ValueError) as error:
            report["errors"].append(f"Protected source verification: {error}")
        try:
            expected = [{key: entry[key] for key in ("path", "bytes", "sha256")}
                        for entry in report["overlayFiles"]]
            require(overlay_inventory(manage.contained(root, report["output"] + "/overlay")) == expected,
                    "Oracle overlay contains changed, missing or unlisted files.")
            report["preserved"]["overlay"] = True
        except (manage.SpecificationError, OSError, ValueError) as error:
            report["errors"].append(f"Overlay verification: {error}")
    if not report["errors"]:
        report["status"] = "passed"
    receipt = manage.contained(root, report["output"] + "/report.json")
    manage.write_output(receipt, manage.encoded(report))
    require(report["status"] == "passed", f"{mode} oracle run failed; inspect {receipt}")
    return receipt


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("original", "corrected"), default="corrected")
    parser.add_argument("--output", help="New relative run directory under artifacts/specification-oracles.")
    parser.add_argument("--timeout-seconds", type=int, default=DEFAULT_TIMEOUT)
    parser.add_argument("--worker", type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args(argv)
    try:
        if args.worker is not None:
            return worker(args.worker)
        receipt = run_oracles(manage.ROOT, args.output, args.mode, timeout_seconds=args.timeout_seconds)
        report = manage.read_json(receipt)
        counts = report["counts"]
        print(f"{args.mode} independent pytest oracles: {counts['passed']} passed, "
              f"{counts['deselected']} explicitly deselected, no skips/failures/errors. Report: {receipt}")
        return 0
    except (manage.SpecificationError, OSError, ValueError, ImportError, ET.ParseError, subprocess.SubprocessError) as error:
        print(f"Oracle runner failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
