#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Bounded, validated native durable FileServer end-to-end performance workload."""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import http.client
import importlib.util
import json
import math
import os
from pathlib import Path
import platform
import re
import statistics
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass
from urllib.parse import urljoin, urlsplit


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("performance_owned_server", ROOT / "eng" / "verify_file_server.py")
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("The owned native server helper is unavailable.")
server_helper = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = server_helper
SPEC.loader.exec_module(server_helper)
COLLECTION = "/dirs/perf/records"


class PerformanceError(RuntimeError):
    """A workload, measurement or correctness obligation was not satisfied."""


def require(value: bool, message: str) -> None:
    if not value:
        raise PerformanceError(message)


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for name, value in pairs:
        require(name not in result, "Duplicate JSON members are not valid workload evidence.")
        result[name] = value
    return result


def bad_number(_: str):
    raise PerformanceError("Non-finite JSON numbers are not valid evidence.")


def parse_json(raw: bytes) -> dict:
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object, parse_constant=bad_number)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise PerformanceError("Malformed JSON workload response.") from error
    require(isinstance(value, dict), "A workload response must be a JSON object.")
    return value


def proof_value(result: subprocess.CompletedProcess) -> dict:
    require(result.returncode == 0 and not result.stderr and len(result.stdout) <= 65536,
            "Runtime proof must exit successfully with bounded clean output.")
    value = parse_json(result.stdout)
    require(value.get("architecture") == "X64" and type(value.get("jitCompiledMethods")) is int,
            "Runtime proof requires the actual x64 architecture and integer JIT count.")
    return value


def native_proof(result: subprocess.CompletedProcess) -> dict:
    value = proof_value(result)
    require(value.get("nativeAot") is True and value["jitCompiledMethods"] == 0,
            "Managed execution is not a native performance baseline.")
    return value


def managed_proof(result: subprocess.CompletedProcess) -> dict:
    value = proof_value(result)
    require(value.get("nativeAot") is False and value["jitCompiledMethods"] > 0,
            "The JIT negative control must actually compile managed methods.")
    return value


def validate_profile(profile: dict, allow_larger: bool = False) -> None:
    try:
        for name in ("schemaVersion", "rounds", "warmupIterations", "readIterations", "collectionIterations",
                     "documentIterations", "writeIterations", "seedBatchSize", "pageSize", "concurrency",
                     "smallDocumentBytes", "largeDocumentBytes"):
            require(type(profile[name]) is int, "Profile numeric fields must be integers, not booleans or floating values.")
        require(all(type(value) is int for value in profile["limits"].values()),
                "Budget metadata must use exact integer limits.")
        require(type(profile["schemaVersion"]) is int and profile["schemaVersion"] == 1
                and profile["storage"] == "durable" and profile["concurrency"] == 1,
                "This profile qualifies only single-concurrency durable server workloads.")
        sizes = profile["resourceCounts"]
        require(isinstance(sizes, list) and sizes and len(sizes) <= 4 and
                all(type(n) is int and 1 <= n <= (10000 if allow_larger else 1000) for n in sizes)
                and sizes == sorted(set(sizes)), "Resource scales must be positive, distinct, increasing and explicitly bounded.")
        require(profile["writeResourceCounts"] and set(profile["writeResourceCounts"]).issubset(sizes),
                "Write scales must explicitly select supported Resource scales.")
        limits = profile["limits"]
        require(1 <= profile["rounds"] <= min(limits["maxRounds"], 5), "Round count exceeds its developer budget.")
        require(1 <= profile["warmupIterations"] <= 20, "A finite nonempty warmup is required.")
        for name in ("readIterations", "collectionIterations", "documentIterations", "writeIterations"):
            require(type(profile[name]) is int and 1 <= profile[name] <= min(limits["maxIterations"], 100),
                    "A scenario has an invalid iteration count.")
        require(type(profile["seedBatchSize"]) is int and 1 <= profile["seedBatchSize"] <= 50,
                "Seed batching must preserve shipping mutation limits.")
        require(type(profile["pageSize"]) is int and 1 <= profile["pageSize"] <= 1000, "Invalid page size.")
        require(profile["smallDocumentBytes"] == 256 and profile["largeDocumentBytes"] == 1048576,
                "The developer document sizes are fixed at 256 bytes and 1MiB.")
        require(0 < limits["maxElapsedSeconds"] <= 1800 and 0 < limits["maxRequests"] <= 10000
                and 1048576 <= limits["maxResponseBytes"] <= 16 * 1024 * 1024
                and 0 < limits["maxStoreBytes"] <= 1024 * 1024 * 1024
                and 0 < limits["maxStoreFiles"] <= 50000,
                "Workload request/time/response/disk budgets must be finite.")
        require(max(sizes) <= limits["maxResourceCount"] and (allow_larger or limits["maxResourceCount"] <= 1000),
                "Larger scales require an explicit opt-in profile and --allow-larger.")
        resources = profile["modelSource"]["groups"]["dirs"]["resources"]
        require(resources["records"]["hasdocument"] is False and resources["files"].get("hasdocument", True) is True,
                "The workload requires separate metadata-only and document-bearing types.")
        require(profile["comparisonPolicy"]["investigationThresholdPercent"] == 10
                and profile["comparisonPolicy"]["hardCiGate"] is False
                and profile["comparisonPolicy"]["deploymentSlo"] is False,
                "The comparison policy is an investigation trigger, not a deployment SLO.")
    except (KeyError, TypeError, ValueError) as error:
        raise PerformanceError("The performance profile is incomplete or malformed.") from error


def summarize(samples: list[int], expected_count: int, elapsed_ns: int) -> dict:
    require(type(expected_count) is int and expected_count > 0 and len(samples) == expected_count,
            "Empty or truncated samples cannot become a performance result.")
    require(all(type(value) is int and value > 0 for value in samples),
            "Every latency sample must be a positive integer nanosecond value.")
    require(type(elapsed_ns) is int and elapsed_ns >= sum(samples), "Invalid validated batch elapsed time.")
    ordered = sorted(samples)
    percentile = lambda p: ordered[math.ceil(p * len(ordered)) - 1] / 1_000_000
    return {
        "samples": len(samples), "p50Ms": percentile(0.50), "p95Ms": percentile(0.95),
        "p99Ms": percentile(0.99), "meanMs": statistics.mean(samples) / 1_000_000,
        "minMs": ordered[0] / 1_000_000, "maxMs": ordered[-1] / 1_000_000,
        "validatedWallSeconds": elapsed_ns / 1_000_000_000,
        "validatedOperationsPerSecond": len(samples) * 1_000_000_000 / elapsed_ns,
        "percentileMethod": "nearest-rank",
    }


def compare_metric(baseline: float, current: float, *, lower_is_better: bool) -> dict:
    require(math.isfinite(baseline) and baseline > 0 and math.isfinite(current) and current >= 0,
            "A comparison requires actual finite baseline/current values.")
    delta = (current - baseline) / baseline * 100 if lower_is_better else (baseline - current) / baseline * 100
    return {"regressionPercent": delta, "investigateIfRepeatable": delta > 10 + 1e-9, "hardGate": False}


@dataclass
class Response:
    status: int
    headers: dict
    body: bytes
    size: int
    sha256: str
    elapsed_ns: int
    target: str = ""
    method: str = ""


def validate_record(value: dict, record_id: str, ordinal: int, payload: str, epoch: int,
                    root: str, *, version: bool = False) -> None:
    xid = COLLECTION + "/" + record_id + ("/versions/v1" if version else "")
    require(value.get("recordid") == record_id and value.get("versionid") == "v1"
            and value.get("xid") == xid and value.get("self") == root + xid
            and type(value.get("ordinal")) is int and value["ordinal"] == ordinal
            and value.get("payload") == payload and type(value.get("epoch")) is int and value["epoch"] == epoch
            and value.get("isdefault") is True, "Metadata identity, exact epoch or fixture payload is incorrect.")


def validate_document(response: Response, size: int, digest: str, xid: str) -> None:
    require(response.status == 200 and response.size == size and response.sha256 == digest,
            "Document status, exact byte length or digest is incorrect.")
    require(response.headers.get("content-type", "").split(";", 1)[0].strip().lower() == "application/octet-stream"
            and response.headers.get("content-length") == str(size)
            and response.headers.get("xregistry-epoch") == "0"
            and response.headers.get("xregistry-versionid") == "v1"
            and response.headers.get("xregistry-xid") == xid
            and not response.headers.get("content-encoding"),
            "Document media type, epoch, identity or exact-length headers are incorrect.")


def validate_collection_ids(pages: list[set[str]], expected: set[str]) -> None:
    require(bool(expected) and bool(pages), "An empty workload is not a collection performance result.")
    seen: set[str] = set()
    for page in pages:
        require(not seen.intersection(page), "Pagination repeated a Resource.")
        seen.update(page)
    require(seen == expected, "The measured collection is truncated or contains unexpected Resources.")


def next_link(header: str, current: str) -> str | None:
    links = re.findall(r'<([^>]*)>\s*;\s*rel=(?:"([^"]+)"|([^;,\s]+))', header)
    selected = [uri for uri, quoted, bare in links if "next" in (quoted or bare).split()]
    require(len(selected) <= 1, "More than one next relation is not a deterministic page chain.")
    if not selected:
        return None
    uri = urljoin(current, selected[0])
    old, new = urlsplit(current), urlsplit(uri)
    require(new.scheme == old.scheme and new.netloc == old.netloc and new.path == old.path
            and new.username is None and new.password is None and not new.fragment,
            "Pagination cannot leave the owned origin/collection.")
    return uri


class Budget:
    def __init__(self, limits: dict):
        self.limits = limits
        self.started = time.monotonic()
        self.requests = 0

    def check(self) -> None:
        require(time.monotonic() - self.started < self.limits["maxElapsedSeconds"], "The workload exceeded its wall-time budget.")

    def request(self) -> None:
        self.check()
        self.requests += 1
        require(self.requests <= self.limits["maxRequests"], "The workload exceeded its request budget.")


class Client:
    def __init__(self, port: int, budget: Budget):
        self.port = port
        self.root = f"http://127.0.0.1:{port}/registry"
        self.budget = budget
        self.connection = http.client.HTTPConnection("127.0.0.1", port, timeout=15)

    def close(self) -> None:
        self.connection.close()

    def request(self, method: str, path: str, body: bytes | dict | None = None,
                content_type: str = "application/json", *, stream: bool = False) -> Response:
        self.budget.request()
        if isinstance(body, dict):
            body = json.dumps(body, separators=(",", ":"), ensure_ascii=True).encode()
        headers = {"Host": f"127.0.0.1:{self.port}", "Accept-Encoding": "identity"}
        if body is not None:
            headers["Content-Type"] = content_type
        parsed = urlsplit(path)
        if parsed.scheme:
            require(parsed.scheme == "http" and parsed.netloc == f"127.0.0.1:{self.port}" and not parsed.fragment,
                    "A workload request left the owned loopback origin.")
            path = parsed.path + ("?" + parsed.query if parsed.query else "")
        else:
            path = "/registry" + path
        start = time.perf_counter_ns()
        try:
            self.connection.request(method, path, body=body, headers=headers)
            response = self.connection.getresponse()
            received = bytearray()
            digest = hashlib.sha256()
            size = 0
            while chunk := response.read(64 * 1024):
                size += len(chunk)
                require(size <= self.budget.limits["maxResponseBytes"], "Response exceeded its byte bound.")
                digest.update(chunk)
                if not stream:
                    received.extend(chunk)
            elapsed = time.perf_counter_ns() - start
            values = {name.lower(): value for name, value in response.getheaders()}
            if "content-length" in values:
                require(values["content-length"].isdigit() and int(values["content-length"]) == size,
                        "A response was truncated or contradicted Content-Length.")
            require(not values.get("content-encoding"), "The workload does not silently measure transformed documents.")
            return Response(response.status, values, bytes(received), size, digest.hexdigest(), elapsed, path, method)
        except (OSError, http.client.HTTPException) as error:
            raise PerformanceError(f"{method} {path}: native server request failed.") from error


def json_response(response: Response, expected_status: int = 200, *, mutation: bool = False) -> dict:
    require(response.status == expected_status,
            f"{response.method} {response.target}: HTTP {response.status}, expected {expected_status}; "
            f"body={response.body[:1024].decode('utf-8', errors='replace')}")
    require(response.headers.get("content-type", "").split(";", 1)[0].strip().lower() == "application/json",
            "Metadata response has an unexpected media type.")
    if mutation:
        require(bool(response.headers.get("xregistry-xregcorrelationid")), "A mutation lacks its committed correlation header.")
    return parse_json(response.body)


def memory_snapshot(pid: int) -> dict:
    if os.name == "nt":
        from ctypes import wintypes

        class Counters(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                        *[(name, ctypes.c_size_t) for name in (
                            "PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
                            "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage",
                            "PagefileUsage", "PeakPagefileUsage", "PrivateUsage")]]
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        psapi = ctypes.WinDLL("psapi", use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
        psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
        handle = kernel.OpenProcess(0x0400 | 0x0010, False, pid)
        if not handle:
            return {"supported": False, "reason": f"OpenProcess failed: {ctypes.get_last_error()}"}
        try:
            value = Counters()
            value.cb = ctypes.sizeof(value)
            if not psapi.GetProcessMemoryInfo(handle, ctypes.byref(value), value.cb):
                return {"supported": False, "reason": f"GetProcessMemoryInfo failed: {ctypes.get_last_error()}"}
            return {"supported": True, "workingSetBytes": value.WorkingSetSize,
                    "peakWorkingSetBytes": value.PeakWorkingSetSize, "privateCommittedBytes": value.PrivateUsage,
                    "source": "GetProcessMemoryInfo"}
        finally:
            kernel.CloseHandle(handle)
    if sys.platform.startswith("linux"):
        try:
            fields = {}
            for line in Path(f"/proc/{pid}/status").read_text().splitlines():
                if line.startswith(("VmRSS:", "VmHWM:", "VmSize:")):
                    name, value, _ = line.split()
                    fields[name[:-1]] = int(value) * 1024
            return {"supported": True, "workingSetBytes": fields["VmRSS"], "peakWorkingSetBytes": fields["VmHWM"],
                    "virtualBytes": fields["VmSize"], "privateCommittedBytes": None,
                    "privateCommittedReason": "Not inferred from RSS or virtual size.", "source": "/proc/PID/status"}
        except (OSError, KeyError, ValueError) as error:
            return {"supported": False, "reason": str(error)}
    return {"supported": False, "reason": "Process-memory collection is implemented only for Windows/Linux."}


def disk_snapshot(path: Path, budget: Budget) -> dict:
    size, files = 0, 0
    for directory, directories, names in os.walk(path, followlinks=False):
        budget.check()
        require(not any((Path(directory) / name).is_symlink() for name in directories + names),
                "The owned measurement store contains a redirected path.")
        for name in names:
            size += (Path(directory) / name).stat().st_size
            files += 1
            require(size <= budget.limits["maxStoreBytes"] and files <= budget.limits["maxStoreFiles"],
                    "The owned store exceeded its disk/file budget.")
    return {"logicalBytes": size, "files": files, "allocatedBytes": None,
            "allocatedBytesReason": "Logical file lengths are measured; filesystem physical allocation is not inferred."}


def host_info(data_root: Path) -> dict:
    result = {"platform": platform.platform(), "machine": platform.machine(), "logicalProcessors": os.cpu_count(),
              "python": sys.version, "sharedHost": True, "affinityPinned": False, "priorityChanged": False}
    sdk = subprocess.run(["dotnet", "--version"], capture_output=True, timeout=20, check=True)
    result["sdk"] = sdk.stdout.decode("utf-8").strip()
    if os.name == "nt":
        drive = data_root.drive[:1]
        require(re.fullmatch("[A-Za-z]", drive) is not None, "An ordinary local drive is required.")
        script = (
            "[PSCustomObject]@{Processors=@(Get-CimInstance Win32_Processor | "
            "Select-Object Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed);"
            "PhysicalMemoryBytes=(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory;"
            "OS=(Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber);"
            f"Volume=(Get-Volume -DriveLetter '{drive}' | Select-Object DriveLetter,FileSystem,DriveType,Size,SizeRemaining);"
            f"Disk=@(Get-Partition -DriveLetter '{drive}' | Get-Disk | Select-Object FriendlyName,BusType,Size)"
            "} | ConvertTo-Json -Depth 5"
        )
        probe = subprocess.run(["pwsh", "-NoProfile", "-Command", script], capture_output=True, timeout=30)
        if probe.returncode == 0:
            result["hardwareStorage"] = parse_json(probe.stdout)
        else:
            result["hardwareStorage"] = {"supported": False, "reason": "The read-only CIM/storage query failed."}
    else:
        probe = subprocess.run(["findmnt", "--noheadings", "--output", "FSTYPE,SOURCE,OPTIONS",
                                "--target", str(data_root)], capture_output=True, timeout=20)
        result["storage"] = probe.stdout.decode("utf-8").strip() if probe.returncode == 0 else "unavailable"
    return result


def document_bytes(size: int) -> bytes:
    return (bytes(range(256)) * ((size + 255) // 256))[:size]


def write_report(path: Path, value: dict) -> None:
    temporary = path.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")
    temporary.replace(path)


def measure_round(executable: Path, data_parent: Path, output: Path, size: int, round_number: int,
                  profile: dict, budget: Budget, expected_hash: str) -> dict:
    require(hashlib.sha256(executable.read_bytes()).hexdigest() == expected_hash, "The native artifact changed between rounds.")
    record = {"resourceCount": size, "round": round_number, "scenarios": []}
    payload = profile["metadataPayload"]
    expected_ids = {f"r{i:06}" for i in range(size)}
    with tempfile.TemporaryDirectory(prefix=f"native-perf-{size}-", dir=data_parent) as temporary:
        store = Path(temporary) / "registry"
        started = time.perf_counter_ns()
        with server_helper.Server(executable, store, True, output / f"server-{size}-{round_number}.log") as server:
            record["startupMs"] = (time.perf_counter_ns() - started) / 1_000_000
            record["startupDefinition"] = "Owned process launch to API-ready, 50ms readiness polling; not cold machine startup."
            client = Client(server.port, budget)
            try:
                root = json_response(client.request("GET", "/"))
                require(root.get("self") == server.root and root.get("specversion") == "1.0-rc4", "Wrong native root/version.")
                capabilities = json_response(client.request("GET", "/capabilities"))
                record["capabilities"] = capabilities
                json_response(client.request("PUT", "/modelsource", profile["modelSource"]), mutation=True)
                json_response(client.request("PUT", "/dirs/perf", {}), 201, mutation=True)
                seed_started = time.perf_counter_ns()
                for offset in range(0, size, profile["seedBatchSize"]):
                    ids = range(offset, min(size, offset + profile["seedBatchSize"]))
                    body = {f"r{i:06}": {"versions": {"v1": {"ordinal": i, "payload": payload}}} for i in ids}
                    returned = json_response(client.request("POST", COLLECTION, body), mutation=True)
                    require(set(returned) == set(body), "A seed write was empty, partial or included unexpected Resources.")
                    disk_snapshot(store, budget)
                documents = {}
                for name, length in (("small", profile["smallDocumentBytes"]), ("large", profile["largeDocumentBytes"])):
                    data = document_bytes(length)
                    digest = hashlib.sha256(data).hexdigest()
                    documents[name] = (length, digest)
                    response = client.request("PUT", f"/dirs/perf/files/{name}/versions/v1", data, "application/octet-stream", stream=True)
                    require(response.status == 201 and response.size == length and response.sha256 == digest
                            and response.headers.get("xregistry-xregcorrelationid"),
                            "Document seeding did not durably acknowledge the exact fixture.")
                    disk_snapshot(store, budget)
                record["seedMs"] = (time.perf_counter_ns() - seed_started) / 1_000_000
                record["afterSeedMemory"] = memory_snapshot(server.process.pid)
                record["afterSeedDisk"] = disk_snapshot(store, budget)
                point = json_response(client.request("GET", COLLECTION + "/r000000"))
                validate_record(point, "r000000", 0, payload, 0, server.root)
                for name, (length, digest) in documents.items():
                    xid = f"/dirs/perf/files/{name}/versions/v1"
                    validate_document(client.request("GET", xid, stream=True), length, digest, xid)

                def collect(initial: str, page_limit: int | None) -> int:
                    current = server.root + initial
                    seen_links = set()
                    pages = []
                    network_ns = 0
                    while current is not None:
                        require(current not in seen_links and len(seen_links) <= size, "Pagination is cyclic or exceeds its page bound.")
                        seen_links.add(current)
                        response = client.request("GET", current)
                        network_ns += response.elapsed_ns
                        values = json_response(response)
                        if page_limit is not None:
                            require(len(values) <= page_limit, "A page exceeded the caller's requested limit.")
                        if "xregistry-count" in response.headers:
                            require(response.headers["xregistry-count"] == str(size), "Pagination total count changed or is incorrect.")
                        for key, value in values.items():
                            require(key in expected_ids, "A collection contains an unexpected Resource.")
                            validate_record(value, key, int(key[1:]), payload, 0, server.root)
                        pages.append(set(values))
                        current = next_link(response.headers.get("link", ""), current)
                    validate_collection_ids(pages, expected_ids)
                    return network_ns

                collect(COLLECTION, None)

                def run(name: str, iterations: int, operation, *, metadata: dict | None = None) -> None:
                    for _ in range(profile["warmupIterations"]):
                        operation()
                    before_disk = disk_snapshot(store, budget)
                    before_memory = memory_snapshot(server.process.pid)
                    before_requests = budget.requests
                    samples = []
                    started = time.perf_counter_ns()
                    for _ in range(iterations):
                        budget.check()
                        samples.append(operation())
                    elapsed = time.perf_counter_ns() - started
                    after_disk = disk_snapshot(store, budget)
                    record["scenarios"].append({
                        "name": name, "status": "measured", "warmupIterations": profile["warmupIterations"],
                        "iterations": iterations, "sampleNanoseconds": samples,
                        "statistics": summarize(samples, iterations, elapsed),
                        "httpRequests": budget.requests - before_requests,
                        "memoryBefore": before_memory, "memoryAfter": memory_snapshot(server.process.pid),
                        "diskBefore": before_disk, "diskAfter": after_disk,
                        "logicalDiskGrowthBytes": after_disk["logicalBytes"] - before_disk["logicalBytes"],
                        "details": metadata or {},
                    })

                def point_read() -> int:
                    response = client.request("GET", COLLECTION + "/r000000")
                    validate_record(json_response(response), "r000000", 0, payload, 0, server.root)
                    return response.elapsed_ns

                run("point-metadata-get", profile["readIterations"], point_read)
                run("complete-collection-get", profile["collectionIterations"], lambda: collect(COLLECTION, None),
                    metadata={"completeness": "All actual next links are followed; every expected Resource is checked."})
                if capabilities.get("pagination") is True:
                    run("opaque-paged-collection-get", profile["collectionIterations"],
                        lambda: collect(COLLECTION + "?limit=" + str(profile["pageSize"]), profile["pageSize"]),
                        metadata={"initialLimit": profile["pageSize"], "continuation": "Actual Link URI reused unchanged."})
                else:
                    record["scenarios"].append({"name": "opaque-paged-collection-get", "status": "unsupported",
                                                "reason": "The actual artifact does not advertise pagination."})
                for name, (length, digest) in documents.items():
                    xid = f"/dirs/perf/files/{name}/versions/v1"
                    def read_document(xid=xid, length=length, digest=digest):
                        response = client.request("GET", xid, stream=True)
                        validate_document(response, length, digest, xid)
                        return response.elapsed_ns
                    run("streamed-document-" + name, profile["documentIterations"], read_document,
                        metadata={"bytes": length, "sha256": digest, "readChunkBytes": 65536})

                def finish(resource_count: int, final_epoch: int) -> None:
                    group = json_response(client.request("GET", "/dirs/perf"))
                    require(group.get("recordscount") == resource_count and group.get("filescount") == 2,
                            "Writes did not produce the exact final collection counts.")
                    record["finalResourceCount"] = resource_count
                    record["finalUpdatedEpoch"] = final_epoch
                    record["finalMemory"] = memory_snapshot(server.process.pid)
                    record["finalDisk"] = disk_snapshot(store, budget)
                    record["status"] = "passed"

                if size not in profile["writeResourceCounts"]:
                    for name in ("atomic-nested-two-resource-write", "durable-metadata-update"):
                        record["scenarios"].append({"name": name, "status": "not-measured",
                            "reason": "The explicit developer profile restricts writes to supported smaller scales; see perf/observed-limits.json."})
                    finish(size, 0)
                    return record

                next_nested = 0
                def nested_write() -> int:
                    nonlocal next_nested
                    pairs = [(f"n{next_nested + i:06}", size + next_nested + i) for i in range(2)]
                    next_nested += 2
                    body = {"dirs": {"perf": {"records": {
                        key: {"versions": {"v1": {"ordinal": ordinal, "payload": "atomic-nested"}}}
                        for key, ordinal in pairs}}}}
                    response = client.request("PATCH", "/", body)
                    json_response(response, mutation=True)
                    for key, ordinal in pairs:
                        value = json_response(client.request("GET", COLLECTION + "/" + key))
                        validate_record(value, key, ordinal, "atomic-nested", 0, server.root)
                    return response.elapsed_ns
                run("atomic-nested-two-resource-write", profile["writeIterations"], nested_write,
                    metadata={"resourcesPerCommit": 2, "validationReadsPerOperation": 2,
                              "resourceCountAtMeasuredStart": size + 2 * profile["warmupIterations"]})

                epoch = 0
                def metadata_update() -> int:
                    nonlocal epoch
                    value = f"durable-update-{epoch + 1:06}"
                    response = client.request("PATCH", COLLECTION + "/r000000/versions/v1",
                                              {"epoch": epoch, "payload": value})
                    epoch += 1
                    validate_record(json_response(response, mutation=True), "r000000", 0, value, epoch, server.root, version=True)
                    validate_record(json_response(client.request("GET", COLLECTION + "/r000000")),
                                    "r000000", 0, value, epoch, server.root)
                    return response.elapsed_ns
                run("durable-metadata-update", profile["writeIterations"], metadata_update,
                    metadata={"validationReadsPerOperation": 1, "exactEpochIncrements": True})
                finish(size + next_nested, epoch)
            finally:
                client.close()
                if record.get("status") != "passed":
                    record["status"] = "failed"
                write_report(output / f"round-{size}-{round_number}.json", record)
    return record


def aggregate(rounds: list[dict]) -> list[dict]:
    result = []
    keys = sorted({(run["resourceCount"], scenario["name"]) for run in rounds
                   for scenario in run["scenarios"] if scenario["status"] == "measured"})
    for size, name in keys:
        values = [scenario for run in rounds if run["resourceCount"] == size
                  for scenario in run["scenarios"] if scenario["name"] == name and scenario["status"] == "measured"]
        medians = [scenario["statistics"]["p50Ms"] for scenario in values]
        rates = [scenario["statistics"]["validatedOperationsPerSecond"] for scenario in values]
        pooled = [sample for scenario in values for sample in scenario["sampleNanoseconds"]]
        elapsed = sum(int(round(scenario["statistics"]["validatedWallSeconds"] * 1_000_000_000)) for scenario in values)
        stats = summarize(pooled, len(pooled), elapsed)
        result.append({"resourceCount": size, "scenario": name, "rounds": len(values), **stats,
                       "roundMedianMs": medians, "medianOfRoundMediansMs": statistics.median(medians),
                       "roundMedianCoefficientOfVariation": statistics.stdev(medians) / statistics.mean(medians) if len(medians) > 1 else None,
                       "roundValidatedOperationsPerSecond": rates,
                       "medianValidatedOperationsPerSecond": statistics.median(rates),
                       "logicalDiskGrowthBytes": [scenario["logicalDiskGrowthBytes"] for scenario in values]})
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--managed-control", type=Path, required=True)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--profile", type=Path, default=ROOT / "perf" / "developer-profile.json")
    parser.add_argument("--allow-larger", action="store_true")
    args = parser.parse_args(argv)
    args.output.mkdir(parents=True, exist_ok=True)
    report = {"schemaVersion": 1, "status": "running", "storage": "durable", "concurrency": 1,
              "rounds": [], "aggregates": [], "measured": False}
    report_path = args.output / "report.json"
    write_report(report_path, report)
    try:
        profile_bytes = args.profile.resolve(strict=True).read_bytes()
        profile = parse_json(profile_bytes)
        validate_profile(profile, args.allow_larger)
        executable = args.server.resolve(strict=True)
        control = args.managed_control.resolve(strict=True)
        budget = Budget(profile["limits"])
        args.data_root.mkdir(parents=True, exist_ok=True)
        report["native"] = native_proof(subprocess.run([str(executable), "--RuntimeInfo", "true"],
            capture_output=True, timeout=20))
        managed = subprocess.run(["dotnet", str(control), "--RuntimeInfo", "true"], capture_output=True, timeout=20)
        report["managedNegativeControl"] = managed_proof(managed)
        try:
            native_proof(managed)
        except PerformanceError:
            report["managedRejectedAsNative"] = True
        else:
            raise PerformanceError("The managed negative control bypassed the native guard.")
        report.update(server=str(executable), serverSha256=hashlib.sha256(executable.read_bytes()).hexdigest(),
                      nativeBinaryBytes=executable.stat().st_size, managedControl=str(control),
                      managedControlSha256=hashlib.sha256(control.read_bytes()).hexdigest(),
                      profile=profile, profileSha256=hashlib.sha256(profile_bytes).hexdigest(),
                      workloadSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                      host=host_info(args.data_root.resolve()), command=[sys.executable, *sys.argv],
                      startedAtUtc=time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                      configuration={"demoLoopback": True, "model": "explicit perf fixture", "keepAlive": True,
                                     "shippingLimitsChanged": False, "durabilityChanged": False,
                                     "sqliteSynchronous": "Unmodified shipping FULL setting; not inferred from a separate SQLite connection.",
                                     "authenticationEgressChanged": False, "requestTimeoutSeconds": 15},
                      timingDefinition={"latency": "HTTP request through complete response consumption and SHA-256; JSON correctness checks excluded.",
                                        "pagedLatency": "Sum of constituent HTTP request durations for the complete verified page chain.",
                                        "throughput": "Operations divided by validated batch wall time, including between-request validation and write verification reads.",
                                        "warmupExcluded": True, "storageCache": "Warm after deterministic seeding and warmup."},
                      unsupportedMetrics={"inMemoryComparison": "No separately hosted in-memory server was introduced.",
                                          "allocatedDiskBytes": "Only exact logical file lengths are measured.",
                                          "cpuCyclesAndGcAllocations": "No inference from wall time or process working set.",
                                          "coldStorageReads": "Fixtures are seeded and warmed; OS cache is not flushed."})
        for size in profile["resourceCounts"]:
            for round_number in range(1, profile["rounds"] + 1):
                result = measure_round(executable, args.data_root.resolve(), args.output.resolve(), size, round_number,
                                       profile, budget, report["serverSha256"])
                report["rounds"].append(result)
                report["aggregates"] = aggregate(report["rounds"])
                write_report(report_path, report)
        require(len(report["rounds"]) == len(profile["resourceCounts"]) * profile["rounds"], "A round was not executed.")
        require(hashlib.sha256(executable.read_bytes()).hexdigest() == report["serverSha256"], "The server artifact changed during measurement.")
        report.update(status="passed", measured=True, totalRequests=budget.requests,
                      totalElapsedSeconds=time.monotonic() - budget.started)
        write_report(report_path, report)
        print(json.dumps({"status": "passed", "rounds": len(report["rounds"]), "scenarios": len(report["aggregates"]),
                          "report": str(report_path.resolve())}, ensure_ascii=True))
        return 0
    except (PerformanceError, OSError, ValueError, KeyError, RuntimeError, subprocess.SubprocessError) as error:
        report.update(status="failed", measured=False, error=str(error))
        write_report(report_path, report)
        print(json.dumps({"status": "failed", "error": str(error), "report": str(report_path.resolve())}, ensure_ascii=True))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
