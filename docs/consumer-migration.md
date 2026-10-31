# Consumer embedding and role-oriented migration

For a new application, start with
[Integrate xRegistry into ASP.NET Core](aspnetcore-integration.md). This
document focuses on deeper role boundaries and migration decisions.

This guide describes the current standalone interfaces and the executable,
package-reference-only consumer. It is not a drop-in binary compatibility
promise, an OPC UA migration layer, release qualification, or a claim of
complete xRegistry conformance.

## Roles, not old type substitutions

| Consumer role | Current standalone choice | Important distinction |
| --- | --- | --- |
| Endpoint/value/model/path/codec concerns | `XRegistry`: `RegistryJson`, `RegistryModel`, `RegistryId`, `RegistryPath`, `RegistryHeaderEncoding`, `RegistryHeaderMetadata`; `XRegistry.Models` for packaged model sources | A domain Endpoint Definition is not an HTTP route. IDs follow the standalone Core grammar; native identifiers require explicit mapping. |
| Media Endpoint definitions | `XRegistry.Models.EndpointDefinition.MaterializeMedia` and `RegistryDomainRules.ValidateMediaEndpointMetadata` | Validates the working-draft producer/consumer and protocol-option rules. Codec identifiers remain open and case-sensitive; the validator rejects recognized codecs assigned to the other media kind. Pinned canonical vectors exercise both paths and check the model's declared stream-format enums. A model-driven matrix checks all 30 directly declared protocol-option fields; signaling and description-reference checks retain their direction and HTTPS constraints. Materialization rechecks option constraints after Level-1 expansion. Protocols without a stream-format declaration do not acquire producer support. The draft defines no codec/format compatibility matrix. |
| WoT Version selection | `XRegistry.Models.WotVersionResolver.Resolve` and `SelectVersionId` | Applies the captured Resource's explicit profile, floors, pins and sticky defaults. SemVer retains prerelease policy; incremental selection requires an exact compatibility line and optional numeric floor. `SelectVersionId` does not assert byte provenance; `Resolve` requires the captured digest and origin. Neither method parses reference URIs or acquires documents. |
| WoT Registry acquisition | `XRegistry.Federation.WotRegistryResolver.ResolveAsync` | Resolves a parsed, nonlocal reference within one authorized source and Group Resource collection. Requires a captured WoT model with supported profile controls, rejects competing Resources, and returns exact bytes with their computed SHA-256 digest, effective line and Registry origin. |
| WoT containing-revision resolution | `XRegistry.Federation.WotRegistryResolver.ResolveLocalAsync` | Resolves a parsed local reference against an already acquired `WotResolvedDocument`. Uses its retained bytes and kind. SemVer floors check the authored version; incremental requirements check the captured revision number and line. It cannot select another Registry Version. |
| WoT JSON Pointer selection | `XRegistry.Federation.WotRegistryResolver.SelectFragmentAsync` | Applies an RFC 6901 URI-fragment pointer to the acquired document after Version or local-revision selection. Returns the selected JSON value with the containing document's full-byte digest and origin. |
| WoT incremental assignment calculation | `XRegistry.Models.WotIncrementalVersionAllocator.Assign` | Calculates a new integer revision from explicit durable high-water state. It does not publish, persist a counter, activate an incremental endpoint or implement compatibility-line selection. |
| WoT incremental publication | `XRegistry.Models.WotRegistryProfiles.Compile(WotVersionProfile.Incremental)` with `RegistryEngine` | Provisions a separate fixed-profile model. Publication commits the counter with Documents, metadata and Core state; revision bytes and compatibility lines cannot change. |
| Generic operation dispatch | `RegistryEngine.ExecuteAsync(RegistryOperation, RegistryOperationContext, CancellationToken)` in `XRegistry.Server` | The engine owns model, lifecycle, versions, validation and atomic change preparation. |
| HTTP client | `XRegistry.Client.XRegistryHttpClient` | One explicit HTTP operation, unchanged status/headers and bounded metadata/Document consumption. |
| HTTP serving | `XRegistry.AspNetCore.MapXRegistry` | Maps a concrete `RegistryEngine`; it does not start or secure a host. |
| Transactional persistence | Application-owned `IRegistryPersistence`, or `LocalRegistryPersistence` over `LocalFileStore` | Storage generations are not entity epochs or remote Registry revisions. |
| Schema-source validation | `XRegistry.Validation` | Supported schema formats and policies, not OPC UA NodeSets/sessions/projection. |
| API Registry and operation references | `XRegistry.Models` (`RegistryModelKind.Api`) and `XRegistry.Validation` (`ApiOperationReference`, `ApiOperationDocumentResolver`) | The model describes versioned API definitions. The parser accepts operation, service, interface, GraphQL and JSON Pointer selectors. Document resolution supports OpenAPI 3.0/3.1 and Swagger 2.0 JSON operations by `operationId` or JSON Pointer, OpenRPC 1.4 JSON methods by exact `name`, Avro Protocol 1.12.0 JSON messages by exact member name, and assembled Smithy 2 JSON AST operations by absolute shape ID. These selectors do not validate the full API document; other language resolvers remain unsupported. |
| Federation read acquisition | `IFederationReadSource` / `HttpFederationReadSource` | A read-source seam, not a write dispatcher or immutable live snapshot. |
| OPC UA access and federation reads | `XRegistry.Bindings.OpcUa` | Uses OPC UA Client Stack 2.0.1 to discover endpoints, apply caller policy, open a Session, remap the portable root through `NamespaceArray`, and verify Registry type and Core version. `OpcUaRegistryReadSource` maps Registry, Group, Resource, Meta, Version and collection views, and reads local FileType Documents or returns external-reference descriptors. `OpcUaRegistryWriter` updates Properties and calls Methods, including Resource Document replacement. Callers supply Group, Resource and ResourceVersions ObjectType mappings. Fake-Session tests cover reader, source and writer behavior; in-process server tests cover Session establishment, XREG-Events subscription management, writes, methods and document transfer. Two-server tests cover trusted external Resource and Version resolution. Core API views and NodeSet projection remain unimplemented. Endpoint security policy is caller-selected. Generic packages remain free of OPC UA types. |

Legacy prepare/replay/incarnation/generation extensions are not standard
xRegistry HTTP. The in-process persistence/response preparation methods do not
imply proprietary wire endpoints.

For `Protobuf/2` and `Protobuf/3`, schema validation checks declaration names
in their containing scope. Fields, oneofs, nested types and enum values cannot
reuse a name in the same message; enum values share their enum's containing
scope. Package-level types, services and enum values are also checked across
caller-supplied imports. Collisions report `Invalid` with `protobuf.duplicate`,
regardless of declaration order. Names remain case-sensitive, and reuse in
different containing messages or packages is valid. Registering a service name
does not make it a message type.
The [Proto2](https://protobuf.dev/reference/protobuf/proto2-spec/#message-definition)
and [Proto3](https://protobuf.dev/reference/protobuf/proto3-spec/#message-definition)
specifications define the message-name rules; the publisher also explains
[enum-value scoping](https://protobuf.dev/programming-guides/style/#enum-value-prefixing).

Each Protobuf document can use its own types, directly imported types and types
forwarded through an unbroken chain of `import public` declarations. Loading a
private transitive dependency does not expose its types to the root, and sibling
imports do not grant each other visibility. An inaccessible field or RPC type
reports `Invalid` with `protobuf.type`, including during message selection.
Dependencies are still acquired only through the caller's resolver; URI-backed
selection applies visibility after resolving import document identities.
See the publisher's [import rules](https://protobuf.dev/programming-guides/proto3/#importing-definitions).
Type checks retain each file's syntax. A Proto3 field cannot directly use a
Proto2 enum, including through a public import; this reports `Invalid` with
`protobuf.enum_syntax`. A missing syntax declaration identifies Proto2.
Messages can cross either syntax boundary and retain their own enum fields.
These checks follow the publisher's
[cross-syntax rules](https://protobuf.dev/programming-guides/proto3/#using-proto2-message-types).
Adjacent quoted literals are concatenated for imports, reserved names and
supported string-valued options or defaults, as defined by the
[string-literal grammar](https://protobuf.dev/reference/protobuf/proto3-spec/#string-literals).
Path and duplicate-name checks use the combined value before acquisition.
The `syntax` discriminator still requires one literal; concatenation does not
add escape decoding or unsupported option policies.
Option and default checks distinguish quoted strings from native constants.
For example, `"true"` cannot satisfy a boolean option, and `"ZERO"` cannot
satisfy an enum default. This also applies when adjacent literals produce the
same text. Boolean options report `protobuf.option`; malformed defaults report
`protobuf.default`. String and bytes defaults retain their quoted representation.
Reserved-number ranges cannot overlap, including at a shared endpoint or across
separate statements. Duplicate singleton reservations also fail with
`protobuf.reserved`; adjacent nonoverlapping ranges remain valid. Checks retain
the distinct message-tag and signed enum-number bounds and charge each interval
comparison to the validation work budget. The publisher's
[descriptor schema](https://github.com/protocolbuffers/protobuf/blob/v33.0/src/google/protobuf/descriptor.proto)
defines these range constraints.
Supported file-only options, such as `java_package` and `optimize_for`, cannot
appear inside messages, oneofs, services or RPC methods. Misplacement reports
`Invalid` with `protobuf.option_scope`. The `deprecated` option remains valid
on its supported declarations but not on a oneof. Unknown and custom options
still report `Unsupported`; placement checks do not assign them a policy.
Proto2 floating defaults accept leading-decimal forms such as `.5` and
`-.5e+2`, as specified by the
[floating-point grammar](https://protobuf.dev/reference/protobuf/proto2-spec/#floating-point-literals).
Malformed decimal neighbors remain invalid, and leading-decimal tokens cannot
satisfy field numbers, enum numbers, reservations or integer defaults.
RPC method bodies accept empty semicolon statements before, after or between
supported options in both syntax profiles, as specified by the
[Proto2](https://protobuf.dev/reference/protobuf/proto2-spec/#service-definition)
and [Proto3](https://protobuf.dev/reference/protobuf/proto3-spec/#service-definition)
service grammars. This applies to unary and streaming declarations without
relaxing method-name, message-type or option-policy checks; each empty statement
consumes validation work.
Quoted strings cannot substitute for grammar keywords, field labels, signs or
separators. For example, `"optional"` is not a field label, and `"="` cannot
separate a field name from its number. Such documents report `Invalid`; real
custom-option syntax still reports `Unsupported`. Strings retain their value
semantics: `import "public" "other.proto";` acquires `publicother.proto` through
the caller's resolver rather than treating `"public"` as an import modifier.
The package applies to every declaration in its file, including declarations
before the `package` statement. Validation and message selection therefore use
the same fully qualified names regardless of that statement's position;
imported files determine their own package scope. Package names require a
full identifier without a leading dot, and duplicate package statements remain
invalid. These rules follow the
[Proto2](https://protobuf.dev/reference/protobuf/proto2-spec/#proto-file)
and [Proto3](https://protobuf.dev/reference/protobuf/proto3-spec/#proto-file)
file grammars and their package declarations. Package discovery consumes the
same finite work budget as declaration validation.
An explicit `packed` option requires a repeated numeric, boolean or enum field
whether its value is `true` or `false`. Setting `packed=false` on a singular,
string, bytes, message, map or oneof field therefore reports `Invalid` with
`protobuf.packed`; omitting the option does not impose that restriction.
The [publisher's option guidance](https://protobuf.dev/programming-guides/proto3/#options)
defines packed and unpacked encoding for repeated basic numeric fields.
Enum and enum-value options use the same explicit policy boundary as other
declarations. Unknown option names or actual custom-option syntax report
`Unsupported`, rather than treating an unimplemented option as malformed solely
because its value is not boolean. Supported enum `allow_alias` and `deprecated`
options and enum-value `deprecated` still require native boolean constants.
Duplicate enum-value options report `Invalid` with `protobuf.option`; quoted
parentheses cannot stand in for custom-option syntax.
Editions, custom extensions, complete weak-import semantics and gRPC API
selection remain unsupported.

`ApiOperationDocumentResolver.Capabilities` advertises finite parser and retrieval
budgets: 1 MiB per document, 4 MiB total, 100,000 parsed JSON values, JSON nesting
and reference-chain limits of 64, at most 32 external documents, and a 30-second
operation deadline. Dependencies are acquired only through the caller's callback;
the callback owns authorization, redirects and credential policy. Returning null
reports an unavailable dependency; throwing `UnauthorizedAccessException`
reports `Denied`. A required dependency failure prevents selection even if an
earlier method matched. Caller cancellation propagates; deadline expiry reports
`Indeterminate`. Resolution never invokes an API.

API dependency references reject raw whitespace and control characters before
base-URI lookup or acquisition. The document portion also rejects malformed
percent escapes and backslashes instead of letting `System.Uri` repair them.
Valid network-path references such as `//other.example/shared.json` inherit the
referring document's scheme; absolute paths such as `/shared.json` retain its
origin. Both require a base URI and are never inferred to be filesystem URLs.
Percent-encoded paths and UTF-8 JSON Pointer fragments retain their existing
resolution behavior. The caller still owns acquisition policy; these syntax
checks do not authorize a scheme or an origin.

For OpenAPI JSON, the root `openapi` value must identify the selected `3.0` or
`3.1` feature set with a numeric patch component. Patch numbers are not bounded
to the releases known by this library. A nonempty hyphen suffix is accepted as
in the publisher's document schemas; this check does not impose SemVer rules
on `info.version`. Malformed root versions fail before dependency acquisition,
including when selection uses a JSON Pointer.
OpenAPI and Swagger `paths` entries must start with `/`; `x-` entries are
specification extensions. Operation-ID selection ignores extension payloads,
including apparent operations or references inside them. Under `paths`, a JSON
Pointer must select an HTTP method beneath a slash-prefixed path, not extension data.
OpenAPI 3.1 operation-ID selection also searches `webhooks`, including referenced
Path Items. Webhook names are exact strings, not slash-prefixed paths; an `x-`
name in this map is a webhook, not a Paths specification extension. Duplicate
IDs across paths and webhooks are ambiguous. A required unavailable webhook
reference prevents selection, even if a path operation matched first.
An inline webhook method can be selected with `/webhooks/{name}/{http-method}`;
JSON Pointers do not traverse Path Item or operation references. Results retain
the native operation JSON and the referenced source URI when applicable.
OpenAPI 3.1 documents can omit `paths` when they contain `components` or
`webhooks`. With no matching path or webhook operation, selection reports `NotFound`.
OpenAPI 3.0 and Swagger still require `paths` and do not select webhooks. Malformed `paths`
values fail in every supported version; malformed `webhooks` fail in 3.1.
For OpenAPI 3.0 and 3.1, operation-ID selection also searches callbacks attached
to reachable operations, including nested callbacks and callbacks on webhooks.
Callback, Path Item and operation references use the containing document's base
URI and the same caller-controlled acquisition policy. Callback expression keys
remain native strings; the resolver never evaluates an expression or invokes its
callback URL. Bare `$` expressions and expressions embedded in `{...}` are
checked against the OpenAPI runtime-expression forms. Header references require
an HTTP token; body fragments require JSON Pointer syntax with exact `~0`/`~1`
escapes. An empty body pointer and empty pointer tokens are valid; pointer text
is not percent-decoded or resolved against an HTTP payload. Malformed expressions
in a reachable callback prevent an earlier operation-ID match. Inline callback
pointers check each expression token, including reusable and nested definitions.
Literal callback text is preserved, not checked as a retrievable URL. Parameter
declarations, runtime values and the resulting callback URL are not evaluated.
An `x-` key within a Callback Object is an extension and is not
searched, whereas an `x-` name in the operation's `callbacks` map names a callback.
Unused callback definitions in `components` are not operation occurrences.
Repeated use of a callback definition creates separate occurrences; duplicate
matching IDs are ambiguous.

Callback operation traversal and each reference chain permit at most 64 edges.
Cycles, excessive depth and exhausted cumulative work report `Indeterminate`
instead of returning an earlier partial match. Missing or policy-denied callback
dependencies prevent selection. Inline nested methods can be selected with
`/paths/{path}/{method}/callbacks/{name}/{expression}/{method}` and the analogous
webhook pointer; each further callback adds another four tokens. Percent-encode
URI-fragment data before use, including braces and `#`, and escape `/` and `~`
inside pointer tokens. Pointers never cross a Callback, Path Item or operation
reference. Swagger does not interpret callback-shaped data as OpenAPI operations.
Pointers can also select inline reusable definitions:
`/components/pathItems/{name}/{method}` in OpenAPI 3.1, and
`/components/callbacks/{name}/{expression}/{method}` in OpenAPI 3.0 and 3.1.
These methods can contain further inline callbacks. A pointer selects the
specified definition even if it is unused; operation-ID lookup still searches
only API occurrences reachable through paths, webhooks and callbacks. Reusable
definitions do not add candidates merely by existing in `components`.
Full OpenAPI validation and YAML parsing remain unsupported.

For `OpenRPC/1.4`, selection checks every resolved Method Object before returning
a match. Each method requires a `params` array, including notifications with no
parameters. An omitted `result` remains absent; the resolver does not invent a
response descriptor. `paramStructure`, when supplied, must be `by-name`,
`by-position` or `either`. Method and content-descriptor annotations have their
declared string or boolean types.

Method names are case-sensitive and must be unique across the resolved
`methods` array. A duplicate requested name reports `Ambiguous`; duplicates
of other names report `Invalid`, even when the requested method exists or is
absent. Repeated references create separate method occurrences, so retrieval
caching does not waive uniqueness. Unused component definitions do not create
method occurrences.

Parameters require distinct exact names and an object or boolean `schema`.
Required parameters must precede optional parameters; an omitted `required`
flag means optional. Results use the same content-descriptor shape. Errors
require an integer `code` and string `message`, with distinct codes within the
method. Integer comparison is exact: `1`, `1.0` and `1e0` are the same code.
Number spellings beyond the shared 256-character limit report `Indeterminate`.

Content Descriptor and Error references use the containing document's base URI
and the same bounded, caller-authorized retrieval as Method references. Missing,
denied or cyclic dependencies prevent selection, including in a later method.
The returned Method Object preserves its native references; resolution does not
inline them. Descriptor schema objects, including their `$ref` values, remain
opaque schema data and do not trigger retrieval. This checks method contracts,
not every schema keyword or document metadata field.
`SupportsApiDocumentValidation` remains false.

Supplied root metadata is checked before any Method dependency is acquired.
Root servers use the same structural rules as method servers; root
`externalDocs` requires a URL string and an optional description string, and
`$schema`, when present, must be a string. None of these URLs triggers retrieval.
The required `info` must be an object with string `title` and `version` fields.
The document version is independent of the OpenRPC feature-set version and is
not parsed as SemVer. Info descriptions and terms-of-service values must be
strings. Supplied contact and license declarations must be objects with string
name/URL fields when present; contact email must also be a string.

Missing `info` fails even when `methods` is empty or contains an otherwise
selectable method. Terms-of-service, contact and license URLs, and documentation
URLs at the root, method or resolved tag, must be well-formed absolute URLs.
These checks follow the URL fields in the
[OpenRPC 1.4 specification](https://spec.open-rpc.org/#external-documentation-object).
Malformed URLs report `Invalid` with `document.url` at the authored field;
whitespace, control characters and malformed percent escapes are rejected
without normalizing the input. URL reachability, email syntax and server-template
validity remain outside these checks. Root server defaults are not inserted into
the selected native Method Object. These checks do not establish full-document
validity.

Supplied method servers require a URL string; optional names and descriptions
retain their string types. Server-variable entries require string defaults and,
when present, arrays of string enum values. Selection preserves template text
without substituting variables, inserting a default server or opening a
connection. This structural check does not validate the complete URL-template
language. The publisher does not require distinct enum values or state a
default-membership rule here; selection does not add either restriction.
Method tags can use caller-authorized references, with the same containing-source
base and failure behavior as descriptor references. Resolved tags require name
strings and valid description types. Method and tag `externalDocs` values
require objects with URL strings and optional descriptions; documentation URLs
are syntax-checked but not retrieved.
Malformed metadata in a later method prevents an earlier selection.

OpenRPC links are resolved as declarations, not executed. A supplied `method`
must name exactly one occurrence in the complete resolved `methods` array;
matching is case-sensitive and does not use a URL or operation selector.
Links can target later methods or form cycles without recursive invocation.
A missing or ambiguous linked method invalidates selection, even if a different
method matched the caller's selector. Link Object references use the containing
source's base URI and caller policy. Returned methods retain their original
link references, names and parameter values; runtime expressions in link
parameters remain data.

Link server overrides reuse the method-server structural checks. Link summary
and description values must be strings. A link can omit `method`; selection
does not infer a target from its name, parameters or server. This does not
validate parameter expressions against the target schema or execute a linked
method.

OpenRPC example pairings require a name string and a `params` array. Supplied
parameter and result examples require name strings and embedded `value` members;
JSON null is a value, not an absent member. Pairing descriptions and example
summaries/descriptions retain their string types. An omitted example result
remains absent as a notification example. `value` and `externalValue` cannot
coexist; this profile requires the publisher's embedded literal form and never
retrieves external example values.

Pairing and Example references use their actual containing document's base URI
and the shared caller-controlled retrieval budgets. Required reference failures
and malformed examples in later methods prevent selection. Literal values,
including objects with `$ref` members, are data rather than imports. The native
method retains its example references and values. These structural checks do
not validate example values against parameter/result schemas or execute requests.

For `AvroProtocol/1.12.0`, `#operations/GetUser` selects the exact `GetUser`
member of `messages`. An absent or empty `messages` object has no selectable
operations; an Avro data schema without a protocol declaration is rejected.
The returned object preserves the native request, response, declared errors and
one-way flag. A one-way message requires a null response and no declared errors.
Protocol names follow Avro fullname syntax; a qualified
name supplies its own namespace. A `types` array contains named record, error,
enum or fixed declarations with unique effective fullnames. Qualified type names
likewise override a supplied namespace; unqualified names inherit it. Names that
differ only in case remain distinct.
Within each named type, aliases are compared after resolving relative spellings
against its effective namespace. `Old` and `example.Old` therefore collide on
`example.Current`; `old` and `other.Old` remain distinct. Alias text is retained,
including strings that are not valid names, as permitted by Avro 1.12.
Cross-declaration alias conflicts and alias-based protocol evolution remain
outside these selection checks.
Request parameters follow the anonymous-record field form: each is an object
with a valid, unique field name and a type schema value. Optional documentation
is a string, `order` is `ascending`, `descending` or `ignore`, and aliases are
an array of strings. Parameter defaults and native schema values are preserved,
not inferred or rewritten. Malformed declarations in another message prevent
selection even after an exact match.

The shared Avro parser binds all protocol named types and message schemas in
declaration order. Forward references fail; a record can refer to itself after
its name is declared. Request lists use anonymous-record field rules, and named
schemas declared within them remain visible to later declarations. Response and
error schemas use the same namespace rules. Declared errors must resolve to
distinct error definitions, not ordinary records or the implicit system-error
string branch. Unused malformed types and invalid later message schemas prevent
selection; an unknown message does not bypass schema checks.

Field defaults are checked against the bound schema, including nested records,
containers, enum symbols, byte/fixed values and numeric ranges. Avro 1.12 union
defaults use the first matching branch; the existing Avro 1.8.2 and 1.11.0 data
schema profiles retain their first-branch rule. Recursive default expansion and
schema work use finite shared budgets. Enum definition defaults must name a
declared symbol. Protocol selection reads logical-type schemas through their
underlying Avro wire types, as permitted by the
[Avro 1.12 logical-type rules](https://avro.apache.org/docs/1.12.0/specification/#logical-types).
Authored annotations remain unchanged, including unknown names and invalid
logical parameters; they cannot bypass underlying schema or default checks.
This does not convert values to UUIDs, dates or decimals or compare logical
types. The Avro 1.8.2/1.11.0 data-schema validation and compatibility profiles
continue to report `Unsupported` for logical-type annotations.
Named types remain in the caller's original protocol;
selection does not inline them or synthesize the implicit system-error branch.
Avro JSON metadata, including a member named `$ref`, never triggers dependency
acquisition. This does not add Avro IDL parsing, complete logical-type support
or full protocol qualification. `SupportsApiDocumentValidation` remains false.

For `Smithy/2.0` with `application/json`, `#operations/example%23Get` selects
the exact `example#Get` operation shape in a
[Smithy JSON AST](https://smithy.io/2.0/spec/json-ast.html) whose `smithy` value
is `"2"` or `"2.0"`. Shape IDs are case-sensitive; conflicting declarations
that differ only in case are rejected. A data, service or resource shape is
not an operation. The result owns the native operation JSON, retaining its
input, output, errors and trait values without inferring an endpoint or binding.

The caller supplies an already assembled AST. Selection checks the operation's
transitive data and custom-trait shape references, including recursive types,
with a 64-edge traversal depth limit and the shared parser/work budgets.
Within that closure, list/map cycles must pass through a structure or union,
and structures and unions must permit a finite value. Container-only cycles,
required structure cycles and unions with no terminating alternative report
`Invalid` with `document.recursion`. Optional structure members, empty containers
and terminating union alternatives allow recursion. These checks do not inspect
unreachable shape definitions for recursive validity.

Enum and intEnum members must target `smithy.api#Unit`. Enum values are unique
nonempty strings and default to the member name when `enumValue` is absent. An
explicit empty string is invalid; whitespace alone remains a nonempty value.
The `enumValue` trait is valid only on enum or intEnum members, not on their root
shapes, other members or operation input/output references. IntEnum
members require unique signed 32-bit integer values; two members assigned
numerically equal values such as `1` and `1.0` are rejected as aliases.
Enum member naming recommendations do not reject otherwise valid identifiers.
Selection preserves the operation JSON rather than inserting implicit enum
values into it. Map keys can target string shapes or enum specializations;
intEnum, aggregate and trait-definition shapes are not valid map key types.

Missing required shapes report `Indeterminate`, not a successful partial result.
Mixins and `apply` entries report `Unsupported`; this resolver does not merge
model files or parse IDL. Smithy shape IDs are not retrieval URLs and never
invoke the document dependency callback. Trait payloads remain node values;
a payload member named `target` or `$ref` is not an import.

Reached operation input, output and error references permit only `target`.
Aggregate member definitions permit `target` and optional `traits`; other
properties are rejected. Traits belong to the referenced shape or to an
aggregate member, not to an operation's reference object, even when the
authored trait map is empty. Error-reference diagnostics identify the array
occurrence. These property checks do not inspect unrelated references or
interpret literal trait payloads as shape references.

The implicit prelude support covers `Blob`, `Boolean`, `Document`, `String`,
`Byte`, `Short`, `Integer`, `Long`, `Float`, `Double`, `BigInteger`,
`BigDecimal`, `Timestamp`, `Unit`, `PrimitiveBoolean`, `PrimitiveByte`,
`PrimitiveShort`, `PrimitiveInteger`, `PrimitiveLong`, `PrimitiveFloat`,
`PrimitiveDouble`, and the traits
`error`, `documentation`, `required`, `clientOptional`, `addedDefault`, `sparse`,
`uniqueItems`, `private`, `length`, `range`, `pattern`, `input`, `output`, `readonly`, `idempotent`,
`default`, `enum`, `enumValue`, `deprecated`, `recommended`, `title`, `since`,
`tags`, `internal`, `sensitive`, `unitType`, `examples`, `retryable`,
`idempotencyToken`, `longPoll`, `jsonName`, `timestampFormat` and `trait`
in `smithy.api`. Other required definitions
must be supplied in the AST. These checks do not validate every Smithy shape
constraint, trait value or selector. Full Smithy validation and model assembly
remain unsupported.

Supplied definitions of these prelude data shapes must retain their predefined
kinds within the selected closure. `Unit` cannot acquire members; `unitType`
requires an object annotation on that exact shape, not on another structure
or a member. Global shape-ID checks also reject authored IDs that differ only
in case from a known prelude data-shape ID, even when that implicit definition
is absent from the AST. These checks do not reconcile every prelude trait schema.

Within the selected closure, `documentation` values must be strings and
`error` values must be exactly `client` or `server`. The recognized `required`,
`input`, `output` and `readonly` annotation traits require object values, as do
the structured `idempotent` and `trait` traits. These checks also apply to
reachable member declarations and custom-trait definitions. They do not check
every trait member or applied constraint. Recognized applicability checks
restrict `required` to structure members, `input`/`output`/`error` to structures,
and `readonly`/`idempotent` to operations. Trait definitions must be simple or
aggregate root shapes, not members. Selection rejects `readonly` with
`idempotent`, input/output combinations, input or output with error, and error
with a trait-definition marker. Custom applicability selectors remain unchecked.
Literal members inside trait payloads do not become dependency references.

The `clientOptional` annotation is restricted to structure members.
`addedDefault` requires a structure member with an explicit `default` trait,
including an explicit null override; property order does not affect that check.
`sparse` is restricted to list and map shapes, not their members. Each requires
an object value. These declarations remain in the supplied model without
dependency acquisition. Selection does not generate client types or serialize
instances; `clientOptional` does not suppress validation of an authored default,
and recognizing `sparse` does not validate collection instance values.

The `uniqueItems` annotation requires an object value on a list and conflicts
with `sparse` regardless of property order. Its transitive data-member closure
cannot contain float, double or document shapes, including the implicit
primitive floating types. The check follows structure/union members and nested
list/map members, terminates on repeated targets, and excludes applied trait
definitions and payloads from the data graph. Unmarked lists retain those value
kinds. This checks model eligibility, not equality or uniqueness of instances.
Unrelated lists outside the selected closure remain unchecked.

The `private` annotation requires an object value. Reached data references and
custom-trait applications cannot target a private shape in another namespace.
Namespace comparison is exact; a child namespace does not grant access.
Every reference is checked, including references to an already visited target.
A member's trait application uses its declaring shape's namespace, not the
namespace of the member's target. A private member does not make its target
private, and direct operation selection is not a model reference. Unrelated
references and literal trait values remain outside this check.

The `length` trait checks root and member applicability to string, blob, list
and map targets, including enum string specializations. Its object value must
declare at least one of `min` or `max`; each supplied bound must be an integral
signed-64-bit number, compared without floating-point rounding. Missing target
definitions still report unresolved dependencies. Non-null defaults must meet
their effective bounds: a member's `length` trait supersedes its target's trait,
including omitted bounds. Strings count Unicode scalar values, blobs count
decoded bytes, and the permitted empty list/map defaults have length zero.
Null member overrides are not measured. Instance lengths and unrelated
definitions remain unchecked.

The `range` trait requires a numeric root or member target and at least one
bound. Numeric bounds must fit the target's native range; integral targets,
including intEnum, reject fractional bounds. Comparisons use exact decimal
values, including the finite IEEE-754 limits, without first rounding bounds
to float or double. Numeric defaults must satisfy their effective inclusive
bounds. A member's range replaces its target's entire range; null member
overrides remain valid. Bounds may use JSON-number spellings encoded as strings;
other string spellings and range checks on special floating-point defaults
report `Unsupported`. Instance range validation remains
outside the selected-operation resolver.

The `pattern` trait requires a string value on a string or enum root, or on a
member targeting one. Declarations use the shared bounded ECMA-262 syntax
checker. Malformed supported syntax reports `Invalid`; constructs outside
that subset, including lookaround and back-references, report `Unsupported`.
Patterns are retained without adding anchors. Non-null defaults requiring
pattern matching also report `Unsupported`, even when the pattern appears
simple; the resolver does not substitute .NET regular-expression semantics.
Null member overrides need no matching.

The deprecated `enum` trait remains supported on string roots, not members or
enum shapes. Its nonempty definition array requires nonempty, exact-case unique
values. Names must be unique ASCII identifiers and either present on every
definition or absent from every definition. Documentation, tags and deprecation
fields retain their declared types; tags may be empty arrays but each tag must
be a nonempty string. Non-null authored defaults must match a declared value,
and applicable length checks still apply. Null member overrides remain valid.
The resolver retains the original trait representation without migrating it
to enum shapes or generating client behavior.

The `deprecated` trait accepts an object on roots and members, with optional
string `message` and `since` fields. The `since` text is not parsed as a date or
version. The `recommended` trait accepts an object only on structure members,
with an optional string `reason`, and conflicts with `required` regardless of
property order. These annotations do not change default validation or generate
client warnings. Reached uses of `box` report `Invalid` because Smithy 2.0
forbids that trait; unrelated definitions and literal metadata are not scanned.

The `title` and `since` traits require strings; `tags` requires an array of
strings and permits empty or repeated tag values. The `internal` annotation
requires an object on roots or members. The `sensitive` annotation requires an
object on a data root, not a service, operation, resource or member. These
checks retain authored metadata without filtering shapes, generating client
behavior or modifying default validation. Diagnostics do not include sensitive
default values.

Reachable trait definitions require a string `selector`, when supplied, and
restrict `structurallyExclusive` to `member` or `target`. After schema-closure
checks, custom applications evaluate type-token selectors and their supported
sequence/`:test`/`:not` compositions against the actual root or member shape.
Explicit `enum` and `intEnum` selectors match only their respective root kinds;
the existing `string` and `integer` selectors also match those specializations.
Enum members remain `member` shapes, not their container's type.
Exact trait-existence predicates such as `structure[trait|trait]` are also
supported. Unqualified names resolve only in `smithy.api`; fully qualified trait
IDs match exactly. Presence does not depend on the trait value's truthiness or
collection size. Predicates inspect the application shape's own traits, not its
container, target or literal payload. Trait lookups consume the shared work budget.
Trait predicates also support `?= true` and `?= false` for present and absent
traits, respectively. A present null, false or empty value still exists. These
checks use the selected shape's own traits and compose with the supported
Boolean functions. The operands may be unquoted or single/double-quoted
`true`/`false` values, including comma-separated alternatives. A comparison
matches any alternative, but every operand is parsed before the result is
accepted. Other supported text operands are valid nonmatches rather than
Boolean aliases. The per-comparison ASCII `i` modifier also applies to presence
operands, so `?= TRUE i` tests presence and `?= FALSE i` tests absence.
Presence never renders the left payload as text: nulls, numeric nodes and
non-ASCII strings still exist. Unsupported right-hand rendering or folding
remains explicit even after an earlier alternative matches.
Literal object properties can be selected with additional `|name` segments.
Trait names and property names may use single or double quotes; quoted names
cannot be empty or contain backslashes or control characters below U+0020.
Names match exactly without Unicode normalization or JSON Pointer decoding.
Missing properties and descent through scalars or arrays yield absence; a
property containing null still exists. Unquoted numeric path segments use their
exact authored spelling as object keys: `|1e2` selects `"1e2"`, not `"100"`,
and `|0` never indexes an array. This also applies to scoped context paths and
properties forwarded through projections. These segments follow
[`SelectorPathSegment`](https://smithy.io/2.0/spec/selectors.html#grammar-token-selectors-SelectorPathSegment);
they are property names, so the 256-character arithmetic comparison limit does
not apply. Scanning and object lookup still consume the shared work budget.
Function properties other than `(keys)`, `(values)`, projection `(first)` and
the `(length)` checks below remain unsupported. Property scans consume the
shared work budget.
Trait and nested-property predicates also support `>`, `>=`, `<` and `<=`.
Both sides must contain Smithy number spellings, either as numbers or strings;
other node kinds, missing properties and nonnumeric strings do not match.
Comparisons use exact decimal values and accept comma-separated alternatives.
Every operand is parsed, including alternatives after an earlier match.
The optional `i` token is accepted after numeric comparisons without changing
their arithmetic or converting nonnumeric text into a number.
Numeric spellings are limited to 256 characters, and parsing and comparison
consume the shared work budget. Existing document-ingestion limits still apply
to numeric JSON tokens.
String predicates support `=`, `!=`, `^=`, `$=` and `*=` with ordinal,
case-sensitive matching and comma-separated alternatives. Strings retain their
decoded text; Booleans compare as `true` or `false`. Present nulls and
aggregates compare as empty strings, but absent attributes never match any of
these operators, including `!=`. Quoted numeric text is compared literally;
numeric JSON values and unquoted numeric operands report `Unsupported` because
numeric-to-string rendering is not implemented. Empty quoted operands remain
unsupported. The `i` modifier enables
case-insensitive string comparison when every operand and the selected value
contain only ASCII characters. Non-ASCII case folding remains unsupported,
including an operand after an earlier match or on an absent attribute. Trait
and property names retain exact matching. Character checks and text comparisons
consume the shared work budget.
The `(length)` property counts array items and object entries, including null
values. Empty aggregates have a present length of zero. Numeric comparisons
and presence checks can use the result; further property access yields absence.
Object-entry counting consumes the shared work budget. Computed lengths and
trait counts use invariant decimal digits with the supported string comparators;
numeric operands for those comparators still require quotes. String length
counts decoded Unicode scalar values, including escaped controls and zero-length
strings, and consumes the shared work budget. A supplementary character counts
once; combining marks and zero-width joiners each count separately. Counting
does not normalize text or count grapheme clusters. This applies to node values,
projected object keys and scoped text values without enabling Unicode case
folding. A quoted `'(length)'`
is a literal object key, not a function property.
The trait attribute also supports `[trait|(length)]`, which counts every trait
applied directly to the selected shape, regardless of its payload value.
An omitted or empty traits object has a present count of zero. Container and
target traits are not inherited. Counting uses the same work budget as node
object lengths; malformed traits objects remain invalid, and unknown implicit
prelude trait sets remain unsupported.
The bare `[trait]` attribute exposes the trait collection itself. It is present
even when the shape omits its traits object. The collection compares as an
empty string with string comparators and has no numeric representation;
`[trait|name]` still tests an individual trait. Malformed traits objects remain
invalid, and unmaterialized implicit prelude shapes remain unsupported.
The `(keys)` and `(values)` properties create projections. Trait keys expose
absolute trait identities, including their namespace/name parts; object-node
keys expose literal text without interpreting punctuation. Values expose each
object entry or array item, including nulls. Further properties apply to every
projected value, and nested projections flatten their results. Missing paths
contribute no values. A projection exists only when it has a value; empty
projections never match string or numeric comparisons. Supported comparisons
match any projected value against any operand, so `!=` does not mean that every
value differs. Every value is checked before accepting a match; unsupported
numeric rendering or non-ASCII folding in a later value still reports
`Unsupported`. Expansion and comparison consume the shared work budget.
The projection `(first)` property extracts one value from the flattened results.
An empty projection yields absence; a singleton retains its node or identity
type. Projections are unordered, so selectors must not depend on which value is
returned when more than one exists. Use `(first)` when the projection is known
to contain a single value. Subsequent property access applies to the extracted
value, not the former projection. Expansion still consumes the shared work
budget before extraction. Projection set operators and named scoped
selectors remain unsupported.
The `id` attribute exposes the selected shape's absolute identity. Its
`namespace`, `name` and `member` properties return the corresponding text;
`member` is absent on a root shape. `(length)` counts the full identity or a
selected part, and further descent through text or a computed count yields
absence. String, numeric and presence comparisons use the same rules as other
supported attributes. Application selectors inspect the annotated root or
member; `idRef` selectors inspect the referenced identity, including known
implicit prelude shapes. Identity lookup neither traverses the referenced shape
nor acquires it as a document. Property scans and comparisons consume the shared
work budget.
The `service` attribute exists only on a selected service shape and compares as
that service's absolute identity. Its `id` property supports the identity
properties above; `version` exposes the authored string with the supported text,
numeric and length comparisons. Missing versions yield absence, while supplied
non-string versions are invalid when accessed. On other roots and on members,
the attribute and its properties are absent. These selector checks do not
validate the service model or its operation/resource closure.
A member is not matched as its target or container type. An omitted selector
permits every shape; each reached application is checked independently.
Type mismatches are invalid. Other selector expressions report `Unsupported`
at the application, while unapplied definitions retain declaration-only checks.
General selector evaluation remains open.
The `:in` function accepts exactly one supported same-shape selector. It yields
the current shape only when the argument yields that shape; nested type,
attribute and Boolean predicates use the selected root or member identity.
This does not implement model-wide membership. Arguments requiring variables,
`:root` or graph traversal report `Unsupported`, including inside an otherwise
matching branch. Nesting and repeated predicate evaluation consume the existing
depth and work budgets.
Unknown function names yield no shapes when their arguments use supported
selector syntax. Arguments are parsed but not evaluated, so trait lookups,
comparison values and service versions do not affect that result. Empty,
malformed or unsupported arguments still report `Unsupported`; parse-only
nesting and token scanning retain the shared limits. Recognized but
unimplemented functions such as `:root`, `:recursive` and `:topdown` remain
unsupported, and the optional deprecated `:each` alias is not implemented.
Shape-scoped assertions use `[@: @{path} comparator literal]`, with `&&`
joining comparisons against the same selected shape. Each path starts with
`id`, `trait` or `service` and supports the properties described above.
Literal alternatives, presence checks, numeric comparisons and per-assertion
ASCII `i` modifiers retain their ordinary attribute semantics. Every assertion
is parsed and checked, including after a false assertion; unknown functions
parse these assertions without evaluating them. Assertion scans and
comparisons consume the shared work budget.
The left operand can also be quoted text, an unquoted Boolean or a root shape
ID, for example `[@: '10' > 9 && true ?= true]`. These are present text values;
quoted numeric text uses the same exact numeric comparison and 256-character
limit as attribute values. Scoped numeric comparisons also accept unquoted
numeric left operands, including negative values, fractions and exponents;
`[@: 9007199254740993 > 9007199254740992]` compares exactly without binary
floating-point rounding. Named and projected scopes retain their context rules,
and every reached numeric left operand has the same 256-character source limit.
Literal text has no property path. Numeric-to-string rendering for unquoted
numbers and empty quoted text remain unsupported.
String and numeric comparisons also accept right-hand context paths, such as
`[@: @{id} = @{service|id}]`, mixed with literal alternatives. Both sides use
the same attribute properties, including `(keys)`, `(values)` and projection
`(first)`. A comparison matches any left value against any right value;
`!=` also uses this any-pair rule. An absent value or empty projection supplies
no pairs and does not match. Every right-hand value is checked, including
alternatives after a match and values compared against an absent left side.
Numeric JSON values remain unsupported in string comparisons; numeric
comparisons retain exact decimal values and numeric limits on both sides.
Unknown functions parse context paths without accessing their values.
Presence comparisons also accept right-hand context paths and literal Boolean
alternatives. Exact text `true` tests for presence, and `false` tests for absence;
other context text does not match. A missing right value or empty right
projection supplies no alternatives, so it cannot make an absence check match.
Every supplied context value is checked, even after a matching Boolean
alternative or when the left side is absent. Numeric JSON rendering remains
unsupported on the right, and ASCII folding checks every right-hand text value.
Unscoped `{<}` and `{<<}` forms accept literal alternatives but do not match:
their right-hand operands are not projections. They still check every supplied
value, including rendering and ASCII folding, rather than treating a literal as
a singleton projection. Context paths on the right require a scoped assertion.
Named scopes resolve an attribute once, then evaluate relative context paths
against that value; for example, `[@trait|example#Custom: @{min} < @{max}]`.
This also applies to the `id` and `service` attributes and trait-key identities.
For projected scopes, every flattened element is checked independently, and
one element must satisfy all `&&` assertions. Values from different elements
cannot satisfy separate assertions in the same scope. An absent scope or empty
projection never matches, even when its assertions contain only literals or
absence checks. Its assertion syntax is still parsed. Later elements remain
subject to unsupported-value checks and shared numeric/work limits after an
earlier element matches. Unknown functions parse named scopes without resolving
their values.
Scoped `{<}` and `{<<}` comparisons test projection subset and proper-subset
membership using the same string representations. Both operands must be
projections, including when either is empty or contains one value. A missing
attribute or a scalar passed to `(values)` is not an empty projection;
forwarding a missing property through an existing projection preserves its
projection type. `(first)` removes that type. Comparisons ignore order and
duplicate values. A proper subset must also omit at least one value found in
the right projection. Comma-separated alternatives and ASCII `i` retain their
ordinary matching rules, and every operand is checked before a result is
accepted. Membership comparisons consume the shared work budget.
`{=}` and `{!=}` remain unsupported: the publisher's `{=}` table describes
one-way containment while the surrounding text calls it equality.

After schema-closure checks, each custom trait application is checked against
its declared node types. Scalar checks cover Boolean/string values, base64
blobs and exact numeric type bounds, including supported string-encoded large
numbers and special floating-point values. String timestamps use the existing
UTC validator; leap seconds and nonzero sub-millisecond fractions remain
unsupported. Numeric timestamps share the default validator's exact epoch-second
range for years 0001 through 9999 and millisecond resolution. These checks also
apply to nested implicit `Timestamp` targets; equivalent numeric spellings retain
their authored representation. Document values remain arbitrary JSON, including null.
Lists and maps recursively check their values, with null entries permitted by
`sparse` or document targets. Structures reject undeclared members and require
all required members; unions require exactly one declared member. Optional
omissions remain omitted. Validation checks every reached application,
including members, without interpreting payload strings as shape references.
Effective `length` and `range` constraints are checked on custom application
values, including nested members and decoded map keys. A member constraint
replaces the corresponding target constraint rather than intersecting with it;
omitted bounds do not inherit target bounds. Length counts Unicode scalar values
for strings, decoded bytes for blobs and entries for collections. Range checks
use exact inclusive numeric comparisons, including string-encoded large numbers.
Pattern matching and range checks on special floating-point strings report
`Unsupported`; optional members that are absent need no value evaluation.
Lists marked `uniqueItems` reject equal custom trait values using the declared
member type. Numbers compare by exact mathematical value, blobs by decoded bytes
and timestamps by instant, including numeric/string equivalents. Nested lists
retain order; maps ignore entry order. Structures compare the same supplied
members, and unions compare the selected member and its value. Omitted defaults
remain omitted, so they do not equal an explicitly supplied member. Sparse nulls
in nested collections compare as nulls. Comparisons consume the shared work and
depth budgets; exhaustion reports `Indeterminate`, without returning an operation.
Ordinary lists and arbitrary document values are not uniqueness-checked.
The deprecated string `enum` constraint checks exact, case-sensitive membership
on custom values, including nested values and decoded map keys before sparse
null values are skipped. It shares the default-value membership check and does
not insert omitted members. These are legacy fixed-set constraint checks;
native `enum`/`intEnum` membership remains unvalidated.

The `conflicts` property must be an array of absolute root trait IDs. A custom
trait cannot coexist on the same shape with any trait named in that array,
regardless of property order or whether the other trait declares a reciprocal
conflict. This also applies to members and reachable trait definitions.
Matching is case-sensitive. An unknown conflict ID does not require a definition
unless that trait is actually applied; conflict declarations are not imports.
When supplied, `breakingChanges` must be a nonempty array of rule objects.
Each rule requires an exact `change` value: `add`, `remove`, `update`, `any`
or `presence`. Optional `severity` values are `ERROR`, `DANGER`, `WARNING`
or `NOTE`; optional messages must be strings. Optional paths must use RFC 6901
JSON Pointer syntax, including valid tilde escapes. Omissions remain authored
omissions; selection does not insert severity or path defaults. These checks do
not resolve paths against trait schemas or compare model versions. Complete
trait-member validation and semantic diff processing remain open.

Structural exclusivity is checked separately for each reachable structure and
trait ID. With `member`, at most one member can directly carry that trait.
With `target`, at most one member can target a shape carrying that trait;
two members targeting the same marked shape still violate the rule. Different
traits can select different members, and the same trait can select one member
in each of several structures. Direct member markings and target markings are
not interchangeable. These checks include reachable structure-valued trait
definitions, but do not constrain union alternatives or inspect unrelated
structures outside the selected closure.

For each input/output-marked structure reached by the selected operation, the
resolver checks references across all recognized JSON AST shape kinds. Only
the matching input or output slot of one operation can reference that structure.
A second operation, another slot, an ordinary data member or a service/resource
reference invalidates selection. This includes references from otherwise
unvisited shapes; declarations are not ignored merely because they occur after
the selected operation. Applied trait IDs are references, but literal trait
values, model metadata and service rename entries do not add reference edges.
Unmarked structures can be shared. These usage checks do not validate unrelated
shape constraints or specialize structures outside the selected closure.

For the structured `idempotent` trait, `exists` and `notFound` cannot both be
present. Each supplied property must be an array of absolute root shape IDs,
and each ID must exactly match an entry in that operation's own `errors` list.
A common service error alone does not satisfy this rule. The normal dependency
checks still require each declared error to resolve to an error-marked structure.
Empty hint arrays are valid without an `errors` list and retain their native
meaning; the resolver neither inserts error declarations nor executes retries.

The `retryable` trait requires an object value on an error-marked structure.
Its optional `throttling` property must be Boolean. Both client and server
errors can carry that annotation. The `idempotencyToken` annotation requires
an object value on a structure member targeting a string, including an enum
string specialization. A structure can mark at most one member as a token;
separate nested structures retain their own limit. An unmarked member sharing
the same string target does not inherit the annotation. Missing target
definitions still report unresolved dependencies. These checks do not generate
tokens, discard replayed requests or implement client retry policy. A token
on a nested structure remains valid model metadata, but does not become a
top-level operation token.

The `longPoll` trait requires an object value on an operation and a numeric
`timeoutMillis` between 1 and 2,147,483,647 inclusive. The timeout must be
mathematically integral; decimal and exponent spellings are compared exactly
without rounding. The required-member rule follows the publisher's trait table
and prelude, rather than its parameterless example. Missing, null and
string-encoded timeouts are invalid. Selection preserves the declaration
without setting client timeouts, waiting for responses or overriding user
request configuration.

The `streaming` annotation requires an object value on a blob or union.
Streaming union alternatives must target structures, including error structures.
A streaming target can occur only in a top-level operation input or output
structure, with at most one streaming member per structure. A member targeting
a streaming blob must explicitly carry `required` or `default`; a root blob
default alone does not satisfy this rule. Structures containing streaming
members cannot themselves be targeted by aggregate members. For reached
streaming shapes, these placement checks also inspect otherwise unvisited data
references in recognized AST member positions. Literal trait values are not
references, and unrelated streaming definitions are not validated.

The object-valued `requiresLength` annotation applies only to streaming blobs.
Those blobs can occur only in operation inputs; a structure reused as an output
is therefore invalid. The operation-only `requestCompression` trait requires
an object with a nonempty `encodings` array. Every entry must be a
case-insensitive `gzip` string; authored casing, order and repeated entries
are preserved. Compression cannot be declared when an input member targets
a streaming blob marked `requiresLength`. Selection does not compress requests
or calculate lengths. Service/protocol-specific HTTP payload bindings and
stream execution remain outside these checks.

The `eventPayload` and `eventHeader` annotations require object values on
structure members. A payload member must target a blob, string, structure or
union. A header member must target a Boolean, byte, short, integer, long,
blob, string or timestamp. String and integer eligibility includes their enum
specializations. Unresolved target definitions remain unresolved dependencies.
The two annotations conflict on the same member, regardless of property order.
A structure can mark at most one explicit payload member; every other member
must then carry `eventHeader`. Separate event structures have independent
limits. Without an explicit payload annotation, multiple headers and unmarked
payload members remain valid. Checks apply to reached structure declarations,
including structures outside an event stream, without serializing events or
choosing a framing protocol. Literal example content and unrelated event
definitions remain outside the selected closure.

The `auth` trait requires an ordered array of unique absolute scheme IDs on
operations. Empty arrays remain valid, and selection preserves authored order.
The four implicit HTTP schemes (`httpBasicAuth`, `httpDigestAuth`,
`httpBearerAuth` and `httpApiKeyAuth`) are recognized without supplied
definitions. Explicit scheme definitions must carry both `trait` and
`authDefinition`; the latter requires a root trait definition and an object
annotation. The scheme's own value schema need not be a structure.
Its optional `traits` array identifies required trait definitions. Those
definitions and their schema dependencies are traversed with the existing
private-access checks. Repeated entries in this dependency array are not
prohibited.

Missing scheme or required-trait definitions remain unresolved without network
acquisition. This includes implicit required traits not yet represented by the
resolver's prelude registry. Selection does not establish that schemes are
applied to every enclosing service, compute inherited authentication priorities
or validate complete custom trait application schemas.

Reached `protocolDefinition` declarations likewise accept any root trait
definition and require object annotations. Their optional `traits` array
identifies required trait definitions, with the same dependency traversal and no
uniqueness restriction.
The optional `noInlineDocumentSupport` property requires a Boolean value.
Operation selection does not infer a service's protocol from these declarations
or enforce that protocol's service-wide prohibition on document shapes. Those
checks require the enclosing service closure and remain unvalidated.

Reached `suppress` annotations require arrays of nonempty event ID strings.
Empty arrays, duplicate IDs and authored ordering are preserved; event IDs do
not become shape dependencies. These declaration checks do not suppress
structural errors or unresolved dependencies. Execution of configurable
validators, hierarchical warning suppression and metadata severity overrides
remain unvalidated.

Reached `idRef` declarations require string shapes or members targeting strings,
including enum specializations. Values must be objects; optional `selector` and
`errorMessage` fields must be strings, and `failWhenMissing` must be Boolean.
Missing schema target definitions remain unresolved. Custom trait values now
check absolute root/member ID syntax and exact-case presence in the assembled
model. `failWhenMissing: true` rejects absent targets; omitted or false permits
absence. Member `idRef` declarations replace the whole target declaration.
Checks include nested values and decoded map keys before sparse null values are
skipped. A reference does not add the identified shape to the schema traversal
or trigger acquisition. When a required target is missing or a found target
fails its supported selector, the diagnostic detail uses the effective
`errorMessage` verbatim, including an explicitly empty string, as defined by the
[Smithy idRef trait](https://smithy.io/2.0/spec/constraint-traits.html#idref-trait).
Codes and paths remain stable. Member overrides do not inherit an omitted
target-level message. Invalid ID syntax and unsupported assembly or selector
evaluation retain their own diagnostics. These are caller-supplied diagnostic
strings, not templates or instructions; consumers must treat them as untrusted
text when displaying or logging them.

An omitted selector or `*` matches every found target. Single type tokens from
the publisher's selector table also match declared types, including `number`,
`simpleType`, `aggregateType`, `dataType`, `serviceType` and the `collection`/`set`
aliases for lists. `string` includes enum specializations and `integer` includes
intEnum specializations. A member ID matches `member`, not the member's target
type. Surrounding ASCII whitespace is ignored; names remain case-sensitive.
A type mismatch is invalid; an optional absent target does not require evaluation.
Type-token sequences intersect their predicates. Type-only `:test` predicates
match when any argument matches, and `:not` requires exactly one argument and
negates its result. These functions can nest and compose with type tokens.
The `:in` function also accepts a single supported same-shape predicate, with
the same boundaries described for application selectors above.
The `:is` function unions results from supported same-shape predicates, including
trait-existence checks, and can compose with sequences or negation. Graph-yielding
branches remain unsupported; they are not treated as Boolean tests on the
starting shape. The deprecated optional `:each` alias is not implemented.
The evaluator checks the entire supported expression before returning a result;
unsupported attribute operations, graph traversal or functions do
not become matches through a true branch or a negated false branch. Empty arguments and partially
parsed expressions report `Unsupported`. Nesting and token scanning consume the
shared depth and work budgets.
Exact `[trait|name]` predicates use the referenced root or member's own trait
object, with the same namespace and presence rules as application selectors.
Trait predicates on unmaterialized implicit prelude targets report `Unsupported`;
the supported type table does not establish their complete trait sets.
Well-formed relative IDs report `Unsupported` because this selector does not
perform IDL assembly. Required references to implicit
prelude shapes outside the supported prelude table also report `Unsupported`,
rather than claiming those shapes are absent. General selector syntax and
evaluation remain unvalidated.

Reached `traitValidators` declarations require trait-definition targets and
objects keyed by nonempty event IDs. Each rule requires a string `selector`;
optional `message` values must be strings and explicit `severity` values must
be `ERROR`, `DANGER`, `WARNING` or `NOTE`. Non-structure trait definitions are
eligible. Selection preserves declarations without executing selectors or
emitting configured validation events. Selector syntax and execution remain
unvalidated; event IDs, selectors and messages do not create dependencies.

The operation-only `optionalAuth` annotation requires an object value.
Selection preserves that declaration without copying enclosing-service schemes,
inferring an empty `auth` list or changing authentication behavior. Supporting
both authenticated and unauthenticated invocation is distinct from disabling
authentication. Effective service authentication and runtime enforcement remain
outside this selector.

The `unstable` trait requires an object on reached shapes and members.
Its optional `featureId` must be a string of at most 100 Unicode scalar values;
supplementary characters count once and combining characters are not normalized.
An omitted feature ID remains valid. Feature IDs are metadata, not shape
references, and do not trigger acquisition.

These are local declaration checks. Selection does not establish that each
feature ID appears in every enclosing service's `unstableFeatures` map,
generate preview warnings or relax model-diff rules. Full preview-feature
validation remains pending.

The operation-only `endpoint` trait requires an object with a string
`hostPrefix`. Prefix literals follow the RFC 3986 registered-name grammar,
without schemes, user information or ports. Labels must have distinct valid
member names and cannot be adjacent. Each label requires an exact-name
top-level input member marked with both `required` and the object-valued
`hostLabel` annotation, targeting a string or enum specialization. The
recommendation that a labeled prefix end in a period is not enforced as a
mandatory rule.

Host labels do not conflict with HTTP message bindings; one member can supply
both a host label and an HTTP header. Extra host-label annotations and
annotations on nested or output members do not become template dependencies.
Selection preserves the authored prefix without expanding it, resolving hosts
or changing protocol serialization. Runtime empty/null label values and the
validity of the final combined endpoint host remain client responsibilities.

The operation-only `httpChecksumRequired` annotation requires an object value,
with or without an accompanying `http` trait. Selection preserves the
declaration without calculating a checksum or inserting a `Content-MD5` header.
Literal example data and annotations on unrelated operations remain outside
the selected closure; the annotation cannot supply missing shape definitions.

The operation-only `http` trait requires an object with string `method` and
`uri` values. Methods remain literal, without an allowlist or inferred safety
traits. An explicit success `code` must be an exact integer from 100 to 999;
the recommended 200-299 range is not mandatory. Selection does not insert the
default code.

URI patterns must use origin-form syntax, without empty path segments,
dot segments, fragments or a trailing `?`. Query literals remain authored,
including percent-encoded characters, but cannot contain labels. Labels must
occupy whole path segments and have distinct names. Each label requires an
exact-name, required top-level input member marked with the object-valued
`httpLabel` annotation; every such input member must also appear in the pattern.
Ordinary labels accept string, numeric, boolean and timestamp targets,
including enum specializations. A pattern can contain at most one greedy
label, which must target a string; selection does not enforce the recommendation
that it be the last label.

The `httpQuery` and `httpHeader` traits require structure members targeting a
string, number, boolean or timestamp, or a list of these types. Query names
must be nonempty and case-sensitively unique within the structure. Header names
must satisfy the HTTP field-name token grammar and be case-insensitively unique.
Discouraged header names such as `Authorization` are not prohibited.

The object-valued `httpQueryParams` annotation requires a map of strings or
lists of strings. The string-valued `httpPrefixHeaders` trait requires a
non-sparse map of strings. Enum specializations retain their string meaning.
Each annotation can appear on only one member per structure. A nonempty header
prefix cannot overlap an explicit header name, compared without regard to case;
an empty prefix is permitted.

The object-valued `httpPayload` annotation accepts any data target and can
appear on only one member per structure. A top-level input payload requires
every sibling member to use a header, header-prefix, query, query-map or label
binding. For top-level outputs and declared errors, sibling bindings are limited
to headers and header prefixes. These sibling rules do not apply to nested
structures. An operation with an `http` trait must bind streaming input and
output members to `httpPayload`; non-HTTP streams do not acquire that requirement.

All seven HTTP message-binding traits conflict pairwise on a single member.
Nested, output and error members do not participate in operation URI matching,
although their annotations must still satisfy declaration rules.
These checks do not compare routes across a service, resolve endpoints,
normalize URI patterns or serialize HTTP messages.

The `httpError` trait requires a numeric signed-32-bit integer on an
error-marked structure. Decimal and exponent spellings are checked exactly,
without rounding. The recommended 400-499 client and 500-599 server ranges
are not mandatory declaration limits; selection does not reject another
integer solely because it falls outside those recommendations.

The object-valued `httpResponseCode` annotation requires a structure member
targeting an integer, including an intEnum specialization. Its containing
structure cannot carry `input`. Unmarked, output and error structures retain
their declared meaning; acceptance does not turn a nested or input-slot member
into an effective response binding. A structure can mark at most one response
code member. The annotation conflicts with `httpLabel`, `httpQuery`,
`httpHeader`, `httpPrefixHeaders`, `httpPayload` and `httpQueryParams` in either
property order. Missing targets still report unresolved dependencies.
These checks do not assign default status codes, emit HTTP responses or
qualify a complete HTTP protocol binding.

The `jsonName` trait requires a string value on a structure or union member.
Explicit names cannot repeat within that structure or union; comparison is
case-sensitive and does not normalize Unicode. Empty strings remain valid
string values. Separate containers can reuse a name. The selector preserves
authored names without renaming model members or generating serialized objects.
These checks do not qualify a protocol's complete property-name mapping.

The `mediaType` trait requires a string on a blob or string root, including
an enum string specialization. Members cannot carry this root-only annotation.
The value must parse as a media type with valid parameter assignments.
Type and subtype names follow RFC 6838's restricted-name grammar and each
contain between 1 and 127 ASCII characters. Wildcards are not media type names;
unregistered names remain permitted without an IANA lookup. Casing and
parameters remain authored metadata. Selection neither validates default
content against the media type nor converts bytes or assigns HTTP headers.

The `xmlName` trait requires a string on a structure or union root, or on a
data member. Its value must follow the publisher's ASCII grammar: each
identifier starts with a letter or underscore and continues with letters,
digits, hyphens or underscores; one optional colon separates a prefix from a
local identifier. Empty names, dots, whitespace and non-ASCII letters are
invalid even when a general XML name parser would accept them.

The object-valued `xmlAttribute` annotation applies only to structure members
targeting Boolean, numeric, string or timestamp shapes, including enum
specializations. It conflicts with `xmlNamespace` on the same member in either
property order. The object-valued `xmlFlattened` annotation applies to structure
or union members targeting lists or maps. Multiple attributes or flattened
members are permitted; these checks do not impose XML-name uniqueness.
Unresolved targets remain unresolved dependencies. Names and annotations stay
in the native model; the resolver does not emit XML or bind namespace prefixes.

The `xmlNamespace` annotation requires an object on data roots or members.
Its required `uri` must be a nonempty, well-formed URI reference. Absolute URIs
and relative references are accepted without retrieval, base resolution or
normalization; XML Namespaces deprecates relative references but does not forbid
them. Raw whitespace and control characters are rejected before URI parsing;
percent-encoded values remain unchanged. Relative fragments follow the RFC 3986
fragment grammar rather than `System.Uri`'s narrower relative-reference handling.
An optional `prefix` must be a nonempty Smithy XML identifier, without a colon.
An operation cannot carry this annotation. Service annotations remain outside
the selected data closure.
These declaration checks do not bind prefixes to `xmlName` values, enforce XML
namespace scopes or retrieve schemas. A namespace URI cannot supply a missing
member target definition.

The `timestampFormat` trait applies to a timestamp root or a member targeting
a timestamp, including list, map-value and union members. Its exact string
value must be `date-time`, `http-date` or `epoch-seconds`. Missing target
definitions still report unresolved dependencies. The annotation controls
protocol serialization; it does not change the default trait's node
representation, so numeric and supported RFC 3339 defaults retain their
existing checks regardless of the authored serialization format. No timestamp
conversion or protocol-default format inference occurs during selection.

The `examples` trait applies only to operations and requires an array of
objects. Each example requires a string `title`; `documentation`, when present,
must also be a string. Empty titles and repeated titles are permitted. An
example cannot define both `output` and `error`, including an explicit null
output. `allowConstraintErrors` requires a Boolean value and an error example,
even when its value is false. An error example must be an object; its optional
`shapeId` must name an error in that operation's own `errors` list. A service
error alone does not establish that binding. Declared errors still undergo
the normal dependency, structure-kind and error-marker checks. The prelude
does not mark `shapeId` as required, so an empty error object remains permitted.
Input, output and error content remain literal document values, including null;
they are neither dependency references nor validated instance examples.

The `externalDocumentation` trait requires a nonempty map on reached roots or
members. Every key must be a nonempty name and every value a nonempty,
well-formed absolute URL. Names are case-sensitive; whitespace-only names are
not trimmed or rejected by an invented naming rule. No HTTP-only scheme
restriction is imposed. URL values reject raw whitespace and control characters
instead of accepting parser-trimmed text; percent escapes remain authored text.
Links retain their authored spelling without retrieval, normalization or generated
documentation. Literal example links and unrelated
declarations remain outside the selected closure, and documentation URLs cannot
supply missing shape definitions.

Defaults are checked on reachable simple, list and map shapes and on structure
members that target those kinds. A default cannot annotate an operation,
structure, union, list/map member or operation reference. Root defaults cannot
be null; an explicit null on an eligible structure member means no default.
When a structure member targets a shape with a root default, the member must
repeat that value or explicitly override it with null. Numeric comparison is
exact, so `1`, `1.0` and `1e0` match. List and map members do not inherit this
repetition requirement.

The implicit primitive prelude types carry the publisher's root defaults:
`PrimitiveBoolean` uses false; `PrimitiveByte`, `PrimitiveShort`,
`PrimitiveInteger`, `PrimitiveLong`, `PrimitiveFloat` and `PrimitiveDouble`
use zero. A structure member targeting one of these types must repeat its
default or explicitly override it with null, whether the AST omits the prelude
shape or supplies it without an explicit default. An authored primitive default
must retain false or zero; its omission does not remove that value. Effective
root defaults must also satisfy authored root constraints, even when a member
overrides the default with null. Exact numeric zero representations compare
equally. List, map and union members do not inherit this repetition requirement.
The resolver does not insert defaults into the selected operation.

Non-null defaults must have the target's value type. Signed integer defaults
must be integral and fit byte, short, integer or long ranges; big integers use
the shared bounded exact-number parser. Numeric float and double defaults must
fit the target's finite range; the exact strings `NaN`, `Infinity` and `-Infinity`
represent the publisher's special floating values. Other string spellings,
including numeric strings, are invalid for float/double targets.
Blob defaults require base64 strings; enum defaults must
name declared values, including implicit string member-name values. List/map
defaults must be empty containers. Document defaults permit scalar values or
empty containers, not nested content. These checks do not apply defaults to
the returned operation or treat literal values as imports.

Numeric timestamp defaults use Unix epoch seconds at millisecond resolution,
with exact inclusive bounds of `-62135596800` and `253402300799.999` for years
0001 through 9999. Decimal and exponent spellings with the same mathematical
value compare equally; sub-millisecond values and values outside those bounds
report `Invalid`. Null member overrides remain valid.
String timestamp defaults accept RFC 3339 Gregorian dates with a UTC `Z`
designator, including lowercase `t`/`z`, and millisecond-aligned fractions.
Additional trailing fractional zeros are permitted without rounding. Numeric
UTC offsets and invalid dates report `Invalid`; leap seconds and nonzero
sub-millisecond fractions report `Unsupported`.
String-encoded bigInteger/bigDecimal defaults support JSON-number spellings
through the same exact decimal parser as numeric nodes. BigInteger values must
be integral after exponent normalization; range comparisons accept numeric or
string-encoded bounds without rounding. Numeric source text remains bounded
to 256 characters. Other string spellings, including leading plus signs or
leading zeros, report `Unsupported` rather than receiving guessed semantics.
Complete timestamp handling and applied pattern constraints remain
open; these checks do not establish full Smithy validity.

SemVer-profile WoT minimum-version floors exclude build metadata and fit the
128-character Core id limit. Generated `0.0.0-N` floors are forbidden, with
positive `N` as defined by the draft's assignment rule; `0.0.0-0` is an
authored prerelease and remains a valid floor. A type link uses the greater
of its explicit floor and the TD's model version. Several type links require
every explicit floor only when that model version is present. The selection
helper receives those parsed values; it does not extract links from JSON-LD.

`WotRegistryResolver` reads Resource Meta and default-Version metadata to match
`wotid`; a native document view does not put Version attributes on the Resource
itself. It requires complete lookup and Version collections, keeps every read in
the selected source context, and shares one finite `FederationReadBudget`.
Unpinned and Version-pinned requests acquire document bytes only for the selected
Version. A digest pin hashes candidate bytes and selects the highest matching
Version, ignoring advisory digest fields. Denied, unavailable or inconsistent
reads fail without trying another Version or source.

`ResolveLocalAsync` retains the containing revision even after its source is
disposed. For SemVer, it checks the stored document's identity, parses exact bytes with
duplicate-name rejection, and reads `version.model` for a TM floor or
`version.instance` for a TD floor. Caller-supplied revision claims cannot replace
those fields. A failed floor does not reopen the source to select a different
Version. Parsing consumes cumulative work but does not recharge input bytes,
objects or requests already consumed by acquisition. Object, JSON-depth, work
and output limits remain finite; cancellation propagates.

`SelectFragmentAsync` accepts a leading `#`, with `#` alone selecting the root.
It decodes percent-encoded UTF-8 once before decoding JSON Pointer tokens.
Object names are case-sensitive; array indices use canonical nonnegative decimal
syntax. Explicit JSON null is a successful selection; a missing target fails
without selecting another Version. Malformed fragments, duplicate JSON names
and document-identity mismatches fail before a value is returned. The input
fragment consumes bytes and an object from the shared budget; the retained
document is parsed again with cumulative work and depth limits but is not
recharged as acquired input. The output limit applies to the selected value's
encoded JSON bytes. Fragment selection does not change the full-document digest
or require a live source.

The caller still processes URI references, document bases and JSON-LD contexts
to establish locality, then names the containing WoT identity in a local
request. `ResolveAsync` rejects local requests; `ResolveLocalAsync` rejects
Registry pins. Resource aliases must first be resolved to an authorized source,
and external document descriptors are not dereferenced. Publication closure
remains unimplemented. SemVer acquisition, local
resolution and fragment cases passed managed execution on both frameworks and
native execution in the complete Federation suite on .NET 10/Windows x64 at
`6d143c4`. Other native cells remain unqualified; the package-consumer fixture
does not yet exercise these methods.
See [native evidence](native-aot.md#current-evidence-and-uncovered-cells).

`WotIncrementalVersionAllocator.Assign` calculates the proposed D3 allocation
from a supplied watermark. A canonical authored positive integer must exceed
that watermark; absent or noncanonical authored values generate its successor.
The helper uses arbitrary-precision arithmetic within Core's 128-character id
limit and rejects sequence exhaustion, invalid captured counters and supplied
ids that disagree with the assignment. It never reconstructs deleted history
from retained Versions. A publication owner must commit the returned watermark
atomically with the bytes, fixed metadata and Core state, retaining the counter
after Resource deletion. This pure function does not perform that transaction.
Existing built-in WoT models retain SemVer. Assignment cases passed managed
execution on both frameworks and native execution in the complete Models suite
on .NET 10/Windows x64 at `af72b7f`.

Use `WotRegistryProfiles.Compile(WotVersionProfile.Incremental)` to provision
an incremental endpoint explicitly. Its read-only `wotversionprofile` marker
fixes the profile; Resources use manual Version ids and multiple roots without
a Resource-wide compatibility claim. New revisions require complete TM or TD
bytes. The engine stores its allocation watermark in the same generation-checked
batch as Documents, fixed metadata, model state and outbox records. Deleting a
Version, Resource or Group retains the watermark, including across file-store
restarts. An exact explicit replay does not advance it; changed bytes or a changed
line fail. Concurrent publications cannot commit the same allocation, and a
known-not-committed conflict can be retried against the new state. Missing,
malformed or inconsistent retained allocation history fails instead of restarting
the sequence.

An incremental revision's `compatibilityline`, including absence, is fixed at
creation. Authorized metadata can supply an absolute URI; a document declaration
using the expanded `http://opcfoundation.org/UA/WoT-Binding/compatibilityLine`
IRI must agree exactly. Compact JSON-LD terms are rejected until context
processing is implemented. A lineless revision can be stored and read directly,
but cannot satisfy automatic line selection; its publication diagnostic is
available through `RegistryResult.Notices`. A successful Document publication or
exact replay without a line returns `wot-line-ineligible` against the assigned
Version path, with the message "This revision is not eligible for automatic
compatibility-line selection." Nested publications report each affected Version
once. Reads, metadata-only changes, deletion and SemVer publication do not emit
this notice.

Notices are operation advisories, not stored Registry metadata or new wire
fields. `PrepareResponseAsync` receives proposed notices before persistence
publication; a host must not report them as committed until `ExecuteAsync`
returns successfully. The HTTP adapter writes structured Warning events under
`XRegistry.AspNetCore`, event `RegistryOperationNotice`, after that return.
These events carry `Code`, `RegistryPath` and `Message`; HTTP status, headers
and Document bytes retain their existing representation. Applications calling
the engine directly consume the immutable notices from the returned result.

The full managed gate passed 10,666 cases and 283 tooling tests for this
publication slice at `7e84cad`. Complete Server and Storage.File suites also
passed native execution on .NET 10/Windows x64 at `f97af1b`, including all 28
incremental publication cases and all three file-backed deletion/restart cases.
Other native cells and complete incremental-profile qualification remain open.

For incremental selection, set the captured `WotVersionResource.Profile` to
`Incremental` and supply each candidate's authorized `CompatibilityLine`. An
unpinned request requires its own `CompatibilityLine`; no root line or authored
TD model version is inherited. An optional `MinimumVersion` uses canonical
positive integer syntax within Core's 128-character id limit. Selection uses
the highest numeric revision in that exact line, unless a sticky Core default
must be checked instead. A conflicting sticky default fails without falling
forward. Incremental TDs have no SemVer major cap or prerelease filter.

Version-id pins override line/floor selection; digest pins compute actual byte
digests and choose the highest numeric matching revision, regardless of line.
Neither falls back to unpinned selection. The result retains the effective line
and profile with the revision, full-byte digest and origin. Equal numbers from
different origins are different revision identities. `ResolveLocalAsync` uses
that retained number and line, ignores forged containing-revision inputs, and
never substitutes another Version or reads the source again. URI/base/JSON-LD
processing is still caller-owned; the new tests use parsed references and
trusted provider metadata rather than claiming complete reference processing.
The full managed gate for this selection/acquisition slice passed 10,748 cases
and 283 tooling tests. At `c196737`, complete Models and Federation suites
passed 985 and 636 native cases on .NET 10/Windows x64, including all 31
incremental selector cases and all 10 incremental provider cases. Both ran
without IL diagnostics or JIT compilation and rejected their managed controls.
This does not qualify the other native cells or complete URI/context processing.

Live HTTP workflow tests now publish TM and TD revisions through
`XRegistryHttpClient` and `MapXRegistry`, then acquire them through
`HttpFederationReadSource` and `WotRegistryResolver`. They cover exact line
selection, sticky failures, raw pins, numeric digest ties, exact replay and
local resolution after source shutdown. Rejected byte, line and revision-id
changes return Core problem codes with their required arguments and do not
consume the next allocation. Document-view navigation to uninlined entities
remains opaque URL data; it is not followed or mistaken for a missing pointer.
These workflows use parsed reference requests.
At `306e6cd`, the complete HTTP suite also passed 421 native cases on
.NET 10/Windows x64, including all 28 incremental workflow cases, with zero IL
diagnostics or JIT compilation and a rejected managed control.

Six additional TM/TD workflows use `LocalRegistryPersistence` over
`LocalFileStore` through three separately opened and closed HTTP hosts and
adapters. An authored revision above the 64-bit range establishes the allocation
watermark. Preserving its history, deleting its Resource or deleting its Group
does not reset that watermark: publication after each reopen assigns the next
number. A retained Version pin still returns the original bytes; a deleted pin
fails without fallback. The complete managed gate passed 10,832 cases plus 283
tooling tests, including all 34 incremental HTTP workflow cases per framework.
At `449d229`, the complete HTTP suite passed 427 native cases on
.NET 10/Windows x64, including those 34 cases, with zero IL diagnostics or JIT
compilation and a rejected managed control. Orderly close/reopen does not
establish crash or power-loss durability, complete JSON-LD processing or
qualification of the other native cells.

## Choose the real seam

The supplied consumer uses an application-owned transactional record store:

```text
application record store
    -> ApplicationRegistryPersistence : IRegistryPersistence
    -> RegistryEngine + explicit IRegistryAuthorizationPolicy
    -> real Kestrel + MapXRegistry
    -> public XRegistryHttpClient
```

The adapter owns concurrency and durability and treats engine records as opaque.
It must preserve model envelopes, event/outbox state, correlation reservations,
and Documents.

```csharp
public interface IRegistryPersistence
{
    bool IsReadOnly { get; }
    ValueTask<IRegistrySnapshot> ReadSnapshotAsync(
        CancellationToken cancellationToken = default);
    ValueTask<IRegistryCommit> PrepareAsync(
        long expectedGeneration,
        IReadOnlyList<RegistryMutation> mutations,
        CancellationToken cancellationToken = default);
}
```

Point reads must not copy the entire Registry. Open Document streams outlive
snapshot disposal. `RegistryMutation` distinguishes preserving, replacing and
removing a Document. Input streams remain caller-owned and are consumed during
preparation. A candidate publishes all records or none, rechecks the expected
generation, and does not report cancellation as rollback after publication.
Unknown commit outcomes are not retry opportunities.

```csharp
var engine = new RegistryEngine(
    new RegistryEngineOptions
    {
        RegistryId = "application-registry",
        PublicRoot = configuredPublicRoot,
        Model = RegistryModel.Compile(modelSource),
        AllowAnonymousReads = false
    },
    applicationPersistence,
    applicationAuthorization);

// Authentication middleware must populate a trusted HttpContext.User first.
app.MapXRegistry(engine, new RegistryHttpOptions
{
    MountPath = "/registry",
    AuthenticationChallenge = "Bearer"
});
```

`PublicRoot` is trusted advertised metadata, never inferred from request Host.
The engine does not dispose application persistence, policy services, or caller
Document streams. Results own metadata and Document bytes independently.

An existing authoritative endpoint whose operation/lifecycle semantics cannot
store the local engine's opaque state is not made pluggable by implementing
`IRegistryEngine`. It needs an application-specific HTTP adapter/forwarding
layer, or a read-source adapter for read-only acquisition. This integration
work is tracked in the [roadmap](roadmap.md#integration-work).

## Executable package consumer

`eng\test-embedding-packages.ps1` copies `tests\PackageSmoke\Embedding*` into
isolated consumers that reference the thirteen generic runtime NuGets:
Core, Client, Server, AspNetCore, Models, Validation, Storage.File, Federation,
Publication, Publication.Server, and the File/Git/OCI bindings. The OPC UA
package is excluded from this NativeAOT consumer gate pending binding-specific
AOT qualification; its behavior is tested through `XRegistry.OpcUa.Tests`. The
consumers have no source ProjectReferences, TUnit, or OPC UA dependency.

The application persistence is process-local copy-on-write storage bounded to
256 records/1 MiB, 64 KiB per Document, and 64 KiB per metadata value. It is not
crash durability or multi-process coordination. The HTTP fixture uses literal
loopback, generated bearer credentials, ASP.NET authentication, and an explicit
Core policy; identity headers are not trusted.

The 27 named cases cross public package seams and cover:

- authentication/authorization, exact HTTP dispatch, model-aware metadata,
  caller-owned streams, atomic publication, cancellation and lease lifetimes;
- real SQLite initialization/reopen and Document preservation;
- authorized File reads and File-backed OCI layout selection;
- verified SHA-256 Git pack/tree/blob reads with no Git executable;
- exhaustive 99/98-object OCI closures and external descriptors;
- live HTTP federation, escaped identities, and pre-dispatch malformed-ID
  rejection;
- Publication manifest integrity and retained channel replay;
- actual runtime module inventory: native SQLite is allowed; a native Git
  backend is not; and
- ordinary and feature-masked managed JIT negative controls.

`EmbeddingFixtureInputs.json` pins independent mapping, OCI, Git, and license
inputs by length and SHA-256. Expected OCI object counts and Document bytes are
not obtained from the package producer. The live federation fixture uses the
actual bound loopback Registry root and retains wire XIDs while comparing
decoded ordinal identities.

## Run the development package gate

```powershell
python -m unittest discover -s tests\Tooling -p "test_embedding*.py" -v
pwsh -NoProfile -File eng\test-embedding-packages.ps1 -RuntimeIdentifier win-x64
```

The default executes net8.0 and net10.0. `-Framework` narrows a diagnostic run.
The script accepts Windows/Linux x64/ARM64 but rejects a RID that does not match
the actual host/process architecture.

`-WorkRoot <absolute-existing-directory>` selects the filesystem for SQLite
stores and controlled native File/OCI copies. The driver creates and removes
only a unique child; caller sentinels, siblings, and the supplied root remain
owned by the caller. Without `-WorkRoot`, the same policy uses the run's
artifact directory. Selecting a path does not bypass the File store's NTFS/ext4
policy.

`-ArtifactsRoot <absolute-existing-directory>` places the unique evidence run,
fresh feed, build outputs and run-owned caches below that directory instead of
`artifacts\embedding`. The gate preserves the supplied parent. This also keeps
temporary storage on a caller-selected volume without reusing another run's
feed or cache.

Each run:

1. Fingerprints selected package source/configuration and probe inputs, then
   packs all thirteen packages into a fresh local feed with isolated outputs.
2. Rejects source changes during packing, resolves literal package versions,
   and restricts `XRegistry*` to the fresh feed using package-source mapping.
3. Performs normal lockfile-free NuGet restore into an isolated cache and
   publishes each consumer, requiring ILC execution and zero `warning IL...`.
4. Matches restored runtime assemblies to nupkg entries and executes all 27
   behavioral cases.
5. Runs ordinary and feature-masked managed controls; each must exit before
   side effects with a positive JIT compilation count.

The native guard independently checks both dynamic-code flags and
`JitInfo.GetCompiledMethodCount()`. Evidence includes package/source/fixture
hashes, inspected assets, module observations, generated consumer inputs,
native and managed binary hashes, publish logs, requests, and reports. Only
explicitly owned caches and work children are removed.

## Current evidence and limits

All eight generic package-consumer cells in
[native run `41044488180`](https://github.com/marcschier/xregistry-dotnet/actions/runs/41044488180)
passed at source commit `c7ff0146084dd85272e13073a00f5706a276139f`:
.NET 8/.NET 10 on Windows/Linux x64 and ARM64. Each cell executed all 27
native cases and both managed negative controls. The run retains source,
package, binary and execution evidence in its `native-all-packages-*`
artifacts. This is development qualification of the consumer fixture, not
full native test-suite coverage or release qualification.

The refreshed artifacts were also checked against their source commit and
actual executable hashes. Each cell retains its own raw source inventory;
Git's declared text line-ending rules account for platform-specific checkout
bytes without changing those inventory hashes.

The development runs at `D:\temp\97aa2a5176f5` (net8.0/win-x64) and
`D:\temp\cac5584c2f5c` (net10.0/win-x64) each passed all 27 native cases,
with zero JIT compilations and no IL warnings. Ordinary and feature-masked
managed controls produced the expected JIT-rejection results. The net8.0 run
records source SHA-256
`624cfa48c114fba88cb745b5b3c36550c9902441479ca4009693e129832e94ba`;
each run retains its own source and package hashes. These are development
receipts, not release qualification. The OPC UA package is excluded and has
no NativeAOT qualification.

The .NET 8 Federation and Publication packages use framework-provided
System.Text.Json. Their previous unnecessary dependency on the 10.x package
made the ASP.NET Core 8 JSON-option initializer emit IL2026/IL3050 during native
compilation. Removing those dependencies resolves the package-consumer warning
gate without suppressing warnings or altering the HTTP host. A test host or
application that independently selects packaged System.Text.Json 10.x still
requires separate qualification.

This evidence does not establish power-loss durability, arbitrary authoritative
dispatch, immutable live HTTP federation, complete protocol conformance, or
production TLS/authentication. The OPC UA
binding has live-server coverage for its tested Session, operation, event
Subscription, and two-server external-target paths, but has no NativeAOT
qualification. Core API views and NodeSet projection remain unimplemented;
endpoint-security policy must be selected by the host. Proprietary extension
adapters remain application-owned. Outstanding qualification is in the
[roadmap](roadmap.md); predecessor feeds and infrastructure-blocked attempts are
in the [changelog](changelog.md#package-consumer-checkpoints).
