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
as JSON. `OpcUaSessionConnection` discovers endpoints and opens a Session only
after caller policy selects one; it maps the advertised root through the
Session's `NamespaceArray` and verifies the root and Core version.

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
content. `OpcUaRegistryWriter` writes scalar Properties; calls create,
get-or-create, delete and attribute Methods; and replaces model-declared
Resource documents through FileTransfer. It supports both entity `Labels` and
Resource Meta `MetaLabels`. Document writes are bounded, start at position
zero, erase the prior contents and always attempt to close an opened handle.

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

The reader, writer and read source have fake-Session coverage. An in-process
OPC UA Stack server also verifies Session establishment, SpecVersion gating,
event Subscription creation and removal, publishing enablement and native
notification fields, Property writes, Group and Resource Methods, attribute
Methods, expected-Epoch `Delete`, model-resolved Resource
document replacement, created-Resource completion, and FileType byte
readback. Callers still supply OPC UA type NodeIds for Groups and Resources,
and the `ResourceVersionsType` NodeId for version operations. The binding draft permits
several ways to represent collection names but does not define a deterministic
collection-name-to-ObjectType lookup. The source does not provide API views
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
returning a target node. Event type discovery, notification decoding and
Subscription management provide client-side support for native xRegistry Events.
Both native deprecation transitions map to Core's
`io.xregistry.<entity>.deprecated` action. Decoded events retain the concrete
`EventType` NodeId, so a consumer can distinguish setting or changing deprecation
from removing it without inventing a Core `undeprecated` action.
If Subscription removal throws, the wrapper restores its notification handler
and permits another stop attempt. The real-server test verifies that repeated
pre-cancelled attempts retain live delivery, that a later stop removes the
Subscription, and that repeated stop/disposal does not recreate it. This does
not establish recovery from an interrupted server deletion or connection loss.

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
