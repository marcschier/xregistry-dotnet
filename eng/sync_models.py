#!/usr/bin/env python3
"""Copy and verify the model resources from the already pinned source corpus."""

from __future__ import annotations

import argparse
import importlib.util
from pathlib import Path
import sys


ROOT = Path(__file__).resolve().parent.parent
SPEC = importlib.util.spec_from_file_location(
    "xregistry_specification", ROOT / "eng" / "specification" / "manage.py"
)
assert SPEC is not None and SPEC.loader is not None
source_tool = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(source_tool)

MODELS = {
    "core": "core/model.json",
    "endpoint": "endpoint/model.json",
    "message": "message/model.json",
    "schema": "schema/model.json",
    "cloudevents": "cloudevents/model.json",
    "registry": "workingdrafts/models/registry/model.json",
    "openusd": "workingdrafts/models/openusd/model.json",
}


def synchronize(root: Path, check: bool) -> None:
    lock = source_tool.verify_corpus(root)
    records = source_tool.source_records(lock)
    directory = root / "src" / "XRegistry.Models" / "Resources"
    expected: dict[str, bytes] = {}
    for name, path in {**MODELS, "LICENSE": "LICENSE"}.items():
        if path not in records:
            raise source_tool.SpecificationError(f"The pinned model is missing: {path}")
        filename = name if name == "LICENSE" else name + ".json"
        expected[filename], _ = source_tool.active_source(root, path, records)
    if directory.exists():
        actual = {path.name for path in directory.iterdir()}
        if actual - expected.keys():
            raise source_tool.SpecificationError("The model resource directory contains unclassified files.")
    for filename, data in expected.items():
        path = source_tool.contained(directory, filename)
        source_tool.write_output(path, data, check)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args(argv)
    try:
        synchronize(ROOT, args.check)
    except (source_tool.SpecificationError, OSError, UnicodeError, ValueError) as error:
        print(f"Built-in model resource error: {error}", file=sys.stderr)
        return 1
    print(f"Verified {len(MODELS)} pinned model resources and their retained license.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
