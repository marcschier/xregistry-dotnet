# Documentation

The documents below describe the current implementation, its supported
boundaries, and the evidence attached to those boundaries. Future work is
centralized in the [roadmap](roadmap.md); retrospective implementation and
specification-correction history is in the [changelog](changelog.md).

## Start here

- [Project overview, samples, and build](../README.md)
- [Architecture contract](architecture.md)
- [Domain terminology](../CONTEXT.md)
- [Contributing](../CONTRIBUTING.md)
- [Security reporting](../SECURITY.md)
- [Roadmap](roadmap.md)
- [Changelog](changelog.md)

## Samples and consumer integration

- [General client sample](../samples/XRegistry.Client/README.md)
- [Durable FileServer sample](../samples/XRegistry.FileServer/README.md)
- [FederationBridge sample](../samples/XRegistry.FederationBridge/README.md)
- [Shared sample hosting security](sample-security.md)
- [Consumer embedding and role-oriented migration](consumer-migration.md)

## Core, server, and storage

- [Server engine and HTTP adapter](server.md)
- [Durable file-store foundation](storage.md)
- [Interaction events](events.md)
- [Catalog publication rules](catalog.md)
- [Built-in model sources](models.md)
- [Document validation](validation.md)
- [Concrete schema selection](schema-selection.md)
- [Message property declarations](message-declarations.md)
- [Message definition materialization](message-materialization.md)
- [Endpoint templates and consumer materialization](endpoint-templates.md)
- [OpenUSD identifier and integrity helpers](openusd.md)

## Clients and bindings

- [HTTP client foundation](http-client.md)
- [Bounded HTTP response content decoding](http-compression.md)
- [Native File sources and OCI layout publication](file-binding.md)
- [Git binding scope](git.md)
- [Managed Git binding](managed-git.md)
- [Managed Git smart-HTTP interoperability](git-interoperability.md)
- [Native OCI snapshots](oci.md)

## Federation

- [Live HTTP federation source](http-federation.md)
- [Bounded FederationBridge host](federation-bridge.md)
- [Read-only producer model admission](producer-model-completion.md)

## Operations, security, and release

- [Security contract and threat model](security.md)
- [Release safeguards and publishing](releasing.md)
- [Native AOT qualification](native-aot.md)

## Evidence and performance

- [Conformance evidence](conformance.md)
- [Specification provenance and verification](spec-feedback.md)
- [Specification tooling](../eng/specification/README.md)
- [Forward interoperability evidence](interoperability.md)
- [Reverse interoperability evidence](reverse-interoperability.md)
- [Performance evidence](performance.md)
- [Native durable-server performance](server-performance.md)

## Architectural decisions

- [ADR 0001: Separate read-only federation from write-through](adr/0001-read-only-federation.md)
- [ADR 0002: SQLite metadata and immutable Documents](adr/0002-sqlite-and-immutable-documents.md)
- [ADR 0003: Pure managed Git](adr/0003-pure-managed-git.md)
