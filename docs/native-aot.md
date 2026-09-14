# Native AOT qualification

Runtime libraries target .NET 8 and .NET 10. The principal samples target
.NET 10. Required release platforms are Windows/Linux x64 and ARM64; annotation,
cross-compilation, emulation and successful JIT execution do not satisfy those
runtime cells.

Packable projects enable trimming/AOT analysis through `Directory.Build.targets`.
The current samples and qualification probes publish Native AOT explicitly.
Use matching native toolchains and execute the resulting binaries:

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

Native/RID restores write private lock profiles beneath the evaluated project
`obj`/artifact directory. Portable source locks remain unchanged, so running a
native publication cannot invalidate the next CI `--locked-mode` restore.
An explicitly selected `NuGetLockFilePath` is never replaced. The isolation
checks exercise all required RID property profiles and an actual Windows native
restore/publication followed by a portable locked restore.

## Actual native execution, not a misleading runtime switch

The controlled Git interoperability workstream ran the probe through `dotnet`
as a negative control. A native-publish runtime configuration can make
`RuntimeFeature.IsDynamicCodeSupported` false **even under CoreCLR**. That flag
alone is therefore insufficient proof.

Qualification probes now also require `IsDynamicCodeCompiled` to be false and
`JitInfo.GetCompiledMethodCount()` to be zero. Native results are associated with
the actual published artifact and hash. The model-package, client-sample,
storage-crash and Git drivers additionally execute a real managed negative
control. It must fail native-only work before network/storage side effects, or
report non-native status for the sample's `runtime-info` command.

These checks do not authenticate an arbitrary untrusted executable's report.
Release receipts still need the exact qualified source, package, runner,
artifact and executed-case evidence described by the conformance/release gates.

## Current evidence and remaining cells

**Latest local implementation checkpoint:** a fresh nonincremental build passes
with zero warnings/errors and all 8,967 managed cases pass. All 276 tooling tests,
the exact CI formatting command and the CI-equivalent locked build pass.
The comparative allocation and stalled-body deadline fixtures use exclusive
scheduling; their original byte/deadline assertions remain unchanged.
The bounded review of new materialization, source-capture and producer-policy
trust boundaries reported no high-confidence security findings; this is not a
whole-product security certification.

| Windows x64 native suite | .NET 8 | .NET 10 |
| --- | --- | --- |
| Core model, metadata, queries and numbers | 750 passed | 750 passed |
| Models, including Message/Endpoint materialization | 750 passed | 750 passed |
| Server, including final Message/Endpoint admission | 834 passed | 834 passed |
| Federation and producer model/capture constraints | Not refreshed | 458 passed |

These successful executables have native PE headers, no CLR directory and no
IL warnings; their hashes are in `artifacts\resume-final-native-images.json`.
Earlier Validation 429/TFM, OCI 270/TFM, File 93/TFM and Git 155/TFM runs remain
separate module receipts. The .NET 8 Git test host emitted two ASP.NET framework
IL warnings; that test-host cell is not described as warning-free. No shipping
package-consumer IL warnings were suppressed.

All eleven final packages in `artifacts\embedding\29c45e4bd888` pass 25 native
consumer cases per TFM and ordinary/feature-masked JIT rejection controls.
That feed includes Message inheritance/header completion, Endpoint templates,
schema-object selection, stable OpenUSD collision assignment, and
catalog-origin/shared-budget HTTP acquisition. Its
source fingerprint is
`5328167584619aded4223746a71c1fe2ea0d48f201513a4e49cc7a61cc0946e5`,
verified against the final package/probe sources after native publication.
The three native principal samples pass 17/14/6 checks; native storage passes
28 tests and six process-crash/restart cases. Actual reference Git smart-HTTP
interop passes 26 cases and 56 exchanges on each native TFM.

The Git interop script exposed one stale source lock: its project graph lacked
the legitimate Federation-to-Models edge. SDK regeneration changed only that
edge and added the Models project; every retained external package version/hash
remained unchanged. The repaired locked script passes, and no runtime lock
was altered by native publishing.

The corrected independent source oracle passes 976 explicit cases with the
same 38 OPC-UA exclusions through SPEC-020. All 1,835 extracted clauses now
have source-pinned reviews; this is not automatic release qualification.
Earlier rejected receipts remain:
one detected a concurrently changed protected ledger; another detected pytest
subtest/XML accounting disagreement. Stable-ledger re-execution and separate
explicit test identities resolved those issues without relaxing the gate.
The final tooling also retries only transient Windows sharing violations during
owned fixture cleanup, with a finite budget and explicit reporting; permanent
access failures still fail.
Linux/ARM64, peer limitations and release approvals below remain separate.

### Historical checkpoints

**Catalog/server-preset and restore-isolation checkpoint:** locked restore and
a fresh nonincremental build pass with zero warnings/errors; all 6,841 managed
cases and 273 tooling cases pass. Windows x64 Server executables pass 631
cases per TFM and Federation passes 342 on .NET 10, with zero IL warnings and
native PE verification. All eleven freshly packed NuGets at
`artifacts\embedding\f6f2ce128318` pass 25 native consumer cases per TFM and
ordinary/feature-masked JIT controls. This gate fingerprints portable source
locks and restores producers in locked mode. Its source fingerprint is
`22fa72eb00fa565020623569696a4aa92ef63bf35c49455bbb69e6e6add4d032`.
The three native principal samples pass 17/14/6 checks; FileServer now proves
actual HTTP rejection/rollback of invalid catalog metadata and acceptance of a
website-only base description. This checkpoint includes SPEC-009, but does not
complete the remaining specification/platform qualification.

**Integrated domain/source checkpoint:** the final normalized source rebuilds
with zero warnings/errors and passes all 6,395 managed cases. Windows x64
Server tests pass 526 cases on each TFM with zero IL warnings and native PE
verification. The fresh all-eleven-package consumers at
`artifacts\embedding\bfc5d144ac52` pass 25 native cases per TFM and both ordinary
and feature-masked JIT controls. Their expanded model/validator case exercises
OpenUSD identifier construction, verified SHA256 fallback and digest-mismatch
rejection through actual NuGets. The final source fingerprint is
`857ebc04a188b57fca3f951dba287b4073f62fb720930891286cb0c37fb637dd`.
All three principal native samples pass again (15/14/6 checks).

The independent specification runs are separate evidence: 898 original and
937 corrected cases passed, each explicitly deselecting the same 38 OPC-UA
cases. One corrected attempt exhausted memory; its exact OCI case and complete
corrected rerun passed without changing selectors or budgets. An initial
tooling cleanup race and allocation-test anomaly are also retained separately;
subsequent complete tooling and managed runs passed without relaxed assertions.
Neither the failed attempts nor the remaining platform/domain gaps are hidden
by these development receipts. No release status was promoted.

The subsequent SPEC-009 model-sharing correction has separate focused evidence:
the compiled OpenUSD type-identity case and all 31 OpenUSD Server cases pass
on both TFMs, and 938 corrected independent oracle cases pass. Catalog
publication work is still being integrated. The complete native/package
checkpoint above remains tied to its recorded SPEC-008 source fingerprint;
it is not relabeled as qualification of those later edits.

**Completed Core protocol and representation checkpoint:** a fresh complete rebuild has
zero warnings and errors, and all 5,483 managed cases pass. Native Windows x64
Core (701) and Server (423) suites pass on both .NET targets; ASP.NET (194),
HTTP (388), Federation (239) and Bridge (165) pass on .NET 10, all with zero IL
warnings and native PE verification. These eight executions total 3,234 cases.
The all-eleven-package fresh-feed consumers at
`artifacts\embedding\0c31dd152415` pass 25 cases per Windows TFM and both ordinary
and feature-masked JIT rejection controls. Package/probe sources at that
checkpoint matched the feed's fingerprint
`aed7a9497465909169ca6d9712e46009bfb38e6e32d9b4cbcd169129cfd35717`.
The three principal native sample gates also pass again (15/14/6 checks).
The Bridge root check now verifies configuration omission by default and
producer ownership through explicit `?inline=capabilities`; it no longer
expects unrequested configuration.
These results supersede the earlier slices below only for the executed cells;
no platform or release status was promoted.

Subsequent directory-mapping changes have separate Windows development
evidence: Core now passes 716 native cases per TFM, and Federation passes 300
native .NET 10 cases, with no IL warnings and native PE checks. Current
Federation managed cases pass 300 per TFM; File/Git/OCI managed integration
passes 77/129/230 per TFM. These cover captured model admission, bounded
Version-state/closure checks, accurate snapshot capabilities and the shared
bounded JSON factory. They do not refresh the preceding all-package feed or
principal-sample checkpoint. Domain work and final-source integration remain
in progress.

Set `XREGISTRY_BRIDGE_REQUIRE_NATIVE=1` when running the published Bridge test
executable. Its native/JIT contract intentionally expects a managed process
without that setting. An initial ad hoc native run omitted it and failed that
assertion; the unchanged binary passes all 165 cases in native-required mode,
while the real managed assembly fails the same native-required assertion.
The initial failure and corrected execution are retained separately.

The earlier model-fix feed `artifacts\embedding\c734fe1dcd9d` and its 5,077
managed cases remain historical evidence, not the current-source receipt.
An earlier recovery passed 4,227 managed tests with no failures/skips.
Those Windows native runs included the complete Core (460) and Server (274)
suites on both TFMs, HTTP (388) and ASP.NET (94) on .NET 10, plus the updated
Git (129) .NET 10 suite. Earlier recovered File (77) and OCI (230) runs also
cover both Windows TFMs. These counts identify executed slices, not clause
coverage or a substitute for the missing platforms.

All eleven then-freshly packed runtime NuGets at
`artifacts\embedding\b2e1ee2ca80f` pass the 25-case native consumer on both
Windows TFMs, including model-aware streamed metadata headers and both JIT
negative controls, with zero IL warnings. Current package/probe source hashes
match that feed. The three principal Windows samples have also executed their
real gates: 15 durable-server, 14 client and six bridge checks. No package
status was promoted by these development runs.

Local Windows x64 execution includes both-target clean model-package consumers,
both-target managed-Git reference interoperability, both-target HTTP/compression
tests, and .NET 10 core/event, federation, OCI, durable-storage and client-sample
executables. These runs exercised behavior, not just process startup.

Earlier Linux x64 native foundation/client/File/storage slices also executed.
After local Docker recovered, current .NET 10 Linux x64 native suites also
passed: 75 File/OCI filesystem cases, 185 OCI cases, 315 HTTP/decoding cases and
28 storage/engine-adapter cases. They ran without an installed .NET runtime,
as a non-root user, without external network access, using isolated ext4 data
volumes and a read-only container root.

The File fixture harness was corrected to carry its immutable fixtures,
independent Python oracle and license into published output, rather than depend
on a checkout-relative directory. No runtime semantics or expected results were
weakened to make the relocated test executable pass.

Subsequent Linux builds were blocked when the Docker data filesystem became
read-only as host C: filled. Only this task's completed diagnostic artifacts
were relocated to D: to recover space; no shared Docker cleanup or filesystem
repair was attempted. The four completed suites remain evidence, but later
hosting/validation/crash builds and current ARM64 execution are not claimed.
The final read-only Docker probe still found no available Linux-engine pipe;
no shared service was restarted or repaired.

`.github/workflows/aot.yml` now requires all eleven runtime packages through
eight fresh-feed/native-consumer TFM/RID cells, plus model-source, storage and
all three samples' RIDs. Linux consumer work uses an explicitly isolated ext4
directory; the package/build cache is separate. The Git reference and upstream
interoperability lanes are in `interop.yml`. The native FileServer and
FederationBridge principal executables have now run functional HTTP checks on
Windows x64 with actual JIT negative controls.

These workflows are prepared locally, not evidence of hosted execution.
Both Windows x64 all-package consumers have executed 25 named behavioral cases,
including literal and escaped Core IDs, with normal and feature-masked JIT
negative controls and zero IL warnings. Other all-package cells, unexecuted
native platforms and complete final-source qualification are still open.

## .NET 8 HTTP test dependency warning

The TUnit HTTP test project brings packaged System.Text.Json 10.0.x into its
.NET 8 host. An isolated reproduction established that ASP.NET Core/ILC 8.0.31
with that package analyzes `JsonOptions.CreateDefaultTypeResolver`, producing
IL2026/IL3050 even while runtime JSON reflection is disabled. Switching to raw
RequestDelegate routes does not fix that combination. This is reproducible
without TUnit and is not a decoder implementation warning.

The test binary executes its cases, but its .NET 8 publishing warning gate is
not clean. No warning was suppressed and no dependency was silently downgraded.
A separate clean-cache consumer of actual Client/Core NuGets, using the
framework JSON dependency, published without those warnings and passed 31
native Kestrel checks. See [HTTP compression](http-compression.md) for the
causal evidence and exact qualification boundary.
