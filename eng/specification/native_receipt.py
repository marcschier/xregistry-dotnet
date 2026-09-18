#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Build a schemaVersion:1 native execution receipt from a real TRX result and an
independently captured native-execution proof.

This is the first concrete building block toward the release/specification
qualification gate's native-evidence requirement (see docs/releasing.md and
docs/roadmap.md). It converts one already-executed, already-verified native
test run into the exact receipt shape `eng/specification/manage.py`'s
`release_check` validates. It does not itself qualify any package or
specification requirement, generate a native test host, decide which
requirements a test maps to, or run tests.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any
import xml.etree.ElementTree as ET

FRAMEWORKS = ("net8.0", "net10.0")
RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64")
MAX_TRX_BYTES = 64 * 1024 * 1024
MAX_PROOF_BYTES = 4096
TRX_NAMESPACE = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
# Outcomes not explicitly recognized as "Passed" or "NotExecuted" (skipped) are
# conservatively folded into "failed"; an unrecognized outcome is never evidence.
SKIPPED_OUTCOME = "NotExecuted"
PASSED_OUTCOME = "Passed"


class ReceiptError(ValueError):
    """The supplied TRX or native-execution proof does not support a receipt."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ReceiptError(message)


def element(tag: str) -> str:
    return f"{{{TRX_NAMESPACE}}}{tag}"


def parse_trx(path: Path) -> dict[str, str]:
    """Returns {bareMethodName: worstOutcome} for every executed test method.

    A parameterized method's outcome is "Passed" only when every one of its
    executions passed; any other execution taints the whole method, matching
    the ledger's per-method `testIds` granularity.
    """
    require(path.is_file(), f"TRX file is missing: {path}")
    raw = path.read_bytes()
    require(0 < len(raw) <= MAX_TRX_BYTES, "TRX file is empty or exceeds the byte limit.")
    require(
        b"<!DOCTYPE" not in raw and b"<!ENTITY" not in raw,
        "TRX must not declare a DOCTYPE or entity.",
    )
    try:
        root = ET.fromstring(raw)
    except ET.ParseError as error:
        raise ReceiptError(f"TRX is not well-formed XML: {error}") from error

    definitions = root.find(element("TestDefinitions"))
    require(definitions is not None, "TRX has no TestDefinitions section.")
    method_by_id: dict[str, str] = {}
    for unit_test in definitions.findall(element("UnitTest")):
        test_id = unit_test.get("id")
        method = unit_test.find(element("TestMethod"))
        name = method.get("name") if method is not None else None
        require(bool(test_id) and bool(name), "Malformed TestDefinitions entry.")
        require(test_id not in method_by_id, f"Duplicate TRX test definition ID: {test_id}")
        method_by_id[test_id] = name

    results = root.find(element("Results"))
    require(results is not None, "TRX has no Results section.")
    entries = results.findall(element("UnitTestResult"))
    require(bool(entries), "TRX contains no test results.")
    rank = {PASSED_OUTCOME: 0, SKIPPED_OUTCOME: 1}
    worst: dict[str, str] = {}
    for entry in entries:
        test_id = entry.get("testId")
        outcome = entry.get("outcome")
        require(bool(outcome) and test_id in method_by_id, "Unmapped or malformed UnitTestResult.")
        method = method_by_id[test_id]
        if method not in worst or rank.get(outcome, 2) > rank.get(worst[method], 2):
            worst[method] = outcome
    return worst


def verify_native_proof(path: Path) -> None:
    """Requires the same nativeAot=true/jitCompiledMethods=0 evidence the sample
    executables' `runtime-info` command reports; a JIT/managed run is never accepted."""
    require(path.is_file(), f"Native-execution proof file is missing: {path}")
    raw = path.read_bytes()
    require(0 < len(raw) <= MAX_PROOF_BYTES, "Native-execution proof file is empty or too large.")
    try:
        document = json.loads(raw.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as error:
        raise ReceiptError(f"Native-execution proof is not valid JSON: {error}") from error
    require(
        isinstance(document, dict)
        and document.get("nativeAot") is True
        and type(document.get("jitCompiledMethods")) is int
        and document["jitCompiledMethods"] == 0,
        "Native-execution proof does not establish nativeAot=true with zero JIT-compiled "
        "methods; a JIT/managed run must never be accepted as native evidence.",
    )


def build_receipt(trx: Path, proof: Path, framework: str, rid: str) -> dict[str, Any]:
    require(framework in FRAMEWORKS, f"Unsupported framework: {framework!r}")
    require(rid in RIDS, f"Unsupported RID: {rid!r}")
    verify_native_proof(proof)
    outcomes = parse_trx(trx)
    passed = sorted(method for method, outcome in outcomes.items() if outcome == PASSED_OUTCOME)
    skipped = sorted(method for method, outcome in outcomes.items() if outcome == SKIPPED_OUTCOME)
    failed = sorted(set(outcomes) - set(passed) - set(skipped))
    return {
        "schemaVersion": 1,
        "framework": framework,
        "rid": rid,
        "nativeExecutable": True,
        "passedTestIds": passed,
        "failedTestIds": failed,
        "skippedTestIds": skipped,
    }


def encoded(value: Any) -> bytes:
    return (json.dumps(value, ensure_ascii=True, indent=2, sort_keys=True) + "\n").encode("utf-8")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--trx", type=Path, required=True, help="A real TRX file from a native test run.")
    parser.add_argument(
        "--native-proof", type=Path, required=True,
        help="A JSON file with nativeAot=true and jitCompiledMethods=0 from the same native run.",
    )
    parser.add_argument("--framework", required=True, choices=FRAMEWORKS)
    parser.add_argument("--rid", required=True, choices=RIDS)
    parser.add_argument("--output", type=Path, required=True, help="A new receipt file; never overwritten.")
    args = parser.parse_args(argv)
    try:
        require(not args.output.exists(), f"Receipt output already exists: {args.output}")
        receipt = build_receipt(args.trx, args.native_proof, args.framework, args.rid)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_bytes(encoded(receipt))
    except (ReceiptError, OSError, UnicodeError) as error:
        print(f"Native receipt error: {error}", file=sys.stderr)
        return 1
    warning = (
        " This receipt has failed/skipped methods and can never satisfy a release row."
        if receipt["failedTestIds"] or receipt["skippedTestIds"] else ""
    )
    print(
        f"Wrote a schemaVersion:1 native execution receipt for {args.framework}/{args.rid}: "
        f"{len(receipt['passedTestIds'])} passed, {len(receipt['failedTestIds'])} failed, "
        f"{len(receipt['skippedTestIds'])} skipped.{warning} This converts real TRX evidence "
        "into the release ledger's receipt shape; it does not itself qualify any requirement."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
