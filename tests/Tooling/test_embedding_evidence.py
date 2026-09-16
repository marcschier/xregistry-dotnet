# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

from __future__ import annotations

import copy
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "embedding_evidence", ROOT / "tests" / "PackageSmoke" / "EmbeddingEvidence.py"
)
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = tool
SPEC.loader.exec_module(tool)
CASES = json.loads((ROOT / "tests" / "PackageSmoke" / "EmbeddingCases.json").read_bytes())


class EmbeddingEvidenceTests(unittest.TestCase):
    def test_every_runtime_package_in_the_declared_manifest_is_required(self) -> None:
        declared = json.loads((ROOT / "eng" / "packages.json").read_bytes())
        self.assertEqual({package["id"] for package in declared["packages"]}, set(tool.PACKAGE_IDS))

    def test_original_fifteen_cases_remain_intact_before_new_runtime_cases(self) -> None:
        self.assertEqual([
            "package-assets-no-opc", "model-path-and-header-codec", "packaged-model-and-validator",
            "anonymous-and-header-identity-denied", "arbitrary-metadata-and-caller-identity",
            "single-dispatch-single-publication", "exact-document-and-upload-ownership",
            "metadata-document-separation", "authenticated-reader-write-denied",
            "unsupported-mutation-before-preparation", "readonly-provider-before-access",
            "direct-operation-caller-stream-ownership", "staged-publication-conflict-and-cancellation",
            "snapshot-lease-lifetimes", "results-outlive-host-and-store"
        ], CASES[:15])
        self.assertEqual(25, len(CASES))
        self.assertEqual("federation-escaped-identities", CASES[-1])

    def native_report(self) -> dict:
        return {
            "schemaVersion": 1,
            "scope": "development-package-embedding",
            "releaseQualified": False,
            "outcome": "passed",
            "framework": "net8.0",
            "runtimeIdentifier": "win-x64",
            "dynamicCodeSupported": False,
            "dynamicCodeCompiled": False,
            "jitCompiledMethodsAtStart": 0,
            "jitCompiledMethodsAtEnd": 0,
            "processArchitecture": "X64",
            "osArchitecture": "X64",
            "cases": [{"name": name, "outcome": "passed"} for name in CASES],
            "httpRequests": 18,
            "requests": ["GET /registry/"] * 18,
            "committedBatches": 5,
            "documentBytesVerified": 100,
            "runtimeEvidence": dict(tool.RUNTIME_MEASUREMENTS, federationHttpRequests=10,
                                    federationEscapedHttpRequests=25, sqliteNativeModules=1),
            "runtimeFacts": {"federationEscapedWireVersionXid":
                             "/workspaces/group%3Aone/artifacts/item%40stable/versions/v%3A1"},
            "nativeModules": ["EmbeddingConsumer.exe", "e_sqlite3.dll"],
            "assemblies": [
                {"name": "XRegistry.Client", "sha256": "a" * 64, "references": ["XRegistry", "System.Runtime"]}
            ],
        }

    def test_exact_native_behavior_report_is_accepted(self) -> None:
        tool.validate_report(self.native_report(), "net8.0", "win-x64", "native")

    def test_startup_only_zero_or_partial_cases_cannot_qualify(self) -> None:
        for cases in ([], self.native_report()["cases"][:-1]):
            report = self.native_report()
            report["cases"] = cases
            with self.subTest(cases=len(cases)), self.assertRaises(tool.EvidenceError):
                tool.validate_report(report, "net8.0", "win-x64", "native")

    def test_case_identity_order_and_outcome_are_enforced(self) -> None:
        for change in ("duplicate", "rename", "reverse", "failed"):
            report = self.native_report()
            if change == "duplicate":
                report["cases"][-1] = report["cases"][0]
            elif change == "rename":
                report["cases"][0]["name"] = "startup-only"
            elif change == "reverse":
                report["cases"].reverse()
            else:
                report["cases"][0]["outcome"] = "failed"
            with self.subTest(change=change), self.assertRaises(tool.EvidenceError):
                tool.validate_report(report, "net8.0", "win-x64", "native")

    def test_each_runtime_feature_and_jit_counter_can_independently_fail_native(self) -> None:
        for field, value in (
            ("dynamicCodeSupported", True), ("dynamicCodeCompiled", True),
            ("jitCompiledMethodsAtStart", 1), ("jitCompiledMethodsAtEnd", 1)
        ):
            report = self.native_report()
            report[field] = value
            with self.subTest(field=field), self.assertRaises(tool.EvidenceError):
                tool.validate_report(report, "net8.0", "win-x64", "native")

    def test_native_report_requires_packet_body_and_publication_evidence(self) -> None:
        for field in ("httpRequests", "committedBatches", "documentBytesVerified"):
            report = self.native_report()
            report[field] = 0
            with self.subTest(field=field), self.assertRaises(tool.EvidenceError):
                tool.validate_report(report, "net8.0", "win-x64", "native")

    def test_other_framework_architecture_and_release_claims_are_rejected(self) -> None:
        for field, value in (
            ("framework", "net10.0"), ("runtimeIdentifier", "linux-x64"),
            ("processArchitecture", "Arm64"), ("osArchitecture", "Arm64"),
            ("releaseQualified", True), ("scope", "full-conformance")
        ):
            report = self.native_report()
            report[field] = value
            with self.subTest(field=field), self.assertRaises(tool.EvidenceError):
                tool.validate_report(report, "net8.0", "win-x64", "native")

    def test_feature_masked_jit_requires_positive_jit_evidence_and_no_dispatch(self) -> None:
        report = self.native_report()
        report.update(outcome="jit-rejected", cases=[], requests=[], httpRequests=0, committedBatches=0,
                      documentBytesVerified=0, assemblies=[],
                      runtimeEvidence={}, nativeModules=[],
                      jitCompiledMethodsAtStart=7, jitCompiledMethodsAtEnd=7)
        tool.validate_report(report, "net8.0", "win-x64", "masked-jit")
        for field, value in (("jitCompiledMethodsAtStart", 0), ("httpRequests", 1),
                             ("dynamicCodeSupported", True)):
            altered = copy.deepcopy(report)
            altered[field] = value
            with self.subTest(field=field), self.assertRaises(tool.EvidenceError):
                tool.validate_report(altered, "net8.0", "win-x64", "masked-jit")

    def test_opc_runtime_references_cannot_hide_in_a_native_report(self) -> None:
        report = self.native_report()
        report["assemblies"][0]["references"].append("Opc.Ua.Core")
        with self.assertRaises(tool.EvidenceError):
            tool.validate_report(report, "net8.0", "win-x64", "native")

    def test_references_alone_cannot_substitute_for_each_new_runtime_operation(self) -> None:
        for field in tool.RUNTIME_MEASUREMENTS:
            report = self.native_report()
            del report["runtimeEvidence"][field]
            with self.subTest(field=field), self.assertRaises(tool.EvidenceError):
                tool.validate_report(report, "net8.0", "win-x64", "native")

    def test_native_git_backends_and_missing_sqlite_execution_are_rejected(self) -> None:
        for module in ("libgit2.dll", "git2.dll", "git.exe"):
            report = self.native_report()
            report["nativeModules"].append(module)
            with self.subTest(module=module), self.assertRaises(tool.EvidenceError):
                tool.validate_report(report, "net8.0", "win-x64", "native")
        report = self.native_report()
        report["runtimeEvidence"]["sqliteNativeModules"] = 0
        with self.assertRaises(tool.EvidenceError):
            tool.validate_report(report, "net8.0", "win-x64", "native")

    def test_escaped_identity_case_must_preserve_wire_metadata_and_cover_each_form(self) -> None:
        report = self.native_report()
        report["runtimeFacts"]["federationEscapedWireVersionXid"] = "/workspaces/group:one/artifacts/item@stable/versions/v:1"
        with self.assertRaises(tool.EvidenceError):
            tool.validate_report(report, "net8.0", "win-x64", "native")
        for name in ("federationEscapedMetadataForms", "federationEscapedDocumentForms", "federationEscapedInvalidTargets"):
            report = self.native_report()
            report["runtimeEvidence"][name] -= 1
            with self.subTest(name=name), self.assertRaises(tool.EvidenceError):
                tool.validate_report(report, "net8.0", "win-x64", "native")

    def test_frozen_fixture_inputs_are_copied_and_independently_hashed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / "fixtures"
            result = tool.stage_fixtures(ROOT, destination)
            self.assertEqual(163, len(result["files"]))
            for item in result["files"]:
                self.assertEqual(item["sha256"], tool.sha256(destination / item["path"]))
            expected = json.loads((destination / "oci" / "expected.json").read_bytes())
            self.assertEqual(99, expected["roots"]["offline"]["objects"])
            self.assertEqual(98, expected["roots"]["linked"]["objects"])

    def test_changed_fixture_pin_is_not_silently_regenerated(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "tests" / "PackageSmoke" / "EmbeddingFixtureInputs.json"
            manifest.parent.mkdir(parents=True)
            fixture = root / "independent"
            fixture.mkdir()
            (fixture / "bytes.bin").write_bytes(b"changed bytes")
            manifest.write_text(json.dumps({"directories": [{
                "name": "mapping", "source": "independent", "files": 1, "bytes": 13, "treeSha256": "0" * 64
            }], "files": []}), encoding="utf-8")
            with self.assertRaises(tool.EvidenceError):
                tool.stage_fixtures(root, root / "copied")

    def test_consumer_assets_require_exact_packages_without_source_projects(self) -> None:
        versions = {name: "0.1.0-fixture" for name in tool.PACKAGE_IDS}
        assets = {"libraries": {f"{name}/0.1.0-fixture": {"type": "package"} for name in versions}}
        tool.validate_assets(assets, versions)
        for additional in ({"Opc.Ua.Core/1.0": {"type": "package"}},
                           {"Local.Application/1.0": {"type": "project"}},
                           {"TUnit/1.0": {"type": "package"}}):
            altered = copy.deepcopy(assets)
            altered["libraries"].update(additional)
            with self.subTest(additional=additional), self.assertRaises(tool.EvidenceError):
                tool.validate_assets(altered, versions)
        altered = copy.deepcopy(assets)
        del altered["libraries"]["XRegistry.Client/0.1.0-fixture"]
        with self.assertRaises(tool.EvidenceError):
            tool.validate_assets(altered, versions)

    def package_fixture(self, root: Path) -> tuple[Path, Path, Path, Path]:
        feed, cache = root / "feed", root / "cache"
        feed.mkdir()
        cache.mkdir()
        versions = {name: "1.0.0-fixture" for name in tool.PACKAGE_IDS}
        assets = {"libraries": {}, "targets": {"net8.0/win-x64": {}}}
        for name, version in versions.items():
            identity = f"{name}/{version}"
            asset = f"lib/net8.0/{name}.dll"
            package_path = f"{name.lower()}/{version}"
            content = ("independent-fixture:" + name).encode()
            assets["libraries"][identity] = {"type": "package", "path": package_path}
            assets["targets"]["net8.0/win-x64"][identity] = {"runtime": {asset: {}}}
            target = cache / package_path / asset
            target.parent.mkdir(parents=True)
            target.write_bytes(content)
            with zipfile.ZipFile(feed / f"{name}.{version}.nupkg", "w") as package:
                package.writestr(asset, content)
        assets_path, versions_path = root / "assets.json", root / "versions.json"
        assets_path.write_text(json.dumps(assets), encoding="utf-8")
        versions_path.write_text(json.dumps(versions), encoding="utf-8")
        return assets_path, versions_path, feed, cache

    def test_inventory_retains_exact_packaged_assets_and_hashes(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            assets, versions, feed, cache = self.package_fixture(root)
            result = tool.inventory(assets, versions, feed, cache, "net8.0", "win-x64", root / "retained")
            self.assertEqual(11, len(result["packages"]))
            self.assertEqual(set(tool.PACKAGE_IDS), {item["name"] for item in result["assemblies"]})
            for item in result["assemblies"]:
                retained = Path(item["path"])
                self.assertTrue(retained.is_relative_to(root / "retained"))
                self.assertEqual(item["sha256"], tool.sha256(retained))

    def test_inventory_rejects_a_stale_or_modified_cached_assembly(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            assets, versions, feed, cache = self.package_fixture(root)
            target = cache / "xregistry/1.0.0-fixture/lib/net8.0/XRegistry.dll"
            target.write_bytes(b"not the freshly packed binary")
            with self.assertRaises(tool.EvidenceError):
                tool.inventory(assets, versions, feed, cache, "net8.0", "win-x64", root / "retained")

    def test_inventory_cannot_follow_an_asset_outside_its_cache(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            assets, versions, feed, cache = self.package_fixture(root)
            value = json.loads(assets.read_bytes())
            value["targets"]["net8.0/win-x64"]["XRegistry/1.0.0-fixture"]["runtime"] = {"../../../outside.dll": {}}
            assets.write_text(json.dumps(value), encoding="utf-8")
            with self.assertRaises(tool.EvidenceError):
                tool.inventory(assets, versions, feed, cache, "net8.0", "win-x64", root / "retained")

    def test_source_fingerprint_excludes_generated_outputs_but_detects_source_changes(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in tool.PACKAGE_IDS:
                project = root / "src" / name
                project.mkdir(parents=True)
                (project / "Source.cs").write_text("class Original {}", encoding="utf-8")
            for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
                         "global.json", "NuGet.config", "version.json", "README.md", "LICENSE", "NOTICE"):
                (root / name).write_text("fixture", encoding="utf-8")
            (root / "eng").mkdir()
            (root / "eng" / "test-embedding-packages.ps1").write_text("fixture", encoding="utf-8")
            (root / "tests" / "PackageSmoke").mkdir(parents=True)
            packages = root / "Directory.Packages.props"
            before = tool.source_manifest(root)["sha256"]
            packages.write_text("changed", encoding="utf-8")
            self.assertNotEqual(before, tool.source_manifest(root)["sha256"])
            packages.write_text("fixture", encoding="utf-8")
            self.assertEqual(before, tool.source_manifest(root)["sha256"])
            (root / "src" / "XRegistry" / "obj").mkdir()
            (root / "src" / "XRegistry" / "obj" / "Generated.cs").write_text("generated", encoding="utf-8")
            (root / "src" / "XRegistry" / "obj" / "project.assets.json").write_text("generated", encoding="utf-8")
            self.assertEqual(before, tool.source_manifest(root)["sha256"])
            (root / "src" / "XRegistry" / "Source.cs").write_text("class Changed {}", encoding="utf-8")
            self.assertNotEqual(before, tool.source_manifest(root)["sha256"])


if __name__ == "__main__":
    unittest.main()
