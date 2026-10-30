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
Full native TUnit requirement receipts remain incomplete. All four .NET 8
complete-suite summaries failed for AspNetCore, HTTP, Federation and Git
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
- Add full API format validators and the remaining language resolvers. The
  OpenAPI/Swagger, OpenRPC, Avro Protocol and assembled Smithy JSON selectors
  do not establish full format validation, YAML support, or gRPC, Thrift,
  Avro IDL, GraphQL, WSDL or Smithy IDL/model-assembly support. Avro message
  selection preserves the declared native message. It checks protocol/type
  declaration names, effective namespace collisions and request fields, then
  binds named request/response/error schemas and checks defaults in native
  declaration order across the complete protocol. Avro 1.12 union defaults
  use the first matching branch. Logical-type annotations remain unsupported;
  complete protocol validation and qualification remain open.
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
  server and documentation URLs remain native strings without invocation.
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
  Instance uniqueness and value equality are not evaluated.
  Private shapes reject reached data/custom-trait references from other exact
  namespaces, including repeated references after a permitted visit. Private
  member markings do not change target accessibility; literal values and
  unrelated model references remain outside the selected-closure check.
  `length` declarations check eligible root/member kinds, at least one bound,
  and exact integral signed-64-bit bounds. Non-null defaults check effective
  bounds, with member traits superseding target traits. Strings count Unicode
  scalars, blobs count decoded bytes, and empty list/map defaults count zero.
  Null member overrides are not measured; instance length evaluation remains open.
  `range` declarations check numeric targets, required bounds, integral bounds
  for integral targets and exact native limits. Numeric defaults check
  inclusive effective bounds, with member traits replacing target traits.
  Null overrides remain valid. String-encoded bounds and special floating
  defaults with ranges report unsupported; instance range checks remain open.
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
  string messages and JSON Pointer path syntax. Trait-schema path resolution
  and semantic model comparison remain open, as do general selector evaluation
  and other trait member schemas.
  The seven implicit primitive prelude types retain their native scalar kinds
  and false/zero root defaults. Structure members must repeat those defaults
  or override them with null; list, map and union members need no repetition.
  Reachable defaults check applicability, root-null rejection, member null
  overrides, explicit repetition of root defaults, native scalar types,
  integer ranges, finite float/double numbers and exact special-value strings,
  base64 blobs, enum membership and empty list/map/document containers. Defaults
  are not inserted into operations. Timestamp and string-encoded
  bigInteger/bigDecimal defaults report unsupported; applied pattern
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
