#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Validate the package inventory; this does not certify runtime conformance."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from typing import Any


ROOT = Path(__file__).resolve().parent.parent
FRAMEWORKS = frozenset(("net8.0", "net10.0"))
RIDS = frozenset(("win-x64", "win-arm64", "linux-x64", "linux-arm64"))
SAMPLES = frozenset(
    ("XRegistry.FederationBridge", "XRegistry.FileServer", "XRegistry.Client")
)
STATUSES = frozenset(("planned", "implemented", "qualified"))
MAX_MANIFEST_BYTES = 1024 * 1024
MAX_PROJECTS = 256
ID_PATTERN = re.compile(r"XRegistry(?:[.-][A-Za-z0-9]+)*")
RESERVED_NAME = re.compile(r"(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", re.I)
PROPERTIES = (
    "PackageId",
    "TargetFramework",
    "TargetFrameworks",
    "IsPackable",
    "IsAotCompatible",
    "EnableAotAnalyzer",
    "EnableTrimAnalyzer",
    "PublishAot",
)


class InventoryError(ValueError):
    """A package inventory or evaluated project violates the repository contract."""


@dataclass(frozen=True)
class Project:
    name: str
    path: Path
    status: str
    sample: bool


def unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise InventoryError(f"Duplicate JSON property: {key}")
        result[key] = value
    return result


def invalid_constant(value: str) -> None:
    raise InventoryError(f"Non-JSON numeric constant: {value}")


def require_keys(value: Any, expected: set[str], label: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise InventoryError(f"{label} must be an object.")
    missing = expected - value.keys()
    extra = value.keys() - expected
    if missing or extra:
        raise InventoryError(
            f"{label} has missing keys {sorted(missing)} or unknown keys {sorted(extra)}."
        )
    return value


def string_set(value: Any, expected: frozenset[str], label: str) -> None:
    if (
        not isinstance(value, list)
        or any(not isinstance(item, str) for item in value)
        or len(value) != len(expected)
        or frozenset(value) != expected
    ):
        raise InventoryError(f"{label} must contain exactly {sorted(expected)}.")


def safe_project_path(root: Path, value: Any, name: str, sample: bool) -> Path:
    if not isinstance(value, str):
        raise InventoryError(f"Project path for {name} must be a string.")
    parts = value.replace("\\", "/").split("/")
    expected = ["samples" if sample else "src", name, f"{name}.csproj"]
    if parts != expected:
        raise InventoryError(
            f"Project path for {name} must be the canonical relative path "
            f"{os.sep.join(expected)}."
        )
    for part in parts:
        if (
            part in ("", ".", "..")
            or part.endswith((".", " "))
            or len(part) > 255
            or re.search(r'[<>:"|?*\x00-\x1f]', part)
            or RESERVED_NAME.match(part)
        ):
            raise InventoryError(f"Unsafe project path component for {name}.")
    current = root
    for part in parts:
        current = current / part
        if current.is_symlink():
            raise InventoryError(f"Project path must not traverse a symlink: {value}")
        is_junction = getattr(current, "is_junction", None)
        if is_junction is not None and is_junction():
            raise InventoryError(f"Project path must not traverse a junction: {value}")
    if not current.resolve().is_relative_to(root.resolve()):
        raise InventoryError(f"Project path escapes the repository: {value}")
    return current


def load_inventory(path: Path, root: Path = ROOT) -> tuple[Project, ...]:
    with path.open("rb") as stream:
        raw = stream.read(MAX_MANIFEST_BYTES + 1)
    if len(raw) > MAX_MANIFEST_BYTES:
        raise InventoryError("Package manifest exceeds the byte limit.")
    document = json.loads(
        raw.decode("utf-8"),
        object_pairs_hook=unique_object,
        parse_constant=invalid_constant,
    )
    require_keys(
        document,
        {
            "schemaVersion",
            "repository",
            "targetFrameworks",
            "nativeRuntimeIdentifiers",
            "packages",
            "samples",
        },
        "Package manifest",
    )
    if type(document["schemaVersion"]) is not int or document["schemaVersion"] != 1:
        raise InventoryError("Unsupported package manifest schemaVersion.")
    if document["repository"] != "https://github.com/marcschier/xregistry-dotnet":
        raise InventoryError("Repository must identify marcschier/xregistry-dotnet.")
    string_set(document["targetFrameworks"], FRAMEWORKS, "Target frameworks")
    string_set(document["nativeRuntimeIdentifiers"], RIDS, "Native runtime identifiers")

    projects: list[Project] = []
    names: dict[bool, set[str]] = {False: set(), True: set()}
    seen_paths: set[str] = set()
    for collection, sample in (("packages", False), ("samples", True)):
        entries = document[collection]
        if not isinstance(entries, list) or not entries or len(entries) > MAX_PROJECTS:
            raise InventoryError(
                f"{collection} must contain between 1 and {MAX_PROJECTS} entries."
            )
        for entry in entries:
            name_key = "name" if sample else "id"
            keys = {name_key, "project", "status"}
            if sample:
                keys.add("targetFramework")
            require_keys(entry, keys, f"{collection} entry")
            name = entry[name_key]
            if not isinstance(name, str) or not ID_PATTERN.fullmatch(name):
                raise InventoryError(f"Invalid {name_key} in {collection}.")
            if name.casefold() in names[sample]:
                raise InventoryError(f"Duplicate {collection} identity: {name}")
            names[sample].add(name.casefold())
            status = entry["status"]
            if not isinstance(status, str) or status not in STATUSES:
                raise InventoryError(f"Invalid implementation status for {name}.")
            if sample and entry["targetFramework"] != "net10.0":
                raise InventoryError(f"Sample {name} must target net10.0.")
            project_path = safe_project_path(root, entry["project"], name, sample)
            path_key = str(project_path).casefold()
            if path_key in seen_paths:
                raise InventoryError(f"Duplicate project path: {entry['project']}")
            seen_paths.add(path_key)
            projects.append(Project(name, project_path, status, sample))
    if "xregistry" not in names[False]:
        raise InventoryError("The standalone XRegistry core package is required.")
    if names[True] != {name.casefold() for name in SAMPLES}:
        raise InventoryError(f"The principal samples must be exactly {sorted(SAMPLES)}.")
    return tuple(projects)


def check_project_inventory(projects: tuple[Project, ...], root: Path = ROOT) -> None:
    declared = {str(project.path.resolve()).casefold() for project in projects}
    for project in projects:
        if not project.path.is_file() and not (project.sample and project.status == "planned"):
            raise InventoryError(f"Declared project is missing: {project.path}")
    for folder in ("src", "samples"):
        directory = root / folder
        if not directory.is_dir():
            continue
        for candidate in directory.rglob("*.csproj"):
            relative = candidate.relative_to(root)
            if any(part.casefold() in {"bin", "obj"} for part in relative.parts[:-1]):
                continue
            if str(candidate.resolve()).casefold() not in declared:
                raise InventoryError(f"Unclassified project: {relative}")


def evaluate_project(
    project: Project, framework: str | None, root: Path = ROOT
) -> dict[str, str]:
    environment = os.environ.copy()
    environment["DOTNET_NOLOGO"] = "1"
    environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    result = subprocess.run(
        [
            "dotnet",
            "msbuild",
            str(project.path),
            "-nologo",
            *([f"-p:TargetFramework={framework}"] if framework is not None else []),
            f"-getProperty:{','.join(PROPERTIES)}",
        ],
        cwd=root,
        env=environment,
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=120,
    )
    if result.returncode:
        details = (result.stderr.strip() or result.stdout.strip())[:8192]
        raise InventoryError(
            f"MSBuild evaluation failed for {project.name} ({framework}): {details}"
        )
    output = json.loads(result.stdout, object_pairs_hook=unique_object)
    if not isinstance(output, dict) or not isinstance(output.get("Properties"), dict):
        raise InventoryError(f"MSBuild returned no property object for {project.name}.")
    properties = output["Properties"]
    if any(not isinstance(properties.get(key), str) for key in PROPERTIES):
        raise InventoryError(f"MSBuild returned incomplete properties for {project.name}.")
    return properties


def check_evaluated_projects(projects: tuple[Project, ...], root: Path = ROOT) -> int:
    evaluated = 0
    for project in projects:
        if project.sample and project.status == "planned" and not project.path.is_file():
            continue
        frameworks = (None,) if project.sample else tuple(sorted(FRAMEWORKS))
        for framework in frameworks:
            properties = evaluate_project(project, framework, root)
            expected_framework = "net10.0" if project.sample else framework
            if properties["TargetFramework"] != expected_framework:
                raise InventoryError(f"Unexpected evaluated target for {project.name}.")
            if project.sample:
                if properties["IsPackable"].casefold() != "false":
                    raise InventoryError(f"Sample {project.name} must not be packable.")
                if properties["TargetFrameworks"]:
                    raise InventoryError(f"Sample {project.name} must declare only net10.0.")
                if properties["PublishAot"].casefold() != "true":
                    raise InventoryError(f"Sample {project.name} must enable PublishAot.")
            else:
                if properties["PackageId"] != project.name:
                    raise InventoryError(f"PackageId mismatch for {project.name}.")
                if properties["IsPackable"].casefold() != "true":
                    raise InventoryError(f"Library {project.name} must be packable.")
                actual_frameworks = properties["TargetFrameworks"].split(";")
                string_set(actual_frameworks, FRAMEWORKS, f"{project.name} frameworks")
                for key in ("IsAotCompatible", "EnableAotAnalyzer", "EnableTrimAnalyzer"):
                    if properties[key].casefold() != "true":
                        raise InventoryError(f"{project.name} must enable {key}.")
            evaluated += 1
    return evaluated


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=ROOT / "eng" / "packages.json")
    parser.add_argument(
        "--check-projects", action="store_true", help="Check source/sample project coverage."
    )
    parser.add_argument(
        "--evaluate", action="store_true", help="Also evaluate target/AOT/pack properties."
    )
    parser.add_argument(
        "--release",
        action="store_true",
        help="Require qualified statuses and project evaluation; not runtime certification.",
    )
    args = parser.parse_args(argv)
    try:
        projects = load_inventory(args.manifest)
        if args.release:
            unfinished = [project.name for project in projects if project.status != "qualified"]
            if unfinished:
                raise InventoryError(f"Release inventory is not qualified: {', '.join(unfinished)}")
        if args.check_projects or args.evaluate or args.release:
            check_project_inventory(projects)
        cells = check_evaluated_projects(projects) if args.evaluate or args.release else 0
    except (
        InventoryError,
        OSError,
        UnicodeError,
        json.JSONDecodeError,
        subprocess.SubprocessError,
    ) as error:
        print(f"Package inventory error: {error}", file=sys.stderr)
        return 1
    packages = sum(not project.sample for project in projects)
    planned = sum(project.status == "planned" for project in projects)
    print(
        f"Validated inventory: {packages} packages, {len(projects) - packages} samples; "
        f"{planned} planned; {cells} evaluated project/TFM cells. "
        "This is not runtime or conformance qualification."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
