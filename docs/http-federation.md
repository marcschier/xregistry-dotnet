# Live HTTP federation source

`HttpFederationReadSource` adapts an explicitly selected HTTP Registry to
`IFederationReadSource`; it does not introduce a new HTTP wire API or treat a
catalog as permission to connect. The source owns an origin-isolated
`XRegistryHttpClient`, a finite session deadline and cumulative request,
object, byte, result and traversal budgets. One session permits one active
read rather than silently queueing unbounded work.

The overload accepting `FederationReadBudget` shares its counters with a larger
composition or catalog-selection operation, including bootstrap. Supply no
source options or use the exact `budget.Limits` instance in those options;
conflicting limits fail before allocating a client or contacting the source.
The Bridge's HTTP source factory uses this overload rather than resetting
request and byte counts for each upstream.

```csharp
using XRegistry.Federation;

using var source = await HttpFederationReadSource.OpenAsync(
    new Uri("https://registry.example/services/xreg/"),
    cancellationToken: cancellation);
var result = await source.ReadAsync(new(
    FederationOperation.Entity,
    "/documents/main/assets/item",
    representation: FederationRepresentation.ApiView), cancellation);
```

Bootstrap verifies the returned `specversion` before interpreting the model.
This baseline supports `1.0-rc4`. It keeps the effective model and available
model source distinct, validates their consistency, and interprets only
enabled capabilities. It never fetches external model includes through an
ambient resolver. A missing optional capabilities API provides no positive
flag evidence: `HasCapabilitiesEvidence` is false, and an explicit capabilities
read fails rather than inventing a complete map.

When Model Source contains includes, callers may supply an explicitly authorized
`HttpFederationReadOptions.ModelResolver`. Its returned documents consume the
same source-session request/object/byte/work limits, and it must impose its own
synchronous I/O deadline. Without that resolver, a valid unresolved model is
`UnsupportedOperation`, not malformed Registry data. Source and effective-model
consistency checks still apply after resolution.

## Reads and ownership

All typed paths retain the selected Registry mount prefix and exact IDs.
Core XIDs are relative URI paths. The source accepts ordinary or percent-escaped
Core ID segments, decodes them exactly once for ordinal identity comparison, and
retains the original source `xid`/navigation spelling. Legal `:` and `@` IDs are
not rejected merely because HTTP serialized `%3A` or `%40`. Encoded separators,
invalid UTF-8 and values requiring a second decode remain invalid.

Document-bearing Resource/Version metadata uses `$details`; metadata-only
Resource types use the unsuffixed API, and Document reads for those types fail
before dispatch. Meta, Group, Registry and collection URLs do not acquire a
`$details` suffix.

Default-Document capture reads Resource Meta, records `defaultversionid`,
reads that explicit Version, then checks that the default-selection metadata
has not changed. A local `xref` is checked against the same compiled Resource
type and follows at most one hop. A `303` external-Document response must have
an empty body and a Location matching the selected Version's domain URL.
It becomes an **unfetched external descriptor**, never an implicit outbound
request or a successful empty Document.

Returned metadata, Document bytes and external descriptors are independently
owned. A zero-byte Document remains present. Source disposal does not invalidate
already returned results. This capture adapter materializes Documents within
its finite single-object budget; use the lower-level HTTP client when streaming
larger content without materializing a federation result.

The default representation is Core document view. It is requested only when
enabled `doc` support was observed; API view is always explicit. The adapter
checks document-view navigation and rebases only model-known navigation
pointers when wrapping entities in the abstract result envelope. Arbitrary
Document bytes, domain URL attributes and user strings are not rewritten.
An ignored flag yielding API URLs cannot masquerade as document-view pointers.

## Exact API-Version acquisition

`ApiRegistryDocumentAcquirer.AcquireAsync` takes one caller-authorized
`IFederationReadSource`, an exact API Version XID and a shared read budget.
It verifies the captured model's API Group and document-bearing Resource,
reads Version metadata, then acquires that exact Version's bytes. It does
not inspect a mutable default, resolve aliases, retrieve dependencies or
invoke an API.

```csharp
var budget = new FederationReadBudget();
using var source = await HttpFederationReadSource.OpenAsync(
    registryRoot, transportOptions, new() { Limits = budget.Limits },
    budget, cancellation);
var acquired = await ApiRegistryDocumentAcquirer.AcquireAsync(
    source, "/apigroups/team/apis/telemetry/versions/1", budget, cancellation);
```

The result retains the exact Version XID, complete authored format identifier,
computed SHA-256 digest, source context and independently owned Document.
The Document's media type, declared base and original retrieval location remain
separate; no base is inferred from an XID or namespace. An HTTP source captures
its retrieval URL as `Document.Origin`. A consumer passes that URL, or the
applicable native-language base, explicitly to its operation resolver.
Acquisition does not normalize format spelling or establish language validity.

Missing or denied Versions, external descriptors, wrong identities and changed
source/model/resolution-owner captures fail without another Version or source.
Metadata and acquired bytes consume cumulative request, object, byte and work
budgets; hashing and output also have finite bounds. Repeated calls cannot
reset a supplied budget. Cancellation propagates.

`AcquireDefaultAsync` accepts an API Resource XID. It reads Resource Meta,
captures Core's `defaultversionid`, acquires that exact Version, then reads
Meta again. It follows a sticky or prerelease default without applying SemVer
selection. An observed default change produces `InconsistentSnapshot`; a
missing selected Version fails without choosing a replacement. Aliases must
identify the same compiled Resource type in the selected local source.
One local `xref` is followed; chains, wrong-type and nonlocal targets fail
before Version acquisition. The result identifies the target's exact Version
and retains its Document origin rather than inventing an alias retrieval URL.

Both Meta reads and exact-Version acquisition use the same captured source
and cumulative budget. Exhaustion before the final Meta check is a failure,
not an unchecked success. These observations do not eliminate ABA changes
or establish a transaction across live HTTP reads.
For an alias, the target default and original alias mapping are both rechecked.
Retargeting or removing the alias during acquisition fails without reading
the new target or returning previously acquired bytes.

The live HTTP workflow publishes two API Versions, acquires the older exact
Version after the default changes, verifies the current and sticky Core
defaults, and selects the retained OpenAPI operation after
closing both source and host. Without an authorized dependency resolver,
its relative Path Item `$ref` produces `Indeterminate`. With one, the resolver
receives the absolute dependency URL resolved against the captured retrieval
origin; the selected operation reports that dependency's URI. The entry
Document and supplied dependency bytes remain separate.
This does not freeze unversioned dependencies,
implement external-document retrieval or qualify full API language validation.

## Collections and errors

Collection capture follows every necessary opaque `next` link, even if
`pagination` was advertised false. Initial query flags are not appended to
subsequent links. The shared client validates each continuation's exact
origin/root before credentials or dispatch; percent-encoded cursors and
path segments retain their wire spelling.

Literal label selection is applied locally after complete unfiltered
enumeration. It needs no filter support and does not infer wildcard or
normalization semantics. Later-page failures, duplicate IDs, changing totals,
wrong XIDs and competing next links cannot produce a first-page success.
Zero complete matches produce `NotFound`; multiple matches produce `Ambiguous`.

Document-view Resource selection uses the declared default Version's inlined
metadata. If that metadata is not included, selection acquires only Resource
metadata (never domain bytes) and checks it against any already captured default
ID. It preserves the requested document-view result rather than leaking the API
projection used for selection. Consumed duplicate/case-colliding identities and
wrong typed paths cannot be hidden by local shadowing.

`ResourceFederationResolver` retains each consumed source's context and enabled
resolution owner for the whole composition operation, not just for individual
callbacks. A change between a Resource probe and its Version read fails before
another read is dispatched. Complete collection and unique-label results also
recheck every consumed source, so an earlier capture cannot silently switch
while a later source is read. These checks detect observed contract violations;
they do not turn a live HTTP source into an immutable snapshot.

`FederationException` retains the original `HttpStatusCode` and owned
`HttpProblemDetails` when a Core error response is supplied. The latter is
untrusted source data, not safe diagnostic output for another caller.
`api_not_found` is an unavailable operation, not a missing entity; an ambiguous
generic 404 is not interpreted as an empty collection. Authentication,
authorization and redirect-policy failures never cause a source fallback.

## Capture evidence and limits

`Context.IsImmutable` is false and `Context.Revision` is null. `Observations`
records each selected representation's URI, status, retrieval time, ETag,
Last-Modified, Vary, content coding and cache policy, separately from Core IDs.
The reader does not turn a root epoch or ETag into a recursive Registry pin.
`RequireImmutableSnapshot` fails before contacting an ordinary HTTP source.

These are **best-effort live observations**, not a transaction across URLs.
Default-selection checks catch an observed race but cannot eliminate ABA
changes or prove a global snapshot. Shared caching, conditional revalidation,
range assembly, independently authorized redirect acquisition and fallback to
equivalent exports/inlining are unsupported. Unrequested `304`/`206`
or redirected representations fail explicitly rather than being combined
without the necessary cached bytes or validators.

Real Kestrel tests cover the public source seam, both TFMs, default/explicit
Documents, metadata-only types, aliases, independent result lifetimes, opaque
multi-page selection, typed errors, view handling and cumulative request limits.
That is not full federation or native-platform qualification.
