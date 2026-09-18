# Interoperability evidence

The peer is pinned in `interop/upstream-lock.json`, including its exact image
digest, source commit and expected executable versions. `eng/qualify-upstream.ps1`
starts a new loopback-only fixture with fresh database storage, checks API
readiness and identity, installs a deterministic model, verifies exact binary
bytes and Version metadata, and invokes the independent `xr` checker.

The checked fixture must execute exactly its six named suites with 101 passes
and zero failures, warnings or skips. `eng/check_upstream_output.py` rejects
truncated/multiple summaries, missing/duplicate suites, mismatching counts and
hidden failed results. Checker accounting is not full specification coverage.

```powershell
pwsh -NoProfile -File eng\qualify-upstream.ps1
dotnet publish interop\XRegistry.InteropProbe\XRegistry.InteropProbe.csproj -c Release -r win-x64
pwsh -NoProfile -File eng\qualify-upstream.ps1 -NativeClient interop\XRegistry.InteropProbe\bin\Release\net10.0\win-x64\publish\XRegistry.InteropProbe.exe
```

The actual native client probe exercises Registry root/capabilities, model
installation, Version creation, byte-exact explicit/default Document reads,
metadata and an unknown-path rejection. It refuses JIT execution.
The current probe is a first contract slice, not a complete client qualification
or reverse-peer test.

The client slice has also executed as a Linux x64 native binary in a
runtime-deps container, without a .NET SDK/runtime installation. The harness
can accept `-NativeClientImage`; it pins the local image ID and shares only the
disposable peer's network namespace, so the test endpoint remains loopback
instead of weakening the production client's HTTP origin policy.

## Recorded peer limitations

The pinned Go server returns **405** for HEAD on a known document Version.
The baseline HTTP-binding text does not define a HEAD operation, so this is
recorded as a peer applicability limitation rather than an established spec
violation. The probe executes the request and asserts the exact pinned outcome;
it does not silently skip it or award HEAD conformance. See
`interop/peer-features.json`. Independently test the intended .NET host's HEAD
behavior; a changed peer outcome needs review.

The peer does not provide an interchangeable oracle for every pagination,
version-ordering or working-draft feature. Its Go test setup starts its own
server/database and cannot be retargeted merely through `XR_SERVER`.
`xr conform` is not an exhaustive CRUD checker.

## Current evidence gaps

The reverse `xr` client has executed native .NET import/get/default/empty/
metadata/restart operations (17 checks). Its unmodified conformance checker
executes but rejects the specification-permitted capabilities `mutable` field:
30 pass, 10 fail. This is retained as a peer compatibility blocker, not filtered
into a green result; see [reverse interoperability](reverse-interoperability.md).

Hosted forward peer and managed-Git reference lanes have since run successfully,
and `0.1.0-alpha` package publication to GitHub Packages and NuGet.org has been
performed. That publication does not qualify interoperability by itself. The
full broader bridge/authorization interop, reverse checker compatibility, and
clean-package peer qualification are not complete; they are tracked in the
[roadmap](roadmap.md#interoperability). Local Docker availability remains a
local rerun constraint only, not the current hosted execution blocker.

Runtime artifacts are retained under `artifacts/interop` per run. Cleanup targets
only each explicitly created container/volume; no broad Docker pruning or shared
database reset is used. A failure is preserved even if additional cleanup fails.
