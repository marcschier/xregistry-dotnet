#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Kill one owned native writer process at a time and verify real restarted state."""

from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import time


DOCUMENT_HEX = bytes(range(256)).hex().upper()
DOCUMENT_SHA256 = "40aff2e9d2d8922e47afd4648e6967497158785fbd1da870e7110266bf944880"


def read_line(process: subprocess.Popen[str], timeout: float = 20) -> str:
    with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
        future = pool.submit(process.stdout.readline)
        try:
            return future.result(timeout).rstrip("\r\n")
        except concurrent.futures.TimeoutError as error:
            process.kill()
            process.wait(timeout=10)
            raise RuntimeError("The owned storage probe missed its handshake deadline.") from error


def inspect(probe: Path, directory: Path, allowed_generations: set[int]) -> dict:
    completed = subprocess.run(
        [str(probe), "inspect", str(directory)],
        check=True, capture_output=True, text=True, timeout=30,
    )
    data = json.loads(completed.stdout)
    if (data.get("native") is not True or type(data.get("generation")) is not int
            or data["generation"] not in allowed_generations
            or type(data.get("jitCompiledMethods")) is not int or data["jitCompiledMethods"] != 0):
        raise RuntimeError("Recovery did not produce an allowed native storage generation.")
    records = data.get("records")
    if not isinstance(records, dict):
        raise RuntimeError("Recovery omitted its record set.")
    expected_count = 1000 if data["generation"] == 1 else 0
    if len(records) != expected_count:
        raise RuntimeError("A killed transaction exposed a partial record set.")
    for index in range(expected_count):
        key = f"record-{index:04d}"
        expected = {
            "metadata": {"ordinal": index, "epoch": 184467440737095516160},
            "document": DOCUMENT_HEX if index == 0 else None,
        }
        record = records.get(key)
        if (record != expected or type(record["metadata"]["ordinal"]) is not int
                or type(record["metadata"]["epoch"]) is not int):
            raise RuntimeError(f"Recovered exact metadata or Document bytes differ at {key}.")
    if (type(data.get("remainingStagingFiles")) is not int
            or data["remainingStagingFiles"] != 0):
        raise RuntimeError("Explicit orphan collection left owned abandoned staging files.")
    if type(data.get("removedOrphans")) is not int or data["removedOrphans"] < 0:
        raise RuntimeError("Recovery omitted valid orphan-collection accounting.")
    return {
        "generation": data["generation"],
        "records": expected_count,
        "removedOrphans": data["removedOrphans"],
    }


def run_case(probe: Path, root: Path, mode: str, index: int) -> dict:
    with tempfile.TemporaryDirectory(prefix=f"xregistry-{mode}-{index}-", dir=root) as temporary:
        directory = Path(temporary)
        process = subprocess.Popen(
            [str(probe), mode, str(directory)], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        )
        try:
            if read_line(process) != "PREPARED":
                raise RuntimeError("The native writer did not reach its durable preparation barrier.")
            if mode == "commit":
                if read_line(process) != "COMMITTED 1":
                    raise RuntimeError("The native writer did not acknowledge the complete commit.")
            elif mode == "race":
                process.stdin.write("COMMIT\n")
                process.stdin.flush()
                published = directory / "blobs" / f"{DOCUMENT_SHA256}.blob"
                deadline = time.monotonic() + 15
                while not published.exists():
                    if process.poll() is not None or time.monotonic() >= deadline:
                        raise RuntimeError("The native writer did not enter blob publication.")
                    time.sleep(0.001)
            process.kill()
            process.wait(timeout=10)
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=10)
            process.stdin.close()
            process.stdout.close()
            process.stderr.close()
        allowed = {0} if mode == "prepare" else {1} if mode == "commit" else {0, 1}
        result = inspect(probe, directory, allowed)
        if mode == "prepare" and result["removedOrphans"] != 1:
            raise RuntimeError("Uncommitted preparation was not recovered as exactly one abandoned stage.")
        if mode == "commit" and result["removedOrphans"] != 0:
            raise RuntimeError("An acknowledged commit left unexpected orphaned content.")
        return {"case": f"{mode}-{index}", **result}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--probe", type=Path, required=True)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--managed-control", type=Path)
    parser.add_argument("--races", type=int, default=4)
    args = parser.parse_args()
    if not 1 <= args.races <= 32:
        parser.error("--races must be between 1 and 32.")
    probe = args.probe.resolve(strict=True)
    root = args.root.resolve()
    root.mkdir(parents=True, exist_ok=True)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text('{"schemaVersion":1,"status":"running","passed":0}\n', encoding="utf-8")
    if args.managed_control is not None:
        with tempfile.TemporaryDirectory(prefix="xregistry-jit-control-", dir=root) as temporary:
            control = subprocess.run(
                ["dotnet", str(args.managed_control.resolve(strict=True)), "prepare", temporary],
                capture_output=True, text=True, timeout=30, check=False,
            )
            if control.returncode != 2 or not control.stderr.strip() or list(Path(temporary).iterdir()):
                raise RuntimeError("The managed control did not reject JIT execution before touching storage.")
    results = [run_case(probe, root, "prepare", 0), run_case(probe, root, "commit", 0)]
    results.extend(run_case(probe, root, "race", index) for index in range(args.races))
    with probe.open("rb") as binary:
        probe_hash = hashlib.file_digest(binary, "sha256").hexdigest()
    report = {
        "schemaVersion": 1,
        "status": "passed",
        "probe": str(probe),
        "probeSha256": probe_hash,
        "passed": len(results),
        "failed": 0,
        "jitNegativeControl": args.managed_control is not None,
        "cases": results,
        "scope": "Native process termination and restart; not physical power-loss certification.",
    }
    args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Verified {len(results)} native storage crash/restart cases.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
