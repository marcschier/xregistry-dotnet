# Built-in model sources

For an application-specific worked example, start with
[Define a custom xRegistry model](custom-models.md).

`XRegistry.Models` embeds seven model sources from the pinned specification
baseline and explicit [source corrections](spec-feedback.md): Core, Endpoint, Message, Schema, CloudEvents,
Registry-of-Registries, and OpenUSD. Model-source loading does not require a
filesystem, network connection, or domain runtime.

```csharp
using XRegistry.Models;

using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Registry);
var groups = source.RootElement.GetProperty("groups");
```

`OpenSource` returns a caller-owned stream of byte-identical source bytes.
`LoadSource` returns an independently owned `JsonDocument`; disposing one call's
document does not invalidate another call's data.

`LoadSource` returns **model sources, not compiled effective models**. The CloudEvents
source intentionally retains its relative `$includes`; a model compiler needs
an explicitly configured resolver. Loading a source does not claim to enforce
model, lifecycle, format, compatibility, or application-protocol behavior.

`BuiltInRegistryModels.Compile(kind)` compiles an effective core model using only
the explicitly packaged model-source resolver. It resolves relative model
includes without network access. `RegistryDomainRules.ValidateEndpointUsage`
checks the Endpoint domain's allowed role combinations separately: those
procedural rules are not core scalar `enum` constraints on an array.

SPEC-008 aligns Message declaration names and shapes with the domain prose:
`basemessage`, HTTP `status` and string-map `query`, NATS `reply-to`, optional
AMQP `subject`, and Endpoint Group-target references. Corrected source bytes
are used directly; no effective-model compatibility alias bypasses validation.
Message HTTP status literals must be 100-599 and cannot coexist with `method`.
Level-1 status templates are preserved for later substitution.

`python eng\sync_models.py --check` compares packaged resources with the pinned
corpus and explicit successor correction manifest. Intentional source updates use the reviewed specification workflow and
`python eng\sync_models.py`; do not edit copied resources directly.
The source Apache-2.0 license is included in the package.

## Compiler and metadata validation

`RegistryModel.CompileCaptured(originalSource, resolvedSource, options, token)`
interprets previously captured model material without any external resolver.
It preserves `Source`, verifies directive and JSON Pointer syntax, rejects
local cycles and overlong chains before following the disallowed hop, and
checks known local members using the normal local/earlier-include precedence.
This also applies when another include is external: an unrelated external
reference cannot override locally known definitions. The resolved copy must
be include-free and satisfy the full model language.

Directory mappings and OCI use this shared interpretation. Capture validation
is bounded by the supplied JSON and work limits; it does not download a model,
authenticate a publisher, or reconstruct unretained external include contents.
An earlier external include can make later included members unknowable, but
explicit authored members still take precedence. A capture containing only
local includes is compared completely. Binding-specific URI access policies
remain separate from model-language validation.

Conditional Version extensions cannot shadow Resource-level navigation names,
even in inactive nested `ifvalues` branches. Model annotation references receive
URL/URI syntax checks without fetching; a supplied icon cannot be empty.
Imported collection overlays are checked after their system definitions exist,
so immutable collection URLs survive compilation and frozen-model restart
without weakening their flags or duplicating imported Resource type identity.

Flat-model peer-name processing retains measured allocations of roughly
1.1/2.1/4.2 MB for 256/512/1024 attributes. Expanded and effective JSON output
enforces committed-byte and output-capacity limits before retaining an
oversized result.
Exact-byte/+1, reentrant/independent-budget and owning-JSON regressions cover
these distinctions. Elapsed samples on this shared host are not a latency SLO.

Engine metadata PATCH supplies retained discriminator context explicitly, while
partial HTTP headers still require their own discriminator values. Resource
XID targets exclude Meta, built-in compatibility modes compare
case-insensitively without changing extension enums, and timestamp enums and
Group constraints compare exact normalized instants. `ValidateGroupConstraints`
provides a pure host-facing check for already-populated values: it neither
applies defaults nor mutates metadata or acquires references.

Public TUnit tests compare all seven byte hashes with independent frozen values,
check model-specific contracts, unknown-kind errors and ownership. The
`eng/test-model-packages.ps1` probe packs into a new local feed, uses a fresh
package cache and package references (not project references), and publishes and
runs real native consumers for both TFMs on the executing platform.

## Domain behavior and current boundaries

The actual Schema model supports mixed formats in an unconstrained Schema
Group, optional per-Group format defaults/constraints, wildcard extension
metadata and Core numeric Version generation. The domain tests cover the
9-to-10 ordering boundary, non-reuse after deletion, exact Documents and
concrete incompatible Avro evolution requiring a different Resource. Text
schemas sent as inline JSON strings need the appropriate non-JSON
`contenttype` (for example `text/plain` for Protobuf); `format` does not
silently change the Core media-type conversion contract.

CloudEvents composite exports use the allowed single-JSON-object form and
permit any of the three subregistries to be absent. This does not add a YAML
parser or establish that every illustrative document in the specification has
been semantically reviewed.

Registry-of-Registries publication has [pure catalog rules](catalog.md) shared
with Federation. Server writes opt in through the Resource's explicit
`modelcompatiblewith` identity, not through collection names or loading the
unmodified packaged model. Trusted hosts can deliberately select
`CompileForServer(RegistryModelKind.Registry)` or `CompileAllForServer()`; the
FileServer sample uses these for its Registry/all built-in choices. These
presets annotate model source while preserving packaged includes/imports and
leave the ordinary compilation and custom-model paths unchanged.
These checks preserve endpoint-free base entries
and unknown extensions and never authorize or acquire an advertised source.

Concrete Message `dataschemauri` selection is available through
[`SchemaObjectSelector`](schema-selection.md): JSON Pointer, Avro record,
Protobuf message and bounded XSD structural selectors retain the complete
authorized document context. Message inheritance and Endpoint authoring/consumer
materialization have separate [Message](message-materialization.md) and
[Endpoint](endpoint-templates.md) APIs. Neither definition materialization nor
schema selection sends runtime application messages or implicitly acquires a URI.

Some reviewed obligations remain explicit application/source/policy or
qualification duties. Non-identical non-Avro schema evolution remains
unsupported rather than declared compatible; YAML and arbitrary schema dialects
are not silently accepted. See the generated [conformance ledger](conformance.md);
these boundaries are not hidden behind successful model compilation. Related
qualification work is centralized in the [roadmap](roadmap.md).
