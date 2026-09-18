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
  seven remaining framework/RID cells. Remaining work: add the same opt-in
  proof test to the other test projects, decide whether/how the existing
  native CI matrix (or a new job) runs the full managed test suites natively
  across all eight `net8.0`/`net10.0` × `win-x64`/`win-arm64`/`linux-x64`/
  `linux-arm64` cells and uploads receipts, then map each reviewed
  requirement's `testIds` to real method names and attach the resulting
  evidence per requirement.

## Native platforms and package consumers

- Execute the required .NET 8/.NET 10 Windows/Linux x64/ARM64 library-consumer
  cells and the .NET 10 principal-sample cells with real Native AOT binaries.
- Refresh source-matched package-consumer and sample evidence after material
  package changes. Preserve exact source, package, executable, and report hashes.
- Resolve the .NET 8 HTTP test-host warning gate caused by the packaged
  System.Text.Json 10.0.x/framework combination without suppressing warnings or
  weakening the clean package-consumer result.
- Add the still-unexecuted Linux/ARM64 sample, storage, binding, federation,
  interoperability, and performance cells described in
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
- Configure and verify the account-owned branch/tag protections, release
  environment approvals, and NuGet trusted publishing policy before any real
  publication.

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

The current documentation does not commit the project to an OPC UA runtime,
native OpenUSD runtime, Git publication, GitHub Packages publication, arbitrary
schema dialects, external TLS termination for the samples, federation mutation,
automatic migration from another implementation, or compatibility aliases for
old private APIs. Adding any of these requires a separately approved design and
evidence plan.
