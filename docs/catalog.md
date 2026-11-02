# Catalog publication rules

The Server validates Registry-of-Registries description metadata **before
publication**, after Core metadata completion. A Resource model opts in by
setting `modelcompatiblewith` to the exact, case-sensitive identity
`https://xregistry.io/xreg/domains/registry/specs/model.json`
(`RegistryDomainRules.CatalogModelUri`). Collection or attribute spelling alone
does not opt in, and a Group-level annotation does not select Resource rules.

The pinned Registry model source does not contain this annotation.
`BuiltInRegistryModels.LoadSource`, `OpenSource`, `Compile`, and `CompileAll`
do not silently add it. Trusted server hosts can choose
`BuiltInRegistryModels.CompileForServer(RegistryModelKind.Registry)` or
`BuiltInRegistryModels.CompileAllForServer()`. These explicit presets annotate
a detached copy of the packaged Registry source. The composite preset retains
the packaged includes and imports; it does not flatten `EffectiveModel` or
duplicate imported Resource definitions. Other `CompileForServer` model kinds
retain their ordinary `Compile` behavior.

The FileServer sample selects these server presets for `--Model registry` and
`--Model all` (including its default `all` choice). `--ModelFile` is unchanged:
a custom model is not opted in by its names or shape. Persisted models still
take precedence, so an existing unannotated store needs an explicit, authorized
model update rather than silently acquiring new constraints on restart.

A host or custom model author can also explicitly annotate model source
without editing the packaged specification:

```csharp
using System.Text.Json.Nodes;
using XRegistry;
using XRegistry.Models;

using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Registry);
var modelSource = JsonNode.Parse(source.RootElement.GetRawText())!.AsObject();
modelSource["groups"]!["categories"]!["resources"]!["registries"]!["modelcompatiblewith"]
    = RegistryDomainRules.CatalogModelUri;
var model = RegistryModel.Compile(RegistryJson.Parse(modelSource.ToJsonString()));
// Supply model to RegistryEngineOptions.Model.
```

## Publication and consumption

Catalog advertisements use `binding`, not `name`, as their required
case-sensitive discriminator. The packaged Registry model is copied byte for
byte from the selected `pr571-d757328e` artifact; `eng\sync_models.py --check`
verifies that source selection. Both that model and the selected draft prose
use `binding`. Historical source snapshots remain unchanged.

```json
{"federationprofiles":[{"binding":"http","endpoint":"https://registry.example/root"}]}
```

Consumers reject the historical `name` field rather than treating it as an
alias. `CatalogAdvertisement.Name` is the C# accessor for the `binding` value;
it does not change the wire field. Existing descriptions using `name` require
an explicit metadata update before they can be consumed under these rules.

`RegistryDomainRules.ValidateCatalogMetadata` checks a completed description
Version, including every explicitly advertised, known HTTP/Git/File/OCI/OPC UA
binding's producer syntax. It accepts empty descriptions, website-only entries,
and well-formed extension-only entries. It does not require a binding that any
particular consumer supports.

The checks include nonempty profile names and relationship types, absolute
credential-free advertised URIs, nonnegative integer priority values, absolute
`registrytypes`, exact `xregurl`/explicit-HTTP agreement, and same-catalog
relationship Resource XIDs. Known bindings require their specified endpoint,
revision/reference, directory-path, and layout syntax. Git's portable
directory-component rules apply to its storage-root locator, not to Core IDs.
OCI repository spelling is checked before URI normalization can hide invalid
components. URI validation does not rewrite retained endpoint text.

Priority follows Core `uinteger` value semantics: integer-valued decimal and
exponent notation and negative zero are accepted. `RegistryNumber` validation
and exact `BigInteger` ordering avoid floating-point rounding or a digits-only
token restriction. Original numeric tokens are retained; fractional, negative
nonzero, string and boolean values remain invalid.

`ValidateCatalogDescription` exposes just the common, pre-selection checks.
`ValidateCatalogAdvertisement` checks one advertisement's common and known
binding syntax. These methods inspect caller-owned `JsonElement` values, use
Core diagnostics, and observe cancellation. They supplement, rather than
replace, Core model validation and the caller's JSON/work limits.

`CatalogDescription.Parse` and `CatalogAdvertisement.ValidateBinding` reuse
these pure Models rules. Federation retains selection order, implicit HTTP
priority, caller policy, supported-binding filtering, and acquisition policy.
`CatalogBindingSyntax` shares Git ref and portable root-path grammar with
Federation; the native consumer's total-length and access limits remain separate.
The Federation-to-Models project dependency is acyclic and adds no external
NuGet dependency.

Unknown profile names and relationship types retain their exact spelling.
Unknown parameters can remain catalog data, including in built-in profiles;
they do not waive known required parameters. Selecting a built-in profile with
unknown parameters still reports `unsupported_operation`, without stripping
parameters or trying a fallback. `XRegistry.Bindings.OpcUa` validates a
selected OPC UA advertisement, maps its portable Registry root into a supplied
`NamespaceTable`, and selects one discovered endpoint only when caller policy
permits it. `CatalogDescription.Parse` does not apply those binding-specific
checks before selection.

`OpcUaRegistryReader` applies the protocol rules over an abstract Session: it
rejects a root that is not a Registry, pages Browse to completion and releases
continuation points on failure, and reads FileType objects with Open, Read until
an empty chunk, and an always-attempted Close. It checks `SpecVersion` before
interpreting Registry contents and parses the `Capabilities` and `Model` files
as JSON. A pre-cancelled file read does not open a handle. If cancellation
occurs while Open or Read returns, the reader stops before another read or
accepting the returned bytes, even when the Session ignores its token. An
acquired handle is closed with an uncancelled token, and a Close failure remains
attached to the original cancellation under `OpcUa.FileCloseFailure`.
Cancellation during a successful Close also prevents the shared reader from
returning bytes. An existing read or Close failure retains precedence over a
later cancellation; cleanup still uses an uncancelled token.
When a completed read is followed by a failed Close, an already classified
federation error retains its original code, diagnostic and cause. An
unclassified Close exception reports `unavailable`.
Read lengths are capped by the configured chunk size, advertised buffer limit
and remaining capture budget. At the exact byte limit, the reader requests one
byte only to distinguish EOF from an oversized document; a nonempty response
rejects the capture with `limit_exceeded`, and that byte is not accepted. Empty documents
still pass through Open, Read and Close. The adapter rejects nonpositive Read
lengths before any native operation, and both reader and adapter reject a
response larger than its request. Read failures still attempt an uncancelled
Close and retain a cleanup failure on the original error.
Before Open, the reader obtains the mandatory namespace-zero Size Property as
UInt64. A reported size above the byte budget rejects before acquiring a
handle. Only an operation status of `Bad_NotSupported` means unknown size;
missing or ambiguous Properties, wrong types and other native failures remain
errors. Known Size must agree with the bytes and EOF. After EOF, the reader
checks Size again before Close: changed or lost known Size, or a final Size
that disagrees with the captured length, reports `inconsistent_snapshot`.
Unknown Size still requires EOF, and a known zero retains the complete
Open/Read/Close sequence. Size failures after Open attempt uncancelled Close
and preserve any cleanup error.
Before acquiring a handle, shared reads and replacement writes obtain the
optional namespace-zero UInt32 `MaxByteStringLength` Property on the selected
FileType Object. A file declaration overrides the ServerCapabilities
declaration; only an absent Property permits server fallback, and absence on
both retains the configured chunk size. Present but ambiguous, remote,
wrong-kind or incorrectly typed Properties and native read errors reject the
transfer. A declared zero rejects before Open because no positive request fits
that limit; it is not interpreted as unlimited. The effective chunk size is the
smaller of the declaration and configured size, including for the final partial
Write. Creation-time handles still close before replacement preflight, so a
limit acquisition failure does not leak or reopen that handle. These checks do
not yet derive a buffer limit from transport-negotiated message sizes.
FileTransfer Method results must match their Part 20 signatures: Open returns
one UInt32 handle, Read returns one ByteString, and Write, SetPosition and Close
return no output arguments. Unexpected output counts or types report
`invalid_package`; native status errors retain precedence. If a successful Open
returns extra arguments but its first output identifies the handle, the adapter
attempts an uncancelled Close before reporting the malformed response and
attaches any cleanup failure. This cleanup does not undo staged changes.
A successful native Close consumes the read-handle exemption even if its
unexpected outputs are then rejected; malformed output does not authorize
reuse of a closed handle.
The Session adapter discovers each FileTransfer Method on the selected Object
and calls its returned NodeId, rather than a standard FileType declaration ID.
It requires one Method with the requested namespace-zero BrowseName, completes
all Browse pages before selecting it, and does not fall back when the Method
is missing or ambiguous. A Method on another Server is rejected before Call.
Writable operations recheck encryption after discovery; cancellation at that
point also prevents Call. Discovery is repeated for each operation rather than
caching instance Method NodeIds across changes to the AddressSpace.
Each FileTransfer operation retains its entry Session identity across namespace
bootstrap, Method discovery and Call. If the Session changes, the adapter
reports `unavailable` rather than issuing a subsequent Call or returning a
handle or bytes from the interrupted operation. It discards its cached
namespace table and does not send an outstanding Browse continuation to the
replacement Session, even for release; a blocked continuation release is
reported as additional diagnostic information. A later Open reads the new
namespace table. These checks cover transitions observed within an operation,
not every disconnected state or stale handle supplied between operations.
`OpcUaSessionConnection` discovers endpoints and opens a Session only
after caller policy selects one; it maps the advertised root through the
Session's `NamespaceArray` and verifies the root and Core version.

The reader's `maxBrowsePages` budget defaults to 10,000 pages per enumeration,
including the initial page and empty pages. Completion on the final permitted
page succeeds. If that page still has a continuation, the reader reports
`limit_exceeded` before requesting another page and attempts to release the
outstanding continuation. A release failure remains attached to the limit
error. This budget is separate from the returned-reference limit.
`OpcUaSessionAdapter` applies its own `maxBrowsePages` budget, also defaulting
to 10,000, to internal type, encoding and Method discovery. These internal
enumerations do not pass through the reader's budget. They count empty pages,
permit exact-limit completion and retain release failures on the limit error.

Root admission requires one ObjectType TypeDefinition and follows only that
definition's ObjectType supertypes. Competing definitions or supertypes,
cycles and non-ObjectType parents are rejected; an absent or unrelated
definition does not identify a Registry. A returned NodeId whose namespace
index is outside the Session's table is not treated as resolved. The ancestry
budget includes the starting type: 31 subtype edges are permitted, 32 are not.

`OpcUaSessionConnection.OpenAsync` and `OpcUaFederationTarget.SelectEndpoint`
require an `isSupportedTransportProfile` predicate in addition to endpoint
permission. The caller identifies the profiles its installed transports support;
the binding does not restrict custom profiles to a built-in list. A rejected
requested profile produces `unsupported_operation` before discovery, without
dropping the request or trying another transport. An endpoint scheme without a
factory in the SDK's default client transport registry also rejects before
discovery.

Discovered endpoints must first match the advertised URL and optional application
and transport identities. No identity match remains `not_found`. Matching
endpoints with no supported profile produce `unsupported_operation`; supported
matches denied by caller policy produce `policy_denied`. Multiple supported,
permitted matches remain `ambiguous`. Transport support does not grant endpoint
permission, establish trust, or replace certificate verification.

`OpcUaSessionAdapter` maps read and write operations onto an `ISessionClient`,
using OPC Foundation .NET Stack 2.0.1. `OpcUaRegistryReader` maps Registry,
Group, logical Resource and exact Version properties. Default Resource
operations use the logical Resource's delegated interface; explicit Version
operations resolve a typed `Versions` component and its exact Version child.
The reader also reads document-bearing Resources. `OpcUaRegistryReadSource`
implements `IFederationReadSource` for Registry, Group, Resource, Meta and
exact Version views. It returns Registry Model and Capabilities, complete
collection snapshots and selector results, with local JSON Pointers rebased to
each returned view. Document reads return exact local FileType bytes or an
unfetched external ExpandedNodeId descriptor; the source does not fetch remote
content.

After local document Read and Close succeed, the source rereads the selected
Version's entity Properties, including Epoch and ContentType, and compares
them with the values obtained before file access. Changed Properties report
`inconsistent_snapshot` instead of returning bytes with outdated metadata.
Default selection also rechecks Resource Meta; an exact Version read does not
depend on later changes to the default. Cancellation during Close or either
recheck prevents a document result. File and recheck failures retain their
original outcomes. The shared reader also checks Size, but these observations
do not establish an immutable revision pin or a Registry-wide transaction.
Generic `ReadFileAsync` does not recheck Version metadata.

`OpcUaRegistryWriter` writes scalar Properties; calls create,
get-or-create, delete and attribute Methods; and replaces model-declared
Resource documents through FileTransfer. It supports both entity `Labels` and
Resource Meta `MetaLabels`. Document writes are bounded, start at position
zero, erase the prior contents and always attempt to close an opened handle.
`DeleteAsync` always sends the required UInt32 `ExpectedEpoch` argument.
Omitting the optional .NET precondition sends `0`, as does an explicit zero;
neither form omits the UA Method argument. Nonzero values are sent unchanged.
Label and Meta label Methods also send their required epoch argument. The
caller selects the applicable counter: `Epoch` for Groups, exact Versions and
Metadata Resources, or `MetaEpoch` for document-backed logical Resource
deletion and Meta labels. Sending the argument does not establish that the
Server enforces comparison or performs deletion atomically.
Resource creation results retain the exact Version NodeId and assigned Version
identifier. Both creation Methods reject an empty assigned identifier or one
that differs from an explicit requested Version, including case differences.
If rejected outputs identify a requested file handle and its Object, the writer
attempts an uncancelled Close without writing, retaining any cleanup failure
on the output error. This validates the Method result, not the target's complete
Version metadata or server-side creation semantics.

`CreateOrReplaceResourceDocumentAsync` composes Resource acquisition and
document replacement. It checks the accepted Core version, compiled
document-bearing collection, request path, required type mappings and byte
budget before calling `GetOrCreateResource` without a creation-time handle.
An omitted or empty Version identifier selects the existing default or lets
the Server assign the first Version; an explicit identifier selects or creates
that exact Version. The writer resolves the returned assigned identifier
through the selected logical Resource's typed `Versions` hierarchy, validates
its `ResourceId`, `VersionId` and other read metadata, and requires the resolved
NodeId to equal the Method result before opening mode 6. It returns the exact
Version identity and `Created` outcome after writing and closing.
The existing `ReplaceResourceDocumentAsync` only resolves an existing Resource
or Version; use the composed operation when creation is allowed. A failed
validation or cancellation after creation can leave the created Resource and
Version in place; the writer does not delete them or retry with another
creation Method. Default selection, validation and document commit are
separate interactions, not one transaction. Other Version metadata changes
remain explicit Property or attribute operations.

Pre-cancelled replacements do not resolve or Open a file. After Open, the
writer checks cancellation before positioning and after positioning or each
Write, including the final chunk and empty replacements. It attempts Close
with an uncancelled token and retains any Close failure on the original error.
`CompleteCreatedResourceDocumentAsync` takes ownership of the supplied
creation-time handle. It closes that handle unchanged, then reopens the same
Object with mode 6 before writing, because a creation-time handle does not
establish truncation semantics. Cancellation or an oversized document still
closes the supplied handle without writing or reopening it. A failed initial
Close prevents replacement. Close is a commit operation, not a rollback:
cancellation after staging bytes does not establish that the Server discarded
them, and separate create/Open/Write/Close Calls are not one atomic replacement.

The production adapter requires `SignAndEncrypt` before Property Writes,
named registry/domain Calls, writable file Opens, file Writes and committing
Closes. A denied operation reports `policy_denied` before its native request.
Only handles opened read-only by this adapter can be repositioned or closed
without that mutation check; the exemption is scoped to the Session, file and
handle, and is removed after a successful Close or a writable reopen. Unknown
handles, including handles returned by creation Methods, require encryption.
A changed channel mode is checked again for each mutation. This does not grant
Roles or Permissions or authenticate an unsecured read; those remain separate
caller and Server responsibilities.

FileType Open validates the Part 20 mode bitmask before native operations.
Read or Write must be selected, reserved bits must be zero, and EraseExisting
requires Write. Append selects the initial position independently of write
access, so mode `9` (Read and Append) receives the same read-handle exemption
as mode `1`; every mode with Write set requires encryption. The adapter sends
the accepted byte unchanged. The Server remains responsible for access rights,
initial positioning and concurrent-open rules.

The adapter rejects non-Good Service, operation and Method argument statuses;
a plausible value cannot override a bad or uncertain status. Good subcodes
remain accepted. Known native failures map to the draft's resolver outcomes.
The original SDK exception, or a `ServiceResultException` carrying the original
status, remains the inner exception. `Exception.Data` retains response headers,
operation diagnostics and Method argument results and diagnostics. Failed
continuation releases remain attached to the original operation failure;
multiple cleanup failures are aggregated rather than replacing an earlier one.
The public message does not copy native diagnostic text. Cancellation remains
cancellation rather than becoming a native error.

Event discovery uses the same Service and operation validation for notifier
reads and Browse pages. A failed page does not become an empty declaration
set. If a failed response returns a continuation point, discovery attempts to
release it as well as any outstanding previous point and retains each cleanup
failure on the original error.
Discovery also requires one ObjectType definition for each event-capable
notifier, at most one ObjectType supertype, Object targets for `HasNotifier`
and ObjectType targets for recognized `GeneratesEvent` declarations. Invalid
declarations are reported rather than filtered out. Returned identities and
the selected notifier must map within the supplied Session namespace table;
discovery does not extend that table. The traversal budgets permit 31 type
edges and 63 notifier edges, with the next edge rejected.
Event discovery also shares page, returned-reference and queued-notifier
budgets across the complete traversal. The defaults are 10,000 pages,
1,000,000 references and 10,000 notifiers, including the selected notifier.
Empty continuation pages consume the page budget; unrecognized event references
still consume the reference budget. A wide notifier tree cannot avoid the
notifier budget by staying within the depth limit. Budget exhaustion reports
`limit_exceeded`, releases any outstanding continuation and retains release
failures on the original error. `DiscoverEventTypesAsync` accepts caller limits;
Subscription creation uses the defaults.

The reader, writer and read source have fake-Session coverage. An in-process
OPC UA Stack server also verifies Session establishment, SpecVersion gating,
event Subscription creation and removal, publishing enablement and native
notification fields, Property writes, Group and Resource Methods, attribute
Methods, expected-Epoch `Delete`, model-resolved Resource
document replacement, created-Resource completion, and FileType byte
readback. The writer workflow uses a `SignAndEncrypt` channel; a separate
`None` case verifies rejection of every public writer operation while reads
remain available. This is client-side admission evidence, not proof that an
arbitrary Server enforces mutation security.
Callers still supply OPC UA type NodeIds for Groups and Resources,
and the `ResourceVersionsType` NodeId for version operations. The binding draft permits
several ways to represent collection names but does not define a deterministic
collection-name-to-ObjectType lookup. `OpcUaRegistryReadSource.OpenAsync` verifies
the selected Registry root before reading its documents. Before returning a
source, it requires exact-name mappings for every Group and Resource collection
in the compiled Model, including collections with no observed instances. Missing
mappings or a document/metadata-only mapping that contradicts the Model report
`unsupported_operation`; the source does not defer these checks until an entity
is requested. An empty Model requires no collection mappings.
An absent Model FileType, malformed Model JSON or Model compilation failure also
reports `unsupported_operation`, retaining the original failure and diagnostic.
Transport, access, integrity, budget and capture failures, cancellation and
other malformed native operations retain their own outcomes; these failures do
not establish that the Model is unsupported.
The source does not provide API views
because the binding exposes no retrievable Core API URL. Metadata-only
`MetadataResourceType` Resources with `hasdocument=false` and `maxversions=1`
are projected from their read-only `Metadata` FileType. Resource Properties
are read while that file handle pins the committed revision; exact Version
selection requires an observed `VersionId`, and the source does not invent a
`Versions` hierarchy or document bytes. `MetaDeprecated` is decoded only when
its ExtensionObject encoding resolves to `DeprecationDataType`; its optional
fields retain their JSON types. Other model-defined Properties are projected
when their BrowseNames map to non-system attributes in the compiled Registry
Model. ExtensionObject values must resolve to a DataType; the reader normalizes
structured fields to lowercase xRegistry names and validates each JSON value
against the model. Unmodelled extension Properties are not projected. The
source preserves OPC UA `ExternalReference` values as unfetched descriptors.
Federation can resolve external Resource and Version targets only through a
caller-injected trusted provider lease; it verifies the target Registry, model
and node roles,
application identity, namespace mapping and origin-relative XID before
returning a target node. Application identity and namespace mapping are checked
before browsing the selected Registry or opening its Model and Capabilities
files. Captured external identities retain URI and identifier values, not table
indexes; each target mapping uses that Session's current namespace table.
`ResolveExternalTargetAsync` requires the caller to retain and supply the
reference's logical Resource or explicitly exact-Version role. A target XID
that contradicts that role is rejected before resolving its Group hierarchy;
the successful result retains the checked role. An unknown role rejects before
provider acquisition.
`OpcUaRegistryReader.ReadOriginRegistryAsync` separately reads the logical
Resource's `OriginRegistry` Variable, without selecting its default Version.
The adapter checks the Variable's declared DataType and the ExtensionObject's
encoding DataType against the supplied RegistryOriginDataType NodeId. It
decodes the three scalar fields in order from binary, rejects malformed or
trailing data and limits the encoded body to 64 KiB. URI spelling, identifier
kind and source table indexes remain unchanged. When the SDK has decoded the
ExtensionObject body, the adapter checks the body's DataType identity and uses
its binary encoder before reading the three scalar fields. It does not convert
through JSON. Native encoding failures retain their status and cause; both raw
and decoded bodies have the same byte limit. The reader rejects missing,
duplicate or non-Variable properties and mixed or incomplete origin forms.
The returned `OpcUaRegistryOriginValue` is an untrusted source value, not a
provider binding or portable stored identity.
`OpcUaRegistryOrigin.CaptureAsync` resolves the application/root form through
the source Session's tables and rejects disagreement between `ServerUri` and
the root's resolved application. The stable-URI form needs no native tables
and acquires no manufactured root. Origin equality compares exact URI text,
identifier kind and value; opaque identifiers compare their bytes.
`ResolveOriginTargetAsync` compares that captured origin with a separately
configured or verified origin before invoking the provider resolver. The
resolver receives the approved origin and external target identity, not an
endpoint inferred from an Application URI. For an application/root origin,
the target application must agree and the independently bound root must map
to the lease's selected Registry root before any Registry documents are opened.
Failures after lease transfer dispose the lease and retain disposal failures.
Successful results retain the admitted origin alongside the checked XID and role.
The low-level `ResolveExternalTargetAsync` remains available without origin
admission; its result has no origin.
`ResolveExternalResourceAsync` composes those operations for a concrete source
XID and explicit role. It reads the logical Resource's origin for either role,
resolves it in the source Session and compares it with the independent binding
before reading the selected Resource or exact Version reference. A missing
selected reference reports `not_found`; the operation does not substitute the
other role or select a default Version. Invalid source-role combinations and
pre-cancelled requests reject before source reads. This is a source-directed
read sequence, not a new atomic snapshot guarantee.
Origin-admitted results expose an `OpcUaFederationSourcePin` containing the
origin, concrete target XID, logical/exact role and portable remote Node
identity. `HasSameSource` compares the origin/XID/role key independently of
document bytes; different origins or Registry roots remain separate. Equivalent
escaped XID segments compare by their decoded value without case folding.
`Serialize` and `Parse` provide a versioned local JSON receipt bounded to 64 KiB.
This is an implementation storage format, not a standardized wire format.
It preserves all four native identifier kinds and contains no endpoint or
Session table indexes. The host owns durable storage and must supply current
independent trust configuration when reopening a receipt; stored origin text
does not authorize acquisition.
`ResolvePinnedTargetAsync` remaps the retained Node identity through the new
Session and rejects a changed target XID before resolving its hierarchy.
`ResolvePinnedExternalResourceAsync` also rereads the source Resource's current
origin and selected reference; a changed portable Node identity rejects before
provider acquisition. A changed origin or authoritative form is a different
source, not an update to an existing pin. Failed target validation disposes the
acquired lease. Real-server cases flush both origin forms to a host-managed
file and reopen it before selecting a different endpoint for the same
application. The replacement Server assigns the Registry namespace a different
index. The checked result has a new Session-local NodeId and locator but retains
the exact serialized pin, XID and Version bytes.
No automatic pin store, non-OPC-UA target router or native server-side
origin publication is provided. These boundaries still prevent presenting this
as the complete `XREG-Federation` proxy workflow.
Cancellation already requested at resolver entry prevents provider acquisition.
If acquisition returns a lease after cancellation, the resolver disposes that
lease before propagating cancellation. A disposal failure is retained under
`OpcUa.TrustedProviderDisposeFailure` without replacing the cancellation.
Event type discovery, notification decoding and
Subscription management provide client-side support for native xRegistry Events.
Both native deprecation transitions map to Core's
`io.xregistry.<entity>.deprecated` action. Decoded events retain the concrete
`EventType` NodeId, so a consumer can distinguish setting or changing deprecation
from removing it without inventing a Core `undeprecated` action.
Pre-cancelled Subscription removal leaves the Subscription and its notification
handler intact. The real-server test verifies live delivery after repeated
pre-cancelled attempts. Once deletion reaches the SDK, a failure or cancellation
can clear the SDK's local Subscription ID without establishing remote removal.
The wrapper retains that ID and retries remote deletion before removing its
local Subscription. Only a Good result or `Bad_SubscriptionIdInvalid` confirms
the remote Subscription no longer needs deletion; other failures remain
explicit and permit another attempt. The original native status is retained.
When the caller's token is cancelled, SDK `Bad_RequestInterrupted` becomes
cancellation with that token and the native failure retained as its cause.
Without caller cancellation, the same status remains a native failure.
Real-server cases cover one and repeated Service failures, cancellation while
the server holds the deletion request, an already-removed remote Subscription,
and subsequent Stop/disposal without another deletion. Retrying deletion does
not restore event delivery after the SDK has cleared its local Subscription,
nor establish recovery across a lost or replaced Session.
Concurrent Stop and Dispose calls await the same deletion task rather than
returning while deletion is still in progress. The initiating caller's token
controls deletion; a later caller can cancel its own wait without cancelling
that deletion. A failed deletion permits a later retry; a completed deletion
remains completed. A real-server test pauses `DeleteSubscriptions` to verify
these waiting and cancellation boundaries and exactly one deletion request.

A two-server test resolves a caller-authorized metadata-only Resource to an
exact target Version, reads its FileType bytes, and verifies the selected XID,
ContentType and bytes. The target uses alternate Group/Resource collection
names and distinct ObjectType NodeIds supplied through the caller mapping.
Endpoint security policy remains the caller's choice; this binding does not
impose a secure-only default. It does not claim OPC UA API conformance. The
generic Federation package has no OPC UA stack dependency.

Invalid writes use existing Core errors such as `invalid_attribute`,
`unknown_attribute`, and `required_attribute_missing`, with the Version path
and precise underlying attribute diagnostic. Failed edits retain existing
metadata, Version allocation, outbox batches, and publication generation.
Adding the compatibility declaration validates existing Versions, including
nondefault Versions; incompatible model transitions fail atomically with
`model_compliance_error`. The declaration survives frozen-model restart.
Readonly input is ignored and defaults are applied by Core before domain checks.

Malformed common catalog data in an offline consumer reports `invalid_package`,
including URI user information. Explicit selection/access-policy denial still
reports `policy_denied`.

## Publication snapshots and channels

`PublicationChannelStore` in `XRegistry.Publication.Server` stores exact
manifest bytes and the supplied objects named by their descriptors. It verifies
byte size and SHA-256 before commit, rejects a changed object at an existing
reference, and commits new objects with the channel-head update in one
`IRegistryPersistence` batch. A channel update must supply the current epoch;
the publication scope remains fixed after channel creation.
An epoch mismatch raises `PublicationChannelConflictException` with Core's
`mismatched_epoch` diagnostic and both epoch values. A channel that has not
been created has comparison epoch zero.

Manifest and open-record parsers interpret byte sizes and channel epochs as
exact, non-negative integer values under Core's numeric rules. Decimal and
exponential spellings such as `790.0` and `79e1` are accepted without
floating-point conversion; fractional and negative values are rejected.
`RegistryJsonLimits` applies to numeric tokens, including nested descriptors.
Parsing retains the received bytes. Equivalent numeric values do not imply
equal Snapshot IDs because manifest identity covers exact bytes.

Snapshot references use Core's Resource identity comparison, while the
`manifest` Version ID remains case-sensitive. Accepting an equivalent Resource
spelling does not rewrite the manifest, descriptor or open record, and does
not change the lowercase Snapshot ID or digest.

Publication Git references require complete commit object IDs. OCI references,
including File OCI layouts, require lowercase SHA-256 digests instead of tags.
The parsers reject mutable or missing selectors, and the reader checks again
before opening a binding for a programmatically supplied channel head.
These checks do not authorize acquisition or select an alternate binding.
Document-tree and extension bindings retain their own selection rules.

Parsed references and programmatic channel access use the same endpoint
guard before the binding resolver runs. Endpoints must be well-formed absolute
URIs without embedded user information; local path spellings are not converted
into File access. This check does not authorize a destination or replace the
selected binding's endpoint and parameter rules.
An explicit empty user-information component such as `https://@host` also
rejects; URI normalization does not remove that declaration before validation.
Selected built-in bindings reject unknown parameter names before acquisition.
Publication and Federation share that vocabulary through `XRegistry.Models`;
separately named extension bindings retain their caller-supplied parameters.
Parsing and acquisition apply the same Models declaration rules for
HTTP roots, Git repositories and paths, OCI repository locators, and File layouts.
A rejected value produces `invalid_binding` with its reference-relative pointer;
the resolver is not invoked for that access context. Parsed descriptors and
offline contexts also reject unknown built-in parameters without acquiring the
referenced object. OPC UA access requires a host-bearing endpoint without a
query or fragment and a non-empty `registryroot`; optional `applicationuri`
and `transportprofileuri` values must be absolute URIs. The adapter still
validates the selected transport and portable NodeId grammar.

`XRegistry.Bindings.OpcUa` does not provide a built-in `IPublicationReadBinding`.
The generic `PublicationReadClient` accepts a caller-supplied binding resolver,
but this repository does not implement an OPC UA mapping for the structured
`head` value in Resource Meta. Under the working draft, an adapter that cannot
represent Meta extensions or obtain a coherent head must report the operation
as unsupported or inconsistent rather than substitute the default Version or
a live view.

A `source-snapshot` manifest must declare at least one included mandatory
extension. Missing or empty `requiredExtensions` rejects before a channel
update changes persistence. This is a necessary structural check, not proof
that an extension retains a source identity or establishes an immutable
capture guarantee. Generic readers still reject unsupported mandatory
extension semantics; the parser does not assign a provenance vocabulary.

Opening a channel reads the retained manifest and checks its descriptor, scope
and completeness. Offline replay is limited to `offline-complete` manifests,
which must supply every referenced object; replay verifies each object again
and returns the original manifest and object bytes. Durability depends on the
selected persistence provider. The store does not resolve external references
through binding adapters.

`PublicationBundle.WritePackageAsync` writes the selected manifest and required
objects to a directory containing `publication-package.json`.
`PublicationPackage.OpenDirectoryAsync` reads that package without network
access. It keeps the selected directory anchored while opening each path,
rejects symbolic links and Windows reparse points during traversal, and checks
regular-file sizes before reading. This prevents a concurrent path replacement
from redirecting a read outside the package root. Handle-anchored package reads
support Windows and Linux x64/arm64. Other operating systems and Linux
architectures throw `PlatformNotSupportedException`.
Readers apply object and traversal limits and verify bytes against their
descriptors.
Limits supplied to `PublicationPackage.Parse` or `OpenDirectoryAsync` also
govern the captured manifest during replay. Tightened numeric budgets reject
before accepting its bytes; relaxed budgets remain in effect when opening the
verified snapshot. Replay uses that checked manifest without a second parse
under different limits, and rejects unsupported mandatory extensions before
reading captured Registry or domain objects.
`PublicationReadClient.OpenRecordAsync` and package readers accept `sources` as
pinned provenance. They read the materialized top-level snapshot and do not
fetch or reconstruct constituent sources; source composition remains
unsupported.

`MapPublicationSnapshot` exposes a read-only Core HTTP view for a
`PublicationOpenedSnapshot`. The caller must build its backend from that
snapshot's captured Registry bytes and matching model; the mapper cannot verify
that provenance. It enforces read-only persistence and the open-record
response-size budget, serves the context at `/.well-known/xregistry-open`, and
adds the Publication context relation to root responses.

## No acquisition authority

Validation does not fetch a URI, open an advertised file path, execute Git,
follow relationships or federation, resolve a model reference, or forward
incoming credentials. `weburl` and `authority` references are retained without
guessing a missing catalog-root URL context. Dangling descriptive links remain
valid.

URI syntax cannot prove that a remote location really is a Registry root, Git
repository, directory, or another catalog's entry. Those producer assertions
and consumer checks are not replaced with path-name heuristics or reachability
probes. Opaque extension values are not a secret-detection mechanism; producers
must still keep credentials out of all catalog metadata. Acceptance never
grants filesystem, network, transport, or publisher trust.

## Descriptive URI references

`CatalogDescriptiveReferences.Resolve` handles only `weburl` and `authority`.
An absent attribute returns null. A relative value without an explicitly supplied
catalog Registry root returns its original text with an unresolved target; it
does not guess an entry-Version URL, advertised endpoint or storage directory.
Absolute values retain their original spelling.

With an explicit credential-free HTTP(S) catalog root, relative references use
that root as a directory. A missing trailing slash is added, but existing empty
path segments are not collapsed. Source, root and resolved-value UTF-8 limits
are checked independently, and cancellation is explicit. The result remains
descriptive metadata, not permission to fetch it or a federation advertisement.

These rules address bounded catalog publication behavior, not complete
working-draft clause coverage, native qualification, or a release claim.

## Explicit source acquisition and retained origin

`CatalogSourceSelection.OpenAsync` composes a previously acquired, selected
description Version with one explicitly authorized binding factory. Selection
uses the existing priority, exact-profile and access-policy rules. The factory
is called once; its failure never tries another advertisement.

```csharp
// Read the description under catalogContext first; select its explicit or
// Meta-default Version before passing it here.
var description = CatalogDescription.FromResource(capturedResource);
await using var selected = await CatalogSourceSelection.OpenAsync(
    description, catalogContext, descriptionVersionXid,
    new AdvertisementSelectionOptions(["http"]),
    async (advertisement, budget, token) =>
    {
        // This explicitly allowed factory chooses its own destination-scoped
        // credentials, network policy and finite session deadline.
        var http = await HttpFederationReadSource.OpenAsync(
            new Uri(advertisement.Endpoint), transportOptions: null,
            options: null, budget: budget, cancellationToken: token);
        return new FederationSourceLease(http,
            () => { http.Dispose(); return ValueTask.CompletedTask; });
    },
    cancellationToken: cancellation);
var result = await selected.Source.ReadAsync(request, cancellation);
var origin = result.Context.CatalogOrigin;
```

The immutable `FederationCatalogOrigin` keeps the catalog's context/revision,
the exact description Version XID and the chosen advertisement, including its
parameters and original array position. The described Registry's context and
the requested content Version remain distinct. Metadata, exact/empty Documents
and external descriptors retain that origin without adding fields to Core
entities or fetching external bytes.

The acquired binding must match the selected locator and establish an effective
model and well-formed enabled resolution ownership. Source/model/owner changes fail
explicitly. The returned lease retains the factory's credential stamp and owns
its disposal; cancellation or validation failure after acquisition releases the
new lease. Already returned result content remains independently owned.

Selection work, source acquisition and nested catalog-origin hops are bounded.
The shared budget includes both the catalog-selected context and any context
opened by its binding. The HTTP overload uses those same counters for bootstrap,
model includes and all subsequent wire reads; callers must not create a new
budget inside the factory. Other bindings already accept a supplied read budget.

This is explicit entry acquisition, not automatic composition or a catalog
crawler. Callers must respect producer-owned resolution before traversing a
view's catalog, retain its chosen description capture, and authorize every
destination independently. Supplying metadata/context is not proof of publisher
authenticity. Descriptive relationships and websites are never executed.
