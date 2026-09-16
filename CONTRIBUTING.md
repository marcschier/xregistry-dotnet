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

NuGet dependency versions are centrally managed in `Directory.Packages.props`.
Use normal restore and build commands:

```powershell
dotnet restore XRegistry.slnx
dotnet build XRegistry.slnx -c Release --no-restore
```

Keep NuGet audit and package-source mapping enabled, and review unexpected
dependency or source changes. NuGet restore is lockfile-free. The independent
Python specification-oracle environment is governed separately by the
hash-locked `eng\requirements-oracles.lock`; install it with `--require-hashes`
as shown in the root README.

Do not add secrets, dynamic runtime code generation, automatic mutation retries,
native/executable Git fallbacks, permissive TLS validation, or silent fallback
stores. See [the security contract](docs/security.md).
