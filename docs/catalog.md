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

`RegistryDomainRules.ValidateCatalogMetadata` checks a completed description
Version, including every explicitly advertised, known HTTP/Git/File/OCI
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
parameters or trying a fallback. OPC UA remains opaque and unsupported by this
implementation; no native OPC UA semantics are validated.

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
