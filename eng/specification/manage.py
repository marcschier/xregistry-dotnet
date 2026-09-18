#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Import, verify and trace the pinned specification without a sibling checkout."""

from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import sys
import tempfile
from typing import Any


ROOT = Path(__file__).resolve().parents[2]
CORPUS = Path("tests") / "Conformance" / "Sources"
LOCK = Path("eng") / "specification-lock.json"
LEDGER = Path("tests") / "Conformance" / "requirements.json"
REPORT = Path("docs") / "conformance.md"
CORRECTIONS = Path("eng") / "specification" / "corrections.json"
CORRECTED_SOURCES = Path("tests") / "Conformance" / "Corrections"
STRENGTH = re.compile(
    r"\b(MUST NOT|SHALL NOT|SHOULD NOT|NOT RECOMMENDED|MUST|SHALL|SHOULD|"
    r"RECOMMENDED|MAY|OPTIONAL|REQUIRED)\b"
)
HASH = re.compile(r"[a-f0-9]{64}")
MAX_JSON_BYTES = 32 * 1024 * 1024
REVIEW_KEYS = {"status", "note", "roles"}
IMPLEMENTATION_KEYS = {"status", "testIds", "nativeEvidence"}
FRAMEWORKS = ("net8.0", "net10.0")
RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64")
VERSION_FILE = Path("version.json")
ALPHA_PRERELEASE = re.compile(
    r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-alpha(?:\.[0-9a-z-]+)*"
)


class SpecificationError(ValueError):
    """Pinned evidence, generated output or review metadata is inconsistent."""


def unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise SpecificationError(f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def invalid_constant(value: str) -> None:
    raise SpecificationError(f"Invalid JSON constant: {value}")


def read_json(path: Path) -> Any:
    with path.open("rb") as stream:
        data = stream.read(MAX_JSON_BYTES + 1)
    if len(data) > MAX_JSON_BYTES:
        raise SpecificationError(f"JSON byte limit exceeded: {path}")
    return json.loads(
        data.decode("utf-8"),
        object_pairs_hook=unique_object,
        parse_constant=invalid_constant,
    )


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def encoded(value: Any) -> bytes:
    return (json.dumps(value, ensure_ascii=True, indent=2) + "\n").encode("utf-8")


def contained(root: Path, relative: str) -> Path:
    if not isinstance(relative, str):
        raise SpecificationError("Source paths must be strings.")
    normalized = relative.replace("\\", "/")
    parts = normalized.split("/")
    if (
        not parts
        or any(
            part in ("", ".", "..")
            or part.endswith((".", " "))
            or re.search(r'[<>:"|?*\x00-\x1f]', part)
            for part in parts
        )
        or PurePosixPath(normalized).is_absolute()
    ):
        raise SpecificationError(f"Unsafe relative source path: {relative!r}")
    path = root
    for part in parts:
        path /= part
        junction = getattr(path, "is_junction", None)
        if path.is_symlink() or (junction is not None and junction()):
            raise SpecificationError(f"Source path traverses a link: {relative}")
    if not path.resolve().is_relative_to(root.resolve()):
        raise SpecificationError(f"Source path escapes its root: {relative}")
    return path


def write_output(path: Path, data: bytes, check: bool = False) -> None:
    if path.exists() and path.read_bytes() == data:
        return
    if check:
        raise SpecificationError(f"Generated output is missing or stale: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def source_records(document: Any) -> dict[str, dict[str, Any]]:
    if not isinstance(document, dict) or not isinstance(document.get("files"), list):
        raise SpecificationError("Source manifest must contain a file inventory.")
    records: dict[str, dict[str, Any]] = {}
    seen: set[str] = set()
    for entry in document["files"]:
        if not isinstance(entry, dict) or set(entry) != {"path", "bytes", "sha256"}:
            raise SpecificationError("Invalid source inventory entry.")
        path = entry["path"]
        if not isinstance(path, str):
            raise SpecificationError("Source inventory path is not a string.")
        path = path.replace("\\", "/")
        if path.casefold() in seen or "opcua" in path.casefold():
            raise SpecificationError(f"Duplicate or excluded source path: {path}")
        if (
            type(entry["bytes"]) is not int
            or not 0 <= entry["bytes"] <= MAX_JSON_BYTES
            or not isinstance(entry["sha256"], str)
            or not HASH.fullmatch(entry["sha256"])
        ):
            raise SpecificationError(f"Invalid size/hash for {path}")
        seen.add(path.casefold())
        records[path] = {**entry, "path": path}
    if (
        type(document.get("fileCount")) is not int
        or document["fileCount"] != len(records)
        or not records
    ):
        raise SpecificationError("Source fileCount does not match the inventory.")
    return records


def verify_source(root: Path, path: str, entry: dict[str, Any]) -> bytes:
    candidate = contained(root, path)
    if not candidate.is_file() or candidate.stat().st_size != entry["bytes"]:
        raise SpecificationError(f"Missing source or size mismatch: {path}")
    data = candidate.read_bytes()
    if sha256(data) != entry["sha256"]:
        raise SpecificationError(f"Source hash mismatch: {path}")
    return data


def load_scope(root: Path) -> dict[str, Any]:
    scope = read_json(root / "eng" / "specification" / "scope.json")
    if (
        not isinstance(scope, dict)
        or type(scope.get("schemaVersion")) is not int
        or scope["schemaVersion"] != 1
    ):
        raise SpecificationError("Unsupported specification scope.")
    if not isinstance(scope.get("normative"), dict) or not scope["normative"]:
        raise SpecificationError("The normative source inventory must not be empty.")
    return scope


def import_snapshot(root: Path, snapshot: Path) -> None:
    scope = load_scope(root)
    manifest_path = snapshot / "baseline-manifest.json"
    if sha256(manifest_path.read_bytes()) != scope["baselineManifestSha256"]:
        raise SpecificationError("Frozen baseline manifest hash does not match the approved scope.")
    manifest = read_json(manifest_path)
    if manifest.get("commit") != scope["commit"] or manifest.get("branch") != scope["branch"]:
        raise SpecificationError("Frozen baseline revision differs from the approved scope.")
    records = source_records(manifest)
    selected: dict[str, bytes] = {}
    excluded: list[dict[str, str]] = []
    for path, entry in sorted(records.items()):
        data = verify_source(snapshot, path, entry)
        if PurePosixPath(path).suffix in scope["excludeSuffixes"]:
            excluded.append({"path": path, "reason": "Presentation artwork, not conformance input."})
            continue
        destination = contained(root / CORPUS, path)
        if destination.exists() and destination.read_bytes() != data:
            raise SpecificationError(f"Refusing to overwrite changed pinned source: {path}")
        selected[path] = data
    if "LICENSE" not in selected or set(scope["normative"]) - selected.keys():
        raise SpecificationError("The source license or a normative document is absent.")
    lock = {
        "schemaVersion": 1,
        "sourceRepository": scope["sourceRepository"],
        "upstreamRepository": scope["upstreamRepository"],
        "commit": scope["commit"],
        "branch": scope["branch"],
        "includesUncommittedChanges": scope["includesUncommittedChanges"],
        "baselineManifestSha256": scope["baselineManifestSha256"],
        "corpusPath": CORPUS.as_posix(),
        "fileCount": len(selected),
        "files": [records[path] for path in selected],
        "excluded": excluded,
        "normative": scope["normative"],
    }
    lock_path = root / LOCK
    if lock_path.exists() and lock_path.read_bytes() != encoded(lock):
        raise SpecificationError("Refusing to replace a different specification lock.")
    for path, data in selected.items():
        write_output(contained(root / CORPUS, path), data)
    write_output(lock_path, encoded(lock))


def verify_corpus(root: Path) -> dict[str, Any]:
    scope = load_scope(root)
    lock = read_json(root / LOCK)
    if (
        not isinstance(lock, dict)
        or type(lock.get("schemaVersion")) is not int
        or lock["schemaVersion"] != 1
    ):
        raise SpecificationError("Unsupported specification lock.")
    for key in (
        "sourceRepository", "upstreamRepository", "commit", "branch",
        "includesUncommittedChanges", "baselineManifestSha256", "normative",
    ):
        if lock.get(key) != scope.get(key):
            raise SpecificationError(f"Specification lock/scope mismatch: {key}")
    if lock.get("corpusPath") != CORPUS.as_posix():
        raise SpecificationError("Unexpected corpus path.")
    records = source_records(lock)
    if "LICENSE" not in records or set(scope["normative"]) - records.keys():
        raise SpecificationError("The locked corpus is missing normative sources or license.")
    for path, entry in records.items():
        verify_source(root / CORPUS, path, entry)
    actual = {
        path.relative_to(root / CORPUS).as_posix()
        for path in (root / CORPUS).rglob("*")
        if path.is_file()
    }
    if actual != set(records):
        raise SpecificationError("The corpus contains missing or unlisted files.")
    correction_records(root, records)
    return lock


def correction_files(root: Path) -> set[str]:
    directory = contained(root, CORRECTED_SOURCES.as_posix())
    if not directory.exists():
        return set()
    if not directory.is_dir():
        raise SpecificationError("Corrected source inventory must be a directory.")

    def scan_error(error: OSError) -> None:
        raise SpecificationError(f"Unable to enumerate corrected source inventory: {error}") from error

    files: set[str] = set()
    for parent, directories, names in os.walk(directory, onerror=scan_error, followlinks=False):
        for name in directories + names:
            relative = (Path(parent) / name).relative_to(root).as_posix()
            candidate = contained(root, relative)
            if candidate.is_file():
                if relative.casefold() in files:
                    raise SpecificationError(f"Corrected source file collision: {relative}")
                files.add(relative.casefold())
            elif not candidate.is_dir():
                raise SpecificationError(f"Corrected source is not a regular file or directory: {relative}")
    return files


def correction_records(root: Path, original: dict[str, dict[str, Any]]) -> dict[str, dict[str, Any]]:
    manifest = contained(root, CORRECTIONS.as_posix())
    if not manifest.exists():
        if correction_files(root):
            raise SpecificationError("Corrected source inventory contains unlisted files without a manifest.")
        return {}
    return validate_corrections(root, original, read_json(manifest))


def validate_corrections(
    root: Path, original: dict[str, dict[str, Any]], document: Any,
    *, pending: dict[str, bytes] | None = None,
) -> dict[str, dict[str, Any]]:
    """Validate the complete history, optionally preflighting new files held in memory."""
    scope = load_scope(root)
    if (
        not isinstance(document, dict)
        or set(document) != {"schemaVersion", "baselineManifestSha256", "entries"}
        or type(document.get("schemaVersion")) is not int
        or document["schemaVersion"] not in {1, 2}
        or document.get("baselineManifestSha256") != scope["baselineManifestSha256"]
        or not isinstance(document.get("entries"), list)
    ):
        raise SpecificationError("Invalid explicit specification correction manifest.")
    result: dict[str, dict[str, Any]] = {}
    files: set[str] = set()
    directories: set[str] = set()
    cases: set[tuple[str, str]] = set()
    pending = {} if pending is None else pending
    used_pending: set[str] = set()
    paths = {path.casefold(): path for path in original}
    fields = {
        "case", "path", "originalSha256", "sha256", "bytes", "file",
        "sourceCommitBeforeCorrections", "reason", "testIds",
    }
    if document["schemaVersion"] == 2:
        fields |= {"predecessorSha256", "predecessorCase"}
    for entry in document["entries"]:
        if not isinstance(entry, dict) or set(entry) != fields:
            raise SpecificationError("Malformed specification correction entry fields.")
        path = entry["path"]
        if (
            not isinstance(path, str) or "\\" in path or "opcua" in path.casefold()
            or (path.casefold() in paths and paths[path.casefold()] != path)
            or (document["schemaVersion"] == 1 and path in result)
        ):
            raise SpecificationError("Duplicate, malformed or excluded correction path.")
        contained(root, CORPUS.as_posix() + "/" + path)
        paths[path.casefold()] = path
        before = original.get(path)
        if entry.get("originalSha256") != (before["sha256"] if before else None):
            raise SpecificationError(f"Correction original hash does not match frozen evidence: {path}")
        if (
            not isinstance(entry.get("case"), str) or not entry["case"].strip()
            or not re.fullmatch(r"SPEC-[0-9]{3}", entry["case"])
            or not isinstance(entry.get("sourceCommitBeforeCorrections"), str)
            or not re.fullmatch(r"[a-f0-9]{40}", entry["sourceCommitBeforeCorrections"])
            or not isinstance(entry.get("reason"), str) or not entry["reason"].strip()
            or not isinstance(entry.get("testIds"), list) or not entry["testIds"]
            or any(not isinstance(test, str) or not test.strip() for test in entry["testIds"])
            or len(set(entry["testIds"])) != len(entry["testIds"])
            or type(entry.get("bytes")) is not int or not 0 <= entry["bytes"] <= MAX_JSON_BYTES
            or not isinstance(entry.get("sha256"), str) or not HASH.fullmatch(entry["sha256"])
            or not isinstance(entry.get("file"), str)
            or entry["file"] != f"{CORRECTED_SOURCES.as_posix()}/{entry['case']}/{path}"
            or entry["file"].casefold() in files
        ):
            raise SpecificationError(f"Correction provenance/evidence fields are invalid: {path}")
        identity = (path, entry["case"])
        if identity in cases:
            raise SpecificationError(f"Duplicate correction path/case: {path}, {entry['case']}")
        cases.add(identity)
        if document["schemaVersion"] == 2:
            predecessor = result.get(path)
            expected_hash = predecessor["sha256"] if predecessor else entry["originalSha256"]
            expected_case = predecessor["case"] if predecessor else None
            if (
                "predecessorSha256" not in entry or "predecessorCase" not in entry
                or entry["predecessorSha256"] != expected_hash
                or entry["predecessorCase"] != expected_case
            ):
                raise SpecificationError(f"Correction predecessor does not match active history: {path}")
            if predecessor and entry["case"] <= predecessor["case"]:
                raise SpecificationError(f"Correction case must advance active history: {path}")
        candidate = contained(root, entry["file"])
        parents = {parent.as_posix().casefold() for parent in PurePosixPath(entry["file"]).parents}
        if entry["file"].casefold() in directories or parents & files:
            raise SpecificationError(f"Corrected source file/directory collision: {entry['file']}")
        directories.update(parents)
        if entry["file"] in pending:
            if candidate.exists():
                raise SpecificationError(f"Refusing to replace an existing correction file or directory: {entry['file']}")
            for parent in candidate.parents:
                if parent == root:
                    break
                if parent.exists() and not parent.is_dir():
                    raise SpecificationError(f"Correction destination parent is not a directory: {parent}")
            data = pending[entry["file"]]
            used_pending.add(entry["file"])
        else:
            if not candidate.is_file() or candidate.stat().st_size != entry["bytes"]:
                raise SpecificationError(f"Corrected source bytes are missing or changed: {path}")
            data = candidate.read_bytes()
        if not isinstance(data, bytes) or len(data) != entry["bytes"] or sha256(data) != entry["sha256"]:
            raise SpecificationError(f"Corrected source bytes are missing or changed: {path}")
        files.add(entry["file"].casefold())
        result[path] = entry
    actual = correction_files(root)
    staged = {path.casefold() for path in used_pending}
    if used_pending != set(pending) or actual & staged or (actual | staged) != files:
        raise SpecificationError("Corrected source inventory contains missing or unlisted files.")
    return result


def active_source(root: Path, path: str, original: dict[str, dict[str, Any]]) -> tuple[bytes, str]:
    corrections = correction_records(root, original)
    if path in corrections:
        entry = corrections[path]
        return contained(root, entry["file"]).read_bytes(), entry["sha256"]
    if path not in original:
        raise SpecificationError(f"The source is not present in the pinned inventory: {path}")
    return verify_source(root / CORPUS, path, original[path]), original[path]["sha256"]


def extract_requirements(
    path: str, text: str, source_hash: str, owner: str
) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    headings: list[str] = []
    paragraph: list[tuple[int, str]] = []
    occurrences: Counter[str] = Counter()
    fence: str | None = None
    in_comment = False

    def emit() -> None:
        if not paragraph:
            return
        block = " ".join(line.strip() for _, line in paragraph)
        strengths = sorted(set(STRENGTH.findall(block)))
        if strengths:
            section = " / ".join(headings)
            identity = sha256(f"{path}\n{section}\n{block}".encode("utf-8"))[:24]
            occurrences[identity] += 1
            rows.append(
                {
                    "id": f"{owner}-{identity}-{occurrences[identity]}",
                    "source": {
                        "path": path,
                        "sha256": source_hash,
                        "firstLine": paragraph[0][0],
                        "lastLine": paragraph[-1][0],
                        "section": section,
                    },
                    "owner": owner,
                    "strengths": strengths,
                    "text": block,
                    "review": {"status": "unreviewed", "note": "", "roles": []},
                    "implementation": {"status": "pending", "testIds": [], "nativeEvidence": []},
                }
            )
        paragraph.clear()

    for number, original in enumerate(text.splitlines(), 1):
        line = original
        if in_comment:
            _, end, line = line.partition("-->")
            if not end:
                continue
            in_comment = False
        while "<!--" in line:
            before, _, after = line.partition("<!--")
            _, end, rest = after.partition("-->")
            if not end:
                line = before
                in_comment = True
                break
            line = before + rest
        stripped = line.strip()
        marker = re.match(r"^\s{0,3}(`{3,}|~{3,})", line)
        if fence is not None:
            if marker and marker[1][0] == fence[0] and len(marker[1]) >= len(fence):
                fence = None
            continue
        if marker:
            emit()
            fence = marker[1]
            continue
        heading = re.match(r"^(#{1,6})\s+(.+?)\s*#*\s*$", line)
        if heading:
            emit()
            level = len(heading[1])
            headings = headings[: level - 1]
            headings.append(heading[2])
        elif not stripped:
            emit()
        elif stripped.startswith("|"):
            emit()
            paragraph.append((number, line))
            emit()
        elif re.match(r"^\s{0,3}(?:[-*+]|\d+[.)])\s", line):
            emit()
            paragraph.append((number, line))
        else:
            paragraph.append((number, line))
    emit()
    if fence is not None or in_comment:
        raise SpecificationError(f"Unclosed code fence or HTML comment: {path}")
    return rows


def validate_review(row: dict[str, Any]) -> None:
    review = row.get("review")
    implementation = row.get("implementation")
    if not isinstance(review, dict) or set(review) != REVIEW_KEYS:
        raise SpecificationError(f"Invalid review fields: {row.get('id')}")
    if (
        not isinstance(review["status"], str)
        or review["status"] not in {"unreviewed", "reviewed", "informative"}
    ):
        raise SpecificationError(f"Invalid review status: {row.get('id')}")
    if (
        not isinstance(review["note"], str)
        or not isinstance(review["roles"], list)
        or any(
            not isinstance(role, str) or role not in {"client", "server", "producer"}
            for role in review["roles"]
        )
        or len(set(review["roles"])) != len(review["roles"])
    ):
        raise SpecificationError(f"Invalid review note/roles: {row.get('id')}")
    if review["status"] != "unreviewed" and not review["note"].strip():
        raise SpecificationError(f"Reviewed requirements need a rationale: {row.get('id')}")
    if review["status"] == "reviewed" and not review["roles"]:
        raise SpecificationError(f"Reviewed requirements need applicable roles: {row.get('id')}")
    if not isinstance(implementation, dict) or set(implementation) != IMPLEMENTATION_KEYS:
        raise SpecificationError(f"Invalid implementation fields: {row.get('id')}")
    if (
        not isinstance(implementation["status"], str)
        or implementation["status"] not in {"pending", "implemented", "qualified"}
    ):
        raise SpecificationError(f"Invalid implementation status: {row.get('id')}")
    tests = implementation["testIds"]
    evidence = implementation["nativeEvidence"]
    if (
        not isinstance(tests, list)
        or any(not isinstance(item, str) or not item.strip() for item in tests)
        or len(set(tests)) != len(tests)
        or not isinstance(evidence, list)
    ):
        raise SpecificationError(f"Invalid test/evidence fields: {row.get('id')}")
    if implementation["status"] != "pending" and (review["status"] != "reviewed" or not tests):
        raise SpecificationError(f"Implementation claims need reviewed tests: {row.get('id')}")
    for item in evidence:
        if (
            not isinstance(item, dict)
            or set(item) != {"framework", "rid", "report", "sha256"}
            or item["framework"] not in FRAMEWORKS
            or item["rid"] not in RIDS
            or not isinstance(item["report"], str)
            or not isinstance(item["sha256"], str)
            or not HASH.fullmatch(item["sha256"])
        ):
            raise SpecificationError(f"Invalid native evidence: {row.get('id')}")


def generate_ledger(root: Path, lock: dict[str, Any]) -> dict[str, Any]:
    previous: dict[str, dict[str, Any]] = {}
    semantic_reviewed = False
    correction_hash = sha256((root / CORRECTIONS).read_bytes()) if (root / CORRECTIONS).exists() else None
    if (root / LEDGER).exists():
        old = read_json(root / LEDGER)
        if (
            not isinstance(old, dict)
            or type(old.get("schemaVersion")) is not int
            or old["schemaVersion"] != 1
            or not isinstance(old.get("requirements"), list)
            or type(old.get("semanticCoverageReviewed")) is not bool
            or old.get("baselineManifestSha256") != lock["baselineManifestSha256"]
        ):
            raise SpecificationError("Invalid existing requirement ledger.")
        semantic_reviewed = old["semanticCoverageReviewed"]
        corrections_changed = old.get("correctionsSha256") != correction_hash
        if semantic_reviewed and corrections_changed:
            raise SpecificationError("The corrected specification baseline changed; reset and repeat semantic coverage review explicitly.")
        for row in old["requirements"]:
            if not isinstance(row, dict) or not isinstance(row.get("id"), str):
                raise SpecificationError("Malformed requirement entry.")
            if row["id"] in previous:
                raise SpecificationError(f"Duplicate requirement ID: {row['id']}")
            validate_review(row)
            if corrections_changed and row["review"]["status"] != "unreviewed":
                raise SpecificationError("The corrected specification baseline changed; reset and repeat requirement review explicitly.")
            previous[row["id"]] = row
    records = source_records(lock)
    rows: list[dict[str, Any]] = []
    for path, owner in lock["normative"].items():
        source_bytes, source_hash = active_source(root, path, records)
        source = source_bytes.decode("utf-8")
        for row in extract_requirements(path, source, source_hash, owner):
            old = previous.pop(row["id"], None)
            if old is not None:
                if (
                    old.get("source", {}).get("sha256") != row["source"]["sha256"]
                    and old["review"]["status"] != "unreviewed"
                ):
                    raise SpecificationError(f"Reviewed source changed: {row['id']}")
                row["review"] = old["review"]
                row["implementation"] = old["implementation"]
            rows.append(row)
    if previous:
        raise SpecificationError(
            "Existing requirements disappeared; review the baseline change explicitly: "
            + ", ".join(list(previous)[:5])
        )
    return {
        "schemaVersion": 1,
        "baselineManifestSha256": lock["baselineManifestSha256"],
        "correctionsSha256": correction_hash,
        "extraction": "Normative prose blocks/table rows; manual semantic review remains required.",
        "semanticCoverageReviewed": semantic_reviewed,
        "requirements": rows,
    }


def render_report(ledger: dict[str, Any], lock: dict[str, Any]) -> bytes:
    rows = ledger["requirements"]
    counts = Counter(row["owner"] for row in rows)
    reviewed = Counter(row["owner"] for row in rows if row["review"]["status"] != "unreviewed")
    qualified = Counter(
        row["owner"] for row in rows if row["implementation"]["status"] == "qualified"
    )
    lines = [
        "# Conformance evidence", "",
        "Generated by `python eng\\specification\\manage.py generate`.", "",
        "Use `python eng\\specification\\manage.py status` for machine-readable",
        "dispositioned/reviewed/implemented/test-mapped/native-evidence counts",
        "without rewriting generated files.", "",
        "**Current evidence status.** Extracted source statements are a review inventory,",
        "not evidence that the client/server implements them. Keyword extraction can",
        "combine multiple obligations and miss procedural or artifact-only requirements;",
        "each family requires explicit semantic coverage review.", "",
        f"Pinned source files: **{lock['fileCount']}**. "
        f"Normative documents: **{len(lock['normative'])}**. "
        f"Extracted blocks: **{len(rows)}**.", "",
        "| Family | Blocks | Reviewed | Qualified |",
        "| --- | ---: | ---: | ---: |",
    ]
    for owner, count in sorted(counts.items()):
        lines.append(f"| {owner} | {count} | {reviewed[owner]} | {qualified[owner]} |")
    lines += [
        "", "## Artifacts and procedural coverage", "",
        "The source lock inventories model documents, derived JSON Schema/JSON",
        "Structure/Avro/OpenAPI artifacts, mapping/OCI schemas, and deterministic",
        "examples as well as prose. Their required behavior must be mapped to",
        "concrete public tests; file presence or a successful build is not coverage.", "",
        "The ledger retains explicit review and implementation fields. Do not replace",
        "unreviewed/pending values with claims generated from a test count. Native",
        "evidence must identify actual report bytes and the tested framework/RID.", "",
        "The release command deliberately fails until semantic coverage and every",
        "applicable requirement's execution evidence have been qualified.", "",
    ]
    return "\n".join(lines).encode("utf-8")


def accounting(ledger: dict[str, Any], lock: dict[str, Any]) -> dict[str, Any]:
    rows = ledger.get("requirements")
    if (
        not isinstance(ledger, dict)
        or type(ledger.get("schemaVersion")) is not int
        or ledger["schemaVersion"] != 1
        or type(ledger.get("semanticCoverageReviewed")) is not bool
        or not isinstance(rows, list)
    ):
        raise SpecificationError("Invalid requirement ledger for accounting.")

    owners = sorted(set(lock["normative"].values()) | {row.get("owner", "") for row in rows})
    by_owner: dict[str, dict[str, int]] = {
        owner: {
            "requirements": 0,
            "dispositioned": 0,
            "reviewed": 0,
            "informative": 0,
            "implemented": 0,
            "qualified": 0,
            "testMapped": 0,
            "nativeEvidenceMapped": 0,
            "qualificationGaps": 0,
        }
        for owner in owners if owner
    }
    totals = {
        "requirements": 0,
        "dispositioned": 0,
        "reviewed": 0,
        "informative": 0,
        "implemented": 0,
        "qualified": 0,
        "testMapped": 0,
        "nativeEvidenceMapped": 0,
        "qualificationGaps": 0,
    }
    for row in rows:
        validate_review(row)
        owner = row["owner"]
        bucket = by_owner.setdefault(owner, {key: 0 for key in totals})
        implementation = row["implementation"]
        review = row["review"]
        status = implementation["status"]
        for target in (bucket, totals):
            target["requirements"] += 1
            if review["status"] != "unreviewed":
                target["dispositioned"] += 1
            if review["status"] == "reviewed":
                target["reviewed"] += 1
            if review["status"] == "informative":
                target["informative"] += 1
            if status == "implemented":
                target["implemented"] += 1
            if status == "qualified":
                target["qualified"] += 1
            if implementation["testIds"]:
                target["testMapped"] += 1
            if implementation["nativeEvidence"]:
                target["nativeEvidenceMapped"] += 1
            if review["status"] != "informative" and status != "qualified":
                target["qualificationGaps"] += 1

    return {
        "schemaVersion": 1,
        "baselineManifestSha256": ledger["baselineManifestSha256"],
        "correctionsSha256": ledger.get("correctionsSha256"),
        "semanticCoverageReviewed": ledger["semanticCoverageReviewed"],
        "pinnedFiles": lock["fileCount"],
        "normativeDocuments": len(lock["normative"]),
        "totals": totals,
        "byOwner": by_owner,
    }


def source_version_is_alpha(root: Path) -> bool:
    """True only when root/version.json declares an explicit `-alpha`/`-alpha.N` prerelease.

    This is the sole, narrowly-scoped, maintainer-approved exception to the
    specification qualification gate (see docs/releasing.md). It fails closed:
    a missing, oversized or malformed version file is never treated as alpha.
    """
    path = root / VERSION_FILE
    try:
        if not path.is_file():
            return False
        document = read_json(path)
    except (OSError, UnicodeError, json.JSONDecodeError, SpecificationError):
        return False
    value = document.get("version") if isinstance(document, dict) else None
    return isinstance(value, str) and ALPHA_PRERELEASE.fullmatch(value) is not None


def release_check(root: Path, ledger: dict[str, Any], *, alpha: bool = False) -> None:
    if ledger.get("semanticCoverageReviewed") is not True and not alpha:
        raise SpecificationError("Semantic coverage review is incomplete.")
    rows = ledger.get("requirements")
    if not isinstance(rows, list) or not rows:
        raise SpecificationError("A release requires a nonempty requirement inventory.")
    for row in rows:
        validate_review(row)
        if row["review"]["status"] == "informative":
            continue
        if alpha:
            # Explicit maintainer-approved alpha prerelease exception (docs/releasing.md):
            # requirement/review structure is still validated above, but the reviewed and
            # qualified statuses plus native execution evidence are not required.
            continue
        if row["review"]["status"] != "reviewed" or row["implementation"]["status"] != "qualified":
            raise SpecificationError(f"Requirement is not qualified: {row['id']}")
        cells: set[tuple[str, str]] = set()
        for evidence in row["implementation"]["nativeEvidence"]:
            report = contained(root, evidence["report"])
            if not report.is_file() or sha256(report.read_bytes()) != evidence["sha256"]:
                raise SpecificationError(f"Native report is missing or changed: {row['id']}")
            receipt = read_json(report)
            if (
                not isinstance(receipt, dict)
                or type(receipt.get("schemaVersion")) is not int
                or receipt["schemaVersion"] != 1
                or receipt.get("framework") != evidence["framework"]
                or receipt.get("rid") != evidence["rid"]
                or receipt.get("nativeExecutable") is not True
                or receipt.get("failedTestIds") != []
                or receipt.get("skippedTestIds") != []
                or not isinstance(receipt.get("passedTestIds"), list)
                or any(not isinstance(test, str) for test in receipt["passedTestIds"])
                or not set(row["implementation"]["testIds"]).issubset(receipt["passedTestIds"])
            ):
                raise SpecificationError(f"Native execution receipt is invalid: {row['id']}")
            cells.add((evidence["framework"], evidence["rid"]))
        if cells != {(framework, rid) for framework in FRAMEWORKS for rid in RIDS}:
            raise SpecificationError(f"Native execution cells are incomplete: {row['id']}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("import", "verify", "generate", "check", "release", "status"))
    parser.add_argument("--snapshot", type=Path)
    args = parser.parse_args(argv)
    try:
        if args.operation == "import":
            if args.snapshot is None:
                raise SpecificationError("Import requires --snapshot.")
            import_snapshot(ROOT, args.snapshot)
        lock = verify_corpus(ROOT)
        if args.operation != "verify":
            ledger = generate_ledger(ROOT, lock)
            if args.operation == "status":
                sys.stdout.buffer.write(encoded(accounting(ledger, lock)))
                return 0
            check = args.operation in {"check", "release"}
            write_output(ROOT / LEDGER, encoded(ledger), check)
            write_output(ROOT / REPORT, render_report(ledger, lock), check)
            if args.operation == "release":
                alpha = source_version_is_alpha(ROOT)
                if alpha:
                    print(
                        "ALPHA PRERELEASE EXCEPTION: version.json declares an explicit "
                        "-alpha prerelease; specification qualification and native-evidence "
                        "enforcement are not required for this release. "
                        "See docs/releasing.md#alpha-prerelease-exception.",
                        file=sys.stderr,
                    )
                release_check(ROOT, ledger, alpha=alpha)
        print(
            f"Specification {args.operation}: {lock['fileCount']} pinned files verified. "
            "This is provenance consistency, not implemented conformance."
        )
        return 0
    except (SpecificationError, OSError, UnicodeError, json.JSONDecodeError) as error:
        print(f"Specification error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
