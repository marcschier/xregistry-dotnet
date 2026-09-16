# Shared sample hosting security

The net10.0 source in `samples\Shared\RegistrySample*.cs` provides common hosting
for the FileServer and FederationBridge hosts. It is sample hosting code, not a new
runtime-library dependency or a production identity provider. Link **all four**
files: `RegistrySampleHosting.cs`, `RegistrySampleSecurityState.cs`,
`RegistrySampleBearerHandler.cs` and `RegistrySampleAuthorizationPolicy.cs`.
Principal programs remain responsible for their model, persistence, upstream
sources, local mount and command-line parsing.

## Interface and ordering

The public types are in `XRegistry.Samples`.

```csharp
public sealed record RegistrySampleHostOptions
{
    public required Uri PublicRoot { get; init; }
    public int ListenPort { get; init; } = 8443;
    public bool DemoLoopback { get; init; }
    public bool AllowAnonymousReads { get; init; }
    public string? CertificatePath { get; init; }
    public string? CertificatePasswordEnvironment { get; init; }
    public string? WriteTokenEnvironment { get; init; }
    public string? ReadTokenEnvironment { get; init; }
}
```

Available static call signatures:

```text
public static void RegistrySampleHosting.Configure(
    WebApplicationBuilder builder,
    RegistrySampleHostOptions options,
    Func<string, string?>? secretProvider = null);

public static void RegistrySampleHosting.UseSecurity(
    WebApplication app, RegistrySampleHostOptions options);

public static IRegistryAuthorizationPolicy RegistrySampleHosting.CreateAuthorizationPolicy(
    RegistrySampleHostOptions options);
```

Call `Configure` once before `Build`, then `UseSecurity` immediately after
`Build`, before response-producing middleware or endpoints. The options must
match. Missing, repeated or mismatched security setup fails explicitly. Own and
dispose the built application even when startup fails.
`UseSecurity` installs routing; if the parent uses non-short-circuiting
`UsePathBase` middleware, place that path-base mapping before `UseSecurity`.
Do not place handlers or other middleware that can return a response before it.

`Configure` registers `IRegistryAuthorizationPolicy` in dependency injection.
Pass that instance to each `RegistryEngine`, together with the **same trusted**
`PublicRoot` and `AllowAnonymousReads` value. Alternatively use the public
policy factory. Do not replace the HTTP adapter's `HttpContext.User` mapping
with arbitrary identity headers.

```csharp
RegistrySampleHosting.Configure(builder, hosting);
await using var app = builder.Build();
RegistrySampleHosting.UseSecurity(app, hosting);

var policy = app.Services.GetRequiredService<IRegistryAuthorizationPolicy>();
// Construct the engine with policy, hosting.PublicRoot and hosting.AllowAnonymousReads.
// The parent host chooses the engine, persistence and local mount.
var endpoint = app.MapXRegistry(engine, httpOptions);
if (hosting.AllowAnonymousReads)
{
    endpoint.AllowAnonymousRegistryReads();
}
await app.RunAsync();
```

`AllowAnonymousRegistryReads<TBuilder>(this TBuilder endpoint)` returns the same
endpoint builder and only adds metadata. It is **not** ASP.NET Core
`AllowAnonymous()`. Anonymous GET/HEAD/OPTIONS require all of: the hosting
option, this Registry endpoint marker, the engine's anonymous-read option and
approval by its authorization policy. Invalid presented credentials still
produce a challenge, never an anonymous fallback.

Health and diagnostic endpoints have no implicit exemption. Their default
policy requires an authenticated read/admin role. A parent can explicitly
publish a read-only endpoint with ASP.NET Core `AllowAnonymous()`, but that
does not bypass TLS, loopback checks, startup readiness or the global
authenticated-administrator check for non-read methods.

## Production transport

Production has no implicit insecure fallback:

- `PublicRoot` must be a well-formed absolute **HTTPS** URI, at most 4096
  characters, without user information, a query, a fragment or backslashes.
- `CertificatePath` is an explicit PFX/PKCS#12 file. The file must have exactly
  one private key, at most sixteen certificates, and a currently valid
  non-CA server leaf. Declared EKU must permit server authentication; declared
  key usage must permit digital signatures. Bundled intermediates are supplied
  to Kestrel. An optional named password must resolve when configured.
- Kestrel HTTPS support is registered explicitly, including for
  `CreateSlimBuilder`. The selected listener permits TLS 1.2/1.3 and HTTP/1.1
  or HTTP/2. TLS handshake and request-header deadlines are ten seconds.
  Request headers have a 16 KiB total budget.
- Production binds an any-IP listener at `ListenPort`; the valid range is
  0 through 65535. Zero requests an ephemeral test/development port. It does
  **not** rewrite `PublicRoot`; choose a fixed advertised port for deployment.

The metadata root may have a different hostname from the local bind. It is
never inferred from `Host`, `Forwarded` or `X-Forwarded-*`. Local mount and
path-base handling remain parent-owned.

Ambient `urls`, HTTP/HTTPS port settings, preferred-hosting-URL settings and
the Kestrel endpoint/certificate configuration loader are replaced. Ambient
Kestrel endpoint reload is disabled. Forwarded-header processing is disabled.
Startup verifies that exactly the intended HTTPS or literal-loopback listener
was bound. Adding other code-configured listeners fails startup rather than
quietly extending exposure.

Middleware checks the physical TLS connection and negotiated protocol, not
just `Request.Scheme`. Before listener verification/application startup, or
during stopping, otherwise acceptable requests receive 503. An accidental
plaintext production listener receives 403 even if a proxy/header mapper
claims the request is HTTPS. This module does **not** support external TLS
termination followed by plaintext HTTP to the sample. A proxy must maintain
TLS to this host; accepting forwarded identity or changing this trust model
requires a separate deployment design.

Windows Schannel rejects PFX imports made with `EphemeralKeySet`, as reproduced
by the real TLS tests. Windows therefore uses normal key import, without
`PersistKeySet`; the host identity needs the appropriate key-container/profile
permissions and application disposal releases the imported keys. Other
platforms use ephemeral import. Protect the PFX file and its password using
host/OS access controls. Client certificate/hostname validation remains the
client's responsibility; the sample does not distribute trust anchors or
disable certificate checks.

## Authentication and core authorization

Production requires `WriteTokenEnvironment`, the **name** of the environment
variable holding an opaque administrative bearer token. `ReadTokenEnvironment`
optionally names a separate read-only token. There are no token values in
options or URLs, and no cookie, query, identity-header or authentication-scheme
fallback. A configured missing token is an error. The two tokens must differ.

Names use portable `[A-Za-z_][A-Za-z0-9_]*` syntax and are limited to 128
characters. Tokens require 32 through 512 UTF-8 bytes using the ASCII
bearer-token alphabet (letters, digits, `-_.~+/=`). Whitespace and Unicode
tokens are rejected. Length checking is not an entropy guarantee: generate
at least 32 cryptographically random bytes, encode them, and store the value
only in the approved secret store/environment. For example, assignment does
not print the value:

```powershell
$env:XREGISTRY_SAMPLE_ADMIN = [Convert]::ToBase64String(
    [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Secrets are resolved once at configuration, through
`Environment.GetEnvironmentVariable` or the host-approved `secretProvider`.
Tests use an isolated in-memory provider, not process-wide environment edits.
The helper retains SHA-256 digests, compares request digests using
`CryptographicOperations.FixedTimeEquals`, and clears digest buffers at
disposal. Rotation requires restarting/reconfiguring the host.

The `RegistrySampleBearer` ASP.NET Core authentication scheme constructs
`HttpContext.User`. Ambient principals are cleared first. Authorization
accepts only authenticated sample-scheme identities with explicit,
sample-issued `registry.read` or `registry.admin` roles:

| Role | Core access |
| --- | --- |
| `registry.read` | `RegistryAccess.Read` only |
| `registry.admin` | All defined core accesses, including model/capability changes and the event outbox |
| Anonymous | Read only with the explicit approvals described above |
| Other identity/role | Denied |

The handler accepts one bounded `Authorization` value; the field-value limit
is 1024 ASCII bytes and the token limit is 512. It does not decode JWTs, validate
JWT issuers/audiences or establish individual human identities. A bearer secret
is replayable by anyone who possesses it.

Missing/wrong credentials produce 401 with
`Bearer realm="xregistry-sample"`; authenticated permission failures produce
403. Core-generated 401 responses also receive a challenge if absent.
Non-read HTTP methods require the administrative identity before dispatch;
the core engine still independently authorizes each operation and affected
entity. Host middleware is not a substitute for core authorization.

No incoming credential is installed as an upstream credential or forwarded by
this module. Federation origin credentials must be configured separately,
with their own permissions and origin restrictions. Reusing an inbound bearer
token as an upstream credential would violate this separation.

The helper does not log secret values. Routine ASP.NET request-URL logging is
filtered below Warning, and the unused Data Protection provider is kept in
memory rather than accessing an ambient key repository. Do not add request
header/body logging or log secret-provider values in the parent host.

## Explicit loopback demonstration

Only `DemoLoopback = true` enables the demonstration mode. Its `PublicRoot`
must be HTTP and name `localhost` or a literal loopback IP, not a public alias.
Kestrel binds a literal loopback address, and every request's connection peer
must remain loopback. Certificate or token settings cannot be combined with
demo mode.

Startup prominently logs **INSECURE LOOPBACK DEMO**. Requests without an
Authorization header receive the identified
`loopback-demo-administrator` principal and its
`urn:xregistry:sample:mode=loopback-demo` claim. Presented bearer credentials
are rejected instead of silently ignored. Any local process can administer
this demo: do not expose it through port forwarding, SSH tunnels, containers,
reverse proxies or shared-machine arrangements that admit untrusted callers.
Absence of a token or certificate never activates demo mode.

## Verification and deployment limits

`tests\XRegistry.SampleHosting.Tests` links the four shared source files into a
standalone net10.0 TUnit executable. It uses real Kestrel, temporary PFX files,
an isolated test CA with `CustomRootTrust`, normal certificate-name checking,
explicit `RequestDelegate` endpoints and the actual `RegistryEngine` policy
seam. It installs no roots in system stores and uses no blanket TLS callback.
Fixture tokens/certificates are generated locally and temporary files are
removed when their hosts are disposed.

The managed net10.0 executable and published **win-x64 Native AOT** executable
each passed **92 tests, zero failures and zero skips**. Native publishing
executed ILC and passed an explicit zero-IL-warning log assertion; no warning
suppression or blanket TLS callback was used. Commands run from the repository
root:

```powershell
$project = '.\tests\XRegistry.SampleHosting.Tests\XRegistry.SampleHosting.Tests.csproj'
$artifacts = '.\artifacts\sample-hosting-build'
dotnet test --project $project --configuration Release --artifacts-path $artifacts --minimum-expected-tests 92 --zero-tests-policy strict --timeout 3m --no-ansi --results-directory "$artifacts\managed-results"
if ($LASTEXITCODE -ne 0) { throw 'Managed sample security tests failed.' }

$env:PATH = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer;' + $env:PATH
dotnet publish $project -c Release -f net10.0 -r win-x64 -p:PublishAot=true -p:TrimmerSingleWarn=false -p:IlcTreatWarningsAsErrors=true --artifacts-path $artifacts -o "$artifacts\native\win-x64" --nologo -v minimal 2>&1 | Tee-Object -FilePath "$artifacts\native-publish.log"
if ($LASTEXITCODE -ne 0) { throw 'Native sample security publish failed.' }
if (Select-String -LiteralPath "$artifacts\native-publish.log" -Pattern '\bwarning IL\d+\b' -Quiet) {
    throw 'Native sample security publish emitted IL warnings.'
}
if (!(Select-String -LiteralPath "$artifacts\native-publish.log" -SimpleMatch 'Generating native code' -Quiet)) {
    throw 'Inconclusive warning gate: ILC did not execute in this run.'
}
& "$artifacts\native\win-x64\XRegistry.SampleHosting.Tests.exe" --minimum-expected-tests 92 --zero-tests-policy strict --timeout 3m --no-ansi --results-directory "$artifacts\native-results"
if ($LASTEXITCODE -ne 0) { throw 'Native sample security tests failed.' }
```

The standalone test project uses the centrally managed TUnit version and a Server project
reference; its build outputs are isolated under `artifacts\sample-hosting-build`.
Other operating systems and architectures were not native-qualified here.

This support is not a release/security certification. It provides coarse
sample roles, not OIDC/JWT, per-person accounts, revocation lists, token
rotation overlap, rate limiting, tenancy isolation, audit retention, platform
certificate provisioning or an internet deployment perimeter. Those remain
explicit host/deployment responsibilities. Principal sample programs and
project registration remain separate host concerns.
