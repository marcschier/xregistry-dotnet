# Architecture contract

This document records the approved architecture, not a claim that its runtime
features are implemented. Implementation evidence belongs in the
[conformance report](conformance.md).

## Module interfaces

The core module owns registry semantics and lossless values. Binding modules
translate those values without redefining their meaning. A Module's interface
includes ownership, authorization, consistency, cancellation, and failure
behavior, not just its public method declarations.

| Module | Interface responsibility | Must not require |
| --- | --- | --- |
| Core | Identity, model, operation/result and source-read contracts | ASP.NET, SQLite, OPC UA, runtime code generation |
| HTTP client | Arbitrary model-defined HTTP registry operations | Generated code for each remote model, a proprietary server |
| Registry engine | Model/lifecycle/query semantics and atomic mutations | A particular HTTP host or persistent store |
| HTTP host | Standards-compliant routes, wire representations and host authorization | The file store or an OPC UA endpoint |
| Durable store | Authoritative snapshots and durable atomic publication | Shared-folder coordination or a remote database |
| Built-in models | Scoped model documents and domain metadata conveniences | Broker, renderer or application-protocol runtime |
| Validation | Declared format and compatibility outcomes | Runtime plugin discovery or a fabricated compatible result |
| Federation | Deterministic source selection and read resolution | Cross-registry mutations or source synchronization |
| File/Git/OCI | Binding-native acquisition and consistent exact-byte reads | Conversion through a fake HTTP server |

`eng/packages.json` is the package, target-framework and sample inventory.
Package implementation status is explicit; inventory membership does not prove
that a package is ready to publish.

## Public seams

The approved seams are public model/client operations, actual HTTP endpoints,
registry operations across restart and recovery, federation/source readers, and
clean NuGet/native consumers. Internal methods are not a substitute test surface.

An in-memory store and a durable file store implement the same semantic store
contract. File and Git implement a common document-tree access contract used by
one directory-mapping interpreter. Remote OCI and local OCI layouts share graph
semantics. An HTTP client and an in-process registry can expose common read
operations without claiming the same snapshot or mutation guarantees.

## Identity and ownership

Registry paths and identifiers are not operating-system paths. Preserve case,
JSON types, canonical integer values, empty Documents, and absent versus null
metadata. Decode at a specified interface exactly once. Do not use URI or
filesystem normalization to silently change a legal identifier.

Returned metadata must own or explicitly lease its backing storage. Returned
document streams have documented disposal and lifetime rules. Caller-supplied
clients and streams are not disposed unless ownership was explicitly transferred.
Cancellation applies across the complete operation, including body reads,
validation, source resolution and staging.

`RegistryJson.Create` accepts a synchronous `Utf8JsonWriter` callback and shared
`RegistryJsonLimits`. It bounds committed output bytes and validates the
finished JSON before returning an independently owned value. The callback must
write one complete value and must not retain the writer; callback failures do
not publish partial output. Binding modules reuse this factory instead of
implementing another unbounded JSON staging buffer.

Expose neither OPC UA-specific value types nor the informative UA branch's
experimental envelope as a prerequisite. Consumers supply their own adapter
rather than imposing native transport details on every caller.

## Local mutations

Validate the complete request and required response representation before
publishing a mutation. The store atomically commits the entire request's
metadata, lifecycle changes, document references and event records.

Document files become durable before committed metadata refers to them.
Interrupted preparation may leave collectible unreferenced files, but it must
not expose a partially updated Registry. Garbage collection respects committed
references and active read leases.

A transport failure after commit does not imply rollback. Do not retry
mutations automatically: even identical PUT requests can advance Epochs and
timestamps. Do not simulate atomic mutation with read/check/write or compensating
writes against an ordinary remote Registry.

## Federation and write-through

Federation remains read-only. The producer-resolved aggregate and configured
write-through mounts are different host surfaces. An aggregate mutation is
rejected before dispatch. A write-through request selects exactly one upstream
and does not acknowledge success before that upstream completes the operation.

Retain the Selected Origin for the Resource's subsequent reads. No speculative
fallback after a selected source rejects authentication, violates policy, or
fails validation. A producer-resolved view is not traversed again by a consumer.

Only model-aware navigation links may be rebased. Arbitrary domain Documents,
extension strings, external document URLs, XIDs and credential destinations must
not be indiscriminately rewritten.

## Native AOT and extension registration

Shipping runtime libraries target .NET 8 and .NET 10. Hosts and extensions are
constructed explicitly or registered through compile-time-visible dependency
injection. Fixed JSON shapes use source-generated contracts; dynamic model
values use bounded explicit readers/writers.

No dynamic assemblies, reflection-based model discovery, runtime serializers
generated for each schema, or native Git backend are part of the runtime design.
Normal .NET platform I/O is not misrepresented as having no native internals.
The SQLite dependency is isolated in the durable-store package.

The eight library-consumer TFM/RID cells and twelve principal sample/RID cells
must execute the real native binaries. A declaration or successful compilation
alone is not deployment qualification.

## Dependencies and provenance

The core package does not depend on another marcschier repository's runtime
package. Build tools, test oracles, and reference Git used to generate fixtures
are not runtime Git implementations; they must remain out of shipping dependency
graphs.

Use one implementation per semantic function. Reuse a library only after its
actual package assets, license, security behavior and AOT execution qualify the
required subset. A library's compatible TFM is not an AOT guarantee.

The [architectural decisions](adr/0001-read-only-federation.md) record the
non-obvious scope and storage trade-offs.
