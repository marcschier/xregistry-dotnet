# Roadmap

This document centralizes work that is explicitly outstanding in the current
guides. It does not convert every unsupported feature into a commitment.
Current behavior, limitations, measurements, security requirements, and
release safeguards remain in their subject guides.

## Release and conformance qualification

- Complete semantic review of procedural, schema, generated-artifact, and
  example obligations that keyword extraction cannot establish.
- Map every applicable reviewed requirement to public tests and genuine
  execution evidence, then satisfy the native receipt matrix required by
  [conformance](conformance.md) and [releasing](releasing.md).
- Keep package and sample status unqualified until those gates pass; do not
  promote status from build success, test totals, or source-oracle success.
- `eng/specification/native_receipt.py` converts one already-executed,
  already-verified native TUnit TRX run plus an independently captured
  `nativeAot`/`jitCompiledMethods` proof into the exact `schemaVersion: 1`
  receipt shape `manage.py`'s `release_check` validates. All 16 projects share
  the same opt-in proof test; inclusion alone does not qualify execution.
  `eng/run_native_suites.py` and the `native-module-suites` CI job now schedule
  the thirteen generic suites across all eight framework/RID cells. Each
  suite must pass natively and in its matching managed baseline with identical
  case inventories, source-bound hashes, zero IL diagnostics and a rejected
  managed receipt. At `1bdfa6f`, all four complete .NET 10 cells passed on
  Windows/Linux x64 and ARM64. The .NET 8 cells remain failed for four test
  hosts with framework JSON IL diagnostics. Resolve those failures without
  suppression, then map each reviewed requirement's `testIds` to real method
  names and attach the resulting evidence per requirement. The source
  reconciliation after this checkpoint has no native requirement promotion.

## Native platforms and package consumers

The selected generic package, model, client/bridge sample and storage matrix
passed all 28 jobs at `1bdfa6f` on Windows/Linux x64 and ARM64. The four complete
.NET 10 module-suite cells also passed at that source. See
[native evidence](native-aot.md#current-evidence-and-uncovered-cells).
The corrected run at `e502e5d` passed all four .NET 10 suite cells on
Windows/Linux x64 and ARM64. Each downloaded cell passed all thirteen suites
and 9,118 cases; every retained managed control DLL matches its summary hash.
Their source inventories,
native binaries, reports, proofs and receipts were also checked, including
complete managed/native case parity and rejection of managed proofs.
This closes the artifact-retention check, not the native matrix or requirement
qualification. All four .NET 8 cells fail the same four framework JSON warning
gates. Windows ARM64 initially failed SDK setup; its isolated retry reached the
harness and passed the other nine suites and 7,675 cases. Its retained source,
hashes, receipts, managed parity and rejected controls were audited.
The complete Validation suite also passed at later source `a60ef6c`
on Windows x64 under both frameworks: 4,940 native cases and 861 methods each,
including Smithy schema paths and meta-trait schemas and Avro Protocol logical
wire schemas, with complete managed parity, exact hashes and rejected managed
proofs.
That selected-project evidence does not refresh the complete native matrix.
The Models suite at `58743c8` also passed on Windows x64 under both frameworks:
1,144 native cases and 354 methods each, including packaged Media sources,
inherited metadata, strict description references and bounded deferred runtime
obligations. Its source, artifacts, case parity and rejected controls were
audited; provider media accuracy and receiver compatibility remain unqualified.
Full native TUnit requirement receipts remain incomplete. All four .NET 8
complete-suite summaries at `1bdfa6f` failed for AspNetCore, HTTP, Federation and Git
test-host IL warnings. Their other nine suites passed, but those partial
successes do not qualify a complete cell.

- Refresh source-matched package-consumer and sample evidence after material
  package changes. Preserve exact source, package, executable, and report hashes.
- Resolve the .NET 8 HTTP test-host warning gate caused by its own packaged
  System.Text.Json 10.0.x dependency. The generic package-consumer gate now
  passes using framework JSON; that does not qualify the TUnit test host.
- Execute the remaining module-level binding, federation, interoperability and
  performance cases described in
  [native AOT qualification](native-aot.md).

## Interoperability

- Run the hosted forward, managed-Git, and reverse interoperability lanes on
  their declared platforms and retain their fail-closed evidence.
- Resolve or explicitly disposition the pinned `xr` checker's rejection of the
  specification-permitted `mutable` capability before claiming a clean reverse
  checker result.
- Expand independent-peer coverage for authorization, bridge behavior,
  package consumers, public Git/TLS environments, and the remaining native
  platforms without modifying peers or filtering failures into success.

## Security and deployment evidence

- Produce executable evidence for the applicable controls in
  [the security contract](security.md), including origin-scoped credentials,
  SSRF/redirect policy, bounded parsing, durable failure behavior, and release
  provenance.
- Qualify the managed-Git SHA-1 collision/trust policy through independent
  review and vectors.
- Exercise production TLS/authentication deployment profiles and platform
  certificate handling beyond the current sample fixtures.
- The account-owned branch/tag protections, release environment approvals, and
  NuGet trusted publishing policy are configured and have been exercised by
  the `0.1.0-alpha` publication. Re-verify them before each future stable or
  prerelease promotion; feed publication does not itself satisfy qualification.

## Durability and performance

- Extend storage evidence with filesystem fault injection, controller/power-loss
  testing, additional supported-platform runs, and larger bounded profiles.
- Establish controlled performance budgets only after repeatable measurements
  on matched hosts. Outstanding workloads include durable recovery, concurrent
  HTTP operations, filtering/sorting/paging, large-Document streaming,
  federation fan-out, Git pack/delta work, OCI lookup, native startup, executable
  size, CPU/GC allocation, cold storage, and production TLS/authentication.
- Bound model-compiler per-token serializer scratch memory so configured output
  limits also have an explicit scratch-allocation contract.
- Keep the greater-than-10% repeatable degradation rule as an investigation
  trigger until a statistically justified release gate is approved.

## Integration work

The source inventory now retains 51 files from five immutable revisions,
including the W25 reconciliation at `514f6eac`; the earlier `811de7d2` files
remain intact. One pending closure block was replaced, and five unchanged
floor clauses were explicitly re-reviewed against the new source. All 2,389
pending successor obligations remain pending. The reconciliation identifies
four missing closure contracts: dependency classification, occurrence/operator
encoding, document-URL mapping to captured targets, and assigned projection
vocabulary/source mapping. It does not supply their implementation.

- Complete the approved draft-alignment scope against the immutable successor
  sources in `eng/specification/successors.json`. The source inventory does not
  replace clause review or execution evidence.
- Complete the selected incremental WoT profile. Separate fixed-profile models,
  atomic allocation, immutable revision bytes and compatibility lines now have
  managed publication evidence, including concurrent conflicts and deletion
  across file-store restarts. Parsed incremental selection uses exact lines,
  numeric floors/ranking, sticky defaults and origin-scoped acquisition;
  retained local references use the captured revision and line.
  Live HTTP TM/TD publication-to-resolution workflows cover exact replay,
  rejection without allocation, sticky conflicts and retained local resolution
  after source shutdown. Six file-backed TM/TD HTTP workflows
  retain greater-than-64-bit allocation across three separate adapter/host
  lifetimes, with preserved history, Resource deletion or Group deletion.
  Retained pins preserve bytes; deleted pins fail. At `1bdfa6f`, these cases
  passed within all four complete .NET 10 module-suite cells. Their orderly
  close/reopen scope does not qualify crash or power-loss behavior.
  SemVer provider acquisition performs scoped native-source lookup and
  verifies acquired byte digests.
  Parsed local references use the retained containing revision and its authored
  version fields. JSON Pointer fragments are applied after selection without
  changing full-document provenance. URI/base/JSON-LD context processing,
  alias integration and complete profile qualification remain open. Lineless
  publication now returns immutable engine advisories; HTTP reports them through
  structured host logging after successful commit, without adding wire metadata.
  Other adapters need their own advisory delivery. WoT publication closure needs
  the four contracts above; do not substitute live targets for captured closure.
- Complete Protobuf schema language coverage, including weak-import semantics.
  Proto2/Proto3 validation now checks exact-case name collisions across fields,
  oneofs, nested types, enum values and services in their containing scopes,
  including caller-supplied imports. Enum values occupy their enum's containing
  scope; declarations in different messages or packages can reuse names.
  Type lookup now requires the declaring file to be the referring file, a
  direct import or a dependency forwarded by public imports. Private transitive
  dependencies and sibling imports no longer leak types. Visibility is checked
  separately for each referring document, including during message selection.
  Cross-syntax checks reject direct Proto2 enum use in Proto3 fields while
  allowing imported messages to retain their own syntax and enum fields.
  Adjacent string literals are combined before supported option, default,
  import-path and reserved-name checks. Syntax discriminators do not accept
  concatenated literals.
  Hexadecimal escapes accept the publisher-defined one/two-digit widths;
  Unicode escapes retain fixed-width checks.
  Named character escapes now decode to ASCII controls before string-value
  use and import acquisition, including adjacent literals. Escaped quotes and
  backslashes retain their decoded values. Unicode escapes now decode scalar
  values before concatenation, reserved-name comparison and import validation;
  surrogate escapes and values above U+10FFFF fail. No Unicode normalization
  occurs, and decoded strings cannot replace native constants or the literal
  syntax discriminator. Hexadecimal and octal escapes now decode octets with
  maximal two/three-digit widths and byte-range checks. Adjacent literals join
  bytes before UTF-8 validation for text consumers; arbitrary bytes defaults
  remain valid. Invalid text and decoded relative-path violations fail before
  acquisition, and escape scanning consumes the shared work budget.
  Option and default validation checks token kinds before values, so quoted
  strings cannot satisfy boolean, floating-point or enum constant requirements.
  Reserved-number checks reject overlapping intervals and duplicate singletons
  across statements while accepting adjacent nonoverlapping ranges.
  Supported file options reject message, oneof, service and method placement;
  `deprecated` is rejected on oneofs. Unknown/custom options remain unsupported.
  Floating defaults accept leading-decimal/exponent forms without allowing
  decimal fractions in integer-only contexts.
  Floating defaults distinguish integer radix syntax from decimal floating
  syntax and accept signed native `inf`/`nan` constants.
  RPC method bodies accept empty semicolon statements mixed with supported
  options, including streaming declarations, within the validation work budget.
  Grammar matching retains token kinds: quoted keywords, labels and separators
  cannot act as syntax, while adjacent import literals retain their combined
  path and supported string-valued options/defaults remain valid.
  File-wide package scope no longer depends on statement position, including
  for imported declarations and root message selection. Dotted package names
  use the full-identifier grammar; package discovery is work-budgeted.
  Explicit `packed` options require repeated packable scalar or enum fields,
  including when set to `false`; omission remains distinct from an explicit
  setting.
  Explicit `lazy` settings require resolved message-type fields, including
  when false. Scalar, enum and map fields fail; native Boolean, duplicate-option
  and import-visibility checks remain enforced. This validates the declared
  hint without adding lazy runtime parsing or `unverified_lazy` support.
  Enum and enum-value unknown/custom options now report the same unsupported
  policy outcome as other declarations; supported boolean and alias rules,
  enum-value duplicate checks and token-kind checks remain enforced.
  Editions and custom extensions remain unsupported. This does not add gRPC
  API operation selection.
- Add full API format validators and the remaining language resolvers. The
  OpenAPI/Swagger, OpenRPC, Avro Protocol and assembled Smithy JSON selectors
  do not establish full format validation, YAML support, or gRPC, Thrift,
  Avro IDL, GraphQL, WSDL or Smithy IDL/model-assembly support. Shared API
  dependency resolution rejects raw whitespace/control characters and malformed
  document URI spellings before acquisition. Network-path and absolute-path
  references use the referring document's base rather than becoming filesystem
  URLs; caller authorization remains required. Avro message
  selection preserves the declared native message. It checks protocol/type
  declaration names, effective namespace collisions and request fields, then
  binds named request/response/error schemas and checks defaults in native
  declaration order across the complete protocol. Avro 1.12 union defaults
  use the first matching branch. Each named type rejects aliases that resolve
  to the same fullname, without rewriting the authored aliases or restricting
  them to valid names. Cross-declaration alias conflicts and alias-based
  protocol evolution remain open. Protocol logical-type annotations retain their
  authored representation while selection checks the underlying wire schema
  and defaults, including unknown or invalid logical annotations. No logical
  value conversion or comparison is performed; data-schema validation and
  compatibility profiles retain their unsupported result. Complete protocol
  validation and qualification remain open.
  OpenAPI 3.1 selection includes webhook operation IDs and inline webhook method
  pointers, with ambiguity checking across paths and webhooks. OpenAPI selection
  also traverses nested callbacks and caller-supplied Callback references within
  finite depth/work limits. Callback keys check bare and embedded runtime
  expressions, header tokens and body-pointer escapes without evaluating
  parameter declarations, runtime values or callback URLs. Literal callback
  text is preserved without URL validation. Inline JSON Pointers can select
  reusable Path Item and Callback component methods without dereferencing or
  making unused definitions into operation-ID candidates.
  OpenRPC selection checks resolved parameter/result descriptors and errors,
  exact method-name uniqueness across all resolved method occurrences,
  parameter name uniqueness and required-before-optional ordering, and exact
  integer error-code uniqueness within each method before selection. Descriptor
  and error references use caller-controlled acquisition; schema values remain
  opaque. Complete OpenRPC document and JSON Schema validation remain open.
  Supplied method servers, server variables, tags and documentation declarations
  have structural checks. Tag references use the same authorized retrieval;
  documentation, terms-of-service, contact and license URLs also receive
  absolute-URL syntax checks before selection. Whitespace, control characters
  and malformed escapes fail at the authored field. Server templates remain
  outside URL syntax validation; all metadata URLs remain native strings
  without invocation.
  Supplied root servers, documentation, schema URI and Info/Contact/License
  metadata have structural checks before Method dependency acquisition. The
  root Info Object is required, including its title and document-version strings.
  Root defaults are not inserted into selected methods; full-document validity
  is not claimed.
  URL-template semantics and complete URL validation remain open.
  Link references use the same acquisition policy; supplied linked method names
  must identify one occurrence after the complete Method array is resolved.
  Link server overrides have the shared structural checks. Links and their
  parameter expressions are preserved without invocation; target-schema
  parameter validation remains open.
  Example pairings and nested Example references use source-relative authorized
  retrieval. Pairings require names and parameter arrays; examples require names
  and embedded literal values, including null. Literal `$ref` payloads do not
  cause acquisition, and missing example results retain notification semantics.
  Malformed later examples and required reference failures prevent selection.
  These checks do not validate example instances against method schemas.
  Smithy selection checks required operation/data/custom-trait dependencies in
  an already assembled non-mixin AST and rejects invalid recursion in that
  closure, invalid enum targets and duplicate or malformed enum values.
  Operation references permit only target; aggregate members can also declare
  traits. Extra reference/member properties prevent selection, including empty
  trait maps on plain references. Unrelated references and literal trait
  payloads remain outside these property checks. Explicit
  enum strings must be nonempty, and `enumValue` applies only to enum/intEnum
  members, not to the enumeration's root shape or other shapes and references.
  The selector's implicit prelude is limited, and full trait/schema validation
  remains open.
  Authored definitions retain known prelude data-shape kinds. `Unit` remains
  memberless, and primitive prelude shapes retain false/zero defaults.
  Omitted primitive default traits
  retain their predefined values for root-constraint checks and structure-member
  repetition. Known implicit data-shape identities also participate in global
  case-conflict checks. The object-valued `unitType` marker is restricted to Unit.
  Full prelude trait-schema reconciliation remains open.
  Operation `examples` check required string titles, optional documentation,
  output/error exclusivity and Boolean `allowConstraintErrors` only with errors.
  Optional example error IDs must be bound in the operation's own errors list;
  normal error dependency checks still apply. Literal input/output/error content
  does not trigger retrieval or instance validation.
  `externalDocumentation` checks a nonempty map of nonempty exact names to
  nonempty, well-formed absolute URLs on reached roots and members. Raw URL
  whitespace and control characters are rejected without trimming. It does
  not restrict links to HTTP, fetch their contents or use them to supply
  missing definitions. Literal examples and unrelated declarations remain
  outside the selected closure.
  `retryable` requires an error structure and an object value, with optional
  Boolean throttling. `idempotencyToken` requires an object-valued annotation
  on a string-targeting structure member, with at most one marked member per
  structure. Nested declarations do not establish top-level operation tokens;
  no replay store, token generation or retry execution is added.
  `longPoll` requires an operation object annotation with a positive signed
  32-bit integer `timeoutMillis`, checked exactly without rounding. The check
  does not execute polling or modify request timeout configuration.
  `auth` checks ordered unique absolute scheme IDs, including empty lists and
  the four implicit HTTP authentication schemes. Explicit schemes must be
  trait definitions marked with object-valued `authDefinition`; required trait
  IDs are traversed with schema dependencies and private-access checks, without
  requiring that dependency list to be unique. Missing definitions remain
  unresolved without acquisition. Complete implicit required-trait coverage
  and enclosing-service scheme application/inheritance remain unvalidated.
  Explicit custom scheme applications use the schema checks described below.
  Custom trait applications now check scalar node kinds, base64, exact numeric
  type bounds and supported string timestamps after schema-closure validation.
  Their application sites also check type-token selectors and supported
  sequence/`:test`/`:not` compositions against the actual root or member shape.
  Explicit `enum`/`intEnum` tokens distinguish the declared root kinds while
  retaining their `string`/`integer` subtype matches. Enum members match `member`,
  not their container's type.
  Exact `[trait|name]` existence predicates inspect that shape's own traits;
  unqualified names resolve in `smithy.api`, and payload truthiness does not
  affect presence. Trait lookups consume the shared work budget.
  Trait `?= true`/`?= false` predicates test presence/absence independently of
  payload truthiness, including on selected reference members. Unquoted or
  single/double-quoted Boolean operands support comma-separated alternatives;
  every operand is parsed before a match is accepted. Other supported text
  operands do not match. Per-comparison ASCII `i` permits Boolean case variants
  without rendering or folding the left payload; all right values remain checked.
  Literal nested object paths and quoted trait/property names use exact
  matching. Missing properties and descent through scalars or arrays yield
  absence; present nulls still exist. Property scans consume the shared work
  budget. Unquoted numeric path tokens address exact authored object keys,
  including signs, fractions and exponents; they are not normalized or used as
  array indices. Scoped paths and projections share the same rule, and key
  spellings are text rather than arithmetic operands. Function properties
  other than `(keys)`, `(values)`, projection `(first)` and supported `(length)`
  remain unsupported.
  Numeric trait/node predicates support `>`, `>=`, `<` and `<=` with exact
  decimal comparison and comma-separated alternatives. Both sides must contain
  Smithy number spellings; absent or nonnumeric values do not match.
  Numeric comparisons accept the optional `i` token without changing exact
  arithmetic or accepting nonnumeric values.
  Numeric spellings have a 256-character limit, and repeated parsing/comparison
  consumes the shared work budget.
  Node `(length)` counts array items or object entries, including null values;
  empty aggregates have a present zero length. Numeric and presence predicates
  accept computed lengths, and subsequent property access yields absence.
  Object counting consumes the shared work budget. Computed counts use invariant
  decimal digits with supported string comparators and quoted numeric operands.
  String length counts decoded Unicode scalar values, including empty strings
  and escaped controls, against the work budget. Supplementary characters count
  once; combining marks and joiners remain separate, without normalization or
  grapheme clustering. Node values, projected keys and scoped text share these
  rules; Unicode case folding remains unsupported.
  `[trait|(length)]` counts the selected shape's own applied traits, including
  a present zero when its traits object is omitted or empty. Payload truthiness
  does not affect the count. Malformed traits objects remain invalid; implicit
  prelude trait counts remain unsupported. Counting consumes the work budget.
  Bare `[trait]` exposes a present trait collection even when its object is
  omitted. The collection compares as an empty string and has no numeric
  representation. Named trait lookups retain their separate presence rules.
  Trait/node `(keys)` and `(values)` create projections with forwarded property
  access and flattened nested results. Trait keys expose identities; object-node
  keys expose text. Projections exist only when nonempty. Comparisons match any
  projected value against any operand, including `!=`; unsupported later values
  are not hidden by earlier matches. Expansion consumes the shared work budget.
  Projection `(first)` extracts one flattened value or yields absence for an
  empty projection. The extracted value retains its node or identity type.
  Projections are unordered; use this property only when a single value is
  expected rather than relying on cross-implementation ordering. Expansion is
  work-charged before extraction. Scoped assertions and projection subset
  comparisons are described below; graph traversal and projection equality
  remain unsupported.
  String trait/node predicates support ordinal `=`, `!=`, `^=`, `$=` and `*=`
  with operand alternatives. Booleans use their lowercase text; present nulls
  and aggregates compare as empty strings, while missing attributes never
  match. Numeric string rendering, unquoted numeric string operands and empty
  quoted operands remain unsupported. String comparisons accept `i` for
  ASCII-only values and operands; non-ASCII folding remains unsupported.
  Trait/property names remain exact. Character checks and repeated comparison
  consume the shared work budget.
  The `id` attribute exposes absolute root/member identity, its namespace/name/
  member parts and their `(length)`. Root member parts are absent. Application
  and `idRef` selectors inspect their respective selected identities, including
  known implicit prelude IDs, without treating those IDs as acquisition URLs.
  Identity property scans consume the shared work budget.
  The service-only `service` attribute compares as its absolute identity and
  exposes `id` and authored `version` text. Other roots and members yield
  absence. Missing versions are absent; supplied non-string versions are invalid
  when accessed. This does not validate the service closure.
  `:in` accepts exactly one supported same-shape selector and preserves the
  selected identity only when the argument yields it. Model-wide membership
  through variables, `:root` or graph traversal remains unsupported. Nesting
  and repeated evaluation consume the existing depth and work budgets.
  Unknown functions yield no shapes after parsing supported argument syntax,
  without evaluating their arguments. Malformed or unsupported syntax still
  reports `Unsupported`, and parse-only nesting/scanning remains bounded.
  Known unimplemented `:root`, `:recursive`, `:topdown` and optional `:each`
  remain unsupported.
  Shape-scoped `[@: @{path} comparator literal]` assertions support `&&`,
  literal alternatives and the existing `id`/`trait`/`service` attribute paths.
  Each assertion retains the ordinary presence, numeric, string and ASCII `i`
  rules, and later assertions are checked even after a false result.
  Unknown functions parse them without evaluation; scans and comparisons
  consume the shared work budget.
  Scoped left operands also accept literal text, Booleans and root shape IDs.
  Quoted numeric text and unquoted numeric left operands support exact numeric
  comparisons with signed, fractional and exponent values. The same
  256-character numeric limit applies, including after an earlier match.
  Unquoted numeric string rendering, empty text and literal property paths
  remain unsupported.
  String/numeric comparisons accept right-hand context paths mixed with literal
  alternatives. Scalar and projected values use any-pair comparisons, including
  `!=`; absent values produce no pairs. Both sides share attribute paths and
  numeric limits, and every right-hand value is checked even after a match or
  against an absent left side. Unknown functions parse the paths without
  accessing their values. Presence comparisons accept right-hand contexts mixed
  with literal Boolean alternatives. Exact `true`/`false` text tests presence/
  absence; other text and empty right projections do not match. All supplied
  context values are checked after a match and against an absent left side.
  Numeric JSON rendering remains unsupported on the right. ASCII `i` folds
  Boolean existence against every right-hand text alternative, including
  alternatives after a match.
  Named scopes resolve one attribute and use relative context paths on both
  sides of comparisons. Projected scopes require one flattened element to
  satisfy every assertion; assertions cannot combine different elements.
  Absent and empty scopes do not match, including literal-only assertions,
  but still parse their syntax. Later elements are checked after a match,
  with numeric/work limits preserved; unknown functions do not resolve scopes.
  Scoped projection `{<}` and `{<<}` comparisons check unordered subset and
  proper-subset membership, ignoring duplicates. Empty/singleton projections
  retain their type; missing/scalar attributes do not become projections, and
  `(first)` removes projection type. Alternatives, ASCII folding, complete
  operand checks and shared work limits apply. `{=}` and `{!=}` remain
  unsupported because the publisher's equality table describes one-way
  containment rather than the equality named in the surrounding text.
  Unscoped `{<}` and `{<<}` accept literal alternatives as valid nonmatches,
  since those alternatives are not projections; all values are still checked.
  Each application is checked independently. Type mismatches are invalid;
  unsupported grammar reports `Unsupported` at the application without changing
  unapplied declaration checks or masking earlier missing schema dependencies.
  Numeric timestamps share the exact default epoch range and millisecond
  resolution checks, including nested implicit prelude targets.
  Recursive list/map values, sparse nulls, required/declared structure members
  and single-member unions are checked on every reached application. Document
  values remain arbitrary JSON. Effective length/range constraints now apply
  to custom values and decoded map keys, with member constraints replacing
  target constraints. Length counts scalar values, decoded bytes or entries;
  range comparisons remain exact. Pattern matching and special-float range
  checks report `Unsupported`. Custom `uniqueItems` lists now use typed value
  equality: exact numbers, decoded blobs, timestamp instants and recursive
  aggregates. Nested lists retain order; maps ignore entry order. Absent members
  remain distinct from supplied defaults. Comparisons consume the shared work
  and depth budgets. Deprecated string `enum` constraints check exact membership
  for custom values and decoded map keys, with the same helper used for defaults.
  Native `enum`/`intEnum` membership and complete custom application schemas
  remain unvalidated.
  Reached `authDefinition` and `protocolDefinition` declarations accept any
  root trait schema, as specified by their `[trait|trait]` selectors, rather
  than requiring structures. Non-trait targets remain invalid, and applied
  values still check the declared schema. Required-trait references retain
  their marker, presence and access checks; `noInlineDocumentSupport` requires
  a Boolean. Protocol selection and the service-closure document prohibition
  remain unvalidated.
  `suppress` checks arrays of nonempty literal event IDs without imposing
  uniqueness or suppressing structural errors and unresolved dependencies.
  Configurable validators, hierarchical warning suppression and metadata
  severity overrides remain unvalidated.
  `idRef` checks string/member-string declaration targets, object values and
  optional string/Boolean fields. Custom application values check absolute
  root/member ID syntax and exact-case local presence, with whole-member
  overrides and optional missing targets. References do not trigger schema
  traversal or acquisition. Missing required targets and supported-selector
  mismatches use the effective authored `errorMessage` verbatim, without
  changing codes or paths. Syntax and capability failures retain their own
  diagnostics; member overrides replace the entire declaration.
  Single type selectors match concrete types and
  documented categories, including enum/intEnum specializations and list aliases.
  Member IDs match the member shape rather than its target. Type-token sequences
  intersect predicates; nested `:test` and `:not` expressions support
  alternatives and negation with bounded scanning and nesting. Exact trait
  existence predicates also inspect referenced roots or members; unmaterialized
  implicit prelude trait sets remain unsupported. Unsupported or
  partially parsed expressions cannot yield a match through short-circuiting.
  `:is` unions supported same-shape predicate results, including trait-existence
  checks, without approximating graph-yielding branches. The deprecated optional
  `:each` alias remains unsupported.
  Relative ID assembly, other selectors on existing targets and required
  references to unindexed
  implicit prelude shapes report `Unsupported`. General selector syntax and
  evaluation remain unvalidated.
  `traitValidators` checks trait-definition targets, nonempty event IDs and rule
  objects with required string selectors, optional string messages and explicit
  recognized severities. Selector syntax/execution and configured validation
  events remain unvalidated.
  `optionalAuth` checks operation applicability and object annotations without
  copying service schemes, inferring disabled authentication or changing runtime
  enforcement. Effective service authentication remains unvalidated.
  `unstable` checks object declarations and optional string `featureId` values
  with a 100-Unicode-scalar limit, without normalization or shape acquisition.
  Enclosing-service `unstableFeatures` membership, generated warnings and
  model-diff behavior remain unvalidated.
  `endpoint` checks operation applicability, object/string declarations,
  registered-name prefix literals and distinct nonadjacent labels. Labels
  require exact-name top-level input members with required string-valued
  `hostLabel` declarations, including enum specializations. Host-label and HTTP
  bindings remain independent. Extra/nested/output host-label annotations do
  not become template dependencies. Trailing-period guidance remains a
  recommendation; runtime prefix expansion, label values and the final combined
  host are not evaluated.
  `httpChecksumRequired` checks operation applicability and object annotations
  without requiring an `http` trait, calculating a checksum or inserting headers.
  `http` checks operation applicability, required string method/URI values and
  exact success codes from 100 to 999. Methods remain literal. Local URI rules
  reject invalid origin-form syntax, dot/empty segments, fragments, query labels
  and malformed or duplicate labels. Required top-level input `httpLabel`
  members must match labels in both directions. Scalar targets and the single
  greedy string label are checked, without requiring the greedy label to be
  last. All seven message-binding traits conflict pairwise on a member.
  Nested/output/error labels do not bind the operation URI. Service-wide route
  comparison, endpoint resolution and runtime HTTP serialization remain outside
  this selector.
  Header/query bindings check scalar and list targets, annotation values and
  their respective case-insensitive/case-sensitive name uniqueness. Headers
  require valid HTTP field-name tokens. Query-map bindings accept string or
  string-list map values; header-prefix bindings accept only non-sparse
  string-valued maps, with one member per structure and no overlap between a
  nonempty prefix and explicit header names. `httpPayload` accepts any data
  target but only one member per structure. Top-level input/output/error
  sibling bindings follow their respective allowed locations, without applying
  those sibling rules to nested structures. HTTP-bound streaming inputs and
  outputs require payload annotations; non-HTTP streams remain valid without
  them. These checks do not perform runtime serialization or binding precedence.
  `httpError` checks error-structure applicability and exact signed-32-bit
  integer values without converting recommended client/server ranges into
  mandatory bounds. `httpResponseCode` checks object-valued integer-targeting
  structure members, excludes input-marked containers, enforces one marked
  member per structure and rejects all six conflicting HTTP bindings in either
  property order. It does not infer effective response bindings from nested
  declarations, assign default codes or emit HTTP responses.
  `streaming` checks blob/union applicability, object annotations, structure-only
  event alternatives and one streaming target per top-level input/output
  structure. Streaming blob members require explicit required/default traits.
  Reached streaming shapes and their containers also undergo inbound aggregate
  member checks across authored shapes; neither nested containers nor
  non-top-level stream references are permitted. `requiresLength` is restricted
  to streaming blobs referenced only by inputs.
  `requestCompression` checks operation applicability, a nonempty array of
  case-insensitive gzip encodings and the conflict with length-requiring
  streaming inputs. Authored declarations are preserved without compression,
  length calculation or stream execution.
  `eventHeader`/`eventPayload` check object values, structure-member applicability,
  declared target kinds and enum specializations. Their mutual conflict is
  property-order independent. A structure can declare only one explicit payload;
  every sibling must then be a header. Separate event structures have independent
  limits; implicit payloads and multiple headers remain valid. These are
  declaration checks, without event serialization or protocol framing.
  `jsonName` requires a string-valued structure/union member annotation and
  unique exact explicit names within each container. `timestampFormat` checks
  timestamp root/member eligibility and the three declared serialization formats.
  Neither annotation rewrites the selected operation or changes default-node
  representations; protocol serialization and full name mapping remain open.
  `mediaType` checks blob/string roots, including enum string specializations,
  string values, RFC 6838 type/subtype names and parameter syntax. Each name
  component permits 1 through 127 characters. Wildcards are rejected without
  restricting names to registered IANA values. The selector preserves casing
  and parameters without interpreting default content or assigning HTTP headers.
  `xmlName` checks structure/union roots and data members against the
  publisher's ASCII identifier or prefix:identifier grammar, rather than the
  broader XML name grammar. `xmlAttribute` checks object-valued structure-member
  annotations on Boolean/numeric/string/timestamp targets, including enum
  specializations, and its conflict with `xmlNamespace` in either property order.
  `xmlFlattened` checks object-valued structure/union members targeting lists
  or maps. Multiple annotated members remain valid, without inferred name
  uniqueness. `xmlNamespace` checks object values on data roots/members,
  required nonempty URI references and optional ASCII identifier prefixes.
  Raw URI whitespace and control characters are rejected; percent escapes
  retain their authored spelling.
  Relative references, including RFC 3986 fragments, remain accepted without
  resolution or normalization. Namespace URIs are not dependency-acquisition
  URLs. No XML serialization, prefix binding, namespace scope validation or
  service protocol qualification is added.
  Recognized prelude traits now check documentation/error values and the object
  shape of annotation, idempotent and trait-marker values throughout the
  selected closure. Explicit recognized prelude targets and their declared
  readonly/idempotent, input/output/error and error/trait-definition conflicts
  are checked. Optionality annotations check object values and explicit targets:
  `clientOptional` requires a structure member, `addedDefault` also requires an
  authored default, and `sparse` requires a list or map. These checks do not
  generate client types, serialize instances or bypass default validation.
  `uniqueItems` checks list-only applicability, object values and the conflict
  with `sparse`. Its transitive data graph cannot contain float, double or
  document shapes; recursive traversal excludes trait-definition dependencies.
  Applied custom-trait lists check typed value equality and uniqueness as
  described above; ordinary data-instance validation remains outside the selector.
  Private shapes reject reached data/custom-trait references from other exact
  namespaces, including repeated references after a permitted visit. Private
  member markings do not change target accessibility; literal values and
  unrelated model references remain outside the selected-closure check.
  `length` declarations check eligible root/member kinds, at least one bound,
  and exact integral signed-64-bit bounds. Non-null defaults check effective
  bounds, with member traits superseding target traits. Strings count Unicode
  scalars, blobs count decoded bytes, and empty list/map defaults count zero.
  Null member overrides are not measured. Applied custom-trait values and map
  keys also check effective length bounds; ordinary data-instance validation
  remains outside the selector.
  `range` declarations check numeric targets, required bounds, integral bounds
  for integral targets and exact native limits. Numeric defaults check
  inclusive effective bounds, with member traits replacing target traits.
  Null overrides remain valid. String-encoded bounds support bounded JSON-number
  spellings; other spellings and special floating defaults with ranges report
  unsupported. Applied custom-trait values also check effective range bounds;
  ordinary data-instance validation remains outside the selector.
  `pattern` declarations check string/enum targets and string values through
  the shared bounded ECMA-262 syntax checker. Unsupported syntax and non-null
  defaults requiring matching report unsupported; no anchors or .NET matching
  semantics are substituted. Pattern matching remains open.
  The deprecated `enum` trait checks string-root applicability, nonempty unique
  values, valid unique all-or-none names and optional metadata types. Authored
  defaults check membership and effective length bounds; null overrides remain
  valid. No enum-shape conversion or client generation is performed.
  `deprecated` checks object values and optional message/since strings.
  `recommended` checks structure-member applicability, optional reason strings
  and its conflict with `required` in either property order. Reached `box`
  applications are rejected as forbidden in Smithy 2.0; unrelated definitions
  remain outside the selected-closure check.
  `title`/`since` strings, `tags` string arrays and `internal` object annotations
  retain authored metadata on roots and members. `sensitive` checks data-root
  applicability and object values. No filtering, client behavior or default
  rewriting is performed.
  Idempotent `exists`/`notFound` declarations are mutually exclusive
  arrays of absolute IDs whose targets must occur in the operation's own errors.
  Their native hints are retained without execution or fallback to service errors.
  Input/output-marked structures reached by the selected operation are checked
  for model-wide references in recognized AST positions: only the matching slot
  of one operation can use each structure. Trait payloads, metadata and service
  renames do not add reference edges. This does not validate unrelated shapes
  or specializations outside the selected closure. Declared custom-trait
  conflicts are checked on reachable shapes, members and trait definitions,
  independent of property order. Unapplied conflict IDs need no definition.
  Trait-marker selector/conflicts/structurallyExclusive properties have shape
  checks. Structural exclusivity is enforced per reachable structure and trait:
  member mode limits direct member markings; target mode limits members whose
  targets carry the trait, including repeated use of the same target. Other
  structures and trait IDs have separate limits. Trait-marker `breakingChanges`
  checks require a nonempty rule array, recognized change/severity values,
  string messages and JSON Pointer path syntax. Nonempty paths resolve through
  the reached trait schema: named structure/union members, list `member` and
  map `key`/`value` segments. Missing members, instance indices and descent into
  unmodeled content are rejected; recursive traversal and repeated member scans
  remain subject to depth/work limits. Empty or absent paths select the whole
  trait. This does not execute change rules or compare models. Semantic model
  comparison remains open, as do general selector evaluation and other trait
  member schemas.
  The seven implicit primitive prelude types retain their native scalar kinds
  and false/zero root defaults. Structure members must repeat those defaults
  or override them with null; list, map and union members need no repetition.
  Reachable defaults check applicability, root-null rejection, member null
  overrides, explicit repetition of root defaults, native scalar types,
  integer ranges, finite float/double numbers and exact special-value strings,
  base64 blobs, enum membership and empty list/map/document containers. Defaults
  are not inserted into operations. Numeric timestamp defaults check exact
  epoch-second bounds for years 0001 through 9999 and millisecond resolution.
  String timestamps check RFC 3339 UTC Z syntax and Gregorian dates, including
  millisecond-aligned fractions with trailing zeros. Leap seconds and nonzero
  sub-millisecond fractions report unsupported, without rounding.
  Null overrides and exact default repetition remain supported. String-encoded
  bigInteger/bigDecimal defaults support bounded JSON-number spellings, exact
  integrality and range checks. Other spellings report unsupported; applied pattern
  constraints and full default validation remain open.
- Complete end-to-end event/outbox delivery qualification while preserving
  atomic mutation publication and caller-visible failure semantics.
- Provide an application-specific adapter or forwarding layer when an existing
  authoritative endpoint must retain semantics that do not fit the current
  local-engine persistence seam.
- Qualify broader live federation deployments, external target/validator
  policies, and catalog discovery separately from the current producer-resolved
  and fixed write-through surfaces.
- Implement specialized USD/MaterialX validation or authorized external plugin
  acquisition only with explicit scope, bounded work, and publisher policy.
- A bounded on-disk Git acquisition path is required before repositories larger
  than the current 256 MiB buffered acquisition limit can be supported.

## Explicit non-commitments

The OPC UA client binding exists, but this roadmap does not commit the project
to Core API views, NodeSet projection, NativeAOT qualification, or complete
protocol conformance. The current documentation also does not commit the project
to a native OpenUSD runtime, Git publication, GitHub Packages publication,
arbitrary schema dialects, external TLS termination for the samples, federation
mutation, automatic migration from another implementation, or compatibility
aliases for old private APIs. Adding any of these requires a separately approved
design and evidence plan.
