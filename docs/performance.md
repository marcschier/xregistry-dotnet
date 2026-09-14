# Performance evidence

The permanent BenchmarkDotNet suite is under `tests/XRegistry.Benchmarks`.
It currently covers strict, owning metadata parsing, HTTP attribute encoding
at 128, 8,192 and 262,144 payload characters, and effective Registry catalog
model compilation. Input scale, runtime versions and future source revisions
are comparison axes; the encoding and parsing methods are not interchangeable
algorithms and are not presented as competing implementations.

All fixture creation and correctness checks occur in global setup. Benchmark
methods return their results. BenchmarkDotNet controls invocation counts.
The executable fails if no benchmark ran, validation failed, or any case failed.
Benchmark tooling is non-packable and does not enter runtime NuGet dependencies.

## Run

Run from the repository root with the pinned SDK and both installed runtimes:

```powershell
pwsh -NoProfile -File eng\benchmark.ps1 -Filter '*' -Job Dry
pwsh -NoProfile -File eng\benchmark.ps1 -Filter '*ParseMetadata*8192*' -Job Default
pwsh -NoProfile -File eng\benchmark.ps1 -Filter '*ParseMetadata*' -Job Short -CompareRuntimes
```

The first command checks execution, **not performance**. The second measures a
representative case. The third compares all three parsing scales using .NET 8
as the baseline and matched invocation counts (`--apples`). Short runs are
development feedback, not final statistical qualification. Full measurements
should use default or longer jobs on an idle, controlled host.

Logs and timestamped Markdown/CSV/JSON reports go to `artifacts/benchmarks`.
The forwarded CLI also supports explicit filters and jobs:

```powershell
dotnet run --project tests\XRegistry.Benchmarks\XRegistry.Benchmarks.csproj -c Release -f net10.0 -- --filter '*CompileCatalog*' --noOverwrite --artifacts artifacts\benchmarks
```

## First measured local baseline

Recorded on Windows 11 25H2, Intel Core Ultra 7 265K, x64, SDK 10.0.401,
BenchmarkDotNet 0.15.8. These are JIT measurements, not native-server latency.
The representative default-job .NET 10.0.12 run measured 8,192-character metadata
parsing at **3.702 us mean, 0.1012 us standard deviation, 9,000 bytes allocated**.

The separate matched three-iteration ShortRun comparison produced:

| Payload characters | .NET 8.0.31 mean | .NET 10.0.12 mean | .NET 10 / .NET 8 time |
| --- | ---: | ---: | ---: |
| 128 | 630.7 ns | 473.6 ns | 0.75 |
| 8,192 | 3.9283 us | 3.6273 us | 0.92 |
| 262,144 | 169.4330 us | 161.0821 us | 0.95 |

The largest case had wide confidence intervals and large-object collections.
The host was shared with development work. These figures establish scale and
reproducible commands, **not an SLO or proof of a runtime speedup**.

## Regression policy and remaining coverage

A reproducible mean/p95 degradation greater than 10% against the same controlled
hardware, source fixture, runtime, GC mode and storage durability settings is an
investigation trigger. Compare confidence intervals and repeat noisy results;
do not turn hosted-runner noise into a reliable release gate. Allocations,
bounded working set and exact result correctness are separate constraints.

No final numerical release budgets have been qualified yet. Remaining workloads
include durable commits/recovery, metadata filtering/sorting/paging, concurrent
HTTP operations, bounded large-document streaming, federation fan-out, Git pack
and delta work, OCI indexed lookup, and native startup/executable size. The
current parser measurements do not cover these workloads. Do not disable
durability or security checks to meet a performance target.
