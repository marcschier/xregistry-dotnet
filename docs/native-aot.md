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

Every repository test project now includes the same opt-in
`NativeExecutionProofTests` through `Directory.Build.targets`; setting
`XREGISTRY_NATIVE_PROOF_PATH` captures that process's dynamic-code flags and JIT
count. Set a distinct proof path for each separately executed test host.
Evaluated compile items contain exactly one copy in each of the 16 projects.
At the common-proof checkpoint, the managed gate passed 10,852 cases plus
283 tooling tests.
The additional proof cases establish availability of the measurement, not
native execution or specification coverage.

## Reproducing complete native module suites

`eng\run_native_suites.py` publishes and executes all thirteen generic library
test projects for one framework/RID cell. Unlike the package-consumer probes,
it runs each complete native TUnit suite and its matching managed assembly.
Select an individual project while investigating a failure:

```powershell
python eng\run_native_suites.py --framework net10.0 --rid win-x64 --project XRegistry.Core.Tests --artifacts-root D:\temp --work-root D:\temp
```

Omit `--project` to run all thirteen suites, or repeat it to select several.
Keep `dotnet`, Git and Python on `PATH`; Windows link fixtures use both `pwsh`
and `powershell.exe`. Install the File/OCI independent-oracle dependency
closure, with versions and hashes constrained by the existing oracle lock:

```powershell
python -m pip install --require-hashes --only-binary=:all: -c eng\requirements-oracles.lock 'jsonschema[format-nongpl]'
```

Missing tools or oracle dependencies fail the gate; they do not turn cases
into skips.
Both roots must already exist and be absolute paths. The artifact root gets a
fresh run directory; the work root supplies `TEMP`, `TMP` and `TMPDIR` for the
test processes. Use local NTFS/ext4 for storage tests. The selected RID must
match the actual host, and each process's proof must report that OS and process
architecture. Cross-compilation and emulated processes cannot qualify a cell.

The driver requires a clean committed checkout, records hashes of all tracked
files and rechecks them after every suite. Every publication uses a new build
directory, must execute ILC and must emit no IL warning or error. Native and
managed runs must pass every case and produce identical multisets of class,
method and parameterized-case display names. Missing, changed or duplicated
cases therefore fail the gate even when every observed native case passes.
The native proof requires zero JIT compilations; the managed baseline must
report non-native status with a positive JIT count and be rejected by the
receipt converter without creating a receipt.

Each run retains source inventories and a summary; each suite retains publish
and execution logs, the actual native executable, both TRX files, process
proofs and a native receipt. The summary binds the source commit to executable,
managed-assembly, TRX, proof and receipt hashes. A failed suite remains failed
while later suites run; a source change aborts the remaining suites. Neither a
partial run nor a receipt inside a failed summary qualifies that cell.

The `native-module-suites` job in `aot.yml` schedules all eight framework/RID
cells and uploads evidence after failure as well as success. Linux jobs
provision a separate ext4 test-data volume. OPC UA, sample-hosting and bridge
tests are explicitly classified outside this generic-library gate; their
omission is not evidence that they passed. Scheduling these cells does not
establish that they pass, resolve the .NET 8 HTTP dependency warning below,
review requirement mappings or promote a package's qualification status.

The driver passed complete Core and Models suites at
`18728752587a11d9fabc1af1b95426c2d3a57bc5` on both .NET 8 and .NET 10/
Windows x64: 756 and 1,006 native cases respectively, with matching complete
managed case inventories. Their receipts aggregate 268 and 332 methods.
Both native processes reported Windows x64 with zero JIT compilations; both
frameworks' managed baselines reported non-native status with positive JIT
counts and were rejected without receipts. Source inventories remained
unchanged. Evidence is retained in `D:\temp\native-suites-mfdnfwlc` (.NET 8)
and `D:\temp\native-suites-rh3auxlv` (.NET 10). These selected subsets do not
qualify a complete thirteen-project cell.

## Current evidence and uncovered cells

The [native matrix at `c7ff014`](https://github.com/marcschier/xregistry-dotnet/actions/runs/41044488180)
passed all 28 jobs on Windows/Linux x64 and ARM64: eight generic package-consumer
cells, four model-consumer jobs executing both frameworks, eight storage cells,
and four jobs each for the client and bridge samples. The .NET 10 storage jobs
also executed the durable HTTP probe. Generic consumer receipts are described
in [consumer embedding](consumer-migration.md#current-evidence-and-limits).
These jobs qualify their development probes, not the complete native TUnit
suites or every specification obligation.

The eight generic consumer artifacts each contain 27 passed cases, zero
IL diagnostics or JIT compilations, both rejected managed controls and matching
executable hashes. Their raw before/after source inventories agree; comparison to the
source commit respects the repository's declared Git text line-ending rules.
The downloaded reports and binaries are retained at
`D:\temp\xregistry-native-matrix-c7ff014`. Four reports for each of the client,
bridge and durable HTTP samples and eight process-crash/restart reports also
passed.
This refresh replaces the older package-consumer checkpoint at `ecd2a3b`;
it does not refresh every module's full-suite receipt.

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

Later live HTTP workflows exposed and corrected missing Core error arguments
and over-rejection of uninlined document-view navigation. At
`306e6cd6d494de9b6cf0f5e47563dbd4c2d5e43f`, the complete HTTP suite passed
421 native cases on .NET 10/Windows x64, including all 28 TM/TD incremental
workflow cases. ILC ran with zero IL diagnostics. The same-process proof
reported native execution with zero JIT compilations; the managed control
reported false/3,999 and was rejected without creating a receipt.

`D:\temp\xregistry-http-native-306e6cd` retains the TRX, proof and receipt,
with publish/run logs beside it. The receipt aggregates 136 distinct passed
methods, with zero failed or skipped. Executable SHA-256:
`d91381b9a232e66547cba3d241de5591ac3c74cdc71cbdbf1704f7e393e6ed3d`.
This qualifies the tested live HTTP workflow on in-memory persistence, not a
durable HTTP restart workflow, another platform or complete Server/Federation
suite execution at this source.

The HTTP suite was refreshed at `449d22934f72c1608f764adee0397c8b05b321ae`
with actual file-backed deletion/restart workflows. All 427 native cases passed
on .NET 10/Windows x64, including all 34 incremental HTTP workflow cases.
Six of those cases reopen `LocalFileStore`, its persistence adapter and the
HTTP host through three lifetimes, preserving greater-than-64-bit allocation
after retained history, Resource deletion or Group deletion.
ILC ran with zero IL diagnostics. The same-process proof reported native
execution with zero JIT compilations; the managed control reported false/3,999
and was rejected without creating a receipt.

`D:\temp\xregistry-http-native-449d229` retains the TRX, proof and receipt,
with publish/run logs beside it. The receipt aggregates 137 distinct passed
methods, with zero failed or skipped. Executable SHA-256:
`e27420657888bb185ff6e9b41560bbc60fd527bab51938f5eb3f6187ae3e363a`.
This extends the tested HTTP path to orderly file-store close/reopen; it does
not qualify crash or power-loss behavior, other platforms, refreshed package
consumers or complete execution of the other module suites at this source.

After common proof inclusion at
`c7ff0146084dd85272e13073a00f5706a276139f`, the complete Core suite passed
756 native cases on each of .NET 8 and .NET 10/Windows x64. Both publications
ran ILC without IL diagnostics; both same-process proofs reported native
execution with zero JIT compilations. Managed controls reported false/4,005
and false/3,999 respectively and were rejected without creating receipts.

Evidence directories are `D:\temp\xregistry-core-native-c7ff014-net8` and
`D:\temp\xregistry-core-native-c7ff014`, with publish/run logs beside them.
Each receipt aggregates 268 distinct passed methods, zero failed and zero
skipped. Executable SHA-256 values:
`7ded8561d97ebc401a37f876cf974f4fd1475ddd087cb72e61f62d2f7bc855e5`
for .NET 8 and
`f1903e361214ee771213945c2cb5b579f8d0a00158b452d223ce223666af90df`
for .NET 10. These two Core cells do not qualify other module suites, the
remaining platforms or reviewed specification requirements.

The complete Models suite was then refreshed at
`611fb0be03be89d9a005437a65ba29aae9b796d8`: 1,006 native cases passed on
each of .NET 8 and .NET 10/Windows x64, including all 76 Media Endpoint cases.
Those cases cover every declared producer streamformat, exact-case open
codec identifiers and the absence of a codec/format compatibility matrix.
The full managed gate passed 10,894 cases plus 283 tooling tests.
Both native publications ran ILC without IL diagnostics, reported zero JIT
compilations and rejected their managed controls, false/4,006 and false/3,999.

Evidence directories are `D:\temp\xregistry-models-native-611fb0b-net8` and
`D:\temp\xregistry-models-native-611fb0b`, with publish/run logs beside them.
Each receipt aggregates 332 distinct passed methods, zero failed and zero
skipped. Executable SHA-256 values:
`97657b8df7c7ee1ea945bc42388d5e9f671feed42fac1639674775eeac5493fb`
for .NET 8 and
`5c81a0cb37a46301e469f657fccfcb16e410a6758dac526b20e70052dc788825`
for .NET 10. Three W26 successor blocks now map to reviewed public tests;
the complete model-type matrix and provider/receiver execution remain pending.
No requirement was promoted to native qualification or global semantic review.

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
