# Use the HTTP client

`XRegistry.Client.XRegistryHttpClient` performs bounded operations against one
configured Registry origin. It does not infer a model, retry mutations, follow
discovery advertisements, or turn Registry error responses into success.

## Configure the client

```csharp
using System.Net.Http.Headers;
using XRegistry.Client;

var token = GetTokenFromApprovedSecretStore();
using var client = new XRegistryHttpClient(
    new Uri("https://registry.example/xreg/"),
    new XRegistryHttpClientOptions
    {
        RequestTimeout = TimeSpan.FromSeconds(30),
        MaxMetadataBytes = 4 * 1024 * 1024,
        MaxDocumentBytes = 64 * 1024 * 1024,
        AuthorizationProvider = (uri, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<AuthenticationHeaderValue?>(
                new("Bearer", token));
        }
    });
```

The credential provider is called only for the validated configured origin.
`AllowLoopbackHttp` and `AllowPrivateOrigin` are explicit opt-ins for controlled
development or private deployments. They are not general certificate or
network-policy bypasses.

## Read metadata and handle Registry errors

```csharp
using var response = await client.SendAsync(
    HttpMethod.Get,
    "/proxygroups/github/proxies/api$details",
    cancellationToken: cancellationToken);

using var body = await response.ReadMetadataAsync(cancellationToken);
if ((int)response.StatusCode is < 200 or >= 300)
{
    Console.Error.WriteLine(body.RootElement);
    return;
}

Console.WriteLine(body.RootElement.GetProperty("upstream").GetString());
```

Transport, framing, size, and cancellation failures throw. Valid HTTP responses,
including xRegistry problem responses and redirects, retain their original
status and headers.

## Write metadata

Send a JSON metadata representation to a `$details` path:

```csharp
using System.Text;

var metadata = Encoding.UTF8.GetBytes(
    """{"upstream":"https://api.github.com/","protocol":"https","enabled":true}""");
using var response = await client.SendAsync(
    HttpMethod.Put,
    "/proxygroups/github/proxies/api$details",
    metadata,
    "application/json",
    cancellationToken: cancellationToken);

if ((int)response.StatusCode is < 200 or >= 300)
{
    using var problem = await response.ReadMetadataAsync(cancellationToken);
    throw new InvalidOperationException(problem.RootElement.ToString());
}
```

The low-level API sends exactly one request. It does not retry a mutation after
dispatch.

## Upload and download a Document

The caller owns both streams. Upload streams are consumed once and are not
rewound or disposed:

```csharp
await using var input = File.OpenRead("proxy-routes.json");
using var upload = await client.SendDocumentAsync(
    HttpMethod.Put,
    "/proxygroups/github/proxies/api",
    input,
    "application/json",
    cancellationToken: cancellationToken);
if ((int)upload.StatusCode is < 200 or >= 300)
{
    using var problem = await upload.ReadMetadataAsync(cancellationToken);
    throw new InvalidOperationException(problem.RootElement.ToString());
}

using var download = await client.SendAsync(
    HttpMethod.Get,
    "/proxygroups/github/proxies/api",
    cancellationToken: cancellationToken);
if ((int)download.StatusCode is < 200 or >= 300)
{
    throw new HttpRequestException(
        "The Document download failed.", null, download.StatusCode);
}

var temporary = "proxy-routes.json.partial";
await using (var output = File.Create(temporary))
{
    await download.CopyDocumentToAsync(output, cancellationToken);
}
File.Move(temporary, "proxy-routes.downloaded.json", overwrite: true);
```

A failed copy can leave partial destination bytes, so stage file downloads
before publishing them.

## Discover Registries

Discovery reads one selected endpoint and returns advertisements without
following them:

```csharp
var registryDiscovery = await client.DiscoverAsync(
    RegistryDiscoveryLocation.Registry,
    cancellationToken: cancellationToken);
var hostDiscovery = await client.DiscoverAsync(
    RegistryDiscoveryLocation.Host,
    cancellationToken: cancellationToken);

foreach (var registry in registryDiscovery.Registries.Concat(hostDiscovery.Registries))
{
    Console.WriteLine(registry);
}
```

The application decides whether an advertised URL is authorized and creates a
separately configured client for it.

## Read all collection pages

Continuation links are opaque and are followed only when they remain within the
configured Registry and satisfy the paging budgets:

```csharp
var query = new[]
{
    new KeyValuePair<string, string?>("limit", "100")
};

await foreach (var page in client.ReadCollectionPagesAsync(
    "/proxygroups",
    query,
    cancellationToken: cancellationToken))
{
    foreach (var record in page.Records.EnumerateObject())
    {
        Console.WriteLine(record.Name);
    }
}
```

Pages already yielded are not rolled back if a later page fails. Configure
`RegistryPaginationOptions` when the defaults are not appropriate.

See the [HTTP client contract](http-client.md) and
[content decoding contract](http-compression.md) for all limits and framing
rules. The [client sample](../samples/XRegistry.Client/README.md) demonstrates
the same packages from a command line and adds File, Git, and OCI operations.
