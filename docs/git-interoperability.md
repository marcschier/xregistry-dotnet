# Managed Git smart-HTTP interoperability

**Qualified local slice: NativeAOT win-x64 consumers of both net8.0 and net10.0
against reference Git 2.55.0.windows.5.** Each framework executed 26 cases and
56 real smart-HTTP exchanges, plus a JIT negative control rejected before any
HTTP request. This is Git acquisition/object-tree interoperability, **not full
xRegistry server interoperability, directory-mapping conformance, or release
qualification**. Package statuses and specification/native matrices are unchanged.

## Reproduce the controlled experiment

From the repository root, with the SDK selected by `global.json`, installed
reference Git, Python 3.13, PowerShell and the matching native compiler:

```powershell
python -m unittest discover -s tests\Tooling -p 'test_git_interop*.py' -v
pwsh -NoProfile -File eng\test-git-interop.ps1 -RuntimeIdentifier win-x64 -Framework net10.0
pwsh -NoProfile -File eng\test-git-interop.ps1 -RuntimeIdentifier win-x64 -Framework net8.0
```

The tooling suite has 23 tests. Each native command publishes only
`tests\XRegistry.Git.InteropProbe` and its library dependencies, then executes
the actual reference experiment. It does not build the solution or create a
sample. The probe is a nonpackable console targeting both library frameworks;
the existing three .NET 10 principal samples remain the only samples. The
parent workstream may register this probe in the solution separately.

On Windows the wrapper temporarily adds the Visual Studio Installer directory
so `vswhere` can locate native tools. It does not invoke `vcvars64`, restart
Docker Desktop, use a container, modify Git configuration, or alter a remote.
All Git commits, refs and annotated tags are confined to newly created temporary
reference repositories, which are removed when the experiment exits.

The probe uses normal lockfile-free NuGet restore with central package versions,
audit, and source mapping. Restore/build outputs and the package cache are
isolated per run; the wrapper removes only its exact owned scratch directory.
It does not change root properties, dependency manifests, or the solution.

For a previously published probe, the driver can be invoked directly:

```powershell
python eng\verify_git_http.py `
    --probe artifacts\git-interop\native\win-x64-net10.0\XRegistry.Git.InteropProbe.exe `
    --managed-control tests\XRegistry.Git.InteropProbe\bin\Release\net10.0\win-x64\XRegistry.Git.InteropProbe.dll `
    --framework net10.0 --rid win-x64
```

The managed DLL is only a **negative control**. The driver starts it with
`dotnet` against the same controlled service, requiring exit 2, the specific
native-only rejection message, no success JSON, and zero HTTP requests. Its
runtime configuration must contain the native-publish setting that disables
dynamic code. That setting alone is not native evidence: under CoreCLR it can
make both `RuntimeFeature` dynamic-code flags false. The probe therefore also
requires `JitInfo.GetCompiledMethodCount() == 0`, records that count in every
native result, and rejects process/OS architecture mismatches. No AOT/trim
warnings are suppressed to implement this check.

## Independent oracle and exact cases

The Python driver resolves installed Git and its `git-http-backend` executable
to absolute paths, records their version/hashes, and invokes them without a
shell. A loopback-only `ThreadingHTTPServer` supplies explicit CGI variables,
including `GIT_PROJECT_ROOT`, `GIT_HTTP_EXPORT_ALL`, `PATH_INFO`,
`REQUEST_METHOD` and `HTTP_GIT_PROTOCOL`. Only the controlled upload-pack routes
are allowed; receive-pack, arbitrary paths, redirects and other services fail.

Each run creates one bare repository with `--object-format=sha1` and one with
`--object-format=sha256`. Reference Git itself creates and reads the objects
using `hash-object`, `mktree`, `commit-tree`, `update-ref`, `tag`, `rev-parse` and
`cat-file`. Expected OIDs, tag peeling, tree IDs and document bytes are never
obtained from the .NET implementation. Root SHA-256 commitments are computed
independently by Python from the reference Git bytes.

Each repository has exactly two commits: `refs/heads/history` retains the first,
`refs/heads/main` and HEAD select the second, and `refs/tags/historical` is an
annotated tag targeting the first. Both commits have different
`xregistry/registry.json` and `xregistry/nested/raw.bin` bytes. Decoy documents at
the repository root ensure that the default `xregistry` root is not confused
with an empty root. The binary literals are:

| Revision | Exact binary hex |
| --- | --- |
| Historical | `00FF0D0A41007F8062696E6172790A` |
| Head | `00FF0D0A42007F806E65772D686561640A` |

The native program calls only `GitSmartHttpClient.FetchAsync` and the returned
`GitSnapshot` public object/tree APIs, plus BCL runtime/JSON APIs. It returns
selected OID, peeled commit, root tree, both blob OIDs and exact bytes. It does
not invoke Git, a shell, hooks, filters, libgit2, or a separate checkout.
The driver runs it in an isolated directory with an empty executable PATH and
checks that Git cannot be resolved there. Git's CGI process has a separate,
explicit oracle-only executable environment.

For each framework the full matrix is:

| Object format | Actual protocol | Successful snapshot cases | Expected rejections | Total |
| --- | --- | ---: | ---: | ---: |
| SHA-1 | v2 | 4 | 3 | 7 |
| SHA-1 | forced v0 | 4 | 3 | 7 |
| SHA-256 | v2 | 4 | 2 | 6 |
| SHA-256 | forced v0 | 4 | 2 | 6 |

The four successes select the exact head ref, historical ref, annotated tag,
and complete historical commit OID. The historical OID is advertised through
the historical ref; this does not claim arbitrary unadvertised-OID support.
Every success compares the independently read commit/tree/blob IDs and bytes.
The tag cases also compare the selected tag-object ID before peeling.

All cells require `PathNotFound` for `refs/heads/absent`, with no fetch or HEAD
fallback, and `IntegrityMismatch` for an incorrect trusted root. SHA-1 additionally
requires `PolicyDenied` when no trusted root is supplied, before any ls-refs or
fetch POST. SHA-256 success cases do not supply a trust override.

The native client always requests `Git-Protocol: version=2`. For forced legacy
cases the server deliberately omits that CGI variable. The driver parses the
**actual reference advertisement** to prove v2 versus v0 and the object format.
It records the actual ls-refs prefix and fetch `want` OID and checks the exact
HTTP operation sequence. Counts must be exactly 26 advertisements, 10 ls-refs
POSTs and 20 fetch POSTs. Pack bytes are independently demultiplexed and checked
for the Git pack header, object count and SHA-1/SHA-256 trailer; the .NET library
independently performs its normal full object/pack verification.

## Isolation, bounds and fail-closed evidence

The reference environment uses an empty HOME/global config/template/hooks
directory, `GIT_CONFIG_NOSYSTEM=1`, disabled system attributes, explicit neutral
author/committer identities and fixed timestamps. Signing, autocrlf, credential
helpers and automatic GC are explicitly disabled. Ambient Git variables,
proxies, SSH agents, preload variables and .NET startup hooks are not inherited
by either child environment.

The driver bounds the experiment to 300 seconds and 256 child processes, with
15-second Git/CGI and 30-second native/JIT child deadlines. The native API has
its own 20-second fetch deadline. Readiness is bounded to five seconds,
connections to four concurrent handlers, socket reads to five seconds,
requests to 64, request bodies to 16 KiB, responses to 4 MiB, and native JSON
to 32 KiB. Child output is spooled and size-checked rather than read unbounded
into memory. Windows children start suspended, enter a private job object, then
resume; cleanup terminates only that job. Unix timeout cleanup targets the
new child's own process group. Server threads are shut down and joined before
temporary repositories are removed.

Native snapshot successes require exit 0; intended Git rejections require exit
3 and the exact failure category without a snapshot. Arbitrary error exits,
stderr, empty/partial/duplicate-key JSON, wrong native identity/bytes, missing
cases, unexpected requests, failed CGI, unproven protocols and fallback wants
cannot become a passing result. The JIT control requires its distinct exit 2
and rejection message. Only after all checks, unchanged executable/driver
hashes, and fixture cleanup does the driver write `status: passed`.

Each execution creates a new uniquely named directory under
`artifacts\git-interop\runs`. `evidence.json` retains the actual Git/CGI/native
hashes, driver hash, reference snapshots, independent root commitments, raw
native JSON and stdout hashes, protocol/CGI/pack evidence, JIT-control evidence,
timestamps, exact totals and cleanup result. Failures retain a `failed` report
without passing totals; an old successful report is never reused.
Synthetic inputs in `test_git_interop.py` qualify **harness accounting only**,
not Git behavior or native execution.

## Executed local evidence

The final Windows runs completed on 2026-09-11 UTC using SDK 10.0.401 and
reference `git version 2.55.0.windows.5`:

| Target | Cases / HTTP exchanges | Native executable SHA-256 |
| --- | --- | --- |
| net10.0 / win-x64 | 26 / 56 | `a08a5fa97177cba0a4b7db97332334d10b21cf493edeec116ad0b0fd923cebbd` |
| net8.0 / win-x64 | 26 / 56 | `a87a7e10e45c3e2fa9e3a02a1c07ed8f2d17a55e45bb89c207ae0253f9bd1861` |

The reports are in these directories beneath `artifacts\git-interop\runs`:

```text
win-x64-net10.0-d328df5a11c24996aef38f435ead8a24\evidence.json
win-x64-net8.0-338a15695f8d44359acc4bf3f7f8bcea\evidence.json
```

Both additionally rejected the actual JIT control with zero HTTP requests.
Their shared driver SHA-256 is
`53492d95c20b72a29c0b7417c81dab8c0a317c982083d80169a9a817268ffac2`.
The reference Git executable SHA-256 is
`78211c7ed73988da93a6d8a33d47ec6187f464d7ea2a9a00c182bbd7a1ecf30f`;
the CGI executable SHA-256 is
`1e4878427da3e77f1d3b8b85ee6d0bebc5c4d754763bc902e059449f69f38351`.
These identify the actually used installed oracle, not a newly configured
remote or a signed release provenance claim.

## CI and current limits

The isolated `native-git-to-reference` job in `.github\workflows\interop.yml`
runs Linux x64 for net8.0 and net10.0 with installed Git, clang and zlib.
It uses the existing immutable checkout/setup/artifact pins, read-only contents
permission, no persisted checkout credentials, no Docker and no dependency on
the pinned xRegistry peer job. It uploads the real evidence and tested native
executable even when a later check fails.

**Linux execution has not been qualified locally or dispatched.** Linux x64 CI
and both ARM64 RIDs are not covered. This fixture does not qualify TLS,
authentication, public Git hosts, forced protocol v1, large repositories or
delta-stress behavior, SHA1DC/signatures/authorship, directory-mapping/federation
capture, or xRegistry server behavior. It does not create specification-ledger
native receipts or change package/sample qualification statuses. No production
Git bug was found, so no production Git source change or unrelated 107-test
suite rerun was needed. Outstanding interoperability work is listed in the
[roadmap](roadmap.md#interoperability).

Official contracts used:

- [Git HTTP backend and CGI variables](https://git-scm.com/docs/git-http-backend)
- [Git protocol v2 and multiplexed packfile transport](https://git-scm.com/docs/protocol-v2)
- [JIT compilation counter API](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.jitinfo.getcompiledmethodcount)
- [NativeAOT JitInfo implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/coreclr/nativeaot/System.Private.CoreLib/src/System/Runtime/JitInfo.NativeAot.cs)
- [MSBuild common-props extension point](https://github.com/dotnet/msbuild/blob/main/src/Tasks/Microsoft.Common.props)
