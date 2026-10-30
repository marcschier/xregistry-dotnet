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
  receipt shape `manage.py`'s `release_check` validates. It has been proven
  end to end: `XRegistry.Models.Tests` (net10.0) publishes and runs natively
  with `dotnet publish ... -p:PublishAot=true`, its opt-in
  `NativeExecutionProofTests` reports genuine `nativeAot: true,
  jitCompiledMethods: 0` (versus a JIT control's `nativeAot: false,
  jitCompiledMethods: 7169`), and the resulting receipt passes every
  structural/hash/native check in `release_check` up to the still-missing
  seven remaining framework/RID cells. Models, Federation, Server, Storage.File and HTTP
  now share the proof test; the complete Federation suite passed 626 native cases on
  .NET 10/Windows x64 at `6d143c4`, with zero IL diagnostics and zero JIT-compiled
  methods. Its receipt covers 242 distinct methods, not 626 independent methods.
  At `f97af1b`, complete Server and Storage.File suites passed 886 and 32 native
  cases in that cell, including incremental publication and durable restart
  contracts, with zero IL diagnostics or JIT compilations. Their receipts
  cover 415 and 30 distinct methods. Managed controls were rejected without
  creating receipts.
  Models and Federation were refreshed at `c196737` in the same cell: 985 and
  636 cases, including all 31 incremental selector and 10 incremental provider
  cases. Their receipts cover 329 and 250 distinct methods, with zero IL/JIT and
  rejected managed controls.
  At `306e6cd`, the complete HTTP suite passed 421 native cases in that cell,
  including all 28 incremental publication-to-resolution workflow cases.
  Its receipt covers 136 methods, with zero IL/JIT and a rejected managed control.
  Remaining work: link the shared proof into the other test projects, decide
  whether/how the existing native CI matrix (or a new job) runs the full suites
  across all eight `net8.0`/`net10.0` × `win-x64`/`win-arm64`/`linux-x64`/
  `linux-arm64` cells and uploads receipts, then map each reviewed
  requirement's `testIds` to real method names and attach the resulting
  evidence per requirement.

## Native platforms and package consumers

The selected generic package, model, client/bridge sample and storage matrix
passed all 28 jobs at `ecd2a3b` on Windows/Linux x64 and ARM64. See
[native evidence](native-aot.md#current-evidence-and-uncovered-cells).
Full native TUnit requirement receipts remain incomplete. Models and Federation
passed 985 and 636 native cases at `c196737`; Server and Storage.File passed
886 and 32 at `f97af1b`. All four are .NET 10/Windows x64 receipts tied to their
own sources. They do not refresh the package-consumer matrix or qualify the
other platforms. The complete HTTP suite subsequently passed 421 native cases
in the same cell at `306e6cd`; its receipt is likewise source-specific.

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

- Complete the approved draft-alignment scope against the immutable successor
  sources in `eng/specification/successors.json`. The source inventory does not
  replace clause review or execution evidence.
- Complete the selected incremental WoT profile. Separate fixed-profile models,
  atomic allocation, immutable revision bytes and compatibility lines now have
  managed publication evidence, including concurrent conflicts and deletion
  across file-store restarts. The full managed gate passed 10,666 cases and 283
  tooling tests at `7e84cad`; complete Server and Storage.File native suites
  passed on .NET 10/Windows x64 at `f97af1b`. Parsed incremental selection now
  uses exact lines, numeric floors/ranking, sticky defaults and origin-scoped
  acquisition; retained local references use the captured revision and line.
  Its full managed gate passed 10,748 cases and 283 tooling tests.
  Complete Models/Federation native suites passed at `c196737` on
  .NET 10/Windows x64. Live HTTP TM/TD publication-to-resolution workflows now
  have managed and .NET 10/Windows x64 native evidence at `306e6cd`, including
  exact replay, rejection without allocation, sticky conflicts and retained
  local resolution after source shutdown. The complete managed gate passed
  10,820 cases and 283 tooling tests.
  Other native cells, URI/context mapping, line eligibility diagnostics and
  durable HTTP restart workflows remain open. The older module receipts do not
  qualify complete Server/Federation suites at the HTTP checkpoint.
  SemVer provider acquisition now
  performs scoped native-source lookup and verifies acquired byte digests.
  Parsed local references use the retained containing revision and its authored
  version fields. JSON Pointer fragments are applied after selection without
  changing full-document provenance, with managed and .NET 10/Windows x64 native
  evidence.
  URI/base/JSON-LD context processing and alias integration remain open.
  WoT publication closure also needs the unresolved source-occurrence and target
  mappings from the draft; do not substitute live targets for captured closure.
- Add full API format validators and the remaining language resolvers. The
  OpenAPI/Swagger and OpenRPC JSON selectors do not establish format validation,
  YAML support, or gRPC, Thrift, Avro Protocol/IDL, GraphQL, WSDL or Smithy support.
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
