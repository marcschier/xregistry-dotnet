# Concrete schema selection

`SchemaObjectSelector.SelectAsync` in `XRegistry.Validation` implements the
concrete-type selection rules in the active Schema Registry specification,
sections 4.3.1-4.3.5. It accepts **already acquired Document bytes** and the raw
`dataschemauri` (or another schema-selection URI). It does not read a Registry,
open a file, issue HTTP requests, or acquire that URI again.

```csharp
using XRegistry.Validation;

const string documentReference =
    "#/schemagroups/team:prod/schemas/events/versions/v:1";

var result = await SchemaObjectSelector.SelectAsync(
    "Protobuf/3",
    """syntax="proto3"; package telemetry; message Event { string id=1; }"""u8.ToArray(),
    documentReference + ":telemetry.Event",
    new SchemaObjectSelectionOptions
    {
        DocumentReference = documentReference,
        Validation = new DocumentValidationOptions { MaxDocumentBytes = 65_536 }
    },
    cancellationToken);

if (result.Status == SchemaObjectSelectionStatus.Selected)
{
    SchemaObject selected = result.Selection!;
    // selected.TypeName is "telemetry.Event"; Document is the entire .proto file.
}
else
{
    // Handle Status and Diagnostics; Selection is null.
}
```

The caller owns acquisition authorization, origin/version consistency, format
metadata, and binding the supplied bytes to `DocumentReference`. Selection does
not prove that binding or validate application data instances. Models-only
Message materialization can remain independent of Validation: its consumer
acquires the authorized Document and then calls this separate selection seam.
No Client adapter or Models-to-Validation dependency is required.

## Results and ownership

`SchemaObjectSelectionResult` has a `Status`, nullable `Selection`, and owned
`DocumentDiagnostic` entries with stable `Path`, `Code`, and `Detail`.

| Status | Meaning |
| --- | --- |
| `Selected` | Exactly one supported concrete declaration was selected. |
| `Invalid` | Invalid schema/selector, or a node of the wrong kind. |
| `NotFound` | A well-formed selector has no match in the supplied Document. |
| `Ambiguous` | Multiple matches; no first-match fallback. |
| `Unsupported` | Unimplemented format/version, schema construct, or XPath policy. |
| `Indeterminate` | Budget exhaustion or unavailable explicit dependency. |

Every non-selection outcome has a diagnostic and a null `Selection`.
`format.unsupported` is distinct from selector diagnostics such as
`selection.pointer`, `selection.name`, `selection.not_type`,
`selection.not_found`, and `selection.ambiguous`. Invalid options throw argument
exceptions. Cancellation propagates `OperationCanceledException`; exceptions
from an explicit resolver also propagate instead of being disguised as schema
decisions.

The selected `SchemaObject` retains:

- Original `Format`, `SchemaUri`, and exact raw `DocumentReference`.
- Owned complete `Document` bytes, not a cut-out definition or Protobuf snippet.
- `Kind`, fully qualified/expanded `TypeName` where applicable, and `JsonPointer`
  or `XPath` as appropriate.
- An independently owned `Json` node, or an `Xml` getter that returns a fresh
  mutable copy with inherited namespace bindings.
- Explicit `DocumentUri` and owned dependency `References`, keyed by the exact
  resolver request strings.

Input and resolver buffers must remain unchanged during the operation. They can
be released or reused after it completes. Returned JSON survives parser disposal;
mutating an XML copy cannot change subsequent copies or the original Document.
A selected JSON `$ref` or XML QName must still be interpreted in the retained
Document/reference context, not as a standalone schema.

## Format policy

Selection first uses the existing [bounded document validators](validation.md).
Their unsupported constructs remain unsupported; selection is not an alternative
semantic engine or a claim of universal schema-tool conformance.

| Accepted formats (case-insensitive) | Concrete selection |
| --- | --- |
| `JsonSchema/draft-07`, `JsonSchema/draft/2019-09`, `JsonSchema/draft/2020-12` | Root schema object by default, or RFC 6901 JSON Pointer. Only object-valued schema positions traversed by the dialect's schema keywords are eligible. |
| `JsonStructure`, `JsonStructure/draft-04` | Explicit JSON Pointer, the document's declared `$root`, or its root type. Definitions namespaces are not types. |
| `Avro/1.8.2`, `Avro/1.11.0` | Optional record-name suffix. Without it the root must itself be a record; there is no search for a first nested record. Explicit names may select nested records. |
| `Protobuf/2`, `Protobuf/3` | **Required** message-name suffix. Only messages declared in the supplied root Document, including nested messages, are selectable. Imported declarations provide validation context, not additional selection targets. |
| `XSD/1.0` | Bounded structural XPath to a global named simple/complex type or element. Without a selector, use the unique global element; if there are no global elements, use the unique global named type. Zero/multiple candidates are `NotFound`/`Ambiguous`. |

JSON metadata, schema-map containers, annotation objects (even those containing
`type`), strings, arrays, and boolean schemas are not selectable schema objects.
JSON Structure definitions-only libraries require an explicit type selector
unless they declare `$root`; the first definition is never inferred.

Avro/Protobuf names are case-sensitive. An exact full name takes precedence,
including a full name in the empty namespace. Otherwise a bare name requires a
unique matching record/message. Dotted names require an exact full-name match;
partial package paths and Avro aliases are not selectors. Protobuf also accepts
a leading `.` for an absolute full name. Enums, Avro fixed types and primitives
are not record/message declarations; an exact wrong-kind name is never
reinterpreted as another namespaced declaration.

## Raw URI identity and fragments

For a conventional URI such as `https://schemas.test/event.json#/$defs/Event`,
the part before `#` is the Document reference and the fragment is the selector.
For an existing Registry fragment, supply the **exact raw acquisition reference**:

```text
DocumentReference:
  #/schemagroups/team:prod/schemas/events/versions/v:1

Avro/Protobuf selection:
  #/schemagroups/team:prod/schemas/events/versions/v:1:telemetry.Event

JSON selection:
  #/schemagroups/team:prod/schemas/events/versions/v:1/definitions/Event
```

The selector never finds the boundary by guessing path lengths, splitting at the
last colon, or normalizing IDs. Different percent-escape spellings are not
interchangeable for this raw prefix match. The same rule supports absolute URIs
whose acquisition reference already contains a fragment. A fragment-only URI
without `DocumentReference` is rejected as ambiguous context. Set
`DocumentReference = ""` explicitly for an unnamed in-memory Document and a
standalone `#/...` selector.

After the raw boundary is established, percent escapes are decoded as strict
UTF-8 exactly once. Malformed escapes, invalid Unicode and invalid UTF-8 sequences
are rejected; `+` is not converted to a space. JSON Pointer then interprets `~0`
and `~1`, once. For example, `%252F` denotes the literal characters `%2F`, not a
path separator. A colon separator may itself be percent-encoded after the exact
Document prefix. An explicit empty local `:` suffix is invalid, not an omitted
Avro suffix.

## Bounded XML selectors

Supported structural paths have one leading `/` or `//`, followed by child steps.
Each step is a QName, `*`, or `prefix:*`, optionally followed by one exact
`[@name='NCName']` predicate (double quotes and whitespace around predicate tokens
are also supported).

```text
/xs:schema/xs:complexType[@name='Telemetry']
/xs:schema/xs:element[@name="Event"]
//xsd:simpleType[@name='Count']
/xs:schema/xs:*[@name='Record']
```

`xs` and `xsd` default to `http://www.w3.org/2001/XMLSchema`; `xml` has its
standard fixed binding. `XmlNamespaces` can supply up to 32 explicit bindings,
including overrides for `xs`/`xsd`. Document prefixes are not guessed. Unprefixed
steps match the empty namespace, even when the XML Document uses a default
namespace. Matching compares expanded names, not prefix spelling.

There is **no XPath engine invocation**. Functions, axes, unions, variables,
positional or other predicates, extra predicates, XPointer wrappers, and interior
`//` searches return `Unsupported` (`selection.xpath_unsupported`). They are not
partially applied or silently ignored. Missing prefix bindings are `Invalid`.
All matches count before the selected-node kind check: zero is `NotFound`,
multiple is `Ambiguous`, and a single metadata node is `Invalid`. Non-global
type/element selection is explicitly `Unsupported` (`selection.xsd_scope`).
The returned XML `TypeName` is `{targetNamespace}Name`, or `Name` with no target
namespace.

## Dependencies, limits and cancellation

The optional `Validation.ResolveReference` callback authorizes only schema
dependencies. There is no default resolver. JSON `$schema` and XML namespace
URIs do not trigger acquisition; DTDs are prohibited and `XmlResolver` remains
disabled. Returned dependencies are copied into the result's owned context.

Set `Validation.DocumentUri` explicitly when relative dependency URIs require a
base. The selector does **not** derive that base by normalizing `SchemaUri`.
With this base, Protobuf imports resolve relative to each supplied document URI;
the root URI closes cycles. Without it, Protobuf uses literal import keys, and
the root `DocumentReference` must be the key used for root imports. This is not a
filesystem/protoc include-path search. Repeated/cyclic Protobuf imports are
coalesced before invoking the callback.

`DocumentValidationOptions` applies across copying, parsing, references and
selection: per-document/aggregate bytes, depth, nodes, references and work.
Ownership copies are checked before allocation. Work conservatively includes
identity parsing, copying, schema processing, name lookup and structural
selection. It is not just a count of selected nodes.

| Additional option | Default | Maximum |
| --- | --- | --- |
| `MaxUriLength` (raw URI and Document reference characters) | 16,384 | 65,536 |
| `MaxSelectorLength` (raw and decoded selector characters, including implicit JSON Structure `$root`) | 4,096 | 65,536 |
| `MaxSelectorSegments` (JSON Pointer/XPath steps) | 64 | 128 |
| Explicit XML namespace bindings | 32 | 32 |

Limits are inclusive; exhaustion returns `Indeterminate` with `limit.*`
diagnostics. XML validation retains the existing conservative compilation policy,
including occurrence-expansion limits, before structural selection. XML parsing
and compilation have bounded inputs and cancellation checks around the framework
calls; this is not a wall-clock preemption guarantee inside `XmlSchemaSet.Compile`.
Resolvers must honor cancellation and enforce their own I/O deadlines.

The implementation uses framework APIs and the shared parsers without reflection
discovery, regex-based Protobuf parsing, new packages, or external executables.
It targets the repository's .NET 8/.NET 10 and NativeAOT-compatible Validation
surface. Managed selection/Validation gates are separate from final native and
package qualification.
