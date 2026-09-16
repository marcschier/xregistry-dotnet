#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Fail-closed execution accounting for the pinned upstream xr checker."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import sys


SUITES = frozenset(
    ("TestSniff", "TestModel", "TestCapabilities", "TestRegistryRoot", "TestGroups", "TestResources")
)
SUMMARY = re.compile(r"^Pass: (\d+)\s+Fail: (\d+)\s+Warn: (\d+)\s+Skip: (\d+)\s*$")
SUITE = re.compile(r"^\W*PASS: (Test\w+)\s*$")
BAD_RESULT = re.compile(r"^\W*(?:FAIL|WARN|SKIP):")
MAX_LOG_BYTES = 4 * 1024 * 1024


class CheckerError(ValueError):
    """The checker did not execute the qualified fixture without unexpected results."""


def check_output(text: str, expected_passes: int) -> dict:
    if type(expected_passes) is not int or not 1 <= expected_passes <= 100000:
        raise CheckerError("Expected pass count must be a positive bounded integer.")
    lines = text.splitlines()
    summaries = [match for line in lines if (match := SUMMARY.fullmatch(line))]
    if len(summaries) != 1 or not lines or not SUMMARY.fullmatch(lines[-1]):
        raise CheckerError("The checker must finish with exactly one complete result summary.")
    passes, failures, warnings, skips = map(int, summaries[0].groups())
    if passes != expected_passes or failures or warnings or skips:
        raise CheckerError(
            f"Unexpected checker totals: pass={passes}, fail={failures}, "
            f"warn={warnings}, skip={skips}; expected {expected_passes} clean passes."
        )
    actual = [match[1] for line in lines if (match := SUITE.fullmatch(line))]
    if len(actual) != len(SUITES) or set(actual) != SUITES:
        raise CheckerError("The checker did not execute exactly the six expected top-level suites.")
    if any(BAD_RESULT.match(line) for line in lines):
        raise CheckerError("Unexpected failing, warning, or skipped result in the checker tree.")
    return {"passes": passes, "failures": 0, "warnings": 0, "skips": 0, "suites": sorted(actual)}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path)
    parser.add_argument("--expected-passes", type=int, required=True)
    args = parser.parse_args(argv)
    try:
        with args.log.open("rb") as stream:
            data = stream.read(MAX_LOG_BYTES + 1)
        if len(data) > MAX_LOG_BYTES:
            raise CheckerError("Checker output exceeds its byte limit.")
        result = check_output(data.decode("utf-8"), args.expected_passes)
    except (CheckerError, OSError, UnicodeError) as error:
        print(f"Upstream checker accounting failed: {error}", file=sys.stderr)
        return 1
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
