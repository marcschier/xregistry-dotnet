# Read-only producer model admission

`ProducerRegistryView` accepts compatible read-only source models without
rejecting them merely because they declare Group constraints, `validateformat`,
or `validatecompatibility`. These controls remain in the effective Model and
Model Source; they are not cleared or rewritten to obtain admission.

The configured Model must still match every opened source's Model Source and
effective Model. Admission is lazy: constructing a view does not open sources,
but an incompatible source fails before its entity data is used.

## Metadata obligations

Version and Resource projections use `RegistryMetadataValidator` with the
**consumer-visible Group metadata**. Ordinary Group metadata is first-source-wins,
independently of which source owns a Resource. This preserves Group scalar
`enum`, `equals`, and default semantics without reading a lower-priority Group
as a substitute. Source metadata and Documents are never mutated.

When Group constraints or static `matchversions` attributes apply, the producer
prepares the selected Resource's complete Version set before returning its
projection. Its default Version, membership count, and Version identities must
agree. Every Version comes from that one Resource owner; another source cannot
supply a missing or more convenient Version.

Static scalar paths are taken from the compiled model, including nested modeled
objects. Numeric comparisons use exact `RegistryNumber` values, not floating
point. Timestamps are first normalized by the Core metadata validator and compared
without truncating fractional seconds. Ordinary strings remain ordinal strings.
Missing/null optional values compare as absent after Core completion/defaulting.

One-hop aliases retain their existing same-type and source-origin checks. The
target's completed Version set is then checked against the referring visible
Group using Core's **constraint-only** validator. Referring-Group defaults are
not injected into target data. All target Versions, including nondefault Versions,
must satisfy those constraints. Dangling aliases and Doc-view aliases retain
their existing unresolved representation.

Incomplete sets, inaccessible required metadata, constraint violations, changed
counts, and exhausted budgets fail explicitly. A prepared Version or alias unit
is cached as validated only after all its applicable checks succeed. HTTP response
preparation finishes before headers or successful metadata are written.

## Validation evidence is not write validation

`validateformat`, `validatecompatibility`, and `strictvalidation` govern writes.
They do not require a read-only producer to acquire or revalidate Document bytes
while serving metadata.

Captured API validation outcomes and reasons are preserved. Where an API outcome
is required by Core but no captured outcome exists, the producer reports `false`
with an explicit explanation that it received no captured validation outcome.
It never reports a newly invented `true`.

Native Doc metadata is not evidence of a successful validation. For API output:

- `formatvalidated` is synthesized as `false` only when format validation is
  enabled and a `format` is present.
- `compatibilityvalidated` is synthesized as `false` only when compatibility
  validation is enabled, `format` is present, and the selected Resource's
  `meta.compatibility` is present. This may require an authorized metadata read,
  never a Document acquisition.

Doc output omits all four protocol validation flags/reasons. Identically named
properties inside opaque domain values are untouched. Explicit Document reads
and inlining retain the existing exact-byte behavior.

## Explicit URI-target policy

Core may report a host-context `target` obligation for a modeled relative URI/URL attribute.
Without an explicit policy, the producer returns `UnsupportedOperation` with
`target_policy_required`; it does not waive the obligation or fetch its URL.
Absolute URI/URL values are exempt from the Core `target` aspect and do not invoke
this policy merely because the model declares a target. They still obey URI syntax
and any separately configured destination-access policy when acquisition is requested.

Each `FederationSourceRegistration` can optionally supply a
`ProducerTargetValidator` as `validateTarget`:

```csharp
var registration = new FederationSourceRegistration(
    "authorized-origin",
    FederationRepresentation.ApiView,
    openAuthorizedSource,
    currentCredentialStamp,
    validateTarget: async (context, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.Budget.ChargeWork();
        // Apply the caller's target semantics using these owned facts:
        // context.Model, EntityPath, Metadata, Obligation, and SourceContext.
        var target = await context.ReadMetadataAsync(authorizedTargetPath);
        return target.GetProperty("xid").GetString() == expectedTargetXid;
    });
```

Returning `true` is the caller's explicit assertion that this target obligation
has been discharged. Returning `false` fails with `PolicyDenied` and
`target_policy_denied`. Policy exceptions and cancellation propagate. This policy
does not assert format or compatibility validation.

The context reader is metadata-only and bound to the **same authorized source**.
It cannot discover sources, read a Document operation, or traverse catalogs.
Requests are budgeted and recorded as access dependencies. A collection-seeded
cache entry is not treated as authorization for an explicit Entity policy read:
the source must have accepted that Entity read.

Dependency reads must be awaited and cannot overlap or outlive the callback.
Completion awaits every started dependency; catching a failed dependency inside
the callback does not turn it into successful admission. The callback must honor
cancellation and charge its additional work/I/O to `context.Budget`; this is a
trusted policy seam, not a sandbox for arbitrary callback code.

Captured source credential/access-profile stamps and retained-read authorization
continue to apply. Include any mutable target-policy revision in the existing
captured `credentialStamp` and `currentCredentialStamp` contract. Dependency
metadata used for model/target checks is retained for cursor access rechecks.

## Preserved boundaries

The producer remains read-only. Resource-unit shadowing, no source fallback,
producer-owned first-source resolution, one-hop alias/projection-origin checks,
query selection, cursor stamps, and byte/result limits remain in effect.
Opened source contexts, model identities, and resolution owners remain captured
throughout the view. They are rechecked on reuse, around target policies, and
before ordinary or query completion, including earlier sources that contributed
membership or absence decisions. A failed source-capture initialization cannot
be reused as an admitted source.
The complete `NativeRegistryContext`, including optional `CatalogOrigin`, is
retained in `ProducerRegistryResult.Origins`. Catalog context/revision,
description-Version identity, and the exact selected advertisement remain
separate from the described Registry's context and content Version identity.
They are never injected into Core metadata or opaque domain values.
Complete native captures can discharge model checks without re-opening their
Version collection or consulting later sources. Ordinary captured API behavior
keeps its existing explicit collection reads.

All work uses the existing Core/Federation contracts and framework APIs, without
a Server dependency, new NuGet packages, implicit schema validation, or changes
to source models or Documents. Focused producer tests target .NET 8 and .NET 10;
the checked-in Bridge test project targets .NET 10. Native and fresh-package
qualification are separate gates tracked in the
[roadmap](roadmap.md#native-platforms-and-package-consumers).
