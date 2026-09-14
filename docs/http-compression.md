# Bounded HTTP response content decoding

`XRegistryHttpClient` defaults to `Accept-Encoding: identity` and rejects
non-identity response codings. Opt in explicitly:

```csharp
using var client = new XRegistryHttpClient(
    new Uri("https://registry.example/xreg/"),
    new XRegistryHttpClientOptions
    {
        EnableContentDecoding = true,
        MaxEncodedResponseBytes = 8 * 1024 * 1024,
        MaxMetadataBytes = 16 * 1024 * 1024,
        MaxDocumentBytes = 64 * 1024 * 1024
    });
using var response = await client.SendAsync(HttpMethod.Get, "model");
using var model = await response.ReadMetadataAsync();
```

Opt-in requests advertise exactly `gzip, deflate, br`. The same negotiation
applies to ordinary requests, streaming uploads, discovery, opaque link GETs
(`GetLinkAsync`) and every collection page. Identity responses remain allowed.
This only decodes HTTP `Content-Encoding`; it does not decompress a Document
merely because its media type or filename denotes a compressed file.

## Limits and ownership

| Option | Default | Applies to |
| --- | --- | --- |
| `EnableContentDecoding` | `false` | Negotiation and permission to decode responses |
| `MaxEncodedResponseBytes` | 1 GiB | Actual encoded body bytes and, separately, each intermediate representation in a coding stack |
| `MaxMetadataBytes` | 16 MiB | Final decoded metadata bytes (also retains its existing request-body role) |
| `MaxDocumentBytes` | 1 GiB | Final decoded Document bytes (also retains its existing upload role) |
| `MaxContentCodingDepth` | 4 | All Content-Encoding tokens, including identity; configurable from 1 through 16 |
| `MaxGzipMembers` | 128 | Cumulative members across every gzip layer, including empty members; 1 through 4096 |
| `MaxGzipHeaderBytes` | 16 KiB | Cumulative gzip headers across all members and layers, including optional fields and header CRCs; 10 bytes through 1 MiB |

Content-Encoding fields additionally have a fixed cumulative 1024-character
limit. Empty list entries, non-token syntax and unsupported codings fail
explicitly. Decoding uses a snapshot of the received coding fields, so merely
inspecting typed content headers cannot normalize away invalid syntax or evade
their limits. Coding stacks are decoded in reverse application order. There is
no fallback to identity, retries or automatic handler decompression.

`Content-Length` is an encoded-byte precheck, not a decoded length and not a
substitute for counting actual reads. Each limit permits the exact boundary;
a bounded one-byte probe detects excess without writing it to the destination.
Compressed input, intermediate representations and decoded output are streamed,
not buffered in full. Each decoding layer has a 16 KiB pooled input buffer,
in addition to the compression primitive's bounded state/window. Inflaters may
decode ahead into their bounded internal window. Metadata is still buffered
only within its decoded metadata limit before JSON parsing.

`RegistryPaginationOptions.MaxTotalBytes` continues to count **decoded** bytes
across pages. Each page is read within the remaining traversal budget, not
merely checked after allocation. Encoded limits are per response/layer; the
existing page count and traversal deadline also bound total traversal work.

`CopyDocumentToAsync` writes exact decoded bytes and never disposes the
caller-owned destination. `BodyBytesRead` counts only successfully completed
decoded writes, including writes before a later framing error. There is still
exactly one body consumption attempt. A failed copy may leave partial bytes:
stage a download and publish it only after the copy completes successfully.
Metadata is not returned until the entire coding stack has completed and
validated. Returned JSON documents retain their independent lifetime.

The original request cancellation and deadline remain active during network
reads, decoding and destination writes, alongside cancellation passed to the
body method. Response disposal cancels active consumption. Network reads are
genuinely asynchronous and receive the linked cancellation token.

## Framing and compatibility

Gzip uses owned RFC 1952 framing with SharpZipLib 1.4.2's incremental raw
`Inflater` and checksum primitives, **not** `GZipStream` or
`GZipInputStream`. Every member needs a valid complete header, deflate end,
CRC32 and ISIZE trailer. Optional extra/name/comment fields are bounded, and
FHCRC is checked as the little-endian low 16 bits of the header CRC32.
Concatenated empty and nonempty members are supported. Invalid or incomplete
later members and all non-member trailing bytes (including zero padding) fail.

HTTP `deflate` means one RFC 1950 **zlib-wrapped** deflate stream, with header
method/window/check bits and the final Adler32 checked. Raw deflate, preset
dictionaries, concatenated zlib streams and trailing bytes are not supported.
Brotli uses the BCL incremental `BrotliDecoder`, requires `OperationStatus.Done`
and consumes exactly one complete stream with no trailing bytes. Brotli has
no gzip-style payload checksum; structural validation is not authentication
or a guarantee that every bit change is detectable.

Status, response headers and content headers remain unchanged, including
encoded `Content-Length` and `Content-Encoding`. A compressed Registry error
is still that error status, not a successful HTTP operation.

No process-wide `AppContext` switches are changed.
`RegistryHttpConnectionPolicy` remains a raw transport with automatic
decompression disabled, including for Git and OCI callers. This document
describes the opt-in response layer rather than changing those transports or
claiming complete HTTP conformance.

## Fixture provenance

`RegistryContentDecodingTests` contains fixed, independently generated bytes
from CPython 3.13.15, zlib 1.3.1 and Python Brotli 1.2.0. Expected metadata,
binary Documents and repeated-byte payloads are specified separately from
those fixtures. The tests do not use the production decoder to obtain expected
bytes or a .NET encoder to generate compressed input.

Canonical fixtures use `zlib.compressobj(9, zlib.DEFLATED, 31)` for gzip,
`zlib.compress(data, 9)` for zlib and `brotli.compress(data, quality=5)` for
Brotli. Stack encoders are applied left to right. Streaming fixtures flush after
32768 ASCII `A` bytes (`Z_SYNC_FLUSH` or Brotli `flush()`), then finish after
32768 ASCII `B` bytes. Expansion fixtures encode 1048576 ASCII `A` bytes.
Unfinished paging fixtures flush after `{"b":{"value":"` followed by 32768
ASCII `A` bytes, without closing the JSON or the coding stream. They require
the remaining paging byte budget to stop decoding before EOF, rather than
relying on a post-read size check.
The optional gzip header contains FTEXT, FEXTRA (`XY`, two data bytes), FNAME
(`fixture.bin`), FCOMMENT (`independent`) and a little-endian header CRC16.

Large-input fixtures contain 20000 ASCII `A` and 20000 ASCII `B` bytes. Gzip
and zlib use level-0 stored blocks. The Brotli literal block was obtained by
compressing `random.Random(123).randbytes(40000)` at quality 5, replacing only
its uncompressed payload, and independently checking the result with Python
Brotli. Its framing bytes are fixed in the test. These fixtures exercise
encoded and intermediate limits across multiple input buffers, in addition
to the highly compressible expansion cases.

## Dependency assets and verification

The Client references SharpZipLib 1.4.2 through central package management.
Both target frameworks select its managed
`lib\net6.0\ICSharpCode.SharpZipLib.dll` asset; there is no separate native
SharpZipLib package. The Client and HTTP test lock files include this
dependency. Brotli remains the framework-provided implementation.

The bounded workstream was executed with SDK 10.0.401 on Windows x64:

| Execution | net8.0 | net10.0 |
| --- | --- | --- |
| Focused content-decoding cases | 164 passed | 164 passed |
| Full HTTP test project, managed | 315 passed | 315 passed |
| Full HTTP test project, win-x64 Native AOT executable | 315 passed | 315 passed |

All these runs had zero failures and zero skipped cases, with strict zero-test
policy and minimum counts of 164 or 315 respectively. The 315-case project
includes the 151 existing HTTP cases, without running the full solution.
Representative commands, run from the repository root:

```powershell
$project = '.\tests\XRegistry.Http.Tests\XRegistry.Http.Tests.csproj'
dotnet test --project $project --configuration Release --treenode-filter '/*/*/RegistryContentDecodingTests/*' --minimum-expected-tests 164 --zero-tests-policy strict --timeout 3m --no-ansi
dotnet test --project $project --configuration Release --no-build --minimum-expected-tests 315 --zero-tests-policy strict --timeout 3m --no-ansi

$env:PATH = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer;' + $env:PATH
foreach ($framework in @('net8.0', 'net10.0')) {
    $output = ".\artifacts\http-content-decoding\win-x64\$framework"
    $log = ".\artifacts\http-content-decoding-$framework.log"
    dotnet publish $project -c Release -f $framework -r win-x64 -p:PublishAot=true -p:TrimmerSingleWarn=false -p:IlcTreatWarningsAsErrors=true -o $output --nologo -v minimal 2>&1 | Tee-Object -FilePath $log
    if ($LASTEXITCODE -ne 0) { throw "Native publish failed for $framework." }
    if (Select-String -LiteralPath $log -Pattern '\bwarning IL\d+\b' -Quiet) {
        throw "Native warning qualification failed for $framework."
    }
    if (!(Select-String -LiteralPath $log -SimpleMatch 'Generating native code' -Quiet)) {
        throw "Inconclusive warning qualification: ILC did not execute for $framework."
    }
    & "$output\XRegistry.Http.Tests.exe" --minimum-expected-tests 315 --zero-tests-policy strict --timeout 3m --no-ansi
    if ($LASTEXITCODE -ne 0) { throw "Native tests failed for $framework." }
}
```

The **net8.0 HTTP test-project warning gate remains red**. The command above
intentionally stops at that failure with the current dependency graph. The
315-case executable was also run separately and passed; functional success
does not make its native publish warning-clean. The net10.0 publish passed the
explicit log gate and its executable passed all 315 cases.

With `TrimmerSingleWarn=false`, the net8.0 diagnostics are IL2026 and IL3050,
both from `Microsoft.AspNetCore.Http.Json.JsonOptions.CreateDefaultTypeResolver`.
IL3053 was the earlier assembly-level summary. The native compiler was observed
to exit **0 despite these warnings**, even with
`IlcTreatWarningsAsErrors=true`. Qualification therefore checks the log, not
just the exit code. It also requires ILC to have actually run; an incremental
publish that merely reuses native output is not a fresh warning qualification.
No decoder or SharpZipLib trimming/AOT warnings were observed.

The navigation follow-up extended `EveryRequestSurfaceUsesMatchingContentNegotiation`
to cover `GetLinkAsync`, including both identity and compression-opt-in modes,
the exact opaque request URI, unchanged error status/ETag, and decoded
`CopyDocumentToAsync` bytes. Both managed TFMs and both win-x64 Native AOT
executables passed the two selected cases with zero failures/skips. The
follow-up used `--treenode-filter '/*/*/RegistryContentDecodingTests/EveryRequestSurfaceUsesMatchingContentNegotiation*'`,
`--minimum-expected-tests 2` and `--zero-tests-policy strict`; the net8.0
framework publishing warnings above remain unchanged.

### Isolated net8.0 framework/dependency limitation

Standalone net8.0 probes used SDK 10.0.401, ASP.NET Core/runtime/ILC 8.0.31,
`CreateSlimBuilder`, real loopback Kestrel and the same explicit IL-warning
gate. They were built before considering test endpoint rewrites:

| Probe | Native warning gate |
| --- | --- |
| Framework JSON, raw `RequestDelegate` endpoint | Clean; native response assertions passed |
| Framework JSON, generic `IResult` endpoint, Web SDK or base SDK | Clean; native response assertions passed |
| TUnit 1.66.27, generic endpoint | IL2026 + IL3050 |
| TUnit 1.66.27, explicit `RequestDelegate` executing `IResult` | IL2026 + IL3050 |
| TUnit 1.66.27, raw byte-writing `RequestDelegate`, no `IResult` | IL2026 + IL3050; native response/runtime assertions still passed |
| No TUnit, only System.Text.Json 10.0.10 added to the clean generic probe | IL2026 + IL3050 |
| Same no-TUnit probe with System.Text.Json 10.0.12 | IL2026 + IL3050 |

This isolates the warning to the net8.0 framework/compiler plus the packaged
System.Text.Json 10.0.x combination, not HTTP content decoding or generic
endpoint mapping by itself. The HTTP test graph brings System.Text.Json
10.0.10 through Microsoft.Extensions.DependencyModel 10.0.10 and
Microsoft.Testing.Extensions.CodeCoverage 18.10.0. The failing native test
probe confirmed `JsonSerializer.IsReflectionEnabledByDefault == false` and
`RuntimeFeature.IsDynamicCodeSupported == false` at execution time.

Inspection of the actual assemblies found that the net8.0 framework
System.Text.Json `ILLink.Substitutions.xml` explicitly substitutes
`get_IsReflectionEnabledByDefault()` with `false` for the disabled feature.
The 10.0.10 package's substitution resource has no such getter substitution;
the property instead carries feature-switch metadata. In this combination,
net8.0 ILC does not eliminate the guarded ASP.NET Core resolver path before
reporting the reflection/AOT diagnostics. Replacing test routes with raw
delegates did not fix it, and updating just the JSON patch did not fix it.

The investigation left Client and HTTP test source, endpoints, assertions and
dependencies unchanged. No warning suppressions, reflection enabling,
`DynamicDependency` roots, linker-substitution workaround or dependency
downgrade was introduced. The full 315 HTTP cases were re-run on each managed
TFM and each native executable: zero failures and zero skips. The net8.0
warning qualification is an explicit remaining blocker, not a passing gate.

### Separate package-consumer qualification

A separate raw-host application restored the locally packed
`XRegistry.Client` and `XRegistry` development packages (`0.1.0-alpha-g`) and
SharpZipLib 1.4.2, with **no project references, TUnit or packaged
System.Text.Json**. It uses the net8.0 framework JSON implementation. A short,
isolated NuGet cache prevented reuse of ambient development packages; the
package, built and restored net8.0 DLL SHA-256 hashes were checked for equality.

This package consumer passed the same explicit warning gate with ILC actually
executing, then passed **31 native checks** against real Kestrel. These cover
gzip/zlib/Brotli Documents and metadata, two coding stacks, unchanged error
status/validators/content headers, exact encoded and decoded limits and their
plus-one failures, corrupt/truncated/trailing input, and bounded empty/nonempty
gzip members. It asserts native execution and disabled JSON reflection.

This is a **warning-clean net8.0 win-x64 package-consumer result**, not a waiver
or substitute for the still-failing HTTP test-project warning gate. Probe
sources, `Check-NativeWarnings.ps1`, local packages, native executables and
logs are retained under the session's
`files\http-content-decoding\aot-warning-investigation` directory.

Native execution on other operating systems or architectures has not been
verified by this workstream. These results do not qualify the overall library
or the HTTP binding for release.
