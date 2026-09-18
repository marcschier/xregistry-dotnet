# xRegistry .NET

Current source and CI package version: **0.1.0-alpha**.

A model-driven xRegistry client and ASP.NET Core server for .NET 8 and .NET 10.
Eleven runtime packages provide Core, client, server, storage, validation,
federation, and File/Git/OCI binding surfaces. Three .NET 10 samples demonstrate
the supported host and client profiles.

Start with the [developer getting-started guide](docs/getting-started.md) to
consume the NuGet packages, call a Registry, embed an ASP.NET Core endpoint, or
define an application-specific model.

## 🧪 Samples

| Sample | Purpose |
| --- | --- |
| [File server](samples/XRegistry.FileServer/README.md) | Durable SQLite/file-backed API, all-domain/default or custom models, HTTPS/authentication, administration, export and backup/restart. |
| [Federation bridge](samples/XRegistry.FederationBridge/README.md) | Producer-resolved reads over configured HTTP/File/Git/OCI sources and separately authorized single-upstream HTTP write-through mounts. |
| [Client](samples/XRegistry.Client/README.md) | Generic HTTP operations, discovery/paging, File/Git/OCI reads, schema validation and explicit OCI producer operations. |

For an isolated local demonstration, use an empty, caller-owned local data
directory:

```powershell
dotnet run --project samples\XRegistry.FileServer --no-launch-profile -- --DataRoot D:\RegistryData --Initialize true --DemoLoopback true --PublicRoot http://127.0.0.1:5280/registry --ListenPort 5280
```

The demo explicitly grants local callers administration. Never forward or
expose its port. Production requires configured HTTPS and credentials; see
[sample security](docs/sample-security.md). Restart without `--Initialize true`.

## 🛠️ Build

Install the SDK pinned by `global.json` and Python 3.13. Install the independent
Python oracle dependencies from their hash-locked manifest, then use normal
NuGet restore/build:

```powershell
python -m pip install --require-hashes --only-binary=:all: -r eng\requirements-oracles.lock
dotnet restore XRegistry.slnx
dotnet build XRegistry.slnx -c Release --no-restore
python eng\check_packages.py --evaluate
pwsh -NoProfile -File eng\test.ps1
```

NuGet versions are centrally managed by `Directory.Packages.props`; audit and
package-source mapping remain enabled. NuGet restore is lockfile-free. The
Python oracle environment is separate and remains pinned by hashes in
`eng\requirements-oracles.lock`.

TUnit tests use Microsoft.Testing.Platform rather than VSTest filters. Native
publishing needs the matching platform toolchain. The test script limits
concurrent test modules to two on shared runners; set
`-MaxParallelTestModules` explicitly on larger machines.

`eng\build.ps1` performs the repository restore, Release build and package
contract evaluation. `eng\pack.ps1` creates development packages locally. Its
`-Release` mode fails until package, specification and source-state
qualification is complete. Neither command publishes anything.

For exact-version CI packages after a successful build, run
`python eng\package_build.py` or pass an explicit `--version`. Commit version
changes first: NBGV reads the committed version, and an explicit package
version must agree with `version.json`. The `package-build` workflow uploads
all eleven `0.1.0-alpha` package/symbol pairs and their source/hash inventory;
those artifacts are not automatically promoted. Publication is a separate,
approval-gated operation described in the
[release guide](docs/releasing.md).

## 📚 Documentation

See the [documentation index](docs/README.md) for package areas, samples,
current behavior and limitations, evidence, security, release safeguards,
architecture decisions, the roadmap, and historical change records.
