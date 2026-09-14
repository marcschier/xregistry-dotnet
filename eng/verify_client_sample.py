#!/usr/bin/env python3
"""Exercise the native client sample against independently frozen, local fixtures."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[1]
FIXTURES = ROOT / "tests" / "Conformance" / "Sources" / "workingdrafts" / "federation" / "samples" / "oci"


def run(client: Path, *arguments: str, expected_exit: int = 0) -> bytes:
    result = subprocess.run(
        [str(client), *arguments], capture_output=True, timeout=60, check=False,
    )
    if result.returncode != expected_exit:
        raise RuntimeError(
            f"Client command {arguments[0]} exited {result.returncode}, expected {expected_exit}: "
            + result.stderr.decode("utf-8", errors="replace")[:2000]
        )
    if expected_exit != 0 and not result.stderr.strip():
        raise RuntimeError("A failed sample command omitted its diagnostic.")
    return result.stdout


def make_capture(directory: Path, selected: dict) -> Path:
    layout = FIXTURES / "layout"
    records: list[dict] = []
    documents: dict = {}
    visited: set[str] = set()

    def blob(descriptor: dict) -> bytes:
        digest = descriptor["digest"]
        algorithm, hexadecimal = digest.split(":", 1)
        if algorithm != "sha256" or len(hexadecimal) != 64 or any(c not in "0123456789abcdef" for c in hexadecimal):
            raise RuntimeError("The frozen fixture contains an invalid SHA-256 descriptor.")
        data = (layout / "blobs" / "sha256" / hexadecimal).read_bytes()
        if len(data) != descriptor["size"] or hashlib.sha256(data).hexdigest() != hexadecimal:
            raise RuntimeError("The independent OCI fixture no longer matches its descriptor.")
        return data

    def walk(descriptor: dict) -> None:
        if descriptor["digest"] in visited:
            return
        if len(visited) >= 1024:
            raise RuntimeError("The fixture exceeded the test driver's finite graph bound.")
        visited.add(descriptor["digest"])
        value = json.loads(blob(descriptor))
        if descriptor["mediaType"] == "application/vnd.oci.image.index.v1+json":
            for child in value["manifests"]:
                walk(child)
            return
        if descriptor["mediaType"] != "application/vnd.oci.image.manifest.v1+json":
            raise RuntimeError("Unexpected fixture graph role.")
        record = json.loads(blob(value["config"]))
        records.append(record)
        if record["kind"] == "version" and record["document"]["mode"] == "embedded":
            if len(value["layers"]) != 1:
                raise RuntimeError("An embedded fixture Version does not have exactly one Document layer.")
            content = blob(value["layers"][0])
            filename = f"document-{len(documents)}.bin"
            (directory / filename).write_bytes(content)
            entity = record["entity"]
            item = {"file": filename}
            if "contenttype" in entity:
                item["contenttype"] = entity["contenttype"]
            for name in ("base", "origin"):
                if name in record["document"]:
                    item[name] = record["document"][name]
            documents[entity["xid"]] = item

    walk({
        "digest": selected["digest"],
        "size": selected["size"],
        "mediaType": "application/vnd.oci.image.index.v1+json",
    })
    capture = directory / "capture.json"
    capture.write_text(json.dumps({"records": records, "documents": documents}), encoding="utf-8")
    return capture


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--managed-control", type=Path)
    args = parser.parse_args()
    client = args.client.resolve(strict=True)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text('{"status":"running","passed":0}\n', encoding="utf-8")
    checks = []
    runtime = json.loads(run(client, "runtime-info"))
    if runtime.get("nativeAot") is not True or type(runtime.get("jitCompiledMethods")) is not int or runtime["jitCompiledMethods"] != 0:
        raise RuntimeError("The client sample must actually execute as Native AOT.")
    checks.append("actual-native-runtime")
    if args.managed_control is not None:
        control = subprocess.run(
            ["dotnet", str(args.managed_control.resolve(strict=True)), "runtime-info"],
            capture_output=True, timeout=60, check=True,
        )
        control_runtime = json.loads(control.stdout)
        if control_runtime.get("nativeAot") is not False or control_runtime.get("jitCompiledMethods", 0) <= 0:
            raise RuntimeError("The managed negative control masqueraded as Native AOT.")
        checks.append("jit-negative-control")
    model = json.loads(run(client, "model", "cloudevents"))
    if set(model["groups"]) != {"endpoints", "messagegroups", "schemagroups"}:
        raise RuntimeError("The client did not compile the actual composite CloudEvents model.")
    checks.append("composite-model")

    expected = json.loads((FIXTURES / "expected.json").read_text(encoding="utf-8"))
    layout = str(FIXTURES / "layout")
    for reference in ("offline", "linked"):
        actual = json.loads(run(client, "verify-oci-layout", layout, reference))
        selected = expected["roots"][reference]
        required = {
            "rootDigest": selected["digest"], "snapshotClass": selected["snapshot"],
            "objects": selected["objects"], "indexes": selected["indexes"],
            "manifests": selected["manifests"], "configs": selected["records"],
            "documents": selected["documents"],
        }
        if actual != required:
            raise RuntimeError("Full layout validation differs from the independent closure inventory.")
        checks.append(f"{reference}-exact-closure")

    for target, content in (
        ("/dirs/main/files/sample", b'{"type":"string"}\n'),
        ("/dirs/main/files/sample/versions/v2", b'{"type":"number"}\n'),
        ("/dirs/main/files/binary", bytes.fromhex("00017F80FF0D0A")),
        ("/dirs/main/files/empty", b""),
        ("/imports/shared/files/alias", b'{"type":"string"}\n'),
    ):
        actual = run(client, "read-oci-layout", layout, "offline", target, "--operation", "document")
        if actual != content:
            raise RuntimeError(f"The native client changed Document bytes at {target}.")
        checks.append(target)
    run(client, "read-oci-layout", layout, "offline", "/dirs/main/notes/info",
        "--operation", "document", expected_exit=1)
    checks.append("metadata-only-is-not-empty-document")
    run(client, "read-oci-layout", layout, "nonexistent", "/", expected_exit=1)
    checks.append("missing-reference-has-no-fallback")

    with tempfile.TemporaryDirectory(prefix="xregistry-client-capture-") as temporary:
        capture = make_capture(Path(temporary), expected["roots"]["offline"])
        result = json.loads(run(client, "verify-oci-capture", str(capture)))
        if result["configs"] != 25 or result["documents"] != 4 or result["snapshotClass"] != "offline-complete":
            raise RuntimeError("Producer input capture did not retain the independent records and Documents.")
        checks.append("verified-portable-capture")
        invalid = json.loads(capture.read_text(encoding="utf-8"))
        first = next(iter(invalid["documents"]))
        invalid["documents"][first]["file"] = "../outside.bin"
        capture.write_text(json.dumps(invalid), encoding="utf-8")
        run(client, "verify-oci-capture", str(capture), expected_exit=1)
        checks.append("capture-document-containment")

    with client.open("rb") as binary:
        digest = hashlib.file_digest(binary, "sha256").hexdigest()
    args.report.write_text(json.dumps({
        "schemaVersion": 1, "status": "passed", "clientSha256": digest,
        "runtime": runtime, "passed": len(checks), "failed": 0, "checks": checks,
        "scope": "Native sample local fixtures and producer preparation, not live remote publication.",
    }, indent=2) + "\n", encoding="utf-8")
    print(f"Verified {len(checks)} native client-sample checks.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
