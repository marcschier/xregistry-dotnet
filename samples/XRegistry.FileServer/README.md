# Durable xRegistry API server

See [ASP.NET Core integration](../../docs/aspnetcore-integration.md) to embed the
server packages in an application, or
[custom models](../../docs/custom-models.md) to run this sample with an
application-specific model.

This .NET 10 ASP.NET Core host combines `RegistryEngine`,
`LocalRegistryPersistence`, SQLite metadata and immutable Document files.
The default `all` model contains Endpoint, Message, Schema, Registry-of-Registries
and OpenUSD Group types. No OPC UA binding or OpenUSD runtime is loaded.

## Explicit local demonstration

```powershell
dotnet run --project samples\XRegistry.FileServer --no-launch-profile -- --DataRoot D:\RegistryData --Initialize true --DemoLoopback true --PublicRoot http://127.0.0.1:5280/registry --ListenPort 5280
```

This mode grants local callers administration and prints a warning. It is not
production authentication; never forward or expose its port. Restart with the
same command **without `--Initialize true`**. An existing store is never erased
or silently replaced.

## HTTPS

Provide an operator-managed PFX and a high-entropy administrative token in a
named environment variable:

```powershell
dotnet run --project samples\XRegistry.FileServer --no-launch-profile -- --DataRoot D:\RegistryData --PublicRoot https://registry.example:8443/registry --ListenPort 8443 --CertificatePath D:\Certificates\registry.pfx --CertificatePasswordEnvironment REGISTRY_PFX_PASSWORD --WriteTokenEnvironment REGISTRY_ADMIN_TOKEN
```

Add `--Initialize true` only for a new directory. `--ReadTokenEnvironment`
selects an optional distinct read-only bearer token. `--AllowAnonymousReads true`
explicitly allows anonymous Registry/discovery reads, but not writes or health
diagnostics. Missing credentials or certificates do not enable demo mode.

See [shared sample security](..\..\docs\sample-security.md) for TLS, certificate,
token, listener and deployment rules. The sample uses coarse opaque bearer
roles, not JWT/OIDC or per-user accounts. Plaintext behind a TLS terminator is
not accepted; maintain TLS to the host.

`PublicRoot` controls advertised links and is never inferred from request Host.
`MountPath` can override local routing independently. The host supplies both
Registry-relative `.xregistry` and origin-root `/.well-known/xregistry`.
`/healthz` is authenticated and reports storage generation, not an entity epoch.

## Models and files

Use `--Model all|core|endpoint|message|schema|cloudevents|registry|openusd`, or
`--ModelFile MODEL.json` for a custom model. The latter does not implicitly fetch
external includes. `--InitialMetadata METADATA.json` supplies required custom
Registry attributes for first initialization.

The `registry` and `all` choices (including the default `all`) use explicit
trusted server presets: `BuiltInRegistryModels.CompileForServer(RegistryModelKind.Registry)`
and `CompileAllForServer()`. They add the catalog Resource's `modelcompatiblewith`
annotation to a detached model source so invalid catalog metadata is rejected
before publication. Packaged model bytes and imported Resource identity remain
unchanged. Other built-ins retain ordinary compilation, and custom `ModelFile`
input is not opted in by shape. See [catalog publication rules](..\..\docs\catalog.md).

Model administration, arbitrary model-defined Group/Resource/Version operations,
exact Documents, defaults, metadata headers and `/export` use the reusable
server/HTTP packages. Built-in syntax and supported compatibility policies are
enabled. Unsupported/indeterminate compatibility is not treated as compatible.
Persisted model source and its frozen compiled form take precedence on restart;
restart does not reacquire model includes.
An existing unannotated model is not silently upgraded by selecting a server
preset; an authorized model update is required and validates existing data.

Authenticated capability administration is enabled by default; use
`--AllowCapabilityUpdates false` to make it immutable. Enabled choices must stay
within the implemented/offered profile and cannot disable host authentication
or authorization. Core filtering, sorting, bounded opaque pagination, short
URLs and readonly-ignore behavior follow the reusable engine's current
capabilities and limits. Configuration changes and their events commit together.

Document requests default to an 8 MiB per-Document limit; `--MaxDocumentBytes`
can set an explicit limit through 256 MiB. Response/working-set bounds increase
with that selection rather than becoming unbounded. Storage metadata, blob,
snapshot, candidate and concurrency quotas also remain finite. Lifetime
correlation reservations consume metadata quota and are not silently discarded.
See [storage](..\..\docs\storage.md) and [server profile](..\..\docs\server.md).

## Offline backup and restore

Stop the writer, create an empty destination on a supported local filesystem,
then run:

```powershell
dotnet run --project samples\XRegistry.FileServer --no-launch-profile -- --DataRoot D:\RegistryData --BackupTo D:\RegistryBackup
```

This does not start HTTP or require server credentials. The exclusive writer
lock prevents backing up through a second active writer. The store backup API
copies a coherent generation and publishes its ready marker last. To restore,
start the stopped deployment against a validated backup directory; preserve the
original backup separately before making it writable.

Windows writes require fixed local NTFS; Linux writes require local ext4.
Shared/network and overlay writer filesystems are not qualified. Failed or
incomplete Registry initialization is not replaced by an empty success on restart.

## Native execution and qualification status

```powershell
pwsh -NoProfile -File eng\test-file-server.ps1 -RuntimeIdentifier win-x64 -DataRoot D:\NativeRegistryTests
```

The persistent driver executes the native host, proves JIT is not mistaken for
AOT, exercises both discovery locations and all-model startup, installs a custom
model, preserves binary/empty Documents across metadata updates, checks nested
rollback and export, kills/restarts the native process, and reopens an offline
backup. The Windows x64 run passed 15 checks, including capability administration
and stable short-URL Document/details behavior across restart.

These are actual HTTP/durability checks, not full specification qualification.
Production TLS/authentication has separate real-Kestrel/native coverage in
`XRegistry.SampleHosting.Tests`. Complete-protocol, native-platform,
independent-peer, security and performance qualification is not established;
see the [roadmap](../../docs/roadmap.md).
