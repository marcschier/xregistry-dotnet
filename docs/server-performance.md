# Native durable server performance workload

**Post-model-fix measurements:** native artifact
`201e66a5e7e8e73ba26287a58b4b6d4d1c2ceb3fb1d1765f58e197ae77d4d3f0`
completed the unchanged 100/1000 workload: three invocations, 18 rounds and
3,960 validated requests. An initial pooled 1000-Resource point-read increase
of 11.9% triggered further investigation; it was not dismissed or hidden.

Three alternating old/current artifact pairs then completed another 7,920
validated requests. The point-read increase did not repeat in those pairs
(+1.8%, -6.7%, -4.7%); the small-Document comparison instead varied from
+40.7% to -10.6%. A separate 100-read-per-scenario investigation retained
900 samples per artifact for each read scenario and all 9,054 validated
requests. It showed much larger whole-run variation, including multi-millisecond
point reads in both artifacts and opposite directions across pairs.

Consequently, **latency regression qualification is inconclusive on this
shared host**. No source-level speedup, absence of regression, or deployment SLO
is claimed from these runs; no quotas, correctness checks or durability were
weakened. All raw data and comparison summaries remain under
`artifacts\crash3-performance`, `artifacts\crash3-performance-ab` and
`artifacts\crash3-read-ab`. The latter explicitly records its changed
investigation profile rather than being combined with the baseline profile.
Compiler allocation improvements are separately demonstrated by the bounded
public compiler regressions, not inferred from these HTTP latency samples.

**Recovery requalification:** the rebuilt native Windows x64 server
`22943c1ceb7d2a65b1d9ddb008aaa698921e2ae9323e45bfb6faaabbf9a8d9bf`
passed the unchanged 100/1000 profile in three invocations: 18 rounds and
3,960 validated requests. At 1000 Resources, pooled p50 was 0.3903 ms for
point reads, 18.7127 ms for atomic nested writes and 17.0589 ms for metadata
updates. Across all fourteen scenario/size combinations, pooled p50 changes
against the retained final-accounting baseline ranged from -9.5% to +5.2%;
none exceeded the 10% investigation trigger.

This is a historical shared-host comparison, not controlled A/B evidence or a
production SLO. The second invocation's slower metadata writes remain in the
results. `artifacts\resume-performance\comparison.json` retains every pooled
row, per-invocation medians, matching profile/workload hashes, verified prior
report hashes and all three current reports. The earlier baseline below is
preserved, not overwritten.

`eng\measure-registry.py` is a bounded end-to-end workload for the actual
`XRegistry.Sample.FileServer` native executable. It is not a BenchmarkDotNet
microbenchmark, synthetic server, in-memory comparison, deployment SLO or hard
CI gate. The workload does not modify runtime/sample code, durability,
authentication, egress, quotas or the existing benchmark project.

**Final-accounting requalification:** after the Server owner's final retained
metadata working-set accounting change, a separate rebuild and three complete invocations passed the
unchanged 100/1000 profile: **18 rounds, 14 scenario/size combinations and
3,960 validated requests**. Native SHA-256:
`e71c8cc2de970b573d18fa02528b3d4a506466a1a97bff84cdcbedb0ea7b5db2`
(20,862,464 bytes); native/JIT controls again reported true/0 and false/59.
All nine 1000-Resource rounds ended at 1016 Resources and Version epoch 8.
Invocation durations were **24.140, 25.859 and 22.521 seconds**; none was
excluded.

At 1000 Resources, pooled p50 values were **0.3943 ms** for point metadata,
**97.8483 ms** for complete collection traversal, **127.7284 ms** for the opaque
64-record page chain, **3.9439 ms** for the 1-MiB Document, **18.0128 ms** for
atomic nested writes and **16.6349 ms** for durable metadata updates.
`perf\scaling-final-baseline-win-x64.json` records this successor and links all
raw reports. Complete 14-row metrics, variance, host/configuration and report
hashes are in
`artifacts\server-performance\scaling-accounted-86d3e50f4d234d2b8a4434b722582c5f\qualification.json`.
Use the reproduction commands below with this new artifact root. No workload,
profile, quota or durability setting changed; all owned processes and stores
were cleaned. Earlier artifacts and the cursor-budget failure remain preserved.
The preceding `a09f...` source qualification, including its exact former
tracked summary, remains under
`artifacts\server-performance\scaling-final-c5b0590b8b364a03aedce4304c92d7be`.

**Initial scaling qualification:** a freshly rebuilt Windows x64 native
FileServer completed the **100/1000-Resource workload**, including writes at
both scales. Three complete invocations produced **18 rounds, 14 scenario/size
combinations and 3,960 validated requests**. Each invocation took 26.4-27.0
seconds. The original 300-to-325 HTTP 413 no longer occurs on this artifact.
Shipping limits, SQLite FULL/flushes and result assertions were unchanged.

**Historical baseline:** the original developer profile completed six independent
server rounds, 12 measured scenario/size combinations and 741 validated
requests in **12.445 seconds**. The report uses warmups and exact result checks.
The same profile was also completed twice independently to expose shared-host
variation. Full raw sample arrays and per-round reports are retained.

## Qualified 100/1000 scaling profile

`perf\scaling-profile.json` retains the original deterministic model, batches
of 25 `versions.v1` records containing exact ordinal/payload values, three
warmups, single concurrency, 64-record opaque pages and both document sizes.
It performs all seven scenarios at both scales. Every 1000-Resource round
seeded all 40 batches, then verified atomic nested writes and durable Version
metadata updates. Each ended with **1016 metadata Resources**, two Documents
and exact updated Version epoch **8**. The original failed reports are retained.

One separate boundary was observed rather than hidden: five measured
collection iterations plus warmups request 17 complete frozen result sets
within the server's two-minute cursor lifetime. At 1000 rows each, this exceeds
the unchanged **16,384 retained-record budget** and correctly returns HTTP 503
`server_busy`. The failed attempt and exact input profile remain in the
artifact directory.

The qualified profile uses **four measured collection iterations**, preserving
all three warmups and every correctness check:
`1 + 2 * (3 + 4) = 15` retained sets, or 15,000 records. This bounds repetition;
it does not reduce the 1000-Resource scale, change cursor quotas/expiry, skip
pagination or retry failures. The original 17-query sequence is not qualified.

### Pooled observations, no discarded outliers

Native SHA-256:
`fd32d230e09a45f1b39f56a76c0fe25da8088cf74b1f189b4e887b84b2658d8e`;
binary size **20,852,224 bytes**. The table pools every successful round from
all three invocations. Per-scale sample counts are 180 point reads, 36 complete
collections, 36 opaque page chains, 90 reads of each Document and 45 of each
write operation.

| Scenario | p50 at 100, ms | p50 at 1000, ms | p95 at 1000, ms | p99 at 1000, ms | Validated ops/s at 1000 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Point metadata GET | 0.3994 | 0.3993 | 3.2658 | 24.5926 | 2175.26 |
| Complete collection GET | 6.0380 | 103.9490 | 181.3615 | 241.8407 | 8.84 |
| Opaque complete page chain, limit 64 | 6.8075 | 140.0562 | 201.7809 | 225.9004 | 6.60 |
| Streamed 256-byte Document | 0.6156 | 0.7143 | 1.0551 | 1.5055 | 1313.09 |
| Streamed 1-MiB Document | 3.6780 | 3.8986 | 23.2581 | 29.4817 | 160.93 |
| Atomic nested two-Resource write | 14.7205 | 18.1753 | 56.9609 | 74.8372 | 19.32 |
| Durable Version metadata update | 14.2080 | 16.5152 | 33.5322 | 58.0658 | 21.31 |

The point median was similar at 100 and 1000, but this does not prove lookup
complexity or rule out scans. Same-artifact host variation was substantial:
the three invocation medians for 100-Resource nested writes were **14.9633,
13.0891 and 69.1153 ms**. The slow invocation remains in the pooled baseline.
Tail values and coefficients of variation are observations, not production
guarantees or a controlled old/new artifact A/B comparison.

At 1000 initial Resources, metadata plus Document seeding took **1439.5-1808.8
ms**, and launch-to-readiness took **525.9-576.7 ms**. Final working sets ranged
from **210,034,688 to 247,713,792 bytes**, lifetime peak working sets from
**239,804,416 to 265,342,976 bytes**, and private committed bytes from
**225,480,704 to 289,243,136 bytes**. Logical store lengths grew from
**2,855,280-2,859,376** to **2,912,624-2,929,008 bytes**, with eight files per
store. Read scenarios added zero logical bytes. Physical allocation was not
measured.

`perf\scaling-baseline-win-x64.json` records all 14 pooled rows, runtime and
profile hashes, memory/disk ranges, the cursor boundary and raw report hashes.
The native proof is `nativeAot: true`, `jitCompiledMethods: 0`, `X64`; its
separately rebuilt JIT control reports `nativeAot: false` and **59** compiled
methods and is rejected by the native guard. The measurement script's hash is
unchanged from the historical baseline.

Artifacts are under
`artifacts\server-performance\scaling-0fcd5bfbb8804d959e229413cae654bf`:
`run-1` is the retained cursor-budget failure; `run-2`, `run-3` and `run-4`
are complete successful invocations. `summary.json` and `pooled-summary.json`
retain full-precision summaries. All owned server processes stopped and all
temporary stores were removed.

### Reproduce the qualified scaling run

The rebuild used existing restore metadata copied into the unique D: `build`
directory, then `dotnet publish --no-restore --artifacts-path <build>` with
`-c Release -f net10.0 -r win-x64 -p:PublishAot=true`. The managed control used
the same isolated build directory with `-p:PublishAot=false`. No dependency
installation, Server edit, shared PDB output, Docker operation or global host
change was required.

```powershell
$work = [IO.Path]::GetFullPath('artifacts\server-performance\scaling-0fcd5bfbb8804d959e229413cae654bf')
$env:TEMP = Join-Path $work 'tmp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $work 'dotnet-home'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_NOLOGO = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:LOCALAPPDATA = Join-Path $work 'local-app-data'
$env:PYTHONDONTWRITEBYTECODE = '1'
$run = Join-Path $work ('repeat-' + [Guid]::NewGuid().ToString('N'))
python -B eng\measure-registry.py `
  --server (Join-Path $work 'native\XRegistry.Sample.FileServer.exe') `
  --managed-control (Join-Path $work 'build\bin\XRegistry.FileServer\release\XRegistry.Sample.FileServer.dll') `
  --profile perf\scaling-profile.json `
  --data-root (Join-Path $run 'stores') --output (Join-Path $run 'evidence')
```

The hardware/storage remain the shared Windows/NTFS host described below.
The greater-than-10% **repeatable investigation trigger**, not a hard gate,
still applies. Linux, ARM64, live TLS, production authentication and an
in-memory comparison were not executed in this follow-up; earlier Linux
evidence elsewhere was preserved.

## Historical developer profile

The original 100/1000-Resource plan encountered an actual shipping limit:
seeding the batch from 300 to 325 metadata Resources returned HTTP 413,
`too_large`, `"The entity operation budget is exhausted."` A root-level atomic
nested write at 250 Resources also returned that limit. Those attempts remain
failed reports, not performance numbers.

The conservative default `perf\developer-profile.json` still explicitly selects:

- Read scenarios at **100 and 250 metadata-only Resources**.
- Atomic nested and durable metadata writes at **100 Resources**.
- Separate document-bearing Resources with deterministic **256-byte and 1-MiB**
  content, streamed in 64-KiB chunks.
- Three rounds per size, three warmup iterations, concurrency **one**.
- Twenty point reads, five complete/paged collections, ten document reads and
  five writes per measured round.

The profile uses existing normal APIs to install a simple `dirs` model, seed
metadata in batches of 25, and create the two Documents. It never enlarges
shipping limits. Larger user-authored profiles require `--allow-larger` and
remain capped at 10,000 Resources, five rounds, 100 iterations per scenario,
10,000 requests, 30 minutes and 1 GiB of logical store data. The default is
tighter: 4,096 requests, 15 minutes, 256 MiB and 20,000 files.

Known quota observations, their qualified follow-up and exact error-artifact
locations are recorded in `perf\observed-limits.json`. The historical
250-Resource write rows remain `not-measured`; that exact scale was not
separately rerun or assigned inferred timings.

## Historical observed numbers

Native artifact SHA-256:
`dc53982305c0870898dff64fdd61087c3c171ee5db9ae3519472a1c4b325f3f8`.
The executable is **20,645,376 bytes**.

The following values are from the final successful run. Latencies are
client-observed HTTP request/body-consumption durations, including SHA-256
processing but excluding subsequent JSON assertions. Paged collection latency
is the sum of its constituent HTTP durations. Throughput is the median of
per-round **validated wall-time** rates, including validation and the extra
verification reads used by write scenarios; it is not an inferred server
saturation rate.

| Scenario | Resources | Samples | p50 ms | p95 ms | p99 ms | Validated ops/s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Point metadata GET | 100 | 60 | 0.332 | 0.431 | 0.488 | 2,757.2 |
| Point metadata GET | 250 | 60 | 0.342 | 0.473 | 2.243 | 2,673.2 |
| Complete collection GET | 100 | 15 | 4.499 | 12.971 | 12.971 | 165.6 |
| Complete collection GET | 250 | 15 | 16.905 | 22.423 | 22.423 | 57.0 |
| Opaque paged complete collection | 100 | 15 | 4.979 | 9.299 | 9.299 | 175.5 |
| Opaque paged complete collection | 250 | 15 | 17.548 | 19.421 | 19.421 | 54.7 |
| Streamed 256-byte Document | 100 | 30 | 0.579 | 0.814 | 1.030 | 1,500.9 |
| Streamed 256-byte Document | 250 | 30 | 0.541 | 0.649 | 0.759 | 1,788.7 |
| Streamed 1-MiB Document | 100 | 30 | 3.150 | 15.346 | 22.856 | 201.8 |
| Streamed 1-MiB Document | 250 | 30 | 3.070 | 18.714 | 19.943 | 197.5 |
| Atomic nested two-Resource write | 100 | 15 | 26.943 | 31.553 | 31.553 | 31.6 |
| Durable metadata update | 100 | 15 | 10.743 | 11.587 | 11.587 | 68.7 |

The point-GET comparison is deliberately repeated at different Registry sizes
to reveal scaling changes. This final run's p50 increased about 3% for 2.5x
the metadata population, but earlier complete runs showed larger differences.
These observations do **not** establish algorithmic complexity or prove the
absence/presence of whole-Registry scans. Collection cost clearly grows with
returned data; point-lookup scaling remains a useful regression investigation
signal.

### Startup, memory and disk

Process launch to API-ready times were **528.553, 531.832, 533.049, 534.697,
539.639 and 1,036.582 ms**. Readiness polls at 50 ms, so this is not a precise
runtime initialization-only measurement or a cold-machine result.

Windows `GetProcessMemoryInfo` supplies actual working set, lifetime peak
working set and private committed bytes:

| Scale | Final working-set range | Lifetime peak working-set range | Final private-commit range |
| --- | --- | --- | --- |
| 100 Resources, reads and writes | 81,633,280-94,814,208 B | 94,736,384-108,183,552 B | 68,763,648-84,037,632 B |
| 250 Resources, reads only | 121,847,808-133,885,952 B | 131,698,688-150,024,192 B | 114,831,360-128,765,952 B |

Logical store bytes at 100 Resources grew from **1,311,088 B** after seeding to
**1,376,624-1,380,720 B** after warmup and measured writes. At 250 Resources the
read-only scenarios retained **1,565,040-1,569,136 B**. Each store had eight
files. Per-scenario growth is also recorded; reads added zero logical bytes.
These are sums of file lengths, **not physical allocated disk space**.

## Validation and timing contract

Before timed work, the harness executes the supplied native artifact's
`--RuntimeInfo true`. It requires both `nativeAot: true` and an integer
`jitCompiledMethods: 0`, plus X64 architecture. A real separately built JIT
control must report `nativeAot: false` and a positive JIT count; the same native
guard must reject it.

The workload also ran directly against the actual managed apphost. It exited
nonzero before measurements with `"Managed execution is not a native
performance baseline."` That negative report is retained under
`artifacts\server-performance\jit-negative`.

For every measured operation:

- Status, content type and exact declared/received lengths are checked.
- Metadata IDs, XIDs, configured-root `self`, ordinal, payload, Version/default
  and exact expected epoch are checked.
- Document count/SHA-256 and document identity/epoch headers are checked;
  binary content is never text-decoded or reformatted.
- Collections must contain the exact expected keys. Pagination follows the
  actual `next` URI unchanged, only within the same owned origin/collection;
  cycles, duplicate/missing members, inconsistent counts and excessive pages
  fail.
- Atomic writes verify both newly created Resources and final collection
  counts. Metadata updates use the exact prior epoch and verify each increment
  and persisted value through a separate read.

Warmup performs the same validations but is excluded from sample arrays and
measurement timers. A persistent HTTP connection avoids measuring a fresh TCP
connection for every point read. Every response is fully consumed. Validation,
JSON parsing, memory/disk snapshots and reporting are outside individual HTTP
latency timers; validated batch throughput includes between-request validation
work. Report fields state these definitions explicitly.

Memory/disk snapshots are outside timed loops. Disk/file budgets are also
checked during seeding. Only owned native processes and unique temporary data
directories are removed. Logs, reports and the managed negative-control build
remain in task artifacts.

## Reproduce locally

Build a managed negative control without changing the sample:

```powershell
dotnet build samples\XRegistry.FileServer\XRegistry.FileServer.csproj `
  -c Release -f net10.0 -p:PublishAot=false `
  --artifacts-path artifacts\performance-control --verbosity quiet
```

Run the actual existing native executable:

```powershell
$run = Join-Path 'artifacts\server-performance' ('run-' + [Guid]::NewGuid().ToString('N'))
python eng\measure-registry.py `
  --server artifacts\file-server-native\win-x64\XRegistry.Sample.FileServer.exe `
  --managed-control artifacts\performance-control\bin\XRegistry.FileServer\release\XRegistry.Sample.FileServer.dll `
  --data-root (Join-Path $run 'stores') `
  --output (Join-Path $run 'evidence')
```

The selected directory must be on storage supported by the unmodified durable
server: local NTFS on Windows, or qualified ext4 for a separately executed Linux
run. The process is an explicit owned loopback demo. No SQLite FULL setting,
flush, authentication, egress, quota, thread priority or affinity is relaxed.
No Git process is introduced into the .NET runtime.

Optional `--profile <path>` and `--allow-larger` permit explicitly bounded
experiments. Unsupported sizes still fail if the shipping artifact rejects
them; the harness cannot silently turn that failure into a smaller workload.

## Host, variance and regression policy

The observed machine was Windows 11 Enterprise **10.0.26200**, SDK
**10.0.401**, Intel **Core Ultra 7 265K**, 20 cores/logical processors,
68,009,082,880 physical-memory bytes, and fixed **D: NTFS** on a
**Micron MTFDKBA2T0TGD-2BK15ABLT NVMe** device. It was a shared host with no
affinity, priority, frequency, thermal, cache or background-load control.
Fixtures and Documents were seeded and warmed; cold storage is not measured.

The final run's coefficient of variation of the three per-round medians ranged
from about **1.6% to 14.8%**, depending on scenario. Two earlier complete
invocations of the same native artifact/profile also succeeded. Some
between-invocation medians moved by more than 10% without any runtime change;
tail samples had larger bursts. Small sample sizes mean p95/p99 can equal the
maximum observed value. No statistically robust production-tail guarantee is
claimed.

Use a **greater-than-10% repeatable degradation as an investigation trigger**:

1. Match artifact, profile, SDK/runtime, machine, storage, client definition and
   warmup conditions. Record any difference rather than normalizing it away.
2. Repeat independent runs and compare per-round medians and validated
   throughput, not one outlier or one hosted-runner execution.
3. Investigate only a reproducible change beyond observed variation; collect
   targeted traces or larger opt-in samples before attributing a root cause.

`compare_metric` reports this trigger and always marks `hardGate: false`.
There is no CI gate, hosted-runner SLO or tuning recommendation unsupported by
measurement.

## Evidence and tooling checks

`perf\baseline-win-x64.json` records the measured summary, memory/disk values,
host, native/profile/workload hashes and all report references.

Final report:
`artifacts\server-performance\final-38ba3a4567aa4a7887282bc96ac9a36b\evidence\report.json`,
SHA-256 `f082f17bff7979d495e41c170ed50c2c6464c88057eba9d96e8b3086afe8935c`.

Repeat reports:
`baseline-b366eb6391674e018b27b30e75534f70` and
`repeat-9005bf542f9e487497a4e438492650ea` under the same artifacts parent.
Failed larger-scale attempts are retained separately. Per-round JSON contains
all exact latency samples; reports also retain invocation, capabilities,
configuration and native/JIT proofs. The server hash is checked between rounds
and at completion so a concurrent parent rebuild cannot mix artifacts silently.

```powershell
python -m unittest discover -s tests\Tooling -p 'test_performance*.py' -v
```

The **12 accounting tests passed**. They cover exact sample counts/percentiles,
empty/truncated/invalid numbers, native and JIT guards, profile bounds and
types, incorrect bytes/status/headers/epochs, collection completeness, opaque
pagination authorization and the non-gating regression policy. These tests are
not measurements.

The historical report's 1000-Resource and larger-write gaps are addressed only
for the explicit scaling profile above. Still unmeasured: more than 1000
initial metadata Resources, an in-memory host, physical disk allocation, CPU
cycles/GC allocations, cold-storage throughput, Linux/ARM64, production
TLS/authentication and deployment SLOs. Unsupported metrics have explicit
reasons in JSON rather than guessed values.
