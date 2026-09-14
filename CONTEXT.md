# xRegistry

xRegistry describes model-defined registries and the resources they expose.
This glossary distinguishes registry semantics from storage and transport terms.

## Language

**Registry**:
A model-defined hierarchy of Groups, Resources, and Versions with an identity
and registry-level metadata.
_Avoid_: database, catalog (unless it is specifically a catalog Registry).

**Group**:
An entity that contains Resources belonging to the collections allowed by its
Group model.
_Avoid_: directory, namespace.

**Resource**:
A logical, addressable resource whose content and version attributes belong to
its Versions.
_Avoid_: file, Version.

**Version**:
An addressable revision of a Resource. A Version is not necessarily immutable;
its allowed changes are governed by the model and registry operations.
_Avoid_: immutable snapshot.

**Resource Meta**:
The metadata entity governing the Resource, including its default-Version state.
It is distinct from the metadata of an individual Version.
_Avoid_: Version metadata.

**Document**:
The exact domain-content bytes of a document-bearing Version, distinct from
the registry metadata describing those bytes.
_Avoid_: JSON metadata, serialized entity.

**Model Source**:
The declared model inputs from which a Registry's effective Model is obtained.
_Avoid_: effective Model.

**Model**:
The effective definitions and constraints governing a Registry's entity types,
attributes, and Resource behavior.
_Avoid_: Model Source.

**Epoch**:
An entity's local mutation value used by the specified concurrency rules.
Epochs are not globally comparable clocks or recursive Registry change markers.
_Avoid_: timestamp, global generation.

**Catalog**:
A Registry containing descriptions and advertisements of other Registries.
Catalog membership alone does not combine those Registries or authorize access.
_Avoid_: merged Registry.

**Federation**:
Read-only resolution across independently administered Registries under the
specified selection, identity, and policy rules.
_Avoid_: synchronization, replication, cross-registry transaction.

**Write-through Mount**:
An explicitly selected frontend location whose mutations are addressed to one
authoritative upstream Registry.
_Avoid_: federation write, mirrored write.

**Selected Origin**:
The source selected for a Resource and retained for its associated metadata,
Version, and Document reads.
_Avoid_: whichever source responds first.

**Producer-resolved View**:
A view whose producer has already performed federation resolution.
_Avoid_: a catalog that the consumer must traverse again.

**Registry Context**:
The identity, selected source, representation, and consistency context under
which a Registry operation is interpreted.
_Avoid_: URL alone, credential profile.

**Snapshot Pin**:
A resolved binding-specific revision identity retained for consistent reads.
It is not, by itself, proof of authorship or a cryptographic signature.
_Avoid_: mutable tag, current branch.

**Endpoint Definition**:
Domain metadata describing an application-protocol endpoint.
_Avoid_: registry HTTP route.
