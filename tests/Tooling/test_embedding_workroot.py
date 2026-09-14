from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / "tests" / "PackageSmoke" / "EmbeddingWorkDirectory.ps1"


def quote(path: Path | str) -> str:
    return "'" + str(path).replace("'", "''") + "'"


class EmbeddingWorkRootTests(unittest.TestCase):
    def run_powershell(self, command: str) -> dict:
        executable = shutil.which("pwsh")
        self.assertIsNotNone(executable, "PowerShell 7 is required to exercise the actual work-directory helper.")
        result = subprocess.run(
            [executable, "-NoProfile", "-NonInteractive", "-Command",
             "$ErrorActionPreference='Stop'; . " + quote(HELPER) + "; " + command],
            capture_output=True, text=True, timeout=30, check=False,
            env=dict(os.environ, NO_COLOR="1"),
        )
        self.assertEqual(0, result.returncode, result.stderr)
        return json.loads(result.stdout)

    def test_caller_root_and_sibling_data_survive_owned_child_cleanup(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run, caller = root / "run", root / "caller data"
            run.mkdir()
            caller.mkdir()
            (caller / "keep.txt").write_text("caller-owned", encoding="utf-8")
            result = self.run_powershell(
                f"$work=New-EmbeddingWorkDirectory -RunDirectory {quote(run)} -WorkRoot {quote(caller)}; "
                "$parent=[IO.Path]::GetDirectoryName($work.Directory); "
                "$child=$work.Directory; "
                "New-Item -ItemType Directory -Path (Join-Path $child 'net10.0') | Out-Null; "
                "Set-Content -LiteralPath (Join-Path $child 'net10.0/state') -Value 'owned'; "
                "Remove-EmbeddingWorkDirectory -Work $work; "
                "@{parent=$parent;child=$child;exists=(Test-Path -LiteralPath $child)} | ConvertTo-Json -Compress"
            )
            self.assertEqual(caller, Path(result["parent"]))
            self.assertFalse(result["exists"])
            self.assertEqual("caller-owned", (caller / "keep.txt").read_text(encoding="utf-8"))
            self.assertEqual(["keep.txt"], [path.name for path in caller.iterdir()])

    def test_concurrent_run_children_are_distinct_and_cleanup_does_not_cross(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run, caller = root / "run", root / "caller"
            run.mkdir()
            caller.mkdir()
            result = self.run_powershell(
                f"$one=New-EmbeddingWorkDirectory -RunDirectory {quote(run)} -WorkRoot {quote(caller)}; "
                f"$two=New-EmbeddingWorkDirectory -RunDirectory {quote(run)} -WorkRoot {quote(caller)}; "
                "Remove-EmbeddingWorkDirectory -Work $one; "
                "$secondExists=Test-Path -LiteralPath $two.Directory; "
                "$different=$one.Directory -ne $two.Directory; "
                "Remove-EmbeddingWorkDirectory -Work $two; "
                "@{different=$different;secondExists=$secondExists} | ConvertTo-Json -Compress"
            )
            self.assertTrue(result["different"])
            self.assertTrue(result["secondExists"])
            self.assertTrue(caller.is_dir())
            self.assertEqual([], list(caller.iterdir()))

    def test_default_work_stays_in_run_artifacts_and_never_deletes_the_run(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            run = Path(temporary) / "run"
            run.mkdir()
            (run / "evidence.json").write_text("retained", encoding="utf-8")
            result = self.run_powershell(
                f"$work=New-EmbeddingWorkDirectory -RunDirectory {quote(run)}; "
                "$parent=$work.Root; Remove-EmbeddingWorkDirectory -Work $work; "
                "@{root=$parent} | ConvertTo-Json -Compress"
            )
            self.assertEqual(run / "work", Path(result["root"]))
            self.assertEqual("retained", (run / "evidence.json").read_text(encoding="utf-8"))

    def test_missing_or_relative_caller_roots_are_rejected_without_creation(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            run = Path(temporary) / "run"
            run.mkdir()
            missing = Path(temporary) / "missing"
            result = self.run_powershell(
                f"$failures=0; foreach ($path in @({quote(missing)},'relative-work')) {{ "
                f"try {{ New-EmbeddingWorkDirectory -RunDirectory {quote(run)} -WorkRoot $path | Out-Null }} "
                "catch { $failures++ } }; @{failures=$failures} | ConvertTo-Json -Compress"
            )
            self.assertEqual(2, result["failures"])
            self.assertFalse(missing.exists())

    def test_cleanup_rejects_the_caller_root_and_a_sibling(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run, caller, sibling = root / "run", root / "caller", root / "sibling"
            for path in (run, caller, sibling):
                path.mkdir()
            result = self.run_powershell(
                f"$failures=0; foreach ($path in @({quote(caller)},{quote(sibling)})) {{ "
                f"try {{ Remove-EmbeddingWorkDirectory -Work ([pscustomobject]@{{Root={quote(caller)};Directory=$path}}) }} "
                "catch { $failures++ } }; @{failures=$failures} | ConvertTo-Json -Compress"
            )
            self.assertEqual(2, result["failures"])
            self.assertTrue(caller.is_dir())
            self.assertTrue(sibling.is_dir())

    def test_cleanup_rejects_a_replaced_child_link_without_touching_target(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run, caller, target = root / "run", root / "caller", root / "target"
            for path in (run, caller, target):
                path.mkdir()
            (target / "keep.txt").write_text("not ours", encoding="utf-8")
            result = self.run_powershell(
                f"$work=New-EmbeddingWorkDirectory -RunDirectory {quote(run)} -WorkRoot {quote(caller)}; "
                "Remove-Item -LiteralPath $work.Directory; "
                "$kind=if($IsWindows){'Junction'}else{'SymbolicLink'}; "
                f"New-Item -ItemType $kind -Path $work.Directory -Target {quote(target)} | Out-Null; "
                "$rejected=$false; try { Remove-EmbeddingWorkDirectory -Work $work } catch { $rejected=$true }; "
                "Remove-Item -LiteralPath $work.Directory -Force; "
                "@{rejected=$rejected} | ConvertTo-Json -Compress"
            )
            self.assertTrue(result["rejected"])
            self.assertEqual("not ours", (target / "keep.txt").read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
