from __future__ import annotations

import copy
import importlib.util
import json
from pathlib import Path
import subprocess
from types import SimpleNamespace
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "storage_crash_accounting", ROOT / "eng" / "verify_storage_crashes.py"
)
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(tool)


class StorageCrashAccountingTests(unittest.TestCase):
    def inspect(self, data: dict, allowed: set[int] = frozenset({0, 1})) -> dict:
        with patch.object(
            tool.subprocess, "run", return_value=SimpleNamespace(stdout=json.dumps(data))
        ):
            return tool.inspect(Path("probe"), Path("data"), allowed)

    def test_empty_uncommitted_recovery_is_not_a_fake_committed_success(self) -> None:
        data = {
            "native": True, "jitCompiledMethods": 0, "generation": 0, "records": {},
            "remainingStagingFiles": 0, "removedOrphans": 1,
        }
        self.assertEqual(
            self.inspect(data, {0}),
            {"generation": 0, "records": 0, "removedOrphans": 1},
        )
        with self.assertRaises(RuntimeError):
            self.inspect(data, {1})

    def test_exact_thousand_record_generation_is_required_after_acknowledgement(self) -> None:
        data = self.committed()
        self.assertEqual(self.inspect(data, {1})["records"], 1000)
        for mutation in ("missing", "epoch", "document", "ordinal-type", "generation-type"):
            changed = copy.deepcopy(data)
            if mutation == "missing":
                del changed["records"]["record-0001"]
            elif mutation == "epoch":
                changed["records"]["record-0001"]["metadata"]["epoch"] -= 1
            elif mutation == "document":
                changed["records"]["record-0000"]["document"] = ""
            elif mutation == "ordinal-type":
                changed["records"]["record-0001"]["metadata"]["ordinal"] = True
            else:
                changed["generation"] = True
            with self.subTest(mutation=mutation), self.assertRaises(RuntimeError):
                self.inspect(changed, {1})

    def test_missing_native_proof_and_orphan_accounting_are_rejected(self) -> None:
        for field, value in (
            ("native", False), ("native", 1), ("jitCompiledMethods", 1),
            ("jitCompiledMethods", False), ("generation", -1),
            ("remainingStagingFiles", 1), ("remainingStagingFiles", False),
            ("removedOrphans", None), ("removedOrphans", -1),
        ):
            data = {
                "native": True, "jitCompiledMethods": 0, "generation": 0, "records": {},
                "remainingStagingFiles": 0, "removedOrphans": 1,
            }
            data[field] = value
            with self.subTest(field=field, value=value), self.assertRaises(RuntimeError):
                self.inspect(data)

    def test_failed_native_inspection_is_not_replaced_by_empty_state(self) -> None:
        with patch.object(tool.subprocess, "run", side_effect=subprocess.CalledProcessError(1, "probe")):
            with self.assertRaises(subprocess.CalledProcessError):
                tool.inspect(Path("probe"), Path("data"), {0})

    @staticmethod
    def committed() -> dict:
        return {
            "native": True,
            "jitCompiledMethods": 0,
            "generation": 1,
            "records": {
                f"record-{index:04d}": {
                    "metadata": {"ordinal": index, "epoch": 184467440737095516160},
                    "document": bytes(range(256)).hex().upper() if index == 0 else None,
                }
                for index in range(1000)
            },
            "remainingStagingFiles": 0,
            "removedOrphans": 0,
        }


if __name__ == "__main__":
    unittest.main()
