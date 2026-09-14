# Contributing

Use the pinned SDK, central package versions, nullable analysis, existing
analyzers, and the public module interfaces described in
[the architecture contract](docs/architecture.md).

Implement changes as observable vertical slices. Use independent expected wire
values and model fixtures; a client/server roundtrip using the same codec is not
an independent conformance oracle. Add boundary, rejection, cancellation and
resource-limit cases relevant to each change.

Keep pinned source artifacts byte-identical. Do not overwrite evidence or
silently regenerate the baseline from a moving checkout. Specification
corrections require a minimal reproduction, classification, an explicit
successor baseline, and an interoperability impact record.

Package statuses and release gates must describe reality. Never mark a feature
qualified because its project builds or its test runner returns success without
executing the relevant cases.

Source `packages.lock.json` files describe portable restores. RID-specific and
Native AOT restores use `native-<rid>.packages.lock.json` (or `native-aot` without
an explicit RID) under each project's evaluated `MSBuildProjectExtensionsPath`,
including custom artifact roots. Do not copy native restore profiles over the
source locks. Explicit caller-provided lock paths are preserved. After changing
project references, regenerate the portable graph with the SDK and verify
`dotnet restore XRegistry.slnx --locked-mode`; retained package versions/content
hashes must not drift unexpectedly.

Do not add secrets, dynamic runtime code generation, automatic mutation retries,
native/executable Git fallbacks, permissive TLS validation, or silent fallback
stores. See [the security contract](docs/security.md).
