#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Copy and verify the model resources from the already pinned source corpus."""

from __future__ import annotations

import argparse
import importlib.util
from pathlib import Path
import re
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
DRAFT_MODELS = "eng/draft-models.json"


def synchronize(root: Path, check: bool) -> int:
    lock = source_tool.verify_corpus(root)
    records = source_tool.source_records(lock)
    directory = root / "src" / "XRegistry.Models" / "Resources"
    expected: dict[str, bytes] = {}
    for name, path in {**MODELS, "LICENSE": "LICENSE"}.items():
        if path not in records:
            raise source_tool.SpecificationError(f"The pinned model is missing: {path}")
        filename = name if name == "LICENSE" else name + ".json"
        expected[filename], _ = source_tool.active_source(root, path, records)
    draft_manifest = source_tool.read_json(source_tool.contained(root, DRAFT_MODELS))
    if (
        not isinstance(draft_manifest, dict)
        or set(draft_manifest) != {"schemaVersion", "models"}
        or type(draft_manifest["schemaVersion"]) is not int
        or draft_manifest["schemaVersion"] != 1
        or not isinstance(draft_manifest["models"], dict)
    ):
        raise source_tool.SpecificationError("Invalid working-draft model source manifest.")
    for name, entry in draft_manifest["models"].items():
        if (
            not isinstance(name, str)
            or not re.fullmatch(r"[a-z][a-z0-9-]*", name)
            or name in MODELS
            or not isinstance(entry, dict)
            or set(entry) != {"sourceRepository", "sourceCommit", "sourcePath", "file", "sha256"}
            or not isinstance(entry["sourceRepository"], str)
            or re.fullmatch(r"https://github\.com/[A-Za-z0-9-]+/[A-Za-z0-9._-]+", entry["sourceRepository"]) is None
            or not isinstance(entry["sourceCommit"], str)
            or not re.fullmatch(r"[a-f0-9]{40}", entry["sourceCommit"])
            or not isinstance(entry["sourcePath"], str)
            or "\\" in entry["sourcePath"]
            or not entry["sourcePath"].startswith("workingdrafts/")
            or not entry["sourcePath"].endswith(".json")
            or not isinstance(entry["file"], str)
            or not entry["file"].startswith("tests/Conformance/DraftSources/")
            or not isinstance(entry["sha256"], str)
            or not re.fullmatch(r"[a-f0-9]{64}", entry["sha256"])
        ):
            raise source_tool.SpecificationError(f"Invalid working-draft model provenance: {name}")
        source_tool.contained(root, entry["sourcePath"])
        source_path = source_tool.contained(root, entry["file"])
        if not source_path.is_file():
            raise source_tool.SpecificationError(f"Working-draft model source is missing: {entry['file']}")
        data = source_path.read_bytes()
        if source_tool.sha256(data) != entry["sha256"]:
            raise source_tool.SpecificationError(f"Working-draft model source hash mismatch: {entry['file']}")
        expected[name + ".json"] = data
    if directory.exists():
        actual = {path.name for path in directory.iterdir()}
        if actual - expected.keys():
            raise source_tool.SpecificationError("The model resource directory contains unclassified files.")
    for filename, data in expected.items():
        path = source_tool.contained(directory, filename)
        source_tool.write_output(path, data, check)
    return len(draft_manifest["models"])


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args(argv)
    try:
        draft_count = synchronize(ROOT, args.check)
    except (source_tool.SpecificationError, OSError, UnicodeError, ValueError) as error:
        print(f"Built-in model resource error: {error}", file=sys.stderr)
        return 1
    print(f"Verified {len(MODELS)} pinned and {draft_count} draft model resources plus their retained license.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
