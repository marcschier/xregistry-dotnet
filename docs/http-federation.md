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
equivalent exports/inlining are not yet implemented. Unrequested `304`/`206`
or redirected representations fail explicitly rather than being combined
without the necessary cached bytes or validators.

Real Kestrel tests cover the public source seam, both TFMs, default/explicit
Documents, metadata-only types, aliases, independent result lifetimes, opaque
multi-page selection, typed errors, view handling and cumulative request limits.
That is not full federation or native-platform qualification.
