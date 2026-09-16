# FederationBridge

Start with the [developer getting-started guide](../../docs/getting-started.md)
for the client and single-Registry server paths. This sample covers the
separate federation host profile.

A real .NET 10 API host for an explicitly configured, read-only producer view and
separate single-authority core-HTTP write-through mounts. It is not replication,
a cross-registry transaction coordinator, or a complete federation-discovery
implementation. Core aliases and requested document/inline/collections/binary
views, shared Core filter/sort semantics and bounded frozen paging are supported.
See [the current contract and limits](../../docs/federation-bridge.md).

## Explicit loopback demo

Run from the repository root:

```powershell
$configuration = (Resolve-Path samples\XRegistry.FederationBridge\bridge.demo.json).Path
dotnet run --project samples\XRegistry.FederationBridge\XRegistry.FederationBridge.csproj `
    -c Release --artifacts-path artifacts\bridge-build --no-launch-profile `
    -- --config $configuration
```

The demo explicitly selects loopback HTTP port 5091. Every local caller receives
demo administrative access, and startup emits a warning. **Do not expose or
tunnel this listener.** Demo mode is never inferred from missing certificates or
tokens and cannot be combined with production credential settings.

The configuration reads the frozen File document-tree fixture in this repository;
it does not modify it or need a running upstream server. Its dynamic model has
document-bearing assets and metadata-only notes/catalog entries.

```powershell
curl.exe --fail http://127.0.0.1:5091/registry
curl.exe --fail http://127.0.0.1:5091/registry/documents/main/assets/item
curl.exe --fail http://127.0.0.1:5091/registry/documents/main/assets/item`$details
curl.exe --fail http://127.0.0.1:5091/registry/categories/main/registries/site
```

The document endpoint returns the literal fixture bytes `{"hello":"world"}` plus
a newline. The catalog entry is metadata-only and does not require `$details`.
The fixture also contains aliases: one source-local hop is resolved in API view.
Document view leaves aliases unresolved; querying their nonlocal Version hierarchy
with `?doc` returns the specific Core `cannot_doc_xref` error.
All aggregate mutations return 405. Unknown write-through mount names return 404.

Core view examples (quote query URLs in PowerShell):

```powershell
curl.exe --fail 'http://127.0.0.1:5091/registry?doc&inline=documents.assets.meta,documents.assets.versions.asset'
curl.exe --fail 'http://127.0.0.1:5091/registry/documents/main?collections&doc'
curl.exe --fail 'http://127.0.0.1:5091/registry/documents/main/assets/item/versions/v1?doc&inline=asset&binary'
```

`doc` emits response-local pointers and removes duplicated default-Version
attributes. `inline` uses Core model-relative dot paths, not entity IDs.
`binary` changes only an explicitly inlined Document's encoding.

Queries run through the shared Core evaluator **after** whole-Resource shadowing:

```powershell
curl.exe --fail 'http://127.0.0.1:5091/registry/documents/main/assets?filter=assetid%3Ditem&sort=versionid'
curl.exe --fail 'http://127.0.0.1:5091/registry/documents/main/assets?filter=assetid%3Ditem&limit=1&doc&inline=versions'
```

One `filter` value comma-ANDs expressions; repeated values OR branches. Follow
returned `cursor` links unchanged. Pages preserve the captured bytes and source
owners, recheck current read policy/credential context, and expire without
renewal. They do not reopen HTTP sources or treat root epoch as an immutable
snapshot. The sample configures explicit retained-read authorization; custom
hosts must do the same before paging is advertised.

## Production configuration

Copy `bridge.production.example.json` to an operator-owned file and replace the
example hosts, File directories and certificate path with authorized locations.
Supply a currently valid PFX/private key and explicitly named environment
variables for a strong administrative Bearer token and, optionally, a distinct
read-only token. Anonymous reads require explicit opt-in.

```powershell
$env:BRIDGE_ADMIN_TOKEN = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:BRIDGE_READER_TOKEN = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
# Provision the PFX/password and upstream credentials through your approved secret mechanism.
# Do not print, commit, or copy these tokens into JSON configuration.
```

The example selects ordered File, HTTP, Git and OCI read sources and one fixed
write-through mount. All sources must expose the configured model/import
structure. The example Git locator assumes a SHA-256 repository; SHA-1 requires
adding an independently authorized `trustedRegistryRootSha256` value. A File OCI
layout uses `"layout": "oci-layout"` plus an explicit `"reference"`.

Upstream credentials use separate named environment variables per source/mount.
They are never copied from the incoming request. Source credentials are captured
only after source policy approval and remain fixed for that request's session.
`adminOnly: true` restricts a read source to the shared host's administrator role.

There is no remote setup, certificate provisioning, CI dispatch, publishing or
permission configuration hidden in these commands.

## Native execution and checks

On Windows, make the Visual Studio Installer directory available for `vswhere`;
do not initialize a `vcvars64` shell:

```powershell
$env:PATH = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer') + ';' + $env:PATH
dotnet publish samples\XRegistry.FederationBridge\XRegistry.FederationBridge.csproj `
    -c Release -r win-x64 --artifacts-path artifacts\bridge-build `
    -o artifacts\bridge-build\native-host
pwsh -NoProfile -File samples\XRegistry.FederationBridge\verify-native.ps1
```

The sample also supports `--runtime-info` without configuration or a listener.
It reports dynamic-code flags, JIT compilation count and process/OS architecture.
Ordinary JIT development remains supported, but cannot qualify as native.

Before starting a listener the verifier checks those fields on the native
candidate and runs the actual managed assembly with `dotnet --runtime-info`
as a negative control. AOT runtime settings can disable dynamic-code flags even
under JIT; a positive JIT count must still disqualify it. The verifier then checks
six functional HTTP outcomes (including the now-supported document view),
records native/managed artifact hashes and logs, and stops only its own process.
Runtime-info output and process lifetime are bounded.

On Linux the executable has no `.exe` suffix; the verifier derives the actual
Windows/Linux x64/ARM64 RID. Explicit `-NativeHost` and `-ManagedHost` paths can
be supplied when CI uses a different output layout. Linux/ARM64 execution is not
claimed by the local Windows results.

The broader Kestrel suite includes source composition and real upstream mutation
fixtures:

```powershell
dotnet test --project tests\XRegistry.Bridge.Tests\XRegistry.Bridge.Tests.csproj `
    -c Release --artifacts-path artifacts\bridge-build `
    --minimum-expected-tests 139 --zero-tests-policy strict --timeout 3m --no-ansi
```
