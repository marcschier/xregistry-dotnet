#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Check or add the repository's short MIT source headers."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import os
from pathlib import Path
import sys


ROOT = Path(__file__).resolve().parents[1]
COPYRIGHT = "Copyright (c) 2026 xregistry-dotnet contributors."
SPDX = "SPDX-License-Identifier: MIT"
PROTECTED = (
    Path("tests/Conformance/Sources"),
    Path("tests/Conformance/Corrections"),
    Path("src/XRegistry.Models/Resources"),
)
PRUNED_NAMES = {
    ".git", ".idea", ".pytest_cache", ".vs", "__pycache__",
    "artifacts", "bin", "obj", "TestResults",
}
HASH_NAMES = {".dockerignore", ".editorconfig", ".gitattributes", ".gitignore"}
HASH_SUFFIXES = {".Dockerfile", ".ps1", ".py", ".sh", ".yaml", ".yml"}
SLASH_SUFFIXES = {".cs", ".source"}
XML_SUFFIXES = {".config", ".csproj", ".props", ".slnx", ".targets", ".template"}


@dataclass(frozen=True)
class Source:
    path: Path
    style: str


def relative_to(path: Path, root: Path) -> Path:
    try:
        return path.relative_to(root)
    except ValueError as error:
        raise ValueError(f"Source is outside the repository: {path}") from error


def protected(relative: Path) -> bool:
    return any(relative == item or item in relative.parents for item in PROTECTED)


def source_style(path: Path) -> str | None:
    if path.name in HASH_NAMES or path.suffix in HASH_SUFFIXES:
        return "hash"
    if path.suffix in SLASH_SUFFIXES:
        return "slash"
    if path.suffix in XML_SUFFIXES:
        return "xml"
    return None


def style_for(path: Path, text: str) -> str | None:
    style = source_style(path)
    if style == "xml":
        if not text.lstrip("\ufeff").startswith("<"):
            raise ValueError(f"Expected XML content in source template: {path}")
    return style


def owned_sources(root: Path = ROOT) -> list[Source]:
    result: list[Source] = []
    for directory, names, files in os.walk(root, followlinks=False):
        current = Path(directory)
        relative_directory = relative_to(current, root)
        names[:] = [
            name for name in names
            if name not in PRUNED_NAMES
            and not (current / name).is_symlink()
            and not protected(relative_directory / name)
        ]
        for name in files:
            path = current / name
            if path.is_symlink():
                continue
            relative = relative_to(path, root)
            if protected(relative):
                continue
            if source_style(relative) is None:
                continue
            data = path.read_bytes()
            try:
                text = data.decode("utf-8-sig")
            except UnicodeDecodeError as error:
                raise ValueError(f"Owned source is not UTF-8: {relative}") from error
            style = style_for(relative, text)
            if style is not None:
                result.append(Source(relative, style))
    return sorted(result, key=lambda item: item.path.as_posix())


def expected_header(style: str) -> tuple[str, str]:
    if style == "slash":
        return f"// {COPYRIGHT}", f"// {SPDX}"
    if style == "hash":
        return f"# {COPYRIGHT}", f"# {SPDX}"
    if style == "xml":
        return f"<!-- {COPYRIGHT} -->", f"<!-- {SPDX} -->"
    raise ValueError(f"Unknown header style: {style}")


def insertion_index(lines: list[str], style: str) -> int:
    if not lines:
        return 0
    if style == "hash" and lines[0].startswith("#!"):
        return 1
    if style == "hash":
        index = 0
        while index < len(lines) and lines[index].startswith(("# syntax=", "# escape=")):
            index += 1
        return index
    if style == "xml" and lines[0].startswith("<?xml"):
        return 1
    return 0


def header_errors(path: Path, style: str, text: str) -> list[str]:
    first, second = expected_header(style)
    lines = text.splitlines()
    index = insertion_index(lines, style)
    errors: list[str] = []
    if lines[index:index + 2] != [first, second]:
        errors.append("missing or misplaced header")
    if sum(line == first for line in lines) != 1:
        errors.append("copyright notice must appear exactly once")
    if sum(line == second for line in lines) != 1:
        errors.append("SPDX identifier must appear exactly once")
    if style == "hash" and lines and lines[0].startswith("#!") and index != 1:
        errors.append("shebang must remain first")
    if style == "xml" and lines and lines[0].startswith("<?xml") and index != 1:
        errors.append("XML declaration must remain first")
    return errors


def add_header(style: str, text: str) -> str:
    first, second = expected_header(style)
    lines = text.splitlines()
    if first in lines or second in lines:
        raise ValueError("Existing partial or duplicate license header needs manual repair.")
    trailing_newline = text.endswith(("\n", "\r"))
    index = insertion_index(lines, style)
    insertion = [first, second]
    if index < len(lines) and lines[index] != "":
        insertion.append("")
    lines[index:index] = insertion
    result = "\n".join(lines)
    if trailing_newline or not lines:
        result += "\n"
    return result


def apply(root: Path = ROOT) -> list[Path]:
    changed: list[Path] = []
    for source in owned_sources(root):
        path = root / source.path
        data = path.read_bytes()
        bom = data.startswith(b"\xef\xbb\xbf")
        text = data.decode("utf-8-sig")
        if not header_errors(source.path, source.style, text):
            continue
        updated = add_header(source.style, text)
        path.write_bytes((b"\xef\xbb\xbf" if bom else b"") + updated.encode("utf-8"))
        changed.append(source.path)
    return changed


def check(root: Path = ROOT) -> list[str]:
    errors: list[str] = []
    for source in owned_sources(root):
        path = root / source.path
        text = path.read_bytes().decode("utf-8-sig")
        for message in header_errors(source.path, source.style, text):
            errors.append(f"{source.path}: {message}")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fix", action="store_true")
    parser.add_argument("--root", type=Path, default=ROOT)
    args = parser.parse_args()
    root = args.root.resolve()
    try:
        if args.fix:
            changed = apply(root)
            print(f"Added MIT headers to {len(changed)} files.")
        errors = check(root)
    except (OSError, UnicodeError, ValueError) as error:
        print(f"Header check failed: {error}", file=sys.stderr)
        return 1
    if errors:
        for error in errors:
            print(error, file=sys.stderr)
        return 1
    print(f"Verified MIT headers on {len(owned_sources(root))} files.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
