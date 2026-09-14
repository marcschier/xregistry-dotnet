# Consumer embedding and role-oriented migration

This guide describes the **current public standalone interfaces** and an
executable, package-reference-only consumer. It is a development handoff, not
a drop-in binary compatibility promise, completed UA migration, release
qualification or claim of complete xRegistry conformance.

The informative reference was `UA-.NETStandard5`, branch
`feat/xregistry-http-bridge`, initially pinned at
`94d5fa1ad5cfa57ce41aed90aec904c58e6ef31f` plus dirty work. That identifies the
origin of the role comparison only. This workstream neither inspected, edited,
built nor depended on that worktree. The experiment is not a specification
or an implementation-adherence target.

## Roles, not old type substitutions

| Informative role | Current standalone choice | Important distinction |
| --- | --- | --- |
| Generic endpoint/value/model/path/codec concerns from `Opc.Ua.XRegistry/Protocol` | `XRegistry`: `RegistryJson`, `RegistryModel`, `RegistryId`, `RegistryPath`, `RegistryHeaderEncoding`, `RegistryHeaderMetadata`; `XRegistry.Models` for packaged model sources | A domain Endpoint Definition is not an HTTP route. IDs follow the standalone Core grammar; map native identifiers explicitly. There is no compatibility alias for an old bespoke type. |
| Generic operation dispatch | `RegistryEngine.ExecuteAsync(RegistryOperation, RegistryOperationContext, CancellationToken)` in `XRegistry.Server` | The built-in engine owns model, lifecycle, versions, validation and atomic change preparation. It is not a pass-through to an arbitrary authoritative protocol dispatcher. |
| HTTP client role from `Opc.Ua.XRegistry.Http` | `XRegistry.Client.XRegistryHttpClient` | One explicit HTTP operation, unchanged status/headers and bounded metadata/Document consumption. No implicit workflow replay or authority selection. |
| HTTP serving role | `XRegistry.AspNetCore.MapXRegistry` | Maps a **concrete `RegistryEngine`**, using an explicit RequestDelegate. It does not accept an arbitrary `IRegistryEngine` implementation or start/secure the host. |
| Generic transactional provider | Application-owned `IRegistryPersistence`, or `LocalRegistryPersistence` over `LocalFileStore` from `XRegistry.Storage.File` | Implement the documented snapshot/candidate/publication contract. Storage generations are not entity epochs or remotely advertised Registry revisions. |
| Generic schema-source validation | `XRegistry.Validation` validators, optionally composed through Server validation policies | These validate supported schema formats/policies. They are not native OPC UA NodeSet/session/projection implementations. Unsupported results are not successes. |
| Federation read acquisition | Optional `IFederationReadSource` / `HttpFederationReadSource` in `XRegistry.Federation` | A read-source seam, not a write dispatcher. Ordinary HTTP capture is a bounded live observation, not a recursive immutable snapshot. |
| Native NodeSets, UA sessions, FileType behavior and protocol projection | Remain in the consuming application/protocol integration | No OPC UA package, session identity, native transport or projection type is introduced into the standalone generic packages or this consumer. |

Legacy prepare/replay/incarnation/generation extensions are **not standard
xRegistry HTTP** and are not required by this embedding. The in-process
`IRegistryPersistence.PrepareAsync` and `IRegistryCommit.CommitAsync` contract,
and `RegistryOperationContext.PrepareResponseAsync`, are not HTTP prepare/replay
endpoints. Their names do not imply wire compatibility with proprietary
workflow extensions.

## Choose the real seam

The supplied example chooses an application-owned transactional **record
store** behind the local engine:

```text
application record store
    -> ApplicationRegistryPersistence : IRegistryPersistence
    -> RegistryEngine + explicit IRegistryAuthorizationPolicy
    -> real Kestrel + MapXRegistry
    -> public XRegistryHttpClient
```

The application adapter owns its concurrency/durability policy and treats
engine records as opaque. It must preserve model envelopes, event/outbox state,
correlation reservations and Documents, rather than translating only the
visible entity rows it happens to recognize.

These are the current contracts:

```csharp
public interface IRegistryPersistence
{
    bool IsReadOnly { get; }
    ValueTask<IRegistrySnapshot> ReadSnapshotAsync(
        CancellationToken cancellationToken = default);
    ValueTask<IRegistryCommit> PrepareAsync(
        long expectedGeneration,
        IReadOnlyList<RegistryMutation> mutations,
        CancellationToken cancellationToken = default);
}
```

`IRegistrySnapshot` exposes Generation, Find, GetChildren, EnumerateRecords and
OpenDocument. Point reads must not copy the whole Registry. Open Document
streams remain valid after snapshot disposal. `RegistryMutation` explicitly
distinguishes preserving, replacing and removing a Document. Input streams
remain caller-owned and must be consumed during preparation. A candidate
publishes all records or none, rechecks the expected generation and must not
report cancellation as rollback after publication. Unknown commit outcomes
are not automatic retry opportunities.

```csharp
var engine = new RegistryEngine(
    new RegistryEngineOptions
    {
        RegistryId = "application-registry",
        PublicRoot = configuredPublicRoot,
        Model = RegistryModel.Compile(modelSource),
        AllowAnonymousReads = false
    },
    applicationPersistence,
    applicationAuthorization);

// Authentication middleware must populate a trusted HttpContext.User first.
app.MapXRegistry(engine, new RegistryHttpOptions
{
    MountPath = "/registry",
    AuthenticationChallenge = "Bearer"
});
```

`PublicRoot` is trusted advertised metadata, never inferred from request Host.
MountPath is local routing. The engine does not dispose application persistence,
policy services or caller Document streams. Results own their metadata and
Document bytes independently of the request, store snapshot and HTTP host.

**Meaningful limitation:** if an existing authoritative endpoint must retain
its own operation/lifecycle semantics instead of storing the local engine's
opaque state, implementing `IRegistryEngine` alone does not make it pluggable
into current `MapXRegistry`. That requires a separately designed application
HTTP adapter/forwarding layer, or a read-source adapter where only read
acquisition is needed. Do not claim the local-engine persistence seam is a
transparent replacement for every authoritative dispatcher.

## The executable consumer

The new `tests\PackageSmoke\Embedding*` files are copied into isolated consumer
projects by `eng\test-embedding-packages.ps1`. The final consumers reference
all **eleven** freshly packed runtime NuGets from `eng\packages.json`:
`XRegistry`, Client, Server, AspNetCore, Models, Validation, Storage.File,
Federation, Bindings.File, Bindings.Git and Bindings.Oci, plus the ASP.NET Core
framework. They have **no source ProjectReferences, TUnit or OPC UA dependency**.

Application-side hosting, authentication, transient storage and evidence code
use BCL and ASP.NET Core APIs. The existing Client brings managed SharpZipLib
1.4.2; Storage.File brings Microsoft.Data.Sqlite and SQLitePCLRaw, including
the actual native SQLite library. Native SQLite is intentional, not a native
Git backend. This is not a claim that the complete package closure is
framework-only. No separate runtime package was added just for the probe.
No dynamic JSON serializer is used: JSON evidence uses `Utf8JsonWriter`,
metadata uses owned `RegistryJson`/`JsonDocument`, and package assembly
references are inspected using BCL metadata APIs.

`EmbeddingPersistence.cs` is an application-shaped adapter, not a wrapper over
the library's in-memory provider. It stages copy-on-write immutable images,
publishes with an expected-generation check, keeps point reads indexed and
preserves independent snapshot/Document leases. It bounds the live image to
256 records/1 MiB, each Document to 64 KiB and each metadata value to 64 KiB.
Its atomicity is **process-local only**: it supplies no crash durability,
multi-process coordination, database backup or recovery guarantee. An actual
application must implement/qualify its own durable transaction, or select the
documented File store on a supported filesystem.

The probe deliberately uses only a literal loopback HTTP listener and
freshly generated fixture-only bearer credentials. An ASP.NET authentication
scheme establishes reader/writer identities, and an explicit core policy
checks each requested/affected entity. It does not trust identity headers.
This is a local embedding probe, not production token/TLS provisioning.
See [sample security](sample-security.md) for the separate HTTPS sample-host
contract. Never reuse an incoming caller credential as an upstream credential.

The original 15 named cases remain intact at the beginning of
`EmbeddingCases.json`:

| Case | What crosses the public seam |
| --- | --- |
| `package-assets-no-opc` | Exact restored/nupkg assembly bytes and their actual metadata references; no OPC/test-framework assemblies |
| `model-path-and-header-codec` | Arbitrary model-defined types, escaped IDs and independently specified UTF-8 header encoding |
| `packaged-model-and-validator` | Packaged Core model, distinct valid/invalid schema-source decisions, OpenUSD symbolic identifier construction, verified SHA256 fallback and explicit digest-mismatch rejection |
| `anonymous-and-header-identity-denied` | Actual HTTP 401 challenges; forged identity headers cannot authenticate or prepare a mutation |
| `arbitrary-metadata-and-caller-identity` | Custom object/boolean/string metadata, exact IDs and trusted writer/reader identities reaching core policy |
| `single-dispatch-single-publication` | One model-aware streamed Document PUT carries typed metadata and produces one HTTP dispatch, one preparation and one atomic publication; returned readonly IDs, exact epoch and Unicode/map headers are read before consuming bytes |
| `exact-document-and-upload-ownership` | Independently owned model-aware response metadata, actual HTTP binary body, decoded count and caller-owned upload/destination streams |
| `metadata-document-separation` | `$details` updates domain metadata without changing Document bytes |
| `authenticated-reader-write-denied` | Authenticated reader receives 403 before application preparation/publication |
| `unsupported-mutation-before-preparation` | Unsupported model mutation returns 405 and the specific problem code before provider preparation |
| `readonly-provider-before-access` | Read-only mutation fails before even the provider snapshot read |
| `direct-operation-caller-stream-ownership` | Transport-independent operation and caller-owned Document input, without any HTTP workflow |
| `staged-publication-conflict-and-cancellation` | Unpublished candidates, atomic two-record commit, stale-generation rejection and cancellation without publication |
| `snapshot-lease-lifetimes` | Preparation consumes but does not dispose input; metadata/Document lease survives snapshot/adapter/store disposal |
| `results-outlive-host-and-store` | HTTP shutdown does not own application persistence; returned metadata/core Documents survive all owner lifetimes |

The binary fixture includes invalid UTF-8, NUL and JSON-looking bytes; expected
bytes are specified independently of the implementation. Entity metadata is
asserted through public reads, not reconstructed from the decoder. Provider
counts are observations at the explicitly selected application persistence
seam, not hooks into engine internals.

Ten appended cases exercise the other runtime packages and escaped federation
identity handling; simply referencing
their assemblies cannot satisfy the evidence validator:

| Appended case | Actual operation |
| --- | --- |
| `storage-file-initialize-commit-reopen` | Real SQLite/immutable-file initialization, commit, `PutPreservingDocument`, disposal/reopen, exact binary and empty-versus-absent Documents |
| `storage-file-adapter-preserves-document` | `LocalRegistryPersistence` preparation/commit, repeated generation-indexed reads, old/new snapshots, metadata-only Document preservation and caller-owned store/leases |
| `file-document-tree-authorized-read` | Explicit File document-tree mode within an approved directory, outside-boundary/unknown-mode rejection, detached exact Document and captured root SHA-256 |
| `file-oci-layout-explicit-read` | Explicit OCI File mode/reference, pinned root/default Version and exact content after source disposal |
| `git-sha256-verified-pack-tree-blob` | Reference-Git-verified SHA-256 pack, 11 verified objects, nested-tag peeling, pinned commit/root tree, exact binary/empty blobs and `GitDocumentTreeReader` |
| `oci-offline-frozen-closure` | Exhaustive `OciSnapshot.ValidateAsync` against the independent 99-object inventory, plus every frozen expected embedded Document |
| `oci-linked-frozen-closure` | Exhaustive independent 98-object linked closure and the unfetched external descriptor, without inventing external bytes |
| `federation-live-http-source` | Real `HttpFederationReadSource`/`IFederationReadSource` through a separate Kestrel `MapXRegistry` host, metadata/Document/selected Version, bounded observations and pre-dispatch immutable-snapshot rejection |
| `native-dependencies-no-git-backend` | Actual process module inventory: native SQLite loaded, its file hash matched to the selected nupkg asset, and no native Git backend loaded |
| `federation-escaped-identities` | Public `RegistryId.ParseEscaped`, raw/reserved-escaped/unreserved-escaped Group/Resource/Version reads, unchanged wire XIDs, exact default/explicit Documents, and pre-dispatch rejection of malformed/double-decoded/separator IDs |

`EmbeddingFixtureInputs.json` pins independent test inputs. The driver copies
41 frozen mapping files, 120 frozen OCI files (including `expected.json`), the
reference-Git fixture and the conformance license into a fresh controlled run.
It verifies byte lengths, individual SHA-256 values and portable ordered-tree
digests without regenerating expectations. The native consumer makes separate
controlled File copies. These fixtures are consumer test content, **not**
additional runtime-package assets.

The OCI inventory requires 99/98 objects, 44 indexes, 25 manifests, 25 configs
and 4/3 embedded Documents respectively. These are frozen expectations, not
counts obtained from this consumer's producer. The Git fixture was verified by
reference `git index-pack --strict` when authored; the native consumer runs
only public managed Git readers. It does not invoke a Git executable or
establish new network-acquisition evidence from a local pack.

The live federation fixture constructs its concrete engine with the actual
bound loopback Registry root; it does not rewrite source metadata or relax
origin/link checks. The earlier ordinary-ID-only limitation is resolved by
the updated Core/Federation runtime interfaces and the new native
`federation-escaped-identities` case. Core IDs such as `group:one`,
`item@stable` and `v:1` can appear percent-escaped in a relative-URI XID.
`RegistryId.ParseEscaped` decodes one segment exactly once; federation compares
decoded ordinal identity while retaining the source's wire metadata XID.
The probe accepts ordinary escapes such as `%69tem%40stable`, not just escapes
of reserved characters. Invalid escapes, double-encoded percent sequences
and encoded path separators are rejected before HTTP dispatch.

## Run the development package gate

```powershell
python -m unittest discover -s tests\Tooling -p "test_embedding*.py" -v
pwsh -NoProfile -File eng\test-embedding-packages.ps1 -RuntimeIdentifier win-x64
```

The default executes both net8.0 and net10.0. `-Framework net8.0` or
`-Framework net10.0` narrows a diagnostic run. The script accepts win-x64,
win-arm64, linux-x64 and linux-arm64, but rejects a RID that does not match the
actual host/process architecture. A cross-compiled executable is not executed
or reported as qualified.

### Work filesystem selection

`-WorkRoot <absolute-existing-directory>` optionally selects the filesystem
used for SQLite stores and controlled native File/OCI copies. The driver
creates a unique `embedding-<guid>` child beneath that root and a separate
TFM subdirectory inside it. It never creates, deletes or cleans the
caller-supplied root itself, and does not touch sibling work directories.
The unique child is removed on success or failure; replaced root/child links
cause an explicit cleanup failure rather than being followed.

Without `-WorkRoot`, the same unique-child policy uses the run's
`artifacts\embedding\<run-id>\work` directory. Feed, producer/consumer build
outputs, package caches, fixtures and evidence remain under repository
artifacts regardless of `-WorkRoot`. `summary.json` records the selected root,
owned child, whether the root was caller-supplied and successful child removal.

For a Linux CI job whose caller has already mounted and permissioned a local
ext4 volume at `/data`, the invocation inside that native host is:

```powershell
pwsh -NoProfile -File eng/test-embedding-packages.ps1 -RuntimeIdentifier linux-x64 -Framework net8.0 -WorkRoot /data
```

Selecting a directory is not filesystem qualification: `LocalFileStore` still
enforces its existing local NTFS/ext4 policy. An overlay or unsupported
filesystem remains a failing operation, never a skip or successful fallback.
This option does not change package selection, native/JIT guards, case counts
or storage policy.

The WorkRoot follow-up passed **26 tooling tests** (20 evidence checks plus
six real filesystem/helper checks). A net10.0 win-x64 Native AOT run with an
explicit caller root passed all **25 native cases**, zero IL warnings and both
JIT-negative controls. Its unique work child was removed while caller-owned
sentinel and sibling files remained intact. That evidence is retained at
`artifacts\embedding\c5c66ef9ffd6`. No Linux/Docker retry was made for this change.

Each run creates `artifacts\embedding\<run-id>`:

1. Fingerprint selected package source/configuration and probe inputs; pack
   all eleven runtime packages into a new local feed with isolated build outputs.
   Portable source dependency locks are included in the fingerprint and packing
   restores in locked mode; generated RID/AOT profiles under `obj` are excluded.
2. Reject source changes during packing. Resolve actual nuspec versions into
   literal PackageReference versions and restrict `XRegistry*` to the fresh
   feed using package-source mapping.
3. Publish each consumer using a fresh, isolated NuGet cache. Fail on any
   `warning IL...` line and require ILC to have actually executed.
4. Match restored runtime assembly bytes to their nupkg entries, retain copies
   and hashes, and execute all 25 real consumer cases, including native
   dependency and escaped-identity checks.
5. Run the managed assembly as an exact JIT negative control, then run it
   again with both RuntimeFeature flags masked off. Each must exit 2 before
   any HTTP dispatch/case and report a **positive** `JitInfo` compiled-method
   count. Feature flags alone are not proof of native execution.

The guard reads `RuntimeFeature.IsDynamicCodeSupported`,
`RuntimeFeature.IsDynamicCodeCompiled` and
`JitInfo.GetCompiledMethodCount()` independently. Native reports require both
flags false and compiled counts zero at entry and completion. Case-name/order,
packet-count, Document-byte and publication evidence are checked independently
by `EmbeddingEvidence.py`; missing, partial or startup-only reports fail.

Evidence retains source/fixture manifests and digests, exact package versions
and SHA-256 hashes, inspected managed and native asset copies, actual assembly
references/native-module observations, generated consumer source/project files,
native/managed binary hashes, publish logs, request method/target lists and
native/JIT-control JSON reports. Producer and consumer NuGet caches, SDK
temporary/home directories and NuGet HTTP/scratch caches are all placed on
the run's artifact volume (D: for the executed Windows run). Their process
environment overrides are restored afterward. Only those explicitly owned
temporary directories are deleted; evidence is retained. No shared/global
NuGet cache is cleared.
The current run's unique native-work child is also temporary; caller roots and
all reports/package/fixture/hash evidence are retained.

`eng\packages.json` describes **implemented, not qualified** packages. This script performs
**DEVELOPMENT packaging only** and always records `releaseQualified: false`.
It does not update package status, source pins, CI or release metadata.

### Latest completed all-runtime-package handoff

The final local feed is `artifacts\embedding\29c45e4bd888`, with fingerprint
`5328167584619aded4223746a71c1fe2ea0d48f201513a4e49cc7a61cc0946e5`.
All eleven packages pass the 25-case native consumer on both Windows x64 TFMs
with zero IL warnings and both real JIT rejection controls. This includes
Message inheritance and header declarations, Endpoint materialization, concrete
schema selection, stable OpenUSD collision assignment and catalog-origin HTTP
acquisition. Package/probe sources are rechecked against the exact fingerprint.

The integrated solution passes 8,967 managed cases, 276 tooling tests and native
sample gates of 17/14/6 checks. Native Core/Models/Server suites pass
750/750/834 cases per TFM; Federation passes 458 on .NET 10. These are local
implementation receipts, not Linux/ARM64, universal schema or release
qualification. No UA worktree migration or package publication was performed.

### Historical catalog/server-preset handoff

The earlier catalog/server-preset and restore-isolation successor is
`artifacts\embedding\f6f2ce128318`, with fingerprint
`22fa72eb00fa565020623569696a4aa92ef63bf35c49455bbb69e6e6add4d032`.
Its producers restore in locked mode and its source manifest includes portable
dependency locks. All eleven packages passed 25 native cases on both Windows
TFMs, zero IL warnings and both JIT rejection controls. The same integration
passes 6,841 managed cases, 273 tooling cases and native sample gates of
17/14/6 cases. Native Server suites pass 631 per TFM and Federation passes 342
on .NET 10. These remain development receipts, not full conformance or release.

### Prior domain/source handoff

The final normalized domain/source checkpoint is
`artifacts\embedding\bfc5d144ac52`, with package/probe source fingerprint
`857ebc04a188b57fca3f951dba287b4073f62fb720930891286cb0c37fb637dd`.
All eleven fresh packages passed 25 native cases on each Windows TFM, zero IL
warnings and both real JIT rejection controls. The model/validator case now
also invokes the public OpenUSD identifier and integrity helpers, including
consumer SHA256 fallback, exact verified bytes and digest-mismatch rejection.
The final-source manifest was compared with the feed after integration.

This includes the captured-model/mapping fixes, Message/Endpoint runtime rules,
SPEC-008 source corrections and bounded OpenUSD Server publication rules.
All 6,395 managed cases and the three principal native samples (15/14/6)
pass at this checkpoint. The earlier `bfa9bbfced54` feed predates line-ending
normalization and is retained rather than relabeled as the final source.
Unexecuted platforms and the remaining domain/source ambiguities still prevent
a complete conformance or release claim.

SPEC-009 and the subsequent catalog-publication work change the source after
this checkpoint. SPEC-009's shared OpenUSD type and cross-Group alias have
focused managed/source-oracle evidence; the full fresh-feed/native gates must
run again after the next integration stabilizes.

### Retained preceding checkpoints

After integrating the Core scalar, deprecation, readonly validation,
nested-Version precedence and representation fixes, a new fresh feed at
`artifacts\embedding\0c31dd152415` passed the same 25 native consumer cases on
both net8.0 and net10.0 Windows x64. Both JIT negative controls rejected
native-only work, and publishing emitted zero IL warnings. Its source/probe
fingerprint is
`aed7a9497465909169ca6d9712e46009bfb38e6e32d9b4cbcd169129cfd35717`,
verified against that checkpoint's package/probe tree after native integration.
All three principal sample gates also pass (15/14/6 checks); the Bridge
verifier separately checks shallow root configuration omission and explicit
capabilities inlining. This run supersedes the following retained feeds for
that checkpoint's source, not
the still-unexecuted platform or conformance cells.

Subsequent directory-mapping and domain work changed those package sources.
The feed above remains a historical consumer receipt; the final normalized
successor is identified in the current handoff section.

The preceding compiler/model-fix feed `artifacts\embedding\c734fe1dcd9d`
passed the same 25-case matrix with fingerprint
`f014f0b340d214efe774c3e4cc9a03ade8425d654e22ad4cf557ec088d1194c0`.
It is retained as historical evidence rather than relabeled as the new source.

The fresh feed at `artifacts\embedding\b2e1ee2ca80f` contains all eleven runtime
NuGets with the conditional-header, event and Git edge-case corrections.
Both net8.0 and net10.0 Windows x64 consumers passed all 25 named cases with
zero IL warnings and fresh native compilation. Ordinary and feature-masked
JIT controls both rejected native-only work before side effects.

The streamed Document case now supplies typed Unicode/map metadata, verifies
readonly Resource/Version IDs and the initial epoch of zero, and reads header
metadata before consuming body bytes. It still asserts one request, one
preparation, one publication and caller-owned streams. The current package/probe
source fingerprint matches the retained feed:
`f7694671d74cd442c900303dfca0e51f9dc25bc2608f84b6142941eb82ce4e24`.
The failed earlier probe at `21010504e699` is retained: it incorrectly expected
an initial epoch of one; the correction changed only the probe expectation.

This is development package evidence, not full conformance or qualification of
Linux/ARM64. The run removed its owned caches/scratch/work child and retained
package inventories, source/fixture hashes, native binaries and both controls.

### Retained escaped-identity all-runtime-package handoff

The updated all-eleven-package consumer completed on Windows x64 for both
net8.0 and net10.0. All eleven development packages were freshly packed at
`0.1.0-alpha-g`, including the parent's Core/Federation escaped-ID fixes.
Evidence is retained at `artifacts\embedding\a9f87c92d933`.

| Gate or case | net8.0 win-x64 | net10.0 win-x64 |
| --- | --- | --- |
| Native named cases | 25 passed; previous 24 retained | 25 passed; previous 24 retained |
| Escaped-ID metadata forms | 9 Group/Resource/Version reads | 9 Group/Resource/Version reads |
| Default/explicit Document forms | 6 reads, 90 exact bytes checked | 6 reads, 90 exact bytes checked |
| Malformed/double-decoded/separator targets | 6 rejected before dispatch | 6 rejected before dispatch |
| Escaped-identity source requests | 25 | 25 |
| Native publish gate | ILC executed, zero IL warnings | ILC executed, zero IL warnings |
| Normal and feature-masked JIT controls | Exit 2 before work | Exit 2 before work |

Version metadata reads preserve the literal wire XID
`/workspaces/group%3Aone/artifacts/item%40stable/versions/v%3A1`,
while returning decoded IDs `group:one`, `item@stable`, and `v:1`.
The ordinary-ID case and all prior storage/File/Git/OCI/ownership cases still
pass; no metadata rewriting or replacement endpoint implementation is used.
All **20 evidence-tool tests** passed.

The source manifests agree on digest
`abe22b350e5382f9090679a119d003b5e4fe4c4cb2a0fa86f35999009f9f164b`.
The run retained package, fixture, assembly, loaded native-module and binary
hashes, and removed its D:-hosted producer cache, consumer cache and scratch
directories. Docker was not used or restarted for this update. The separate
Linux embedding attempt remains infrastructure-blocked.

### First all-runtime-package handoff (retained)

The all-eleven-package run completed on **Windows x64, fixed local NTFS**, with
SDK 10.0.401. All eleven development package versions resolved to
`0.1.0-alpha-g` from the fresh feed. Evidence is retained at
`artifacts\embedding\c7b7b7f2fbf2`.

| Gate or operation | net8.0 win-x64 | net10.0 win-x64 |
| --- | --- | --- |
| Native named cases | 24 passed: original 15 + 9 | 24 passed: original 15 + 9 |
| Original HTTP/publication/Document checks | 12 requests, 4 publications, 75 checked bytes | 12 requests, 4 publications, 75 checked bytes |
| File-store reopen / adapter generations | 2 / 2 | 2 / 2 |
| File mapping / File OCI exact Documents | 18 / 18 bytes | 18 / 18 bytes |
| Independent SHA-256 Git pack | 794 bytes, 11 verified objects, 11-byte binary blob | 794 bytes, 11 verified objects, 11-byte binary blob |
| Exhaustive frozen OCI closures | 99 offline / 98 linked objects | 99 offline / 98 linked objects |
| Live HTTP federation | 8 source requests, 15-byte exact Document | 8 source requests, 15-byte exact Document |
| Native backend observations | 1 SQLite module, 0 Git modules | 1 SQLite module, 0 Git modules |
| Native publish warning gate | ILC executed; zero IL warnings | ILC executed; zero IL warnings |
| Normal and feature-masked JIT controls | Both exit 2 before work; positive JIT counts | Both exit 2 before work; positive JIT counts |

The native SQLite module was `e_sqlite3.dll` from
`SQLitePCLRaw.lib.e_sqlite3/2.1.12`. Its **actually loaded file** matched the
selected nupkg asset SHA-256
`b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e`
on both TFMs. No Git executable was invoked by the consumer, and no native Git
package asset or loaded module was accepted. Local-pack reads do not replace
separate Git network/interoperability evidence.

Both source manifests agree on digest
`b9863dc2df74967c6bf622ad5cdfd3a7218ae3d0f24eaff4704940cb26278c64`.
`fixture-inventory.json` retains 163 exact input-file records; each
`inventory-<TFM>.json` retains all eleven package hashes plus managed/native
asset hashes. `summary.json` retains native and managed-control executable
hashes. The main consumer still has no source ProjectReferences, TUnit or
OPC UA dependency, and uses framework JSON rather than the net8 TUnit/STJ10
combination.

All **19 evidence-tool tests** passed, including all-runtime manifest coverage,
preservation of the original 15 cases, required per-package operations,
independent fixture pins, native-backend restrictions and JIT-negative
controls. The final owned NuGet cache was removed; source, fixtures, concrete
store data, packages, native executables and exact reports remain as evidence.
Other native RIDs are still unexecuted, and every package remains `planned`.

#### Linux embedding attempt: infrastructure-blocked

On 2026-09-12 Docker Desktop again reported `linux x86_64`, but the isolated
embedding qualification could not start. Inspection of the existing native
tools image and pinned SDK/runtime-deps images failed with containerd content
blob I/O errors. A pull of the pinned SDK digest
`sha256:4ea6fe75dd36706bb6d8c3c293d4c4315840f5d76ea28ac97def77e3ec487fa5`
then failed writing containerd's `meta.db`: **read-only file system**.

No embedding container or volume was created, and no existing container,
volume or Docker service was restarted, pruned or removed by this attempt.
Logs are retained at `artifacts\embedding-linux\io-probe-06086d85`.
This is an infrastructure blocker before native execution, not a failed
consumer test or Linux qualification. The Windows results above are unchanged;
Linux fresh-package execution remains unverified until Docker storage is usable.
After the tools image ID was independently confirmed, a direct `docker run`
retry in context `desktop-linux`, using
`sha256:b9eae3236348dd47f716f94fe50d245d09b032c648c26ac872f0873968fa39b5`,
also failed with the same blob I/O error (exit 125). No container remained.
That execution-attempt evidence is retained at
`artifacts\embedding-linux\imagecheck-06086d85`.

### Initial six-package handoff evidence (retained)

The initial version of the commands above completed on Windows x64 with SDK
10.0.401. Its fresh feed resolved six development packages to
`0.1.0-alpha-g`. That completed 15-case run is
retained at `artifacts\embedding\0ce3b14e0f4b`.

| Gate | net8.0 win-x64 | net10.0 win-x64 |
| --- | --- | --- |
| Actual Native AOT execution | 15 named cases passed | 15 named cases passed |
| Observed HTTP requests | 12 | 12 |
| Main application-store publications | 4 | 4 |
| Exact Document bytes checked across copies | 75 | 75 |
| Native JIT count, entry/completion | 0 / 0 | 0 / 0 |
| Ordinary managed negative control | Exit 2; rejected before work | Exit 2; rejected before work |
| Feature-masked managed negative control | Both flags false; JIT count 9 / 15; exit 2 | Both flags false; JIT count 8 / 14; exit 2 |
| Native publish IL-warning gate | Zero IL warnings; ILC executed | Zero IL warnings; ILC executed |

The evidence-tool suite passed **13 tests**, including independently red-capable
checks for incomplete/renamed/failed case sets, startup-only reports, JIT
execution, wrong architectures, release claims, source ProjectReferences,
OPC dependencies, changed restored binaries and escaping asset paths.

`summary.json` records source and executable hashes; `inventory-net8.0.json`
and `inventory-net10.0.json` record every selected nupkg hash and retained
runtime-assembly hash. `source-before.json` and `source-after-pack.json` agree
on source digest
`c8eb92b70fd26315d3b5bc39179e77168996c5c7fd685a596825470410fcbca8`.
The owned NuGet cache was removed after the successful run. Packages, source,
assembly-reference evidence, executables, request lists and reports remain.

Only **Windows x64** was executed. The accepted RID parameters for Windows
ARM64 and Linux x64/ARM64 are not evidence that those platforms passed.

## Remaining migration and qualification work

There is no automatic UA migration, old-type compatibility layer or mandatory
private workflow protocol. Native UA identity/session/projection, native ID
mapping, existing producer semantics and any proprietary extension adapter
remain application work.

Initialize/commit/reopen is not process-crash or power-loss durability
qualification. Frozen local Git/OCI reads are not new network-acquisition or
producer-publication qualification. This probe does not qualify arbitrary
authoritative dispatch, live HTTP federation snapshot consistency,
complete protocol conformance, production
authentication/TLS deployment or unexecuted native platforms.
See [server](server.md), [storage](storage.md),
[HTTP client](http-client.md) and [HTTP federation](http-federation.md) for their
current guarantees and explicit limitations.
