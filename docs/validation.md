# Document validation

`IDocumentValidator` checks schema source documents, not xRegistry metadata or
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
| JSON Structure | Draft-04 Core document/type/namespace/reference/union/compound grammar and bounded primitive restrictions. Required extensions, inheritance and unqualified extended primitive const/enum rules are unsupported. |

These are declared policies, not claims of universal equivalence to every
upstream schema tool or version. All parsers share finite document, aggregate
byte, depth, node, reference and work budgets. JSON ingestion shares the core's
strict UTF-8, Unicode escape, duplicate-member and number checks.

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
as an actual Windows x64 native binary. Complete all-platform and final
package-consumer qualification remains required.
