# Native durable server performance workload

`eng\measure-registry.py` is a bounded end-to-end workload for the actual
`XRegistry.Sample.FileServer` Native AOT executable. It is not a BenchmarkDotNet
microbenchmark, synthetic server, in-memory comparison, deployment SLO, or hard
CI gate. It does not modify runtime/sample code, durability, authentication,
egress, quotas, or the existing benchmark project.

Shared-host A/B experiments show substantial run-to-run variance. Latency
regression qualification is therefore inconclusive on this host: no
source-level speedup, absence of regression, or deployment SLO is claimed.
The retained before/after investigation is summarized in the
[changelog](changelog.md#native-and-performance-checkpoints).

## Current 100/1000 scaling profile

The current retained Windows x64 source-matched artifact is
`e71c8cc2de970b573d18fa02528b3d4a506466a1a97bff84cdcbedb0ea7b5db2`
(20,862,464 bytes). Three complete invocations of the unchanged profile passed:
18 rounds, 14 scenario/size combinations, and 3,960 validated requests.
Native/JIT controls reported true/0 and false/59. All nine 1000-Resource rounds
ended at 1016 Resources and Version epoch 8. Invocation durations were 24.140,
25.859, and 22.521 seconds; none was discarded.

At 1000 Resources, pooled p50 values were:

| Scenario | p50 ms |
| --- | ---: |
| Point metadata GET | 0.3943 |
| Complete collection traversal | 97.8483 |
| Opaque 64-record page chain | 127.7284 |
| Streamed 1-MiB Document | 3.9439 |
| Atomic nested two-Resource write | 18.0128 |
| Durable metadata update | 16.6349 |

`perf\scaling-final-baseline-win-x64.json` records the summary and links all raw
reports. Complete metrics, variance, host/configuration, and hashes are in
`artifacts\server-performance\scaling-accounted-86d3e50f4d234d2b8a4434b722582c5f\qualification.json`.

`perf\scaling-profile.json` retains the deterministic model, batches of 25
`versions.v1` records, three warmups, single concurrency, 64-record opaque
pages, and 256-byte/1-MiB Documents. It performs seven scenarios at 100 and
1000 Resources. Each 1000-Resource round verifies all seeded batches, atomic
nested writes, durable Version updates, final membership, and exact epoch.

Five measured collection iterations plus warmups require 17 complete frozen
sets, exceeding the unchanged 16,384 retained-record budget and correctly
returning HTTP 503 `server_busy`. The qualified profile uses four measured
collection iterations:

```text
1 + 2 * (3 + 4) = 15 retained sets = 15,000 records
```

This bounds repetition without reducing the 1000-Resource scale, changing
cursor quotas/expiry, skipping pagination, or retrying failures. The original
17-query sequence remains a retained failed boundary report.

## Pooled scaling observations

The retained pooled profile below uses artifact
`fd32d230e09a45f1b39f56a76c0fe25da8088cf74b1f189b4e887b84b2658d8e`
(20,852,224 bytes). Every successful round from three invocations is included.

| Scenario | p50 at 100, ms | p50 at 1000, ms | p95 at 1000, ms | p99 at 1000, ms | Validated ops/s at 1000 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Point metadata GET | 0.3994 | 0.3993 | 3.2658 | 24.5926 | 2175.26 |
| Complete collection GET | 6.0380 | 103.9490 | 181.3615 | 241.8407 | 8.84 |
| Opaque page chain, limit 64 | 6.8075 | 140.0562 | 201.7809 | 225.9004 | 6.60 |
| Streamed 256-byte Document | 0.6156 | 0.7143 | 1.0551 | 1.5055 | 1313.09 |
| Streamed 1-MiB Document | 3.6780 | 3.8986 | 23.2581 | 29.4817 | 160.93 |
| Atomic nested two-Resource write | 14.7205 | 18.1753 | 56.9609 | 74.8372 | 19.32 |
| Durable Version metadata update | 14.2080 | 16.5152 | 33.5322 | 58.0658 | 21.31 |

The point median similarity does not prove lookup complexity or rule out scans.
Same-artifact variation was substantial: the three 100-Resource nested-write
medians were 14.9633, 13.0891, and 69.1153 ms. Tail values and coefficients of
variation are observations, not production guarantees.

At 1000 initial Resources, seeding took 1439.5-1808.8 ms and readiness took
525.9-576.7 ms. Final working set ranged from 210,034,688 to 247,713,792 bytes;
lifetime peak working set from 239,804,416 to 265,342,976 bytes; private commit
from 225,480,704 to 289,243,136 bytes. Logical store length grew from
2,855,280-2,859,376 to 2,912,624-2,929,008 bytes with eight files. Physical
allocation was not measured.

## 100/250 reference measurements

The bounded developer profile selects reads at 100 and 250 metadata Resources,
writes at 100 Resources, separate 256-byte and 1-MiB Documents, three rounds,
three warmups, and concurrency one. Larger user profiles require
`--allow-larger` and remain capped at 10,000 Resources, five rounds, 100
iterations per scenario, 10,000 requests, 30 minutes, and 1 GiB logical data.

Native artifact SHA-256 is
`dc53982305c0870898dff64fdd61087c3c171ee5db9ae3519472a1c4b325f3f8`;
size is 20,645,376 bytes.

| Scenario | Resources | Samples | p50 ms | p95 ms | p99 ms | Validated ops/s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Point metadata GET | 100 | 60 | 0.332 | 0.431 | 0.488 | 2,757.2 |
| Point metadata GET | 250 | 60 | 0.342 | 0.473 | 2.243 | 2,673.2 |
| Complete collection GET | 100 | 15 | 4.499 | 12.971 | 12.971 | 165.6 |
| Complete collection GET | 250 | 15 | 16.905 | 22.423 | 22.423 | 57.0 |
| Opaque paged collection | 100 | 15 | 4.979 | 9.299 | 9.299 | 175.5 |
| Opaque paged collection | 250 | 15 | 17.548 | 19.421 | 19.421 | 54.7 |
| Streamed 256-byte Document | 100 | 30 | 0.579 | 0.814 | 1.030 | 1,500.9 |
| Streamed 256-byte Document | 250 | 30 | 0.541 | 0.649 | 0.759 | 1,788.7 |
| Streamed 1-MiB Document | 100 | 30 | 3.150 | 15.346 | 22.856 | 201.8 |
| Streamed 1-MiB Document | 250 | 30 | 3.070 | 18.714 | 19.943 | 197.5 |
| Atomic nested write | 100 | 15 | 26.943 | 31.553 | 31.553 | 31.6 |
| Durable metadata update | 100 | 15 | 10.743 | 11.587 | 11.587 | 68.7 |

The host was shared; these figures do not establish algorithmic complexity or
a production-tail guarantee. Startup, memory, disk, and exact report references
remain in `perf\baseline-win-x64.json`.

## Validation and timing contract

Before timed work, the harness requires the native candidate to report
`nativeAot: true`, `jitCompiledMethods: 0`, and matching architecture. A
separately built JIT control must report `nativeAot: false` and a positive JIT
count and must be rejected before measurements.

Every operation checks status, content type, exact lengths, IDs/XIDs, configured
root, ordinal, payload, Version/default and epoch. Document reads verify count,
SHA-256, identity, headers, and exact bytes. Collections verify exact keys and
follow same-origin opaque `next` URIs while rejecting cycles, duplicates,
missing members, count changes, and excessive pages. Writes verify both
Resources and persisted values through independent reads.

Warmups perform the same validation but are excluded from sample arrays.
Responses are fully consumed. Validation, parsing, memory/disk snapshots, and
reporting are outside individual HTTP latency timers; validated throughput
includes between-request validation work.

Only owned native processes and unique temporary data directories are removed.
Logs, reports, and the managed negative-control build remain as evidence.

## Reproduce locally

```powershell
dotnet build samples\XRegistry.FileServer\XRegistry.FileServer.csproj `
  -c Release -f net10.0 -p:PublishAot=false `
  --artifacts-path artifacts\performance-control --verbosity quiet

$run = Join-Path 'artifacts\server-performance' ('run-' + [Guid]::NewGuid().ToString('N'))
python eng\measure-registry.py `
  --server artifacts\file-server-native\win-x64\XRegistry.Sample.FileServer.exe `
  --managed-control artifacts\performance-control\bin\XRegistry.FileServer\release\XRegistry.Sample.FileServer.dll `
  --profile perf\scaling-profile.json `
  --data-root (Join-Path $run 'stores') `
  --output (Join-Path $run 'evidence')
```

Use fixed local NTFS on Windows or qualified ext4 for a separately executed
Linux run. No SQLite durability, authentication, egress, quota, thread priority,
or affinity setting is relaxed. Optional profiles remain bounded and cannot
turn a shipping-limit failure into a smaller successful workload.

## Regression policy and current coverage

Use a greater-than-10% **repeatable** degradation as an investigation trigger,
not a hard gate. Match artifact, profile, SDK/runtime, machine, storage, client
definition, and warmup conditions; repeat independent runs; compare per-round
medians and validated throughput; retain outliers and configuration differences.

Current evidence does not cover more than 1000 initial metadata Resources, an
in-memory host, physical disk allocation, CPU cycles/GC allocations, cold
storage, Linux/ARM64, production TLS/authentication, or a deployment SLO. These
gaps are tracked in the [roadmap](roadmap.md#durability-and-performance).

```powershell
python -m unittest discover -s tests\Tooling -p 'test_performance*.py' -v
```

The 12 accounting tests cover sample counts/percentiles, invalid reports,
native/JIT guards, profile bounds, bytes/status/headers/epochs, collection
completeness, pagination authorization, and the non-gating regression policy.
They are tooling tests, not measurements.
