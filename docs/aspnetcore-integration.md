# Integrate xRegistry into an ASP.NET Core application

The host owns authentication, authorization policy, persistence, model
selection, and object lifetimes. `XRegistry.AspNetCore` maps a configured
`RegistryEngine`; it does not start or secure an application by itself.

## Packages

An embedded transient server normally references:

```xml
<ItemGroup>
  <PackageReference Include="XRegistry.AspNetCore" Version="0.1.0-alpha" />
  <PackageReference Include="XRegistry.Models" Version="0.1.0-alpha" />
</ItemGroup>
```

Add `XRegistry.Storage.File` for durable local persistence and
`XRegistry.Validation` when the application calls the lower-level validation
APIs directly. `XRegistry.Server` already carries the validation dependency
used by `BuiltInRegistryResourceValidator`. Use the package source approved for
your application; CI artifacts are not automatically promoted to NuGet.org.

## Compose the engine and endpoint

The following excerpt assumes the application has already registered an
authentication scheme:

```csharp
using System.Security.Claims;
using XRegistry;
using XRegistry.AspNetCore;
using XRegistry.Models;
using XRegistry.Server;

var builder = WebApplication.CreateBuilder(args);
// Register the application's approved "Bearer" authentication handler before Build.

var app = builder.Build();
app.UseAuthentication();

var model = BuiltInRegistryModels.CompileForServer(RegistryModelKind.Schema);
var persistence = new InMemoryRegistryPersistence();
var authorization = new ApplicationRegistryAuthorization();
var engine = new RegistryEngine(
    new RegistryEngineOptions
    {
        RegistryId = "application-registry",
        PublicRoot = new Uri("https://registry.example/registry"),
        Model = model,
        AllowAnonymousReads = false,
        ResourceValidator = new BuiltInRegistryResourceValidator(),
        Limits = new RegistryLimits
        {
            MaxDocumentBytes = 8 * 1024 * 1024
        }
    },
    persistence,
    authorization);

app.MapXRegistry(engine, new RegistryHttpOptions
{
    MountPath = "/registry",
    AuthenticationChallenge = "Bearer",
    RequestTimeout = TimeSpan.FromSeconds(30)
});

await app.RunAsync();

sealed class ApplicationRegistryAuthorization : IRegistryAuthorizationPolicy
{
    public ValueTask<bool> AuthorizeAsync(
        ClaimsPrincipal caller,
        RegistryAccess access,
        RegistryPath path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allowed = access == RegistryAccess.Read
            ? caller.Identity?.IsAuthenticated == true
            : caller.IsInRole("RegistryWriter");
        return ValueTask.FromResult(allowed);
    }
}
```

Register the authentication services appropriate to the application. The policy
receives the trusted `HttpContext.User` by default; arbitrary identity headers
are not recognized. Authentication determines who the caller is, while
`IRegistryAuthorizationPolicy` determines what that principal may do.

`PublicRoot` is trusted advertised metadata. Set it from deployment
configuration; do not derive it from an untrusted inbound Host header.
`MountPath` controls only local routing.

## Use durable local persistence

For a new, empty, privately administered local directory:

```csharp
using XRegistry.Storage.File;

Directory.CreateDirectory(dataRoot);
using var store = initialize
    ? LocalFileStore.Initialize(dataRoot)
    : LocalFileStore.Open(dataRoot);
using var persistence = new LocalRegistryPersistence(store);

var engine = new RegistryEngine(options, persistence, authorization);
```

Initialization never replaces an existing store, and `Open` never creates a
missing store. Keep the store and persistence alive for the complete engine
lifetime. The local writer supports the filesystems and single-writer rules
documented in [durable storage](storage.md). The
[FileServer sample](../samples/XRegistry.FileServer/README.md) is the complete
host, HTTPS, initialization, backup, restart, and health example.

## Select model and validation behavior

`RegistryModel.Compile` validates the model structure. It does not validate
existing Registry data, authenticate callers, or validate the format of an
uploaded Document. Supply an `IRegistryResourceValidator`, such as
`BuiltInRegistryResourceValidator`, to enable supported schema format and
compatibility checks.

The engine does not dispose application persistence, authorization services,
or caller-owned Document streams. Operation results own their returned
metadata and Document content.

See the [server contract](server.md), [consumer embedding guide](consumer-migration.md),
and [sample security contract](sample-security.md) before deploying a host.
Use [custom models](custom-models.md) when the Registry domain is
application-specific.
