# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""SDK property-level regression checks for lockfile-free restores."""

import json
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "src" / "XRegistry" / "XRegistry.csproj"


class RestorePolicyTests(unittest.TestCase):
    def properties(self, *values: str) -> dict[str, str]:
        result = subprocess.run(
            ["dotnet", "msbuild", str(PROJECT), "-nologo",
             "-getProperty:NuGetLockFilePath,RestorePackagesWithLockFile,RestoreLockedMode",
             *["-p:" + value for value in values]],
            cwd=ROOT, capture_output=True, text=True, check=True, timeout=45,
        )
        return json.loads(result.stdout)["Properties"]

    def test_repository_disables_lockfile_generation(self):
        values = self.properties()
        self.assertEqual("", values["NuGetLockFilePath"])
        self.assertEqual("false", values["RestorePackagesWithLockFile"].lower())
        self.assertNotEqual("true", values["RestoreLockedMode"].lower())

    def test_rid_and_aot_profiles_do_not_opt_into_lockfiles(self):
        profiles = (
            ("RuntimeIdentifier=win-x64", "PublishAot=false"),
            ("RuntimeIdentifier=win-arm64", "PublishAot=true"),
            ("RuntimeIdentifier=linux-x64", "PublishAot=true"),
            ("RuntimeIdentifier=linux-arm64", "PublishAot=true"),
            ("PublishAot=true",),
        )
        for profile in profiles:
            with self.subTest(profile=profile):
                values = self.properties(*profile)
                self.assertEqual("", values["NuGetLockFilePath"])
                self.assertEqual("false", values["RestorePackagesWithLockFile"].lower())
                self.assertNotEqual("true", values["RestoreLockedMode"].lower())

if __name__ == "__main__":
    unittest.main()
