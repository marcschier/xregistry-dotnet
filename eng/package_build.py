#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Build exact-version CI artifacts without release qualification or publishing authority."""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("_package_build_release", ROOT / "eng" / "release" / "release.py")
assert spec is not None and spec.loader is not None
release = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = release
spec.loader.exec_module(release)


def source_version(root: Path, requested: str | None = None) -> str:
    declared = release.json_bytes((root / "version.json").read_bytes())
    current = release.version(declared.get("version"))
    selected = release.version(requested) if requested is not None else current
    release.require(selected == current, "An explicit package version must match version.json.")
    return selected


def verify_packages(output: Path, ids: tuple[str, ...], version: str, commit: str) -> list[dict]:
    expected = {f"{name}.{version}.{suffix}" for name in ids for suffix in ("nupkg", "snupkg")}
    actual = {p.name for p in output.iterdir() if p.is_file()}
    release.require(actual == expected, "The build must produce exactly the package/symbol pairs in the inventory.")
    entries = []
    for name in sorted(expected):
        path = output / name
        with zipfile.ZipFile(path) as archive:
            manifests = [p for p in archive.namelist() if p.endswith(".nuspec")]
            release.require(len(manifests) == 1, "Each NuGet archive needs one nuspec.")
            raw = archive.read(manifests[0])
            release.require(b"<!DOCTYPE" not in raw.upper() and b"<!ENTITY" not in raw.upper(), "Unsafe nuspec XML.")
            document = ET.fromstring(raw)
            metadata = next((node for node in document if node.tag.rsplit("}", 1)[-1] == "metadata"), None)
            release.require(metadata is not None, "Missing package metadata.")
            fields = {node.tag.rsplit("}", 1)[-1]: node for node in metadata}
            package_id = fields["id"].text
            release.require(package_id in ids and fields["version"].text == version, "Wrong package ID or version.")
            release.require(name == f"{package_id}.{version}.{path.suffix[1:]}", "Package filename/identity mismatch.")
            repository = fields["repository"]
            release.require(repository.get("commit") == commit and repository.get("url") == release.REPOSITORY_URL,
                            "Package source provenance does not match this checkout.")
            if path.suffix == ".nupkg":
                for framework in ("net8.0", "net10.0"):
                    release.require(any(p.startswith(f"lib/{framework}/") and p.endswith(".dll") for p in archive.namelist()),
                                    f"Missing {framework} library assets.")
        entries.append({"name": name, "bytes": path.stat().st_size, "sha256": release.file_hash(path)})
    return entries


def build(root: Path, output: Path, requested: str | None = None) -> dict:
    version = source_version(root, requested)
    commit = release.checked(["git", "rev-parse", "--verify", "HEAD"], root, capture=True).decode().strip()
    release.require(release.COMMIT.fullmatch(commit) is not None, "A package build needs a real source commit.")
    branch = os.environ.get("GITHUB_REF") or release.checked(
        ["git", "symbolic-ref", "--quiet", "HEAD"], root, capture=True).decode().strip()
    projects = [p for p in release.inventory.load_inventory(root / "eng" / "packages.json", root) if not p.sample]
    ids = tuple(p.name for p in projects)
    release.require(output.resolve().is_relative_to((root / "artifacts").resolve()), "Package output must stay under artifacts.")
    release.no_link(output)
    output.mkdir(parents=True, exist_ok=False)
    for project in projects:
        release.checked([
            "dotnet", "pack", str(project.path), "-c", "Release", "--no-restore", "--nologo", "-v", "minimal",
            "-o", str(output), f"-p:PackageVersion={version}", f"-p:Version={version}",
            f"-p:RepositoryCommit={commit}", f"-p:RepositoryBranch={branch}",
            "-p:PublicRelease=true", "-p:ContinuousIntegrationBuild=true",
        ], root, timeout=600)
    report = {
        "schemaVersion": 1, "kind": "ci-package-build", "releaseQualified": False,
        "version": version, "repository": release.REPOSITORY, "commit": commit, "ref": branch,
        "files": verify_packages(output, ids, version, commit),
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
