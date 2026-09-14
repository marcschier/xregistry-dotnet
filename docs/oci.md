# Native OCI snapshots

**Historical Linux platform evidence:** the original 185 OCI cases passed in a
.NET 10 Linux x64 Native AOT executable, without an installed .NET runtime or
external network access. This includes the independent Python producer oracle.
The executable ran as a non-root user with a read-only container root and
isolated writable test directories. The subsequent escaped-XID changes have
Windows evidence below, not a new Linux qualification. ARM64 and live
remote-registry qualification remain open.

`XRegistry.Bindings.Oci` implements the **unreleased version-1 native OCI
snapshot profile** in the active SPEC-005 successor,
`tests\Conformance\Corrections\SPEC-005\workingdrafts\bindings\oci.md`
(SHA-256 `6743373b66bff3402fb5f8cd735d35a77da17d7acc18eb3f0b954ed7b80ec0ad`).
The original source remains immutable under `tests\Conformance\Sources`.
It targets `net8.0`
and `net10.0`, with trimming/AOT analysis enabled. It does not run Docker,
ORAS, Git, Python, containers, archive extraction, or domain-document code.
Python is an independent **test oracle only**.

The package provides a native read-only resolver and a separate snapshot
producer/publication path. It is not an xRegistry HTTP server. It does not
reinterpret directory-mapping records as OCI metadata or manufacture URLs for
an HTTP facade.

## Public integration contract

| Interface | Contract |
| --- | --- |
| `OciSnapshot.OpenAsync(IOciObjectReader, reference, budget, cancellationToken)` | Resolve one explicit tag/digest once, verify and pin the root, and compile captured model material without external acquisition. |
| `OciSnapshot.OpenLayoutAsync(IDocumentTreeReader, reference, budget, cancellationToken)` | Use the caller's safe root-relative tree. Read `oci-layout` and bounded `index.json`; the latter is an entrypoint, not the Registry root. File may compose this directly. OCI never references File. |
| `IOciObjectReader` / `OciObjectResponse` | Distinct manifest and blob acquisition, fixed `NativeRegistryContext`, and transferred exact-stream ownership. Null means absent; other failures are explicit. The caller owns the reader. |
| `OciSnapshot.ReadAsync(FederationReadRequest)` | Registry/Group/Resource/Meta/Version metadata, complete collections, unique literal label selection, model, capabilities, and embedded documents. Only `DocumentView` is supported. |
| `OciReadResult` | `Target` retains requested identity; `SelectedXid` retains selected/resolved identity; `Context` retains the snapshot pin. Metadata is in `Value`, selected by `JsonPointer`. Document bytes are in `Document`. |
| `OciSnapshot.ReadExternalDocumentDescriptorAsync(target)` | Explicitly return an **unfetched** external descriptor with resolved Version and snapshot context. This is not successful byte retrieval. |
| `OciSnapshot.ValidateAsync()` | Exhaustive required-closure and semantic validation, separate from selective reads. Returns distinct object/index/manifest/config/document counts. |
| `IFederationReadSource` implemented by `OciSnapshot` | Adapt materialized Core views into shared complete collection/entity envelopes, and external Document modes into explicit unfetched `{"kind":"external","uri":"..."}` descriptors. Navigation is rebased into the returned envelope; raw configs are never exposed as API metadata. |
| `OciRepository.Parse` / `ParseProfile` | Validate the repository locator, explicit reference and advertisement parameters; build exact Distribution manifest/blob URIs. |
| `OciDistributionClient` | Origin-policy-controlled HTTPS reader and separately invoked `IOciPublisher`. Credentials come only from an explicit provider. |
| `OciSnapshotInput` / `OciSnapshotWriter.CreateAsync` | Capture immutable portable records and exact documents, construct a deterministic native graph, and validate it through the same reader before making it publishable. |
| `OciSnapshotPackage` / `IOciPublisher` | Owned immutable objects and bottom-up publication; commit the selected reference last. `CreateLayoutEntryPoint` supplies a bounded single-root layout entrypoint for a safe File host. |

### Local layout composition

```csharp
// The host supplies and owns a safe FileDocumentTreeReader or another exact tree.
IDocumentTreeReader tree = selectedTree;
await using var snapshot = await OciSnapshot.OpenLayoutAsync(
    tree, "release", new FederationReadBudget(), cancellationToken);

var result = await snapshot.ReadAsync(
    new FederationReadRequest(
        FederationOperation.Document, "/dirs/main/files/sample"),
    cancellationToken);

using var bytes = result.Document!.OpenRead();
// Each OpenRead() has an independent position and lifetime.
```

Layout names are only `oci-layout`, `index.json`, and
`blobs/sha256/<validated lowercase hex>`. IDs never become native paths.
Containment, symlink/reparse-point protection, stable-handle reads and observed
filesystem mutation belong to the supplied tree reader. No filesystem policy is
duplicated in OCI.

An omitted layout reference is a convenience for exactly one distinct eligible
advertised root. Native advertisements always require an explicit reference.
Duplicate tags are ambiguous even if their digests match. A direct digest may
select a stored root absent from the entrypoint. Other roots and unrelated blobs
do not enter the selected closure.

### HTTPS Distribution

```csharp
var repository = OciRepository.Parse("oci://registry.example.org/team/catalog");
var policy = new RegistryHttpConnectionPolicy(repository.Origin);
using var transport = new OciDistributionClient(repository, policy,
    new OciDistributionOptions
    {
        AuthorizationProvider = repositoryCredentials,
        RequestTimeout = TimeSpan.FromSeconds(30)
    });
await using var snapshot = await OciSnapshot.OpenAsync(
    transport, "release", cancellationToken: cancellationToken);
```

The owned-client constructor reuses `XRegistry.Client`'s DNS and
actual-connection policy. The alternative caller-owned `HttpClient` constructor
is an explicit transport trust seam: its handler must enforce that policy and
disable redirects, decompression, ambient credentials, cookies and proxies.
Supplying a policy object does not retrofit an arbitrary handler. Default
authorization/cookie/proxy-authorization/Host headers are rejected, including
ones added after construction.

Indexes and manifests use `/v2/<repository>/manifests/<reference-or-digest>`,
advertising both standard OCI media types. Configs, documents and placeholders
use `/v2/<repository>/blobs/<digest>`. Exact bytes, descriptor size and SHA-256
are verified before parsing or returning data. SHA-256 and SHA-512
`Docker-Content-Digest` evidence is checked independently; an absent header does
not replace or prevent computing the SHA-256 pin.

Tags are never re-resolved after selection. A missing pinned descendant cannot
trigger a retry at a newer root. Redirects are not followed and produce
`policy_denied`; Bearer token negotiation produces `unsupported_operation`.
Provide repository-authorized credentials rather than relying on implicit token
realm acquisition. There is no plaintext fallback. Upload-session locations
must remain inside the explicitly authorized HTTPS repository before credentials
or data are sent.

## Native data semantics

The reader checks schema/media/artifact types, annotation versions and roles,
ordered two/three-control entity indexes, native descriptor edges, exact range
partitions, sorted full-XID keys, duplicate entries, node/edge agreement and
finite traversal. Required graph objects cannot use `urls`, `data`, `platform`
or `subject`. Unknown profile annotations and required kinds are not ignored.
Unknown non-profile string annotations, including empty keys/values permitted
by OCI's annotation map rules, are ignored as non-authoritative metadata.
That does not relax the version-1 `io.xregistry.oci.*` allow-list, duplicate-key
rejection, or any canonical routing rule.

Every index, including the layout entrypoint, is limited independently to **256
descriptors and 1,048,576 exact UTF-8 bytes**. Whitespace and final newlines
count. Shards may be smaller than these limits; collections are not capped at
256 entities. Selective reads validate only their consumed path and do not
download unrelated Version documents. Successful lookup is not a claim that
unvisited closure is valid.

The original `modelsource`, full `model`, and include-resolved
`resolvedmodelsource` result field are retained separately. The portable config
uses the profile's `modelresolved` spelling. The real `RegistryModel` compiler
interprets the captured resolved source, preserving `ximportresources` type
identity; no runtime model types or substitute model compiler are generated.
Original include URLs are provenance, not consumption instructions. Declared
empty and imported collections are retained.

The frozen independent fixture's full model declares Meta `xref` as local
`xid`, whereas Core's general model declares `url`. Validation accepts that
specific native narrowing when comparing effective models, but does not rewrite
the captured full model. Alias type identity comes from the compiled original
import graph, never structural schema equality.

Metadata results are Core document views with actual response-local `#` JSON
Pointers. A standalone Meta read returns its owning Resource and Versions in
`Value`, with `JsonPointer == "/meta"`; `defaultversionurl` therefore names an
existing target in the returned document. Resource metadata is not a projection
of default Version attributes. Nested extension data named `self` is preserved.

Core validation's normalized, owned metadata is retained separately from exact
captured configs. Document views use UTC timestamps, completed-entity defaults,
and active conditional attributes from that shared validation result. Owning
Group `equals` constraints see the Group's normalized defaults as well.
Neither validation nor materialization rewrites content-addressed config bytes,
their original XID spelling, or the selected root.

Complete Version-set validation compares the normalized values of statically
defined `matchversions` attributes, including nested object attributes.
Timestamp equality compares instants without truncating fractional seconds;
`.120000000Z` and `.12Z` agree, while `.120000001Z` does not. Timestamp-looking
ordinary strings remain ordinal strings, and numeric matching uses Core's exact
`RegistryNumber` semantics. Core forbids `matchversions` inside conditional
branches, arrays, maps or wildcard definitions; OCI does not invent a second
conditional metadata engine.

Native storage/document-mode restrictions are checked again after defaults
are applied. A model default cannot introduce a forbidden navigation/document
field or an external locator on an embedded Version. Normalized document media
types are used consistently by native reads, external descriptors and producer
context checks; absent content type without a model default still has the
profile's existing `application/octet-stream` behavior.

Aliases retain source identity and unexpanded Meta `xref`, even when dangling
or targeting another alias. Alias Versions metadata and enumeration report
`unsupported_operation`/`cannot_doc_xref`. Document reads follow at most one
same-Registry, same-Resource-model-type hop; dangling and second-hop targets are
`not_found`. Label selection uses default-Version labels for Resources and
one-hop aliases, checks all necessary shards, and compares literal values
case-insensitively without wildcards or Unicode normalization.

`embedded` documents retain exact opaque bytes, domain content type, and
optional original `base`/`origin`. These URIs never become the OCI acquisition
context. Missing content type returns `application/octet-stream` without
rewriting metadata. A zero-byte document is present content, not an unused
layer. The OCI unused layer is exactly `{}` (two bytes), is itself verified in
full closure validation, and is never returned as domain content.
`metadata-only` agrees with `hasdocument: false` and cannot return a document.
`external` agrees with an absolute credential-free Core document URL and is
allowed only in a linked snapshot. Native `OciSnapshot.ReadAsync` document reads
return `unavailable` for external content; the explicit native descriptor method
retains its `mode`/`url`/`contenttype` shape.

Document-form names belong to the Document plane only when the Resource model
has `hasdocument: true`. On a metadata-only Resource, model-admitted attributes
named its singular, `<RESOURCE>base64` or `<RESOURCE>url` remain ordinary
metadata. Their declared types, defaults, nested values and captured nulls are
preserved; no base64 decoding, URL acquisition or external descriptor is inferred
from the spelling. Effective Core defaults still take precedence over a missing
or null value when the model defines a default. Actual document-capable
Versions retain the detached-field and external-locator restrictions.
This Core-aligned field-plane clarification is implemented; the OCI
prose/oracle successor to SPEC-005 remains a parent-owned capture, separate from
Mapping's SPEC-014 correction.

The `IFederationReadSource` adapter deliberately returns
`FederationReadResult.FromExternalDocument` with the shared
`{"kind":"external","uri":"..."}` shape instead. It resolves and validates the
selected Version under the existing operation guard, preserves the resolved
XID and snapshot context, and does not fetch external bytes or the unused
placeholder. OCI external mode already requires an absolute locator and forbids
`document.base`, so no storage-derived base is invented. This is mode dispatch,
not a catch/retry fallback for `Unavailable`; actual acquisition failures still
propagate unchanged.

An offline-complete snapshot contains every required internal object and every
declared embedded document. It does not recursively capture catalog
advertisements, original include URLs, descriptive links or domain dependencies.
No claim of publisher authenticity is inferred from a digest.

### Escaped Core XIDs and routing-key scope

XIDs are URI paths, while singular IDs and collection-map keys are decoded
Core identifiers. OCI now delegates component validation to
`RegistryId.ParseEscaped`: one strict decoding pass, the existing 384 encoded /
128 decoded component bounds, and no encoded separators, invalid Unicode or
decoded percent character. Original request `Target` is retained; returned
`SelectedXid` and metadata retain the selected stored entity's spelling.
Metadata map keys and response-local JSON Pointers use decoded IDs.

Containment, descriptor/config correspondence and model identity compare
decoded ordinal identity. Sibling uniqueness applies Core's case-insensitive
comparison to decoded IDs. **Stored index ordering, range
bounds, exact bytes and digest verification remain serialized-byte operations.**
Already stored objects are never rewritten to normalize an incoming selector.

New producer output canonicalizes known Core `entity.xid` and Meta `xref`
fields and routing keys: ASCII unreserved characters remain literal; other
allowed ID characters use uppercase percent escapes (`:` -> `%3A`, `@` ->
`%40`). Under the SPEC-005 unreleased-draft correction, this URI spelling is
required for internal graph identity annotations and finite range boundaries.
Portable config XIDs may retain URI-equivalent spellings. Arbitrary extension strings,
domain URLs, document base/origin and document bytes are not rewritten.

Selective lookup normalizes the selector once and follows exactly one interval
at each level. It does not enumerate the Registry or probe alternate spellings.
Consumed noncanonical graph annotations are `invalid_package`, not a cue to
retry another representation or rewrite an existing content-addressed object.

The original frozen draft specified bytewise ordering without a canonical URI
spelling. Arbitrarily partially escaped stored
keys can sort into intervals different from every normal selector spelling.
For example, `/items/%61%3Aone` sorts before the boundary `/items/a`, while its
URI-equivalent selectors `/items/a:one` and `/items/a%3Aone` sort after it.
SPEC-005 closes that implementability gap without changing serialized-byte
ordering or the 256-descriptor/1-MiB bounds. Earlier noncanonical draft layouts
require explicit migration or regeneration and new propagated digests; they
are not silently treated as conforming. Original baseline evidence is retained.
A selective result still validates only its consumed path, not every unvisited
object; full closure/conformance claims require `ValidateAsync`.

## Producer and publication

```csharp
// These are already coherently captured portable OCI records and exact bytes.
var input = new OciSnapshotInput(capturedRecords, capturedDocuments);
var package = await OciSnapshotWriter.CreateAsync(input,
    new OciWriteOptions(), cancellationToken);

// A separate, explicitly authorized producer operation, not a federation write.
await package.PublishAsync(publisher, "release",
    cancellationToken: cancellationToken);
```

Records follow the frozen `oci-record.schema.json`: one Registry config with
`snapshot`, `modelresolved`, and its model/source/capabilities metadata; separate
Group, Resource, Meta and Version configs; and Version `document.mode`. The
document map has exactly the embedded Version XIDs. Missing parents, Meta,
defaults, documents, extra document mappings, contradictory types/constraints,
aliases with Versions, and false offline-complete claims are rejected.

The caller must capture coherent source state before constructing the input.
The writer does not turn a Registry epoch or a sequence of mutable API reads
into snapshot isolation. Input collections are copied and their values are
immutable. Contradictory document context produces `inconsistent_snapshot`.
Producer capabilities must already describe the read-only view; mutation flags
are rejected rather than silently retained or rewritten.

Producer validation resolves Group metadata before Version constraints, so
input enumeration order cannot hide a Group default or change `equals`
semantics. Construction still emits the captured portable configs, with only
the already specified canonical Core routing-field transformations.

The writer uses sorted property names, safe UTF-8 JSON escaping, normalized
exact number tokens, deterministic ordering, and deterministic range splitting.
It preserves array order and opaque document bytes. This reproducibility is a
producer choice, not a globally standardized semantic digest. Routing splits
before either configured/normative limit; an impossible entry, fixed entity
index or shard control fails with `limit_exceeded`.

Construction and publication share the reader's graph/record constraints.
After exhaustive validation, publication writes blobs using Distribution's
upload protocol, then child manifests/indexes by digest, then the root, and
finally the explicit tag/reference. No automatic publication retry occurs.
Failures can leave harmless unreferenced immutable objects. A lost response
after a mutation can have an unknown remote outcome; it is not proof of rollback.
Blob existence probing/cross-repository mounts are not used.
The draft recommends these reuse optimizations with SHOULD, not MUST. This
producer deliberately publishes its already owned, verified bytes to one
explicitly authorized repository and requires a positive publication
acknowledgement. No source-repository authorization/mount context is inferred;
no execution or qualification of those optimizations is claimed.

For a local layout, the safe File host can stream `package.Objects` to their
digest-derived locations, write `oci-layout` version `1.0.0`, and atomically
publish `package.CreateLayoutEntryPoint(reference)` last. This helper creates a
single-root entrypoint; merging/preserving existing tags and safe durable
filesystem publication remain the File host's responsibility.

## Bounds, errors and lifetimes

A snapshot uses one cumulative `FederationReadBudget` across bootstrap,
subsequent reads and validation. Defaults are 16 MiB per object, 64 MiB total
input/result bounds, 4,096 requests/objects, 1,000,000 work units and depth 64.
Producer defaults allow 256 MiB cumulative work bytes, 32,768 requests/objects
and 4,000,000 work units. Callers may choose finite limits; format index limits
cannot be raised.

Retained normalized metadata has an additional aggregate bound using the same
`MaxTotalBytes` ceiling, separately from actual acquired/staged byte accounting.
This prevents repeated model defaults from expanding a small set of input
records into unbounded cached metadata. Work for normalized JSON values is
charged to the cumulative work budget. Exact input-byte counters are not
inflated by those semantic copies.

Exact objects are buffered only within these bounds, then returned as owned
read-only streams through `FederationDocument`/`OciSnapshotObject`. A result's
bytes and JSON survive source/session disposal. A session rejects concurrent
operations, callback reentry and disposal during an active operation. Disposal
clears caches but does not dispose caller-owned readers. Cancellation is
propagated; HTTP deadlines also cover body consumption.

Data/access failures use `FederationException.Code`: `not_found`, `ambiguous`,
`unsupported_binding`, `unsupported_version`, `unsupported_operation`,
`integrity_error`, `invalid_package`, `inconsistent_snapshot`, `limit_exceeded`,
`policy_denied` or `unavailable`. Invalid public constructor arguments and
session misuse retain ordinary .NET argument/disposal/operation exceptions.
Budget exhaustion never returns a truncated collection or an asserted unique
match.

## Evidence and current boundaries

The subsequent metadata-only field-plane fix passed **270/270 OCI tests on each
managed TFM**. Its 18 additional cases cover ordinary object/string/URL-looking
values, nested and top-level nulls, missing/null default inputs, wrong types,
actual document-field rejection, complete closure and producer/read round
trips. It supersedes the 252-case managed totals below, not any native/platform
qualification. Exact evidence and the parent source/oracle proposal are in
session `files\oci-completion\field-plane-implementation`.

The OCI-completion follow-up passed **252/252 public tests on net8.0 and
252/252 on net10.0**, with zero failures/skips, using
`artifacts\oci-completion` and D-drive scratch. The new public regressions
cover typed timestamp/number matching, defaults and conditional materialization,
Group-default constraints, consistent document media, post-normalization native
rules, bounded expanded metadata, unknown non-profile annotations, and explicit
unsupported-view/root-absence outcomes. Independent original/SPEC005 producer
oracles and existing graph/publication/credential controls also ran.

These are current managed results, not fresh native or feed qualification.
The earlier native results below describe their original revisions. Both
NativeAOT and trimming analyzers remain enabled; parent-owned full native/feed
gates are separate. The session's `files\oci-completion` contains exact logs,
input pins, functional red/green slices and a proposal for all 55 previously
unreviewed OCI rows (52 reviewed, 3 informative); the ledger was not edited.

The explicit project is `tests\XRegistry.Oci.Tests\XRegistry.Oci.Tests.csproj`,
using CPM-pinned TUnit 1.66.27 and Microsoft.Testing.Platform. It links the
unchanged independently expected fixtures, and copies the frozen Python oracle
and Apache-2.0 license into test output only. Authored malformed graph fixtures
are independently re-addressed test data, not writer output used as its own
oracle. The seven actual packaged models are also exercised through native
snapshot construction and reads.

The escaped-XID Release runs passed **226/226 cases on net8.0, 226/226 on
net10.0, and 226/226 in the net10.0 win-x64 Native AOT executable**, with zero
failures or skips. Existing File composition suites also passed **75/75** on
each target and in a fresh Windows native executable. Both native executables
ran with an empty `DOTNET_ROOT_X64`; a managed apphost control failed with
`.NET location: Not found` under that same setting. Scoped whitespace
verification passed for the affected OCI files. Exact logs and commands are
under `artifacts\uri-binding-consistency`.

The current package references
Client and Federation; the parent's Client dependency brings SharpZipLib 1.4.2
transitively. OCI does not use it for document wrapping or decompression.

| Normative surface | Public test evidence |
| --- | --- |
| Root selection and exact model bootstrap | `FrozenRootsPinExactBytesAndPreserveCapturedModel`, `DigestSelectsAnUnadvertisedRootAndUnqualifiedSelectionIsAmbiguous`, `LayoutSelectionRejectsDuplicateTagsEvenWhenDigestsMatch` |
| Pin stability, no newer-root repair | `MovingATagCannotChangeTheSelectedRootOrRepairMissingDescendants`, `WrongRouteAndMovedTagCannotRepairAMissingPinnedManifest` |
| Descriptor, media, artifact, annotation and JSON rules | `ProfileRulesRejectMalformedRootAndControlEdges`, `StrictJsonFailuresAreReportedAfterIndependentReaddressing`, `DistributionRejectsContradictoryTransportEvidence` |
| Exact 256/257 and byte bounds | `LayoutIndexDescriptorCountHasAnExactInclusiveLimit`, `CollectionIndexCountIncludesEveryDescriptorWithoutTruncating`, `RootIndexLimitCountsExactUtf8IncludingWhitespaceAndMultibyteText` |
| Sorted ranges, exact boundary, selective trace | `InvalidRangePartitionsAreNotAbsence`, `InvalidLeafKeysAndEmptyShardsCannotBecomeSuccessfulEnumeration`, `AnExactShardBoundarySelectsTheRightIntervalWithoutReadingTheLeftPayloads`, `FrozenVersionLookupReadsOnlyItsRoutingPathAndPayload` |
| Full required closure vs selective success | `FrozenClosureMatchesIndependentInventory`, `MissingUnvisitedPayloadFailsClosureNotSelectiveRead`, `AnOfflineCompleteClaimCannotHideAnExternalVersionInAnUnvisitedBranch` |
| Core views, model/source/imports/empties | `StandaloneMetaPointersResolveInsideReturnedDocument`, `RegistryViewPreservesImportedAndEmptyModelCollections`, `OriginalIncludesRemainProvenanceAndResolvedImportsNeverFetchNetworkModels`, `EveryPackagedModelProducesACompleteNativeSnapshotWithoutRuntimeModelGeneration` |
| Default, aliases and type identity | `FrozenDefaultUsesV1NotNewerV2AndImportedAliasesKeepSourceIdentity`, `AliasMetadataRemainsUnexpandedEvenWhenUnresolvable`, `StructurallyIdenticalResourceTypesDoNotAuthorizeLocalAliases` |
| Resource/Version constraints | `FullClosureRejectsContradictoryCoreRecordSemantics`, `ExhaustiveValidationEnforcesCrossVersionAndCoreIdentityConstraints`, `VersionReadsDischargeOwningGroupEqualsConstraints` |
| Exact opaque, binary, zero, metadata-only and external documents | `MetadataOnlyAndZeroByteDocumentsAreDistinct`, `OpaqueOciLookingDomainBytesAreNotParsedCompressedOrRewritten`, `ExplicitExternalDescriptorRetainsSnapshotAndResolvedIdentityWithoutAcquisition`, `EmbeddedBaseAndOriginAreNotSnapshotLocatorsAndOpaqueBytesRemainUnchanged` |
| Complete label selection | `CompleteLabelSelectionChecksLaterShardsAndDefaultLabels`, `ARequiredMissingShardCannotBeReportedAsAUniqueLabelMatch`, `LabelValuesAreLiteralCaseInsensitiveAndNeverUnicodeNormalized` |
| Shared federation/result context | `FederationSourceMaterializesCompleteEnvelopesRatherThanExposingStorageConfigs`, `ProducerResolutionSignalUsesTheActualFieldAndDoesNotCauseCatalogRetraversal` |
| Shared external Document descriptors, unchanged native APIs and no unavailable-error fallback | `FederationExternalDocumentsUseTheSharedDescriptorWithoutChangingNativeReads`, `FederationAcquisitionUnavailableIsNotRetriedAsAnExternalDescriptor` |
| Escaped selectors, stored spelling, decoded IDs and local pointers | `EscapedSelectorRetainsRequestSpellingAndSelectsTheFrozenIdentity`, `LiteralAndEscapedSelectorsResolveTheSameStoredColonAndAtIdentity`, `MetadataUsesDecodedIdsAndLocalPointersButPreservesStoredXidSpelling`, `FederationEnvelopeRetainsStoredCollectionIdentityAndDecodedNavigation` |
| Strict one-pass URI syntax, exact component bounds and identity collisions | `EscapedSelectorsRejectSeparatorsDoubleDecodingAndInvalidEncoding`, `MalformedStoredUriKeysFailBeforeAcquiringAnyCollectionMember`, `EncodedSelectorsKeepCoreDecodedAndEncodedComponentBounds`, `EscapedSelectorIdentityRemainsCaseSensitive`, `FullValidationRejectsSiblingUriAliasesEvenWhenTheirDescriptorsShareADigest` |
| Canonical producer keys and bounded cross-shard selection | `ProducerCanonicalizesCoreXidsButNotDocumentBytesUrlsOrExtensionStrings`, `EquivalentInputXidSpellingsProduceTheSameCanonicalGraph`, `CanonicalUriKeysKeepBytewiseShardOrderingAndBoundedAliasLookup`, `NoncanonicalRoutingKeysAndBoundsAreRejectedWithoutRewritingPinnedObjects`, `LegacyNoncanonicalShardKeysRequireExplicitMigration` |
| Budgets, cancellation, concurrency and ownership | `BootstrapBudgetsHaveInclusiveExactLimits`, `WorkDepthAndResultExhaustionNeverReturnPartialSuccess`, `CancellationReentryAndDisposalKeepOwnershipAndAllowLaterSequentialReads`, `ConcurrentReadAndDisposalAreRejectedUntilTheOwningReadCompletes`, `ResponseBodyDeadlinesAndCallerCancellationDisposeTheOwnedStream` |
| HTTPS routes, credentials and token/redirect denial | `DistributionUsesManifestAndBlobRoutesAndPinsTagOnce`, `UnauthorizedRedirectAndTokenFlowNeverForwardCredentials`, `AmbientCredentialsAddedAfterConstructionAreNotSent` |
| Deterministic coherent producer, 999 entities and byte-driven splits | `EquivalentInputsProduceIdenticalRootsAndInputOwnershipPreventsTornCaptures`, `WriterPartitionsLargeCollectionsWithoutA256EntityCap`, `WriterSplitsByExactBytesBelowTheDescriptorLimitAndRejectsImpossibleEntries`, `WriterSplitsImmediatelyBeforeTheIndependentExactEncodedIndexBoundary` |
| Independent writer validation and publication | `IndependentPythonOracleAcceptsProducedLayouts`, `PublisherCommitsReferenceOnlyAfterEveryBlobAndChildManifest`, `DistributionPublishesOrdinaryBlobsAndNestedManifestsBeforeTheSelectedTag`, `UploadSessionDestinationsRequireIndependentRepositoryScopedAuthorization` |

Commands, from the repository root:

```powershell
dotnet test --project tests\XRegistry.Oci.Tests\XRegistry.Oci.Tests.csproj -c Release -f net8.0 --no-ansi --results-directory tests\XRegistry.Oci.Tests\TestResults\net8
dotnet test --project tests\XRegistry.Oci.Tests\XRegistry.Oci.Tests.csproj -c Release -f net10.0 --no-ansi --results-directory tests\XRegistry.Oci.Tests\TestResults\net10

$env:VSInstallerPath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$env:PATH = "$env:VSInstallerPath;$env:PATH"
dotnet publish tests\XRegistry.Oci.Tests\XRegistry.Oci.Tests.csproj -c Release -f net10.0 -r win-x64 -p:PublishAot=true -o tests\XRegistry.Oci.Tests\bin\native\win-x64
& .\tests\XRegistry.Oci.Tests\bin\native\win-x64\XRegistry.Oci.Tests.exe --no-ansi --results-directory tests\XRegistry.Oci.Tests\TestResults\native-win-x64
```

Windows native discovery needs the Installer directory on this process's PATH
so Visual Studio's internal `vswhere.exe` invocation resolves. Setting an
MSBuild property alone does not change that process PATH; no manual
`vcvarsall` setup or system-wide environment edit is required.

**Intentionally unsupported or host-owned:** token negotiation and redirects;
external-content acquisition; relative URI-target validation requiring separately
authorized context (absolute URI/URL values are exempt from Core `target`);
source coherence acquisition before producer input;
signatures/referrers and arbitrary domain dependency capture; local filesystem
durability/entrypoint merging; HTTP facades and write-through federation. These
are not advertised as native resolver capabilities.

**Qualification boundaries:** the Windows native executables are exercised,
not merely published. The escaped-XID follow-up does not claim new Linux or
ARM64 execution or live remote-registry interoperability. Arbitrary
noncanonical legacy graph annotations now require explicit migration.
Linux requalification is blocked by the unavailable Docker engine after the shared read-only
container filesystem; the earlier 185-case Linux evidence remains valid for
that earlier revision. Test HTTP handlers execute exact Distribution protocol
flows without remote mutation.

The frozen Python oracle continues to qualify original fixtures. The reviewed
SPEC-005 successor also checks canonical graph annotations, decoded config
identity and exact-byte bounds. All 394 affected independent mapping/OCI
regressions passed after updating literal expected graph keys, without changing
Document bytes. Recovered DirectoryMapping/File/Git tests now directly exercise
escaped entity, collection, alias and Document selection through the shared
interpreter.

The resumed OCI suite passed 228 cases per managed TFM and 228 cases in a
fresh .NET 10 Windows x64 native executable, with zero IL warnings. These
supersede the earlier 226-case URI run for the corrected contract, not the
unexecuted platform cells.

`IndependentCorrectedPythonOracleResolvesEscapedProducedDocument` now runs the
retained SPEC-005 Python successor against sharded .NET producer output from
both literal and escaped input identities. It checks full graph acceptance,
the unchanged snapshot pin and requested selector, canonical resolved identity,
and exact Document bytes. The original oracle still runs separately; its files
are not replaced. All 22 writer-test executions passed across net8.0 and
net10.0, including these four new executions. The subsequent complete
230-case OCI suites passed in native Windows x64 executables on both TFMs,
with zero IL warnings. These runs do not qualify Linux or ARM64.

### Frozen provenance

The approved baseline manifest is
`1ac85c1403324e1ea5bf2d50fe5d5d567aa2c5cc272e27eb3645c6679ddc6bdb`.
`eng\specification\corrections.json` records explicit successor artifacts; the
following hashes describe the immutable original baseline, not SPEC-005.

| Artifact | SHA-256 |
| --- | --- |
| OCI normative working draft | `3baa6cf9820d0aff7630e23cd6fa52090dda248906854ee9256016b19880a26f` |
| Independent `expected.json` | `b768b0e5ec7402dfd5ad3bbea4a408badfa63075c07749b81f9f3b0608b4eaf4` |
| Frozen `tools\oci_examples.py` | `f65861e11332b8a61441ee247a33d3cd3193820bfe875da9624af63e04110d00` |
| Frozen `tools\federation_examples.py` | `a489c9e7b755a5e1b9e86b0d9328262edb9579e513dbe64a09039403338286aa` |
| OCI graph schema | `40ba8201e1d5bfd323281b9959c08012deb9f266ac52ee8a5fde4a91fe016b73` |
| OCI record schema | `bddb245dca0f8592b7895654dbb159803078bd76d40259f68d4833682afc212b` |

The offline fixture root is
`sha256:c937f902c54ca9c63e510bab3b4ec07ec3775ad338ac030ac93d916a736efba6`
(99 objects); the linked root is
`sha256:dff871378d2678ee851fb7d97bae5a6b69b05c3d668f224a2f97127e8e8dee88`
(98 objects). Each has 44 indexes, 25 manifests and 25 configs. Their independently
expected bytes, not values regenerated by the .NET writer, anchor the reader
tests.
