#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Publish and execute source-bound generic TUnit suites on their actual native host."""

from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

from specification import native_receipt

ROOT = Path(__file__).resolve().parents[1]
SUITES = (
    "XRegistry.Core.Tests", "XRegistry.Models.Tests", "XRegistry.Validation.Tests",
    "XRegistry.Server.Tests", "XRegistry.AspNetCore.Tests", "XRegistry.Http.Tests",
    "XRegistry.Storage.File.Tests", "XRegistry.Federation.Tests",
    "XRegistry.Publication.Tests", "XRegistry.Publication.Server.Tests",
    "XRegistry.File.Tests", "XRegistry.Git.Tests", "XRegistry.Oci.Tests",
)
EXCLUDED = {
    "XRegistry.OpcUa.Tests": "OPC UA binding Native AOT qualification is outside this generic gate.",
    "XRegistry.SampleHosting.Tests": "Sample hosting is qualified separately from generic library suites.",
    "XRegistry.Bridge.Tests": "The bridge sample has its own native qualification gate.",
}
PROOF_METHOD = "NativeAotStatusIsReportedWhenRequested"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def native_host() -> str:
    systems = {"Windows": "win", "Linux": "linux"}
    machines = {"amd64": "x64", "x86_64": "x64", "aarch64": "arm64", "arm64": "arm64"}
    system, machine = platform.system(), platform.machine().lower()
    require(system in systems and machine in machines, "A native Windows/Linux x64/ARM64 host is required.")
    require(sys.maxsize > 2**32, "A 64-bit Python process is required.")
    return systems[system] + "-" + machines[machine]


def source_inventory(root: Path) -> dict:
    def git(*args: str) -> bytes:
        return subprocess.check_output(["git", "-C", str(root), *args], timeout=30)

    require(not git("status", "--porcelain", "--untracked-files=normal").strip(),
            "Commit source changes before producing source-bound native suite evidence.")
    commit = git("rev-parse", "HEAD").decode("ascii").strip()
    files = [
        {"path": name, "sha256": digest(root / name)}
        for name in sorted(git("ls-files", "-z").decode("utf-8").split("\0"))
        if name
    ]
    encoded = json.dumps(files, ensure_ascii=True, sort_keys=True).encode()
    return {"commit": commit, "sha256": hashlib.sha256(encoded).hexdigest(), "files": files}


def validate_suite_inventory(root: Path) -> None:
    actual = {
        p.stem for p in (root / "tests").glob("*/*.csproj")
        if ET.parse(p).findtext(".//IsTestProject") == "true"
    }
    require(actual == set(SUITES) | set(EXCLUDED),
            "Every test project must be explicitly classified in the native suite gate.")


def run_recorded(arguments: list[str], log: Path, environment: dict[str, str],
                 *, timeout: int, expected_exit: int = 0) -> None:
    with log.open("wb") as stream:
        result = subprocess.run(arguments, cwd=ROOT, env=environment, stdout=stream,
                                stderr=subprocess.STDOUT, timeout=timeout, check=False)
    require(result.returncode == expected_exit,
            f"Command exited {result.returncode}, expected {expected_exit}; see {log}.")


def verify_publish(log: Path, executable: Path) -> None:
    text = log.read_text(encoding="utf-8-sig", errors="replace")
    require(executable.is_file(), f"The native executable is missing: {executable}")
    require("Generating native code" in text, f"ILC execution was not established: {log}")
    require(not re.search(r"\b(?:warning|error) IL[0-9]+", text),
            f"The native publication emitted IL diagnostics: {log}")


def verify_run(trx: Path, proof: Path, framework: str, rid: str) -> tuple[dict, int]:
    receipt = native_receipt.build_receipt(trx, proof, framework, rid)
    verify_process(proof, rid)
    require(not receipt["failedTestIds"] and not receipt["skippedTestIds"],
            "The native suite has failed or skipped methods.")
    require(PROOF_METHOD in receipt["passedTestIds"], "The same-process native proof test did not pass.")
    root = ET.parse(trx).getroot()
    entries = root.findall(native_receipt.element("Results") + "/" + native_receipt.element("UnitTestResult"))
    require(entries and all(e.get("outcome") == "Passed" for e in entries),
            "Every executed native case must pass.")
    return receipt, len(entries)


def verify_process(proof: Path, rid: str) -> None:
    value = json.loads(proof.read_bytes())
    system, architecture = rid.split("-")
    require(value.get("os") == system and value.get("processArchitecture") == architecture,
            "The same-process proof does not match the selected native OS/architecture.")


def case_inventory(trx: Path) -> Counter:
    native_receipt.parse_trx(trx)
    root = ET.parse(trx).getroot()
    definitions = {}
    for entry in root.find(native_receipt.element("TestDefinitions")):
        method = entry.find(native_receipt.element("TestMethod"))
        require(bool(method.get("className")), "The TRX test class is missing.")
        definitions[entry.get("id")] = (method.get("className"), method.get("name"))
    inventory = Counter()
    for entry in root.find(native_receipt.element("Results")):
        require(bool(entry.get("testName")), "The TRX case display name is missing.")
        inventory[(*definitions[entry.get("testId")], entry.get("testName"))] += 1
    return inventory


def verify_control(trx: Path, native_trx: Path, proof: Path, framework: str, rid: str, rejected: Path) -> None:
    outcomes = native_receipt.parse_trx(trx)
    require(outcomes.get(PROOF_METHOD) == "Passed" and all(v == "Passed" for v in outcomes.values()),
            "Every managed baseline method, including the proof method, must pass.")
    verify_process(proof, rid)
    require(case_inventory(trx) == case_inventory(native_trx),
            "Native and managed runs must execute the same classes, methods and parameterized cases.")
    value = json.loads(proof.read_bytes())
    require(value.get("nativeAot") is False
            and type(value.get("jitCompiledMethods")) is int and value["jitCompiledMethods"] > 0,
            "The matching managed control must report false with a positive JIT count.")
    require(not rejected.exists(), "The rejected-control receipt path must be fresh.")
    result = native_receipt.main([
        "--trx", str(trx), "--native-proof", str(proof), "--framework", framework,
        "--rid", rid, "--output", str(rejected),
    ])
    require(result == 1 and not rejected.exists(), "The receipt converter did not reject the managed control.")


def write_json(path: Path, value: dict) -> None:
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def execute_suite(name: str, framework: str, rid: str, run: Path,
                  environment: dict[str, str]) -> dict:
    suite = run / name
    suite.mkdir()
    native = suite / "native"
    build = suite / "build"
    project = ROOT / "tests" / name / (name + ".csproj")
    executable = native / (name + (".exe" if rid.startswith("win-") else ""))
    publish_log = suite / "publish.log"
    run_recorded([
        "dotnet", "publish", str(project), "-c", "Release", "-f", framework, "-r", rid,
        "--self-contained", "--artifacts-path", str(build), "-p:PublishAot=true",
        "-p:IsAotCompatible=true", "-p:TrimmerSingleWarn=false", "-o", str(native),
        "--nologo", "-v", "minimal",
    ], publish_log, environment, timeout=1800)
    verify_publish(publish_log, executable)
    run_recorded(["dotnet", "--info"], suite / "dotnet-info.log", environment, timeout=30)
    proof = suite / "proof.json"
    native_environment = environment | {"XREGISTRY_NATIVE_PROOF_PATH": str(proof)}
    common = ["--minimum-expected-tests", "1", "--zero-tests-policy", "strict",
              "--timeout", "5m", "--no-ansi", "--report-trx"]
    run_recorded([str(executable), *common, "--report-trx-filename", "native.trx",
                  "--results-directory", str(suite / "results")],
                 suite / "native.log", native_environment, timeout=360)
    receipt, cases = verify_run(suite / "results" / "native.trx", proof, framework, rid)
    managed = list((build / "bin" / name).rglob(name + ".dll"))
    require(len(managed) == 1, "Exactly one matching managed test assembly is required.")
    control_environment = environment | {"XREGISTRY_NATIVE_PROOF_PATH": str(suite / "managed-proof.json")}
    run_recorded([
        "dotnet", str(managed[0]), *common, "--report-trx-filename", "control.trx",
        "--results-directory", str(suite / "control-results"),
    ], suite / "managed.log", control_environment, timeout=360)
    verify_control(suite / "control-results" / "control.trx", suite / "results" / "native.trx",
                   suite / "managed-proof.json",
                   framework, rid, suite / "rejected-receipt.json")
    write_json(suite / "receipt.json", receipt)
    return {
        "project": name, "outcome": "passed", "cases": cases,
        "methods": len(receipt["passedTestIds"]), "executableSha256": digest(executable),
        "managedSha256": digest(managed[0]), "trxSha256": digest(suite / "results" / "native.trx"),
        "proofSha256": digest(proof), "receiptSha256": digest(suite / "receipt.json"),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--framework", choices=native_receipt.FRAMEWORKS, required=True)
    parser.add_argument("--rid", choices=native_receipt.RIDS, required=True)
    parser.add_argument("--project", choices=SUITES, action="append")
    parser.add_argument("--artifacts-root", type=Path, required=True)
    parser.add_argument("--work-root", type=Path, required=True,
                        help="Existing local filesystem directory for test TEMP/TMP/TMPDIR; NTFS/ext4 is required by storage.")
    args = parser.parse_args(argv)
    try:
        require(args.rid == native_host(), "Cross-compilation/emulation is not native execution evidence.")
        projects = args.project if args.project is not None else list(SUITES)
        require(len(projects) == len(set(projects)), "Each suite may be selected only once.")
        for directory in (args.artifacts_root, args.work_root):
            require(directory.is_absolute() and directory.is_dir(), "Artifact and work roots must be existing absolute directories.")
        validate_suite_inventory(ROOT)
        before = source_inventory(ROOT)
        run = Path(tempfile.mkdtemp(prefix="native-suites-", dir=args.artifacts_root))
        write_json(run / "source-before.json", before)
        summary = {
            "schemaVersion": 1, "scope": "development-native-tunit-suites", "releaseQualified": False,
            "framework": args.framework, "rid": args.rid, "outcome": "running",
            "sourceCommit": before["commit"], "sourceSha256": before["sha256"],
            "selectedProjects": projects, "excludedProjects": EXCLUDED, "suites": [],
        }
        write_json(run / "summary.json", summary)
        environment = dict(os.environ) | {k: str(args.work_root) for k in ("TEMP", "TMP", "TMPDIR")}
        environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US"
        if os.name == "nt":
            installer = Path(os.environ["ProgramFiles(x86)"]) / "Microsoft Visual Studio" / "Installer"
            environment["PATH"] = str(installer) + os.pathsep + environment.get("PATH", "")
        for name in projects:
            print(f"Native suite {name}: {args.framework}/{args.rid}; evidence {run}", flush=True)
            try:
                result = execute_suite(name, args.framework, args.rid, run, environment)
            except (OSError, ValueError, subprocess.SubprocessError, native_receipt.ReceiptError) as error:
                result = {"project": name, "outcome": "failed", "error": str(error)}
                print(f"{name}: {error}", file=sys.stderr, flush=True)
            summary["suites"].append(result)
            try:
                after = source_inventory(ROOT)
                write_json(run / "source-after.json", after)
                require(after == before, "Source inputs changed during native qualification.")
            except (OSError, ValueError, subprocess.SubprocessError) as error:
                summary["sourceError"] = str(error)
                summary["outcome"] = "failed"
                write_json(run / "summary.json", summary)
                raise
            write_json(run / "summary.json", summary)
        summary["outcome"] = "passed" if all(s["outcome"] == "passed" for s in summary["suites"]) else "failed"
        write_json(run / "summary.json", summary)
        print(f"Native suite gate {summary['outcome']}: {run}", flush=True)
        return 0 if summary["outcome"] == "passed" else 1
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print(f"Native suite gate error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
