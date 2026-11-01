# Document validation

`IDocumentValidator` checks schema source documents and the declared gRPC source profile, not xRegistry metadata or
application data instances. `BuiltInDocumentValidator` returns `Valid`,
`Invalid`, `Unsupported` or `Indeterminate` with owned diagnostics. It never
turns an unsupported construct, exhausted budget or unresolved reference into
successful validation.

| Format family | Current syntax policy |
| --- | --- |
| JSON Schema | draft-07, 2019-09, 2020-12 structural/keyword validation with explicitly resolved references; unimplemented dynamic/resource vocabularies are unsupported. |
| XML Schema | XSD 1.0 through hardened, preflight-bounded `XmlSchemaSet`; no DTD/external resolver. XSD 1.1 and unqualified constructs are unsupported. |
| Avro | 1.8.2 and 1.11.0 grammar, names/references, unions, records, enum/fixed and defaults. Logical-type policies are explicitly unqualified. |
| Protobuf | Proto2/3 lexer and declaration/type/field/default/enum/reserved/service checks, with explicit imports. Custom options, groups and extensions are unsupported. No protoc subprocess. |
| gRPC | `gRPC/proto3` uses the Proto3 policy and requires at least one service declared in the supplied root document. Imported source uses its declared syntax. Descriptor sets are unsupported. Source-method selection is available separately through `ApiOperationDocumentResolver`. |
| JSON Structure | Draft-04 Core document/type/namespace/reference/union/compound grammar and bounded primitive restrictions. Required extensions, inheritance and unqualified extended primitive const/enum rules are unsupported. |

Standalone XSD imports and includes use only `DocumentValidationOptions.ResolveReference`.
Each location resolves against the retrieved containing schema's URI and
inherited `xml:base`, including a base on the dependency declaration itself.
Rooted and network-path locations require an absolute base; they never become
filesystem paths. Without a Document URI or XML base, ordinary relative locations
retain named-reference callback lookup. A relative XML base without a containing
absolute origin reports `Indeterminate`; an authored absolute XML base can
establish that origin.

Locationless imports use the namespace as an explicit catalog key, not a
retrieval origin for the catalog's relative dependencies. Malformed retrieval
spellings fail before callback dispatch. Standalone schema-location fragments
remain `Unsupported`; this validator does not borrow the WSDL fragment selector.
Shared and circular dependencies reuse parsed schemas and input-byte charges.
The completed graph is depth-checked before native schema compilation, so
acquiring shorter paths first cannot hide a longer cached chain. XML parsing,
graph traversal and compilation consume the shared work budget. Cancellation
propagates without dispatching later references. These acquisition rules do not
enable additional XSD constructs or qualify complete XSD 1.0 conformance.

The bounded compiler accepts XSD 1.0 `xs:all` content models and checks their
native particle constraints: children are element declarations, the group occurs
at most once, and each child occurs at most once. Optional elements retain
`minOccurs="0"`; invalid group placement, duplicate element declarations and
illegal occurrence values fail schema compilation. This validates schema
declarations, not application instances. WSDL 2.0 element selection shares that
compiler and retains the authored `xs:all` rather than translating its children
into separate message choices.

Finite occurrence reservations parse both `minOccurs` and `maxOccurs` using
XML Schema integer syntax, including whitespace, signs and leading zeros.
Arbitrary-width values cannot bypass the budget through integer overflow.
The larger finite occurrence value contributes to the cumulative structural
reservation; `maxOccurs="unbounded"` still reserves a finite minimum.
Oversized reservations report `Indeterminate` with `limit.work` before
compilation. Malformed or negative occurrence values report `Invalid`.
This does not enable `xs:choice`, type derivation, identity constraints or
the other explicitly unsupported compiler constructs.

Protobuf map declarations reserve their synthetic entry symbol in the containing
message: `foo` reserves `FooEntry`, and `map_field` reserves `MapFieldEntry`.
An authored message, enum, field, oneof or another map cannot reuse that symbol,
regardless of declaration order. The reservation does not affect other message
scopes. This follows the [published map rules](https://protobuf.dev/programming-guides/proto3/#maps)
and applies to the shared gRPC source parser as well.

Proto3 checks the uniqueness of effective JSON field names, including names
derived from underscore-separated field identifiers and explicit `json_name`
overrides. Proto2 retains its best-effort mapping policy: collisions are rejected
when both fields specify `json_name`, but not when either name is derived.
These syntax-specific rules follow the
[published JSON-handling design](https://github.com/protocolbuffers/protobuf/blob/v33.0/docs/design/editions/edition-zero-json-handling.md).
The `deprecated_legacy_json_field_conflicts` option remains unsupported; it
cannot silently disable these checks.

The gRPC source profile checks import cycles in both document validation and
operation selection. A cycle reports `Indeterminate` with `dependency.cycle`,
never successful validation. The server adapter leaves such a document unchecked
in non-strict mode and rejects it in strict mode. Shared acyclic dependencies
remain valid and are retrieved once per distinct document.
When `DocumentValidationOptions.DocumentUri` is supplied for gRPC source,
imports resolve against the URI of each importing document before the callback
is invoked. Malformed URI spellings reject validation before normalization or
retrieval. Without a base URI, the document validator retains named-reference
lookup. The server adapter uses the same configured base and callback; it does
not acquire dependencies independently.

These are declared policies, not claims of universal equivalence to every
upstream schema tool or version. All parsers share finite document, aggregate
byte, depth, node, reference and work budgets. JSON ingestion shares the core's
strict UTF-8, Unicode escape, duplicate-member and number checks.
Protobuf charges each field/value comparison against a reserved-number range to
the cumulative work budget. Exhausting that budget reports `Indeterminate` with
`limit.work`, rather than accepting a declaration whose reservation checks were
not completed.

## WSDL 1.1 operation selection

`ApiOperationDocumentResolver` selects abstract operations from `WSDL/1.1`
XML Documents with `application/xml` or `text/xml` media types. The locator
uses the expanded port type name, independent of XML prefix spelling:
`#interfaces/%7Burn:users%7DPort/operations/Read`. Operation names remain exact
local names; selection does not choose a service address or protocol binding.

A name-only locator reports `Ambiguous` for overloaded operations. The
`input/<name>/output/<name>` suffix selects both effective message names,
including the defaults in
[WSDL 1.1 section 2.4.5](https://www.w3.org/TR/2001/NOTE-wsdl-20010315).
Use `!` for an absent input or output. Names use XML NMTOKEN syntax, so a
colon or leading digit is not rejected by an invented NCName restriction.
Effective input and output names must be unique within each port type,
including collisions introduced by defaults or between different operations.

`WsdlOperation` retains the native operation XML with its in-scope namespaces,
expanded message and fault QNames, effective names and transmission pattern.
One-way, notification, request-response and solicit-response remain distinct.
No JSON operation or JSON Pointer is fabricated. Malformed XML, DTDs,
wrong WSDL roots, missing message references, duplicate port types and
indistinguishable overloads fail before selection.

WSDL 1.1 imports use only the explicit caller dependency resolver.
Each `location` resolves against its containing Document URI and inherited
`xml:base` values. Rooted and network-path references retain the authored
authority; they are not converted to local file paths. Shared imported Documents
are parsed once, while every import declaration must agree with the acquired
target namespace. An operation selected from an imported WSDL retains that
Document's URI. No callback means an unresolved dependency, not implicit network
access or a successful partial selection.

Imported XML Schema Documents are acquired, parsed with DTD processing prohibited,
and checked for the declared target namespace. Their definitions remain schema
data; this selector does not compile schemas, validate message parts or follow
schema imports/includes. Required WSDL extensions and other imported document
kinds report `Unsupported`. Unavailable, denied, malformed or cyclic imports
prevent selection even when the entry Document contains a matching operation.
The callback owns authorization, redirects, bounded I/O and credential policy.
Its bytes are associated with the requested import URI; the callback contract
does not return a distinct post-redirect origin.

`SupportsCallerProvidedWsdlImports` is true; `SupportsApiDocumentValidation`
remains false. Embedded schema validity and complete binding/service
validation remain unsupported. All acquired Documents share byte, node, depth,
work, dependency-count and deadline limits; cancellation propagates without
dispatching later dependencies.

The HTTP workflow acquires an older exact API Version after its default changes
and selects the retained WSDL operation through an explicitly authorized relative
message import after source and host disposal.
It preserves entry bytes and native operation meaning without asserting that
registration or selection establishes full document validity.

## WSDL 2.0 operation selection

`WSDL/2.0` XML selection uses expanded QNames for both interface and operation,
for example `#interfaces/%7Burn:api%7DPort/operations/%7Burn:api%7DRead`.
The WSDL 1.1 input/output overload suffix is not accepted. An inherited operation
retains its declaring interface and namespace; a derived interface does not
rename it. Diamond inheritance reaches the same component once. Distinct
declaring interfaces are different parent components, so operations with the
same QName conflict even when their XML declarations look alike.

`Wsdl20Operation` owns the native operation XML, declaring interface, expanded
operation name, message-exchange-pattern IRI and effective style set. Omitted
patterns retain WSDL's `in-out` default. Omitted styles use the declaring
interface's `styleDefault`, not a derived interface's defaults; an explicitly
empty style overrides that default. Selection does not choose a binding or
endpoint, invoke a service or translate the operation into JSON.

This policy checks the three message-exchange patterns in
[WSDL 2.0 Adjuncts](https://www.w3.org/TR/2007/REC-wsdl20-adjuncts-20070626/)
and the five patterns in the
[additional-MEP Note](https://www.w3.org/TR/2007/NOTE-wsdl20-additional-meps-20070626/)
with `#any`, `#none` or resolved XSD element content. The Note is non-normative; support for
its named patterns is part of this implementation's declared policy.
Placeholder labels and directions retain
their native meaning; XML child order does not substitute for those labels.
Invalid names, duplicate or missing interfaces, inheritance cycles, conflicting
operations and malformed placeholders fail before returning a match.
Pointers containing only unimplemented schemes, other type systems, custom patterns and required extensions report
`Unsupported`, never a successful unchecked selection.

Fault references resolve against the operation's declaring interface and its
extended interfaces, not every interface visible from the selected derived
interface. `Faults` retains each fault's expanded name, declaring interface,
owned declaration XML, native content model, direction and effective
message label. Diamond paths retain the same fault component; different
declaring parents with the same fault QName conflict.

The native propagation rule determines each fault's applicable placeholder.
`in-only` and `out-only` prohibit faults. Replacement faults use the direction
of the second message; triggering faults use the opposite direction from the
associated message. Omitted labels use the single applicable placeholder.
Explicit labels must match that placeholder, and each fault-name/label pair
must be unique within an operation. Malformed names, unavailable fault
components and invalid directions or labels prevent partial selection.
XML name, QName, label and IRI attributes use their native whitespace
facets without normalizing the resulting namespace or pattern identity.

WSDL 2.0 selection shares the hardened XML reader and finite byte, node, depth,
work and deadline limits. Inheritance is bounded separately at 64 edges,
including cached chains; declaration order cannot bypass that limit.
WSDL 2.0 imports and includes use the explicit caller dependency callback.
Includes require matching target namespaces; imports require foreign target
namespaces. Location hints resolve against each containing Document and
inherited `xml:base`. Shared and circular includes reuse the same parsed
Document without duplicating its declarations, as required by
[WSDL 2.0 sections 4.1 and 4.2](https://www.w3.org/TR/2007/REC-wsdl20-20070626/#includes).
Foreign QNames require an import in the Document that authors the reference;
an included Document's import does not grant that permission to its includer.
Documents importing the same namespace share its acquired components.

An import without a location uses already acquired namespace components or
asks the callback for that namespace URI. Such a catalog lookup does not
establish a retrieval origin; relative dependencies still need an explicit base.
Unavailable import hints do not invalidate an otherwise resolved operation.
If missing namespace information prevents resolving an authored component,
selection reports `Indeterminate`, not an invented missing-component result.
Includes still require their Documents. Denied dependencies,
namespace contradictions, malformed XML and exhausted aggregate budgets prevent
partial selection. The callback owns redirect and credential policy, and its
bytes remain associated with the requested location rather than an inferred
post-redirect URI. Selected operations retain their declaring Document's origin.

Message and fault element QNames resolve to named global elements in inline
or explicitly imported XSD 1.0 schemas. WSDL includes expose their schemas;
WSDL imports do not expose the imported Document's schemas to the importer.
Direct `types/xs:import` declarations expose the imported namespace. An inline
schema's `xs:import` supplies compiler dependencies without making foreign
elements referenceable by the containing WSDL. Multiple visible schemas can
share a namespace, but cannot duplicate global elements or global types.
Element and type names occupy separate symbol spaces, so a type cannot satisfy
a message's element reference, even when it has the same QName.

`SchemaElements` retains each referenced global element's expanded name,
declaration XML, declaring schema origin and complete acquired schema context.
`SchemaDocumentUris` records each context Document's origin in the same order as
`SchemaDocuments`; a namespace catalog lookup retains a null origin rather than
treating the namespace as a retrieval location.
Fault references expose their element through `SchemaElement`; `#any` and
`#none` have no schema element. The shared bounded XSD compiler checks the
explicitly linked schema graph before selection, with its network resolver
disabled. Input bytes and nodes already charged
by WSDL acquisition are not charged again; copying, reparsing and compilation
still consume cumulative work. Schema imports/includes use the same caller
callback, distinct-acquisition limit and aggregate budgets as WSDL dependencies.
Locations use the containing schema's origin and inherited `xml:base`. Shared
and circular dependencies are acquired once; cached chains cannot bypass the
64-edge depth limit. Includes expose declarations, including native chameleon
adoption of a no-namespace schema into each containing namespace, without
rewriting the retained schema. Imports require their declared namespace;
direct WSDL imports cannot import a no-namespace schema.

Locationless schema imports use resolved namespace Documents, including all
matching inline Documents needed by the compiler, or ask the callback for the
namespace URI. Declared location hints are acquired before their dependency
graphs; unavailable import hints can use resolved namespace Documents or an
explicit catalog, while includes still require their location's Document.
Missing schema information reports `Indeterminate`; denied
dependencies, malformed XML and namespace contradictions prevent partial
selection. XSD constructs outside the bounded compiler
policy report `Unsupported`. These checks do not establish full schema or WSDL
validation.

Schema locations can select an embedded schema with an XML ID shorthand
fragment, such as `other.wsdl#payload`, or the W3C
[`element()` scheme](https://www.w3.org/TR/2003/REC-xptr-element-20030325/).
An ID matches an XSD `id` or
`xml:id` and must identify one schema element; duplicate IDs and targets of
the wrong kind prevent selection. Fragment decoding does not change the
schema's namespace identity. The callback receives only the fragment-free
retrieval URI. Multiple fragments and whole-schema references reuse one
captured Document and share the distinct-acquisition count.

A fragment-only location uses the containing Document's effective XML base;
it does not necessarily identify the authored Document when `xml:base` changes
that base. Without an origin or XML base, a fragment-only reference can select
the authored Document's own inline schema without acquisition. Retained schema
context includes inherited XML bases when known. An external XML container
supplies only the selected schema, not its WSDL interfaces. Missing Documents
or selected elements report `Indeterminate`.

`element(payload)` selects an ID; `element(payload/2)` selects its second
element child, and `element(/1/2)` selects the root's second element child.
Text, comments and processing instructions do not count as children.
Following the
[XPointer Framework](https://www.w3.org/TR/2003/REC-xptr-framework-20030325/),
pointer parts are tried in authored order. Unknown schemes are skipped;
invalid `element()` data and missing targets permit the next part to run.
The first located element stops evaluation, even when it is not a schema.
An out-of-range ordinal never selects a nearby element. Percent decoding occurs
once before framework parsing; scheme data accepts balanced parentheses and
the `^^`, `^(` and `^)` escapes. Malformed framework syntax is invalid before
retrieval. If no supported part locates an element, selection is indeterminate;
empty fragments and pointers containing only unsupported schemes remain
unsupported. This policy does not evaluate XPath `xpointer()` expressions,
process DTD IDs or infer ID types from application schemas.

The actual HTTP workflow retains an older exact Version and selects its inherited
operation, fault and externally imported element declarations through explicit
relative WSDL and `element()` schema-fragment dependencies after default change and
source disposal.

## Compatibility

`IDocumentCompatibilityValidator.CheckAsync` accepts candidate bytes and
authoritative ancestors in **nearest-first** order. `backward` means the new
reader can read the previous writer; `forward` reverses that relationship;
`full` requires both. Transitive modes examine every supplied ancestor rather
than only the nearest one.

The built-in Avro policy implements reader/writer resolution, numeric
promotions, unions, named/field aliases, record defaults, enum symbol/default
resolution, fixed sizes and recursive types. Other format families currently
provide only validated exact-schema identity; changed schemas return
`Unsupported`, not an invented compatibility decision. Hosts may inject
separately qualified policies.

The result distinguishes `Compatible`, `Incompatible`, `Unsupported` and
`Indeterminate`. Invalid schema input cannot be used to prove compatibility.
History and recursive work are bounded.

## Applying the core model policy

`DocumentValidationPolicy.EvaluateAsync` composes format, compatibility and
strictness settings. Invalid format or proven incompatibility always rejects
publication. Unsupported/indeterminate results are accepted only when the model
is non-strict, with explicit diagnostics and unchecked metadata.

`formatvalidated: false` and `compatibilityvalidated: false` mean **not checked**,
never "checked and failed". Rejected checks do not become false-success fields.
Absent format disables both checks; absent compatibility mode disables that
check. The host still owns authorization, entity lifecycle, correct ancestor
selection and atomic publication.

Managed tests run on both TFMs; the syntax/compatibility slice has also executed
as an actual Windows x64 native binary. All-platform and final package-consumer
qualification are not complete; see the [roadmap](roadmap.md#native-platforms-and-package-consumers).
