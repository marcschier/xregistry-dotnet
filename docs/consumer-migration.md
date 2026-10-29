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
| Generic operation dispatch | `RegistryEngine.ExecuteAsync(RegistryOperation, RegistryOperationContext, CancellationToken)` in `XRegistry.Server` | The engine owns model, lifecycle, versions, validation and atomic change preparation. |
| HTTP client | `XRegistry.Client.XRegistryHttpClient` | One explicit HTTP operation, unchanged status/headers and bounded metadata/Document consumption. |
| HTTP serving | `XRegistry.AspNetCore.MapXRegistry` | Maps a concrete `RegistryEngine`; it does not start or secure a host. |
| Transactional persistence | Application-owned `IRegistryPersistence`, or `LocalRegistryPersistence` over `LocalFileStore` | Storage generations are not entity epochs or remote Registry revisions. |
| Schema-source validation | `XRegistry.Validation` | Supported schema formats and policies, not OPC UA NodeSets/sessions/projection. |
| API Registry and operation references | `XRegistry.Models` (`RegistryModelKind.Api`) and `XRegistry.Validation` (`ApiOperationReference`, `ApiOperationDocumentResolver`) | The model describes versioned synchronous API definitions. The parser accepts operation, service, interface, GraphQL and JSON Pointer selectors; document resolution supports OpenAPI 3.0/3.1 and Swagger 2.0 JSON operations by `operationId` or JSON Pointer, but does not validate the full API document or resolve GraphQL documents. |
| Federation read acquisition | `IFederationReadSource` / `HttpFederationReadSource` | A read-source seam, not a write dispatcher or immutable live snapshot. |
| OPC UA access and federation reads | `XRegistry.Bindings.OpcUa` | Uses OPC UA Client Stack 2.0.1 to discover endpoints, apply caller policy, open a Session, remap the portable root through `NamespaceArray`, and verify Registry type and Core version. `OpcUaRegistryReadSource` maps Registry, Group, Resource, Meta, Version and collection views, and reads local FileType Documents or returns external-reference descriptors. `OpcUaRegistryWriter` updates Properties and calls Methods, including Resource Document replacement. Callers supply Group, Resource and ResourceVersions ObjectType mappings. Fake-Session tests cover reader, source and writer behavior; in-process server tests cover Session establishment, XREG-Events subscription management, writes, methods and document transfer. Two-server tests cover trusted external Resource and Version resolution. Core API views and NodeSet projection remain unimplemented. Endpoint security policy is caller-selected. Generic packages remain free of OPC UA types. |

Legacy prepare/replay/incarnation/generation extensions are not standard
xRegistry HTTP. The in-process persistence/response preparation methods do not
imply proprietary wire endpoints.

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

The latest retained development run is `artifacts\embedding\da6fb57037d0`,
with source SHA-256
`2d38796426466d95abca8a561acb4878f60e3ed14a9a8320d0059196b56c57e4`. Its
net10.0/win-x64 NativeAOT consumer passed all 27 cases with zero JIT
compilations; both managed controls produced the expected JIT-rejection
results. The report is not release qualification. The OPC UA package was not
included in that run and has no NativeAOT qualification. The focused OPC UA
suite passed 464 cases across net8.0 and net10.0. The full Release suite passed
9,912 managed cases, with 269 tooling tests and pinned-source/model checks
passing.

This evidence does not establish Linux/ARM64 package qualification, power-loss
durability, arbitrary authoritative dispatch, immutable live HTTP federation,
complete protocol conformance, or production TLS/authentication. The OPC UA
binding has live-server coverage for its tested Session, operation, event
Subscription, and two-server external-target paths, but has no NativeAOT
qualification. Core API views and NodeSet projection remain unimplemented;
endpoint-security policy must be selected by the host. Proprietary extension
adapters remain application-owned. Outstanding qualification is in the
[roadmap](roadmap.md); predecessor feeds and infrastructure-blocked attempts are
in the [changelog](changelog.md#package-consumer-checkpoints).
