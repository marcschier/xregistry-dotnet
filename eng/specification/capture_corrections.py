#!/usr/bin/env python3
"""Explicitly capture reviewed spec corrections without overwriting frozen source evidence."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import re
import subprocess
import sys

import manage


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--case", required=True)
    parser.add_argument("--reason", required=True)
    parser.add_argument("--test", action="append", required=True)
    parser.add_argument("paths", nargs="+")
    args = parser.parse_args()
    try:
        if not re.fullmatch(r"SPEC-[0-9]{3}", args.case):
            raise manage.SpecificationError("Correction case must use SPEC-NNN.")
        if (
            not args.reason.strip() or any(not test.strip() for test in args.test)
            or len(set(args.test)) != len(args.test)
        ):
            raise manage.SpecificationError("Correction reason and unique regression test IDs must be nonempty.")
        lock = manage.verify_corpus(manage.ROOT)
        original = manage.source_records(lock)
        manifest = manage.contained(manage.ROOT, manage.CORRECTIONS.as_posix())
        before = manifest.read_bytes() if manifest.exists() else None
        document = manage.read_json(manifest) if before is not None else {
            "schemaVersion": 2,
            "baselineManifestSha256": lock["baselineManifestSha256"],
            "entries": [],
        }
        existing = manage.validate_corrections(manage.ROOT, original, document)
        entries = [
            {
                **entry, "predecessorSha256": entry["originalSha256"], "predecessorCase": None,
            } if document["schemaVersion"] == 1 else entry
            for entry in document["entries"]
        ]
        source_root = Path(os.path.abspath(args.source))
        anchor = Path(source_root.anchor)
        if source_root != anchor:
            manage.contained(anchor, source_root.relative_to(anchor).as_posix())
        if not source_root.is_dir():
            raise manage.SpecificationError("The correction source must be a directory.")
        revision = subprocess.run(
            ["git", "-C", str(source_root), "rev-parse", "HEAD"],
            check=True, capture_output=True, text=True, timeout=30
        ).stdout.strip()
        if not re.fullmatch(r"[a-f0-9]{40}", revision):
            raise manage.SpecificationError("The source repository has no valid commit provenance.")
        pending: dict[str, bytes] = {}
        seen: set[str] = set()
        for source in args.paths:
            path = source.replace("\\", "/")
            if path.casefold() in seen:
                raise manage.SpecificationError(f"Duplicate correction capture path: {path}")
            seen.add(path.casefold())
            if "opcua" in path.casefold():
                raise manage.SpecificationError("OPC-UA-specific corrections are out of scope.")
            source_path = manage.contained(source_root, path)
            if not source_path.is_file():
                raise manage.SpecificationError(f"Correction input is not a file: {path}")
            with source_path.open("rb") as stream:
                data = stream.read(manage.MAX_JSON_BYTES + 1)
            if len(data) > manage.MAX_JSON_BYTES:
                raise manage.SpecificationError(f"Correction input byte limit exceeded: {path}")
            destination = manage.CORRECTED_SOURCES / args.case / Path(path)
            predecessor = existing.get(path)
            if predecessor and predecessor["case"] == args.case:
                if manage.contained(manage.ROOT, predecessor["file"]).read_bytes() != data:
                    raise manage.SpecificationError(
                        f"Correction {args.case} already captured different bytes for {path}; use a new case."
                    )
                continue
            if predecessor and args.case < predecessor["case"]:
                raise manage.SpecificationError(f"Correction case must advance active history: {path}")
            original_hash = original[path]["sha256"] if path in original else None
            entries.append({
                "case": args.case,
                "path": path,
                "originalSha256": original_hash,
                "predecessorSha256": predecessor["sha256"] if predecessor else original_hash,
                "predecessorCase": predecessor["case"] if predecessor else None,
                "sha256": manage.sha256(data),
                "bytes": len(data),
                "file": destination.as_posix(),
                "sourceCommitBeforeCorrections": revision,
                "reason": args.reason,
                "testIds": args.test,
            })
            pending[destination.as_posix()] = data
        if not pending:
            print(f"All requested sources already match {args.case}; manifest and evidence are unchanged.")
            return 0
        document = {
            "schemaVersion": 2,
            "baselineManifestSha256": lock["baselineManifestSha256"],
            "entries": entries,
        }
        output = manage.encoded(document)
        if len(output) > manage.MAX_JSON_BYTES:
            raise manage.SpecificationError("Correction manifest byte limit exceeded.")
        manage.validate_corrections(manage.ROOT, original, document, pending=pending)
        if (manifest.read_bytes() if manifest.exists() else None) != before:
            raise manage.SpecificationError("Correction manifest changed during capture; retry with the current history.")
        created: list[Path] = []
        try:
            for relative, data in pending.items():
                destination = manage.contained(manage.ROOT, relative)
                destination.parent.mkdir(parents=True, exist_ok=True)
                with destination.open("xb") as stream:
                    created.append(destination)
                    stream.write(data)
                    stream.flush()
                    os.fsync(stream.fileno())
            manage.validate_corrections(manage.ROOT, original, document)
            if (manifest.read_bytes() if manifest.exists() else None) != before:
                raise manage.SpecificationError("Correction manifest changed during capture; retry with the current history.")
            manage.write_output(manifest, output)
        except (OSError, manage.SpecificationError) as error:
            for destination in reversed(created):
                try:
                    destination.unlink()
                except OSError as cleanup_error:
                    raise manage.SpecificationError(
                        f"{error}; also unable to remove new capture artifact {destination}: {cleanup_error}"
                    ) from cleanup_error
            raise
        print(f"Captured {len(pending)} corrected sources as {args.case}; original corpus and prior corrections are unchanged.")
        return 0
    except (manage.SpecificationError, OSError, ValueError, subprocess.SubprocessError) as error:
        print(f"Correction capture failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
