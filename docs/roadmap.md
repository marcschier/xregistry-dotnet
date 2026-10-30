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
  selection preserves the declared native message but does not check all schema
  definitions, name binding or defaults.
  OpenAPI 3.1 selection includes webhook operation IDs and inline webhook method
  pointers, with ambiguity checking across paths and webhooks. OpenAPI selection
  also traverses nested callbacks and caller-supplied Callback references within
  finite depth/work limits. It does not evaluate runtime expressions or validate
  their complete syntax. Inline JSON Pointers can select reusable Path Item and
  Callback component methods without dereferencing or making unused definitions
  into operation-ID candidates.
  Smithy selection checks required operation/data/custom-trait dependencies in
  an already assembled non-mixin AST and rejects invalid recursion in that
  closure, invalid enum targets and duplicate or malformed enum values. Its
  implicit prelude is limited, and full trait/schema validation remains open.
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
