# Consumer embedding and role-oriented migration

For a new application, start with
[Integrate xRegistry into ASP.NET Core](aspnetcore-integration.md). This
document focuses on deeper role boundaries and migration decisions.

This guide describes the current standalone interfaces and the executable,
package-reference-only consumer. It is not a drop-in binary compatibility
promise, an OPC UA migration layer, release qualification, or a claim of
complete xRegistry conformance.

## Roles, not old type substitutions

| Consumer role | Current standalone choice | Important distinction |
| --- | --- | --- |
| Endpoint/value/model/path/codec concerns | `XRegistry`: `RegistryJson`, `RegistryModel`, `RegistryId`, `RegistryPath`, `RegistryHeaderEncoding`, `RegistryHeaderMetadata`; `XRegistry.Models` for packaged model sources | A domain Endpoint Definition is not an HTTP route. IDs follow the standalone Core grammar; native identifiers require explicit mapping. |
| Media Endpoint definitions | `XRegistry.Models.EndpointDefinition.MaterializeMedia` and `RegistryDomainRules.ValidateMediaEndpointMetadata` | Validates the working-draft producer/consumer and protocol-option rules. Codec identifiers remain open and case-sensitive; the validator rejects recognized codecs assigned to the other media kind. The draft defines no codec/format compatibility matrix. |
| WoT Version selection | `XRegistry.Models.WotVersionResolver.Resolve` and `SelectVersionId` | Applies typed floors, pins, sticky defaults and prerelease policy to caller-captured Resource Versions. `SelectVersionId` returns a selection without asserting byte provenance; `Resolve` requires the caller's captured digest and origin. Neither method parses reference URIs or acquires documents. |
| WoT Registry acquisition | `XRegistry.Federation.WotRegistryResolver.ResolveAsync` | Resolves a parsed, nonlocal reference within one authorized source and Group Resource collection. Requires the source's captured SemVer WoT model, rejects competing Resources, and returns exact bytes with their computed SHA-256 digest and retained Registry origin. |
| WoT containing-revision resolution | `XRegistry.Federation.WotRegistryResolver.ResolveLocalAsync` | Resolves a parsed local reference against an already acquired `WotResolvedDocument`. Uses its retained bytes and kind, applies local floors to the authored version, and cannot select another Registry Version. |
| WoT JSON Pointer selection | `XRegistry.Federation.WotRegistryResolver.SelectFragmentAsync` | Applies an RFC 6901 URI-fragment pointer to the acquired document after Version or local-revision selection. Returns the selected JSON value with the containing document's full-byte digest and origin. |
| WoT incremental assignment calculation | `XRegistry.Models.WotIncrementalVersionAllocator.Assign` | Calculates a new integer revision from explicit durable high-water state. It does not publish, persist a counter, activate an incremental endpoint or implement compatibility-line selection. |
| Generic operation dispatch | `RegistryEngine.ExecuteAsync(RegistryOperation, RegistryOperationContext, CancellationToken)` in `XRegistry.Server` | The engine owns model, lifecycle, versions, validation and atomic change preparation. |
| HTTP client | `XRegistry.Client.XRegistryHttpClient` | One explicit HTTP operation, unchanged status/headers and bounded metadata/Document consumption. |
| HTTP serving | `XRegistry.AspNetCore.MapXRegistry` | Maps a concrete `RegistryEngine`; it does not start or secure a host. |
| Transactional persistence | Application-owned `IRegistryPersistence`, or `LocalRegistryPersistence` over `LocalFileStore` | Storage generations are not entity epochs or remote Registry revisions. |
| Schema-source validation | `XRegistry.Validation` | Supported schema formats and policies, not OPC UA NodeSets/sessions/projection. |
| API Registry and operation references | `XRegistry.Models` (`RegistryModelKind.Api`) and `XRegistry.Validation` (`ApiOperationReference`, `ApiOperationDocumentResolver`) | The model describes versioned API definitions. The parser accepts operation, service, interface, GraphQL and JSON Pointer selectors. Document resolution supports OpenAPI 3.0/3.1 and Swagger 2.0 JSON operations by `operationId` or JSON Pointer, and OpenRPC 1.4 JSON methods by exact `name`. These selectors do not validate the full API document; other language resolvers remain unsupported. |
| Federation read acquisition | `IFederationReadSource` / `HttpFederationReadSource` | A read-source seam, not a write dispatcher or immutable live snapshot. |
| OPC UA access and federation reads | `XRegistry.Bindings.OpcUa` | Uses OPC UA Client Stack 2.0.1 to discover endpoints, apply caller policy, open a Session, remap the portable root through `NamespaceArray`, and verify Registry type and Core version. `OpcUaRegistryReadSource` maps Registry, Group, Resource, Meta, Version and collection views, and reads local FileType Documents or returns external-reference descriptors. `OpcUaRegistryWriter` updates Properties and calls Methods, including Resource Document replacement. Callers supply Group, Resource and ResourceVersions ObjectType mappings. Fake-Session tests cover reader, source and writer behavior; in-process server tests cover Session establishment, XREG-Events subscription management, writes, methods and document transfer. Two-server tests cover trusted external Resource and Version resolution. Core API views and NodeSet projection remain unimplemented. Endpoint security policy is caller-selected. Generic packages remain free of OPC UA types. |

Legacy prepare/replay/incarnation/generation extensions are not standard
xRegistry HTTP. The in-process persistence/response preparation methods do not
imply proprietary wire endpoints.

`ApiOperationDocumentResolver.Capabilities` advertises finite parser and retrieval
budgets: 1 MiB per document, 4 MiB total, 100,000 parsed JSON values, JSON nesting
and reference-chain limits of 64, at most 32 external documents, and a 30-second
operation deadline. Dependencies are acquired only through the caller's callback;
the callback owns authorization, redirects and credential policy. Returning null
reports an unavailable dependency; throwing `UnauthorizedAccessException`
reports `Denied`. A required dependency failure prevents selection even if an
earlier method matched. Caller cancellation propagates; deadline expiry reports
`Indeterminate`. Resolution never invokes an API.

WoT minimum-version floors use SemVer without build metadata and fit the
128-character Core id limit. Generated `0.0.0-N` floors are forbidden, with
positive `N` as defined by the draft's assignment rule; `0.0.0-0` is an
authored prerelease and remains a valid floor. A type link uses the greater
of its explicit floor and the TD's model version. Several type links require
every explicit floor only when that model version is present. The selection
helper receives those parsed values; it does not extract links from JSON-LD.

`WotRegistryResolver` reads Resource Meta and default-Version metadata to match
`wotid`; a native document view does not put Version attributes on the Resource
itself. It requires complete lookup and Version collections, keeps every read in
the selected source context, and shares one finite `FederationReadBudget`.
Unpinned and Version-pinned requests acquire document bytes only for the selected
Version. A digest pin hashes candidate bytes and selects the highest matching
Version, ignoring advisory digest fields. Denied, unavailable or inconsistent
reads fail without trying another Version or source.

`ResolveLocalAsync` retains the containing revision even after its source is
disposed. It checks the stored document's identity, parses its exact bytes with
duplicate-name rejection, and reads `version.model` for a TM floor or
`version.instance` for a TD floor. Caller-supplied revision claims cannot replace
those fields. A failed floor does not reopen the source to select a different
Version. Parsing consumes cumulative work but does not recharge input bytes,
objects or requests already consumed by acquisition. Object, JSON-depth, work
and output limits remain finite; cancellation propagates.

`SelectFragmentAsync` accepts a leading `#`, with `#` alone selecting the root.
It decodes percent-encoded UTF-8 once before decoding JSON Pointer tokens.
Object names are case-sensitive; array indices use canonical nonnegative decimal
syntax. Explicit JSON null is a successful selection; a missing target fails
without selecting another Version. Malformed fragments, duplicate JSON names
and document-identity mismatches fail before a value is returned. The input
fragment consumes bytes and an object from the shared budget; the retained
document is parsed again with cumulative work and depth limits but is not
recharged as acquired input. The output limit applies to the selected value's
encoded JSON bytes. Fragment selection does not change the full-document digest
or require a live source.

The caller still processes URI references, document bases and JSON-LD contexts
to establish locality, then names the containing WoT identity in a local
request. `ResolveAsync` rejects local requests; `ResolveLocalAsync` rejects
Registry pins. Resource aliases must first be resolved to an authorized source,
and external document descriptors are not dereferenced. Incremental-profile
selection and publication closure remain unimplemented. Acquisition, local
resolution and fragment cases passed managed execution on both frameworks and
native execution in the complete Federation suite on .NET 10/Windows x64 at
`6d143c4`. Other native cells remain unqualified; the package-consumer fixture
does not yet exercise these methods.
See [native evidence](native-aot.md#current-evidence-and-uncovered-cells).

`WotIncrementalVersionAllocator.Assign` calculates the proposed D3 allocation
from a supplied watermark. A canonical authored positive integer must exceed
that watermark; absent or noncanonical authored values generate its successor.
The helper uses arbitrary-precision arithmetic within Core's 128-character id
limit and rejects sequence exhaustion, invalid captured counters and supplied
ids that disagree with the assignment. It never reconstructs deleted history
from retained Versions. A publication owner must commit the returned watermark
atomically with the bytes, fixed metadata and Core state, retaining the counter
after Resource deletion. That server integration and its durable/concurrent
execution evidence are not implemented by this pure function; existing built-in
WoT models and server writes remain SemVer.

## Choose the real seam

The supplied consumer uses an application-owned transactional record store:

```text
application record store
    -> ApplicationRegistryPersistence : IRegistryPersistence
    -> RegistryEngine + explicit IRegistryAuthorizationPolicy
    -> real Kestrel + MapXRegistry
    -> public XRegistryHttpClient
```

The adapter owns concurrency and durability and treats engine records as opaque.
It must preserve model envelopes, event/outbox state, correlation reservations,
and Documents.

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

Point reads must not copy the entire Registry. Open Document streams outlive
snapshot disposal. `RegistryMutation` distinguishes preserving, replacing and
removing a Document. Input streams remain caller-owned and are consumed during
preparation. A candidate publishes all records or none, rechecks the expected
generation, and does not report cancellation as rollback after publication.
Unknown commit outcomes are not retry opportunities.

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
The engine does not dispose application persistence, policy services, or caller
Document streams. Results own metadata and Document bytes independently.

An existing authoritative endpoint whose operation/lifecycle semantics cannot
store the local engine's opaque state is not made pluggable by implementing
`IRegistryEngine`. It needs an application-specific HTTP adapter/forwarding
layer, or a read-source adapter for read-only acquisition. This integration
work is tracked in the [roadmap](roadmap.md#integration-work).

## Executable package consumer

`eng\test-embedding-packages.ps1` copies `tests\PackageSmoke\Embedding*` into
isolated consumers that reference the thirteen generic runtime NuGets:
Core, Client, Server, AspNetCore, Models, Validation, Storage.File, Federation,
Publication, Publication.Server, and the File/Git/OCI bindings. The OPC UA
package is excluded from this NativeAOT consumer gate pending binding-specific
AOT qualification; its behavior is tested through `XRegistry.OpcUa.Tests`. The
consumers have no source ProjectReferences, TUnit, or OPC UA dependency.

The application persistence is process-local copy-on-write storage bounded to
256 records/1 MiB, 64 KiB per Document, and 64 KiB per metadata value. It is not
crash durability or multi-process coordination. The HTTP fixture uses literal
loopback, generated bearer credentials, ASP.NET authentication, and an explicit
Core policy; identity headers are not trusted.

The 27 named cases cross public package seams and cover:

- authentication/authorization, exact HTTP dispatch, model-aware metadata,
  caller-owned streams, atomic publication, cancellation and lease lifetimes;
- real SQLite initialization/reopen and Document preservation;
- authorized File reads and File-backed OCI layout selection;
- verified SHA-256 Git pack/tree/blob reads with no Git executable;
- exhaustive 99/98-object OCI closures and external descriptors;
- live HTTP federation, escaped identities, and pre-dispatch malformed-ID
  rejection;
- Publication manifest integrity and retained channel replay;
- actual runtime module inventory: native SQLite is allowed; a native Git
  backend is not; and
- ordinary and feature-masked managed JIT negative controls.

`EmbeddingFixtureInputs.json` pins independent mapping, OCI, Git, and license
inputs by length and SHA-256. Expected OCI object counts and Document bytes are
not obtained from the package producer. The live federation fixture uses the
actual bound loopback Registry root and retains wire XIDs while comparing
decoded ordinal identities.

## Run the development package gate

```powershell
python -m unittest discover -s tests\Tooling -p "test_embedding*.py" -v
pwsh -NoProfile -File eng\test-embedding-packages.ps1 -RuntimeIdentifier win-x64
```

The default executes net8.0 and net10.0. `-Framework` narrows a diagnostic run.
The script accepts Windows/Linux x64/ARM64 but rejects a RID that does not match
the actual host/process architecture.

`-WorkRoot <absolute-existing-directory>` selects the filesystem for SQLite
stores and controlled native File/OCI copies. The driver creates and removes
only a unique child; caller sentinels, siblings, and the supplied root remain
owned by the caller. Without `-WorkRoot`, the same policy uses the run's
artifact directory. Selecting a path does not bypass the File store's NTFS/ext4
policy.

`-ArtifactsRoot <absolute-existing-directory>` places the unique evidence run,
fresh feed, build outputs and run-owned caches below that directory instead of
`artifacts\embedding`. The gate preserves the supplied parent. This also keeps
temporary storage on a caller-selected volume without reusing another run's
feed or cache.

Each run:

1. Fingerprints selected package source/configuration and probe inputs, then
   packs all thirteen packages into a fresh local feed with isolated outputs.
2. Rejects source changes during packing, resolves literal package versions,
   and restricts `XRegistry*` to the fresh feed using package-source mapping.
3. Performs normal lockfile-free NuGet restore into an isolated cache and
   publishes each consumer, requiring ILC execution and zero `warning IL...`.
4. Matches restored runtime assemblies to nupkg entries and executes all 27
   behavioral cases.
5. Runs ordinary and feature-masked managed controls; each must exit before
   side effects with a positive JIT compilation count.

The native guard independently checks both dynamic-code flags and
`JitInfo.GetCompiledMethodCount()`. Evidence includes package/source/fixture
hashes, inspected assets, module observations, generated consumer inputs,
native and managed binary hashes, publish logs, requests, and reports. Only
explicitly owned caches and work children are removed.

## Current evidence and limits

All eight generic package-consumer cells in
[native run `41024778707`](https://github.com/marcschier/xregistry-dotnet/actions/runs/41024778707)
passed at source commit `ecd2a3bb92e5c390590b121ae96b598671361305`:
.NET 8/.NET 10 on Windows/Linux x64 and ARM64. Each cell executed all 27
native cases and both managed negative controls. The run retains source,
package, binary and execution evidence in its `native-all-packages-*`
artifacts. This is development qualification of the consumer fixture, not
full native test-suite coverage or release qualification.

The development runs at `D:\temp\97aa2a5176f5` (net8.0/win-x64) and
`D:\temp\cac5584c2f5c` (net10.0/win-x64) each passed all 27 native cases,
with zero JIT compilations and no IL warnings. Ordinary and feature-masked
managed controls produced the expected JIT-rejection results. The net8.0 run
records source SHA-256
`624cfa48c114fba88cb745b5b3c36550c9902441479ca4009693e129832e94ba`;
each run retains its own source and package hashes. These are development
receipts, not release qualification. The OPC UA package is excluded and has
no NativeAOT qualification.

The .NET 8 Federation and Publication packages use framework-provided
System.Text.Json. Their previous unnecessary dependency on the 10.x package
made the ASP.NET Core 8 JSON-option initializer emit IL2026/IL3050 during native
compilation. Removing those dependencies resolves the package-consumer warning
gate without suppressing warnings or altering the HTTP host. A test host or
application that independently selects packaged System.Text.Json 10.x still
requires separate qualification.

This evidence does not establish power-loss durability, arbitrary authoritative
dispatch, immutable live HTTP federation, complete protocol conformance, or
production TLS/authentication. The OPC UA
binding has live-server coverage for its tested Session, operation, event
Subscription, and two-server external-target paths, but has no NativeAOT
qualification. Core API views and NodeSet projection remain unimplemented;
endpoint-security policy must be selected by the host. Proprietary extension
adapters remain application-owned. Outstanding qualification is in the
[roadmap](roadmap.md); predecessor feeds and infrastructure-blocked attempts are
in the [changelog](changelog.md#package-consumer-checkpoints).
