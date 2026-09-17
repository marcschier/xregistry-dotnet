# Define a custom xRegistry model

A model defines the Registry's Groups, Resources, metadata attributes,
versioning, and whether a Resource has a Document. The FileServer accepts a
custom model through `--ModelFile`; applications can compile the same source
with `RegistryModel.Compile`.

The checked-in
[`xrproxy-model.json`](../samples/XRegistry.FileServer/models/xrproxy-model.json)
is an xrproxy-style example. It demonstrates how an application could catalog
proxy definitions and policies. It is not a copy or compatibility claim for the
models of `github.com/xregistry/xrproxy`.

## Read the example model

The example defines:

- Registry attribute `environment`;
- Group `proxygroups`, whose singular entity name is `proxygroup`;
- Document-bearing Resource `proxies`, singular `proxy`;
- required URL attribute `upstream`;
- enumerated `protocol` and Boolean `enabled` attributes;
- documentless Resource `policies`, singular `policy`.

Core supplies identity, navigation, epoch, Version, and collection fields. A
Document-bearing Resource can store exact bytes for every Version.
`"hasdocument": false` makes a Resource metadata-only.

## Compile a model in an application

```csharp
using XRegistry;

var path = Path.GetFullPath("models/xrproxy-model.json");
await using var input = File.OpenRead(path);
var source = await RegistryJson.ParseAsync(
    input,
    cancellationToken: cancellationToken);
var model = RegistryModel.Compile(
    source,
    new RegistryModelCompilationOptions
    {
        SourceUri = new Uri(path)
    });

Console.WriteLine(model.Groups["proxygroups"].Resources["proxies"].HasDocument);
```

`SourceUri` gives relative includes a stable absolute base. Core never fetches a
file or URL implicitly. If a model uses external `$includes`, the application
must supply an explicit `IRegistryModelResolver` and bounded
`RegistryModelCompilationOptions`. The resolver owns acquisition,
authentication, URI authorization, and returned JSON.

Resource imports and includes are structural composition mechanisms; they do
not authorize a network request or import existing Registry data.

## Run the FileServer with the model

From the repository root:

```powershell
New-Item -ItemType Directory -Path D:\XrProxyRegistry
$model = (Resolve-Path samples\XRegistry.FileServer\models\xrproxy-model.json).Path
dotnet run --project samples\XRegistry.FileServer --no-launch-profile -- --DataRoot D:\XrProxyRegistry --Initialize true --DemoLoopback true --PublicRoot http://127.0.0.1:5280/registry --ListenPort 5280 --ModelFile $model
```

If a custom model declares required Registry-level attributes without defaults,
place them in a JSON object and pass `--InitialMetadata PATH.json` during first
initialization. The example's `environment` attribute is optional.

Demo mode is local-only and grants administration. Do not expose its listener.
Production startup requires the certificate and credential configuration in the
[FileServer guide](../samples/XRegistry.FileServer/README.md).

## Create a proxy definition

Using `XRegistryHttpClient`, create metadata first:

```csharp
using System.Text;
using XRegistry.Client;

using var client = new XRegistryHttpClient(
    new Uri("http://127.0.0.1:5280/registry/"),
    new XRegistryHttpClientOptions { AllowLoopbackHttp = true });

var metadata = Encoding.UTF8.GetBytes(
    """{"upstream":"https://api.github.com/","protocol":"https","enabled":true}""");
using var created = await client.SendAsync(
    HttpMethod.Put,
    "/proxygroups/github/proxies/api$details",
    metadata,
    "application/json");
if ((int)created.StatusCode is < 200 or >= 300)
{
    using var problem = await created.ReadMetadataAsync();
    throw new InvalidOperationException(problem.RootElement.ToString());
}
```

Then publish the exact proxy configuration Document:

```csharp
await using var document = File.OpenRead("proxy-routes.json");
using var uploaded = await client.SendDocumentAsync(
    HttpMethod.Put,
    "/proxygroups/github/proxies/api",
    document,
    "application/json");
if ((int)uploaded.StatusCode is < 200 or >= 300)
{
    throw new HttpRequestException(
        "The Document upload failed.", null, uploaded.StatusCode);
}
```

Read metadata from the `$details` representation and Document bytes from the
Resource representation:

```csharp
using var details = await client.SendAsync(
    HttpMethod.Get,
    "/proxygroups/github/proxies/api$details");
using var proxy = await details.ReadMetadataAsync();

using var content = await client.SendAsync(
    HttpMethod.Get,
    "/proxygroups/github/proxies/api");
await using var output = File.Create("downloaded-proxy-routes.json");
await content.CopyDocumentToAsync(output);
```

The effective paths derive from plural Group and Resource names plus their
singular ID fields. Versions are available below
`/proxygroups/{proxygroupid}/proxies/{proxyid}/versions/{versionid}`.

## Change a deployed model

An initialized store retains the authored model source and a frozen compiled
form. On restart, persisted model state takes precedence over a different
command-line model selection. A model change is an authenticated, authorized
model update that validates existing data; it is not an implicit replacement
caused by restarting with another file.

See [built-in model sources and compiler behavior](models.md) for includes,
imports, compiler budgets, and effective models. See [document validation](validation.md)
when Resources need syntax or compatibility checks, and
[specification provenance](spec-feedback.md) for the repository's model-source
rules.
