#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Fail-closed release assembly and exact-artifact NuGet promotion."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tempfile
from typing import Any
from urllib.parse import quote
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = "marcschier/xregistry-dotnet"
REPOSITORY_URL = f"https://github.com/{REPOSITORY}"
WORKFLOW = ".github/workflows/release.yml"
PROMOTION_WORKFLOW = ".github/workflows/nuget.yml"
ENVIRONMENT = "release"
APPROVER_LOGIN = "marcschier"
APPROVER_ID = 11168470
MANIFEST = "release-manifest.json"
SOURCE_FILES = {
    "source-packages.json": "eng/packages.json",
    "source-specification-lock.json": "eng/specification-lock.json",
    "source-corrections.json": "eng/specification/corrections.json",
    "source-requirements.json": "tests/Conformance/requirements.json",
}
GATES = {
    "packages": "python eng\\check_packages.py --release",
    "conformance": "python eng\\specification\\manage.py release",
}
MAX_JSON = 32 * 1024 * 1024
MAX_FILE = 128 * 1024 * 1024
MAX_ARCHIVE = 1024 * 1024 * 1024
MAX_FILES = 4096
MAX_RESULTS = 1000
SHA256 = re.compile(r"[0-9a-f]{64}")
COMMIT = re.compile(r"[0-9a-f]{40}")
VERSION = re.compile(
    r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-([0-9a-z-]+(?:\.[0-9a-z-]+)*))?"
)
RESERVED = re.compile(r"(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", re.I)


def load_tool(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load repository qualification tool: {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


inventory = load_tool("_release_inventory", ROOT / "eng" / "check_packages.py")
specification = load_tool(
    "_release_specification", ROOT / "eng" / "specification" / "manage.py"
)


class ReleaseError(ValueError):
    """The release contract or an external qualification check was not satisfied."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ReleaseError(message)


def keys(value: Any, expected: set[str], label: str) -> None:
    require(isinstance(value, dict) and set(value) == expected, f"Invalid {label} fields.")


def positive(value: Any, label: str) -> int:
    require(type(value) is int and 0 < value < 2**63, f"Invalid {label}.")
    return value


def digest(value: Any, label: str, pattern: re.Pattern[str] = SHA256) -> str:
    require(
        isinstance(value, str) and pattern.fullmatch(value) is not None
        and set(value) != {"0"},
        f"Invalid {label}.",
    )
    return value


def version(value: Any) -> str:
    require(isinstance(value, str) and 0 < len(value) <= 64, "Explicit version is required (1-64 characters).")
    match = VERSION.fullmatch(value)
    require(match is not None, "Version must be canonical lowercase SemVer, without v, whitespace or build metadata.")
    require(all(int(part) <= 2147483647 for part in match.groups()[:3]), "Version component exceeds NuGet's integer limit.")
    if match[4] is not None:
        require(
            all(not part.isdigit() or part == "0" or not part.startswith("0") for part in match[4].split(".")),
            "Numeric prerelease identifiers must not have leading zeroes.",
        )
    return value


def is_alpha_prerelease(value: str) -> bool:
    """True only for an explicit `-alpha`/`-alpha.N` prerelease identifier.

    This is the sole, narrowly-scoped, maintainer-approved exception to the
    package/specification qualification gate (see docs/releasing.md). It never
    applies to a stable version or any other prerelease channel (`rc`, `beta`,
    and so on), and it does not relax build, test, hashing, attestation or
    approval requirements.
    """
    match = VERSION.fullmatch(version(value))
    prerelease = match[4]
    return prerelease is not None and prerelease.split(".")[0] == "alpha"


def json_bytes(raw: bytes) -> Any:
    require(len(raw) <= MAX_JSON, "JSON byte limit exceeded.")
    return json.loads(
        raw.decode("utf-8"),
        object_pairs_hook=inventory.unique_object,
        parse_constant=inventory.invalid_constant,
    )


def encoded(value: Any) -> bytes:
    return (json.dumps(value, ensure_ascii=True, sort_keys=True, indent=2) + "\n").encode("utf-8")


def read_bytes(path: Path, limit: int = MAX_FILE) -> bytes:
    regular_file(path)
    with path.open("rb") as stream:
        raw = stream.read(limit + 1)
    require(len(raw) <= limit, f"File byte limit exceeded: {path.name}")
    return raw


def sha256(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def file_hash(path: Path) -> str:
    regular_file(path)
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def no_link(path: Path) -> None:
    for part in (path, *path.parents):
        junction = getattr(part, "is_junction", None)
        require(not part.is_symlink() and not (junction is not None and junction()), f"Links are forbidden: {path.name}")


def regular_file(path: Path) -> None:
    no_link(path)
    require(path.is_file(), f"Required regular file is missing: {path.name}")


def portable_path(value: Any, *, flat: bool = False) -> str:
    require(isinstance(value, str) and 0 < len(value) <= 1024, "Invalid file path.")
    parts = value.split("/")
    require(
        "\\" not in value and (not flat or len(parts) == 1)
        and all(
            part not in ("", ".", "..") and len(part) <= 255
            and not part.endswith((".", " "))
            and not re.search(r'[<>:"|?*\x00-\x1f\x7f]', part)
            and not RESERVED.match(part)
            and part.isascii()
            for part in parts
        ),
        f"Unsafe file path: {value!r}",
    )
    return value


def source_path(root: Path, value: str) -> Path:
    normalized = portable_path(value.replace("\\", "/"))
    return specification.contained(root, normalized)


def package_ids(path: Path, root: Path, *, qualified: bool) -> tuple[str, ...]:
    regular_file(path)
    projects = inventory.load_inventory(path, root)
    packages = tuple(sorted(item.name for item in projects if not item.sample))
    require(len(packages) == 11 and len(projects) == 14, "Release requires exactly eleven packages and three samples.")
    if qualified:
        unfinished = [item.name for item in projects if item.status != "qualified"]
        require(not unfinished, f"Release inventory is not qualified: {', '.join(unfinished)}")
    return packages


def identity_document(
    requested: str, repository_id: int, commit: str, tag_object: str, run_id: int, attempt: int
) -> dict[str, Any]:
    value = {
        "version": requested, "repository": REPOSITORY, "repositoryId": repository_id,
        "tag": f"v{requested}", "tagObject": tag_object, "commit": commit,
        "workflow": WORKFLOW, "runId": run_id, "runAttempt": attempt,
    }
    validate_identity(value)
    return value


def validate_identity(value: Any) -> None:
    keys(value, {"version", "repository", "repositoryId", "tag", "tagObject", "commit", "workflow", "runId", "runAttempt"}, "release identity")
    version(value["version"])
    require(value["repository"] == REPOSITORY and value["workflow"] == WORKFLOW, "Wrong source repository or release workflow.")
    require(value["tag"] == f"v{value['version']}", "Version/tag identity mismatch.")
    for field in ("repositoryId", "runId", "runAttempt"):
        positive(value[field], field)
    for field in ("commit", "tagObject"):
        digest(value[field], field, COMMIT)


def artifact_name(identity: dict[str, Any]) -> str:
    validate_identity(identity)
    return f"xregistry-release-{identity['version']}-{identity['commit']}-{identity['runId']}-{identity['runAttempt']}"


def validate_selection(value: Any) -> None:
    keys(value, {"identity", "workflowId", "artifact", "sourceSha256"}, "selection")
    validate_identity(value["identity"])
    positive(value["workflowId"], "workflow ID")
    artifact = value["artifact"]
    keys(artifact, {"id", "name", "size", "sha256"}, "artifact")
    positive(artifact["id"], "artifact ID")
    require(0 < positive(artifact["size"], "artifact size") <= MAX_ARCHIVE, "Artifact byte limit exceeded.")
    digest(artifact["sha256"], "artifact digest")
    require(artifact["name"] == artifact_name(value["identity"]), "Artifact name/attempt mismatch.")
    keys(value["sourceSha256"], set(SOURCE_FILES), "source hashes")
    for hashed in value["sourceSha256"].values():
        digest(hashed, "source file hash")


def checked(command: list[str], root: Path = ROOT, *, capture: bool = False, timeout: int = 120) -> bytes:
    try:
        result = subprocess.run(
            command, cwd=root, check=False, capture_output=capture, timeout=timeout,
        )
    except subprocess.TimeoutExpired as error:
        raise ReleaseError(f"{command[0]} timed out; the operation did not qualify.") from error
    require(result.returncode == 0, f"{command[0]} failed (exit {result.returncode}); the operation did not qualify.")
    return result.stdout if capture else b""


def git(root: Path, *arguments: str) -> str:
    return checked(["git", *arguments], root, capture=True).decode("utf-8").strip()


def clean_source(root: Path, commit: str) -> None:
    digest(commit, "source commit", COMMIT)
    require(git(root, "rev-parse", "--verify", "HEAD") == commit, "Checkout is not the exact source commit.")
    require(not git(root, "status", "--porcelain", "--untracked-files=all"), "Release requires a clean source worktree.")


def github_context(environment: dict[str, str], workflow: str, event: str, ref: str) -> None:
    require(environment.get("GITHUB_ACTIONS") == "true", "Release operations require GitHub Actions.")
    require(
        environment.get("GITHUB_SERVER_URL") == "https://github.com"
        and environment.get("GITHUB_REPOSITORY") == REPOSITORY
        and environment.get("GITHUB_EVENT_NAME") == event
        and environment.get("GITHUB_REF") == ref
        and environment.get("GITHUB_WORKFLOW_REF") == f"{REPOSITORY}/{workflow}@{ref}"
        and environment.get("GITHUB_WORKFLOW_SHA") == environment.get("GITHUB_SHA"),
        "Untrusted repository, event, ref or workflow source.",
    )
    digest(environment.get("GITHUB_SHA"), "workflow source commit", COMMIT)


def build_identity(root: Path, environment: dict[str, str]) -> dict[str, Any]:
    ref = environment.get("GITHUB_REF", "")
    require(ref.startswith("refs/tags/v"), "Build requires an explicit v-prefixed tag.")
    requested = version(ref.removeprefix("refs/tags/v"))
    github_context(environment, WORKFLOW, "push", ref)
    event = json_bytes(read_bytes(Path(environment["GITHUB_EVENT_PATH"]), MAX_JSON))
    require(
        isinstance(event, dict) and event.get("ref") == ref
        and event.get("created") is True and event.get("deleted") is False
        and event.get("forced") is False,
        "Release requires a new, non-forced tag push, not a deletion or retag.",
    )
    commit = environment["GITHUB_SHA"]
    clean_source(root, commit)
    require(git(root, "rev-parse", "--verify", "--end-of-options", f"{ref}^{{commit}}") == commit, "Tag does not resolve to the checked-out commit.")
    return identity_document(
        requested, int(environment["GITHUB_REPOSITORY_ID"]), commit,
        git(root, "rev-parse", "--verify", "--end-of-options", ref),
        int(environment["GITHUB_RUN_ID"]), int(environment["GITHUB_RUN_ATTEMPT"]),
    )


def zip_members(archive: zipfile.ZipFile, *, flat: bool) -> dict[str, zipfile.ZipInfo]:
    entries = archive.infolist()
    require(0 < len(entries) <= MAX_FILES, "ZIP entry count is empty or exceeds the limit.")
    names: set[str] = set()
    files: dict[str, zipfile.ZipInfo] = {}
    total = 0
    for entry in entries:
        require(entry.orig_filename == entry.filename, "NUL-terminated ZIP name is forbidden.")
        directory = entry.is_dir()
        name = portable_path(entry.filename[:-1] if directory else entry.filename, flat=flat)
        require(not flat or not directory, "Release artifact must contain only flat files.")
        require(name.casefold() not in names, "Duplicate or case-colliding ZIP member.")
        names.add(name.casefold())
        mode = stat.S_IFMT(entry.external_attr >> 16)
        require(
            mode in (0, stat.S_IFDIR if directory else stat.S_IFREG)
            and not entry.external_attr & 0x400,
            "ZIP links, reparse points and special files are forbidden.",
        )
        require(not entry.flag_bits & 1 and entry.compress_type in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED), "Encrypted or unsupported ZIP member.")
        require(0 <= entry.file_size <= MAX_FILE, "ZIP member byte limit exceeded.")
        total += entry.file_size
        require(total <= MAX_ARCHIVE, "ZIP expanded byte limit exceeded.")
        if not directory:
            files[name] = entry
    return files


def one_child(element: ET.Element, name: str) -> ET.Element:
    children = [child for child in element if child.tag.rsplit("}", 1)[-1] == name]
    require(len(children) == 1, f"NuGet metadata must contain exactly one {name}.")
    prefix = element.tag.rsplit("}", 1)[0] + "}" if "}" in element.tag else ""
    require(children[0].tag == prefix + name, f"NuGet {name} uses a different XML namespace.")
    return children[0]


def verify_package(path: Path, package_id: str, identity: dict[str, Any]) -> None:
    with zipfile.ZipFile(path) as package:
        entries = zip_members(package, flat=False)
        nuspecs = [name for name in entries if name.casefold().endswith(".nuspec")]
        require(len(nuspecs) == 1 and "/" not in nuspecs[0], "Package must have exactly one root nuspec.")
        require(entries[nuspecs[0]].file_size <= MAX_JSON, "Nuspec byte limit exceeded.")
        raw = package.read(nuspecs[0]).decode("utf-8-sig")
        require("\x00" not in raw and not re.search(r"<!\s*(?:DOCTYPE|ENTITY)", raw, re.I), "DTD/entity declarations are forbidden in nuspecs.")
        document = ET.fromstring(raw)
        require(
            document.tag == "package" or document.tag == "{http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd}package",
            "Invalid nuspec root or unsupported namespace.",
        )
        metadata = one_child(document, "metadata")
        for field, expected in (("id", package_id), ("version", identity["version"])):
            node = one_child(metadata, field)
            require(len(node) == 0 and node.text == expected, f"Package {field} mismatch: {path.name}")
        repository = one_child(metadata, "repository")
        require(
            repository.get("type") == "git" and repository.get("url") == REPOSITORY_URL
            and repository.get("commit") == identity["commit"]
            and repository.get("branch") == f"refs/tags/{identity['tag']}",
            f"Package source/tag identity mismatch: {path.name}",
        )
        suffix = "pdb" if path.suffix == ".snupkg" else "dll"
        frameworks = {name.split("/")[1] for name in entries if name.startswith("lib/") and len(name.split("/")) >= 3}
        require(frameworks == inventory.FRAMEWORKS, f"Package framework inventory mismatch: {path.name}")
        for framework in inventory.FRAMEWORKS:
            require(f"lib/{framework}/{package_id}.{suffix}" in entries, f"Package is missing its {framework} {suffix}: {path.name}")
        for entry in entries.values():
            with package.open(entry) as stream:
                while stream.read(1024 * 1024):
                    pass


def report_references(ledger: Any, *, alpha: bool = False) -> dict[str, str]:
    require(
        isinstance(ledger, dict) and type(ledger.get("schemaVersion")) is int
        and ledger["schemaVersion"] == 1
        and (ledger.get("semanticCoverageReviewed") is True or alpha),
        "Semantic coverage review is incomplete.",
    )
    rows = ledger.get("requirements")
    require(isinstance(rows, list) and bool(rows), "Requirement inventory is incomplete.")
    identifiers: set[str] = set()
    reports: dict[str, str] = {}
    aliases: dict[str, str] = {}
    applicable = 0
    for row in rows:
        require(isinstance(row, dict) and isinstance(row.get("id"), str) and bool(row["id"]), "Invalid requirement ID.")
        require(row["id"] not in identifiers, "Duplicate requirement ID.")
        identifiers.add(row["id"])
        specification.validate_review(row)
        if row["review"]["status"] == "informative":
            continue
        applicable += 1
        if alpha:
            # Explicit maintainer-approved alpha prerelease exception (docs/releasing.md):
            # requirement structure is still validated above, but reviewed/qualified status
            # and the exact eight-cell native evidence requirement are not enforced.
            evidence = row["implementation"]["nativeEvidence"]
        else:
            require(row["review"]["status"] == "reviewed" and row["implementation"]["status"] == "qualified", "Requirement is not qualified.")
            evidence = row["implementation"]["nativeEvidence"]
            require(len(evidence) == 8, "Each applicable requirement needs exactly eight native cells.")
        cells: set[tuple[str, str]] = set()
        for item in evidence:
            cell = (item["framework"], item["rid"])
            require(cell not in cells, "Duplicate native cell.")
            cells.add(cell)
            path = portable_path(item["report"].replace("\\", "/"))
            hashed = digest(item["sha256"], "native report hash")
            require(path not in reports or reports[path] == hashed, "Conflicting native report hashes.")
            require(path.casefold() not in aliases or aliases[path.casefold()] == path, "Case-colliding native report paths.")
            reports[path] = hashed
            aliases[path.casefold()] = path
    require(applicable > 0, "No applicable requirements were qualified.")
    require(len(reports) <= MAX_FILES - 27, "Native report count exceeds the artifact limit.")
    return reports


def verify_evidence(payload: Path, alpha: bool) -> set[str]:
    ledger = json_bytes(read_bytes(payload / "source-requirements.json", MAX_JSON))
    reports = report_references(ledger, alpha=alpha)
    lock = json_bytes(read_bytes(payload / "source-specification-lock.json", MAX_JSON))
    digest(ledger.get("baselineManifestSha256"), "specification baseline hash")
    require(
        isinstance(lock, dict) and ledger.get("baselineManifestSha256") == lock.get("baselineManifestSha256")
        and ledger.get("correctionsSha256") == file_hash(payload / "source-corrections.json"),
        "Specification baseline/correction evidence mismatch.",
    )
    with tempfile.TemporaryDirectory(prefix="xregistry-evidence-") as directory:
        root = Path(directory)
        for relative, hashed in reports.items():
            raw = read_bytes(payload / f"native-{hashed}.json", MAX_JSON)
            require(sha256(raw) == hashed, "Native report bytes changed.")
            target = source_path(root, relative)
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open("xb") as stream:
                stream.write(raw)
        specification.release_check(root, ledger, alpha=alpha)
    return {f"native-{hashed}.json" for hashed in reports.values()}


def package_names(ids: tuple[str, ...], requested: str) -> set[str]:
    return {f"{name}.{requested}.{suffix}" for name in ids for suffix in ("nupkg", "snupkg")}


def manifest_files(document: Any, identity: dict[str, Any]) -> dict[str, dict[str, Any]]:
    keys(document, {"schemaVersion", "identity", "qualification", "files"}, "release manifest")
    require(type(document["schemaVersion"]) is int and document["schemaVersion"] == 1, "Unsupported release manifest schema.")
    validate_identity(document["identity"])
    require(document["identity"] == identity, "Release manifest source/tag/run identity mismatch.")
    require(document["qualification"] == GATES, "Release qualification gates are missing or different.")
    require(isinstance(document["files"], list) and 0 < len(document["files"]) < MAX_FILES, "Invalid release file inventory.")
    result: dict[str, dict[str, Any]] = {}
    aliases: set[str] = set()
    for entry in document["files"]:
        keys(entry, {"name", "size", "sha256"}, "release file")
        name = portable_path(entry["name"], flat=True)
        require(name.casefold() != MANIFEST.casefold() and name.casefold() not in aliases, "Duplicate release file inventory.")
        require(positive(entry["size"], "file size") <= MAX_FILE, "Release file byte limit exceeded.")
        digest(entry["sha256"], "release file hash")
        aliases.add(name.casefold())
        result[name] = entry
    require(sum(entry["size"] for entry in result.values()) <= MAX_ARCHIVE, "Release expanded byte limit exceeded.")
    return result


def verify_payload(
    payload: Path, identity: dict[str, Any], source_hashes: dict[str, str], expected_ids: tuple[str, ...]
) -> dict[str, Any]:
    no_link(payload)
    document = json_bytes(read_bytes(payload / MANIFEST, MAX_JSON))
    files = manifest_files(document, identity)
    require({path.name for path in payload.iterdir()} == set(files) | {MANIFEST}, "Missing or extra release files.")
    keys(source_hashes, set(SOURCE_FILES), "source hashes")
    for name, entry in files.items():
        path = payload / name
        regular_file(path)
        require(path.stat().st_size == entry["size"] and file_hash(path) == entry["sha256"], f"Changed release bytes: {name}")
    for name, hashed in source_hashes.items():
        digest(hashed, "source hash")
        require(name in files and files[name]["sha256"] == hashed, f"Source file is not from the exact tag commit: {name}")
    alpha = is_alpha_prerelease(identity["version"])
    ids = package_ids(payload / "source-packages.json", payload, qualified=not alpha)
    require(ids == expected_ids, "Tagged package IDs differ from the trusted promotion inventory.")
    expected = set(SOURCE_FILES) | package_names(ids, identity["version"]) | verify_evidence(payload, alpha)
    require(set(files) == expected, "Incomplete or unexpected package/evidence inventory.")
    for package_id in ids:
        for suffix in ("nupkg", "snupkg"):
            verify_package(payload / f"{package_id}.{identity['version']}.{suffix}", package_id, identity)
    return document


def stage_payload(root: Path, packages: Path, destination: Path, identity: dict[str, Any]) -> None:
    alpha = is_alpha_prerelease(identity["version"])
    ids = package_ids(root / "eng" / "packages.json", root, qualified=not alpha)
    require({path.name for path in packages.iterdir()} == package_names(ids, identity["version"]), "Missing, extra or duplicate package output.")
    require(not destination.exists(), "Release payload destination already exists.")
    no_link(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="release-stage-", dir=destination.parent) as directory:
        payload = Path(directory) / "payload"
        payload.mkdir()
        hashes = {}
        for name, relative in SOURCE_FILES.items():
            raw = read_bytes(source_path(root, relative), MAX_JSON)
            (payload / name).write_bytes(raw)
            hashes[name] = sha256(raw)
        ledger = json_bytes(read_bytes(payload / "source-requirements.json", MAX_JSON))
        for relative, hashed in report_references(ledger, alpha=alpha).items():
            raw = read_bytes(source_path(root, relative), MAX_JSON)
            require(sha256(raw) == hashed, "Native report is missing or changed.")
            (payload / f"native-{hashed}.json").write_bytes(raw)
        for name in sorted(package_names(ids, identity["version"])):
            regular_file(packages / name)
            require((packages / name).stat().st_size <= MAX_FILE, "Package byte limit exceeded.")
            shutil.copyfile(packages / name, payload / name)
        files = [
            {"name": path.name, "size": path.stat().st_size, "sha256": file_hash(path)}
            for path in sorted(payload.iterdir())
        ]
        (payload / MANIFEST).write_bytes(encoded({
            "schemaVersion": 1, "identity": identity, "qualification": GATES, "files": files,
        }))
        verify_payload(payload, identity, hashes, ids)
        payload.rename(destination)


def extract_verified(archive_path: Path, destination: Path, selection: dict[str, Any], ids: tuple[str, ...]) -> None:
    validate_selection(selection)
    require(not destination.exists(), "Extraction destination already exists.")
    no_link(destination)
    regular_file(archive_path)
    require(
        archive_path.stat().st_size == selection["artifact"]["size"]
        and file_hash(archive_path) == selection["artifact"]["sha256"],
        "Artifact archive size/hash mismatch.",
    )
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive_path) as archive:
        entries = zip_members(archive, flat=True)
        require(MANIFEST in entries and entries[MANIFEST].file_size <= MAX_JSON, "Authoritative manifest is missing or too large.")
        document = json_bytes(archive.read(MANIFEST))
        files = manifest_files(document, selection["identity"])
        require(set(entries) == set(files) | {MANIFEST}, "Missing or extra archive members.")
        for name, entry in files.items():
            require(entries[name].file_size == entry["size"], "ZIP/manifest size mismatch.")
        with tempfile.TemporaryDirectory(prefix="release-extract-", dir=destination.parent) as directory:
            payload = Path(directory) / "payload"
            payload.mkdir()
            for name, entry in entries.items():
                with archive.open(entry) as source, (payload / name).open("xb") as target:
                    shutil.copyfileobj(source, target, 1024 * 1024)
            verify_payload(payload, selection["identity"], selection["sourceSha256"], ids)
            payload.rename(destination)


class GitHub:
    def command(self, endpoint: str, accept: str = "application/vnd.github+json") -> list[str]:
        require(endpoint.startswith(f"repos/{REPOSITORY}/") or endpoint == f"repos/{REPOSITORY}", "Unexpected GitHub API endpoint.")
        return [
            "gh", "api", "--hostname", "github.com", "--method", "GET",
            "-H", f"Accept: {accept}", "-H", "X-GitHub-Api-Version: 2022-11-28", endpoint,
        ]

    def get(self, endpoint: str) -> Any:
        return json_bytes(checked(self.command(endpoint), capture=True))

    def source(self, commit: str, relative: str) -> bytes:
        digest(commit, "source commit", COMMIT)
        require(relative in SOURCE_FILES.values(), "Unexpected source file request.")
        raw = checked(self.command(
            f"repos/{REPOSITORY}/contents/{relative}?ref={commit}", "application/vnd.github.raw+json"
        ), capture=True)
        require(0 < len(raw) <= MAX_JSON, "GitHub source file byte limit exceeded.")
        return raw

    def pages(self, endpoint: str, collection: str) -> list[dict[str, Any]]:
        result: list[dict[str, Any]] = []
        total = None
        page = 1
        while True:
            separator = "&" if "?" in endpoint else "?"
            document = self.get(f"{endpoint}{separator}per_page=100&page={page}")
            require(isinstance(document, dict), "Malformed GitHub page.")
            count, entries = document.get("total_count"), document.get(collection)
            require(type(count) is int and 0 <= count <= MAX_RESULTS, "GitHub result limit exceeded; selection is ambiguous.")
            require(isinstance(entries, list) and all(isinstance(item, dict) for item in entries), "Malformed GitHub result collection.")
            require(total is None or total == count, "GitHub result count changed during pagination.")
            total = count
            result.extend(entries)
            require(len(result) <= total and len(entries) <= 100, "Inconsistent GitHub pagination.")
            if len(result) == total:
                ids = [positive(item.get("id"), "GitHub result ID") for item in result]
                require(len(set(ids)) == len(ids), "Duplicate GitHub results.")
                return result
            require(len(entries) == 100, "Incomplete GitHub pagination.")
            page += 1

    def download(self, selection: dict[str, Any], destination: Path) -> None:
        validate_selection(selection)
        no_link(destination)
        endpoint = f"repos/{REPOSITORY}/actions/artifacts/{selection['artifact']['id']}/zip"
        with destination.open("xb") as stream:
            try:
                result = subprocess.run(self.command(endpoint), stdout=stream, stderr=subprocess.PIPE, timeout=180, check=False)
            except subprocess.TimeoutExpired as error:
                raise ReleaseError("Artifact download timed out.") from error
        require(result.returncode == 0, "Artifact download failed.")
        require(destination.stat().st_size == selection["artifact"]["size"], "Downloaded artifact size mismatch.")


def resolve_tag(api: GitHub, requested: str) -> tuple[str, str]:
    tag = f"v{version(requested)}"
    document = api.get(f"repos/{REPOSITORY}/git/ref/tags/{quote(tag, safe='')}")
    require(isinstance(document, dict) and document.get("ref") == f"refs/tags/{tag}", "Exact release tag was not found.")
    target = document.get("object")
    seen: set[str] = set()
    original = None
    for _ in range(8):
        require(isinstance(target, dict), "Invalid tag target.")
        hashed = digest(target.get("sha"), "tag target", COMMIT)
        require(hashed not in seen, "Annotated tag cycle.")
        seen.add(hashed)
        if original is None:
            original = hashed
        if target.get("type") == "commit":
            return hashed, original
        require(target.get("type") == "tag", "Tag must resolve to a commit, not a tree or blob.")
        document = api.get(f"repos/{REPOSITORY}/git/tags/{hashed}")
        require(isinstance(document, dict) and document.get("sha") == hashed, "Annotated tag identity mismatch.")
        target = document.get("object")
    raise ReleaseError("Annotated tag nesting limit exceeded.")


def resolve(api: GitHub, requested: str) -> dict[str, Any]:
    requested = version(requested)
    repository = api.get(f"repos/{REPOSITORY}")
    require(isinstance(repository, dict) and repository.get("full_name") == REPOSITORY and repository.get("fork") is False, "Wrong release repository.")
    repository_id = positive(repository.get("id"), "repository ID")
    commit, tag_object = resolve_tag(api, requested)
    workflow = api.get(f"repos/{REPOSITORY}/actions/workflows/release.yml")
    require(isinstance(workflow, dict) and workflow.get("path") == WORKFLOW and workflow.get("state") == "active", "Release workflow is absent, changed or inactive.")
    workflow_id = positive(workflow.get("id"), "workflow ID")
    runs = api.pages(
        f"repos/{REPOSITORY}/actions/workflows/{workflow_id}/runs?event=push&status=success&head_sha={commit}&exclude_pull_requests=true",
        "workflow_runs",
    )
    candidates = [run for run in runs if run.get("head_branch") == f"v{requested}"]
    require(len(candidates) == 1, "Expected exactly one successful release run for the exact tag/commit; none or multiple were found.")
    run = candidates[0]
    require(
        run.get("event") == "push" and run.get("status") == "completed" and run.get("conclusion") == "success"
        and run.get("head_sha") == commit and run.get("path") == WORKFLOW
        and run.get("workflow_id") == workflow_id and run.get("pull_requests") == [],
        "Run is not a successful qualifying tag release.",
    )
    for field in ("repository", "head_repository"):
        origin = run.get(field)
        require(isinstance(origin, dict) and origin.get("id") == repository_id and origin.get("full_name") == REPOSITORY, "Run came from the wrong source repository.")
    identity = identity_document(requested, repository_id, commit, tag_object, run.get("id"), run.get("run_attempt"))
    artifacts = api.pages(f"repos/{REPOSITORY}/actions/runs/{identity['runId']}/artifacts", "artifacts")
    matches = [artifact for artifact in artifacts if artifact.get("name") == artifact_name(identity)]
    require(len(matches) == 1, "Expected exactly one release artifact for this run attempt.")
    artifact = matches[0]
    origin = artifact.get("workflow_run")
    require(
        artifact.get("expired") is False and isinstance(origin, dict)
        and origin.get("id") == identity["runId"] and origin.get("head_sha") == commit
        and origin.get("head_branch") == identity["tag"]
        and origin.get("repository_id") == repository_id and origin.get("head_repository_id") == repository_id,
        "Artifact is expired or belongs to another source/run.",
    )
    hashed = artifact.get("digest")
    require(isinstance(hashed, str) and hashed.startswith("sha256:"), "GitHub artifact SHA-256 is required.")
    selection = {
        "identity": identity, "workflowId": workflow_id,
        "artifact": {"id": artifact.get("id"), "name": artifact.get("name"), "size": artifact.get("size_in_bytes"), "sha256": hashed.removeprefix("sha256:")},
        "sourceSha256": {name: sha256(api.source(commit, path)) for name, path in SOURCE_FILES.items()},
    }
    validate_selection(selection)
    return selection


def require_approval(api: GitHub, environment: dict[str, str]) -> None:
    require(environment.get("GITHUB_RUN_ATTEMPT") == "1", "Promotion reruns are forbidden; a fresh dispatch and approval are required.")
    run_id = positive(int(environment["GITHUB_RUN_ID"]), "promotion run ID")
    reviews = api.get(f"repos/{REPOSITORY}/actions/runs/{run_id}/approvals")
    require(isinstance(reviews, list), "Malformed release approval history.")
    matches = []
    for review in reviews:
        require(isinstance(review, dict) and isinstance(review.get("environments"), list), "Malformed environment review.")
        if any(isinstance(item, dict) and item.get("name") == ENVIRONMENT for item in review["environments"]):
            matches.append(review)
    require(len(matches) == 1 and matches[0].get("state") == "approved", "An explicit, unambiguous release environment approval is required.")
    reviewer = matches[0].get("user")
    actors = {environment.get("GITHUB_ACTOR", "").casefold(), environment.get("GITHUB_TRIGGERING_ACTOR", "").casefold()}
    require("" not in actors, "Dispatch and triggering actor identities are required.")
    positive(int(environment["GITHUB_ACTOR_ID"]), "dispatch actor ID")
    require(
        isinstance(reviewer, dict) and reviewer.get("type") == "User"
        and isinstance(reviewer.get("login"), str) and bool(reviewer["login"])
        and reviewer["login"].casefold() == APPROVER_LOGIN,
        "Release approval must come from the designated human maintainer.",
    )
    require(positive(reviewer.get("id"), "reviewer ID") == APPROVER_ID, "The reviewer must match the designated maintainer's immutable GitHub ID.")


def verify_attestation(archive: Path, selection: dict[str, Any]) -> None:
    validate_selection(selection)
    identity = selection["identity"]
    require(file_hash(archive) == selection["artifact"]["sha256"], "Artifact archive hash mismatch.")
    checked([
        "gh", "attestation", "verify", str(archive), "--hostname", "github.com",
        "--repo", REPOSITORY, "--deny-self-hosted-runners",
        "--cert-identity", f"{REPOSITORY_URL}/{WORKFLOW}@refs/tags/{identity['tag']}",
        "--signer-workflow", f"{REPOSITORY}/{WORKFLOW}",
        "--signer-digest", identity["commit"], "--source-digest", identity["commit"],
        "--source-ref", f"refs/tags/{identity['tag']}",
        "--predicate-type", "https://slsa.dev/provenance/v1",
    ], capture=True)


def emit_outputs(values: dict[str, str], environment: dict[str, str]) -> None:
    with Path(environment["GITHUB_OUTPUT"]).open("a", encoding="utf-8", newline="\n") as stream:
        for key, value in values.items():
            require(re.fullmatch(r"[a-z-]+", key) is not None and "\n" not in value and "\r" not in value, "Unsafe workflow output.")
            stream.write(f"{key}={value}\n")


def build(root: Path, environment: dict[str, str]) -> None:
    identity = build_identity(root, environment)
    alpha = is_alpha_prerelease(identity["version"])
    if alpha:
        print(
            "ALPHA PRERELEASE EXCEPTION: version "
            f"{identity['version']!r} is an explicit -alpha prerelease. Package/specification "
            "qualification and native-evidence enforcement are not required for this release; "
            "see docs/releasing.md#alpha-prerelease-exception. Build, test, hashing, attestation "
            "and approval requirements are unchanged.",
        )
    package_ids(root / "eng" / "packages.json", root, qualified=not alpha)
    checked(["python", str(root / "eng" / "check_packages.py"), "--release"], root, timeout=600)
    checked(["python", str(root / "eng" / "specification" / "manage.py"), "release"], root, timeout=600)
    checked(["pwsh", "-NoProfile", "-File", str(root / "eng" / "build.ps1")], root, timeout=1800)
    checked(["pwsh", "-NoProfile", "-File", str(root / "eng" / "test.ps1"), "-NoBuild"], root, timeout=1800)
    clean_source(root, identity["commit"])
    output = root / "artifacts" / "release" / "packages"
    no_link(output)
    output.mkdir(parents=True, exist_ok=False)
    projects = inventory.load_inventory(root / "eng" / "packages.json", root)
    for project in projects:
        if project.sample:
            continue
        checked([
            "dotnet", "pack", str(project.path), "-c", "Release", "--no-restore", "--nologo",
            "-o", str(output), "-v", "minimal",
            f"-p:PackageVersion={identity['version']}", f"-p:Version={identity['version']}",
            f"-p:RepositoryCommit={identity['commit']}", f"-p:RepositoryBranch=refs/tags/{identity['tag']}",
            "-p:ContinuousIntegrationBuild=true", "-p:PublicRelease=true",
        ], root, timeout=600)
    clean_source(root, identity["commit"])
    for relative in SOURCE_FILES.values():
        raw = checked(["git", "show", f"{identity['commit']}:{relative}"], root, capture=True)
        require(raw == read_bytes(source_path(root, relative), MAX_JSON), "Qualification source bytes are not the tagged commit.")
    stage_payload(root, output, root / "artifacts" / "release" / "payload", identity)
    emit_outputs({"artifact-name": artifact_name(identity)}, environment)


def promotion_context(root: Path, environment: dict[str, str]) -> str:
    requested = version(environment.get("RELEASE_VERSION"))
    github_context(environment, PROMOTION_WORKFLOW, "workflow_dispatch", "refs/heads/main")
    clean_source(root, environment["GITHUB_SHA"])
    return requested


def pinned_selection(environment: dict[str, str]) -> dict[str, Any]:
    raw = environment.get("EXPECTED_SELECTION", "")
    require(bool(raw), "The pre-approval artifact selection is required.")
    value = json_bytes(raw.encode("utf-8"))
    validate_selection(value)
    return value


def prepare(root: Path, environment: dict[str, str], *, approved: bool) -> None:
    requested = promotion_context(root, environment)
    api = GitHub()
    if approved:
        require_approval(api, environment)
        expected = pinned_selection(environment)
    selection = resolve(api, requested)
    if approved:
        require(selection == expected, "Tag/run/artifact/source changed after pre-approval selection.")
        require(bool(environment.get("NUGET_USER", "").strip()), "Configure the release environment NUGET_USER variable.")
    directory = Path(environment["RUNNER_TEMP"]) / "xregistry-release-promotion"
    no_link(directory)
    directory.mkdir(exist_ok=False)
    archive = directory / f"{selection['artifact']['name']}.zip"
    api.download(selection, archive)
    verify_attestation(archive, selection)
    ids = package_ids(root / "eng" / "packages.json", root, qualified=False)
    extract_verified(archive, directory / "payload", selection, ids)
    document = json_bytes(read_bytes(directory / "payload" / MANIFEST, MAX_JSON))
    emit_outputs({"selection": json.dumps(selection, ensure_ascii=True, sort_keys=True, separators=(",", ":"))}, environment)
    with Path(environment["GITHUB_STEP_SUMMARY"]).open("a", encoding="utf-8") as summary:
        identity = selection["identity"]
        summary.write(
            f"## Exact release selection\n\nVersion `{requested}`; tag `{identity['tag']}`; "
            f"commit `{identity['commit']}`; tag object `{identity['tagObject']}`.\n\n"
            f"Release run `{identity['runId']}`, attempt `{identity['runAttempt']}`; "
            f"artifact `{selection['artifact']['id']}`; ZIP SHA-256 `{selection['artifact']['sha256']}`.\n\n"
            "This selection does not publish or replace required environment approval.\n\n"
            "| File | SHA-256 |\n| --- | --- |\n"
        )
        for entry in document["files"]:
            if entry["name"].endswith((".nupkg", ".snupkg")):
                summary.write(f"| `{entry['name']}` | `{entry['sha256']}` |\n")


def push_packages(payload: Path, selection: dict[str, Any], ids: tuple[str, ...], api_key: str) -> None:
    validate_selection(selection)
    require(bool(api_key) and not any(char in api_key for char in "\r\n\x00"), "A short-lived NuGet login output is required.")
    config = ROOT / "eng" / "release" / "nuget.config"
    regular_file(config)
    document = verify_payload(payload, selection["identity"], selection["sourceSha256"], ids)
    files = {entry["name"]: entry for entry in document["files"]}
    for package_id in ids:
        for suffix in ("nupkg", "snupkg"):
            name = f"{package_id}.{selection['identity']['version']}.{suffix}"
            path = payload / name
            require(file_hash(path) == files[name]["sha256"], "Package changed immediately before NuGet push.")
            # Disable implicit symbol discovery; both verified files are submitted explicitly.
            arguments = [
                "dotnet", "nuget", "push", str(path), "--source", "https://api.nuget.org/v3/index.json",
                "--api-key", api_key, "--timeout", "300", "--force-english-output",
                "--configfile", str(config),
            ]
            if suffix == "nupkg":
                arguments.append("--no-symbols")
            try:
                result = subprocess.run(arguments, check=False, timeout=360, capture_output=True)
            except subprocess.TimeoutExpired as error:
                raise ReleaseError(f"NuGet push timed out for {name}; remote publication may be partial.") from error
            require(result.returncode == 0, f"NuGet push failed for {name} (exit {result.returncode}); remote publication may be partial. No duplicate/conflict was ignored.")


def push(root: Path, environment: dict[str, str]) -> None:
    requested = promotion_context(root, environment)
    selection = pinned_selection(environment)
    api = GitHub()
    require_approval(api, environment)
    require(resolve(api, requested) == selection, "Approved tag/run/artifact/source is no longer the exact selection.")
    directory = Path(environment["RUNNER_TEMP"]) / "xregistry-release-promotion"
    verify_attestation(directory / f"{selection['artifact']['name']}.zip", selection)
    ids = package_ids(root / "eng" / "packages.json", root, qualified=False)
    push_packages(directory / "payload", selection, ids, environment.get("NUGET_API_KEY", ""))
    print("Submitted the eleven verified package/symbol pairs to NuGet; server-side validation/indexing is separate.")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("build", "resolve", "prepare", "push"))
    args = parser.parse_args(argv)
    try:
        environment = dict(os.environ)
        if args.operation == "build":
            build(ROOT, environment)
        elif args.operation in ("resolve", "prepare"):
            prepare(ROOT, environment, approved=args.operation == "prepare")
        else:
            push(ROOT, environment)
        return 0
    except (ValueError, OSError, KeyError, zipfile.BadZipFile, ET.ParseError) as error:
        print(f"Release error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
