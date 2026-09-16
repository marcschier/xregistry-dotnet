# Server engine and HTTP adapter

This is a working, bounded core integration, not a claim that the entire frozen
specification/working-draft plan is complete. Both libraries target net8.0 and
net10.0 with the repository's strict trimming/NativeAOT analyzers.

The explicitly OpenUSD-compatible model has bounded atomic publication
checks for authoritative identity, verbatim names, current roots, declared
dependencies, local digests and plugin declarations. Opaque artifacts bypass
format collaborators without fabricated validation flags. One-hop borrowed
roots are checked in both mutation directions, with private referring Groups
kept out of reverse-error details. See [OpenUSD current boundaries](openusd.md);
collision-only identifiers and native USD format
validation are not claimed complete.

Catalog publication rules are shared with Federation and selected by explicit
Resource compatibility identity. The FileServer `registry` and `all` choices
use trusted server presets that add that declaration to detached model sources;
ordinary compilation, original packaged bytes, custom `ModelFile` input and
persisted custom models remain unchanged. See [catalog publication](catalog.md).

## Public entry points and ownership

```csharp
var engine = new RegistryEngine(
    new RegistryEngineOptions
    {
        RegistryId = "registry",
        PublicRoot = new Uri("https://registry.example/catalog"),
        Model = RegistryModel.Compile(modelSource),
        AllowAnonymousReads = false
    },
    persistence,
    authorizationPolicy);

app.MapXRegistry(engine, new RegistryHttpOptions { MountPath = "/catalog" });
```

The host must supply `IRegistryAuthorizationPolicy`; there is no default allow
policy, header-based impersonation, development bypass, or automatic outbound
fetcher. Configure authentication middleware before the endpoint. The default
caller is `HttpContext.User`; an optional `CallerMapper` is trusted host code.
Anonymous reads require both `AllowAnonymousReads` and policy approval. Writes
always require an authenticated principal and policy approval, regardless of
the anonymous-read setting.

`IRegistryEngine.ExecuteAsync(RegistryOperation, RegistryOperationContext,
CancellationToken)` is the transport-independent boundary. `RegistryOperation`
has `Action`, `Path`, optional owned `Metadata`, optional caller-owned `Document`,
`ContentType`, ordered/repeated `Parameters`, and an optional
`ExpectedModelRevision`. `RegistryAction` covers Read, Head, Options, Replace,
Patch, Post and Delete. A non-null Document stream denotes a present body even
when it contains zero bytes. The engine consumes but never disposes it.

`RegistryOperationContext(ClaimsPrincipal)` optionally accepts
`PrepareResponseAsync(RegistryResult, CancellationToken)`. This hook must
prepare, not transmit, the response. The HTTP adapter uses it to validate and
buffer the actual status/headers/body before persistence preparation. Failure
does not publish. `DescribeAsync(action, path, context, ct)` supplies the HTTP
adapter's model-aware header shape and revision pin before it reads the body.

`RegistryResult` owns immutable `RegistryJson` and optional bounded
`RegistryDocument` bytes; neither borrows a request, snapshot, or disposable
JSON document. `RegistryDocument.OpenRead()` returns a caller-owned read-only
stream. The result reports Kind, Path, IsDocument, ContentType, Location,
ContentLocation, AllowedActions, ResourceDefinition and CorrelationId. The
engine and adapter do not own the injected persistence, clock, or policy
services.

## Composition handoff: exact endpoint and security contracts

The host composition entry points are:

```csharp
// XRegistry.Server
public RegistryEngine(RegistryEngineOptions options,
    IRegistryPersistence persistence, IRegistryAuthorizationPolicy authorization);

public ValueTask<RegistryResult> ExecuteAsync(RegistryOperation operation,
    RegistryOperationContext context, CancellationToken cancellationToken = default);

// XRegistry.AspNetCore.RegistryEndpointRouteBuilderExtensions
public static IEndpointConventionBuilder MapXRegistry(
    this IEndpointRouteBuilder endpoints, RegistryEngine engine,
    RegistryHttpOptions? options = null);
```

`MapXRegistry` takes the concrete local `RegistryEngine`, not a federation
resolver or an arbitrary `IRegistryEngine`. It maps an explicit RequestDelegate;
it does not start Kestrel or own host, authentication, persistence, or shutdown.
The FileServer host supplies those lifetimes. Federation selection, origin
retention and bridge-specific read/write composition remain in the parent's
bridge layer; this module has no Federation project dependency.

`RegistryEngineOptions` requires RegistryId, PublicRoot and Model.
`AllowAnonymousReads` and `AllowCapabilityUpdates` default to false. Optional hooks are ResourceValidator,
DocumentReferencePolicy and ObligationValidator; clock, limits, model compilation
and discovery configuration are also explicit. `RegistryHttpOptions` contains
MountPath (default empty), CallerMapper (default null, meaning HttpContext.User),
AuthenticationChallenge, MaxErrorBytes (32768), and RequestTimeout (30 seconds).
MountPath is local routing; PublicRoot alone controls advertised URLs.

```csharp
public interface IRegistryAuthorizationPolicy
{
    ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access,
        RegistryPath path, CancellationToken cancellationToken = default);
}
```

`RegistryAccess` values are Read, Create, Update, Delete, UpdateModel,
UpdateCapabilities, ReadEvents and AcknowledgeEvents. A false policy result
becomes `forbidden`; missing required authentication becomes `unauthorized`.
Authentication middleware must run before the endpoint. CallerMapper must return
a trusted principal, never a principal inferred from arbitrary client headers.

The initial create/update request gate uses Update on its requested route;
deletion uses Delete; `/modelsource` uses UpdateModel; `/capabilities` uses
UpdateCapabilities. The engine then checks Create/Update/Delete for each
affected entity, including implicit parents, parent membership-counter updates,
Meta, ancestry repair, pruning and cascades. A policy must permit both the
request gate and those effects. In particular, creating a Group can require
Update on `/`. This interface supplies caller/access/path, not current entity
contents or a distinction between direct and automatic parent Update effects;
hosts needing that distinction must also apply request-level authorization.

## Implemented behavior

| Routes | Supported operations |
| --- | --- |
| `/` | GET, HEAD, OPTIONS, PUT, PATCH, POST of Group maps |
| `/.xregistry` | GET, HEAD, OPTIONS; explicit Registry-based discovery |
| `/model`, `/capabilitiesoffered`, `/export` | GET, HEAD, OPTIONS, subject to enabled availability |
| `/capabilities` | GET, HEAD, OPTIONS; PUT/PATCH when the host opts into authorized capability updates |
| `/modelsource` | GET, HEAD, OPTIONS, PUT; also mutable through root modelsource |
| Model-defined Group/Resource/Version collections | GET, HEAD, OPTIONS, PATCH, POST, DELETE |
| Group and Resource entities | GET, HEAD, OPTIONS, PUT, PATCH, POST, DELETE |
| Meta entities | GET, HEAD, OPTIONS, PUT, PATCH; no DELETE |
| Version entities | GET, HEAD, OPTIONS, PUT, PATCH, DELETE |

PATCH on a document-bearing Resource/Version requires `$details`; metadata-only
types accept it with or without the suffix and never emit the suffix. OPTIONS
and 405 responses report the applicable Allow list. Successful creation emits
Location and appropriate Version Content-Location. All mapped responses include
the trusted `xregistry-root` Link. Writes return the correlation ID used by
their atomic event batch.

Both discovery mechanisms are specified by the pinned Core specification:

| Mechanism | Placement with a Registry mounted at `/catalog` |
| --- | --- |
| Registry-based | `/catalog/.xregistry` |
| Optional host-based | `/.well-known/xregistry`, at the origin root |

Host-based discovery is a standardized optional host concern, not an invented
endpoint. It must not be mounted as `/catalog/.well-known/xregistry`.
`MapXRegistry` implements Registry-based discovery only; the parent host may
separately provide the origin-wide endpoint. Both responses use an array of
absolute URL strings, for example:

```json
{"registries":["https://registry.example/catalog"]}
```

The braces shown for the list in the pinned HTTP discovery example contradict
the Core definition. They are not an alternative wire format: neither JSON
serialization nor parsing is relaxed to accept them. The specification example
correction and its successor/regression artifacts are verified through the
[specification workflow](spec-feedback.md).

Nested updates, implicit parents, retention/cascade deletion and all affected
epochs are published in one expected-generation batch. Sibling ID uniqueness is
case-insensitive while lookup remains case-sensitive. Equivalent escaped IDs
address the same canonical persisted entity. Epoch guards use exact
`BigInteger` arithmetic: creation ignores a submitted guard, zero is a real
guard, null/absence disables the guard, and every accepted touch advances the
entity. Descendant updates do not turn the root epoch into a global generation.
Explicit timestamps, equal/null modified-at touches and UTC normalization are
preserved.

All four standard Version modes are implemented: manual, createdat, modifiedat,
and semver. This includes generated monotonically advancing IDs, sticky/default
selection, default-only updates without touching Versions, retention, ancestry
repair, circular-reference rejection, and single-root enforcement. Nested
Version representations override the competing Resource default projection.
Core validates declared/wildcard metadata; the engine enforces Group constraints,
instance constraint narrowing, cross-Version matching, and existing-data
compliance on model changes.

System `name`, `format`, `documentation` and `icon` values cannot be empty.
Ordinary empty descriptions and model-admitted extension strings remain
allowed. Scalar metadata counts the attribute name plus its decoded UTF-8 value
against the inclusive 4096-byte limit; JSON escaping and HTTP percent encoding
do not change that value limit. Inline Documents, `any` values and unnamed
collection items retain their separate bounds.

Deprecation `removal` cannot precede `effective`. A future removal promise
blocks direct deletion, parent cascades and deletion of a Resource's last
Version until the promised instant, with atomic rollback. The promise remains
mutable. Timestamp comparisons retain full fractional precision, including
values more precise than the host clock.

Changes only to readonly format/compatibility validation flags or reasons are
persisted without marking the Version updated: its epoch, modified time,
Version ordering and update events remain unchanged. User-initiated metadata
or Document changes still follow the usual update rules.

Same-actual-type cross references use one hop only. Imported Resource definition
identity survives persistence/restart; structurally similar independent types
are not interchangeable. Inaccessible or cross-referenced targets produce the
prescribed dangling shape. Alias URLs and IDs remain local to the source.
Converting an alias back to a normal Resource creates fresh Version metadata.

Referring Group `enum` and `equals` constraints now apply to every retained
Version of an xref target, including target updates and later creation behind a
dangling reference. Validation uses the final atomic candidate after Version
ordering and retention; it does not apply the referring Group's defaults or
read Document bytes. Group-instance and model changes recheck affected aliases.
Forward checks require read authorization for the target Resource, Meta and
all its Versions before evaluating values; reverse failures identify the target
being changed rather than disclosing an unreadable alias's identity.

This check reuses Core's bounded `ValidateGroupConstraints` operation.
Relevant Group and Resource metadata collections are inspected through the
request-local index, once per collection; unrelated Version subtrees and the
global persistence record enumerator are not used. Reverse lookup is a bounded
scan of relevant constrained collections, not a claimed constant-time
persistent reverse index. Existing operation/working-set limits still apply.

Supported flags are `binary`, `collections`, `doc`, `epoch`, `filter`, `ignore`,
`inline`, `setdefaultversionid`, `sort`, and `specversion`. Inline supports model-relative
collection paths and terminal wildcards. Export applies document-view pointers
and omits duplicated default-Version attributes. Doc Version output omits
`formatvalidated`, `compatibilityvalidated` and their dependent reason fields;
ordinary API metadata retains false validation flags and their reasons.
Collections-only Registry/Group output contains only collection maps (and any
permitted collection helpers), not the top entity's `shortself`. Nested entities
still follow the enabled shortself capability, while Doc omits their short URLs.
Opaque Document/extension fields with these names are not stripped.
Ignore supports capabilities,
defaultversionid, defaultversionsticky, epoch, id, modelsource and readonly,
subject to the enabled flags/ignores choices.

Unknown or capability-disabled query flags are ignored. `/export` still applies
its implicit `doc&inline=*,capabilities,modelsource` projection when explicit
use of those flags is disabled. Only an enabled explicit `inline` overrides the
export default. An enabled bare or empty `inline` means `*`, so it includes the
hierarchy but not configuration unless configuration paths are also named.

Owner POST responses use the same normalized collection input as mutation
processing. Root `capabilities` and `modelsource` selected by `ignore` neither
change configuration nor reappear as response collections, even when their
ignored values have invalid types. A POST containing only ignored fields
returns `{}` without changing entity metadata or configuration; it still
requires write authentication and authorization. Unignored owner attributes,
invalid child entities, and denied descendant mutations reject the entire
request before publication.

Raw Document bytes survive JSON-looking binary data, invalid UTF-8 and
present-empty bodies. Inline output uses the model typemap for JSON/string
versus base64, with exact-byte fallback and explicit binary override. Metadata
and Document bodies are never selected by guessing Content-Type. Scalar/map
HTTP headers use the Core header encoding; unknown or unrepresentable metadata
headers fail explicitly before publication.

`available` capability entries are objects containing `mutable`, not strings.
Offered capabilities describe implemented choices and injected format validators;
enabled capabilities are persisted when edited. There is no advertised federation producer resolution:
neither the draft's `federation.resolution` capability nor a nonstandard
`resolutionowner` field is invented.

## Mutable capability profiles

Public capability editing is a trusted host opt-in:

```csharp
var options = new RegistryEngineOptions
{
    RegistryId = "registry",
    PublicRoot = new Uri("https://registry.example/catalog"),
    Model = model,
    AllowCapabilityUpdates = true
};
```

The constructor/persistence/MapXRegistry contracts remain compatible.
Authentication and `IRegistryAuthorizationPolicy` are still required for every
write. Neither capability JSON nor the `mutable` legacy list can enable the
host opt-in, anonymous writes, change principals, or weaken authorization.
With the default false option, capability metadata remains read-only and root
readonly capability input retains its prior ignored-input behavior.

When opted in, PUT/PATCH `/capabilities` and root `capabilities` updates are
implemented. PATCH replaces each supplied top-level capability in full, not
nested pieces. PUT resets omitted capabilities to the server defaults; root
`capabilities: null` resets the full profile. A lone `"*"` in a string list
expands offered choices. Mixing it with other values, case-insensitive
duplicates, unsupported choices, wrong JSON types and unknown capabilities fail
explicitly.

`available` remains a map of objects containing `mutable`. Capabilities,
entities and model must remain available. Model, export and capabilitiesoffered
are immutable; capability-edit mutability is fixed by the host opt-in. Entities
and modelsource can be restricted to read-only, and optional metadata surfaces
can be disabled. Enabled availability governs direct and inline retrieval;
disabled surfaces return `not_available`. Mutation attempts on enabled but
read-only metadata reject. Root capability/modelsource-only edits can remain
permitted when ordinary Registry entity metadata is read-only.

The legacy top-level `mutable` array is retained as the fixed empty choice,
not a second authority over the `available.*.mutable` controls. Shortself and
pagination offer false/true choices (shortself only false if PublicRoot contains
a literal dollar sign). Version modes retain manual and must cover the final
model. Format and compatibility values must be offered, and compatibility
format keys must select enabled formats. Compatibility wildcard keys are
expanded against the concrete enabled formats; overlapping selections must
agree.

Capabilities are processed before modelsource and entity data in a combined
root write. Request flag/ignore interpretation and permission to process
capabilities/modelsource use the pre-request profile; new format/compatibility
and entity-mutability restrictions govern the resulting data operation. Final
Version-mode validation uses the complete combined model candidate, not an
intermediate model. There is no configuration publication before all metadata,
response, authorization and persistence checks complete.

Successful capability writes touch the Registry epoch/time and record one
correlated Registry/capabilities interaction through the Core accumulator.
The complete enabled profile is stored in opaque `$capabilities` metadata in
the same batch as entity/model/outbox/correlation changes. Restart validates it
against the current host's offered choices; an incompatible stored profile is
an explicit infrastructure error, not a silent reset.

Disabled query flags retain the binding's ordinary unsupported-flag behavior:
they are ignored. When pagination is disabled, `limit` is ignored and a bounded
whole collection is returned, or `too_large` if it cannot fit. Disabling the
epoch query flag does not disable body epoch preconditions. Enabled ignore
choices govern the `ignore` parameter; unsupported choices still reject.

Resource validation receives additive EnabledFormats/EnabledCompatibilities
context properties. The built-in adapter honors them, and the engine prevents
disabled checks from being reported as successful. Definitively invalid
enabled formats/compatibility still reject; disabled checks are unchecked with
reasons, or strict rejections. Previously stored validation observations are not
rewritten merely because a check is later disabled; subsequent writes use the
current profile.

## Short entity URLs and readonly ignore

Enabling `shortself` atomically prepares engine-owned aliases. The reserved
Registry-relative namespace is `/~/`: the Registry token is `0`, while Groups
and Resources receive opaque 96-bit identifiers (16 Base64url characters).
Meta and Version URLs derive from their Resource alias using `/meta` and
`/versions/<escaped-id>`. This also preserves local identity for one-hop xrefs.
No model-defined collection name can collide with the `~` namespace.

The additive navigation seam is:

```csharp
ValueTask<RegistryPath> ResolvePathAsync(RegistryAction action,
    string escapedPath, RegistryOperationContext context,
    CancellationToken cancellationToken = default);
```

The HTTP adapter calls it before model-aware request decoding. It translates
only the reserved short path, validates the resulting canonical model route,
and enforces the normal caller policy. `$details` and query flags have the
same meaning as on the canonical route. Short URLs always use trusted PublicRoot,
contain no dollar sign, and never replace canonical self/xid/Location metadata.
Short URLs can be longer than the Registry root or an already short entity path;
the specification's preference for shorter URLs is not an unconditional size
guarantee.

Shortself appears only on enabled API entity representations and scalar
Document headers, not document-view entity/Meta/Version metadata. Domain URL
attributes and Document contents are not recursively rewritten, including
domain fields also named `shortself`. The short identifiers survive disable/
re-enable and restart; already issued aliases remain routable while hidden.
Deleting a Group/Resource removes its alias. Recreating it receives a new token;
Version-ID reuse under a surviving Resource follows the Core-permitted reuse
semantics.

Opaque entity envelopes retain `shortid`, and `$short/<token>` records hold
canonical XIDs. Alias preparation/storage is atomic with the capability/data
write. Backfilling identifiers does not touch Group/Version epochs or invent
entity-update events just for enabling a representation. The adapter must
preserve these opaque records and native Document-preservation semantics.

Frozen pages are never rewritten after a shortself/profile change. Cursors
created in a capability-mutable or persisted-profile context check the current
profile revision and reject as `bad_cursor` if it changed, including changes
from another engine over the same persistence. These continuations perform a
bounded configuration snapshot read; ordinary immutable-default cursors retain
their backend-free behavior. Hosts sharing publicly mutable configuration must
use compatible capability-update configuration.

`ignore=readonly` removes submitted readonly Resources from valid collection/
nested operations; the corresponding collection mutation response omits them.
It does not grant permission to clear readonly, modify their Versions, or evade
authorization. JSON collection shape and keys still validate. Once a readonly
Resource is omitted, its payload and guards are not processed; guards on every
remaining writable entity still apply unless independently ignored by a
supported flag. Readonly state remains server-controlled.

A single readonly Resource, Meta, Version or Versions-collection write cannot
remove its only target and therefore returns `bad_flag`. A delete cascade that
cannot retain an ignored readonly child also returns `bad_flag`. An absent-body
Resource-collection delete skips readonly members and deletes permitted members.
Indirect constraint/lifecycle effects cannot modify readonly Resource data.
Repeated/comma-separated ignore values and empty/valueless/`*` forms retain the
Core semantics, expanding only currently enabled ignore choices.

Configuration parsing remains bounded by RegistryLimits JSON bytes/depth/nodes
(4 MiB/64/100,000 by default), and capability validation and alias backfill use
the 10,000-operation default work ceiling. Short paths are capped at 8,192
characters. There is at most one alias record per Registry, Group and Resource;
Meta/Version aliases do not require per-view records. Existing persistence,
header, response, concurrency, cancellation and query limits apply. Quota or
preparation failure rolls back configuration and aliases instead of exposing
partial state.

## Request-local collection scaling

Collection membership is indexed once per request and pinned snapshot generation.
The index retains exact keys, case-insensitive decoded sibling IDs and snapshot
records; new members and deletions update that request-local state immediately.
Ordered entity lists are materialized lazily and invalidated on membership
changes. Counting or checking an ID does not decode every sibling's metadata,
and discovered records are reused for subsequent point materialization.
No cross-request cache or persistence-interface change is involved.

This removes repeated whole-collection enumeration from `New`, generated-ID
collision checks and repeated `Children` calls. Deleted Version lookup uses the
existing exact-key entity index instead of scanning all staged entities.
Retention, deleting the last Version, recreating a Resource in a later request
and case-insensitive sibling conflicts continue to use the current candidate
membership, not a stale snapshot list.

A Group metadata write schedules untouched Resources for full validation only
when Group constraints change, or when a modeled `equals` dependency changes in
the completed Group metadata. Explicit
constraint changes conservatively recheck the Group's Resource collections;
unchanged constraints with changed equals values select their affected types.
Model updates still revalidate the model's affected data. Last-Version deletion
completes the newly affected Group and checks dependent constraints before
publication. Indirect defaults remain subject to Version authorization and
Resource readonly checks.

Manual ancestry validation checks each ancestor path once rather than repeating
every ancestor prefix for every Version. Cycles, missing ancestors, deleted
ancestor repair and single-root rules remain enforced. A singleton Version set
has no cross-Version comparison to perform; multi-Version comparisons retain
their checks and charge actual comparison work.

**No limits were raised.** The default entity-operation ceiling remains 10,000
and working-set ceiling 64 MiB. Collection discovery and materialization,
index lookups, lifecycle traversal, constraint checks and cross-Version
comparisons remain charged. Retained record metadata, index keys/bookkeeping
and staged entity data count toward working-set limits. Genuine expensive
operations still fail atomically as the qualified `operation_limit`; indexes do not provide
unlimited capacity.

Public engine regressions seed 1,000 metadata Resources in 40 batches of 25,
then perform two-Resource atomic root-nested writes and point Resource/Meta
updates under these unchanged defaults. They verify exact counts, local epochs,
single-generation commits, rollback and case-insensitive uniqueness. An actual
native net10 win-x64 Kestrel smoke covers the same 1,000-Resource batch/nested
write boundary, rollback and sibling identity failures. These are bounded
functional/scaling checks, not inferred throughput or latency measurements.
The prior 100/250-Resource developer measurements are not a permanent capacity
claim; independent native performance measurements are owned by the performance
workstream.

## Bounded filter, sort and pagination

Filtering and sorting operate on authorized logical metadata before response
projection. They do not select which entities a write mutates. A failed
write-response preparation still rejects the complete mutation as before.
The default capability profile includes `filter`, `sort`, and pagination.
When capability editing is enabled, runtime behavior follows the enabled values,
not the larger offered choice set.

Within one `filter` value, comma-separated expressions are ANDed. Repeated
`filter` parameters are ORed. AND intersects matching subtrees, rather than
accepting different sibling witnesses; OR unions them without duplicate IDs.
Matching a parent includes its descendants, while matching only a deeper entity
includes its ancestor path and that entity's descendants. Filtering does not
imply inlining. A failed direct entity predicate returns `not_found`; a nested
filter can leave an existing root with empty child collections.

Paths use the frozen Core dot notation, relative to the request target.
Registry collection IDs are elided from the path; use the modeled ID attribute
to select one. Metadata supports property paths, quoted map keys, nonnegative
array indexes, `.*` inside objects/maps and `[*]` inside arrays. Property names
remain case-sensitive. Conditional definitions use the active modeled value
type, not just the JSON token kind.

Literal values are not SQL literals or JSON strings: quotes in a value are
literal characters. Missing/null checks, empty-string equality, boolean
case-sensitive literals, the two inequality spellings and relative comparisons
follow Core. Numeric comparison uses exact significand/exponent arithmetic,
without floating-point conversion or expansion of huge powers. Timestamps are
normalized through Core before comparison. Strings use deterministic
ordinal-ignore-case comparison; this meets case insensitivity but deliberately
does not implement the recommended en-US collation, so it also works in native
invariant-globalization hosts. Equality/inequality string wildcards support
escaped literal `\*`. Relative comparisons with wildcards or null are rejected.

Sort accepts one scalar model-known path and optional `=asc`/`=desc`. It cannot
cross another Registry collection or select multiple values through a wildcard.
Missing values sort lowest. The same-direction ID comparison breaks ties.
Default paginated order is ID ascending. Complex/unknown sort projections fail
as `bad_sort`, and sorting a non-collection result fails as `sort_noncollection`.

Filtered collection counts reflect the same authorized subset as their maps.
Empty API collection links carry `filter=excludeall`; nonempty filtered links
encode equivalent ID/subtree selections. A link that cannot fit the configured
query budget fails explicitly rather than silently dropping the filter. In
document view, inlined collections use their valid JSON Pointers instead.
Default-Version URLs remain absolute when filtering excludes that Version
from the document.

### Shared executable query module

The grammar, exact comparisons, subtree evaluation, logical fact construction
and filtered collection-link construction now live once in Core,
`XRegistry.Queries`. Core has no Server, ASP.NET Core, persistence, SQL or
Federation dependency. `RegistryEngine` uses its `Request.QuerySource` adapter;
the former Server grammar, evaluator and renderer-based fact cache are removed.
The public entry point is executable over detached logical data, not just a
limits/page DTO or an adapter pretending to be a local persistence store:

```csharp
using XRegistry.Queries;

using var budget = new RegistryQueryBudget(
    new RegistryQueryEvaluationLimits(), timeProvider, cancellationToken);
var selection = await RegistryQuery.EvaluateAsync(model, source,
    new RegistryQueryRequest(collectionPath, trustedPublicRoot)
    {
        Filters = ["rank>9007199254740992,meta.readonly=true"],
        Sort = "rank=desc",
        DefaultSortById = true
    }, budget);
```

`Filters` contains decoded, ordered HTTP-style parameter values: one string
can contain comma-AND expressions, and separate strings are OR branches.
`Sort` is one effective scalar sort value. The host still owns flag parsing,
capability gating, unknown-flag behavior and representation selection. The
shared module does not introduce SQL/JSONPath syntax or a second filter grammar.

```csharp
public interface IRegistryQuerySource
{
    ValueTask<RegistryQueryEntity?> ReadEntityAsync(RegistryPath path,
        bool includeDocument, CancellationToken cancellationToken = default);

    IAsyncEnumerable<RegistryPath> GetChildrenAsync(RegistryPath collection,
        CancellationToken cancellationToken = default);
}
```

The source presents a **complete, authorized, already-shadowed logical view**
relative to the target and queried facts. The host establishes that the target
exists and is accessible before evaluation; `excludeall` can short-circuit
without reading the source. Unneeded ancestors and configuration need not be
materialized. Collection enumeration is complete, not an upstream first page.
It returns canonicalizable direct entity paths with case-insensitively unique
decoded IDs. Partial/unavailable input is an error, never an empty collection.

`RegistryQueryEntity(RegistryJson metadata)` carries one raw plane:
Registry/Group/Version paths return their own normalized attributes;
**Resource and Meta paths both return Resource Meta attributes**, not a
default-Version projection. Required `defaultversionid` identifies the Version
to read through that same source. Version input must carry its matching
`versionid`; Resource and separately requested Meta defaults must agree.
`/model`, `/modelsource` and `/capabilities` return the actual authorized plane
when queried. A null result means authorization-hidden, not missing data.
Metadata values must already satisfy their model definitions; shared queries
enforce JSON budgets and required fact consistency, not write validation or
external metadata obligations.

The optional entity properties are `ReadOnlyMemory<byte>? Document`,
`string? ShortSelf`, `string? DefaultVersionId` and
`bool IsDanglingCrossReference`. `includeDocument`
requests exact Version bytes, including present-empty bytes, unless the
modeled Document URL is present. Missing bytes without that URL fail
explicitly. A URL is never fetched by the query module. Document memory is
borrowed and must remain stable through evaluation; metadata is immutable
owned `RegistryJson`. ShortSelf is trusted, enabled host navigation, not a
domain field to rewrite. Dangling aliases must explicitly identify themselves
and retain their xref; querying their Meta still requires Meta authorization.

For Version entries, DefaultVersionId can supply raw selected-unit context
without adding permission to read the Resource projection or its Meta.
The local adapter supplies this context: an authorized direct Version query
must not become forbidden merely because the Resource/Meta route is hidden.
Core derives `isdefault` and never exposes the context ID as a Version
attribute. It must agree with any already-read Resource and later Document
projection. Without it, Version facts require the Resource-plane lookup.
It does not authorize a separate Resource or Meta query.

The shared fact builder constructs default-Version Resource attributes,
separate Meta, `isdefault`, API self/xid, modeled collection counts/URLs,
default-Version URLs and conditional JSON/string/base64 Document facts. Facts
are independent of response `doc`, `binary`, `inline`, short-path routing and
paging. Arbitrary domain URL strings and Document contents are not traversed
as Registry navigation. A scalar comparison against a runtime complex value
fails as `bad_filter`, including heterogeneous Document projections; it does
not silently discard that entity.

`RegistryQuerySelection.RootPaths` is an immutable ordered collection of
logical identities: direct collection members, or the retained entity target.
Callers map these identities back to their existing Resource units, preserving
origin and ownership. `Includes(path)` selects nested members and retains an
entity target even when a nested filter leaves no children.
`CollectionUrl(collection, visibleCount)` builds a bounded trusted-root API
link for the same subset; its count must describe the projected authorized
members. A contradictory count cannot silently generate an unfiltered URL.
These helpers do not imply inline, authorize access, or select mutation inputs.
They require the budget to remain alive through projection. Selection retains
no source, raw facts, Documents or backend leases.

The second concrete adapter is
`ProducerRegistryView.QuerySource : IRegistryQuerySource`, in
`XRegistry.Federation/ProducerRegistryView.Query.cs`. Its public entry is
`ProducerRegistryView.ReadQueryAsync(RegistryQueryRequest request,
ProducerViewOptions options, RegistryQueryBudget budget)`. It establishes
complete shadowed membership before yielding and retains the **previously
selected whole Resource origin** for Meta, Versions, Documents and source-local
aliases. Presentation consumes the same RootPaths, Includes and CollectionUrl;
there is no copied grammar, fact evaluator or fake local persistence adapter.

Complete HTTP projection and opaque bridge cursors remain bridge-owned.
Bridge paging freezes encoded pages, retains source-local authorization
dependencies and caller/root/path/model/query/representation bindings, and
rechecks retained-read authorization and credential context without reopening
sources on replay. Local engine cursor ownership and authorization semantics
remain unchanged.

The current evidence records 139 passing managed tests, 139 passing
Windows-native tests and six principal native HTTP cases, including
`FilterAfterWholeResourceShadowNeverRecoversLowerPriorityVersions`,
`MetaAndDefaultVersionFactsUseSharedCorePlanesAndExactNumericOrdering` and
`PagesFreezeSelectedBytesAndDoNotReopenSources`. These runs include escaped-XID/shared-query integration,
DefaultVersionId context, owner-before-members checks and alias Meta
authorization and the stricter alias-target Meta identity guard. The
principal-host evidence is
`artifacts/bridge-build/host-smoke/95c081ce060246cf8727817bf1bb3350/evidence.json`.
Linux bridge execution is not included in this source-matched evidence.

Core defaults, separate from retained-page quotas:

| `RegistryQueryEvaluationLimits` member | Default |
| --- | ---: |
| MaxFilterExpressions / MaxPathSegments | 128 / 32 |
| MaxQueryCharacters / MaxWork | 8,192 / 250,000 |
| MaxEntities / MaxFactBytes | 4,096 projections / 8 MiB |
| MaxSourceReads / MaxCollectionMembers | 8,192 / 16,384 |
| MaxSourceBytes / MaxDocumentBytes | 16 MiB cumulative / 8 MiB per Document |
| MaxDuration | 5 seconds |
| Json | RegistryJsonLimits defaults: 4 MiB, depth 64, 100,000 nodes |

Counters are cumulative when a budget is reused across evaluations.
`Spend(long)` and `RunAsync` let hosts share the work/deadline with policy and
response preparation. Core source calls receive deadline-linked cancellation
and are awaited to completion before disposing their enumerators; adapters
must cooperate, and arbitrary managed code that ignores cancellation cannot
be forcibly terminated. No source worker/background task or retained cache is
started. Source adapters must also bound their own I/O, buffering and leases
before yielding data.

The local adapter propagates that same deadline to Document lease acquisition
and asynchronous reads, both for query facts and frozen-page projection.
A timed-out read disposes its lease before returning `server_busy`; caller
cancellation remains cancellation rather than an invented empty result.

The local adapter maps existing `RegistryQueryLimits` evaluation fields to
Core and maps source-read/member ceilings to `RegistryLimits.MaxEntityOperations`
(10,000), source bytes to `MaxWorkingSetBytes` (64 MiB), and Document/query/JSON
limits to their existing RegistryLimits values. These are additional finite
ceilings; page/cursor quotas below remain unchanged. Core returns explicit
`query_source_incomplete` or `invalid_query_source` diagnostics for absent or
contradictory required inputs, not success-shaped fallbacks.

### Paging interface and HTTP representation

Existing constructors, `IRegistryPersistence`, `Find`, `GetChildren` and
`MapXRegistry` are unchanged. Additive interfaces are:

```csharp
// RegistryEngineOptions
public RegistryQueryLimits QueryLimits { get; init; } = new();

// RegistryResult
public RegistryPageInfo? Page { get; }

// RegistryPageInfo
public ulong TotalCount { get; }
public DateTimeOffset? ExpiresAt { get; }
public IReadOnlyList<RegistryPageLink> Links { get; }

public sealed record RegistryPageLink(string Relation, Uri Target);
```

An additive `DescribeAsync(action, path, context, parameters, ct)` overload lets
the HTTP adapter recognize retained pages without decoding them under a later
model revision. Immutable-default continuations remain backend-free; mutable or
persisted capability contexts perform the bounded revision check described above.
The original overload remains.

`limit` is accepted only on initial GET/HEAD requests to a Group, Resource or
Version collection. It must be a positive UInt64; zero, signs, decimal/exponent
forms, overflow and repeated values are rejected. The server may return fewer
records than requested because its configured record/byte limits are additional
upper bounds.

The initial result set is filtered, sorted and authorization-trimmed before its
projected JSON is frozen. Later writes do not alter retained metadata, Documents,
epochs, counts or representation. Cursor state contains bounded encoded pages
and authorization paths, not backend snapshots/Document leases or an unbounded
whole-Registry cache. Single-page and empty results retain no cursor state.

Multi-page responses contain trusted-PublicRoot opaque `next`, `prev`, `first`
and `last` Link URLs where applicable, each with the same exact `count`.
`next` always advances and is absent on the final page; `prev` is absent on the
first. Repeating first/prev navigation is legal per the pagination specification;
the next chain never cycles. All collection pages additionally include
`xRegistry-count`, including single-page/empty responses that have no page
links. This convenience header does not replace the standard Link count
attributes. Retained pages include a fixed HTTP `Expires` date, matching the
HTTP-date examples in the pinned binding, and `Cache-Control: private, no-store`.

Continuation URLs have only `cursor=<random-token>`. Tokens are 256-bit random
references and contain no plaintext query, caller credentials or record IDs.
Clients must follow the returned URL exactly without reapplying filter, sort,
limit or representation flags. Extra/repeated/modified continuation parameters,
malformed tokens, other roots/collections and foreign engine instances fail
explicitly. URI construction uses PublicRoot and canonical escaped IDs, never
request Host.

Caller binding hashes the trusted principal's identities, authentication
configuration, claims and claim properties. No arbitrary request headers are
identity inputs. Anonymous principals share the anonymous security context;
hosts needing distinct anonymous-user cursors must provide stable distinguishing
claims through trusted authentication/mapping.

Every continuation rechecks current read authorization for the retained
dependency set, including queried/projected descendants and xref targets.
Revocation invalidates the whole cursor with `forbidden`; it does not silently
drop records and change the frozen count. Dependencies are conservative and
may include authorized entities examined while filtering even if not ultimately
returned. A changed principal cannot consume another security context's cursor.

### Numerical defaults and failure behavior

| `RegistryQueryLimits` member | Default |
| --- | ---: |
| MaxFilterExpressions | 128 |
| MaxPathSegments | 32 |
| MaxWork | 250,000 traversal/comparison units |
| MaxEntities | 4,096 cached entity/attribute fact projections |
| MaxFactBytes | 8 MiB encoded fact data |
| MaxDuration | 5 seconds |
| MaxPageRecords | 128 |
| MaxPageBytes | 1 MiB |
| MaxCursorRecords | 2,048 records per frozen set |
| MaxCursorBytes | 8 MiB charged retention per set |
| MaxCursors | 32 retained sets per engine |
| MaxCursorTokens | 4,096 page tokens across the engine |
| MaxTotalCursorRecords | 16,384 |
| MaxTotalCursorBytes | 32 MiB charged retention |
| MaxAuthorizationPaths | 8,192 per set |
| CursorLifetime | 2 minutes, never sliding |

Encoded page bytes, token/dependency strings and fixed bookkeeping charges count
toward cursor quotas. Existing RegistryLimits JSON, response, working-set,
query-length (8,192 characters by default) and concurrency budgets also apply;
the tighter limit wins. Each page is validated before cursor admission. Work or
representation exhaustion returns `too_large`; store capacity returns
`server_busy` without evicting an unexpired set. Failed/canceled response
preparation consumes no cursor capacity.

Expiry checks both the fixed UTC expiry and monotonic elapsed lifetime, so wall
clock rollback does not extend retention. Cleanup is opportunistic on cursor
access/admission; there is no background eviction task and no storage lease to
keep alive. Expired tracked cursors return `cursor_expired` (HTTP 410); tokens
that are no longer retained, unknown or forged return `bad_cursor` (HTTP 400).
Cursors are process/engine-local and do not survive engine restart. Multi-host
deployments must route continuations to the owning engine instance.

Query authorization and response-preparation callbacks receive deadline-linked
cancellation. They must cooperate with cancellation; the engine cannot terminate
arbitrary host code that ignores it. MaxPathSegments has a hard ceiling of 128,
MaxDuration one minute, and CursorLifetime fifteen minutes; configuration must
remain finite and positive.

This is Core dot notation, not general JSONPath: recursive descent, slices,
negative indexes, embedded predicates, wildcard Registry collection names and
unnamed entity-root attribute wildcard selectors are rejected. Quoted property
keys support literal Unicode and escaped quotes/backslashes, not additional
JSONPath escape syntaxes. Administrative maps/discovery are not entity query
collections. Unknown unsupported flags remain ignored on ordinary requests;
opaque continuation URLs are deliberately stricter because their query and
representation are fixed.

## Persistence handoff

`XRegistry.Server` does not reference any durable store. The completed
`XRegistry.Storage.File.LocalRegistryPersistence` adapter implements this
contract over `LocalFileStore`, keeping the dependency direction
Storage.File-to-Server:

```csharp
public interface IRegistryPersistence
{
    bool IsReadOnly { get; }
    ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default);
    ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration,
        IReadOnlyList<RegistryMutation> mutations, CancellationToken cancellationToken = default);
}
```

`IRegistrySnapshot : IDisposable` exposes `Generation`, `Find(key)`,
`GetChildren(collectionKey)`, `EnumerateRecords()` and
`OpenDocument(key, cancellationToken)`. Metadata is immutable `RegistryJson`.
`RegistryRecord(key, metadata, hasDocument)` preserves the distinction between
no Document and a present empty Document. `GetChildren` returns only immediate
ordinal-key children of `collectionKey + "/"`. Point reads must not copy the
whole Registry; administrative enumeration is separately budgeted by the engine.

```csharp
public interface IRegistrySnapshot : IDisposable
{
    long Generation { get; }
    RegistryRecord? Find(string key);
    IEnumerable<RegistryRecord> GetChildren(string collectionKey);
    IEnumerable<RegistryRecord> EnumerateRecords();
    Stream OpenDocument(string key, CancellationToken cancellationToken = default);
}

public interface IRegistryCommit : IDisposable
{
    ValueTask<long> CommitAsync(CancellationToken cancellationToken = default);
}
```

`RegistryMutation.Put(key, metadata)` preserves existing content.
`PutWithoutDocument` removes content; `PutDocument(key, metadata, stream)`
replaces it with the exact remaining stream bytes, including zero bytes.
`Delete(key)` deletes metadata and content. Each batch contains unique keys.
Streams are caller-owned; preparation must finish reading them before returning.
The candidate owns its staged content. `LocalRegistryPersistence` implements
Preserve through `StorageMutation.PutPreservingDocument`, retaining the native
immutable Document reference without opening or restaging its bytes.

`IRegistryCommit : IDisposable` exposes
`ValueTask<long> CommitAsync(CancellationToken cancellationToken = default)`.
Preparation does not publish. Commit rechecks the generation and atomically
publishes every record or none. Dispose abandons an uncommitted candidate.
`RegistryConcurrencyException` means definitely not committed;
`RegistryCommitOutcomeUnknownException` means publication is uncertain and the
caller must not automatically retry. Cancellation must not report rollback
after publication. Stream leases outlive snapshot disposal.

Keys are opaque, never filesystem paths. Engine entity keys are canonicalized
from decoded IDs, not raw URL spelling. Internal keys beginning with `$` are
reserved for model/configuration and atomic event records. Persistence must not
interpret the JSON schema or conflate its generation with entity epochs.

`InMemoryRegistryPersistence(maxRecords, maxBytes, maxDocumentBytes)` is a finite,
structurally shared transient implementation. It provides atomic publication,
not crash durability. Old snapshots pin their old immutable state until callers
release them.

Engine records have an opaque `attributes` envelope. A Resource record stores
Meta attributes plus a `nextversion` counter; individual Version records own
their Documents. `$modelsource` stores the original source and a frozen compiled
source with reconstructed actual-identity imports. Restart compiles that frozen
source without external resolution, rather than refetching mutable includes.
`$events/<correlationid>` records join the same batch as entity mutations.
`$correlations/<lowercase-compact-guid>` records reserve generated interaction
IDs for the Registry lifetime. These small records contain the correlation ID,
are committed with the entity/outbox changes, and are not deleted when delivery
is acknowledged or Groups are removed. Preserve them in backups and adapters.
They consume storage quota; exhaustion fails explicitly rather than forgetting
uniqueness. Stores without lifetime correlation reservation records require
their historic correlation IDs to be retained or backfilled before claiming
lifetime uniqueness for existing history.
The adapter must preserve these opaque records rather than translating them
into exposed HTTP representations.

The adapter is constructed as:

```csharp
public LocalRegistryPersistence(
    LocalFileStore store, bool ownsStore = false, int maxSnapshots = 64);
```

It implements `IRegistryPersistence` and `IDisposable`. By default the caller
retains store ownership; `ownsStore: true` transfers that disposal responsibility
to the adapter. Disposing the adapter releases its cache, while previously
returned snapshots and Document streams retain their leases. The writable
adapter reports `IsReadOnly == false`.

`ReadGeneration` performs an O(1) indexed generation check. On a new generation,
the adapter uses `ReadMetadataSnapshot` to build immutable ordinal point/child
indexes once, without opening every Document. Existing snapshots retain their
old generation and blob leases. `OpenDocument` still verifies the exact bytes,
length and hash; metadata-only snapshot acquisition is not a substitute for
Document integrity verification. Both the adapter's snapshot limit and the
file-store's finite limits apply.

For the current LocalFileStore API, the important translations are:

| Engine mutation | File-store mutation |
| --- | --- |
| Delete | `StorageMutation.Delete(key)` |
| PutWithoutDocument / Remove | `StorageMutation.Put(key, metadataUtf8)` |
| PutDocument / Replace | `StorageMutation.Put(key, metadataUtf8, suppliedStream)` |
| Put / Preserve, with or without an existing Document | `StorageMutation.PutPreservingDocument(key, metadataUtf8)` |

The metadata-only file-store Put removes content; it must not be used for a
Preserve mutation. Native reference preservation does not consume one open stream per preserved
Document. The adapter serializes opaque RegistryJson without reflection or
numeric narrowing and never disposes engine-supplied replacement streams.
The engine retains its original snapshot through preparation and commit; a
snapshot pin must not monopolize a backend operation lock for its whole lifetime.

The adapter maps StorageFailure.GenerationMismatch to RegistryConcurrencyException,
Busy to RegistryException with diagnostic code `server_busy`, LimitExceeded to
`too_large`, and CommitOutcomeUnknown/UnusableStore to
RegistryCommitOutcomeUnknownException. Other storage/integrity failures remain
infrastructure errors, not empty snapshots or successful no-ops. CommitAsync wraps
the bounded synchronous file-store commit, retaining cancellation and unknown
acknowledgement semantics. No mutation is automatically replayed.

`tests\XRegistry.Storage.File.Tests\RegistryPersistenceTests.cs` covers native
preservation, immutable generation indexes, retained leases, stale candidates,
snapshot quotas, and exact-byte restart. In particular,
`FileBackedEngineRestartsWithFrozenCustomModelExactBytesAndCommittedOutbox`
checks a custom model after reopen, Document bytes, atomic outbox/correlation
records, and nested-failure rollback without advancing the stored generation.
The separate process-crash/recovery harness and principal hosts provide their
own evidence; transient restart is not presented as crash-recovery evidence.

## Validation and event integration

Endpoint usage validation is selected by the effective Group model's explicit
`modelcompatiblewith` declaration:
`https://xregistry.io/xreg/domains/endpoint/specs/model.json`.
An exact declaration of this pinned domain opts the Group into
`XRegistry.Models.RegistryDomainRules.ValidateEndpointUsage`; a collection or
singular name of `endpoints`/`endpoint` alone does not. The declaration is not
treated as proof of valid metadata: the rule checks the normalized, merged
Group after Core validation, before publication. This applies to nested writes,
patches, and existing Groups when a model update introduces the declaration.
An invalid model update rolls back rather than grandfathering incompatible
Group data.

The packaged Endpoint and CloudEvents models returned by
`BuiltInRegistryModels.Compile(kind)` already include this declaration.
Explicitly compatible Groups under different collection names are also checked,
and frozen-model restart preserves the opt-in. This integration adds a Server
project reference to Models without changing public Server signatures. It
reuses only the Endpoint usage rule; it does not invent additional protocol
validation, execute network protocols or `protoc`, or fetch the declaration URI.

`RegistryEngineOptions.ResourceValidator` accepts `IRegistryResourceValidator`.
Its `ValidateAsync(RegistryResourceValidationContext, ct)` receives the actual
Resource/Group definitions, owned Group and Meta metadata, and every candidate
Version before retention pruning. A `RegistryVersionCandidate` has VersionId,
Metadata, owned exact Document and optional ExternalDocument URI. Metadata-only
and empty Documents both supply zero bytes. Return one
`RegistryVersionValidation` per Version ID, independently classifying format
and compatibility as Valid, Invalid, Unsupported, Indeterminate or NotRequested.
Omitting a requested outcome is an infrastructure contract failure.

The validator advertises `Formats` and `Compatibilities`. Actual validation
failure rejects the entire mutation; unsupported/indeterminate checks set false
and a reason unless strictvalidation requires rejection. Disabled checks remove
their status/reason fields. The engine divides `MaxSchemaSteps` across all
Resource validator calls in an interaction. Validators must honor their share and
cancellation; the engine additionally enforces byte, time and outstanding-call
boundaries. A timed-out non-cooperative validator cannot publish and continues
occupying its external-work slot until it terminates.

The opt-in `BuiltInRegistryResourceValidator(DocumentValidationOptions? options =
null)` implements this interface using the Validation package:

```csharp
var options = new RegistryEngineOptions
{
    RegistryId = "schemas",
    PublicRoot = new Uri("https://registry.example"),
    Model = BuiltInRegistryModels.Compile(RegistryModelKind.Schema),
    ResourceValidator = new BuiltInRegistryResourceValidator()
};
```

It uses `BuiltInDocumentValidator.ValidateAsync` for the qualified JSON Schema,
XSD 1.0, Avro, Protobuf and JSON Structure variants, and
`BuiltInDocumentCompatibilityValidator.CheckAsync` with the actual nearest-first
`ancestorid` chain captured before retention. Direct modes compare the immediate
ancestor; transitive modes require the complete candidate ancestor history.
History exhaustion never silently truncates a transitive check into a direct
one. Cross-format or externally unavailable history remains explicitly
unsupported/indeterminate, without fetching it.

Avro uses the supplied reader/writer resolution for all six directional and
transitive modes. **Other formats implement validated exact-byte identity only.**
Even semantically equivalent reformatting is unsupported by that limited
compatibility policy. Advertised compatibility mode names identify the
available evaluators, not a universal schema-evolution algorithm. The returned
status is authoritative: Invalid/Incompatible reject publication, while
Unsupported/Indeterminate mean unchecked with a reason (or rejection in strict
mode). A false validation flag never represents a known validation failure.

The adapter reserves its work share across requested syntax and compatibility
checks and maps it to the Validation package's real `MaxWork` and node budgets.
Unused shares are not reassigned. Per-document/total bytes, depth and history
limits are further restricted by the engine limits. Large batches can therefore
produce explicit unchecked budget outcomes even when the same document validates
alone; raise the configured finite budgets rather than interpreting those
outcomes as validity. Schema reference resolution has no default network reader.
An optional `DocumentValidationOptions.ResolveReference` must enforce the host's
egress policy and cancellation. The default schema base URI is the Version's
Document URL, without `$details`, so absolute self-references stay local.

This is an additive Server-to-Validation project reference. Leaving
`ResourceValidator` unset preserves explicit unsupported outcomes; the adapter
is not enabled silently. The parent Validation implementation is not modified.

`IRegistryMetadataObligationValidator.ValidateAsync(
RegistryMetadataObligationContext, ct)` discharges Core's relative URI-target
obligations using the trusted caller and model. Without this service such
obligations reject, rather than disappearing. InitialMetadata cannot contain
undischarged external obligations; submit such metadata as an authorized engine
operation.
Absolute URI/URL metadata values are not constrained by the Core `target` aspect
and do not create target-validation or acquisition work. This exemption does not
waive ordinary URI syntax, authentication, or separately configured outbound policy.

External Document references require
`IRegistryDocumentReferencePolicy.AuthorizeAsync(ClaimsPrincipal, Uri, ct)`.
The policy is called before storing a URL and before returning a Document
redirect. Approval never automatically fetches data. Metadata reads preserve
the reference and a Document read returns 303. External model includes require
the explicitly configured Core resolver and compilation budgets.

`ReadEventBatchesAsync(context, limit, ct)` returns owned pending CloudEvent
batches. `AcknowledgeEventBatchAsync(correlationId, context, ct)` removes one
batch atomically without altering entity epochs.
`DeliverEventBatchesAsync(IRegistryEventSink, context, limit, ct)` delivers then
acknowledges. These APIs require authenticated ReadEvents/AcknowledgeEvents
policy decisions; they are not public HTTP endpoints. Consumers must deduplicate
event IDs because delivery can succeed before an acknowledgement fails.
Per-interaction events share time/correlation, merge changed fields, and enforce
deleted/created/updated precedence. Deprecation and model events are produced
without inventing a standardized watch transport.

Server now selects lifecycle observations and delegates generic envelope,
precedence, changed-name coalescing and serialization to Core's
`RegistryInteractionEvents`. Normal default-pointer changes use
`RecordDefaultVersionChange`; unavailable projection knowledge remains unknown
rather than claiming a complete changed-name subset. `Seal()` prepares owning
structured CloudEvents before response/persistence preparation. Core event IDs
are `correlation:ordinal`; treat event IDs as opaque, and do not rewrite any
already-persisted legacy event IDs.

Stored Documents participate in changed-name reporting despite living outside
metadata records. A default-pointer change includes the Resource's singular
Document attribute when either Version has stored content, including a
zero-byte Document; metadata-only and external-only Versions do not invent
that attribute. Replacing stored content with an external reference reports
both the removed Document attribute and the added URL attribute on the Version
and its default Resource projection. Event preparation uses presence metadata,
without loading Document bytes or generating spurious Version updates for a
pointer-only change.

The event review covers all 59 extracted `core/events.md` entries: 56 have
explicit implementation/test mappings and three are informative notation or
scope text. Core event context/limits have 33 cases per TFM; the complete
274-case Server suites, including lifecycle/deprecation/cascade and Document
event regressions, passed in native Windows x64 executables for net8.0 and
net10.0 with zero IL warnings. String collection assertions use TUnit's
explicit comparer overload rather than reflection-based structural comparison.
No event clause is release-qualified while the required platform matrix remains
incomplete.

Generated correlation IDs are compact lowercase GUIDs, so their response header
value equals Core's safely encoded `CorrelationHeaderValue`. Acknowledgement
accepts either GUID case and removes only the outbox record, never its lifetime
reservation. Lifecycle decisions, authorization, reservations, atomic publication
and delivery remain Server responsibilities. No public engine, endpoint,
persistence or event-delivery signature extension is required by this
integration.

## Standardized error contract

`ProblemCatalog` contains the 60 Core and six HTTP-binding error definitions
from the frozen catalog. Their numeric status, specification URI and English
title templates are checked directly against the pinned Markdown, independently
of the test fixture expectations. Standardized problems use the binding's
`application/json; charset=utf-8` representation with explicit type, title,
status, code and subject fields. Optional detail/source/instance fields remain
bounded. Nonempty `args` contains the actual title substitutions; `subject`
is not duplicated in args, and an empty args object is omitted.

Registry metadata validation errors are contextualized at the owning entity
before returning from the engine. Meta guards identify the Meta XID; Version
validation identifies the Version, while compatibility/default/ancestry errors
identify the Resource. Epoch arguments preserve arbitrary-precision integers.
Unknown attributes, required attributes, model-default errors, Group
constraints, capability choices, Document representation conflicts and xrefs
carry their applicable catalog arguments. The Core query/value implementation
and public query interface are unchanged.

Explicit null removal of an existing required attribute without a default
reports `invalid_attribute`; missing creation/replacement values retain
`required_attribute_missing`, and required defaults still reset normally.
Nested object/map/array and Document-header mutations retain the same
distinction and atomic rollback. Model replacement preserves existing
Group/Resource singular names and classifies incompatible existing Version
metadata as `model_compliance_error`.

HTTP errors whose specification subject is `request_path` retain the incoming
escaped path, including `PathBase` and the configured mount, but exclude the
query. Core XID subjects and `details_required` remain Registry-relative.
The distinction is attached to the error definition rather than blanket
prefixing every diagnostic.

The following distinctions are intentional corrections:

| Situation | Contract |
| --- | --- |
| Missing entity | `not_found`, 404 |
| Unknown modeled Group/Resource type | `unknown_group_type` / `unknown_resource_type`, 400 |
| Unsupported administrative API path | HTTP `api_not_found`, 404 |
| Unsupported method on a supported route | `action_not_supported`, 405 with the applicable Allow list |
| PATCH to a document view | HTTP `details_required`, 405 |
| Disabled available-metadata category | `not_available`, 400 with that capability category as subject |
| Readonly entity/Meta/backend | `readonly`, 400; backend writes reject before storage access |
| Response cannot fit the bounded result | Core `too_large`, **406**, not 413 |
| Oversized request body, including chunked Document input | qualified `request_too_large`, 413 |
| Oversized request headers / target | qualified `request_headers_too_large` / `request_target_too_large`, 431 / 414 |
| Entity/schema/working-set/persistence policy quota | qualified `operation_limit`, 422; no response-size catalog claim |
| Read infrastructure failure | `data_retrieval_error`, 500, without storage details |
| Unknown commit outcome | qualified `commit_outcome_unknown`, 503 and `xRegistry-commit-outcome: unknown` |

Policy/authentication/cursor/request-limit/source-contract errors are explicitly
qualified as `urn:xregistry-dotnet:problem:<code>` and use
`application/problem+json`, rather than inventing nonexistent Core catalog
anchors. Missing Document-reference or metadata-obligation host policies are
not mislabeled as unsupported HTTP methods. Unknown host extension diagnostics
do not expose arbitrary exception messages or URLs.

The operation-limit classification changes only the reported policy error:
entity work, schema work, working-set and persistence quotas themselves are
unchanged. Query/result preparation limits retain the frozen shared evaluator's
existing `too_large` diagnostic and map to the catalog's 406 response.

Error paths preserve the trusted PublicRoot Link header. Method failures report
the route's supported methods, including HEAD/OPTIONS where applicable.
Successful committed interactions retain their correlation header; rejected
requests do not publish an outbox entry or advertise a success correlation.
Typed/header decoding, header-name representation and prepared response
failures happen before persistence commit. After commit, transmission failure
aborts the response rather than falsely reporting rollback.

Header errors retain the actual header name. Values undergo the existing
single percent-decoding pass, accepting valid lowercase escapes and rejecting
invalid UTF-8/quoting as HTTP `header_error`. Identifier headers are not silently
discarded as readonly data: their binding identity checks still apply.
Duplicate JSON members are `parsing_data`, with the request's subject, and
metadata-body requests reject extra Registry headers explicitly.

Error disclosure is finite: the separate error-body default is 32 KiB (minimum
1 KiB), dynamic argument/detail values are limited to 1,024 characters and
subjects to 2,048 characters. URL/credential-bearing diagnostics and oversized
argument values are withheld/redacted instead of copied into the response.
Subjects cannot introduce an exception-supplied foreign URL. If a host error
omits mandatory catalog arguments, or the configured error budget cannot
represent it, the adapter logs the infrastructure contract failure and emits a
small valid qualified `error_contract` problem with status 500. It does not
silently substitute a malformed standardized problem or an empty success.

The frozen prose has a few local inconsistencies: HTTP capability update text
links unknown keys to generic `capability_error`, while Core's more specific
update rules require `capability_unknown`; the implementation uses the specific
Core rule. Inline prose names both `inline_noninlineable` and `bad_inline` for
non-inlineable attributes; known non-inlineable names use the specific former
code, while unknown/malformed paths use `bad_inline`. The `server_busy` catalog
has numeric status 503 with an inconsistent reason phrase; HTTP uses numeric
503 and the normal server reason phrase.

This is bounded error-contract closure, not an all-condition conformance claim.
All 66 catalog definitions are represented, but not all 66 have independent
live condition vectors. Exotic conditional/dynamic XID attribute failures may
still use contextual `invalid_attribute` rather than `malformed_xid`.
Repeated-filter diagnostics preserve the effective ordered filter values
joined as an OR set; the frozen shared evaluator does not expose a failing
branch index. Errors rejected by Kestrel before the mapped delegate (invalid
HTTP framing or request-line syntax) remain host-level responses. Host-specific
extension families, alternate transports and the full clause-ledger review
remain outside this closure.

## Current boundaries and profile

`RegistryLimits` bounds JSON bytes/depth/nodes/numeric work, each Document,
prepared responses and headers, query length, entity work, aggregate loaded
metadata/content, concurrent operations, and external validation/compilation
time and concurrency. `RegistryHttpOptions.RequestTimeout` additionally bounds
the complete HTTP request/body/response lifetime. A read-only backend rejects
writes before snapshot/payload access. Authorization covers the initial action,
implicit parents, explicitly updated entities and lifecycle/cascade effects
before Document consumption and persistence preparation.

After a successful commit, a lost response is not evidence of rollback.
Known concurrency conflicts are 503 without publication. Unknown commit
outcomes are explicit 503 problems with `xRegistry-commit-outcome: unknown`.
Problem details are written explicitly, independently of the metadata codec,
using the standardized contract and qualification boundaries above.

The following remain outside this implemented profile and are not advertised:
host-wide well-known discovery and federation resolution. A Resource with a
Document URL returns the required **303 See Other**, an empty body and Location
when its Document is requested (frozen HTTP lines 1940-1946), subject to the
explicit host URL policy. No normative `proxyurl` attribute or mandatory proxy
fetch was identified, so no new proxy semantics or automatic network fetch
is implemented. Ordinary domain/IRI metadata is not fetch authorization.
The durable adapter and
file-backed engine restart integration are implemented in Storage.File.
Production authorization, principal-host/bridge composition, crash-recovery
qualification, and complete clause-ledger review are separate integration and
evidence responsibilities tracked in the [roadmap](roadmap.md). No cross-registry transaction, remote prepare,
or automatic mutation replay is required or claimed.

## Reproducible verification

Run each new test project explicitly; no root solution/configuration edits are
required:

```powershell
dotnet run --project tests\XRegistry.Server.Tests\XRegistry.Server.Tests.csproj -f net8.0
dotnet run --project tests\XRegistry.Server.Tests\XRegistry.Server.Tests.csproj -f net10.0
dotnet run --project tests\XRegistry.AspNetCore.Tests\XRegistry.AspNetCore.Tests.csproj -f net8.0
dotnet run --project tests\XRegistry.AspNetCore.Tests\XRegistry.AspNetCore.Tests.csproj -f net10.0

$env:PATH = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer;' + $env:PATH
dotnet publish tests\XRegistry.AspNetCore.Tests\NativeSmoke\NativeSmoke.csproj -c Release -r win-x64
& .\tests\XRegistry.AspNetCore.Tests\NativeSmoke\bin\Release\net10.0\win-x64\publish\NativeSmoke.exe
```

The native smoke is a separate explicit loopback-only test host using the pinned
Schema model, not a production security bypass or principal sample application.
