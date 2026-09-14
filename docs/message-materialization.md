# Message definition materialization

`XRegistry.Models` exposes `MessageDefinitionMaterializer.MaterializeAsync` as
a public, transport-independent package entry. It resolves canonical
`basemessage` chains and composes definition metadata. It does not implement a
broker, fetch schemas, render application-context templates, or publish runtime
messages. The timestamp sentinel remains a constraint, not a request to read a
clock during definition materialization.

## Explicit source and Registry context

A `MessageDefinition` contains owned `RegistryJson` metadata and the
caller-established `Location`. Optional `RegistryRoot`, `Model`, and `Path`
describe its actual Registry/type context. These values are independent of
untrusted `self`, `xid`, authorization or compatibility claims in metadata.
`Path` disambiguates models containing multiple distinct Message Resource types.

The `MessageDefinitionSource` callback is the only acquisition seam. It must
authorize the metadata read before accessing a source. It receives the authored
reference, resolved target URI, referring definition, local typed XID if any,
depth, and source JSON limits. It returns an owned definition with its actual
source context, or an explicit unresolved outcome.

```csharp
using XRegistry;
using XRegistry.Models;

var model = BuiltInRegistryModels.Compile(RegistryModelKind.Message);
var registryRoot = new Uri("https://registry.example/registry");
var baseUri = new Uri(
    "https://registry.example/registry/messagegroups/shared/messages/base");
var baseline = new MessageDefinition(RegistryJson.Parse("""
    {"envelope":"CloudEvents/1.0",
     "envelopemetadata":{"type":{"value":"example.created"}},
     "dataschemaformat":"JSONSchema/draft-07",
     "dataschemauri":"https://schema.example/created"}
    """), baseUri, registryRoot, model);
var authored = new MessageDefinition(RegistryJson.Parse("""
    {"basemessage":"/messagegroups/shared/messages/base",
     "protocol":"HTTP","protocoloptions":{"method":"POST"}}
    """), new Uri(
        "https://registry.example/registry/messagegroups/http/messages/created"),
    registryRoot, model);

var result = await MessageDefinitionMaterializer.MaterializeAsync(
    authored,
    (request, token) =>
    {
        token.ThrowIfCancellationRequested();
        // This example authorizes only one already-owned metadata object.
        if (request.TargetUri.AbsoluteUri != baseUri.AbsoluteUri)
        {
            return ValueTask.FromResult(MessageDefinitionSourceResult.Unresolved(
                MessageDefinitionSourceStatus.AccessDenied));
        }
        return ValueTask.FromResult(MessageDefinitionSourceResult.Found(baseline));
    });

if (!result.IsComplete)
{
    Console.WriteLine(result.UnresolvedStatus);
}
else
{
    Console.WriteLine(result.Constraints.BindingKind); // ExplicitProtocol
    Console.WriteLine(result.Constraints.PayloadContentType); // application/json
}
```

A client can implement the same callback using its own already-authorized
metadata reader, constructing `MessageDefinition` from the returned
`RegistryJson` and actual Registry/base-URI context. No Models dependency on
Client or Federation is needed. An acquiring callback should use
`request.JsonLimits` while reading rather than allocating an unbounded response
and waiting for the materializer to reject it afterward. Redirects, credentials,
transport trust, cancellation inside synchronous callback code and any I/O
deadline remain caller responsibilities.

Local `/...` references require both an explicit Registry root and effective
model. They must identify Message Resources or Versions, including shared
imported Message types. Group, Meta, collection, `$details`, network-path and
server-generated pseudo-Version targets are rejected before acquisition.
Other relative forms are not guessed. Absolute locations use the BCL URI
representation and must not contain user information.
The selected Registry root retains significant empty path segments; only a
missing terminal slash is added for relative resolution. Server admission is
separate: it checks local base kinds/model types without requiring existence or
acquiring any definition.

## Composition and constraints

Acquisition proceeds from the authored definition toward the oldest base.
Composition then proceeds in reverse: derived values override inherited values,
objects/maps merge recursively by exact property name, and other JSON values
replace the previous value. Arrays are replaced atomically, preserving order
and duplicates; literal null is an overlay value, not an implicit JSON Merge
Patch deletion. Unknown/schema objects receive only this structural treatment.
Their contents do not become source references, templates, property declarations
or schema-language composition instructions.

The result preserves the original definition and returns the actual traversed
`Definitions`, the requested `References`, and a JSON-pointer-indexed
`PropertySources` map. An inherited relative schema/specification URI retains
its declaring definition's Registry and base-URI context; it is not rebound to
the derived Registry. URI normalization is used to detect repeated requests and
actual-source aliases, not to rewrite retained metadata values. Fragments remain
part of definition identity.

For a complete chain, the selected Message model validates supplied Core
attributes and the composed domain metadata. Definition-only inputs need not
fabricate absent server-generated entity attributes; supplied Core fields do
not use readonly-input discarding to bypass validation. A custom model must
explicitly declare its extensions. The materializer reuses
`RegistryDomainRules.CompleteMessageMetadata` for parent-owned typed-property
rules and defaults after inheritance, not before an overlay.

Property declarations are completed from raw owned JSON before they cross the
generic model-completion step, and their domain-completed maps are retained
after that step. Thus a modeled `any` value inside a declaration Object cannot
silently turn `value: null` into an absent constraint. An absent `value` stays
absent; inherited and overriding literal-null constraints keep their source
provenance. Declaration `type`/`required` defaults come from the domain helper,
whether a caller's model uses nested Objects or opaque declaration maps.
This early pass is limited to declaration data: normal top-level model defaults
and full cross-aspect validation retain their completion order.

Composed definitions must satisfy schema-format dependencies, schema locator
exclusions, selected protocol rules, literal property types and same-plane
content-type agreement. Changing a selector does not silently erase inherited
options. Contradictory composed declarations fail rather than being repaired
by dropping constraints.

`Constraints` exposes envelope, protocol and payload views. An explicit protocol
has precedence over the envelope's implicit bindings. Envelope-only definitions
do not invent a concrete protocol. Structured CloudEvents distinguish the outer
representation's content type from the nested payload type.

CloudEvents materialization makes its implicit required declarations and time
sentinel explicit and infers a schema constraint from a declared `dataschemauri`.
The documented JSONSchema-to-`application/json` inference is supported.
`PayloadContentTypeResolver` permits a caller's pure mapping for other schema
formats. An unknown mapping produces an explicit
`message_payload_content_type` obligation. Inline schema data does not provide
an independently established URI, so the materializer records a
`message_schema_uri` obligation instead of manufacturing one.

`IsComplete` describes base-chain availability, not proof of schema identity,
schema validity, transport support, endpoint reachability or discharged
`Obligations`. Inspect both the completeness result and outstanding obligations
before constructing or matching runtime messages. No Group metadata or broker
state is implicitly acquired.

## Unavailable sources, bounds and failures

The Message specification permits dangling, unreachable and unauthorized bases
without a resolution error. A source reports `NotFound`, `Unavailable`, or
`AccessDenied` explicitly; the materializer stops and returns `IsComplete=false`
with the first unresolved reference/status and available overlays. Missing
callbacks return `SourceNotConfigured`. These are not empty successful
definitions, and no alternative source is tried. Incomplete metadata does not
claim complete constraint validation.

Unexpected callback exceptions propagate. Invalid JSON/model/domain metadata,
cycles and exhausted limits fail with structured `RegistryException` diagnostics;
they are not converted into tolerated missing-base outcomes. Cancellation is
observed before source calls, during traversal/composition, after acquisition
and while awaiting an asynchronous callback that ignores its token.

`MessageMaterializationOptions` supplies inclusive finite limits:

- `MaxDepth`: base-reference depth, with the authored root at zero; defaults to 16.
- `MaxTotalBytes`: aggregate UTF-8 metadata bytes across acquired definitions;
  defaults to 16 MiB.
- `MaxWork`: explicitly charged data traversal, merge, copying, inference and
  provenance work; defaults to 1,000,000 units.
- `JsonLimits`: Core per-definition/output bytes, nesting, nodes and exact
  number-token limits. Generated defaults must fit the output limit too.

Model compilation/validation also retains its own Core limits. These limits
bound this operation's work and metadata; they are not a promise to preempt
arbitrary synchronous caller code or to cap the complete process heap.

Materializer diagnostics include `message_cycle`, `message_depth_limit`,
`message_byte_limit`, `message_work_limit`, and `message_context_required`.
Core/domain errors such as `invalid_attribute`, `byte_limit` and `node_limit`
retain their precise paths. Returned metadata is independently owned and remains
usable after input buffers/documents are released.

This is definition materialization evidence, not a claim of complete Message
instance generation/matching, schema-language execution, native qualification,
or overall client/server completion.
