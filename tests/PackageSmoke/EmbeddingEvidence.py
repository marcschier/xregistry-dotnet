from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import zipfile


PACKAGE_IDS = ("XRegistry", "XRegistry.Client", "XRegistry.Server",
               "XRegistry.AspNetCore", "XRegistry.Models", "XRegistry.Validation",
               "XRegistry.Storage.File", "XRegistry.Federation", "XRegistry.Bindings.File",
               "XRegistry.Bindings.Git", "XRegistry.Bindings.Oci")
CASE_NAMES = json.loads(Path(__file__).with_name("EmbeddingCases.json").read_bytes())
RUNTIME_MEASUREMENTS = {
    "storageReopenedGeneration": 2, "storageAdapterGeneration": 2,
    "fileMappingDocumentBytes": 18, "fileOciDocumentBytes": 18,
    "gitVerifiedObjects": 11, "gitDocumentBytes": 11,
    "ociOfflineObjects": 99, "ociOfflineIndexes": 44, "ociOfflineManifests": 25,
    "ociOfflineConfigs": 25, "ociOfflineDocuments": 4,
    "ociLinkedObjects": 98, "ociLinkedIndexes": 44, "ociLinkedManifests": 25,
    "ociLinkedConfigs": 25, "ociLinkedDocuments": 3,
    "federationDocumentBytes": 15, "gitNativeModules": 0,
    "federationEscapedMetadataForms": 9, "federationEscapedDocumentForms": 6,
    "federationEscapedDocumentBytes": 90, "federationEscapedInvalidTargets": 6,
}


class EvidenceError(ValueError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise EvidenceError(message)


def forbidden_dependency(name: str) -> bool:
    lowered = name.lower()
    return (lowered.startswith(("opc.ua", "opcfoundation", "opcuanet", "tunit", "libgit2"))
            or "ua.netstandard" in lowered)


def forbidden_native_module(name: str) -> bool:
    lowered = name.lower()
    return forbidden_dependency(lowered) or lowered in ("git", "git.exe", "git.dll", "git2.dll")


def validate_report(report: dict, framework: str, rid: str, mode: str) -> None:
    require(report.get("schemaVersion") == 1, "Unexpected report schema.")
    require(report.get("scope") == "development-package-embedding"
            and report.get("releaseQualified") is False, "This is development evidence only.")
    require(report.get("framework") == framework and report.get("runtimeIdentifier") == rid,
            "The report does not describe the requested framework/native RID.")
    architecture = {"x64": "X64", "arm64": "Arm64"}[rid.split("-")[1]]
    require(report.get("processArchitecture") == report.get("osArchitecture") == architecture,
            "Native host and process architectures must agree.")
    for field in ("dynamicCodeSupported", "dynamicCodeCompiled"):
        require(type(report.get(field)) is bool, "Runtime feature evidence is missing.")
    for field in ("jitCompiledMethodsAtStart", "jitCompiledMethodsAtEnd"):
        require(type(report.get(field)) is int and report[field] >= 0, "JIT method-count evidence is missing.")

    if mode == "native":
        require(report.get("outcome") == "passed", "The native consumer did not pass.")
        require(not report["dynamicCodeSupported"] and not report["dynamicCodeCompiled"]
                and report["jitCompiledMethodsAtStart"] == report["jitCompiledMethodsAtEnd"] == 0,
                "Native evidence must exclude dynamic code and JIT compilation.")
        cases = report.get("cases", [])
        require([case.get("name") for case in cases] == CASE_NAMES, "The exact named case set was not executed.")
        require(all(case.get("outcome") == "passed" for case in cases), "A named case did not pass.")
        require(report.get("httpRequests", 0) >= 12, "There is no sufficient real HTTP dispatch evidence.")
        require(isinstance(report.get("requests"), list) and len(report["requests"]) == report["httpRequests"],
                "The observed wire request list does not match the dispatch count.")
        require(report.get("committedBatches", 0) >= 4, "There is no sufficient application-publication evidence.")
        require(report.get("documentBytesVerified", 0) >= 28, "There is no sufficient exact-Document evidence.")
        measurements = report.get("runtimeEvidence", {})
        for name, expected in RUNTIME_MEASUREMENTS.items():
            require(measurements.get(name) == expected, f"Missing or incorrect runtime operation evidence: {name}.")
        require(measurements.get("federationHttpRequests", 0) >= 6, "The federation source did not exercise real HTTP.")
        require(measurements.get("federationEscapedHttpRequests", 0) >= 15,
                "Escaped federation identities were not exercised over real HTTP.")
        require(report.get("runtimeFacts", {}).get("federationEscapedWireVersionXid") ==
                "/workspaces/group%3Aone/artifacts/item%40stable/versions/v%3A1",
                "The escaped wire metadata identity was not preserved.")
        require(measurements.get("sqliteNativeModules", 0) >= 1, "Actual native SQLite loading was not observed.")
        modules = report.get("nativeModules", [])
        require(modules and not any(forbidden_native_module(name) for name in modules),
                "The consumer loaded a prohibited native Git/OPC backend.")
        assemblies = report.get("assemblies", [])
        require(bool(assemblies), "Package assembly-reference evidence is missing.")
        for assembly in assemblies:
            require(len(assembly.get("sha256", "")) == 64, "A package assembly hash is missing.")
            require(not forbidden_dependency(assembly.get("name", "")), "An OPC UA/test dependency was included.")
            require(not any(forbidden_dependency(name) for name in assembly.get("references", [])),
                    "An OPC UA/test assembly reference was included.")
    else:
        require(mode in ("jit", "masked-jit"), "Unknown control mode.")
        require(report.get("outcome") == "jit-rejected" and report.get("cases") == [],
                "The JIT negative control did not reject before cases.")
        require(report["jitCompiledMethodsAtStart"] > 0 and report["jitCompiledMethodsAtEnd"] > 0,
                "The negative control must actually show JIT-compiled methods.")
        require(report.get("httpRequests") == report.get("committedBatches")
                == report.get("documentBytesVerified") == 0 and report.get("assemblies") == [] and report.get("requests") == [],
                "The JIT control executed work before the native guard.")
        require(report.get("runtimeEvidence") == {} and report.get("nativeModules") == [],
                "The JIT control exercised a runtime package before the guard.")
        if mode == "masked-jit":
            require(not report["dynamicCodeSupported"] and not report["dynamicCodeCompiled"],
                    "The feature-masked control did not actually mask both feature flags.")


def validate_assets(assets: dict, versions: dict[str, str]) -> None:
    libraries = assets.get("libraries", {})
    require(set(versions) == set(PACKAGE_IDS), "All eleven development runtime packages must be explicit.")
    for identity, library in libraries.items():
        name = identity.split("/")[0]
        require(library.get("type") != "project", "The consumer must not use source ProjectReferences.")
        require(not forbidden_dependency(name), "The consumer contains an OPC UA or TUnit dependency.")
    for name, version in versions.items():
        require(libraries.get(f"{name}/{version}", {}).get("type") == "package",
                f"The exact packaged dependency is absent: {name}.")


def sha256(path: Path) -> str:
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def source_manifest(root: Path) -> dict:
    files: set[Path] = set()
    for package in PACKAGE_IDS:
        directory = root / "src" / package
        for path in directory.rglob("*"):
            relative = path.relative_to(directory)
            if any(part in ("obj", "bin") for part in relative.parts):
                continue
            require(not path.is_symlink(), "Source fingerprints do not traverse symbolic links.")
            if path.is_file():
                files.add(path)
    for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
                 "global.json", "NuGet.config", "version.json", "README.md", "LICENSE", "NOTICE"):
        files.add(root / name)
    for path in (root / "tests" / "PackageSmoke").glob("Embedding*"):
        if path.is_file():
            files.add(path)
    files.add(root / "eng" / "test-embedding-packages.ps1")
    fixture_manifest = root / "tests" / "PackageSmoke" / "EmbeddingFixtureInputs.json"
    if fixture_manifest.is_file():
        fixture_inputs = json.loads(fixture_manifest.read_bytes())
        for item in fixture_inputs["directories"]:
            directory = root.joinpath(*item["source"].split("\\"))
            files.update(path for path in directory.rglob("*") if path.is_file())
        files.update(root.joinpath(*item["source"].split("\\")) for item in fixture_inputs["files"])
    entries = [{"path": str(path.relative_to(root)), "sha256": sha256(path)}
               for path in sorted(files)]
    digest = hashlib.sha256(json.dumps(entries, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    return {"schemaVersion": 1, "sha256": digest, "files": entries}


def inventory(assets_path: Path, versions_path: Path, feed: Path, cache: Path,
              framework: str, rid: str, destination: Path) -> dict:
    assets = json.loads(assets_path.read_bytes())
    versions = json.loads(versions_path.read_bytes())
    validate_assets(assets, versions)
    targets = assets["targets"]
    target = targets.get(f"{framework}/{rid}", targets.get(framework))
    require(target is not None, "The exact restored target is absent.")
    packages = []
    assemblies = []
    native_assets = []
    destination.mkdir(parents=True, exist_ok=True)
    for name, version in versions.items():
        package = feed / f"{name}.{version}.nupkg"
        require(package.is_file(), f"Fresh-feed package is missing: {name}.")
        packages.append({"id": name, "version": version, "path": str(package), "sha256": sha256(package)})
    for identity, item in sorted(target.items()):
        library = assets["libraries"][identity]
        if library.get("type") != "package":
            continue
        package_root = cache / library["path"]
        selected_assets = {asset: "managed" for asset in item.get("runtime", {}) if asset.lower().endswith(".dll")}
        selected_assets.update({asset: "native" for asset in item.get("native", {})})
        for asset, properties in item.get("runtimeTargets", {}).items():
            if properties.get("rid") == rid:
                kind = properties.get("assetType")
                if kind == "native" or kind == "runtime" and asset.lower().endswith(".dll"):
                    selected_assets[asset] = "native" if kind == "native" else "managed"
        for asset, kind in sorted(selected_assets.items()):
            original = package_root / asset
            require(original.resolve().is_relative_to(cache.resolve()), "A package asset escapes the isolated cache.")
            package_name, version = identity.split("/", 1)
            archive_path = (feed / f"{package_name}.{version}.nupkg" if package_name in versions else
                            package_root / f"{package_name.lower()}.{version}.nupkg")
            with zipfile.ZipFile(archive_path) as archive:
                expected = hashlib.sha256(archive.read(asset)).hexdigest()
            require(original.is_file() and sha256(original) == expected, "Restored bytes differ from the nupkg.")
            require(not forbidden_native_module(Path(asset).name), "A prohibited native Git/OPC asset was restored.")
            retained = destination / kind / package_name / Path(asset).name
            retained.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(original, retained)
            evidence = {"name": Path(asset).stem if kind == "managed" else Path(asset).name,
                        "package": identity, "asset": asset, "path": str(retained), "sha256": expected}
            (assemblies if kind == "managed" else native_assets).append(evidence)
    require(set(PACKAGE_IDS).issubset({entry["name"] for entry in assemblies}), "Package runtime assets are incomplete.")
    return {"schemaVersion": 1, "framework": framework, "runtimeIdentifier": rid,
            "packages": packages, "assemblies": assemblies, "nativeAssets": native_assets}


def stage_fixtures(root: Path, destination: Path) -> dict:
    specification = json.loads((root / "tests" / "PackageSmoke" / "EmbeddingFixtureInputs.json").read_bytes())
    retained = []
    destination.mkdir(parents=True, exist_ok=False)
    for item in specification["directories"]:
        source = root.joinpath(*item["source"].split("\\"))
        entries = []
        for path in sorted(source.rglob("*")):
            require(not path.is_symlink(), "Frozen fixture trees must not contain links.")
            if path.is_file():
                entries.append({"path": path.relative_to(source).as_posix(),
                                "bytes": path.stat().st_size, "sha256": sha256(path)})
        entries.sort(key=lambda entry: entry["path"])
        digest = hashlib.sha256(json.dumps(entries, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
        require(digest == item["treeSha256"] and len(entries) == item["files"]
                and sum(entry["bytes"] for entry in entries) == item["bytes"], "Independent frozen fixture bytes changed.")
        for entry in entries:
            target = destination / item["name"] / entry["path"]
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source / entry["path"], target)
            require(sha256(target) == entry["sha256"], "Fixture bytes changed while copying.")
            retained.append({"path": str(target.relative_to(destination)), "sha256": entry["sha256"], "bytes": entry["bytes"]})
    for item in specification["files"]:
        source = root.joinpath(*item["source"].split("\\"))
        require(source.stat().st_size == item["bytes"] and sha256(source) == item["sha256"],
                "An independently pinned fixture input changed.")
        target = destination / item["name"]
        shutil.copyfile(source, target)
        require(sha256(target) == item["sha256"], "Fixture bytes changed while copying.")
        retained.append({"path": str(target.relative_to(destination)), "sha256": item["sha256"], "bytes": item["bytes"]})
    return {"schemaVersion": 1, "root": str(destination), "files": retained}


def main() -> None:
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="command", required=True)
    sources = commands.add_parser("sources")
    sources.add_argument("--root", type=Path, required=True)
    sources.add_argument("--output", type=Path, required=True)
    fixtures = commands.add_parser("fixtures")
    fixtures.add_argument("--root", type=Path, required=True)
    fixtures.add_argument("--destination", type=Path, required=True)
    fixtures.add_argument("--output", type=Path, required=True)
    packages = commands.add_parser("inventory")
    for name in ("assets", "versions", "feed", "cache", "destination", "output"):
        packages.add_argument("--" + name, type=Path, required=True)
    packages.add_argument("--framework", required=True)
    packages.add_argument("--rid", required=True)
    reports = commands.add_parser("report")
    reports.add_argument("--input", type=Path, required=True)
    reports.add_argument("--framework", required=True)
    reports.add_argument("--rid", required=True)
    reports.add_argument("--mode", choices=("native", "jit", "masked-jit"), required=True)
    reports.add_argument("--inventory", type=Path)
    args = parser.parse_args()
    if args.command == "sources":
        write_json(args.output, source_manifest(args.root.resolve()))
    elif args.command == "fixtures":
        write_json(args.output, stage_fixtures(args.root.resolve(), args.destination.resolve()))
    elif args.command == "inventory":
        write_json(args.output, inventory(args.assets, args.versions, args.feed, args.cache,
                                         args.framework, args.rid, args.destination))
    else:
        report = json.loads(args.input.read_bytes())
        validate_report(report, args.framework, args.rid, args.mode)
        if args.inventory is not None and args.mode == "native":
            manifest = json.loads(args.inventory.read_bytes())
            expected = {(item["name"], item["sha256"]) for item in manifest["assemblies"]}
            actual = {(item["name"], item["sha256"]) for item in report["assemblies"]}
            require(actual == expected, "The native report did not inspect the exact packaged runtime assets.")
            sqlite = {item["sha256"] for item in manifest.get("nativeAssets", []) if "sqlite" in item["name"].lower()}
            require(report.get("runtimeFacts", {}).get("sqliteModuleSha256") in sqlite,
                    "The actually loaded SQLite binary differs from the selected native package asset.")
        print(f"Validated {args.mode} evidence: {args.framework} {args.rid}.")


if __name__ == "__main__":
    main()
