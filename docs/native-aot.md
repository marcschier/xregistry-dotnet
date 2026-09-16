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
pwsh -NoProfile -File eng\test-model-packages.ps1 -RuntimeIdentifier win-x64
pwsh -NoProfile -File eng\test-client-sample.ps1 -RuntimeIdentifier win-x64
pwsh -NoProfile -File eng\test-storage-native.ps1 -RuntimeIdentifier win-x64 -Framework net10.0 -DataRoot D:\xregistry-test-data
pwsh -NoProfile -File eng\test-git-interop.ps1 -RuntimeIdentifier win-x64 -Framework net10.0
```

On Windows, the scripts prepend the Visual Studio Installer directory so the
compiler can find `vswhere`. Do not inherit a `vcvars64` Platform setting for
ordinary managed builds. Linux compilation needs clang and zlib development
headers. Durable-storage tests require local ext4 rather than an overlay
filesystem; their CI job provisions a separate per-job ext4 data volume.

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

The latest retained local source state has a fresh nonincremental build with
zero warnings/errors, 8,967 managed cases, and 276 tooling tests. The exact CI
formatting command and ordinary CI-equivalent build passed. Comparative
allocation and stalled-body deadline fixtures use exclusive scheduling; their
byte/deadline assertions are unchanged. A bounded review of materialization,
source-capture, and producer-policy trust boundaries reported no
high-confidence security findings; this is not a whole-product certification.

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

All eleven packages in `artifacts\embedding\29c45e4bd888` pass 25 native
consumer cases per Windows x64 TFM and ordinary/feature-masked JIT rejection
controls. The source fingerprint is
`5328167584619aded4223746a71c1fe2ea0d48f201513a4e49cc7a61cc0946e5`.
The three principal samples pass 17/14/6 checks; native storage passes 28 tests
and six process-crash/restart cases. Reference Git smart-HTTP interoperability
passes 26 cases and 56 exchanges on each Windows x64 TFM.

The corrected independent source oracle passes 976 explicit cases with the same
38 OPC-UA exclusions through SPEC-020. All 1,835 extracted clauses have
source-pinned reviews, but none is made release-qualified by that count. The
strict tooling retains rejected receipts for a concurrently changed protected
ledger and for pytest subtest/XML accounting disagreement.

Linux/ARM64 package/sample cells, peer limitations, and release approvals are
not covered by the current source-matched evidence. The complete outstanding
matrix is tracked in the
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
