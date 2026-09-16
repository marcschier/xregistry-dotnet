# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

from __future__ import annotations

import copy
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("reverse_build", ROOT / "eng" / "verify-upstream-to-dotnet-build.py")
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = tool
SPEC.loader.exec_module(tool)


class ReverseBuildTests(unittest.TestCase):
    def test_reverse_lock_must_agree_with_exact_existing_upstream_pin(self) -> None:
        lock = json.loads((ROOT / "interop" / "reverse-toolchain.json").read_bytes())
        upstream = json.loads((ROOT / "interop" / "upstream-lock.json").read_bytes())
        tool.validate_lock(lock, upstream)
        for field, value in (("commit", "a" * 40), ("repository", "https://unapproved.invalid/repo"),
                             ("expectedVersion", "Version: latest"), ("goModSha256", "bad")):
            altered = copy.deepcopy(lock)
            altered[field] = value
            with self.subTest(field=field), self.assertRaises(tool.BuildError):
                tool.validate_lock(altered, upstream)

    def test_binary_build_metadata_requires_unmodified_exact_vcs_commit(self) -> None:
        text = (
            "xr.exe: go1.27.1\n"
            "\tpath\tgithub.com/xregistry/server/cmds/xr\n"
            "\tbuild\tvcs=git\n"
            "\tbuild\tvcs.revision=5854af0130db7723bad489f16ba66536126b823a\n"
            "\tbuild\tvcs.modified=false\n"
            "\tbuild\tGOOS=windows\n\tbuild\tGOARCH=amd64\n"
        )
        tool.validate_build_metadata(text, "5854af0130db7723bad489f16ba66536126b823a", "go1.27.1", "windows-amd64")
        for old, new in (("modified=false", "modified=true"), ("vcs.revision=5854", "vcs.revision=ffff"),
                         ("go1.27.1", "go1.26.8"), ("GOARCH=amd64", "GOARCH=arm64")):
            with self.subTest(new=new), self.assertRaises(tool.BuildError):
                tool.validate_build_metadata(text.replace(old, new),
                    "5854af0130db7723bad489f16ba66536126b823a", "go1.27.1", "windows-amd64")

    def test_archive_cannot_write_outside_its_owned_go_directory(self) -> None:
        for name in ("../outside", "/absolute", "go/../../outside", "go\\..\\outside", "other/bin/go"):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary:
                archive = Path(temporary) / "tool.zip"
                with zipfile.ZipFile(archive, "w") as output:
                    output.writestr(name, b"not executable")
                with self.assertRaises(tool.BuildError):
                    tool.extract_go_archive(archive, Path(temporary) / "tools")

    def test_archive_hash_and_size_are_verified_before_extraction(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            archive = Path(temporary) / "go.zip"
            archive.write_bytes(b"not-the-pinned-archive")
            with self.assertRaises(tool.BuildError):
                tool.verify_archive(archive, {"sha256": "0" * 64, "bytes": 20})


if __name__ == "__main__":
    unittest.main()
