#!/usr/bin/env python3
"""Build the exact unmodified pinned xr oracle in an isolated task artifact directory."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import stat
import sys
import tarfile
import urllib.request
import zipfile


ROOT = Path(__file__).resolve().parents[1]
SOURCE = "https://github.com/xregistry/server"
MAX_ARCHIVE_EXPANDED = 1024 * 1024 * 1024
MAX_ARCHIVE_FILES = 50000
SPEC = importlib.util.spec_from_file_location("reverse_build_runtime", ROOT / "eng" / "verify-upstream-to-dotnet.py")
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("The reverse harness is missing.")
runtime = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = runtime
SPEC.loader.exec_module(runtime)


class BuildError(RuntimeError):
    """The independent oracle could not be built with its exact provenance."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise BuildError(message)


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def validate_lock(lock: dict, upstream: dict) -> None:
    require(lock.get("schemaVersion") == 1 and type(lock.get("schemaVersion")) is int, "Invalid reverse toolchain schema.")
    require(lock.get("repository") == SOURCE == upstream.get("repository")
            and lock.get("commit") == upstream.get("commit")
            and re.fullmatch(r"[0-9a-f]{40}", lock.get("commit", "")) is not None,
            "Reverse source identity must agree with the immutable upstream lock.")
    require(lock.get("expectedVersion") == upstream.get("executableVersion")
            == "Version: " + lock["commit"][:12], "CLI version expectation contradicts the source pin.")
    require(re.fullmatch(r"go1\.\d+\.\d+", lock.get("goVersion", "")) is not None, "A precise stable Go version is required.")
    for name in ("goModSha256", "goSumSha256"):
        require(re.fullmatch(r"[0-9a-f]{64}", lock.get(name, "")) is not None, "Module files require exact hash commitments.")
    for target in ("windows-amd64", "linux-amd64"):
        archive = lock.get("archives", {}).get(target, {})
        suffix = ".zip" if target.startswith("windows") else ".tar.gz"
        require(archive.get("url") == f"https://go.dev/dl/{lock['goVersion']}.{target}{suffix}"
                and re.fullmatch(r"[0-9a-f]{64}", archive.get("sha256", "")) is not None
                and type(archive.get("bytes")) is int and 1 <= archive["bytes"] <= 256 * 1024 * 1024,
                "A supported Go archive requires an exact official URL, size and SHA-256.")


def validate_build_metadata(text: str, commit: str, version: str, target: str) -> None:
    os_name, architecture = target.split("-", 1)
    lines = text.splitlines()
    require(bool(lines) and lines[0].endswith(": " + version), "The xr binary was built with a different Go toolchain.")
    fields = {}
    for line in lines[1:]:
        pieces = line.strip().split("\t")
        if len(pieces) == 2 and pieces[0] == "build" and "=" in pieces[1]:
            name, value = pieces[1].split("=", 1)
            require(name not in fields, "Duplicate Go build identity fields.")
            fields[name] = value
    require("\tpath\tgithub.com/xregistry/server/cmds/xr" in text and fields.get("vcs") == "git"
            and fields.get("vcs.revision") == commit and fields.get("vcs.modified") == "false"
            and fields.get("GOOS") == os_name and fields.get("GOARCH") == architecture,
            "The oracle build metadata does not prove the exact unmodified source/platform.")


def verify_archive(path: Path, expected: dict) -> None:
    require(path.stat().st_size == expected["bytes"] and digest(path) == expected["sha256"],
            "The Go archive size or SHA-256 does not match the reviewed pin.")


def _member(name: str) -> None:
    path = PurePosixPath(name)
    require("\\" not in name and not path.is_absolute() and path.parts and path.parts[0] == "go"
            and all(part not in ("", ".", "..") and ":" not in part for part in path.parts),
            "The Go archive contains an unsafe path.")


def extract_go_archive(archive: Path, destination: Path) -> None:
    require(not destination.exists(), "The isolated Go extraction destination must not already exist.")
    if archive.suffix == ".zip":
        with zipfile.ZipFile(archive) as source:
            entries = source.infolist()
            require(len(entries) <= MAX_ARCHIVE_FILES and sum(entry.file_size for entry in entries) <= MAX_ARCHIVE_EXPANDED,
                    "Go archive expansion exceeds its finite budget.")
            for entry in entries:
                _member(entry.filename)
                require(not stat.S_ISLNK(entry.external_attr >> 16), "Go tool extraction does not permit symlinks.")
            source.extractall(destination)
    else:
        with tarfile.open(archive, "r:gz") as source:
            entries = source.getmembers()
            require(len(entries) <= MAX_ARCHIVE_FILES and sum(entry.size for entry in entries) <= MAX_ARCHIVE_EXPANDED,
                    "Go archive expansion exceeds its finite budget.")
            for entry in entries:
                _member(entry.name)
                require(entry.isfile() or entry.isdir(), "Go tool extraction does not permit links or devices.")
            source.extractall(destination, members=entries, filter="data")


def download_go(expected: dict, work: Path) -> Path:
    archive = work / expected["url"].rsplit("/", 1)[1]
    if not archive.exists():
        with urllib.request.urlopen(expected["url"], timeout=60) as source, archive.open("xb") as output:
            count = 0
            while chunk := source.read(65536):
                count += len(chunk)
                require(count <= expected["bytes"], "Go download exceeded its pinned length.")
                output.write(chunk)
    verify_archive(archive, expected)
    destination = work / "toolchain"
    extract_go_archive(archive, destination)
    return destination / "go" / "bin" / ("go.exe" if os.name == "nt" else "go")


def build(args: argparse.Namespace, report: dict) -> None:
    lock_path = args.lock.resolve(strict=True)
    lock = runtime.decode_json(lock_path.read_bytes())
    upstream = runtime.decode_json((ROOT / "interop" / "upstream-lock.json").read_bytes())
    validate_lock(lock, upstream)
    work = args.work.resolve()
    require(work != ROOT and work != Path(work.anchor), "A dedicated task artifact directory is required.")
    work.mkdir(parents=True, exist_ok=True)
    hooks = work / "empty-hooks"
    hooks.mkdir(exist_ok=True)
    require(not any(hooks.iterdir()), "The disabled hooks directory must remain empty.")
    environment = os.environ.copy()
    environment.update(GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_COUNT="0",
                       GIT_TERMINAL_PROMPT="0", GOENV="off", GOTOOLCHAIN="local", GOWORK="off",
                       GOPATH=str(work / "gopath"), GOCACHE=str(work / "gocache"), GOMODCACHE=str(work / "gomodcache"),
                       GOPROXY="https://proxy.golang.org,direct", GOSUMDB="sum.golang.org",
                       GOPRIVATE="", GONOPROXY="", GONOSUMDB="", CGO_ENABLED="0")
    environment.pop("GOFLAGS", None)
    environment.pop("GOROOT", None)
    commands = report["commands"]

    def run(name: str, command: list[str], cwd: Path, timeout: int = 300):
        result = runtime.run_command(command, cwd, environment, timeout, max_output=16 * 1024 * 1024)
        (work / (name + ".stdout")).write_bytes(result.stdout)
        (work / (name + ".stderr")).write_bytes(result.stderr)
        commands.append({"name": name, "argv": command, "cwd": str(cwd), "exitCode": result.returncode,
                         "stdout": name + ".stdout", "stderr": name + ".stderr"})
        require(result.returncode == 0, f"{name} failed; see its captured output under {work}.")
        return result

    git = shutil.which("git")
    require(git is not None, "Git is required as an isolated source-acquisition test oracle.")
    source = args.source.resolve(strict=True) if args.source else work / "upstream-source"
    require(source.is_relative_to(work), "The source checkout must be inside this dedicated artifact directory.")
    git_options = ["-c", "core.hooksPath=" + str(hooks), "-c", "credential.helper=", "-c", "protocol.file.allow=never"]
    if not source.exists():
        source.mkdir()
        run("git-init", [git, *git_options, "-c", "init.templateDir=", "init", "--quiet", str(source)], work)
        run("git-fetch", [git, "-C", str(source), *git_options, "fetch", "--quiet", "--depth=1", SOURCE, lock["commit"]], work)
        run("git-checkout", [git, "-C", str(source), *git_options, "-c", "advice.detachedHead=false",
                             "checkout", "--quiet", "--detach", "FETCH_HEAD"], work)
    commit = run("git-commit", [git, "-C", str(source), *git_options, "rev-parse", "HEAD"], work).stdout.decode().strip()
    require(commit == lock["commit"], "The source checkout is not the exact pinned commit.")
    require(digest(source / "go.mod") == lock["goModSha256"] and digest(source / "go.sum") == lock["goSumSha256"],
            "The pinned module manifests do not match their exact commitments.")
    run("git-tracked-clean", [git, "-C", str(source), *git_options, "diff", "--exit-code", "HEAD", "--"], work)
    generated = {}
    for template in ("shared_entity", "shared_model"):
        contents = (source / "common" / template).read_bytes()
        for folder, package in (("registry", "registry"), ("cmds/xr/xrlib", "xrlib")):
            path = source / folder / (template + ".go")
            expected = contents.replace(b"XXX", package.encode())
            if path.exists():
                require(path.read_bytes() == expected, "An upstream generated source differs from the exact pinned Makefile recipe.")
            else:
                path.write_bytes(expected)
            generated[path.relative_to(source).as_posix()] = digest(path)
    require(platform.machine().lower() in ("amd64", "x86_64"), "Only x64 oracle builds are qualified.")
    target = "windows-amd64" if os.name == "nt" else "linux-amd64" if sys.platform.startswith("linux") else ""
    require(bool(target), "Only Windows/Linux x64 oracle builds are qualified.")
    environment.update(GOOS=target.split("-")[0], GOARCH="amd64", GOAMD64="v1")
    environment.pop("GOEXPERIMENT", None)
    go = args.go.resolve(strict=True) if args.go else Path(shutil.which("go")) if shutil.which("go") else None
    required_missing = go is None
    if go is not None:
        result = runtime.run_command([str(go), "version"], work, environment, 30)
        required_missing = result.returncode != 0 or result.stdout.decode("utf-8").strip() != f"go version {lock['goVersion']} {target.replace('-', '/')}"
    if required_missing:
        require(args.bootstrap_go, "The pinned Go toolchain is missing; explicitly permit isolated installation with --bootstrap-go.")
        go = download_go(lock["archives"][target], work)
        report["isolatedGoInstalledAfterMissingPrerequisite"] = True
    require(go is not None, "The pinned Go toolchain could not be selected.")
    environment["GOROOT"] = str(go.parent.parent)
    version = run("go-version", [str(go), "version"], work).stdout.decode().strip()
    require(version == f"go version {lock['goVersion']} {target.replace('-', '/')}", "The actual Go toolchain version/platform differs from the pin.")
    binary_dir = work / "bin"
    binary_dir.mkdir(exist_ok=True)
    binary = binary_dir / ("xr.exe" if os.name == "nt" else "xr")
    run("go-build-xr", [str(go), "build", "-mod=readonly", "-trimpath", "-buildvcs=true",
                        "-ldflags", "-X=github.com/xregistry/server/common.GitCommit=" + commit,
                        "-o", str(binary), "./cmds/xr"], source)
    run("go-mod-verify", [str(go), "mod", "verify"], source)
    require(digest(source / "go.mod") == lock["goModSha256"] and digest(source / "go.sum") == lock["goSumSha256"],
            "Building the oracle modified its module locks.")
    run("git-final-clean", [git, "-C", str(source), *git_options, "diff", "--exit-code", "HEAD", "--"], work)
    build_metadata = run("go-build-metadata", [str(go), "version", "-m", str(binary)], work).stdout.decode()
    validate_build_metadata(build_metadata, commit, lock["goVersion"], target)
    cli_version = runtime.validate_cli_version(run("xr-version", [str(binary), "--version"], work), lock["expectedVersion"])
    report.update(status="passed", repository=SOURCE, commit=commit, source=str(source), sourceModified=False,
                  sourceTree=run("git-tree", [git, "-C", str(source), *git_options, "rev-parse", "HEAD^{tree}"], work).stdout.decode().strip(),
                  version=cli_version, binary=str(binary), binarySha256=digest(binary), platform=target,
                  goVersion=version, goBinary=str(go), goBinarySha256=digest(go),
                  goArchive=lock["archives"][target], generatedSourceSha256=generated,
                  goModSha256=lock["goModSha256"], goSumSha256=lock["goSumSha256"], buildMetadata=build_metadata,
                  nativeDotnetRuntimeDependency=False)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--source", type=Path)
    parser.add_argument("--go", type=Path)
    parser.add_argument("--bootstrap-go", action="store_true")
    parser.add_argument("--lock", type=Path, default=ROOT / "interop" / "reverse-toolchain.json")
    args = parser.parse_args(argv)
    args.work.mkdir(parents=True, exist_ok=True)
    report = {"schemaVersion": 1, "status": "running", "commands": []}
    code = 0
    try:
        build(args, report)
    except (BuildError, runtime.ReverseInteropError, OSError, ValueError, KeyError) as error:
        report.update(status="failed", error=str(error))
        code = 1
    runtime.write_json(args.work / "oracle-build.json", report)
    print(json.dumps({"status": report["status"], "evidence": str((args.work / "oracle-build.json").resolve())}, ensure_ascii=True))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
