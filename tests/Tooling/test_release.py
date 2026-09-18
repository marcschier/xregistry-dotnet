# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

from __future__ import annotations

import contextlib
import copy
import importlib.util
import io
import json
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import warnings
import zipfile


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("release_tool", ROOT / "eng" / "release" / "release.py")
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = tool
SPEC.loader.exec_module(tool)
ERRORS = (tool.ReleaseError, tool.inventory.InventoryError, tool.specification.SpecificationError)


def synthetic_identity() -> dict:
    return tool.identity_document("1.0.0-rc9", 7, "1" * 40, "2" * 40, 101, 1)


def synthetic_package(package_id: str, identity: dict, suffix: str, **changes) -> bytes:
    values = {
        "id": package_id, "version": identity["version"], "commit": identity["commit"],
        "url": tool.REPOSITORY_URL, "branch": f"refs/tags/{identity['tag']}",
    }
    values.update(changes)
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr(
            f"{package_id}.nuspec",
            '<?xml version="1.0" encoding="utf-8"?>'
            '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
            f'<metadata><id>{values["id"]}</id><version>{values["version"]}</version>'
            f'<repository type="git" url="{values["url"]}" branch="{values["branch"]}" '
            f'commit="{values["commit"]}" /></metadata></package>',
        )
        for framework in ("net8.0", "net10.0"):
            extension = "pdb" if suffix == "snupkg" else "dll"
            archive.writestr(f"lib/{framework}/{package_id}.{extension}", b"SYNTHETIC TOOLING FIXTURE, NOT RELEASE EVIDENCE")
    return stream.getvalue()


class SyntheticFixture(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory(prefix="xregistry-release-test-")
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.identity = synthetic_identity()
        self.source = json.loads((ROOT / "eng" / "packages.json").read_bytes())
        for entry in self.source["packages"] + self.source["samples"]:
            entry["status"] = "qualified"
        self.ids = tuple(sorted(entry["id"] for entry in self.source["packages"]))
        self.packages = self.root / "package-output"
        self.packages.mkdir()
        for package_id in self.ids:
            for suffix in ("nupkg", "snupkg"):
                (self.packages / f"{package_id}.{self.identity['version']}.{suffix}").write_bytes(
                    synthetic_package(package_id, self.identity, suffix)
                )
        corrections = b'{"fixture":"synthetic, not a specification correction"}\n'
        evidence = []
        for framework in ("net8.0", "net10.0"):
            for rid in ("win-x64", "win-arm64", "linux-x64", "linux-arm64"):
                relative = f"artifacts/native-fixtures/{framework}-{rid}.json"
                raw = tool.encoded({
                    "schemaVersion": 1, "framework": framework, "rid": rid,
                    "nativeExecutable": True, "passedTestIds": ["Synthetic.NotReleaseEvidence"],
                    "failedTestIds": [], "skippedTestIds": [],
                })
                path = tool.source_path(self.root, relative)
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(raw)
                evidence.append({"framework": framework, "rid": rid, "report": relative, "sha256": tool.sha256(raw)})
        self.ledger = {
            "schemaVersion": 1, "semanticCoverageReviewed": True,
            "baselineManifestSha256": "a" * 64, "correctionsSha256": tool.sha256(corrections),
            "requirements": [{
                "id": "SYNTHETIC-NOT-RELEASE-EVIDENCE",
                "review": {"status": "reviewed", "note": "Tooling fixture only", "roles": ["client", "server"]},
                "implementation": {"status": "qualified", "testIds": ["Synthetic.NotReleaseEvidence"], "nativeEvidence": evidence},
            }],
        }
        source_data = {
            "source-packages.json": tool.encoded(self.source),
            "source-specification-lock.json": tool.encoded({"baselineManifestSha256": "a" * 64}),
            "source-corrections.json": corrections,
            "source-requirements.json": tool.encoded(self.ledger),
        }
        self.hashes = {}
        for name, raw in source_data.items():
            path = tool.source_path(self.root, tool.SOURCE_FILES[name])
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(raw)
            self.hashes[name] = tool.sha256(raw)
        self.payload = self.root / "payload"

    def stage(self) -> None:
        tool.stage_payload(self.root, self.packages, self.payload, self.identity)

    def verify(self) -> dict:
        return tool.verify_payload(self.payload, self.identity, self.hashes, self.ids)

    def read_manifest(self) -> dict:
        return json.loads((self.payload / tool.MANIFEST).read_bytes())

    def write_manifest(self, value: dict) -> None:
        (self.payload / tool.MANIFEST).write_bytes(tool.encoded(value))

    def replace_payload_file(self, name: str, raw: bytes, *, source: bool = False) -> None:
        (self.payload / name).write_bytes(raw)
        manifest = self.read_manifest()
        for entry in manifest["files"]:
            if entry["name"] == name:
                entry.update(size=len(raw), sha256=tool.sha256(raw))
        self.write_manifest(manifest)
        if source:
            self.hashes[name] = tool.sha256(raw)

    def replace_ledger(self, ledger: dict) -> None:
        self.replace_payload_file("source-requirements.json", tool.encoded(ledger), source=True)

    def archive(self, name: str = "fixture.zip", extra: list[tuple[str, bytes]] | None = None) -> Path:
        path = self.root / name
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
                for item in sorted(self.payload.iterdir()):
                    archive.write(item, item.name)
                for member, raw in extra or []:
                    archive.writestr(member, raw)
        return path

    def selection(self, archive: Path) -> dict:
        return {
            "identity": self.identity, "workflowId": 88,
            "artifact": {
                "id": 900, "name": tool.artifact_name(self.identity),
                "size": archive.stat().st_size, "sha256": tool.file_hash(archive),
            },
            "sourceSha256": self.hashes,
        }

    def promotion_environment(self) -> dict[str, str]:
        return {
            "GITHUB_ACTIONS": "true", "GITHUB_SERVER_URL": "https://github.com",
            "GITHUB_REPOSITORY": tool.REPOSITORY, "GITHUB_EVENT_NAME": "workflow_dispatch",
            "GITHUB_REF": "refs/heads/main", "GITHUB_SHA": "3" * 40,
            "GITHUB_WORKFLOW_SHA": "3" * 40,
            "GITHUB_WORKFLOW_REF": f"{tool.REPOSITORY}/{tool.PROMOTION_WORKFLOW}@refs/heads/main",
            "GITHUB_RUN_ID": "202", "GITHUB_RUN_ATTEMPT": "1", "GITHUB_ACTOR": "dispatcher",
            "GITHUB_TRIGGERING_ACTOR": "dispatcher", "GITHUB_ACTOR_ID": "42",
            "RELEASE_VERSION": self.identity["version"], "RUNNER_TEMP": str(self.root),
            "GITHUB_OUTPUT": str(self.root / "github-output"),
            "GITHUB_STEP_SUMMARY": str(self.root / "github-summary"), "NUGET_USER": "fixture-account",
        }


class VersionTests(unittest.TestCase):
    def test_canonical_stable_alpha_and_semver_boundaries(self) -> None:
        for value in ("0.0.0", "1.2.3", "1.0.0-rc4", "0.1.0-alpha", "0.1.0-alpha.1", "1.2.3-0", "2147483647.0.0", "1.2.3-" + "a" * 58):
            with self.subTest(value=value):
                self.assertEqual(tool.version(value), value)

    def test_version_injection_aliases_and_invalid_boundaries_are_rejected(self) -> None:
        for value in (
            None, "", True, 1, [], "v1.2.3", "1.2", "1.2.3.4", "01.2.3", "1.02.3", "1.2.03",
            "1.2.3+build", "1.2.3-ALPHA", "1.2.3-alpha.01", "1.2.3-01", "1.2.3-alpha..1",
            "1.2.3-alpha_1", "1.2.3\n", " 1.2.3", "1.2.3 ", "1.2.3;whoami",
            "$(whoami)", "`whoami`", "1.2.3/../x", "1.2.3\\x", "1.2.3&echo bad",
            '1.2.3"-p:Bad=true', "1.2.3-%0a", "1.2.3-\u0430", "2147483648.0.0", "1.2.3-" + "a" * 59,
        ):
            with self.subTest(value=value), self.assertRaises(tool.ReleaseError):
                tool.version(value)

    def test_duplicate_json_keys_non_json_numbers_and_byte_limit(self) -> None:
        for value in (b'{"a":1,"a":2}', b'{"a":NaN}', b'{"a":Infinity}'):
            with self.subTest(value=value), self.assertRaises(ERRORS):
                tool.json_bytes(value)
        with mock.patch.object(tool, "MAX_JSON", 2):
            self.assertEqual(tool.json_bytes(b"{}"), {})
            with self.assertRaisesRegex(tool.ReleaseError, "byte limit"):
                tool.json_bytes(b"{} ")

    def test_version_injection_fails_cli_before_network_or_commands(self) -> None:
        stderr = io.StringIO()
        with mock.patch.dict(tool.os.environ, {"RELEASE_VERSION": "1.2.3\nx=y"}, clear=True), \
                mock.patch.object(tool, "GitHub") as api, mock.patch.object(tool.subprocess, "run") as run, \
                contextlib.redirect_stderr(stderr):
            self.assertEqual(tool.main(["resolve"]), 1)
        api.assert_not_called()
        run.assert_not_called()
        self.assertIn("Version must be canonical", stderr.getvalue())
        self.assertNotIn("\nx=y", stderr.getvalue())


class PayloadTests(SyntheticFixture):
    def test_synthetic_roundtrip_binds_all_eleven_package_pairs_and_eight_native_cells(self) -> None:
        self.stage()
        archive = self.archive()
        destination = self.root / "verified"
        tool.extract_verified(archive, destination, self.selection(archive), self.ids)
        document = tool.verify_payload(destination, self.identity, self.hashes, self.ids)
        self.assertEqual(document["identity"], self.identity)
        self.assertEqual(document["qualification"], tool.GATES)
        self.assertEqual(len([entry for entry in document["files"] if entry["name"].endswith(".nupkg")]), 11)
        self.assertEqual(len([entry for entry in document["files"] if entry["name"].endswith(".snupkg")]), 11)
        self.assertEqual(len([entry for entry in document["files"] if entry["name"].startswith("native-")]), 8)
        for original in self.packages.iterdir():
            self.assertEqual((destination / original.name).read_bytes(), original.read_bytes())

    def test_missing_extra_and_nested_package_outputs_cannot_be_staged(self) -> None:
        original = next(self.packages.iterdir())
        raw = original.read_bytes()
        for operation in ("missing", "extra", "directory"):
            with self.subTest(operation=operation):
                if operation == "missing":
                    original.unlink()
                    added = None
                else:
                    added = self.packages / ("extra.nupkg" if operation != "directory" else "nested")
                    if operation == "directory":
                        added.mkdir()
                    else:
                        added.write_bytes(raw)
                with self.assertRaisesRegex(tool.ReleaseError, "package output"):
                    self.stage()
                self.assertFalse(self.payload.exists())
                if added is not None:
                    added.rmdir() if added.is_dir() else added.unlink()
                else:
                    original.write_bytes(raw)

    def test_planned_packages_and_samples_block_assembly(self) -> None:
        path = self.root / "eng" / "packages.json"
        for collection in ("packages", "samples"):
            for status in ("planned", "implemented"):
                source = copy.deepcopy(self.source)
                source[collection][0]["status"] = status
                path.write_bytes(tool.encoded(source))
                with self.subTest(collection=collection, status=status), self.assertRaisesRegex(tool.ReleaseError, "not qualified"):
                    self.stage()
                self.assertFalse(self.payload.exists())

    def test_package_and_sample_count_framework_and_rid_contracts_are_preserved(self) -> None:
        for field in ("packages", "samples", "targetFrameworks", "nativeRuntimeIdentifiers"):
            source = copy.deepcopy(self.source)
            source[field].pop()
            path = self.root / "eng" / "packages.json"
            path.write_bytes(tool.encoded(source))
            with self.subTest(field=field), self.assertRaises(ERRORS):
                tool.package_ids(path, self.root, qualified=True)
        source = copy.deepcopy(self.source)
        source["samples"][0]["targetFramework"] = "net8.0"
        path.write_bytes(tool.encoded(source))
        with self.assertRaisesRegex(tool.inventory.InventoryError, "net10.0"):
            tool.package_ids(path, self.root, qualified=True)

    def test_changed_bytes_anywhere_in_inventory_fail_before_promotion(self) -> None:
        self.stage()
        for name in (f"{self.ids[-1]}.{self.identity['version']}.nupkg", f"{self.ids[-1]}.{self.identity['version']}.snupkg", "source-packages.json"):
            path = self.payload / name
            original = path.read_bytes()
            path.write_bytes(original + b"x")
            with self.subTest(name=name), self.assertRaisesRegex(tool.ReleaseError, "Changed release bytes"):
                self.verify()
            path.write_bytes(original)

    def test_missing_extra_duplicate_and_case_colliding_manifest_files_are_rejected(self) -> None:
        self.stage()
        original = self.read_manifest()
        for mutation in ("missing", "extra", "duplicate", "case", "self"):
            manifest = copy.deepcopy(original)
            if mutation == "missing":
                manifest["files"].pop()
            elif mutation == "extra":
                manifest["files"].append({"name": "surprise.nupkg", "size": 1, "sha256": "1" * 64})
            else:
                entry = copy.deepcopy(manifest["files"][0])
                if mutation == "case":
                    entry["name"] = entry["name"].upper()
                elif mutation == "self":
                    entry["name"] = tool.MANIFEST
                manifest["files"].append(entry)
            self.write_manifest(manifest)
            with self.subTest(mutation=mutation), self.assertRaises(ERRORS):
                self.verify()

    def test_manifest_requires_exact_schema_fields_gate_claims_and_hash_types(self) -> None:
        self.stage()
        original = self.read_manifest()
        mutations = [
            lambda value: value.update(schemaVersion=True),
            lambda value: value.update(schemaVersion=2),
            lambda value: value.update(qualification={}),
            lambda value: value["qualification"].update(packages="python check_packages.py --evaluate"),
            lambda value: value.update(extra="ignored?"),
            lambda value: value["files"][0].update(size=True),
            lambda value: value["files"][0].update(size=0),
            lambda value: value["files"][0].update(sha256="A" * 64),
        ]
        for index, mutate in enumerate(mutations):
            manifest = copy.deepcopy(original)
            mutate(manifest)
            self.write_manifest(manifest)
            with self.subTest(index=index), self.assertRaises(ERRORS):
                self.verify()

    def test_wrong_source_tag_commit_repository_workflow_run_and_attempt_are_rejected(self) -> None:
        self.stage()
        original = self.read_manifest()
        for key, value in (
            ("version", "0.1.0-alpha.2"), ("tag", "v0.1.0-alpha.2"), ("commit", "9" * 40),
            ("tagObject", "9" * 40), ("repository", "attacker/xregistry-dotnet"),
            ("repositoryId", 8), ("workflow", ".github/workflows/ci.yml"),
            ("runId", 102), ("runAttempt", 2), ("runId", True),
        ):
            manifest = copy.deepcopy(original)
            manifest["identity"][key] = value
            self.write_manifest(manifest)
            with self.subTest(key=key, value=value), self.assertRaises(ERRORS):
                self.verify()

    def test_source_files_must_match_exact_tag_bytes_not_just_self_consistent_hashes(self) -> None:
        self.stage()
        self.replace_payload_file("source-packages.json", tool.encoded(self.source) + b" ")
        with self.assertRaisesRegex(tool.ReleaseError, "exact tag commit"):
            self.verify()

    def test_expected_package_ids_are_taken_from_trusted_inventory(self) -> None:
        self.stage()
        source = copy.deepcopy(self.source)
        source["packages"][-1].update(id="XRegistry.Unapproved", project="src\\XRegistry.Unapproved\\XRegistry.Unapproved.csproj")
        self.replace_payload_file("source-packages.json", tool.encoded(source), source=True)
        with self.assertRaisesRegex(tool.ReleaseError, "trusted promotion inventory"):
            self.verify()

    def test_correctly_hashed_nuspec_wrong_id_version_source_or_tag_is_rejected(self) -> None:
        self.stage()
        package_id = self.ids[0]
        for field, value in (
            ("id", "XRegistry.Other"), ("version", "0.1.0-alpha.2"), ("commit", "9" * 40),
            ("url", "https://github.com/attacker/repo"), ("branch", "refs/heads/main"),
        ):
            for suffix in ("nupkg", "snupkg"):
                name = f"{package_id}.{self.identity['version']}.{suffix}"
                original = (self.payload / name).read_bytes()
                self.replace_payload_file(name, synthetic_package(package_id, self.identity, suffix, **{field: value}))
                with self.subTest(field=field, suffix=suffix), self.assertRaisesRegex(tool.ReleaseError, "mismatch"):
                    self.verify()
                self.replace_payload_file(name, original)

    def test_nuspec_entities_duplicate_metadata_and_missing_framework_are_rejected(self) -> None:
        package_id = self.ids[0]
        path = self.packages / f"{package_id}.{self.identity['version']}.nupkg"
        original = path.read_bytes()
        for mutation in ("entity", "duplicate-id", "missing-tfm", "nested-nuspec", "traversal", "namespace"):
            stream = io.BytesIO()
            with zipfile.ZipFile(io.BytesIO(original)) as source, zipfile.ZipFile(stream, "w") as target:
                for entry in source.infolist():
                    raw = source.read(entry)
                    name = entry.filename
                    if mutation == "missing-tfm" and "net10.0" in name:
                        continue
                    if name.endswith(".nuspec"):
                        if mutation == "entity":
                            raw = raw.replace(b"<package ", b'<!DOCTYPE package [<!ENTITY x "expanded">]><package ')
                        elif mutation == "duplicate-id":
                            raw = raw.replace(b"<metadata>", b"<metadata><id>other</id>")
                        elif mutation == "nested-nuspec":
                            name = "nested/" + name
                        elif mutation == "namespace":
                            raw = raw.replace(b"<repository ", b'<repository xmlns="urn:untrusted" ')
                    target.writestr(name, raw)
                if mutation == "traversal":
                    target.writestr("../escape", b"x")
            path.write_bytes(stream.getvalue())
            with self.subTest(mutation=mutation), self.assertRaises(ERRORS):
                tool.verify_package(path, package_id, self.identity)

    def test_unreviewed_unqualified_empty_informative_only_and_incomplete_native_evidence_fail(self) -> None:
        self.stage()
        mutations = [
            lambda value: value.update(semanticCoverageReviewed=False),
            lambda value: value.update(requirements=[]),
            lambda value: value["requirements"][0]["review"].update(status="unreviewed"),
            lambda value: value["requirements"][0]["implementation"].update(status="implemented"),
            lambda value: value["requirements"][0]["implementation"]["nativeEvidence"].pop(),
            lambda value: value["requirements"].append(copy.deepcopy(value["requirements"][0])),
        ]
        for index, mutate in enumerate(mutations):
            ledger = copy.deepcopy(self.ledger)
            mutate(ledger)
            self.replace_ledger(ledger)
            with self.subTest(index=index), self.assertRaises(ERRORS):
                self.verify()
        ledger = copy.deepcopy(self.ledger)
        ledger["requirements"][0]["review"].update(status="informative", roles=[])
        ledger["requirements"][0]["implementation"].update(status="pending", testIds=[], nativeEvidence=[])
        self.replace_ledger(ledger)
        with self.assertRaisesRegex(tool.ReleaseError, "No applicable"):
            self.verify()

    def test_duplicate_native_cells_and_unsafe_report_paths_are_rejected(self) -> None:
        self.stage()
        for relative in ("../outside.json", "C:/outside.json", "//server/share.json", "artifacts/CON.json", "artifacts/a.json:stream"):
            ledger = copy.deepcopy(self.ledger)
            ledger["requirements"][0]["implementation"]["nativeEvidence"][0]["report"] = relative
            self.replace_ledger(ledger)
            with self.subTest(relative=relative), self.assertRaises(ERRORS):
                self.verify()
        ledger = copy.deepcopy(self.ledger)
        evidence = ledger["requirements"][0]["implementation"]["nativeEvidence"]
        evidence[-1] = copy.deepcopy(evidence[0])
        self.replace_ledger(ledger)
        with self.assertRaisesRegex(tool.ReleaseError, "Duplicate native cell"):
            self.verify()
        self.assertFalse((self.root / "outside.json").exists())

    def test_missing_native_report_is_not_replaced_with_a_receipt(self) -> None:
        self.stage()
        path = next(self.payload.glob("native-*.json"))
        path.unlink()
        manifest = self.read_manifest()
        manifest["files"] = [entry for entry in manifest["files"] if entry["name"] != path.name]
        self.write_manifest(manifest)
        with self.assertRaisesRegex(tool.ReleaseError, "Required regular file"):
            self.verify()
        self.assertFalse(path.exists())

    def test_hash_valid_receipts_still_require_real_execution_shape_and_complete_test_ids(self) -> None:
        self.stage()
        original_manifest = self.read_manifest()
        item = self.ledger["requirements"][0]["implementation"]["nativeEvidence"][0]
        old_name = f"native-{item['sha256']}.json"
        original_raw = (self.payload / old_name).read_bytes()
        for key, value in (
            ("nativeExecutable", False), ("framework", "net9.0"), ("rid", "linux-s390x"),
            ("schemaVersion", True), ("passedTestIds", []), ("failedTestIds", ["failed"]),
            ("skippedTestIds", ["skipped"]),
        ):
            receipt = json.loads(original_raw)
            receipt[key] = value
            raw = tool.encoded(receipt)
            hashed = tool.sha256(raw)
            name = f"native-{hashed}.json"
            (self.payload / old_name).unlink()
            (self.payload / name).write_bytes(raw)
            manifest = copy.deepcopy(original_manifest)
            for entry in manifest["files"]:
                if entry["name"] == old_name:
                    entry.update(name=name, size=len(raw), sha256=hashed)
            self.write_manifest(manifest)
            ledger = copy.deepcopy(self.ledger)
            ledger["requirements"][0]["implementation"]["nativeEvidence"][0]["sha256"] = hashed
            self.replace_ledger(ledger)
            with self.subTest(key=key), self.assertRaisesRegex(tool.specification.SpecificationError, "receipt is invalid"):
                self.verify()
            (self.payload / name).unlink()
            (self.payload / old_name).write_bytes(original_raw)
            self.write_manifest(original_manifest)

    def test_changed_archive_hash_or_size_is_rejected_without_extraction(self) -> None:
        self.stage()
        archive = self.archive()
        for field, value in (("sha256", "9" * 64), ("size", archive.stat().st_size + 1)):
            selection = self.selection(archive)
            selection["artifact"][field] = value
            with self.subTest(field=field), self.assertRaisesRegex(tool.ReleaseError, "archive size/hash"):
                tool.extract_verified(archive, self.root / "verified", selection, self.ids)
            self.assertFalse((self.root / "verified").exists())

    def test_zip_traversal_absolute_windows_ads_reserved_and_nested_names_never_extract(self) -> None:
        self.stage()
        for index, name in enumerate((
            "../escape.txt", "/escape.txt", "C:/escape.txt", "\\\\server\\share\\evil",
            "..\\escape.txt", "directory/file.nupkg", "bad:stream", "CON", "aux.json",
            "trailing.", "trailing ", "a//b", "./x", "non-ascii-\u00e9.json",
        )):
            archive = self.archive(f"hostile-{index}.zip", [(name, b"not safe")])
            destination = self.root / "verified"
            with self.subTest(name=name), self.assertRaisesRegex(tool.ReleaseError, "Unsafe file path"):
                tool.extract_verified(archive, destination, self.selection(archive), self.ids)
            self.assertFalse(destination.exists())
        self.assertFalse((self.root.parent / "escape.txt").exists())

    def test_zip_duplicate_case_collision_extra_file_and_manifest_absence_are_rejected(self) -> None:
        self.stage()
        name = next(self.packages.iterdir()).name
        for index, member in enumerate((name, name.upper(), "unlisted.nupkg")):
            archive = self.archive(f"duplicate-{index}.zip", [(member, b"duplicate")])
            with self.subTest(member=member), self.assertRaises(ERRORS):
                tool.extract_verified(archive, self.root / "verified", self.selection(archive), self.ids)
            self.assertFalse((self.root / "verified").exists())
        (self.payload / tool.MANIFEST).unlink()
        archive = self.archive("no-manifest.zip")
        with self.assertRaisesRegex(tool.ReleaseError, "Authoritative manifest"):
            tool.extract_verified(archive, self.root / "verified", self.selection(archive), self.ids)

    def test_zip_symlinks_special_files_encryption_nul_and_resource_limits(self) -> None:
        entry = zipfile.ZipInfo("file")
        entry.file_size = 1
        archive = mock.Mock()
        mutations = [
            lambda item: setattr(item, "external_attr", (stat.S_IFLNK | 0o777) << 16),
            lambda item: setattr(item, "external_attr", (stat.S_IFIFO | 0o600) << 16),
            lambda item: setattr(item, "external_attr", 0x400),
            lambda item: setattr(item, "flag_bits", 1),
            lambda item: setattr(item, "orig_filename", "file\x00evil"),
            lambda item: setattr(item, "file_size", tool.MAX_FILE + 1),
            lambda item: setattr(item, "compress_type", zipfile.ZIP_BZIP2),
        ]
        for index, mutate in enumerate(mutations):
            bad = copy.copy(entry)
            mutate(bad)
            archive.infolist.return_value = [bad]
            with self.subTest(index=index), self.assertRaises(ERRORS):
                tool.zip_members(archive, flat=True)
        archive.infolist.return_value = [entry]
        with mock.patch.object(tool, "MAX_FILE", 1), mock.patch.object(tool, "MAX_ARCHIVE", 1):
            self.assertEqual(set(tool.zip_members(archive, flat=True)), {"file"})
        second = copy.copy(entry)
        second.filename = second.orig_filename = "second"
        archive.infolist.return_value = [entry, second]
        with mock.patch.object(tool, "MAX_ARCHIVE", 1), self.assertRaisesRegex(tool.ReleaseError, "expanded byte"):
            tool.zip_members(archive, flat=True)
        with mock.patch.object(tool, "MAX_FILES", 1), self.assertRaisesRegex(tool.ReleaseError, "entry count"):
            tool.zip_members(archive, flat=True)

    def test_real_zip_links_directories_and_nul_names_never_create_a_payload(self) -> None:
        self.stage()
        for mutation in ("symlink", "directory", "nul"):
            archive = self.archive(f"entry-{mutation}.zip")
            info = zipfile.ZipInfo("directory/" if mutation == "directory" else "BADX")
            if mutation == "symlink":
                info.create_system = 3
                info.external_attr = (stat.S_IFLNK | 0o777) << 16
            with zipfile.ZipFile(archive, "a") as output:
                output.writestr(info, b"target" if mutation != "directory" else b"")
            if mutation == "nul":
                archive.write_bytes(archive.read_bytes().replace(b"BADX", b"BAD\x00"))
            with self.subTest(mutation=mutation), self.assertRaises(ERRORS):
                tool.extract_verified(archive, self.root / "verified", self.selection(archive), self.ids)
            self.assertFalse((self.root / "verified").exists())

    def test_failed_verification_leaves_no_payload_and_existing_destination_is_preserved(self) -> None:
        self.stage()
        name = next(self.packages.iterdir()).name
        (self.payload / name).write_bytes(b"changed")
        archive = self.archive()
        destination = self.root / "verified"
        with self.assertRaises(ERRORS):
            tool.extract_verified(archive, destination, self.selection(archive), self.ids)
        self.assertFalse(destination.exists())
        self.assertEqual(list(self.root.glob("release-extract-*")), [])
        destination.mkdir()
        sentinel = destination / "keep"
        sentinel.write_bytes(b"existing user data")
        with self.assertRaisesRegex(tool.ReleaseError, "already exists"):
            tool.extract_verified(archive, destination, self.selection(archive), self.ids)
        self.assertEqual(sentinel.read_bytes(), b"existing user data")

    def test_filesystem_links_are_rejected_before_reading_or_writing(self) -> None:
        with mock.patch.object(Path, "is_symlink", return_value=True):
            with self.assertRaisesRegex(tool.ReleaseError, "Links are forbidden"):
                tool.read_bytes(next(self.packages.iterdir()))
            with self.assertRaisesRegex(tool.ReleaseError, "Links are forbidden"):
                self.stage()


class FakeGitHub(tool.GitHub):
    def __init__(self, fixture: SyntheticFixture):
        self.fixture = fixture
        self.calls: list[str] = []
        self.source_calls: list[tuple[str, str]] = []
        identity = fixture.identity
        self.repository = {"id": identity["repositoryId"], "full_name": tool.REPOSITORY, "fork": False}
        self.tag = {"ref": f"refs/tags/{identity['tag']}", "object": {"type": "tag", "sha": identity["tagObject"]}}
        self.tags = {identity["tagObject"]: {"sha": identity["tagObject"], "object": {"type": "commit", "sha": identity["commit"]}}}
        self.workflow = {"id": 88, "path": tool.WORKFLOW, "state": "active"}
        self.runs = [{
            "id": identity["runId"], "run_attempt": identity["runAttempt"], "head_branch": identity["tag"],
            "head_sha": identity["commit"], "event": "push", "status": "completed", "conclusion": "success",
            "path": tool.WORKFLOW, "workflow_id": 88, "pull_requests": [],
            "repository": copy.deepcopy(self.repository), "head_repository": copy.deepcopy(self.repository),
        }]
        self.artifacts = [{
            "id": 900, "name": tool.artifact_name(identity), "size_in_bytes": 123, "digest": "sha256:" + "4" * 64,
            "expired": False, "workflow_run": {
                "id": identity["runId"], "head_sha": identity["commit"], "head_branch": identity["tag"],
                "repository_id": identity["repositoryId"], "head_repository_id": identity["repositoryId"],
            },
        }]
        self.reviews = [{
            "state": "approved", "environments": [{"id": 44, "name": "release"}],
            "user": {"id": 11168470, "login": "marcschier", "type": "User"},
        }]

    def get(self, endpoint: str):
        self.calls.append(endpoint)
        prefix = f"repos/{tool.REPOSITORY}"
        if endpoint == prefix:
            return copy.deepcopy(self.repository)
        if endpoint == f"{prefix}/git/ref/tags/{self.fixture.identity['tag']}":
            return copy.deepcopy(self.tag)
        if endpoint.startswith(f"{prefix}/git/tags/"):
            return copy.deepcopy(self.tags[endpoint.rsplit("/", 1)[1]])
        if endpoint == f"{prefix}/actions/workflows/release.yml":
            return copy.deepcopy(self.workflow)
        if endpoint.startswith(f"{prefix}/actions/workflows/88/runs?"):
            return {"total_count": len(self.runs), "workflow_runs": copy.deepcopy(self.runs)}
        if endpoint.startswith(f"{prefix}/actions/runs/101/artifacts?"):
            return {"total_count": len(self.artifacts), "artifacts": copy.deepcopy(self.artifacts)}
        if endpoint == f"{prefix}/actions/runs/202/approvals":
            return copy.deepcopy(self.reviews)
        raise AssertionError(f"Unexpected test API call: {endpoint}")

    def source(self, commit: str, relative: str) -> bytes:
        self.source_calls.append((commit, relative))
        return tool.source_path(self.fixture.root, relative).read_bytes()


class ResolutionTests(SyntheticFixture):
    def test_resolution_pins_annotated_tag_commit_one_run_attempt_artifact_and_exact_source_files(self) -> None:
        api = FakeGitHub(self)
        selection = tool.resolve(api, self.identity["version"])
        self.assertEqual(selection["identity"], self.identity)
        self.assertEqual(selection["artifact"]["id"], 900)
        self.assertEqual(selection["artifact"]["sha256"], "4" * 64)
        self.assertEqual(selection["sourceSha256"], self.hashes)
        self.assertEqual(api.source_calls, [(self.identity["commit"], path) for path in tool.SOURCE_FILES.values()])
        self.assertIn(f"repos/{tool.REPOSITORY}/actions/workflows/88/runs?event=push&status=success&head_sha={self.identity['commit']}&exclude_pull_requests=true&per_page=100&page=1", api.calls)

    def test_lightweight_tag_is_bound_without_guessing_an_annotated_object(self) -> None:
        api = FakeGitHub(self)
        api.tag["object"] = {"type": "commit", "sha": self.identity["commit"]}
        selection = tool.resolve(api, self.identity["version"])
        self.assertEqual(selection["identity"]["tagObject"], self.identity["commit"])
        self.assertFalse(any("/git/tags/" in call for call in api.calls))

    def test_tag_cycles_wrong_ref_noncommit_targets_and_nesting_limit_fail(self) -> None:
        for mutation in ("cycle", "wrong-ref", "tree", "blob", "wrong-object", "depth"):
            api = FakeGitHub(self)
            tag_object = self.identity["tagObject"]
            if mutation == "cycle":
                api.tags[tag_object]["object"] = api.tag["object"]
            elif mutation == "wrong-ref":
                api.tag["ref"] += "-other"
            elif mutation in ("tree", "blob"):
                api.tag["object"]["type"] = mutation
            elif mutation == "wrong-object":
                api.tags[tag_object]["sha"] = "9" * 40
            else:
                for index in range(9):
                    current = tag_object if index == 0 else f"{index:040x}"
                    following = f"{index + 1:040x}"
                    api.tags[current] = {"sha": current, "object": {"type": "tag", "sha": following}}
            with self.subTest(mutation=mutation), self.assertRaises(ERRORS):
                tool.resolve_tag(api, self.identity["version"])

    def test_no_latest_fallback_when_exact_run_is_missing_or_ambiguous(self) -> None:
        for mutation in ("none", "another-tag", "two-successes"):
            api = FakeGitHub(self)
            if mutation == "none":
                api.runs = []
            elif mutation == "another-tag":
                api.runs[0]["head_branch"] = "v0.9.0"
            else:
                api.runs.append({**copy.deepcopy(api.runs[0]), "id": 102})
            with self.subTest(mutation=mutation), self.assertRaisesRegex(tool.ReleaseError, "exactly one successful release run"):
                tool.resolve(api, self.identity["version"])
            self.assertFalse(any("/artifacts" in call for call in api.calls))

    def test_failed_incomplete_pr_wrong_workflow_and_wrong_source_runs_never_qualify(self) -> None:
        for field, value in (
            ("event", "pull_request"), ("status", "in_progress"), ("conclusion", "failure"),
            ("conclusion", "neutral"), ("path", ".github/workflows/ci.yml"), ("workflow_id", 89),
            ("head_sha", "9" * 40), ("pull_requests", [{"id": 1}]), ("run_attempt", True),
            ("repository", {"id": 8, "full_name": tool.REPOSITORY}),
            ("head_repository", {"id": 7, "full_name": "attacker/fork"}),
        ):
            api = FakeGitHub(self)
            api.runs[0][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(ERRORS):
                tool.resolve(api, self.identity["version"])

    def test_missing_expired_duplicate_unhashed_and_wrong_attempt_artifacts_fail(self) -> None:
        for mutation in ("none", "duplicate", "expired", "no-digest", "sha512", "wrong-name", "wrong-run", "wrong-commit", "wrong-tag", "fork", "zero-size", "too-large"):
            api = FakeGitHub(self)
            artifact = api.artifacts[0]
            if mutation == "none":
                api.artifacts = []
            elif mutation == "duplicate":
                api.artifacts.append({**copy.deepcopy(artifact), "id": 901})
            elif mutation == "expired":
                artifact["expired"] = True
            elif mutation == "no-digest":
                artifact["digest"] = None
            elif mutation == "sha512":
                artifact["digest"] = "sha512:" + "4" * 64
            elif mutation == "wrong-name":
                artifact["name"] = artifact["name"][:-1] + "2"
            elif mutation == "wrong-run":
                artifact["workflow_run"]["id"] = 102
            elif mutation == "wrong-commit":
                artifact["workflow_run"]["head_sha"] = "9" * 40
            elif mutation == "wrong-tag":
                artifact["workflow_run"]["head_branch"] = "v0.9.0"
            elif mutation == "fork":
                artifact["workflow_run"]["head_repository_id"] = 8
            else:
                artifact["size_in_bytes"] = 0 if mutation == "zero-size" else tool.MAX_ARCHIVE + 1
            with self.subTest(mutation=mutation), self.assertRaises(ERRORS):
                tool.resolve(api, self.identity["version"])

    def test_repository_and_workflow_identity_are_not_inferred_from_display_names(self) -> None:
        for target, field, value in (
            ("repository", "fork", True), ("repository", "full_name", "attacker/repo"),
            ("repository", "id", True), ("workflow", "path", ".github/workflows/ci.yml"),
            ("workflow", "state", "disabled_manually"), ("workflow", "id", 0),
        ):
            api = FakeGitHub(self)
            getattr(api, target)[field] = value
            with self.subTest(target=target, field=field), self.assertRaises(ERRORS):
                tool.resolve(api, self.identity["version"])

    def test_pagination_reads_all_results_and_rejects_truncation_changes_duplicates_and_caps(self) -> None:
        api = tool.GitHub()
        endpoint = f"repos/{tool.REPOSITORY}/actions/runs"
        first = {"total_count": 101, "workflow_runs": [{"id": index} for index in range(1, 101)]}
        last = {"total_count": 101, "workflow_runs": [{"id": 101}]}
        with mock.patch.object(api, "get", side_effect=[first, last]) as get:
            self.assertEqual(len(api.pages(endpoint, "workflow_runs")), 101)
        self.assertEqual(get.call_args_list[1].args[0], endpoint + "?per_page=100&page=2")
        for pages in (
            [{"total_count": 1001, "workflow_runs": []}],
            [{"total_count": 1, "workflow_runs": []}],
            [first, {"total_count": 102, "workflow_runs": [{"id": 101}]}],
            [first, {"total_count": 101, "workflow_runs": [{"id": 1}]}],
            [{"total_count": True, "workflow_runs": []}],
        ):
            with self.subTest(pages=len(pages)), mock.patch.object(api, "get", side_effect=pages), self.assertRaises(ERRORS):
                api.pages(endpoint, "workflow_runs")

    def test_api_client_uses_github_get_only_and_failures_have_no_fallback(self) -> None:
        api = tool.GitHub()
        with mock.patch.object(tool, "checked", return_value=b'{"id":7}') as command:
            self.assertEqual(api.get(f"repos/{tool.REPOSITORY}"), {"id": 7})
        arguments = command.call_args.args[0]
        self.assertEqual(arguments[:6], ["gh", "api", "--hostname", "github.com", "--method", "GET"])
        with self.assertRaisesRegex(tool.ReleaseError, "Unexpected GitHub"):
            api.command("https://attacker.invalid/")
        with mock.patch.object(tool, "checked", side_effect=tool.ReleaseError("GitHub unavailable")), self.assertRaisesRegex(tool.ReleaseError, "unavailable"):
            tool.resolve(api, self.identity["version"])

    def test_download_uses_exact_artifact_id_and_rejects_missing_truncated_or_failed_content(self) -> None:
        self.stage()
        archive = self.archive()
        selection = self.selection(archive)
        api = tool.GitHub()
        destination = self.root / "download.zip"

        def download(arguments, **kwargs):
            kwargs["stdout"].write(archive.read_bytes())
            return subprocess.CompletedProcess(arguments, 0, None, b"")

        with mock.patch.object(tool.subprocess, "run", side_effect=download) as run:
            api.download(selection, destination)
        self.assertEqual(destination.read_bytes(), archive.read_bytes())
        self.assertEqual(run.call_args.args[0][-1], f"repos/{tool.REPOSITORY}/actions/artifacts/900/zip")
        self.assertNotIn("shell", run.call_args.kwargs)
        for result in (
            subprocess.CompletedProcess([], 0, None, b""),
            subprocess.CompletedProcess([], 1, None, b"error"),
            subprocess.TimeoutExpired(["gh"], 180),
        ):
            destination.unlink()
            patch = {"side_effect": result} if isinstance(result, Exception) else {"return_value": result}
            with self.subTest(result=type(result).__name__), mock.patch.object(tool.subprocess, "run", **patch), self.assertRaises(ERRORS):
                api.download(selection, destination)

    def test_attestation_verification_enforces_signer_source_ref_commit_and_hosted_runner(self) -> None:
        self.stage()
        archive = self.archive()
        selection = self.selection(archive)
        with mock.patch.object(tool, "checked", return_value=b"") as run:
            tool.verify_attestation(archive, selection)
        arguments = run.call_args.args[0]
        for flag, value in (
            ("--repo", tool.REPOSITORY), ("--source-ref", f"refs/tags/{self.identity['tag']}"),
            ("--source-digest", self.identity["commit"]), ("--signer-digest", self.identity["commit"]),
            ("--signer-workflow", f"{tool.REPOSITORY}/{tool.WORKFLOW}"),
            ("--cert-identity", f"{tool.REPOSITORY_URL}/{tool.WORKFLOW}@refs/tags/{self.identity['tag']}"),
            ("--predicate-type", "https://slsa.dev/provenance/v1"),
        ):
            self.assertEqual(arguments[arguments.index(flag) + 1], value)
        self.assertIn("--deny-self-hosted-runners", arguments)
        with mock.patch.object(tool, "checked", side_effect=tool.ReleaseError("No verified attestation")), self.assertRaisesRegex(tool.ReleaseError, "No verified"):
            tool.verify_attestation(archive, selection)

    def test_approval_requires_the_designated_maintainer_and_real_environment_history(self) -> None:
        environment = self.promotion_environment()
        tool.require_approval(FakeGitHub(self), environment)
        for mutation in ("absent", "rejected", "pending", "other-environment", "duplicate", "self", "same-id", "bot"):
            api = FakeGitHub(self)
            if mutation == "absent":
                api.reviews = []
            elif mutation in ("rejected", "pending"):
                api.reviews[0]["state"] = mutation
            elif mutation == "other-environment":
                api.reviews[0]["environments"][0]["name"] = "unprotected"
            elif mutation == "duplicate":
                api.reviews.append(copy.deepcopy(api.reviews[0]))
            elif mutation == "self":
                api.reviews[0]["user"]["login"] = "Dispatcher"
            elif mutation == "same-id":
                api.reviews[0]["user"]["id"] = 42
            else:
                api.reviews[0]["user"]["type"] = "Bot"
            with self.subTest(mutation=mutation), self.assertRaises(ERRORS):
                tool.require_approval(api, environment)
        environment["GITHUB_RUN_ATTEMPT"] = "2"
        with self.assertRaisesRegex(tool.ReleaseError, "fresh dispatch"):
            tool.require_approval(FakeGitHub(self), environment)

    def test_designated_maintainer_may_dispatch_and_explicitly_approve(self) -> None:
        environment = self.promotion_environment()
        environment.update(GITHUB_ACTOR="marcschier", GITHUB_TRIGGERING_ACTOR="marcschier", GITHUB_ACTOR_ID="11168470")
        tool.require_approval(FakeGitHub(self), environment)

    def test_changed_selection_after_approval_is_rejected_before_download_or_oidc(self) -> None:
        environment = self.promotion_environment()
        original = tool.resolve(FakeGitHub(self), self.identity["version"])
        for field in ("commit", "tagObject", "runId", "runAttempt"):
            api = FakeGitHub(self)
            changed = copy.deepcopy(original)
            changed["identity"][field] = "9" * 40 if field in ("commit", "tagObject") else 2
            if field != "tagObject":
                changed["artifact"]["name"] = tool.artifact_name(changed["identity"])
            environment["EXPECTED_SELECTION"] = json.dumps(changed)
            with self.subTest(field=field), mock.patch.object(tool, "GitHub", return_value=api), \
                    mock.patch.object(tool, "clean_source"), mock.patch.object(api, "download") as download, \
                    self.assertRaisesRegex(tool.ReleaseError, "changed after"):
                tool.prepare(self.root, environment, approved=True)
            download.assert_not_called()
        self.assertFalse((self.root / "xregistry-release-promotion").exists())

    def test_changed_artifact_id_digest_size_or_source_hash_cannot_cross_approval_boundary(self) -> None:
        environment = self.promotion_environment()
        original = tool.resolve(FakeGitHub(self), self.identity["version"])
        for field in ("id", "sha256", "size", "source"):
            changed = copy.deepcopy(original)
            if field == "source":
                changed["sourceSha256"]["source-packages.json"] = "9" * 64
            else:
                changed["artifact"][field] = "9" * 64 if field == "sha256" else changed["artifact"][field] + 1
            environment["EXPECTED_SELECTION"] = json.dumps(changed)
            api = FakeGitHub(self)
            with self.subTest(field=field), mock.patch.object(tool, "GitHub", return_value=api), \
                    mock.patch.object(tool, "clean_source"), mock.patch.object(api, "download") as download, \
                    self.assertRaisesRegex(tool.ReleaseError, "changed after"):
                tool.prepare(self.root, environment, approved=True)
            download.assert_not_called()

    def test_absent_selection_approval_or_username_stops_approved_preparation(self) -> None:
        environment = self.promotion_environment()
        selection = tool.resolve(FakeGitHub(self), self.identity["version"])
        for mutation in ("selection", "approval", "username"):
            current = {**environment, "EXPECTED_SELECTION": json.dumps(selection)}
            api = FakeGitHub(self)
            if mutation == "selection":
                current.pop("EXPECTED_SELECTION")
            elif mutation == "approval":
                api.reviews = []
            else:
                current["NUGET_USER"] = ""
            with self.subTest(mutation=mutation), mock.patch.object(tool, "GitHub", return_value=api), \
                    mock.patch.object(tool, "clean_source"), mock.patch.object(api, "download") as download, \
                    self.assertRaises(ERRORS):
                tool.prepare(self.root, current, approved=True)
            download.assert_not_called()

    def test_synthetic_preapproval_and_approved_preparation_verify_before_emitting_exact_selection(self) -> None:
        self.stage()
        archive = self.archive()
        for approved in (False, True):
            api = FakeGitHub(self)
            api.artifacts[0].update(size_in_bytes=archive.stat().st_size, digest=f"sha256:{tool.file_hash(archive)}")
            selection = tool.resolve(api, self.identity["version"])
            environment = self.promotion_environment()
            runner = self.root / f"runner-{approved}"
            runner.mkdir()
            environment["RUNNER_TEMP"] = str(runner)
            environment["GITHUB_OUTPUT"] = str(runner / "outputs")
            environment["GITHUB_STEP_SUMMARY"] = str(runner / "summary")
            if approved:
                environment["EXPECTED_SELECTION"] = json.dumps(selection)
            events = []

            def download(selected, destination):
                events.append("download")
                self.assertEqual(selected, selection)
                shutil.copyfile(archive, destination)

            def attest(arguments, **kwargs):
                events.append("attest")
                self.assertEqual(arguments[:3], ["gh", "attestation", "verify"])
                self.assertFalse(Path(environment["GITHUB_OUTPUT"]).exists())
                return b""

            with self.subTest(approved=approved), mock.patch.object(tool, "GitHub", return_value=api), \
                    mock.patch.object(tool, "clean_source"), mock.patch.object(api, "download", side_effect=download), \
                    mock.patch.object(tool, "checked", side_effect=attest):
                tool.prepare(self.root, environment, approved=approved)
            output = Path(environment["GITHUB_OUTPUT"]).read_text()
            self.assertEqual(output.count("\n"), 1)
            self.assertEqual(json.loads(output.removeprefix("selection=")), selection)
            self.assertEqual(events, ["download", "attest"])
            payload = runner / "xregistry-release-promotion" / "payload"
            self.assertEqual(tool.verify_payload(payload, self.identity, self.hashes, self.ids)["identity"], self.identity)
            summary = Path(environment["GITHUB_STEP_SUMMARY"]).read_text()
            self.assertIn(self.identity["commit"], summary)
            self.assertIn("artifact `900`", summary)
            self.assertEqual(summary.count(".nupkg`"), 11)
            self.assertEqual(summary.count(".snupkg`"), 11)

    def test_failed_attestation_cannot_extract_or_emit_a_successful_selection(self) -> None:
        self.stage()
        archive = self.archive()
        api = FakeGitHub(self)
        api.artifacts[0].update(size_in_bytes=archive.stat().st_size, digest=f"sha256:{tool.file_hash(archive)}")
        environment = self.promotion_environment()
        with mock.patch.object(tool, "GitHub", return_value=api), mock.patch.object(tool, "clean_source"), \
                mock.patch.object(api, "download", side_effect=lambda selection, destination: shutil.copyfile(archive, destination)), \
                mock.patch.object(tool, "checked", side_effect=tool.ReleaseError("Attestation verification failed")), \
                self.assertRaisesRegex(tool.ReleaseError, "Attestation verification failed"):
            tool.prepare(self.root, environment, approved=False)
        self.assertFalse((self.root / "xregistry-release-promotion" / "payload").exists())
        self.assertFalse(Path(environment["GITHUB_OUTPUT"]).exists())


class BuildAndPushTests(SyntheticFixture):
    def test_exact_tag_checkout_new_push_and_clean_source_are_required(self) -> None:
        environment = self.promotion_environment()
        ref = f"refs/tags/{self.identity['tag']}"
        environment.update(
            GITHUB_REF=ref, GITHUB_SHA=self.identity["commit"], GITHUB_WORKFLOW_SHA=self.identity["commit"],
            GITHUB_WORKFLOW_REF=f"{tool.REPOSITORY}/{tool.WORKFLOW}@{ref}", GITHUB_EVENT_NAME="push",
            GITHUB_REPOSITORY_ID="7", GITHUB_RUN_ID="101",
            GITHUB_EVENT_PATH=str(self.root / "event.json"),
        )
        event = {"ref": ref, "created": True, "deleted": False, "forced": False}
        Path(environment["GITHUB_EVENT_PATH"]).write_bytes(tool.encoded(event))
        with mock.patch.object(tool, "git", side_effect=[self.identity["commit"], "", self.identity["commit"], self.identity["tagObject"]]):
            self.assertEqual(tool.build_identity(self.root, environment), self.identity)
        for key, value in (("created", False), ("deleted", True), ("forced", True), ("ref", "refs/heads/main")):
            Path(environment["GITHUB_EVENT_PATH"]).write_bytes(tool.encoded({**event, key: value}))
            with self.subTest(key=key), self.assertRaisesRegex(tool.ReleaseError, "new, non-forced"):
                tool.build_identity(self.root, environment)
        with mock.patch.object(tool, "git", side_effect=[self.identity["commit"], "?? unreviewed.cs"]), self.assertRaisesRegex(tool.ReleaseError, "clean source"):
            tool.clean_source(self.root, self.identity["commit"])
        with mock.patch.object(tool, "git", return_value="9" * 40), self.assertRaisesRegex(tool.ReleaseError, "exact source commit"):
            tool.clean_source(self.root, self.identity["commit"])

    def test_dispatch_from_untrusted_branch_repository_pr_or_workflow_is_rejected(self) -> None:
        environment = self.promotion_environment()
        for key, value in (
            ("GITHUB_REF", "refs/heads/attacker"), ("GITHUB_EVENT_NAME", "pull_request_target"),
            ("GITHUB_REPOSITORY", "attacker/fork"), ("GITHUB_SERVER_URL", "https://attacker.invalid"),
            ("GITHUB_WORKFLOW_SHA", "9" * 40), ("GITHUB_WORKFLOW_REF", "other"), ("GITHUB_ACTIONS", "false"),
        ):
            with self.subTest(key=key), self.assertRaisesRegex(tool.ReleaseError, "Untrusted|GitHub Actions"):
                tool.promotion_context(self.root, {**environment, key: value})

    def test_build_runs_existing_release_gates_tests_and_explicit_version_pack_for_all_ids(self) -> None:
        def command(arguments, *args, **kwargs):
            if arguments[:2] == ["git", "show"]:
                return tool.source_path(self.root, arguments[2].split(":", 1)[1]).read_bytes()
            return b""

        with mock.patch.object(tool, "build_identity", return_value=self.identity), \
                mock.patch.object(tool, "clean_source"), mock.patch.object(tool, "checked", side_effect=command) as run, \
                mock.patch.object(tool, "stage_payload") as stage, mock.patch.object(tool, "emit_outputs") as outputs:
            tool.build(self.root, {})
        commands = [call.args[0] for call in run.call_args_list]
        self.assertEqual(commands[0], ["python", str(self.root / "eng" / "check_packages.py"), "--release"])
        self.assertEqual(commands[1], ["python", str(self.root / "eng" / "specification" / "manage.py"), "release"])
        self.assertIn(str(self.root / "eng" / "build.ps1"), commands[2])
        self.assertIn(str(self.root / "eng" / "test.ps1"), commands[3])
        self.assertIn("-NoBuild", commands[3])
        packs = [command for command in commands if command[:2] == ["dotnet", "pack"]]
        self.assertEqual(len(packs), 11)
        self.assertEqual({Path(command[2]).stem for command in packs}, set(self.ids))
        for command in packs:
            for argument in (
                f"-p:PackageVersion={self.identity['version']}", f"-p:Version={self.identity['version']}",
                f"-p:RepositoryCommit={self.identity['commit']}", f"-p:RepositoryBranch=refs/tags/{self.identity['tag']}",
                "--no-restore",
            ):
                self.assertIn(argument, command)
        stage.assert_called_once()
        self.assertEqual(outputs.call_args.args[0]["artifact-name"], tool.artifact_name(self.identity))

    def test_failed_qualification_or_tests_never_produce_a_release_manifest(self) -> None:
        for failing_command in range(4):
            with self.subTest(failing_command=failing_command), \
                    mock.patch.object(tool, "build_identity", return_value=self.identity), \
                    mock.patch.object(tool, "checked", side_effect=[b""] * failing_command + [tool.ReleaseError("Qualification failed")]), \
                    mock.patch.object(tool, "stage_payload") as stage, self.assertRaisesRegex(tool.ReleaseError, "Qualification failed"):
                tool.build(self.root, {})
            stage.assert_not_called()
        self.assertFalse((self.root / "artifacts" / "release").exists())

    def test_package_status_blocker_prevents_even_build_or_pack_commands(self) -> None:
        source = copy.deepcopy(self.source)
        source["packages"][0]["status"] = "planned"
        (self.root / "eng" / "packages.json").write_bytes(tool.encoded(source))
        with mock.patch.object(tool, "build_identity", return_value=self.identity), \
                mock.patch.object(tool, "checked") as run, self.assertRaisesRegex(tool.ReleaseError, "not qualified"):
            tool.build(self.root, {})
        run.assert_not_called()

    def test_promotion_submits_exact_verified_bytes_without_build_wildcards_or_skipped_duplicates(self) -> None:
        self.stage()
        selection = self.selection(self.archive())
        submitted = []

        def run(arguments, **kwargs):
            path = Path(arguments[3])
            submitted.append((path.name, path.read_bytes(), arguments, kwargs))
            return subprocess.CompletedProcess(arguments, 0, b"", b"")

        with mock.patch.object(tool.subprocess, "run", side_effect=run):
            tool.push_packages(self.payload, selection, self.ids, "SYNTHETIC-NOT-A-CREDENTIAL")
        self.assertEqual(len(submitted), 22)
        self.assertEqual({item[0] for item in submitted}, tool.package_names(self.ids, self.identity["version"]))
        for name, raw, arguments, kwargs in submitted:
            self.assertEqual(raw, (self.packages / name).read_bytes())
            self.assertEqual(arguments[:3], ["dotnet", "nuget", "push"])
            self.assertEqual(arguments[arguments.index("--source") + 1], "https://api.nuget.org/v3/index.json")
            self.assertEqual(Path(arguments[arguments.index("--configfile") + 1]), ROOT / "eng" / "release" / "nuget.config")
            self.assertNotIn("--skip-duplicate", arguments)
            self.assertNotIn("*", arguments[3])
            self.assertNotIn("shell", kwargs)
            self.assertEqual("--no-symbols" in arguments, name.endswith(".nupkg"))

    def test_changed_last_package_blocks_all_remote_writes_not_just_that_package(self) -> None:
        self.stage()
        selection = self.selection(self.archive())
        (self.payload / f"{self.ids[-1]}.{self.identity['version']}.snupkg").write_bytes(b"changed")
        with mock.patch.object(tool.subprocess, "run") as run, self.assertRaisesRegex(tool.ReleaseError, "Changed release bytes"):
            tool.push_packages(self.payload, selection, self.ids, "SYNTHETIC-NOT-A-CREDENTIAL")
        run.assert_not_called()

    def test_nuget_failure_and_timeout_stop_immediately_and_do_not_echo_temporary_key(self) -> None:
        self.stage()
        selection = self.selection(self.archive())
        key = "SYNTHETIC-NOT-A-CREDENTIAL"
        for result in (subprocess.CompletedProcess([], 1, b"", key.encode()), subprocess.TimeoutExpired(["dotnet", key], 360)):
            patch = {"side_effect": result} if isinstance(result, Exception) else {"return_value": result}
            with self.subTest(result=type(result).__name__), mock.patch.object(tool.subprocess, "run", **patch) as run:
                with self.assertRaises(tool.ReleaseError) as raised:
                    tool.push_packages(self.payload, selection, self.ids, key)
            self.assertIn("publication may be partial", str(raised.exception))
            self.assertNotIn(key, str(raised.exception))
            self.assertEqual(run.call_count, 1)

    def test_missing_oidc_output_never_starts_nuget_push(self) -> None:
        self.stage()
        selection = self.selection(self.archive())
        with mock.patch.object(tool.subprocess, "run") as run, self.assertRaisesRegex(tool.ReleaseError, "short-lived"):
            tool.push_packages(self.payload, selection, self.ids, "")
        run.assert_not_called()

    def test_push_rechecks_approval_selection_and_attestation_before_using_prepared_bytes(self) -> None:
        self.stage()
        archive = self.archive()
        api = FakeGitHub(self)
        api.artifacts[0].update(size_in_bytes=archive.stat().st_size, digest=f"sha256:{tool.file_hash(archive)}")
        selection = tool.resolve(api, self.identity["version"])
        directory = self.root / "xregistry-release-promotion"
        directory.mkdir()
        shutil.copyfile(archive, directory / f"{selection['artifact']['name']}.zip")
        shutil.copytree(self.payload, directory / "payload")
        environment = {
            **self.promotion_environment(), "EXPECTED_SELECTION": json.dumps(selection),
            "NUGET_API_KEY": "SYNTHETIC-NOT-A-CREDENTIAL",
        }
        events = []

        def run(arguments, **kwargs):
            if arguments[0] == "gh":
                events.append("attest")
                self.assertEqual(arguments[1:3], ["attestation", "verify"])
            else:
                events.append("push")
                self.assertEqual(arguments[:3], ["dotnet", "nuget", "push"])
                path = Path(arguments[3])
                self.assertEqual(path.read_bytes(), (self.packages / path.name).read_bytes())
            return subprocess.CompletedProcess(arguments, 0, b"", b"")

        with mock.patch.object(tool, "GitHub", return_value=api), mock.patch.object(tool, "clean_source"), \
                mock.patch.object(tool.subprocess, "run", side_effect=run), contextlib.redirect_stdout(io.StringIO()):
            tool.push(self.root, environment)
        self.assertEqual(events, ["attest"] + ["push"] * 22)
        self.assertIn(f"repos/{tool.REPOSITORY}/actions/runs/202/approvals", api.calls)

    def test_push_refuses_changed_selection_or_missing_approval_before_any_remote_write(self) -> None:
        selection = tool.resolve(FakeGitHub(self), self.identity["version"])
        environment = {**self.promotion_environment(), "EXPECTED_SELECTION": json.dumps(selection)}
        for mutation in ("approval", "artifact"):
            api = FakeGitHub(self)
            if mutation == "approval":
                api.reviews = []
            else:
                api.artifacts[0]["id"] = 901
            with self.subTest(mutation=mutation), mock.patch.object(tool, "GitHub", return_value=api), \
                    mock.patch.object(tool, "clean_source"), mock.patch.object(tool, "push_packages") as push, \
                    mock.patch.object(tool, "verify_attestation") as attest, self.assertRaises(ERRORS):
                tool.push(self.root, environment)
            push.assert_not_called()
            attest.assert_not_called()

    def test_workflow_outputs_reject_newline_injection(self) -> None:
        path = self.root / "outputs"
        for values in ({"selection": "x\nbad=1"}, {"bad\nkey": "x"}, {"selection": "x\rbad=1"}):
            with self.subTest(values=values), self.assertRaisesRegex(tool.ReleaseError, "Unsafe workflow output"):
                tool.emit_outputs(values, {"GITHUB_OUTPUT": str(path)})
        self.assertEqual(path.read_bytes(), b"")

    def test_alpha_identity_bypasses_package_status_blocker_but_still_runs_gates(self) -> None:
        alpha_identity = tool.identity_document(
            "0.1.0-alpha", 7, self.identity["commit"], self.identity["tagObject"],
            self.identity["runId"], self.identity["runAttempt"],
        )
        source = copy.deepcopy(self.source)
        source["packages"][0]["status"] = "planned"
        (self.root / "eng" / "packages.json").write_bytes(tool.encoded(source))

        def command(arguments, *args, **kwargs):
            if arguments[:2] == ["git", "show"]:
                return tool.source_path(self.root, arguments[2].split(":", 1)[1]).read_bytes()
            return b""

        stdout = io.StringIO()
        with mock.patch.object(tool, "build_identity", return_value=alpha_identity), \
                mock.patch.object(tool, "clean_source"), mock.patch.object(tool, "checked", side_effect=command) as run, \
                mock.patch.object(tool, "stage_payload") as stage, mock.patch.object(tool, "emit_outputs"), \
                contextlib.redirect_stdout(stdout):
            tool.build(self.root, {})
        self.assertIn("ALPHA PRERELEASE EXCEPTION", stdout.getvalue())
        commands = [call.args[0] for call in run.call_args_list]
        self.assertEqual(commands[0], ["python", str(self.root / "eng" / "check_packages.py"), "--release"])
        self.assertEqual(commands[1], ["python", str(self.root / "eng" / "specification" / "manage.py"), "release"])
        stage.assert_called_once()


class AlphaPrereleaseExceptionTests(unittest.TestCase):
    """Proves the version-scoped alpha exception (docs/releasing.md) without weakening
    the strict gate for every other version channel. See ``is_alpha_prerelease``,
    ``release_check`` and every ``qualified=`` call site in eng/release/release.py."""

    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory(prefix="xregistry-alpha-test-")
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)

    def unqualified_ledger(self) -> dict:
        return {
            "schemaVersion": 1, "semanticCoverageReviewed": True,
            "baselineManifestSha256": "a" * 64,
            "correctionsSha256": tool.sha256(b'{"fixture":"synthetic, not a specification correction"}\n'),
            "requirements": [{
                "id": "SYNTHETIC-NOT-RELEASE-EVIDENCE",
                "review": {"status": "reviewed", "note": "Alpha fixture, not yet fully qualified.", "roles": ["client"]},
                "implementation": {"status": "implemented", "testIds": ["Synthetic.NotReleaseEvidence"], "nativeEvidence": []},
            }],
        }

    def build_fixture(self, root: Path, version: str, *, package_status: str) -> tuple[dict, tuple[str, ...], Path, Path, dict[str, str]]:
        identity = tool.identity_document(version, 7, "1" * 40, "2" * 40, 101, 1)
        source = json.loads((ROOT / "eng" / "packages.json").read_bytes())
        for entry in source["packages"] + source["samples"]:
            entry["status"] = package_status
        ids = tuple(sorted(entry["id"] for entry in source["packages"]))
        packages = root / "package-output"
        packages.mkdir()
        for package_id in ids:
            for suffix in ("nupkg", "snupkg"):
                (packages / f"{package_id}.{identity['version']}.{suffix}").write_bytes(
                    synthetic_package(package_id, identity, suffix)
                )
        source_data = {
            "source-packages.json": tool.encoded(source),
            "source-specification-lock.json": tool.encoded({"baselineManifestSha256": "a" * 64}),
            "source-corrections.json": b'{"fixture":"synthetic, not a specification correction"}\n',
            "source-requirements.json": tool.encoded(self.unqualified_ledger()),
        }
        hashes = {}
        for name, raw in source_data.items():
            path = tool.source_path(root, tool.SOURCE_FILES[name])
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(raw)
            hashes[name] = tool.sha256(raw)
        payload = root / "payload"
        return identity, ids, packages, payload, hashes

    def test_alpha_version_bypasses_unqualified_packages_and_ledger(self) -> None:
        identity, ids, packages, payload, hashes = self.build_fixture(self.root, "0.1.0-alpha", package_status="implemented")
        tool.stage_payload(self.root, packages, payload, identity)
        document = tool.verify_payload(payload, identity, hashes, ids)
        self.assertEqual(document["identity"], identity)
        self.assertEqual(document["qualification"], tool.GATES)

    def test_alpha_dotted_prerelease_also_bypasses_unqualified_packages_and_ledger(self) -> None:
        identity, ids, packages, payload, hashes = self.build_fixture(self.root, "0.1.0-alpha.3", package_status="implemented")
        tool.stage_payload(self.root, packages, payload, identity)
        tool.verify_payload(payload, identity, hashes, ids)

    def test_non_alpha_prerelease_still_requires_full_qualification(self) -> None:
        for version in ("1.0.0-rc9", "0.1.0-beta"):
            with self.subTest(version=version):
                root = Path(tempfile.mkdtemp(prefix="xregistry-alpha-negative-"))
                self.addCleanup(shutil.rmtree, root, ignore_errors=True)
                identity, ids, packages, payload, hashes = self.build_fixture(root, version, package_status="implemented")
                with self.assertRaisesRegex(tool.ReleaseError, "not qualified"):
                    tool.stage_payload(root, packages, payload, identity)

    def test_alpha_grammar_requires_the_literal_alpha_identifier_as_first_component(self) -> None:
        for version, expected in (
            ("0.1.0-alpha", True), ("0.1.0-alpha.1", True), ("0.1.0-alpha.rc1", True),
            ("0.1.0-alphabet", False), ("0.1.0-beta", False), ("1.0.0", False),
            ("0.1.0-rc.alpha", False),
        ):
            with self.subTest(version=version):
                self.assertEqual(tool.is_alpha_prerelease(version), expected)

    def test_alpha_grammar_rejects_a_non_canonical_version_rather_than_silently_answering_false(self) -> None:
        with self.assertRaises(tool.ReleaseError):
            tool.is_alpha_prerelease("0.1.0-ALPHA")

    def test_alpha_exception_does_not_relax_malformed_review_structure(self) -> None:
        ledger = self.unqualified_ledger()
        ledger["requirements"][0]["review"]["status"] = "not-a-real-status"
        with self.assertRaises(tool.specification.SpecificationError):
            tool.specification.release_check(self.root, ledger, alpha=True)


class WorkflowContractTests(unittest.TestCase):
    def test_promotion_configuration_is_isolated_and_contains_no_stored_credentials(self) -> None:
        document = tool.ET.fromstring((ROOT / "eng" / "release" / "nuget.config").read_bytes())
        self.assertEqual(document.tag, "configuration")
        self.assertEqual([child.tag for child in document], ["packageSources"])
        self.assertEqual([child.tag for child in document[0]], ["clear"])
        self.assertEqual(document[0][0].attrib, {})

    def test_release_only_builds_tag_source_then_attests_the_uploaded_digest_with_separate_permissions(self) -> None:
        text = (ROOT / ".github" / "workflows" / "release.yml").read_text()
        self.assertIn("tags: ['v*']", text)
        self.assertNotIn("workflow_dispatch:", text)
        self.assertIn("ref: ${{ github.sha }}", text)
        self.assertIn("persist-credentials: false", text)
        self.assertIn("python eng\\release\\release.py build", text)
        build, attest = text.split("\n  attest:", 1)
        self.assertNotIn("id-token: write", build)
        self.assertIn("needs: build", attest)
        self.assertNotIn("actions/checkout@", attest)
        self.assertIn("subject-digest: sha256:${{ needs.build.outputs.artifact-digest }}", attest)
        self.assertIn("if-no-files-found: error", build)
        self.assertIn("overwrite: false", build)

    def test_dispatch_contract_is_explicit_version_main_only_real_release_approval_and_oidc(self) -> None:
        text = (ROOT / ".github" / "workflows" / "nuget.yml").read_text()
        self.assertRegex(text, r"workflow_dispatch:\s+inputs:\s+version:")
        inputs = text.split("permissions:", 1)[0]
        self.assertIn("required: true", inputs)
        self.assertIn("type: string", inputs)
        self.assertNotIn("default:", inputs)
        self.assertIn("github.ref == 'refs/heads/main'", text)
        resolve, publish = text.split("\n  publish:", 1)
        self.assertNotIn("id-token: write", resolve)
        self.assertIn("needs: resolve", publish)
        self.assertIn("environment: release", publish)
        self.assertIn("EXPECTED_SELECTION: ${{ needs.resolve.outputs.selection }}", publish)
        self.assertIn("NUGET_API_KEY: ${{ steps.login.outputs.NUGET_API_KEY }}", publish)
        self.assertLess(publish.index("release.py prepare"), publish.index("uses: NuGet/login@"))
        self.assertLess(publish.index("uses: NuGet/login@"), publish.index("release.py push"))
        self.assertNotIn("secrets.", text)
        self.assertNotIn("dotnet pack", publish)
        self.assertEqual(text.count("ref: ${{ github.sha }}"), 2)

    def test_actions_are_immutable_and_no_privileged_pr_or_qualification_bypass_exists(self) -> None:
        import re
        expected = {
            "actions/checkout": "fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09",
            "actions/setup-python": "ece7cb06caefa5fff74198d8649806c4678c61a1",
            "actions/setup-dotnet": "26b0ec14cb23fa6904739307f278c14f94c95bf1",
            "actions/upload-artifact": "b7c566a772e6b6bfb58ed0dc250532a479d7789f",
            "actions/attest-build-provenance": "96278af6caaf10aea03fd8d33a09a777ca52d62f",
            "NuGet/login": "8d196754b4036150537f80ac539e15c2f1028841",
        }
        for name in ("release.yml", "nuget.yml"):
            text = (ROOT / ".github" / "workflows" / name).read_text()
            actions = re.findall(r"uses: ([^@\s]+)@([^\s]+)", text)
            self.assertGreater(len(actions), 0)
            for action, pinned in actions:
                self.assertRegex(pinned, r"^[0-9a-f]{40}$")
                self.assertEqual(pinned, expected[action])
            for forbidden in (
                "pull_request", "workflow_run:", "continue-on-error", "--skip-duplicate",
                "packages: write", "contents: write", "write-all", "secrets.NUGET", "latest-run",
            ):
                self.assertNotIn(forbidden, text)
            self.assertIn("permissions: {}", text)
            self.assertIn("cancel-in-progress: false", text)
            self.assertNotRegex(text, r"run:.*\$\{\{\s*inputs\.version")


if __name__ == "__main__":
    unittest.main()
