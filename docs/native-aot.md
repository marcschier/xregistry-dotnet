# Native AOT qualification

Runtime libraries target .NET 8 and .NET 10. The principal samples target
.NET 10. Required release platforms are Windows/Linux x64 and ARM64; annotation,
cross-compilation, emulation and successful JIT execution do not satisfy those
runtime cells.

Packable projects enable trimming/AOT analysis through `Directory.Build.targets`.
The samples and qualification probes publish Native AOT explicitly. Use normal
NuGet restore with centrally managed package versions, matching native
toolchains, and the actual published binaries:

```powershell
pwsh -NoProfile -File eng\test-model-packages.ps1 -RuntimeIdentifier win-x64 -ArtifactsRoot D:\temp
pwsh -NoProfile -File eng\test-client-sample.ps1 -RuntimeIdentifier win-x64
pwsh -NoProfile -File eng\test-storage-native.ps1 -RuntimeIdentifier win-x64 -Framework net10.0 -DataRoot D:\xregistry-test-data
pwsh -NoProfile -File eng\test-git-interop.ps1 -RuntimeIdentifier win-x64 -Framework net10.0
```

On Windows, the scripts prepend the Visual Studio Installer directory so the
compiler can find `vswhere`. Do not inherit a `vcvars64` Platform setting for
ordinary managed builds. Linux compilation needs clang and zlib development
headers. Durable-storage tests require local ext4 rather than an overlay
filesystem; their CI job provisions a separate per-job ext4 data volume.

The model-package gate accepts an existing absolute `-ArtifactsRoot` parent
and creates a fresh child for its feed, consumers and cache. Use a short path
on Windows: the SDK's runtime-copy step can report a present file as missing
when its cache path reaches the 260-character Win32 boundary. The default
remains `artifacts\package-smoke`; selecting a parent does not reuse an old feed.

NuGet restore is lockfile-free. Native/RID and portable restores use isolated
project output and package-cache locations where the qualification scripts
require them; they do not replace central package versioning, NuGet audit, or
package-source mapping.

## Actual native execution

A native-publish runtime configuration can make
`RuntimeFeature.IsDynamicCodeSupported` false **even under CoreCLR**. That flag
alone is insufficient proof. Qualification probes also require
`IsDynamicCodeCompiled` to be false and
`JitInfo.GetCompiledMethodCount()` to be zero.

Native results are associated with the actual published artifact and hash. The
model-package, client-sample, storage-crash and Git drivers execute a real
managed negative control. It must fail native-only work before network/storage
side effects, or report non-native status for a sample's `runtime-info` command.

These checks do not authenticate an arbitrary untrusted executable's report.
Release receipts require the exact qualified source, package, runner, artifact,
and executed-case evidence described by the
[conformance](conformance.md) and [release](releasing.md) gates.

## Current evidence and uncovered cells

The [native matrix at `ecd2a3b`](https://github.com/marcschier/xregistry-dotnet/actions/runs/41024778707)
passed all 28 jobs on Windows/Linux x64 and ARM64: eight generic package-consumer
cells, four model-consumer jobs executing both frameworks, eight storage cells,
and four jobs each for the client and bridge samples. The .NET 10 storage jobs
also executed the durable HTTP probe. Generic consumer receipts are described
in [consumer embedding](consumer-migration.md#current-evidence-and-limits).
These jobs qualify their development probes, not the complete native TUnit
suites or every specification obligation.

The complete Federation TUnit suite passed 626 native cases on .NET 10/Windows
x64 at `6d143c4456a9d5d9f369e9c62daebbefc770d12a`, including all 157 scoped,
document-local and JSON Pointer WoT resolver cases. Publication ran ILC with zero
IL diagnostics.
The same process reported `nativeAot: true` and `jitCompiledMethods: 0`; its
managed control reported `nativeAot: false` and 3,981 JIT-compiled methods and
was rejected by the receipt converter.

The run is retained locally at `D:\temp\xregistry-fed-native-6d143c4`, with its
publish/run logs beside that directory. `receipt.json` records 242 distinct
passed methods, zero failed and zero skipped; parameterized executions are
combined using their worst outcome, so the method count differs from the 626
executed cases. The native executable's SHA-256 is
`2b0755f9cfb9204617872a0457200784b35c0199e3964a7a2fab19f9ad095eb9`.
This is one full-suite development cell, not a package-consumer receipt, a
reviewed requirement mapping or completion of the eight-cell native matrix.
The matching .NET 8 publication still emits the two framework JSON warnings
described below and is not qualified.

The complete Models TUnit suite passed 954 native cases on .NET 10/Windows x64
at `af72b7f60433be4fab9e81285e4a6c006915fd73`, including all 43 incremental
assignment cases. ILC ran with zero IL diagnostics. The same-process proof
reported native execution with zero JIT compilations; the managed control
reported false/3,981 and was rejected without creating a receipt.
`D:\temp\xregistry-models-native-af72b7f` retains the proof, TRX and receipt,
with publish/run logs beside the directory. Its receipt records 313 distinct
passed methods, zero failed and zero skipped. Executable SHA-256:
`1b5edadb01828e064b4f67ee8cfbdde7947451a8995298fa68ce8684d9c82d73`.
This establishes execution of the pure allocator, not a durable incremental
publication adapter or complete profile qualification.

Complete Server and Storage.File suites passed natively on .NET 10/Windows x64
at `f97af1b91ac79e3a02a2eb19736230b8b69bf2c5`: 886 and 32 cases respectively,
including all 28 incremental publication cases and all three file-backed
deletion/restart cases. Both publications ran ILC with zero IL diagnostics.
Both execution proofs reported native status with zero JIT compilations;
managed controls reported false/3,997 and were rejected without creating receipts.
The Server suite's two reflection-based byte-equivalency assertions were
replaced with ordered byte equality before publication; no warning was suppressed.

The runs are retained in `D:\temp\xregistry-server-native-f97af1b` and
`D:\temp\xregistry-storage-native-f97af1b`, with publish/run logs beside them.
Each contains its proof, TRX and converted receipt. Server's receipt records
415 distinct passed methods; Storage.File's records 30. Neither has a failed or
skipped method. Executable SHA-256 values are
`b55c799ef46da65bbd35219519bc20af0267f083cdba7d7778e062e627459648`
for Server and
`de0fc0a455b0ddd7ede5e5bf31fa3f62572765451a7689481d55c437d0272f53`
for Storage.File. This establishes one native publication/durability cell.
Incremental reference selection, the other native cells and requirement-level
release qualification remain open.

The subsequent profile-selection checkpoint
`c19673704e3bc8add83c23fed9d2c9f2979afa14` refreshed complete Models and
Federation suites in the same native cell. Models passed 985 cases, including
all 31 incremental selector cases; Federation passed 636 cases, including all
10 incremental provider cases. Both ran ILC with zero IL diagnostics and
reported native execution with zero JIT compilations. Models' managed control
reported false/3,981; Federation's reported false/3,982. Both were rejected
without creating receipts.

Evidence directories are `D:\temp\xregistry-models-native-c196737` and
`D:\temp\xregistry-fed-native-c196737`, with publish/run logs beside them.
Their receipts aggregate 329 and 250 distinct passed methods respectively,
with zero failed or skipped. Executable SHA-256 values:
`de3ffaff376242c3894db9d6f9fcb47964aedb476322fa5695121af0cb6b0a4a`
for Models and
`f2432873cb0d070a0c0e0a7e1a26ac4701eae570545385fbc694fbeddd29cb4d`
for Federation. These runs cover parsed incremental selection and scoped
acquisition. They do not establish complete URI/context processing, refreshed
Server/Storage.File execution at this newer source, other platforms or
requirement-level release qualification.

The module-level receipts below are historical and have not been refreshed
against the complete successor source set. Do not treat their counts as
qualification of the current implementation.

| Windows x64 native suite | .NET 8 | .NET 10 |
| --- | --- | --- |
| Core model, metadata, queries and numbers | 750 passed | 750 passed |
| Models, including Message/Endpoint materialization | 750 passed | 750 passed |
| Server, including final Message/Endpoint admission | 834 passed | 834 passed |
| Federation and producer model/capture constraints | Not refreshed | 458 passed |

The successful executables have native PE headers, no CLR directory and no IL
warnings; hashes are in `artifacts\resume-final-native-images.json`. Validation
429/TFM, OCI 270/TFM, File 93/TFM and Git 155/TFM are separate module receipts.
The .NET 8 Git test host emitted two ASP.NET framework IL warnings and is not
described as warning-free. No shipping package-consumer IL warning was
suppressed.

An earlier eleven-package run at `artifacts\embedding\29c45e4bd888` passed 25 native
consumer cases per Windows x64 TFM and ordinary/feature-masked JIT rejection
controls. The source fingerprint is
`5328167584619aded4223746a71c1fe2ea0d48f201513a4e49cc7a61cc0946e5`.
The three principal samples pass 17/14/6 checks; native storage passes 28 tests
and six process-crash/restart cases. Reference Git smart-HTTP interoperability
passes 26 cases and 56 exchanges on each Windows x64 TFM.

The historical source oracle passed 976 explicit cases with 38 OPC-UA
exclusions through SPEC-020. The combined ledger now retains 1,835 historical
blocks and 2,398 successor blocks; most successor obligations still require
review and evidence mapping. Their count does not establish conformance.
The strict tooling retains rejected receipts for a concurrently changed
protected ledger and for pytest subtest/XML accounting disagreement.

The passing matrix does not cover every module-level binding, federation,
interoperability or performance case. Full native requirement receipts,
unimplemented profiles, peer limitations and release approvals remain open.
The outstanding work is tracked in the
[roadmap](roadmap.md#native-platforms-and-package-consumers). Older checkpoint
receipts and before/after narratives are retained in the
[changelog](changelog.md#package-consumer-checkpoints).

## .NET 8 HTTP test dependency warning

The TUnit HTTP test project brings packaged System.Text.Json 10.0.x into its
.NET 8 host. An isolated reproduction established that ASP.NET Core/ILC 8.0.31
with that package analyzes `JsonOptions.CreateDefaultTypeResolver`, producing
IL2026/IL3050 even while runtime JSON reflection is disabled. Switching to raw
`RequestDelegate` routes does not fix that combination. This is reproducible
without TUnit and is not a decoder implementation warning.

The test binary executes its cases, but its .NET 8 publishing warning gate is
not clean. No warning was suppressed and no dependency was silently downgraded.
A clean-cache consumer of the actual Client/Core NuGets, using the framework
JSON dependency, published without those warnings and passed 31 native Kestrel
checks. See [HTTP compression](http-compression.md) for the causal evidence and
exact qualification boundary.

The generic all-package consumer also uses framework JSON on .NET 8.
Federation and Publication no longer force a System.Text.Json 10.x dependency
into an ASP.NET Core 8 application. Fresh Windows x64 development consumers
pass all 27 cases on both target frameworks with zero IL warnings, while both
managed controls fail native-only work as required. The source and package
receipts are identified in
[consumer embedding](consumer-migration.md#current-evidence-and-limits).
This correction does not change the TUnit host's own dependency graph or
qualify unexecuted platforms.
