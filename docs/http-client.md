# HTTP client foundation

`XRegistryHttpClient` sends one complete request to a configured Registry root.
Paths remain model-defined, so a custom Registry does not need generated C#
entity types. Ordered/repeated query values, null flags, empty query values,
present empty bodies, exact Document bytes, error statuses and response headers
remain distinguishable.

```csharp
using XRegistry.Client;

using var client = new XRegistryHttpClient(new Uri("https://registry.example/xreg/"));
using var response = await client.SendAsync(HttpMethod.Get, "model", cancellationToken: cancellation);
using var model = await response.ReadMetadataAsync(cancellation);
```

The caller disposes the client, each response and each returned `JsonDocument`.
Read each response body once. Metadata has its own finite byte/depth limit;
Document copying streams through a pooled bounded buffer. A failed copy can
leave partial destination bytes, so download files through staging when atomic
local publication is needed.

The transport defaults to identity encoding and rejects unexpected compressed
bodies. Set `EnableContentDecoding` to negotiate strictly framed gzip,
zlib-wrapped deflate and Brotli with encoded, decoded and coding-work bounds.
See [HTTP compression](http-compression.md) for framing rules and qualification
limits. Buffered request bodies and single-use streaming uploads are bounded;
`SendDocumentAsync` leaves the caller's stream open and never replays it.
Model-aware Document headers support the representable subset described below;
other metadata still requires the metadata-body representation. Complete HTTP
conformance is not claimed.
No project is marked release-qualified by this foundation.

## Discovery and collection pages

`DiscoverAsync()` reads `.xregistry` relative to the configured Registry root.
`DiscoverAsync(RegistryDiscoveryLocation.Host)` explicitly reads the hosting
origin's `/.well-known/xregistry` instead. These are distinct mechanisms in the
core specification; the host path is not mounted under a Registry prefix.
Unavailable or malformed discovery is an error, not an empty result or an
automatic fallback. Advertised absolute URLs remain ordered, opaque data:
discovery does not fetch or authorize them. The credential provider receives
the actual selected discovery URI and can reject host-wide credential use.

`ReadCollectionPagesAsync` yields independently owned `RegistryCollectionPage`
objects. Their `Records` JSON is usable after response/client disposal.

```csharp
await foreach (var page in client.ReadCollectionPagesAsync(
    "schemagroups", query: [new("limit", "100")], cancellationToken: cancellation))
{
    foreach (var record in page.Records.EnumerateObject())
    {
        Console.WriteLine(record.Name);
    }
}
```

Only the initial request receives those query values. Subsequent `next` links
retain the exact escaped path and query, including percent-escape spelling,
ordered/repeated flags and empty values. RFC relative-path resolution occurs
without percent-decoding opaque cursors. Every target must stay within the
configured origin and Registry root before credentials or dispatch.

Pages must be HTTP 200 JSON objects keyed by record IDs. Duplicate IDs, cycles,
ambiguous next links, invalid/changing counts, violated limits and incomplete
advertised totals fail explicitly. `RegistryPaginationOptions` bounds total
pages, records, decoded bytes and elapsed time. Per-response limits still apply.
Anchored pagination links require a different context and are explicitly
unsupported by this helper. Redirects/errors are not retried. Earlier yielded
pages remain partial results if a later page fails; enumeration is not an atomic
remote snapshot guarantee.

## Credentials and network isolation

`XRegistryHttpClientOptions.AuthorizationProvider` is an explicit host callback
for this validated origin. It runs within the request deadline; it is not
derived from inbound arbitrary headers. The client never automatically retries
mutations or authentication challenges and does not follow redirects.

`RegistryHttpConnectionPolicy` creates an owned HTTP client with cookies,
automatic redirects, ambient credentials and proxies disabled. A delegating
handler checks each request's origin and rejects Host overrides. Its connection
callback validates DNS answers and connects to the validated address itself,
avoiding a separate unvalidated second resolution.

Default HTTPS connections reject private, loopback, link-local, multicast,
documentation and selected transition/special-purpose addresses. Explicit
private-origin permission is limited to the configured HTTPS host; it does not
authorize metadata-service or loopback destinations. Explicit local HTTP mode
accepts only literal loopback/localhost origins and loopback connection
addresses. Different service origins, including OCI token services, require
separate authorization; a redirect is not permission.

This is a conservative application egress policy, not a replacement for network
egress controls or a claim that every future IANA address allocation is known.
Normal TLS certificate/hostname validation remains enabled.

## Header strings

The shared `XRegistry.Http.RegistryHeaderEncoding` codec follows the pinned
HTTP attribute-value rules: UTF-8, canonical uppercase percent escapes, legacy
quoted-string unescaping, exactly one percent-decoding round, strict invalid
Unicode rejection, and separate encoded/decoded byte bounds.
Decoded CR/LF is attribute data; it must be re-encoded, not concatenated into a
raw HTTP header. Model-dependent scalar conversion remains a separate layer.

## Model-aware Document metadata

The additive `SendDocumentAsync` overload accepts `RegistryJson metadata` and
an explicit `RegistryResourceDefinition resource` after the borrowed stream.
It sends **one PUT or POST** to a matching Resource or Version Document path.
There is no preliminary model fetch, buffered Document copy, retry, or general
HTTP header override. The original overload with a separate `contentType`
argument remains available.

Given a compiled matching `registryModel` and caller-owned streams:

```csharp
using XRegistry;
using XRegistry.Client;

var resource = registryModel.Groups["teams"].Resources["files"];
var changes = RegistryJson.Parse("""
    {"name":"Example","contenttype":"application/octet-stream","epoch":0}
    """);

using var response = await client.SendDocumentAsync(
    HttpMethod.Put, "teams/g/files/f", source, changes, resource,
    cancellationToken: cancellation);

if (response.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.Created)
{
    RegistryJson headers = response.ReadHeaderMetadata(resource);
    var epoch = RegistryNumber.FromElement(headers.RootElement.GetProperty("epoch"));
    await response.CopyDocumentToAsync(destination, cancellation);
}
```

`ReadHeaderMetadata(resource)` is synchronous and does **not** consume the
response body. It can be called repeatedly before or after
`CopyDocumentToAsync`, until response disposal. Its returned `RegistryJson`
owns its values, needs no disposal, and remains usable after response/client
disposal. `ReadMetadataAsync` still consumes a **JSON body**, not headers.
Neither method changes an error status into success. Check the status and
representation before choosing how to read it; malformed metadata headers do
not prevent separately consuming the Document bytes.

Both APIs use `XRegistry.Http.RegistryHeaderMetadata`, extracted from the
actual ASP.NET Core `HeaderMetadata` implementation. ASP.NET Core is now a
thin adapter around this Core codec, retaining its existing standardized
errors and `name` arguments. Client references Core, not ASP.NET Core or Server.
Type lookup includes the Resource's direct Version/Resource attributes, active
`ifvalues` siblings (including nested conditions), and the active wildcard.
The conditional expansion loop is shared with `RegistryMetadataValidator`;
there is no second condition matcher or entity validator in the HTTP binding.
The public Core `Encode` and `Decode` methods require an explicit
`RegistryHeaderMetadataDirection`:

- `ClientInput` ignores ordinary read-only attributes, including invalid
  read-only values. `epoch`, `versionid`, and the Resource ID are not discarded:
  they retain the server's guard semantics. Header conversion is not complete
  entity/model validation; the server still validates and authorizes the write.
- `Response` preserves read-only metadata, including `xid`, `self`, `isdefault`,
  `epoch`, and collection counts. Ordinary HTTP fields and unmodeled response
  control fields `xRegistry-count`, `xRegistry-xregcorrelationid`, and
  `xRegistry-commit-outcome` remain available through `Headers` rather than
  becoming entity attributes.

### Conditional definitions (`ifvalues`)

All bounded raw fields are collected before looking up conditional types.
A discriminator can therefore appear before or after its siblings, including
at a deeper conditional level. Selection uses the existing metamodel's
case-insensitive comparison against the scalar's string serialization. Numeric
discriminators and conditional values retain exact JSON tokens. Legacy
unquoting and one percent-decoding pass occur before a header becomes a typed
discriminator.

Active named definitions take precedence over wildcards; a conditional wildcard
never overrides a named definition. An inactive sibling is not implicitly
declared, although an active wildcard can permit that name with the wildcard's
own type. Simultaneously active, conflicting sibling definitions fail with the
existing Core `invalid_attribute` diagnostic and attribute `name`. Unknown or
unavailable conditional header types fail with `header_error` and the actual
header `name`.

`Content-Type` can be a discriminator too. The ASP.NET adapter supplies it to
the shared codec when the model uses it in `ifvalues`; the engine still owns
the normal Document content-type processing. An absent request Content-Type
means clearing, not an unknown retained value, per frozen `core/http.md`
lines 434-441. Other omitted request discriminators are not inferred from
stored state or from model defaults. Read-only discriminator input is ignored,
not trusted to select writable siblings; returned read-only discriminator
values can select response types.

For a conditional write, supply the relevant **writable** discriminators and
their ancestors together with the siblings. Unchanged discriminator values may
be included (frozen `core/http.md`, lines 1602-1608). No implicit GET, model
prefetch, Document read, synthesized default, or second dispatch fills in the
context. Ordinary entity type, enumeration, required-value and authorization
checks remain with the existing validator/engine; case-insensitive condition
matching does not make an otherwise invalid enumeration value valid.

Header projections can legitimately be incomplete. For example, this Resource
`attributes` fragment admits both `{"kind":"counter","reading":42}` and
`{"kind":"other","reading":"42"}`:

```json
{
  "kind": {
    "type": "string",
    "ifvalues": {
      "counter": { "siblingattributes": { "reading": { "type": "integer" } } }
    }
  },
  "*": { "type": "string" }
}
```

Without `xRegistry-kind`, the raw field `xRegistry-reading: 42` does not say
which of those types applies. The decoder reports missing discriminator
context instead of silently using the wildcard. The same protection applies
when a missing second discriminator could introduce a conflicting definition.
Known named fields unaffected by the missing context remain readable.
`Encode(..., Response)` projects an already validated complete snapshot;
`Decode(..., Response)` cannot assume a partial header set is that snapshot.

This is a context limitation, not a new wire restriction or proof that the
model cannot be represented. Supplying the discriminator removes the ambiguity.
The frozen activation rule is `core/model.md#attributesstringifvalues`,
lines 457-489; scalar response headers are a projection under
`core/http.md`, lines 1573-1600. Two genuinely non-header cases remain:

- A string discriminator with the `ifvalues` key `"null"` and a sibling
  `reading` of type `integer` can have valid metadata
  `{"kind":"null","reading":42}`. It cannot be written as a Document header
  request: `core/http.md` lines 1603-1608 define `null` as deletion, and
  lines 3239-3247 require legacy unquoting followed by one percent-decoding
  pass. Neither `"null"` nor `%6Eull` escapes that meaning. The independent
  `ConditionalStringNullIsNotARequestDiscriminatorEscape` case proves the
  request/response distinction.
- A map of objects can place `kind.ifvalues` inside each item's `attributes`,
  admitting `{"entries":{"a":{"kind":"counter","reading":42}}}`. Its items
  are still objects, not scalars. Frozen `core/http.md` lines 1586-1600 allow
  member headers for scalar-valued maps and prohibit complex attributes.
  `ConditionalMapsWithObjectItemsStillRequireTheMetadataBody` validates that
  independent model/input through Core, then proves header rejection.

Both cases require the metadata-body representation; no spec/correction or
private header syntax is introduced.

### Representability and nulls

`contenttype` is carried only by the standard `Content-Type` field. Its media
type must be valid printable ASCII; it is not percent-encoded/decoded. In the
new overload, absent or null `contenttype` omits that header, which the frozen
HTTP binding defines as **clearing** the stored content type on an update.
There is no separate competing content-type argument.

Other absent attributes remain absent (unchanged on update). Empty strings
produce present empty field values. An explicit JSON null produces `null`,
requesting deletion, including deletion of a complex top-level attribute.
The request sentinel is case-sensitive and is interpreted **after** legacy
unquoting and one percent-decoding pass. Consequently the literal string
`"null"` cannot be written through these headers: quoting it or unnecessarily
escaping its letters does not disambiguate it. The client rejects it and
requires a `$details` metadata-body write; it never invents an escape convention
or silently requests deletion.

The frozen deletion rule applies to update requests. Response encoding omits
null attributes, and response decoding treats `null` as literal data for a
string-valued attribute (including the binding's `any` string fallback).
This allows a string `"null"` stored through `$details` to be read correctly
from Document response headers; it does not make that string representable
in a subsequent header-based write. Headers are a partial representation, not
a lossless substitute for every metadata JSON value.

Scalar-valued maps use `xRegistry-<attribute>.<member>` and are sent as **whole
map replacements**, not per-member patches. Dots after the first dot remain
part of the member name; names are not percent-decoded. Empty member names,
non-HTTP-token names (including otherwise legal Core map keys containing `:`),
and case-insensitive collisions fail rather than being renamed or combined.
An empty map cannot be distinguished from an omitted map through member
headers, so client encoding rejects `{}`; use the metadata-body representation
for that value, or null when deletion is intended.

Non-null arrays, objects, and complex-valued maps need a metadata-body request.
`any` headers decode as strings as specified by the binding; a non-string
client `any` value is rejected rather than silently changing its JSON type.
Numeric headers retain exact JSON number tokens, including values beyond
64 bits; use `RegistryNumber` or `JsonElement.GetRawText()` rather than a
fixed-width or floating-point conversion when the model permits such values.
The frozen header section refers to canonical scalar strings without defining
a separate numeric normalization algorithm. This codec retains the existing
server's exact JSON numeric spelling rather than adding a second normalization
scheme.

Inline `<resource>` and `<resource>base64` metadata are always rejected for
streamed requests, even when null. Metadata views (`$details`), collections,
Meta paths, documentless Resource models, mismatched Resource collection names,
and unsupported methods are rejected before credentials, stream reads or
dispatch. A non-null `<resource>url` requires an empty **seekable** stream so
the client can establish emptiness without reading; otherwise use a
metadata-body request. The client does not follow that URL.

There is no Authorization, Host, cookie, proxy, or arbitrary conditional-header
option. Model attributes with such names remain prefixed `xRegistry-` metadata,
not transport overrides. This slice uses metadata `epoch` guards; it does not
add `If-Match` or `If-None-Match` APIs.

### Header budgets and scope

`XRegistryHttpClientOptions.MaxHeaderBytes` defaults to 32 KiB and
`MaxHeaderCount` to 128. Bytes include each field name, value, and four bytes
for `": "` plus CRLF. Percent escapes count at their encoded ASCII size.
Each repeated field value counts separately; duplicates are not hidden by
comma joining or typed header normalization. Zero permits no counted fields.
Outgoing budgets cover the generated metadata fields, including Content-Type,
not transport-generated Host/Content-Length or separately supplied credentials.
Response decoding counts **all** supplied response/content fields, including
fields that are not metadata.

Metadata uses `MaxMetadataBytes` and `MaxJsonDepth`, plus the Core defaults of
100,000 JSON nodes, 1,024 characters per number token, and an absolute explicit
number exponent of 10,000. Direct Core callers can set these through
`RegistryHeaderMetadataOptions.Json`. The original Document byte limits,
ordered/repeated query handling, content negotiation, cancellation, borrowed
stream ownership, and complete request/response deadline remain in effect.
Header reading does not restart that deadline or advance `BodyBytesRead`.
For models with conditions, `MaxNodes` also bounds conditional-definition
visits/additions (including unresolved branch traversal), and `MaxDepth` bounds
conditional nesting. JSON byte/node/depth limits remain separately enforced.

The focused regression suites are `RegistryHeaderMetadataTests` in Core and
`RegistryDocumentHeaderTests` in Http. They include independent literal wire
expectations, aggregate boundaries and adjacent failures, actual Kestrel
`MapXRegistry` writes/reads and error contracts, and pre-dispatch/stream
ownership checks. These are bounded surface tests, not a claim of complete
binding conformance. The previously documented net8.0 TUnit/System.Text.Json
10.x native-publish warning limitation remains separate from Client runtime
AOT compatibility; see [HTTP compression](http-compression.md).

The initial header slice's managed validation used SDK 10.0.401 and passed the complete
affected projects on both frameworks, with zero failures or skipped tests:

| Project | net8.0 | net10.0 |
| --- | ---: | ---: |
| XRegistry.Core.Tests | 427 | 427 |
| XRegistry.Http.Tests | 373 | 373 |
| XRegistry.AspNetCore.Tests | 92 | 92 |

This includes 93 new Core cases, 58 new Http cases, and the existing 315-case
Http / 92-case ASP.NET baselines. Build artifacts, copied existing dependency
cache, temporary files and results were isolated under
`D:\artifacts\client-header-build`; dependencies were restored from that cache
with an empty local package source, not downloaded.

For example, with those artifacts already built, the executed full-suite
commands have this form (run once for each `net8.0` and `net10.0`):

```powershell
$artifacts = 'D:\artifacts\client-header-build'
$framework = 'net10.0' # Also executed with net8.0.
dotnet test --project tests\XRegistry.Core.Tests\XRegistry.Core.Tests.csproj -c Release -f $framework --no-build --no-restore --artifacts-path $artifacts --minimum-expected-tests 93 --zero-tests-policy strict --timeout 3m --no-ansi --results-directory "$artifacts\results\core-$framework"
dotnet test --project tests\XRegistry.Http.Tests\XRegistry.Http.Tests.csproj -c Release -f $framework --no-build --no-restore --artifacts-path $artifacts --minimum-expected-tests 373 --zero-tests-policy strict --timeout 3m --no-ansi --results-directory "$artifacts\results\http-$framework"
dotnet test --project tests\XRegistry.AspNetCore.Tests\XRegistry.AspNetCore.Tests.csproj -c Release -f $framework --no-build --no-restore --artifacts-path $artifacts --minimum-expected-tests 92 --zero-tests-policy strict --timeout 3m --no-ansi --results-directory "$artifacts\results\aspnetcore-$framework"
```

The optional native publish/run was not performed for the initial slice after shared
D-drive free space fell to approximately 8.8 GiB. Clean managed builds and
analyzer results are not being substituted for native execution evidence.

### Conditional-header follow-up verification

The follow-up adds **25 Core conditional cases and 13 Http conditional cases**
to the same two test files. Focused checks cover 118 Core header cases and the
13 new HTTP cases. Independently authored models/raw fields reproduced failures
before the shared lookup changes: unordered conditional scalars, conditional
map encoding in both directions, unsafe missing-discriminator wildcard fallback,
Content-Type context in the actual ASP.NET adapter, and omitted Content-Type's
clearing semantics. Red and green logs are retained under
`D:\artifacts\client-header-build\conditional\logs`.

After the changes, the complete affected projects passed with zero failures or
skips:

| Execution | net8.0 | net10.0 |
| --- | ---: | ---: |
| Core managed | 460 | 460 |
| Http managed | 386 | 386 |
| ASP.NET Core managed | 93 | 93 |
| Http win-x64 Native AOT | Not run | 386 |

Managed builds had zero warnings/errors. The net10 native publish performed a
fresh native compilation and passed an explicit log gate rejecting every
`warning IL...`; zero IL warnings were emitted. No net8 test-host framework
warnings were suppressed or downgraded, and no full binding-conformance claim
follows from these executions.

The native restore used only previously cached packages. Its build-scoped
`conditional\native.props` imports the unchanged repository `Directory.Build.props`
and redirects `NuGetLockFilePath` into `conditional\native-locks` using
`$(MSBuildProjectName)`. Source lock-file hashes were checked unchanged. No
dependency manifest, correction, parent event test, or Server event code was
changed by this follow-up.

The full managed test commands above were repeated with minimum counts
452 / 386 / 92 for Core / Http / ASP.NET respectively and results under
`conditional\results`. Native commands used the isolated props/cache:

```powershell
$root = 'D:\artifacts\client-header-build'
. "$root\conditional\environment.ps1"
$env:PATH = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer;' + $env:PATH
$project = 'tests\XRegistry.Http.Tests\XRegistry.Http.Tests.csproj'
dotnet publish $project -c Release -f net10.0 -r win-x64 --no-restore --artifacts-path "$root\conditional\native-build" -p:DirectoryBuildPropsPath="$root\conditional\native.props" -p:TargetFrameworks=net10.0 -p:PublishAot=true -p:TrimmerSingleWarn=false -p:IlcTreatWarningsAsErrors=true -p:UseSharedCompilation=false -m:1 -nr:false --nologo -v minimal -o "$root\conditional\native\win-x64"
& "$root\conditional\native\win-x64\XRegistry.Http.Tests.exe" --minimum-expected-tests 386 --zero-tests-policy strict --timeout 3m --no-ansi --results-directory "$root\conditional\results\native-net10-final-2"
```

The final evidence is `native-publish-final-2.log`, `native-run-final-2.log`,
and the six `*-full-net8.log` / `*-full-net10.log` managed logs in
`conditional\logs`. Native qualification here requires both the publish-log
checks (fresh compilation and no IL warnings) and the successful executable run.
