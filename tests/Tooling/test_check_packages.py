from __future__ import annotations

import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("package_inventory", ROOT / "eng" / "check_packages.py")
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = tool
SPEC.loader.exec_module(tool)


def manifest() -> dict:
    return {
        "schemaVersion": 1,
        "repository": "https://github.com/marcschier/xregistry-dotnet",
        "targetFrameworks": ["net8.0", "net10.0"],
        "nativeRuntimeIdentifiers": ["win-x64", "win-arm64", "linux-x64", "linux-arm64"],
        "packages": [
            {"id": "XRegistry", "project": "src\\XRegistry\\XRegistry.csproj", "status": "planned"}
        ],
        "samples": [
            {
                "name": name,
                "project": f"samples\\{name}\\{name}.csproj",
                "targetFramework": "net10.0",
                "status": "planned",
            }
            for name in ("XRegistry.FederationBridge", "XRegistry.FileServer", "XRegistry.Client")
        ],
    }


class ManifestTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.path = self.root / "packages.json"

    def load(self, value: dict | str | bytes):
        data = value if isinstance(value, bytes) else (
            value.encode("utf-8") if isinstance(value, str) else json.dumps(value).encode("utf-8")
        )
        self.path.write_bytes(data)
        return tool.load_inventory(self.path, self.root)

    def test_valid_manifest_preserves_id_paths_status_and_sample_identity(self) -> None:
        projects = self.load(manifest())
        self.assertEqual(len(projects), 4)
        self.assertEqual(projects[0].name, "XRegistry")
        self.assertEqual(projects[0].path, self.root / "src" / "XRegistry" / "XRegistry.csproj")
        self.assertEqual(projects[0].status, "planned")
        self.assertFalse(projects[0].sample)
        self.assertEqual(
            [item.name for item in projects if item.sample],
            ["XRegistry.FederationBridge", "XRegistry.FileServer", "XRegistry.Client"],
        )

    def test_duplicate_json_keys_and_non_json_numbers_are_rejected(self) -> None:
        for text in ('{"x":1,"x":2}', '{"x":NaN}', '{"x":Infinity}', '{"x":-Infinity}'):
            with self.subTest(text=text), self.assertRaises(tool.InventoryError):
                self.load(text)

    def test_manifest_byte_limit_accepts_exact_limit_and_rejects_next_byte(self) -> None:
        data = json.dumps(manifest()).encode()
        exact = data + b" " * (tool.MAX_MANIFEST_BYTES - len(data))
        self.assertEqual(len(self.load(exact)), 4)
        with self.assertRaisesRegex(tool.InventoryError, "byte limit"):
            self.load(exact + b" ")

    def test_schema_version_requires_integer_one_not_bool_or_float(self) -> None:
        for version in (True, False, 1.0, "1", 0, 2, None, []):
            data = manifest()
            data["schemaVersion"] = version
            with self.subTest(version=version), self.assertRaises(tool.InventoryError):
                self.load(data)

    def test_exact_keys_reject_missing_or_extra_root_and_entry_fields(self) -> None:
        for scope, field in ((None, "repository"), ("packages", "status"), ("samples", "name")):
            for operation in ("missing", "extra"):
                data = manifest()
                target = data if scope is None else data[scope][0]
                if operation == "missing":
                    del target[field]
                else:
                    target["unexpected"] = 123
                with self.subTest(scope=scope, operation=operation), self.assertRaises(tool.InventoryError):
                    self.load(data)

    def test_repository_rejects_altered_origins_paths_queries_and_control_characters(self) -> None:
        for url in (
            "http://github.com/marcschier/xregistry-dotnet",
            "https://user@github.com/marcschier/xregistry-dotnet",
            "https://github.com:443/marcschier/xregistry-dotnet",
            "https://github.com/marcschier/xregistry-dotnet/",
            "https://github.com/marcschier/xregistry-dotnet?q=1",
            "https://github.com/marcschier/xregistry-dotnet#fragment",
            "\nhttps://github.com/marcschier/xregistry-dotnet",
            "https://git\thub.com/marcschier/xregistry-dotnet",
            "https://[invalid/path",
            None,
        ):
            data = manifest()
            data["repository"] = url
            with self.subTest(url=url), self.assertRaises(tool.InventoryError):
                self.load(data)

    def test_target_and_rid_sets_reject_missing_duplicate_unknown_and_nonstring(self) -> None:
        for key in ("targetFrameworks", "nativeRuntimeIdentifiers"):
            valid = manifest()[key]
            for value in (valid[:-1], valid + [valid[0]], valid[:-1] + ["unknown"], [None], "net10.0"):
                data = manifest()
                data[key] = value
                with self.subTest(key=key, value=value), self.assertRaises(tool.InventoryError):
                    self.load(data)

    def test_collections_cannot_be_empty_nonlists_or_over_limit(self) -> None:
        for key in ("packages", "samples"):
            for value in ([], {}, None, [manifest()[key][0]] * (tool.MAX_PROJECTS + 1)):
                data = manifest()
                data[key] = value
                with self.subTest(key=key, value_type=type(value)), self.assertRaises(tool.InventoryError):
                    self.load(data)

    def test_case_colliding_package_ids_are_rejected(self) -> None:
        data = manifest()
        data["packages"] += [
            {"id": "XRegistry.Tools", "project": "src\\XRegistry.Tools\\XRegistry.Tools.csproj", "status": "planned"},
            {"id": "XRegistry.tools", "project": "src\\XRegistry.tools\\XRegistry.tools.csproj", "status": "planned"},
        ]
        with self.assertRaisesRegex(tool.InventoryError, "Duplicate"):
            self.load(data)

    def test_invalid_package_id_status_missing_core_and_wrong_samples_are_rejected(self) -> None:
        for name in ("xregistry", "XRegistry..Bad", "XRegistry/Bad", "XRegistry.é", None):
            data = manifest()
            data["packages"][0]["id"] = name
            with self.subTest(name=name), self.assertRaises(tool.InventoryError):
                self.load(data)
        for status in ("", "passed", None, [], True):
            data = manifest()
            data["packages"][0]["status"] = status
            with self.subTest(status=status), self.assertRaises(tool.InventoryError):
                self.load(data)
        data = manifest()
        data["packages"][0].update(id="XRegistry.Other", project="src\\XRegistry.Other\\XRegistry.Other.csproj")
        with self.assertRaisesRegex(tool.InventoryError, "core package"):
            self.load(data)
        data = manifest()
        data["samples"].pop()
        with self.assertRaisesRegex(tool.InventoryError, "principal samples"):
            self.load(data)
        data = manifest()
        data["samples"][0]["targetFramework"] = "net8.0"
        with self.assertRaisesRegex(tool.InventoryError, "net10.0"):
            self.load(data)

    def test_project_paths_are_canonical_contained_and_platform_safe(self) -> None:
        for path in (
            "C:\\src\\XRegistry\\XRegistry.csproj",
            "..\\src\\XRegistry\\XRegistry.csproj",
            "src\\XRegistry\\..\\XRegistry.csproj",
            "src\\\\XRegistry\\XRegistry.csproj",
            "src\\XRegistry\\XRegistry.csproj:stream",
            "src\\XRegistry\\XRegistry.csproj ",
            "src\\XRegistry\\Another.csproj",
            None,
        ):
            data = manifest()
            data["packages"][0]["project"] = path
            with self.subTest(path=path), self.assertRaises(tool.InventoryError):
                self.load(data)
        data = manifest()
        data["packages"][0]["project"] = "src/XRegistry/XRegistry.csproj"
        self.assertEqual(self.load(data)[0].path, self.root / "src" / "XRegistry" / "XRegistry.csproj")

    def test_missing_library_rejected_but_planned_absent_samples_allowed(self) -> None:
        projects = self.load(manifest())
        with self.assertRaisesRegex(tool.InventoryError, "missing"):
            tool.check_project_inventory(projects, self.root)
        projects[0].path.parent.mkdir(parents=True)
        projects[0].path.write_text("<Project />")
        tool.check_project_inventory(projects, self.root)
        data = manifest()
        data["samples"][0]["status"] = "implemented"
        with self.assertRaisesRegex(tool.InventoryError, "missing"):
            tool.check_project_inventory(self.load(data), self.root)

    def test_unclassified_projects_fail_and_build_intermediates_are_ignored(self) -> None:
        projects = self.load(manifest())
        projects[0].path.parent.mkdir(parents=True)
        projects[0].path.write_text("<Project />")
        generated = projects[0].path.parent / "obj" / "generated.csproj"
        generated.parent.mkdir()
        generated.write_text("<Project />")
        tool.check_project_inventory(projects, self.root)
        other = self.root / "src" / "Other.csproj"
        other.write_text("<Project />")
        with self.assertRaisesRegex(tool.InventoryError, "Unclassified"):
            tool.check_project_inventory(projects, self.root)

    def test_cli_success_is_inventory_only_and_release_rejects_planned(self) -> None:
        self.load(manifest())
        stdout, stderr = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            self.assertEqual(tool.main(["--manifest", str(self.path)]), 0)
            self.assertEqual(tool.main(["--manifest", str(self.path), "--release"]), 1)
        self.assertIn("not runtime or conformance qualification", stdout.getvalue())
        self.assertIn("not qualified", stderr.getvalue())


class ProjectEvaluationTests(unittest.TestCase):
    def library(self, framework: str = "net8.0") -> dict[str, str]:
        return {
            "PackageId": "XRegistry",
            "TargetFramework": framework,
            "TargetFrameworks": "net8.0;net10.0",
            "IsPackable": "true",
            "IsAotCompatible": "true",
            "EnableAotAnalyzer": "true",
            "EnableTrimAnalyzer": "true",
            "PublishAot": "",
        }

    def test_each_library_tfm_and_aot_flag_is_required(self) -> None:
        project = tool.Project("XRegistry", Path("src/XRegistry/XRegistry.csproj"), "planned", False)
        for key, bad in (
            ("PackageId", "Wrong"),
            ("TargetFramework", "net7.0"),
            ("TargetFrameworks", "net10.0"),
            ("IsPackable", "false"),
            ("IsAotCompatible", "false"),
            ("EnableAotAnalyzer", "false"),
            ("EnableTrimAnalyzer", "false"),
        ):
            properties = self.library()
            properties[key] = bad
            with self.subTest(key=key), mock.patch.object(tool, "evaluate_project", return_value=properties):
                with self.assertRaises(tool.InventoryError):
                    tool.check_evaluated_projects((project,))

    def test_sample_must_declare_only_net10_and_publish_native(self) -> None:
        project = tool.Project("XRegistry.Client", Path("sample.csproj"), "implemented", True)
        base = self.library("net10.0")
        base.update(PackageId=project.name, IsPackable="false", TargetFrameworks="", PublishAot="true")
        for key, bad in (("IsPackable", "true"), ("TargetFrameworks", "net8.0;net10.0"), ("PublishAot", "false")):
            properties = {**base, key: bad}
            with self.subTest(key=key), mock.patch.object(tool, "evaluate_project", return_value=properties):
                with self.assertRaises(tool.InventoryError):
                    tool.check_evaluated_projects((project,))

    def test_msbuild_nonzero_and_missing_properties_are_explicit_failures(self) -> None:
        project = tool.Project("XRegistry", Path("example.csproj"), "planned", False)
        results = (
            subprocess.CompletedProcess([], 1, "", "compiler error"),
            subprocess.CompletedProcess([], 0, '{"unexpected": {}}', ""),
            subprocess.CompletedProcess([], 0, '{"Properties": {}}', ""),
        )
        for result in results:
            with self.subTest(result=result), mock.patch.object(tool.subprocess, "run", return_value=result):
                with self.assertRaises(tool.InventoryError):
                    tool.evaluate_project(project, "net8.0")

    def test_msbuild_invocation_has_no_shell_and_a_bounded_timeout(self) -> None:
        project = tool.Project("XRegistry", Path("example.csproj"), "planned", False)
        result = subprocess.CompletedProcess([], 0, json.dumps({"Properties": self.library()}), "")
        with mock.patch.object(tool.subprocess, "run", return_value=result) as run:
            properties = tool.evaluate_project(project, "net8.0")
        self.assertEqual(properties["PackageId"], "XRegistry")
        self.assertEqual(run.call_args.args[0][:2], ["dotnet", "msbuild"])
        self.assertIn("-p:TargetFramework=net8.0", run.call_args.args[0])
        self.assertFalse(run.call_args.kwargs.get("shell", False))
        self.assertEqual(run.call_args.kwargs["timeout"], 120)


if __name__ == "__main__":
    unittest.main()
