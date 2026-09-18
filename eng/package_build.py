#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Build exact-version CI artifacts without release qualification or publishing authority."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from typing import Any
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[1]
inventory_spec = importlib.util.spec_from_file_location(
    "_package_build_inventory", ROOT / "eng" / "check_packages.py"
)
assert inventory_spec is not None and inventory_spec.loader is not None
inventory = importlib.util.module_from_spec(inventory_spec)
sys.modules[inventory_spec.name] = inventory
inventory_spec.loader.exec_module(inventory)

REPOSITORY = "marcschier/xregistry-dotnet"
REPOSITORY_URL = f"https://github.com/{REPOSITORY}"
COMMIT = re.compile(r"[0-9a-f]{40}")
VERSION = re.compile(
    r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-([0-9a-z-]+(?:\.[0-9a-z-]+)*))?"
)
MAX_JSON = 32 * 1024 * 1024


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


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


def json_bytes(raw: bytes) -> Any:
    require(len(raw) <= MAX_JSON, "JSON byte limit exceeded.")
    return json.loads(
        raw.decode("utf-8"),
        object_pairs_hook=inventory.unique_object,
        parse_constant=inventory.invalid_constant,
    )


def no_link(path: Path) -> None:
    for part in (path, *path.parents):
        junction = getattr(part, "is_junction", None)
        require(not part.is_symlink() and not (junction is not None and junction()), f"Links are forbidden: {path.name}")


def file_hash(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def checked(command: list[str], root: Path = ROOT, *, capture: bool = False, timeout: int = 120) -> bytes:
    try:
        result = subprocess.run(
            command, cwd=root, check=False, capture_output=capture, timeout=timeout,
        )
    except subprocess.TimeoutExpired as error:
        raise ValueError(f"{command[0]} timed out.") from error
    require(result.returncode == 0, f"{command[0]} failed (exit {result.returncode}).")
    return result.stdout if capture else b""


def source_version(root: Path, requested: str | None = None) -> str:
    declared = json_bytes((root / "version.json").read_bytes())
    current = version(declared.get("version"))
    selected = version(requested) if requested is not None else current
    require(selected == current, "An explicit package version must match version.json.")
    return selected


def verify_packages(output: Path, ids: tuple[str, ...], build_version: str, commit: str) -> list[dict]:
    expected = {f"{name}.{build_version}.{suffix}" for name in ids for suffix in ("nupkg", "snupkg")}
    actual = {p.name for p in output.iterdir() if p.is_file()}
    require(actual == expected, "The build must produce exactly the package/symbol pairs in the inventory.")
    entries = []
    for name in sorted(expected):
        path = output / name
        with zipfile.ZipFile(path) as archive:
            manifests = [p for p in archive.namelist() if p.endswith(".nuspec")]
            require(len(manifests) == 1, "Each NuGet archive needs one nuspec.")
            raw = archive.read(manifests[0])
            require(b"<!DOCTYPE" not in raw.upper() and b"<!ENTITY" not in raw.upper(), "Unsafe nuspec XML.")
            document = ET.fromstring(raw)
            metadata = next((node for node in document if node.tag.rsplit("}", 1)[-1] == "metadata"), None)
            require(metadata is not None, "Missing package metadata.")
            fields = {node.tag.rsplit("}", 1)[-1]: node for node in metadata}
            package_id = fields["id"].text
            require(package_id in ids and fields["version"].text == build_version, "Wrong package ID or version.")
            require(name == f"{package_id}.{build_version}.{path.suffix[1:]}", "Package filename/identity mismatch.")
            repository = fields["repository"]
            require(repository.get("commit") == commit and repository.get("url") == REPOSITORY_URL,
                    "Package source provenance does not match this checkout.")
            if path.suffix == ".nupkg":
                for framework in ("net8.0", "net10.0"):
                    require(any(p.startswith(f"lib/{framework}/") and p.endswith(".dll") for p in archive.namelist()),
                            f"Missing {framework} library assets.")
        entries.append({"name": name, "bytes": path.stat().st_size, "sha256": file_hash(path)})
    return entries


def build(root: Path, output: Path, requested: str | None = None) -> dict:
    build_version = source_version(root, requested)
    commit = checked(["git", "rev-parse", "--verify", "HEAD"], root, capture=True).decode().strip()
    require(COMMIT.fullmatch(commit) is not None, "A package build needs a real source commit.")
    branch = os.environ.get("GITHUB_REF") or checked(
        ["git", "symbolic-ref", "--quiet", "HEAD"], root, capture=True).decode().strip()
    projects = [p for p in inventory.load_inventory(root / "eng" / "packages.json", root) if not p.sample]
    ids = tuple(p.name for p in projects)
    require(output.resolve().is_relative_to((root / "artifacts").resolve()), "Package output must stay under artifacts.")
    no_link(output)
    output.mkdir(parents=True, exist_ok=False)
    for project in projects:
        checked([
            "dotnet", "pack", str(project.path), "-c", "Release", "--no-restore", "--nologo", "-v", "minimal",
            "-o", str(output), f"-p:PackageVersion={build_version}", f"-p:Version={build_version}",
            f"-p:RepositoryCommit={commit}", f"-p:RepositoryBranch={branch}",
            "-p:PublicRelease=true", "-p:ContinuousIntegrationBuild=true",
        ], root, timeout=600)
    report = {
        "schemaVersion": 1, "kind": "ci-package-build", "releaseQualified": False,
        "version": build_version, "repository": REPOSITORY, "commit": commit, "ref": branch,
        "files": verify_packages(output, ids, build_version, commit),
    }
    (output / "package-build.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts" / "ci-packages")
    args = parser.parse_args()
    try:
        result = build(ROOT, args.output, args.version)
    except (ValueError, OSError, KeyError, zipfile.BadZipFile, ET.ParseError, subprocess.SubprocessError) as error:
        print(f"Package build failed: {error}", file=sys.stderr)
        return 1
    print(f"Built {len(result['files']) // 2} exact-version package/symbol pairs at {args.output}; no publication or release qualification.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
