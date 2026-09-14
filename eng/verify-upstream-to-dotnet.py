#!/usr/bin/env python3
"""Qualify the real pinned xr CLI against an owned native durable .NET FileServer."""

from __future__ import annotations

import argparse
import base64
import hashlib
import http.client
import importlib.util
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import tempfile
import threading
import time
from typing import Callable


ROOT = Path(__file__).resolve().parents[1]
MAX_OUTPUT_BYTES = 4 * 1024 * 1024
REQUIRED_CASES = (
    "native-runtime", "pinned-cli-version", "trusted-root-and-discovery",
    "install-explicit-model", "xr-import", "nonempty-fixture",
    "xr-get-collection", "xr-get-default-binary", "xr-get-v1-binary",
    "xr-get-v2-empty", "xr-get-meta", "xr-get-v1-details", "xr-get-v2-details",
    "restart-model-and-context", "xr-restart-default", "xr-restart-empty",
    "xr-restart-meta", "xr-conform",
)


def _load(name: str, file: Path):
    spec = importlib.util.spec_from_file_location(name, file)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load required repository helper {file.name}.")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


server_helper = _load("reverse_owned_file_server", ROOT / "eng" / "verify_file_server.py")
accounting = _load("reverse_upstream_accounting", ROOT / "eng" / "check_upstream_output.py")
Server = server_helper.Server
free_port = server_helper.free_port


class ReverseInteropError(RuntimeError):
    """A reverse qualification obligation was not satisfied."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ReverseInteropError(message)


def _unique(pairs: list[tuple[str, object]]) -> dict:
    value = {}
    for key, item in pairs:
        if key in value:
            raise ReverseInteropError("Duplicate JSON keys are not evidence.")
        value[key] = item
    return value


def _bad_number(_: str):
    raise ReverseInteropError("Non-finite JSON numbers are not evidence.")


def decode_json(raw: bytes) -> dict:
    require(len(raw) <= MAX_OUTPUT_BYTES, "JSON evidence exceeds its byte bound.")
    try:
        result = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique, parse_constant=_bad_number)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise ReverseInteropError("Malformed JSON evidence.") from error
    require(isinstance(result, dict), "JSON evidence must be an object.")
    return result


def validate_native_runtime(result: subprocess.CompletedProcess, rid: str) -> dict:
    require(type(result.returncode) is int and result.returncode == 0 and not result.stderr,
            "The native runtime proof process did not exit cleanly.")
    value = decode_json(result.stdout)
    require(rid in ("win-x64", "linux-x64"), "Only the explicitly qualified x64 native RIDs are supported.")
    require(value.get("nativeAot") is True and type(value.get("jitCompiledMethods")) is int
            and value["jitCompiledMethods"] == 0 and value.get("architecture") == "X64",
            "Native proof requires the RuntimeFeature-derived flag, zero JIT-compiled methods and X64 architecture.")
    return value


def validate_cli_version(result: subprocess.CompletedProcess, expected: str) -> str:
    require(result.returncode == 0 and not result.stderr, "The pinned CLI version command failed.")
    try:
        version = result.stdout.decode("utf-8").strip()
    except UnicodeError as error:
        raise ReverseInteropError("The CLI version is not UTF-8.") from error
    require(version == expected, "The CLI version does not match the immutable upstream pin.")
    return version


def validate_document(result: subprocess.CompletedProcess, expected: bytes) -> None:
    require(result.returncode == 0 and not result.stderr, "The real xr document command failed.")
    require(result.stdout == expected, "The real xr CLI did not return the exact independently expected document bytes.")


def validate_checker(result: subprocess.CompletedProcess, expected_passes: int) -> dict:
    require(result.returncode == 0 and not result.stderr, f"The real xr conform command failed (exit {result.returncode}).")
    require(len(result.stdout) <= MAX_OUTPUT_BYTES, "Checker evidence exceeds its byte bound.")
    try:
        return accounting.check_output(result.stdout.decode("utf-8"), expected_passes)
    except (UnicodeError, accounting.CheckerError) as error:
        raise ReverseInteropError(str(error)) from error


def classify_checker_failure(text: str) -> list[str]:
    return ["XR-CAPABILITIES-MUTABLE"] if "Unknown capability specified: mutable." in text else []


def observed_checker_totals(text: str) -> dict | None:
    summaries = [match for line in text.splitlines() if (match := accounting.SUMMARY.fullmatch(line))]
    if len(summaries) != 1:
        return None
    return dict(zip(("passes", "failures", "warnings", "skips"), map(int, summaries[0].groups())))


def validate_fixture(fixture: dict) -> None:
    try:
        require(fixture["schemaVersion"] == 1 and type(fixture["schemaVersion"]) is int, "Unsupported reverse fixture.")
        require(fixture["specVersion"] == "1.0-rc4", "The reverse fixture must use the frozen Core version.")
        require(set(fixture["modelSource"]["groups"]) == {"dirs"}, "The checker fixture requires exactly one declared Group type.")
        model = fixture["modelSource"]["groups"]["dirs"]
        require(model["singular"] == "dir" and set(model["resources"]) == {"files"}
                and model["resources"]["files"]["singular"] == "file"
                and model["resources"]["files"].get("hasdocument", True) is True,
                "The checker fixture requires a real document-bearing Resource model.")
        require(fixture["importTarget"] == "/dirs" and set(fixture["importBody"]) == {"team"},
                "The reverse import must contain the named nonempty Group.")
        resources = fixture["importBody"]["team"]["files"]
        require(set(resources) == {"item"}, "The reverse import must contain the named Resource.")
        resource = resources["item"]
        require(set(resource["versions"]) == {"v1", "v2"} and resource["meta"]["defaultversionid"] == "v1"
                and resource["meta"]["defaultversionsticky"] is True,
                "The reverse fixture must explicitly retain older v1 as default.")
        expected = fixture["expected"]
        binary = base64.b64decode(expected["binaryBase64"], validate=True)
        require(binary == bytes.fromhex("00FF7F0D0A41") and
                hashlib.sha256(binary).hexdigest() == expected["binarySha256"],
                "The independently chosen binary fixture changed.")
        require(resource["versions"]["v1"]["filebase64"] == expected["binaryBase64"] and
                resource["versions"]["v2"]["filebase64"] == "" and expected["emptyVersion"] == "v2"
                and expected["defaultVersion"] == "v1" and expected["versionIds"] == ["v1", "v2"]
                and expected["resourceXid"] == "/dirs/team/files/item" and expected["groupXid"] == "/dirs/team",
                "The imported and independently expected fixture identities/documents disagree.")
        require(type(fixture["checker"]["expectedPasses"]) is int and fixture["checker"]["expectedPasses"] == 101
                and set(fixture["checker"]["expectedSuites"]) == accounting.SUITES,
                "Reverse checker coverage must retain its exact seeded six-suite obligations.")
    except (KeyError, TypeError, ValueError) as error:
        raise ReverseInteropError("Incomplete or malformed reverse fixture.") from error


def validate_seed_observation(fixture: dict, model: dict, groups: dict, resources: dict, versions: dict) -> None:
    try:
        require(set(model["groups"]) == {"dirs"} and set(model["groups"]["dirs"]["resources"]) == {"files"}
                and model["groups"]["dirs"]["resources"]["files"].get("hasdocument", True) is True,
                "The server does not expose the required document-bearing fixture model.")
        require(set(groups) == {"team"} and groups["team"]["dirid"] == "team"
                and groups["team"]["xid"] == fixture["expected"]["groupXid"],
                "The real xr import did not populate the expected Group.")
        require(set(resources) == {"item"} and resources["item"]["fileid"] == "item"
                and resources["item"]["xid"] == fixture["expected"]["resourceXid"],
                "The real xr import did not populate the expected Resource.")
        require(set(versions) == {"v1", "v2"} and versions["v1"]["versionid"] == "v1"
                and versions["v2"]["versionid"] == "v2" and versions["v1"]["isdefault"] is True
                and versions["v2"]["isdefault"] is False, "The server's Version/default state is incomplete.")
    except (KeyError, TypeError) as error:
        raise ReverseInteropError("The observed fixture is empty or malformed.") from error


def run_command(command: list[str], cwd: Path, environment: dict, timeout: float,
                max_output: int = MAX_OUTPUT_BYTES) -> subprocess.CompletedProcess:
    require(timeout > 0 and type(max_output) is int and max_output > 0, "Command limits must be finite and positive.")
    env = dict(environment)
    if os.name == "nt" and "SystemRoot" not in env:
        env["SystemRoot"] = os.environ.get("SystemRoot", r"C:\Windows")
    process = subprocess.Popen(command, cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    buffers = [bytearray(), bytearray()]
    exceeded = threading.Event()
    failures: list[OSError] = []

    def drain(stream, index: int) -> None:
        try:
            with stream:
                while chunk := stream.read(65536):
                    if len(buffers[index]) + len(chunk) > max_output:
                        exceeded.set()
                        if process.poll() is None:
                            process.kill()
                        return
                    buffers[index].extend(chunk)
        except OSError as error:
            failures.append(error)
            if process.poll() is None:
                process.kill()

    threads = [threading.Thread(target=drain, args=(process.stdout, 0), daemon=True),
               threading.Thread(target=drain, args=(process.stderr, 1), daemon=True)]
    for thread in threads:
        thread.start()
    timed_out = False
    try:
        process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        timed_out = True
        process.kill()
        process.wait(timeout=15)
    finally:
        for thread in threads:
            thread.join(timeout=15)
    require(not timed_out, "The owned command exceeded its deadline and was terminated.")
    require(not exceeded.is_set(), "The owned command exceeded its output byte limit and was terminated.")
    require(not failures and not any(thread.is_alive() for thread in threads), "The owned command output could not be captured completely.")
    return subprocess.CompletedProcess(command, process.returncode, bytes(buffers[0]), bytes(buffers[1]))


def checked_request(port: int, method: str, path: str, body: bytes | None = None,
                    content_type: str | None = None, *, expected_status: int = 200,
                    capture: Callable[[tuple[int, dict, bytes]], None] | None = None) -> tuple[int, dict, bytes]:
    try:
        result = server_helper.request(port, method, path, body, content_type)
    except (OSError, http.client.HTTPException) as error:
        raise ReverseInteropError(f"{method} {path}: the owned native endpoint is unavailable.") from error
    if capture is not None:
        capture(result)
    require(result[0] == expected_status, f"{method} {path} returned {result[0]}, expected {expected_status}.")
    return result


def write_json(path: Path, value: dict) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")
    temporary.replace(path)


def cli_environment(home: Path) -> dict:
    environment = os.environ.copy()
    for key in list(environment):
        if key.startswith("XR_"):
            environment.pop(key)
    environment.update(HOME=str(home), USERPROFILE=str(home), XDG_CONFIG_HOME=str(home),
                       NO_PROXY="127.0.0.1,localhost", no_proxy="127.0.0.1,localhost")
    return environment


def qualify(args: argparse.Namespace, report: dict) -> None:
    executable = args.server.resolve(strict=True)
    output = args.output.resolve()
    build = decode_json(args.oracle_build.resolve(strict=True).read_bytes())
    lock = decode_json((ROOT / "interop" / "reverse-toolchain.json").read_bytes())
    fixture = decode_json(args.fixture.resolve(strict=True).read_bytes())
    validate_fixture(fixture)
    require(build.get("status") == "passed" and build.get("commit") == lock["commit"]
            and build.get("version") == lock["expectedVersion"] and build.get("sourceModified") is False,
            "The oracle build evidence does not prove the pinned unmodified source.")
    xr = Path(build["binary"]).resolve(strict=True)
    require(hashlib.sha256(xr.read_bytes()).hexdigest() == build.get("binarySha256"),
            "The xr executable differs from its qualified build evidence.")
    host = "win" if os.name == "nt" else "linux" if sys.platform.startswith("linux") else "unsupported"
    rid = host + "-x64"
    require(platform.machine().lower() in ("amd64", "x86_64") and host != "unsupported", "This lane qualifies only Windows/Linux x64.")
    require(build.get("platform") == ("windows-amd64" if host == "win" else "linux-amd64"), "Oracle binary platform mismatch.")
    report.update(server=str(executable), serverSha256=hashlib.sha256(executable.read_bytes()).hexdigest(),
                  upstreamCommit=lock["commit"], oracleBuild=str(args.oracle_build.resolve()),
                  oracleSha256=build["binarySha256"], rid=rid, cwd=str(ROOT),
                  fixtureSha256=hashlib.sha256(args.fixture.read_bytes()).hexdigest())
    home = output / "cli-home"
    home.mkdir()
    config = home / ".xr"
    config.write_text("# Isolated reverse-interoperability configuration; no credentials.\n", encoding="utf-8")
    seed = output / "import.json"
    seed.write_text(json.dumps(fixture["importBody"], separators=(",", ":")) + "\n", encoding="utf-8")
    environment = cli_environment(home)
    commands = report["commands"]

    def command(name: str, arguments: list[str], *, native: bool = False) -> subprocess.CompletedProcess:
        invocation = [str(executable), *arguments] if native else [str(xr), "--config", str(config), *arguments]
        result = run_command(invocation, output, environment, args.command_timeout)
        stdout, stderr = output / f"{name}.stdout", output / f"{name}.stderr"
        stdout.write_bytes(result.stdout)
        stderr.write_bytes(result.stderr)
        commands.append({"case": name, "argv": invocation, "cwd": str(output), "exitCode": result.returncode,
                         "stdout": stdout.name, "stderr": stderr.name,
                         "stdoutBytes": len(result.stdout), "stderrBytes": len(result.stderr),
                         "stdoutSha256": hashlib.sha256(result.stdout).hexdigest()})
        return result

    def case(name: str, action: Callable[[], object]):
        try:
            value = action()
        except (ReverseInteropError, OSError, RuntimeError, ValueError, KeyError) as error:
            report["cases"].append({"name": name, "status": "failed", "error": str(error)})
            raise
        report["cases"].append({"name": name, "status": "passed"})
        write_json(output / "evidence.json", report)
        return value

    report["runtime"] = case("native-runtime", lambda: validate_native_runtime(command("runtime-info", ["--RuntimeInfo", "true"], native=True), rid))
    report["oracleVersion"] = case("pinned-cli-version", lambda: validate_cli_version(command("xr-version", ["--version"]), lock["expectedVersion"]))
    binary = base64.b64decode(fixture["expected"]["binaryBase64"], validate=True)
    resource = fixture["expected"]["resourceXid"]
    args.data_root.mkdir(parents=True, exist_ok=True)

    def observe(server, name: str, method: str, path: str, body: bytes | None = None, content_type: str | None = None) -> dict:
        def capture(response: tuple[int, dict, bytes]) -> None:
            status, headers, payload = response
            (output / f"{name}.body").write_bytes(payload)
            report["observations"].append({"name": name, "method": method, "path": path, "status": status,
                                           "requestBodyBase64": None if body is None else base64.b64encode(body).decode(),
                                           "headers": headers, "body": f"{name}.body"})
        _, _, payload = checked_request(server.port, method, path, body, content_type, capture=capture)
        return decode_json(payload)

    def xr_json(server, name: str, arguments: list[str]) -> dict:
        result = command(name, ["--server", server.root, *arguments])
        require(result.returncode == 0 and not result.stderr, f"{name}: real xr command failed.")
        return decode_json(result.stdout)

    def verify_meta(value: dict, root: str) -> None:
        require(value.get("xid") == resource + "/meta" and value.get("fileid") == "item"
                and value.get("defaultversionid") == "v1" and value.get("defaultversionsticky") is True
                and value.get("defaultversionurl") == root + resource + "/versions/v1$details",
                "The CLI's Resource Meta/default Version result differs from the explicit fixture.")

    def verify_details(value: dict, version: str, root: str) -> None:
        require(value.get("versionid") == version and value.get("xid") == resource + "/versions/" + version
                and value.get("fileid") == "item" and value.get("isdefault") is (version == "v1")
                and value.get("ancestorid") == "v1" and value.get("contenttype") == "application/octet-stream"
                and value.get("self") == root + resource + "/versions/" + version + "$details",
                "The CLI's explicit Version metadata differs from the fixture.")

    with tempfile.TemporaryDirectory(prefix="reverse-owned-store-", dir=args.data_root.resolve()) as temporary:
        data = Path(temporary) / "registry"
        with Server(executable, data, True, output / "server-initialize.log") as server:
            def discover():
                root = observe(server, "initial-root", "GET", "/registry")
                require(root.get("specversion") == fixture["specVersion"] and root.get("self") == server.root and root.get("xid") == "/",
                        "Native root/version/PublicRoot evidence is wrong.")
                for index, path in enumerate(("/.well-known/xregistry", "/registry/.xregistry")):
                    value = observe(server, f"discovery-{index}", "GET", path)
                    require(value == {"registries": [server.root]}, "Discovery did not retain the configured root under a spoofed Host.")
                for observation in report["observations"]:
                    require("spoofed.invalid" not in observation["headers"].get("link", ""), "Request Host contaminated advertised links.")
            case("trusted-root-and-discovery", discover)
            case("install-explicit-model", lambda: observe(server, "model-install", "PUT", "/registry/modelsource",
                 json.dumps(fixture["modelSource"], separators=(",", ":")).encode(), "application/json"))
            def import_fixture():
                result = command("xr-import", ["--server", server.root, "import", fixture["importTarget"], "--data", "@" + str(seed)])
                require(result.returncode == 0 and not result.stderr, "The unmodified xr import failed.")
            case("xr-import", import_fixture)
            def populated():
                model = observe(server, "fixture-model", "GET", "/registry/model")
                groups = observe(server, "fixture-groups", "GET", "/registry/dirs")
                resources = observe(server, "fixture-resources", "GET", "/registry/dirs/team/files")
                versions = observe(server, "fixture-versions", "GET", "/registry" + resource + "/versions")
                validate_seed_observation(fixture, model, groups, resources, versions)
                report["capabilities"] = observe(server, "capabilities", "GET", "/registry/capabilities")
                require("inline" in report["capabilities"].get("flags", []), "The qualified six-suite checker fixture requires actual inline support.")
            case("nonempty-fixture", populated)
            case("xr-get-collection", lambda: require(set(xr_json(server, "xr-get-collection", ["get", "/dirs"])) == {"team"}, "xr get returned an empty or changed collection."))
            for name, path, expected in (("xr-get-default-binary", resource, binary),
                                         ("xr-get-v1-binary", resource + "/versions/v1", binary),
                                         ("xr-get-v2-empty", resource + "/versions/v2", b"")):
                case(name, lambda name=name, path=path, expected=expected:
                     validate_document(command(name, ["--server", server.root, "get", path]), expected))
            case("xr-get-meta", lambda: verify_meta(xr_json(server, "xr-get-meta", ["get", resource + "/meta"]), server.root))
            for version in ("v1", "v2"):
                name = "xr-get-" + version + "-details"
                case(name, lambda name=name, version=version: verify_details(
                    xr_json(server, name, ["get", resource + "/versions/" + version, "--details"]), version, server.root))

        with Server(executable, data, False, output / "server-restart.log") as server:
            def restart_model():
                model = observe(server, "restart-model", "GET", "/registry/model")
                require(set(model.get("groups", {})) == {"dirs"}, "Restart lost the explicitly installed model.")
                root = observe(server, "restart-root", "GET", "/registry")
                require(root.get("self") == server.root, "Restart did not use the newly configured PublicRoot.")
            case("restart-model-and-context", restart_model)
            case("xr-restart-default", lambda: validate_document(command("xr-restart-default", ["--server", server.root, "get", resource]), binary))
            case("xr-restart-empty", lambda: validate_document(command("xr-restart-empty", ["--server", server.root, "get", resource + "/versions/v2"]), b""))
            case("xr-restart-meta", lambda: verify_meta(xr_json(server, "xr-restart-meta", ["get", resource + "/meta"]), server.root))
            def conform():
                result = command("xr-conform", ["--server", server.root, "conform", "--logs", "--warns", "--skips", "--depth", "0", "--nowrap"])
                report["knownLimitations"] = classify_checker_failure(result.stdout.decode("utf-8", errors="replace"))
                report["checker"] = {"status": "failed", "exitCode": result.returncode,
                                     "expectedPasses": fixture["checker"]["expectedPasses"],
                                     "observed": observed_checker_totals(result.stdout.decode("utf-8", errors="replace"))}
                parsed = validate_checker(result, fixture["checker"]["expectedPasses"])
                report["checker"] = {"status": "passed", **parsed, "exitCode": 0}
            case("xr-conform", conform)
    require([item["name"] for item in report["cases"]] == list(REQUIRED_CASES), "Reverse qualification did not execute the complete named case set.")
    require(all(item["status"] == "passed" for item in report["cases"]), "A required reverse case failed.")
    require(hashlib.sha256(executable.read_bytes()).hexdigest() == report["serverSha256"], "The native executable changed during qualification.")
    report.update(status="passed", qualified=True)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--oracle-build", type=Path, required=True)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--fixture", type=Path, default=ROOT / "interop" / "reverse-fixture.json")
    parser.add_argument("--command-timeout", type=float, default=120)
    args = parser.parse_args(argv)
    args.output.mkdir(parents=True, exist_ok=True)
    report = {"schemaVersion": 1, "direction": "pinned-upstream-xr-to-native-dotnet",
              "status": "running", "qualified": False, "passed": 0, "failed": 0,
              "cases": [], "commands": [], "observations": [], "knownLimitations": []}
    write_json(args.output / "evidence.json", report)
    code = 0
    try:
        require(0 < args.command_timeout <= 600, "A finite command timeout up to ten minutes is required.")
        qualify(args, report)
    except (ReverseInteropError, OSError, ValueError, KeyError, RuntimeError, subprocess.SubprocessError) as error:
        report.update(status="failed", qualified=False, error=str(error))
        code = 1
    finally:
        report["passed"] = sum(item["status"] == "passed" for item in report["cases"])
        report["failed"] = max(int(code != 0), sum(item["status"] == "failed" for item in report["cases"]))
        report["notExecuted"] = [name for name in REQUIRED_CASES if name not in {item["name"] for item in report["cases"]}]
        write_json(args.output / "evidence.json", report)
    print(json.dumps({"status": report["status"], "qualified": report["qualified"], "passed": report["passed"],
                      "failed": report["failed"], "knownLimitations": report["knownLimitations"],
                      "evidence": str((args.output / "evidence.json").resolve())}, ensure_ascii=True))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
