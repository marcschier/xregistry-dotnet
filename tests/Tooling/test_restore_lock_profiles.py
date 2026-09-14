"""SDK property-level regression checks for portable and RID-specific lock isolation."""

import json
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "src" / "XRegistry" / "XRegistry.csproj"


class RestoreLockProfileTests(unittest.TestCase):
    def properties(self, *values: str) -> dict[str, str]:
        result = subprocess.run(
            ["dotnet", "msbuild", str(PROJECT), "-nologo",
             "-getProperty:NuGetLockFilePath,MSBuildProjectExtensionsPath",
             *["-p:" + value for value in values]],
            cwd=ROOT, capture_output=True, text=True, check=True, timeout=45,
        )
        return json.loads(result.stdout)["Properties"]

    def test_portable_restore_keeps_the_source_lock_convention(self):
        values = self.properties()
        self.assertEqual("", values["NuGetLockFilePath"])

    def test_rid_profiles_live_under_the_project_artifacts(self):
        for rid in ("win-x64", "win-arm64", "linux-x64", "linux-arm64"):
            with self.subTest(rid=rid):
                values = self.properties("RuntimeIdentifier=" + rid, "PublishAot=true")
                self.assertEqual(
                    Path(values["MSBuildProjectExtensionsPath"]) / ("native-" + rid + ".packages.lock.json"),
                    Path(values["NuGetLockFilePath"]),
                )

    def test_rid_managed_publish_does_not_overwrite_portable_lock(self):
        values = self.properties("RuntimeIdentifier=win-x64", "PublishAot=false")
        self.assertEqual(
            Path(values["MSBuildProjectExtensionsPath"]) / "native-win-x64.packages.lock.json",
            Path(values["NuGetLockFilePath"]),
        )

    def test_aot_without_explicit_rid_has_an_isolated_profile(self):
        values = self.properties("PublishAot=true")
        self.assertEqual(
            Path(values["MSBuildProjectExtensionsPath"]) / "native-aot.packages.lock.json",
            Path(values["NuGetLockFilePath"]),
        )

    def test_explicit_caller_lock_path_is_not_replaced(self):
        target = str(ROOT / "artifacts" / "caller-selected.packages.lock.json")
        values = self.properties("RuntimeIdentifier=win-x64", "PublishAot=true", "NuGetLockFilePath=" + target)
        self.assertEqual(target, values["NuGetLockFilePath"])


if __name__ == "__main__":
    unittest.main()
