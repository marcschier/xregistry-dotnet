# Bounded FederationBridge host

`samples\XRegistry.FederationBridge` is a functional .NET 10 principal sample,
not an adapter pretending that `MapXRegistry(RegistryEngine)` accepts a federation
reader. It uses new `ProducerRegistryView` composition in the Federation library,
its own explicit HTTP adapter, and the shared sample-host security source. It
does not change the Server/AspNetCore engine, existing federation resolver,
HTTP acquisition, File implementation, root manifests, solution or CI.

## Read-only producer view

The default aggregate is `/registry`. GET, HEAD and OPTIONS are supported.
Registry, Group, Resource, Meta, Version and typed collection reads are
model-aware. `/model`, `/modelsource` and `/capabilities` return configured,
validated material. The enabled capability is the draft's
`federation.resolution: producer`; no nonstandard Core ownership attribute is
invented.

Unflagged entity metadata remains shallow, with complete composed collection
counts and navigable URLs. Root `model`, `modelsource` and `capabilities` appear
only when explicitly selected by `inline`; `inline=*` does not include them.
Their standalone routes remain available. This output projection does not
discard captured source metadata or fetch additional configuration planes.
Collection responses contain complete immediate
entity maps. The aggregate implements Core `doc`, `inline`, `collections`,
`binary`, `filter` and `sort`. Filtering, sorting and query facts use the actual
shared `XRegistry.Queries` implementation. Retained pagination is advertised only
when an explicit current retained-read authorization policy is configured (as it
is in the principal sample). Export, discovery and offered capabilities remain
explicitly unsupported.

Source order is configuration, not arrival timing. Ordinary Registry/Group
metadata comes from the first eligible source; the Registry ID is the configured
view ID. Group membership and Resource collections are composed from complete
source collections before counts, uniqueness or visibility decisions. Conflicting
case spellings in a materialized collection fail instead of producing two
case-insensitively conflicting members.

A Resource is the shadowing unit. Its first eligible exact match owns Meta, the
entire Versions collection and every Document. Version sets are never merged.
A selected Version failure is returned from that source; it is not permission
to try another Registry. Point Version/Document reads open sources lazily only
until the Resource has been selected, not every source in the network.
`ResourceFederationResolver` is reused for selected-origin entity reads.

An already producer-resolved first source supplies the requested view directly;
other configured sources and catalogs are not retraversed. Native producer
Resource envelopes project their captured default Version without new source
reads. Document responses may additionally read the exact selected Version's
metadata from that same source to provide HTTP metadata headers.

## One-hop aliases and projection compatibility

Aliases are recognized from their source-local Meta `xref`. The target must be a
syntactically valid Resource XID of the same **actual compiled Resource type**;
structurally identical independent types do not qualify. Imported shared types
do qualify, including the frozen OCI fixture's imported-type aliases.
Malformed/type-incompatible references return `malformed_xref`.

API-view metadata copies target state while retaining alias IDs, XIDs and
navigation. Resolved alias Meta is included only when requested by `inline`;
its `metaurl` remains available in a shallow response. Serialized alias Meta
retains the original `xref`. The identity-only dangling/second-hop exception
below still includes its Meta/xref without an inline request. Default and
explicit Version/Document reads access only the selected source's target, and
Content-Location names the alias Version rather than an unrelated aggregate
target. The alias itself remains one Resource shadowing unit. A missing selected
Version never causes source fallback.

Missing, inaccessible and second-hop targets produce the Core identity-only
Resource/Meta shape; they do not inherit target attributes. Their API Versions
collection is empty, an explicit Version is 404, and a dangling default Document
is empty rather than fetched from another source. Transitive traversal is never
performed.

One explicit compatibility rejection remains: if the source-local target XID
would be owned by a different configured source in the aggregate, projection
returns **409 `alias_origin_conflict`**. Silently keeping that `xref` would
change its meaning. This bounded check may perform targeted Resource probes in
other sources; it never substitutes a competing Document or Version.
Producer-owned first views do not retraverse excluded configured sources.

Core document view does not dereference aliases. It emits alias identity and
Meta/xref data and omits target state/Versions. A direct alias Version or
Versions-collection `?doc` request returns **400 `cannot_doc_xref`**, the
specified view conflict, not a generic unsupported-view response. With multiple
sources the same target-ownership compatibility check still applies.

## Models, representations and provenance

Core `RegistryModel`, `RegistryPath`, `RegistryMetadataValidator`, strict
`RegistryJson` and `RegistryHeaderEncoding` are reused. There is no second model
compiler. Each opened source must match both configured model source/import
structure and effective model. The source context, model object and resolution
ownership must remain stable inside a request.

XIDs are relative URIs: identity comparisons decode each component exactly once
with Core's strict decoder and compare the resulting IDs ordinally. For example,
`item:1@home` and `item%3A1%40home` name the same Resource. Raw ID attributes and
collection keys are not URI-decoded and must agree with their typed identity
before any default-Version projection. Malformed escapes, encoded separators,
controls and double-encoded invalid IDs are rejected, not repaired. Ordinary
source XID spelling and the original alias `xref` are retained; generated
navigation escapes decoded IDs once, including Document Content-Location.

The renderer rebases only known Core navigation: entity self links, Meta/default
Version links and model-defined collection URLs. It derives these from the
explicitly trusted PublicRoot and typed identity, never incoming Host or forwarded
headers. It does not recursively rewrite arbitrary extension JSON, domain URLs,
Document URLs or document bytes.

`inline` accepts model-relative dot paths, repeated/comma-separated selections,
and terminal `*`. Unknown paths, entity IDs in paths, non-inlineable attributes
and nonterminal wildcards return `bad_inline`. Wildcards exclude root `model`,
`modelsource` and `capabilities` unless explicitly selected.

With `doc`, every rendered entity's `self` points into the actual response
document. Collection/Meta/default-Version links become response-local pointers
only when their targets are actually included. A standalone Meta read therefore
keeps an absolute default-Version metadata URL. Resources contain no duplicated
default-Version attributes, Versions lose the specified validation flags and
their dependent reason fields, and
`shortself` is omitted. Opaque extension objects and inlined domain JSON are
never traversed as Registry navigation.

API metadata retains captured false validation flags and their reasons; Doc
projection does not change or revalidate the source capture. Models requiring
validation remain subject to the bounded producer admission rules below.

`collections` applies only to Registry/Group entities, implies wildcard inline,
and strips only the top entity's ordinary metadata. `binary` does not imply
inlining: when a Document is requested inline, it forces exact base64.
Otherwise the compiled model's typemap selects JSON/string/binary. Empty or
invalid JSON/UTF-8 content falls back to exact base64, not replacement characters.
An external inline descriptor remains a URL, never fetched/empty content.

Document-bearing Resource/Version metadata uses `$details`. Metadata-only types
do not emit that suffix and return metadata on their ordinary URLs. Document
responses retain exact bytes, including zero bytes, content type, the exact
selected Version's Content-Location and representable scalar/map metadata headers.
An external descriptor is not an empty Document: an explicitly authorized
HTTP(S) external origin produces 303 with that URI, without fetching it.
Unlisted external origins are rejected.

`X-Bridge-Sources`, `X-Bridge-Binding`, `X-Bridge-Revision`,
`X-Bridge-Root-Sha256` and `X-Bridge-Consistency` carry available provenance
outside Core entity attributes. A parent epoch is not a global capture token.
HTTP captures remain live best-effort, even if root epochs repeat. File/Git/OCI
contexts retain exactly the pin/verification guarantees supplied by their
bindings; multiple immutable sources are not advertised as one atomic snapshot.
Responses use `Cache-Control: no-store`.

Models needing Group constraints or external document-format/
compatibility validation are rejected by this bounded view. Any remaining
state-dependent metadata obligation is rejected rather than waived. These are
profile limits, not claims that the broader specification lacks those features.

## Source factories, caller policy and lifetimes

Configuration supports explicit HTTP, File document-tree/File OCI-layout, Git
document-tree and native OCI Distribution sources. File paths have an explicit
authorized directory; layouts/references are never autodetected. Git requires
HTTPS in this sample's document-tree locator; SHA-1 acquisition additionally
requires an independent Registry-root SHA-256 commitment. Native OCI uses its
HTTPS Distribution origin and an explicit reference.

No catalog-driven automatic source discovery is implemented. Inbound requests
cannot choose a source URI, filesystem root, credential, binding or revision.
Source authorization runs before acquisition or credential lookup. A source's
Bearer credential is captured once per opened session, independently of the
host's administrative/read tokens and mount credentials.

Every admitted request owns a fresh `ProducerRegistryView`. Sources open lazily
and are disposed after preparation, including failure/cancellation paths. Reads
inside a session are sequential; native sessions are never shared concurrently.
Source/result/selection caches are request-local and bounded by object/work/byte
accounting. Optional retained pages hold only frozen encoded result bytes,
credential fingerprints and authorization/provenance descriptors, never live
source sessions. They are bound and reauthorized as described below.

Defaults are eight configured sources/mounts at most, four admitted requests,
a 30-second request deadline, 2 MiB request/response objects, 16 MiB source/read
accounting and 256 source requests. Protocol readers retain their own finite
budgets as well; HTTP source-session acquisition has its own limits. Admission
does not create an unbounded queue. Active operations receive request-abort,
deadline and host-stopping cancellation. Disposal waits for the active source
operation instead of disposing a reader during an overlapping read.

## Fixed write-through mounts

`/registries/{configured-name}` delegates to exactly one explicitly authorized
upstream root. These are authoritative delegation endpoints, **not** part of
the producer-resolved aggregate and not cross-registry transactions.

The adapter rejects aggregate writes, unknown mounts, unsupported methods,
unknown/native query selectors, over-budget bodies and unsupported content
encoding before upstream credential lookup or dispatch. The caller must be
authenticated for mutations and pass the explicit mount policy. The configured
mount model determines metadata versus Document bodies; Content-Type is not
used to guess that distinction.

The bounded mount profile accepts core methods and these query names:
`binary`, `collections`, `doc`, `epoch`, `filter`, `ignore`, `inline`,
`setdefaultversionid`, `specversion`, `limit`, `offset`. Their original order,
repetition, empty values, flag spelling and escaped query bytes are retained.
Value semantics and Core rejections remain authoritative upstream decisions.
Arbitrary opaque continuation query names are not accepted as new mount
requests; this is not a general pagination reverse proxy. Aggregate HTTP source
capture, separately, uses the existing complete opaque-pagination reader.
Model/capability mutations are disabled on fixed-model mounts.

Request bodies are bounded and fully consumed before dispatch. Only deliberate
protocol headers are forwarded: negotiated/conditional/range headers and valid
modeled `xRegistry-` metadata headers. Incoming Authorization, Host, cookies,
proxy credentials and forwarded-identity headers are never forwarded. The
independent upstream credential callback runs once. Egress uses
`RegistryHttpConnectionPolicy`, including DNS and actual socket checks.

Each operation uses one explicit HTTP/1.1 request, connection close, a fresh
policy-owned client and single-serialization mutation content. There are no
mutation retry, fan-out, compensation or rollback paths. Redirects and page
links are not followed.

HTTP status, errors and exact body bytes are preserved after bounded body
completion and representation/header checks. Metadata bodies are not rewritten:
their canonical identity remains the authoritative upstream, unlike aggregate
metadata. Equivalent escaped XID spellings are accepted, but a different logical
XID or inconsistent raw ID attribute is not a successful response. Relative
Location/Content-Location/Link references are resolved to
that same upstream origin while retaining opaque query spelling. Known API
navigation must remain within the configured upstream origin and Registry root;
anchored links and escaping redirects/page links fail. These header conversions
preserve meaning when the response is received through another host.

The transport negotiates identity responses and rejects compressed request
bodies. A successful remote response is not sent downstream until its full
representation has been checked. Failure, timeout or disconnect after mutation
dispatch is **UNKNOWN**, never rollback or an automatic retry. When a response
can still be sent, it is a 503 problem with
`X-Bridge-Mutation-Outcome: unknown`; otherwise the connection is aborted and
the unknown outcome is logged. A completed upstream rejection is returned
unchanged, not relabeled as a successful mutation.

## Shared Core query execution

`ProducerRegistryView.ReadQueryAsync` calls the public
`XRegistry.Queries.RegistryQuery.EvaluateAsync` entry point. Its
`IRegistryQuerySource` adapter enumerates complete already-shadowed direct
members and retains their selected Resource owners. Filters are never sent to
backing sources before shadowing, and a filtered-out local Resource cannot
reappear from a lower-priority source.

The adapter supplies raw Resource/Meta planes (including defaultversionid), own
Registry/Group/Version attributes, and exact stable Document memory only when
requested. Aliases keep the same source-local target and logical alias identity.
Version entries supply `RegistryQueryEntity.DefaultVersionId` from that
identity-checked Resource-unit Meta context, including an alias target's retained context. Core
derives `isdefault` without an extra query Resource-plane lookup or exposing the
context ID as a Version attribute. Captured inline Meta also satisfies Version
collection default checks without reacquiring the logical Meta URI. This context
does not authorize Resource/Meta queries; the bridge's existing whole-Resource
source-selection probe and explicit Meta authorization remain unchanged.
Queries of alias `meta.*` and standalone alias Meta views, including dangling
aliases, perform the logical source-local Meta read; embedded Meta is not a
substitute for that authorization.
A changed Meta target fails instead of rebinding the selected origin.
Missing required Meta/default/Document facts and incomplete source collections
throw rather than becoming empty successful results. The shared evaluator owns
the grammar, exact comparisons, conditional model typing, raw-plane fact
construction, default-Version traversal and subtree AND/OR semantics.

One `filter` parameter is comma-AND; repeated `filter` parameters are OR branches.
`sort` uses the shared model-aware scalar projection and ordering. The adapter
consumes ordered `RootPaths`, uses `Includes` for nested membership, and uses
`CollectionUrl` for reproducible filtered links/counts. Document pointers remain
the bridge projector's responsibility. The shared budget remains alive through
projection and response preparation, with cumulative work/byte/read limits and
a default five-second query deadline.

After shared syntax validation, the bridge checks the actual collection owner
before any evaluator callback reads its members. It also checks that owner when
`filter=excludeall` lets the evaluator skip source membership entirely.
An absent Group or Resource is 404, not a successful empty child collection.
A valid `excludeall` checks only its owner and need not enumerate children.
Invalid query grammar still fails before source acquisition. Equivalent escaped
collection targets retain the shared evaluator's ordered selection.

No Core/Server implementation was modified or grammar/fact code copied into the
bridge. The earlier extraction gap is resolved by the shared public Core module;
the bridge does not construct a RegistryEngine or fake persistence.

## Caller-bound frozen paging

An initial collection GET/HEAD with `limit` evaluates and projects the complete
query first. A missing explicit sort uses the shared ID-ascending ordering.
The host then freezes complete direct Resource units into encoded pages.
`next`, `prev`, `first` and `last` links contain opaque `cursor` tokens and the
complete selected record count. Use the returned cursor URL unchanged: adding
filter, sort, view or size parameters is rejected. Requests without `limit`
retain the existing complete-response behavior.

Pages retain no source/Document session. Replay uses exactly the frozen bytes;
it never reopens sources, refilters a newer HTTP view, merges Version sets or
falls back to another origin. A live HTTP capture is still best-effort during
preparation; freezing bytes makes page replay stable, not the upstream Registry
globally immutable.

Each capture is bound to the authenticated principal's identity/claims, original
escaped collection path, trusted root, model, original query/representation, and source
credential/pin context. Before initial publication and every replay, the host
rechecks current source policy and every captured source-local read dependency
through `AuthorizeRetainedRead`. It also compares each source's current opaque
credential fingerprint with the fingerprint captured by its lease. Principal,
path/query/representation or credential mismatches fail; denied authorization
does not return retained bytes.

`AuthorizeRetainedRead` is explicit: without it paging is not advertised and
`limit`/`cursor` returns 501 before acquisition. The principal's implementation
reapplies the shared source/admin/read policy to the captured paths. Deployments
requiring upstream per-entity revocation checks must supply a policy with that
knowledge; the page store does not silently perform remote ACL discovery.
Credentials themselves are neither stored in pages nor exposed as provenance.

Default limits are 256 records per requested page, 1,024 records and 4 MiB
charged storage per capture, 16 live captures, 16 MiB total charged storage,
4,096 tokens, and a two-minute monotonic lifetime. Encoded page bytes,
headers/dependencies and conservative overhead are accounted for. Captures
that exceed a bound fail before publication; a full store returns 503 instead
of evicting live captures. Expiry never refreshes on replay. The `paging`
configuration section can lower these bounds within the hard sample ceilings.

## Hosting and execution evidence

The host links the shared `RegistrySampleHosting` files rather than installing
a runtime NuGet authentication dependency. Production requires an explicit PFX
HTTPS listener and strong environment-supplied administrative Bearer token.
Read tokens are distinct, anonymous reads are opt-in, and principals come from
the shared authenticated scheme rather than caller headers. Explicit loopback
demo mode warns that every local caller is an administrator; missing secrets
never select demo mode.

See [the runnable sample instructions](../samples/XRegistry.FederationBridge/README.md)
and its demo/production example configuration. The checked sample model is the
frozen fixture's valid dynamic model, including metadata-only types and imports.
The source fixture is not modified.

The managed and win-x64 NativeAOT Kestrel suites cover ordered root/Group/Resource
materialization, fixed-origin Versions/Documents, producer-direct behavior,
empty/external documents, metadata-only frozen File composition, trusted link
rebasing, exact write-through routing/query/header/body semantics, zero-outbound
policy/budget denials, shared Core facts/filter/sort/subtree semantics, frozen
page replay/authorization/credential/expiry limits, escaped-XID identity and
raw-ID rejection, absent collection owners, cancellation and UNKNOWN outcomes. Native execution
requires `XREGISTRY_BRIDGE_REQUIRE_NATIVE=1` and checks both dynamic-code support
and actual JIT compilation count; it is not startup-only evidence.

The real native principal executable is also exercised by `verify-native.ps1`
against six HTTP cases, now including a positive document-view check. Before
those cases, `--runtime-info` must identify this application/framework, false
dynamic-code flags, zero JIT compilations and matching actual process/OS
architecture. The actual managed DLL is executed by `dotnet` as a negative
control and must show positive JIT work and `nativeAot: false`, even when native
publish settings turn its dynamic-code flags off. An actual JIT apphost supplied
as the purported native executable is rejected before HTTP qualification.
The verifier supports Windows/Linux and x64/ARM64 paths without assuming `.exe`,
records native/managed hashes under `artifacts\bridge-build\host-smoke`, bounds
runtime-info processes/output, keeps logs, removes its generated config and
stops only its own processes.

Linux/ARM64 execution, live Git/OCI aggregate deployments, full external-validator
obligations and general federation catalog discovery remain unqualified.
Linux execution remains blocked by the shared Docker storage failure observed
during this work; no restart/prune/repair was attempted. Alias target-origin conflicts and
alias Version document views are explicit semantic incompatibilities rather
than missing generic alias/view implementations. The sample does not complete the specification
ledger or release/native matrices, configure remote infrastructure, or change
package qualification status. The parent owns solution registration and any CI
integration.
