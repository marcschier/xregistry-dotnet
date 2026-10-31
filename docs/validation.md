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
