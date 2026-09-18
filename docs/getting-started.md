# Getting started

The .NET packages can be used as an HTTP client, embedded in an ASP.NET Core
application, or combined with the durable local file store. They target .NET 8
and .NET 10; the supplied hosts and executables use .NET 10.

## Choose packages

Reference the packages your application uses directly:

| Goal | Packages |
| --- | --- |
| Call an HTTP xRegistry | `XRegistry.Client` |
| Compile custom model documents | `XRegistry` |
| Use packaged Core, Endpoint, Message, Schema, CloudEvents, Registry, or OpenUSD models | `XRegistry.Models` |
| Run the engine in-process | `XRegistry.Server` |
| Expose the engine through ASP.NET Core | `XRegistry.AspNetCore` |
| Add the SQLite/immutable-file store | `XRegistry.Storage.File` |
| Call schema validation APIs directly | `XRegistry.Validation` |

For example:

```powershell
dotnet add package XRegistry.Client --version 0.1.0-alpha
```

Version `0.1.0-alpha` is published on NuGet.org and remains a prerelease:
install it explicitly and do not treat it as a conformance-qualified stable
release. Contributors can instead clone this repository and use its project
references, CI package artifacts, and samples; see the [release guide](releasing.md)
for the publication and qualification boundary.

## Run a local Registry

Create an empty, caller-owned directory and start the FileServer's explicit
loopback demo:

```powershell
New-Item -ItemType Directory -Path D:\RegistryData
dotnet run --project samples\XRegistry.FileServer --no-launch-profile -- --DataRoot D:\RegistryData --Initialize true --DemoLoopback true --PublicRoot http://127.0.0.1:5280/registry --ListenPort 5280
```

Demo mode grants local callers administration. Do not expose, forward, or
tunnel its listener. Restart without `--Initialize true`.

## Connect from .NET

Loopback HTTP is rejected unless a development client opts in:

```csharp
using XRegistry.Client;

using var client = new XRegistryHttpClient(
    new Uri("http://127.0.0.1:5280/registry/"),
    new XRegistryHttpClientOptions
    {
        AllowLoopbackHttp = true,
        RequestTimeout = TimeSpan.FromSeconds(10),
        MaxMetadataBytes = 1024 * 1024,
        MaxDocumentBytes = 8 * 1024 * 1024
    });

using var response = await client.SendAsync(HttpMethod.Get);
if ((int)response.StatusCode is < 200 or >= 300)
{
    using var problem = await response.ReadMetadataAsync();
    throw new InvalidOperationException(problem.RootElement.ToString());
}

using var registry = await response.ReadMetadataAsync();
Console.WriteLine(registry.RootElement.GetProperty("registryid").GetString());
```

`XRegistryHttpResponse` and the JSON document returned by
`ReadMetadataAsync` are caller-owned. A response body can be consumed once.
Registry error statuses remain response values rather than being changed into
successful results.

## Developer guides

- [Use the HTTP client](client-guide.md) for authentication, metadata,
  Documents, discovery, and pagination.
- [Integrate with ASP.NET Core](aspnetcore-integration.md) to embed an endpoint
  and select persistence and authorization.
- [Define a custom model](custom-models.md) for an application-specific
  Registry.
- [Deploy the FileServer sample](../samples/XRegistry.FileServer/README.md) for
  the complete HTTPS, credentials, backup, and restart flow.

Production clients use HTTPS and explicitly scoped credentials. Credentials do
not belong in URLs, command lines, model files, or source code. Mutations are
not retried because a transport failure after dispatch can leave their outcome
unknown.
