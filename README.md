# xRegistry .NET

Current source and CI package version: **1.0.0-rc4**.

A model-driven xRegistry client and ASP.NET Core server for .NET 8 and .NET 10.
Eleven runtime packages and three .NET 10 samples implement the non-OPC-UA
specification family and its supported native bindings.

**Implemented, not release-qualified.** All extracted specification clauses have
source-pinned reviews, including explicit application-runtime, policy and
qualification boundaries. Windows x64 Native AOT executions are recorded;
the current Linux/ARM64 matrix, independent-peer limitations and controlled
performance qualification remain open. Neither package availability nor test
counts imply universal protocol or schema-language conformance.

## Build

Install the SDK pinned by `global.json` and Python 3.13. In your Python
environment, install the hash-locked independent test-oracle dependencies,
then run:

```powershell
python -m pip install --require-hashes --only-binary=:all: -r eng\requirements-oracles.lock
dotnet build XRegistry.slnx -c Release
python eng\check_packages.py --evaluate
pwsh -NoProfile -File eng\test.ps1
```

The first build restores dependencies; subsequent CI builds use checked package
lock files. TUnit tests use Microsoft.Testing.Platform rather than VSTest
filters. Native publishing needs the matching platform toolchain.
The test script limits concurrent test modules to two on shared runners;
`-MaxParallelTestModules` can be set explicitly on larger machines.

`eng/build.ps1` performs a locked restore and build; `-UpdateLocks` is reserved
for deliberate dependency-manifest updates. `eng/pack.ps1` creates development
packages locally. Its `-Release` mode fails until package, specification and
source-state qualification is complete. Neither command publishes anything.

For exact-version CI packages after a locked build, run
`python eng\package_build.py` (or pass an explicit `--version`).
Commit version changes first; NBGV reads the committed version, and an explicit
package version must agree with `version.json`.
The `package-build` workflow uploads all eleven `1.0.0-rc4` package/symbol pairs
and their source/hash inventory. These artifacts are not automatically promoted.
See [release and trusted-publisher setup](docs/releasing.md).

## Scope and packages

`eng/packages.json` lists the core, client, server, ASP.NET Core host, durable file
store, domain-model/validation, federation, and File/Git/OCI binding packages.
Entries are marked `implemented`, not `qualified`. Publication remains gated on
the separate source, conformance and native-platform release requirements.

Three principal .NET 10 samples are implemented:

| Sample | Purpose |
| --- | --- |
| [File server](samples/XRegistry.FileServer/README.md) | Durable SQLite/file-backed API, all-domain/default or custom models, HTTPS/authentication, administration, export and backup/restart. |
| [Federation bridge](samples/XRegistry.FederationBridge/README.md) | Producer-resolved reads over configured HTTP/File/Git/OCI sources and separately authorized single-upstream HTTP write-through mounts. |
| [Client](samples/XRegistry.Client/README.md) | Generic HTTP operations, discovery/paging, File/Git/OCI reads, schema validation and explicit OCI producer operations. |

Federation itself remains read-only. Git acquisition and object handling are
managed code, without an installed/bundled Git executable or native Git library.
Independent tests use reference Git as an oracle; that is not a runtime dependency.
The samples have explicit, documented profile limits and are not a substitute
for the unfinished complete conformance/native-platform qualification.

For an isolated local demonstration, use an empty, caller-owned local data
directory:

```powershell
dotnet run --project samples\XRegistry.FileServer --no-launch-profile -- --DataRoot D:\RegistryData --Initialize true --DemoLoopback true --PublicRoot http://127.0.0.1:5280/registry --ListenPort 5280
```

The demo explicitly grants local callers administration. Never forward its
port. Production requires configured HTTPS and credentials; see
[sample security](docs/sample-security.md). Restart without `--Initialize true`.

- [Architecture contract](docs/architecture.md)
- [Security contract and threat model](docs/security.md)
- [Conformance evidence](docs/conformance.md)
- [HTTP client, discovery and paging](docs/http-client.md)
- [Bounded HTTP compression](docs/http-compression.md)
- [Live HTTP federation](docs/http-federation.md)
- [Interaction events](docs/events.md)
- [Native AOT evidence and remaining cells](docs/native-aot.md)
- [Standalone consumer embedding and migration boundaries](docs/consumer-migration.md)
- [Independent upstream-to-.NET results and checker limitation](docs/reverse-interoperability.md)
- [Performance measurements and remaining gates](docs/performance.md)
- [Measured native durable-server workloads](docs/server-performance.md)
- [Domain terminology](CONTEXT.md)
- [Contributing](CONTRIBUTING.md)

No OPC UA implementation or native OpenUSD runtime is included. Existing
OPC UA integration is informative consumer input, not a dependency or API
compatibility constraint.
