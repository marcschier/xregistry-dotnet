#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Verify the actual native durable HTTP sample, including killed-process restart and backup."""

from __future__ import annotations

import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
from urllib.parse import urlsplit


MODEL = {"groups": {"dirs": {"singular": "dir", "resources": {"files": {"singular": "file", "maxversions": 2}}}}}
BINARY = bytes.fromhex("00FF7F0D0A41")
GROUPS = {"categories", "endpoints", "messagegroups", "schemagroups", "usdassetgroups", "usdschemaplugingroups"}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def request(port: int, method: str, path: str, body: bytes | None = None,
            content_type: str | None = None) -> tuple[int, dict, bytes]:
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=15)
    try:
        headers = {"Host": "spoofed.invalid"}
        if content_type is not None:
            headers["Content-Type"] = content_type
        connection.request(method, path, body=body, headers=headers)
        response = connection.getresponse()
        data = response.read(16 * 1024 * 1024 + 1)
        require(len(data) <= 16 * 1024 * 1024, "A server response exceeded the fixture limit.")
        return response.status, {name.lower(): value for name, value in response.getheaders()}, data
    finally:
        connection.close()


def free_port() -> int:
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


class Server:
    def __init__(self, executable: Path, data: Path, initialize: bool, log: Path):
        self.port = free_port()
        self.root = f"http://127.0.0.1:{self.port}/registry"
        environment = os.environ.copy()
        for key in ("CertificatePath", "CertificatePasswordEnvironment", "WriteTokenEnvironment",
                    "ReadTokenEnvironment", "ModelFile", "InitialMetadata", "MountPath",
                    "BackupTo", "RuntimeInfo", "RegistryId"):
            environment.pop(key, None)
        self.log = log.open("wb")
        self.process = subprocess.Popen([
            str(executable), "--DataRoot", str(data), "--Initialize", str(initialize).lower(),
            "--DemoLoopback", "true", "--PublicRoot", self.root, "--ListenPort", str(self.port),
            "--Model", "all",
        ], stdout=self.log, stderr=subprocess.STDOUT, env=environment)
        try:
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                if self.process.poll() is not None:
                    raise RuntimeError(f"The native server exited during startup; see {log}.")
                try:
                    if request(self.port, "GET", "/registry")[0] == 200:
                        return
                except (OSError, http.client.HTTPException):
                    pass
                time.sleep(0.05)
            raise RuntimeError(f"The native server did not become responsive; see {log}.")
        except BaseException:
            self.close()
            raise

    def close(self) -> None:
        if self.process.poll() is None:
            self.process.kill()
            self.process.wait(timeout=15)
        self.log.close()

    def __enter__(self) -> "Server":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--managed-control", type=Path)
    args = parser.parse_args()
    executable = args.server.resolve(strict=True)
    args.data_root.mkdir(parents=True, exist_ok=True)
    args.output.mkdir(parents=True, exist_ok=True)
    report = args.output / "evidence.json"
    report.write_text('{"status":"running","passed":0}\n', encoding="utf-8")
    checks: list[str] = []
    runtime = json.loads(subprocess.run(
        [str(executable), "--RuntimeInfo", "true"], capture_output=True, check=True, timeout=20,
    ).stdout)
    require(runtime.get("nativeAot") is True and type(runtime.get("jitCompiledMethods")) is int
            and runtime["jitCompiledMethods"] == 0, "The server sample is not actually Native AOT.")
    checks.append("actual-native-runtime")
    if args.managed_control is not None:
        managed = json.loads(subprocess.run(
            ["dotnet", str(args.managed_control.resolve(strict=True)), "--RuntimeInfo", "true"],
            capture_output=True, check=True, timeout=20,
        ).stdout)
        require(managed.get("nativeAot") is False and managed.get("jitCompiledMethods", 0) > 0,
                "The managed server control incorrectly claimed native execution.")
        checks.append("jit-negative-control")
    with tempfile.TemporaryDirectory(prefix="xregistry-http-store-", dir=args.data_root.resolve()) as temporary:
        data = Path(temporary) / "primary"
        backup = Path(temporary) / "backup"
        with Server(executable, data, True, args.output / "initialize.log") as server:
            status, headers, payload = request(server.port, "GET", "/registry")
            root = json.loads(payload)
            require(status == 200 and root["specversion"] == "1.0-rc4", "The durable HTTP root has the wrong version.")
            require(root["self"] == server.root and "spoofed.invalid" not in headers.get("link", ""),
                    "The sample trusted the request Host instead of PublicRoot.")
            checks.append("trusted-root-and-version")
            status, _, body = request(server.port, "GET", "/registry/model")
            require(status == 200 and set(json.loads(body)["groups"]) == GROUPS, "The default model omitted a scoped domain.")
            checks.append("all-domain-default-model")
            catalog_path = "/registry/categories/probe/registries/site"
            status, _, body = request(server.port, "PUT", catalog_path,
                                      b'{"xregurl":"relative/root"}', "application/json")
            require(status == 400 and json.loads(body).get("code") == "invalid_attribute",
                    "The default server preset accepted an invalid catalog Registry root.")
            status, _, body = request(server.port, "GET", "/registry")
            require(status == 200 and json.loads(body)["categoriescount"] == 0,
                    "Rejected catalog publication left an implicitly created Group.")
            checks.append("catalog-preset-rejects-invalid-publication")
            status, _, body = request(server.port, "PUT", catalog_path,
                                      b'{"weburl":"https://example.com/"}', "application/json")
            require(status == 201 and json.loads(body)["weburl"] == "https://example.com/",
                    "The catalog server preset rejected a valid website-only base description.")
            status, _, _ = request(server.port, "DELETE", "/registry/categories/probe")
            require(status in (200, 204), "The native catalog fixture could not be removed.")
            checks.append("catalog-base-description-without-resolution")
            status, _, body = request(server.port, "GET", "/.well-known/xregistry")
            require(status == 200 and json.loads(body) == {"registries": [server.root]},
                    "Host discovery is not the normative root-scoped array.")
            status, _, body = request(server.port, "GET", "/registry/.xregistry")
            require(status == 200 and json.loads(body) == {"registries": [server.root]}, "Registry discovery is incorrect.")
            checks.append("both-discovery-locations")
            status, _, _ = request(server.port, "PUT", "/registry/modelsource", json.dumps(MODEL).encode(), "application/json")
            require(status == 200, "The sample could not install an arbitrary custom model.")
            checks.append("custom-model-administration")
            path = "/registry/dirs/group/files/resource/versions/v1"
            status, headers, body = request(server.port, "PUT", path, BINARY, "application/octet-stream")
            require(status == 201 and body == BINARY and headers.get("xregistry-xregcorrelationid"),
                    "The native server did not acknowledge exact binary creation and correlated commit.")
            checks.append("exact-binary-creation")
            status, _, body = request(server.port, "GET", path + "$details")
            details = json.loads(body)
            require(status == 200 and details["versionid"] == "v1", "Version metadata was not selected with details.")
            status, _, _ = request(server.port, "PATCH", path + "$details",
                                   json.dumps({"epoch": details["epoch"], "name": "retained"}).encode(), "application/json")
            require(status == 200, "Metadata-only update was rejected.")
            status, _, body = request(server.port, "GET", path)
            require(status == 200 and body == BINARY, "Metadata-only update lost its preserved Document.")
            checks.append("metadata-update-preserves-document")
            status, _, body = request(server.port, "GET", "/registry/capabilities")
            require(status == 200 and json.loads(body)["available"]["capabilities"]["mutable"] is True,
                    "The full server did not enable authenticated capability administration.")
            status, _, _ = request(server.port, "PATCH", "/registry/capabilities",
                                   b'{"shortself":true}', "application/json")
            require(status == 200, "The implemented shortself capability could not be enabled.")
            status, _, body = request(server.port, "GET", path + "$details")
            short = json.loads(body)["shortself"]
            short_path = urlsplit(short).path
            require(short.startswith(server.root + "/~/"), "Shortself did not use the trusted Registry namespace.")
            status, _, body = request(server.port, "GET", short_path)
            require(status == 200 and body == BINARY, "Shortself did not preserve its unsuffixed Document semantics.")
            status, _, body = request(server.port, "GET", short_path + "$details")
            require(status == 200 and json.loads(body)["versionid"] == "v1", "Shortself details did not resolve its canonical Version metadata.")
            checks.append("capability-administration-and-shortself")
            status, _, body = request(server.port, "PUT", "/registry/dirs/group/files/resource/versions/v2", b"",
                                      "application/octet-stream")
            require(status == 201 and body == b"", "A present-empty Document was not created.")
            checks.append("present-empty-document")
            status, _, _ = request(server.port, "PATCH", "/registry", b'{"dirs":{"never":{"undeclared":true}}}', "application/json")
            require(status == 400, "The invalid nested mutation was not rejected.")
            status, _, body = request(server.port, "GET", "/registry/dirs")
            require(status == 200 and set(json.loads(body)) == {"group"}, "A failed nested mutation leaked partial parents.")
            checks.append("nested-request-rollback")
            status, _, body = request(server.port, "GET", "/registry/export")
            export = json.loads(body)
            versions = export["dirs"]["group"]["files"]["resource"]["versions"]
            require(status == 200 and set(versions) == {"v1", "v2"}, "Export omitted committed Versions.")
            checks.append("document-view-export")
            status, _, body = request(server.port, "GET", "/healthz")
            require(status == 200 and json.loads(body)["generation"] > 0, "Protected native storage health failed.")
            checks.append("protected-storage-health")

        with Server(executable, data, False, args.output / "restart.log") as server:
            status, _, body = request(server.port, "GET", "/registry/model")
            require(status == 200 and set(json.loads(body)["groups"]) == {"dirs"},
                    "Restart discarded the persisted frozen custom model.")
            status, _, body = request(server.port, "GET", "/registry/dirs/group/files/resource/versions/v1")
            require(status == 200 and body == BINARY, "An acknowledged Document did not survive native process termination.")
            status, _, body = request(server.port, "GET", "/registry/dirs/group/files/resource/versions/v2")
            require(status == 200 and body == b"", "Restart lost present-empty content.")
            status, _, body = request(server.port, "GET", short_path + "$details")
            require(status == 200 and json.loads(body)["versionid"] == "v1", "Restart lost the persisted short URL mapping.")
            checks.append("killed-process-restart")
        backup.mkdir()
        subprocess.run([str(executable), "--DataRoot", str(data), "--BackupTo", str(backup)],
                       check=True, capture_output=True, timeout=30)
        with Server(executable, backup, False, args.output / "restore.log") as server:
            status, _, body = request(server.port, "GET", "/registry/dirs/group/files/resource/versions/v1")
            require(status == 200 and body == BINARY, "The independently reopened backup lost exact Document bytes.")
            checks.append("offline-backup-and-restore")
    with executable.open("rb") as binary:
        digest = hashlib.file_digest(binary, "sha256").hexdigest()
    report.write_text(json.dumps({
        "schemaVersion": 1, "status": "passed", "serverSha256": digest, "runtime": runtime,
        "passed": len(checks), "failed": 0, "checks": checks,
        "scope": "Native durable HTTP in explicit loopback demo; production TLS/auth are separately exercised by SampleHosting tests.",
    }, indent=2) + "\n", encoding="utf-8")
    print(f"Verified {len(checks)} native durable-server checks.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
