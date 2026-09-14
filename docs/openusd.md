# OpenUSD identifier and integrity helpers

These helpers cover identifier computation and exact-byte integrity. The
Registry engine also enforces the bounded publication rules below for the
explicitly OpenUSD-compatible model. Neither layer implicitly acquires remote content,
renders USD scenes, registers schema plugins, or implements OPC UA.

The source is the pinned OpenUSD working draft:
`tests\Conformance\Sources\workingdrafts\models\openusd\spec.md`,
SHA-256 `77b824ef148a8db8e16e2af5daa241c80c43604436370aaf03c4340416bd4e07`.
The relevant clauses are lines 234-239, 458-475, 492-508, and 648-697.
The identifier-only clarification below is also reflected in the authorized
sibling draft's `workingdrafts\models\openusd\spec.md`, especially Sections
1.3, 1.4, 4.1, 4.2, 5.1.1 and 5.4. That live amendment is not yet an immutable
conformance-source capture; the pinned source above remains unchanged.

## Identifier computation

`XRegistry.Models.OpenUsdIdentifiers` provides pure source normalization,
candidate construction and sibling-scoped assignment:

```csharp
var identifier = OpenUsdIdentifiers.NormalizeAssetIdentifier(
    "./textures/albedo.png", maxUtf8Bytes: 128, cancellationToken);
var candidate = OpenUsdIdentifiers.CreateSymbolicIdCandidate(
    identifier, maxUtf8Bytes: 128, cancellationToken);
// identifier: textures/albedo.png
// candidate:  textures.albedo.png
```

Supply the authored string already extracted from USD `@` delimiters.
Normalization removes leading `./` components, not internal dot segments.
It preserves case, sub-paths, percent spelling and package selectors. It never
inverts a symbolic ID or changes an identifier into a filesystem path.

The candidate operation implements the draft's source-component construction:
reverse authority labels, retain an explicitly written port, decode each path
segment once, normalize to the specified ASCII alphabet, and preserve case.
An empty surviving label sequence produces `_`. URI classification uses raw URI
syntax, not `System.Uri` canonicalization, socket-port ranges, or
framework-specific URI length limits. This does not validate reachability.

Results of at most 128 characters are not shortened. Longer results lose trailing
normalized source labels until the prefix fits 119 characters; an overlong first
label is truncated and its exposed trailing punctuation removed. The operation
then appends `.` and the first eight lowercase SHA-256 hex digits of the **exact
source string passed to the candidate operation**. Dots retained inside a source
label do not create additional label boundaries during shortening.

Normalization and both candidate methods require an explicit, nonnegative UTF-8
**input** byte limit. The original input is counted before normalization or
percent decoding. Invalid UTF-16 or malformed percent-encoded UTF-8 is rejected,
not replacement-encoded. Only candidate construction percent-decodes, once.
Argument failures throw argument exceptions; quota exhaustion throws
`InvalidDataException`; cancellation throws `OperationCanceledException`.

### Stable sibling-scoped assignment

A candidate alone is not an assigned ID. `AssignSymbolicId` takes a complete,
consistent, caller-authorized snapshot whose keys are assigned Core IDs and
whose values are exact source strings. For Resources, pass the already
normalized `assetidentifier` as the source:

```csharp
var siblings = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["a.b"] = "a/b"
};
var assigned = OpenUsdIdentifiers.AssignSymbolicId(
    "a.b", siblings, maxUtf8Bytes: 1024, maxSiblings: 64, cancellationToken);
// assigned: a.b.2e7336dc; a/b keeps a.b; siblings is not modified.
```

The policy is deterministic for that fixed sibling state, not for the source
alone. IDs conflict case-insensitively; authoritative sources match exactly.
It retains an existing source binding first, even if the original competing
sibling has been deleted. Otherwise it selects the unoccupied candidate, or its
sole fallback if the candidate is occupied. Publication/reservation must be
atomic. Other explicit reservations can be included in the supplied snapshot;
JSON member order does not choose an owner for the candidate.

`CreateSymbolicIdCollisionCandidate` derives the fallback from the original
normalized source labels, not by shortening or hashing the candidate string.
It applies the same label-dropping/truncation rule to reserve at most 119 prefix
characters plus the nine-character suffix. This also applies to collision-only
candidates of length 120-128. A candidate already shortened because its original
result exceeded 128 has the same fallback: no second suffix is added.

| Source | Existing ID/source binding | Assignment |
| --- | --- | --- |
| `a.b` | `a.b` belongs to `a/b` | `a.b.2e7336dc` |
| `a/b` | `a.b` belongs to `a.b` | `a.b.c14cddc0` |
| `pump` | `Pump` belongs to `Pump` | `pump.0b203c46` |
| 120 copies of `a` | case-insensitive candidate occupied | 119 copies plus `.2f3d3354` |
| 128 copies of `a` | case-insensitive candidate occupied | 119 copies plus `.6836cf13` |

Without a collision, the 120/128-character candidates stay unchanged. Neither
this policy nor the host unconditionally suffixes IDs, renames published
Resources, or rebinds an existing ID to another source. The host checks prior
identity even when a local one-hop alias is retargeted.

An occupied fallback throws `InvalidOperationException`: no random suffix,
counter, extra hash or overwrite is attempted. It can conflict with another
source's ordinary ID, or with a real SHA-prefix collision. For example, the URI
sources ending in `asset?i=184995` and `asset?i=191756` at `https://example.test/`
both produce `test.example.asset.df41192b` as their fallback. Eight hex digits
are not a uniqueness or security guarantee.

The assignment byte budget covers the source plus **all** sibling keys and
values cumulatively, including on the existing-binding path. The sibling limit
is inclusive; enumeration cannot consume more than the limit plus one overflow
probe, even if a supplied dictionary misreports its count. Invalid/duplicate
Core IDs, duplicate exact-source bindings, and an existing source bound outside
its candidate/fallback pair throw `InvalidDataException`, as do exhausted
budgets. Invalid arguments/encoding and cancellation retain the outcomes above.
The dictionary must remain unchanged during the operation.

The engine also checks symbolic Group assignment against sibling reservations
and the prior exact Group name. Asset-container names already legal as Core
Group IDs remain verbatim; their case-insensitive collision is rejected, not
repaired by symbolic conversion. Plugin Group construction uses the exact
manifest `Plugins[0].Name`, which the pure helpers do not extract.

### Bounded consumer forward resolution

`OpenUsdResolution.ResolveAsync` resolves a normalized source to one Resource
under the caller's selected Group and consistent Registry context. It reads
metadata at the candidate, then at the distinct fallback only if necessary.
A missing candidate still requires the fallback probe: a published suffix can
outlive its competitor.

The callback is explicit, receives only an ID, its remaining metadata byte
allowance and cancellation, and returns `ReadOnlyMemory<byte>?` containing
UTF-8 Resource metadata. For example, a caller-authorized cache needs no network:

```csharp
var authorizedMetadata = new Dictionary<string, ReadOnlyMemory<byte>>
{
    ["a.b.2e7336dc"] =
        """{"usdassetid":"a.b.2e7336dc","assetidentifier":"a.b"}"""u8.ToArray()
};
var found = await OpenUsdResolution.ResolveAsync(
    "./a.b",
    (id, remainingBytes, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        if (!authorizedMetadata.TryGetValue(id, out var bytes))
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
        if (bytes.Length > remainingBytes)
            throw new InvalidDataException("Metadata exceeds the remaining allowance.");
        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(bytes);
    },
    new OpenUsdResolutionLimits(
        maxSourceUtf8Bytes: 128, maxMetadataBytes: 4096, maxProbes: 2),
    cancellationToken);
// found.ResourceId: a.b.2e7336dc; found.AssetIdentifier: a.b
```

If retrieval is needed, the application supplies an authorized, bounded
metadata callback for that same Group/context, for example a `$details` read.
Only an established absence is `null`; an empty byte buffer is invalid JSON,
not absence. Authorization, transport and incomplete-read failures must
propagate. Never use a null `byte[]` conversion as the absence marker: return
the nullable memory explicitly as shown.

A metadata response must have the exact addressed `usdassetid` and a nonempty,
normalized `assetidentifier`. The latter must match the requested normalized
source exactly; a matching `name` or an inverse guess is insufficient.
The returned `Metadata` is an independently owned `RegistryJson`, validated
with Core's finite JSON depth/node/number limits. Callback memory is not
retained and must stay unchanged until resolution completes.

Source bytes are limited before normalization. `MaxMetadataBytes` is cumulative
across both responses; payload length is checked before JSON parsing/copying.
`MaxProbes` accepts 0-2 and cannot allow a third lookup. An already shortened
candidate has only one distinct location. Zero or exhausted quotas fail before
a required callback. Cancellation is observed before/after callbacks and before
return; an in-flight callback is awaited, not abandoned, and must cooperate
with its token for prompt cancellation.

| Resolution outcome | Public result |
| --- | --- |
| Exact authoritative match | Owned metadata, assigned `ResourceId`, normalized `AssetIdentifier` |
| No match at either permitted location | `KeyNotFoundException` |
| Byte/probe budget exhausted, invalid identity metadata | `InvalidDataException` |
| Invalid JSON/UTF-8 or Core JSON budget exceeded | Original `RegistryException` |
| Invalid arguments or source encoding | Appropriate argument exception |
| Cancellation or callback failure | Original cancellation/failure propagated |

The helper does not follow `self`, `xref`, `usdasseturl`, package selectors or
source URIs, and acquires no artifact bytes. Only after a successful identity
match may the application explicitly authorize and acquire the Document using
the returned ID and retained context. This is identity resolution, not complete
OpenUSD model validation, publisher authentication or artifact integrity.

### Bounded Group lookup

`OpenUsdGroupResolution.ResolveAsync` applies the corresponding lookup rules to
an explicitly chosen `OpenUsdGroupKind.AssetContainer` or `SchemaPlugin`
collection. A legal Core Asset Container name has one verbatim ID location.
Symbolic Group names probe the candidate and sole fallback and require the
returned Group ID and exact authoritative `name` to agree. A retained fallback
can still be found after its former competitor disappears.

The caller supplies one authorized, consistent metadata reader and
`OpenUsdResolutionLimits`. At most two responses share the cumulative metadata
byte budget and Core JSON bounds; missing, invalid, denied, cancelled and
exhausted outcomes remain explicit. Group names are not normalized as artifact
identifiers. No artifact, catalog, URI or broker acquisition is performed.

## Metadata-aware artifact integrity

`XRegistry.Validation.OpenUsdArtifactIntegrity` exposes metadata validation and
bounded artifact reads:

```csharp
OpenUsdArtifactIntegrity.ValidateMetadata(
    digest, digestAlgorithm, OpenUsdDigestRole.Producer);

var artifact = await OpenUsdArtifactIntegrity.ReadAsync(
    callerOwnedStream,
    digest,
    digestAlgorithm,
    OpenUsdDigestRole.Consumer,
    new OpenUsdArtifactReadLimits(
        maxArtifactBytes: 16 * 1024 * 1024,
        maxReadOperations: 4096),
    cancellationToken);

using var content = artifact.OpenRead();
```

The inputs are BCL strings and an already acquired `Stream`. Null metadata means
an absent field after the caller has validated the JSON field types. Do not
convert malformed or non-string JSON fields to null.

| Declaration | Producer | Consumer |
| --- | --- | --- |
| Digest present, algorithm absent | Reject | Use `Sha256` |
| `Sha256`, `Sha384`, or `Sha512` explicitly present | Exact spelling required | Exact spelling required |
| Digest present | Lowercase hex, respectively 64/96/128 characters | Same |
| Empty digest or unknown algorithm | Reject | Reject |
| Digest absent | Allowed; not verified | Allowed; not verified |

A recognized explicit algorithm without a digest is not prohibited by the
source. No algorithm is injected into model metadata. `ReadAsync` validates the
declaration itself using its explicit role; a producer cannot accidentally get
consumer fallback by skipping a separate validation call.

Every byte supplied by the caller is significant. The helper does not decode
text, normalize newlines, extract package members, or fetch `usdasseturl`.
For a published package member, the caller supplies the extracted **member**
bytes. For an external Document, the caller first authorizes and acquires the
exact declared content.

### Completion, limits, and ownership

Content is privately staged, EOF is established, and any declared digest is
compared before an `OpenUsdArtifact` is returned. The result exposes `Length`,
`IsDigestVerified`, and independent nonwritable `OpenRead` streams. A missing
digest yields `IsDigestVerified == false`; it is never called verified.

`MaxArtifactBytes` bounds actual content bytes, independently of `Stream.Length`.
The reader consumes at most the remaining byte allowance plus one byte to
detect overflow. `MaxReadOperations` includes every read, including the EOF probe.
For example, one data read containing `abc` needs a second read to establish EOF.
Short-reading streams may need a larger operation budget.

The helper retains bounded in-memory content rather than streaming unverified
bytes into a caller destination. Staging and the owned result retain at most
twice the byte budget, plus a read buffer of at most 64 KiB and fixed hash state.
Choose application limits accordingly; there is no implicit unbounded mode.

The source remains caller-owned on success and failure and is read from its
current position. It is never disposed, rewound, sought, or implicitly acquired.
Errors can consume a prefix of the source, but return no partial artifact.
Callers must not concurrently read or mutate the same input stream.

Cancellation is checked before and after reads and before handing out content.
A source must cooperate with `Stream.ReadAsync` cancellation for prompt
interruption. The helper does not abandon an in-flight read or return its buffer
to a pool while that read still owns it.

| Failure | Public outcome |
| --- | --- |
| Invalid declaration, role, source or limits | Appropriate argument exception |
| Actual byte/read quota exhausted | `InvalidDataException` |
| Declared digest does not match | `CryptographicException` |
| Cancellation | `OperationCanceledException` |
| Source I/O failure | Original exception propagated |

Digest equality establishes integrity against the supplied declaration, not
publisher authenticity, format validity, compatibility, authorization, or source
identity. Do not set Core format/compatibility validation flags from it.

## Engine publication rules

For explicitly OpenUSD-compatible Groups and Resources, the engine validates
the finalized, authorized transaction before publication. It checks normalized
authoritative identifiers, assigned symbolic IDs, verbatim names, cross-Version
identity against the prior snapshot, the single current RootLayer and optional
`rootlayer`, and declared same-Group dependencies. A canonical Package satisfies
references to its unlisted members. Current Resource roles come from default
Versions; historical roles do not create extra current roots.

Group construction must include its required root/name in the same transaction.
Changing root roles and the entry point together is atomic. A dependency must
be removed or redirected before its target can be deleted. Core Group updates
still retain unmentioned child Resources; this adds no implicit delete syntax.
Whole-Group deletion has no remaining graph to inspect.

Schema Plugin Groups reject RootLayer artifacts, enforce the manifest/generated
schema role/format pairs, and compare bounded local `Plugins[0].Name` with the
Group's name and symbolic ID. Producer digest syntax and algorithm are checked;
local digest declarations are verified against exact stored Document bytes.
Unverified delegated digests and delegated plugin manifests whose required
identity cannot be inspected are rejected without fetching the remote URI.
Ordinary delegated artifacts without a digest remain subject to the existing
explicit Document-reference policy.

Opaque artifacts never reach format validators and never acquire fabricated
format/compatibility success flags. Mixed opaque/inspectable Versions cannot
claim cross-Version compatibility from an incomplete validator input. Similar
custom models without the domain compatibility markers retain ordinary Core
behavior and their configured validators.

Forward graph checks require access to the needed metadata. Changes to a target
also revalidate Groups borrowing that Resource through one-hop aliases. These
reverse invariant checks neither require caller read access to private referring
Groups nor expose their identity in errors. Traversal uses indexed collections
with the engine's cumulative work, byte and cancellation limits.

## Remaining boundaries

The focused collision assignment/resolution policy above is implemented and
the related live prose is amended. Immutable source capture and conformance
disposition remain with the parent; this is not a complete `OPENUSD-001` or
domain/platform qualification claim.
SPEC-009 replaces the copied schema-plugin Resource definition with an actual
Core import, so asset and plugin Groups share the declared type and can use
one-hop cross-Group aliases. Specialized USD/MaterialX validators and authorized
acquisition of verifiable external plugin
content, and complete domain/platform qualification remain separate work.
No native USD parser, renderer or code execution is hidden behind these rules.
