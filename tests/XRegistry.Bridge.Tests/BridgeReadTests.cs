using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.File;
using XRegistry.Federation;
using XRegistry.Samples.Bridge;

namespace XRegistry.Bridge.Tests;

public class BridgeReadTests
{
    [Test]
    public async Task RootAndGroupCollectionsComposeOrderedResourceUnits()
    {
        var local = new ControlledSource("local", "local-v", ["shared", "local"]);
        var remote = new ControlledSource("remote", "remote-v", ["shared", "remote"]);
        await using var app = await BridgeFixture.StartAsync([local.Registration(), remote.Registration()]);
        using var client = BridgeFixture.Client(app);

        using var root = await client.GetAsync("/registry");
        await Assert.That(root.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var rootJson = await BridgeFixture.Json(root);
        await Assert.That(rootJson.GetProperty("registryid").GetString()).IsEqualTo("bridge");
        await Assert.That(rootJson.GetProperty("dirscount").GetInt32()).IsEqualTo(1);
        await Assert.That(rootJson.GetProperty("dirsurl").GetString()).IsEqualTo("https://bridge.example/registry/dirs");
        await Assert.That(rootJson.TryGetProperty("capabilities", out _)).IsFalse();
        await Assert.That(rootJson.TryGetProperty("model", out _)).IsFalse();
        await Assert.That(rootJson.TryGetProperty("modelsource", out _)).IsFalse();
        await Assert.That(rootJson.GetProperty("site").GetString()).IsEqualTo("https://local.example/domain");
        await Assert.That(rootJson.TryGetProperty("origin", out _)).IsFalse();

        using var configuration = await client.GetAsync("/registry?inline=capabilities,model,modelsource");
        await Assert.That(configuration.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var configured = await BridgeFixture.Json(configuration);
        await Assert.That(configured.GetProperty("capabilities").GetProperty("federation").GetProperty("resolution").GetString()).IsEqualTo("producer");
        await Assert.That(configured.GetProperty("model").GetProperty("attributes").GetProperty("registryid").GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(configured.GetProperty("modelsource").GetProperty("attributes").GetProperty("site").GetProperty("type").GetString()).IsEqualTo("url");

        using var group = await client.GetAsync("/registry/dirs/g");
        var groupJson = await BridgeFixture.Json(group);
        await Assert.That(group.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(groupJson.GetProperty("filescount").GetInt32()).IsEqualTo(3);
        await Assert.That(groupJson.GetProperty("filesurl").GetString()).IsEqualTo("https://bridge.example/registry/dirs/g/files");

        using var resources = await client.GetAsync("/registry/dirs/g/files");
        var resourceJson = await BridgeFixture.Json(resources);
        await Assert.That(resources.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(string.Join(",", resourceJson.EnumerateObject().Select(item => item.Name)))
            .IsEqualTo("local,remote,shared");
        await Assert.That(resourceJson.GetProperty("shared").GetProperty("versionid").GetString()).IsEqualTo("local-v");
        await Assert.That(resourceJson.GetProperty("shared").GetProperty("self").GetString())
            .IsEqualTo("https://bridge.example/registry/dirs/g/files/shared$details");
        await Assert.That(resourceJson.GetProperty("remote").GetProperty("versionid").GetString()).IsEqualTo("remote-v");
    }

    [Test]
    public async Task ResourceShadowOwnsEveryVersionAndDocumentWithoutOpeningLaterSources()
    {
        var local = new ControlledSource("local", "v1", ["shared"]);
        var remote = new ControlledSource("remote", "v2", ["shared"]);
        await using var app = await BridgeFixture.StartAsync([local.Registration(), remote.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var document = await client.GetAsync("/registry/dirs/g/files/shared");
        await Assert.That(document.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Convert.ToHexString(await document.Content.ReadAsByteArrayAsync())).IsEqualTo("00FF0D0A41");
        await Assert.That(document.Headers.GetValues("xRegistry-versionid").Single()).IsEqualTo("v1");
        await Assert.That(document.Content.Headers.ContentLocation!.AbsoluteUri).IsEqualTo("https://bridge.example/registry/dirs/g/files/shared/versions/v1");
        using var missingVersion = await client.GetAsync("/registry/dirs/g/files/shared/versions/v2$details");
        await Assert.That(missingVersion.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var versions = await client.GetAsync("/registry/dirs/g/files/shared/versions");
        await Assert.That(string.Join(",", (await BridgeFixture.Json(versions)).EnumerateObject().Select(item => item.Name))).IsEqualTo("v1");
        await Assert.That(remote.Opens).IsEqualTo(0);
        await Assert.That(local.Opens).IsEqualTo(local.Closes);
    }

    [Test]
    public async Task OpaqueDomainValuesArePreservedWhileOnlyModeledNavigationIsRebased()
    {
        var source = new ControlledSource("local", "v1", ["shared"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/registry/dirs/g/files/shared$details");
        request.Headers.Host = "untrusted.example";
        using var response = await client.SendAsync(request);
        var value = await BridgeFixture.Json(response);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(value.GetProperty("self").GetString()).IsEqualTo("https://bridge.example/registry/dirs/g/files/shared$details");
        await Assert.That(value.GetProperty("metaurl").GetString()).IsEqualTo("https://bridge.example/registry/dirs/g/files/shared/meta");
        await Assert.That(value.GetProperty("versionsurl").GetString()).IsEqualTo("https://bridge.example/registry/dirs/g/files/shared/versions");
        await Assert.That(value.GetProperty("domain").GetProperty("self").GetString()).IsEqualTo("https://local.example/opaque");
        await Assert.That(value.GetProperty("domain").GetProperty("uri").GetString()).IsEqualTo("https://domain.example/document");
        await Assert.That(value.TryGetProperty("source", out _)).IsFalse();
        await Assert.That(response.Headers.GetValues("X-Bridge-Sources").Single()).IsEqualTo("local");
    }

    [Test]
    public async Task ProducerViewIsReadOnceWithoutOpeningOrRetraversingOtherSources()
    {
        var producer = new ControlledSource("producer", "v1", ["shared"]) { Producer = true };
        var forbidden = new ControlledSource("catalog", "v2", ["other"]);
        await using var app = await BridgeFixture.StartAsync([producer.Registration(), forbidden.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/shared$details");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(producer.Reads.Count).IsEqualTo(1);
        await Assert.That(forbidden.Opens).IsEqualTo(0);
    }

    [Test]
    public async Task NativeProducerProjectsItsCapturedDefaultWithoutAdditionalSourceReads()
    {
        var producer = new ControlledSource("producer", "v1", ["shared"]) { Producer = true };
        producer.Override = (request, _) =>
        {
            var owner = "/dirs/g/files/shared";
            var resource = new JsonObject
            {
                ["fileid"] = "shared",
                ["xid"] = owner,
                ["self"] = "#/entity",
                ["metaurl"] = "#/entity/meta",
                ["meta"] = producer.Entity(owner + "/meta"),
                ["versionsurl"] = "#/entity/versions",
                ["versionscount"] = 1,
                ["versions"] = new JsonObject { ["v1"] = producer.Entity(owner + "/versions/v1") }
            };
            return ValueTask.FromResult(FederationReadResult.FromMetadata(request.Target, producer.Context,
                RegistryJson.Parse(new JsonObject { ["entity"] = resource }.ToJsonString()).RootElement));
        };
        var registration = new FederationSourceRegistration("producer", FederationRepresentation.DocumentView,
            (_, _) => ValueTask.FromResult(new FederationSourceLease(producer, static () => ValueTask.CompletedTask)));
        await using var app = await BridgeFixture.StartAsync([registration]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/shared$details");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(producer.Reads.Count).IsEqualTo(1);
    }

    [Test]
    public async Task IncompleteCollectionsAndIncompatibleModelsNeverProducePartialSuccess()
    {
        var source = new ControlledSource("local", "v1", ["shared"]) { Complete = false };
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("code").GetString()).IsEqualTo("source_inconsistentsnapshot");
        source.Complete = true;
        source.Model = RegistryModel.Compile(RegistryJson.Parse("""{"groups":{"other":{"singular":"other"}}}"""));
        using var mismatch = await client.GetAsync("/registry/dirs/g/files/shared$details");
        await Assert.That(mismatch.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    public async Task UnsupportedQueriesUnknownTypesAndAggregateWritesNeverOpenSources()
    {
        var source = new ControlledSource("local", "v1", ["shared"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var query = await client.GetAsync("/registry?native=oci");
        using var unknown = await client.GetAsync("/registry/unknown/id");
        using var write = await client.PutAsync("/registry/dirs/g/files/shared", new ByteArrayContent([1]));
        await Assert.That(query.StatusCode).IsEqualTo(HttpStatusCode.NotImplemented);
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(write.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(source.Opens).IsEqualTo(0);
    }

    [Test]
    public async Task CallerSourcePolicyRunsBeforeAcquisitionAndCachesAreRequestScoped()
    {
        var denied = new ControlledSource("denied", "v1", ["shared"]);
        var eligible = new ControlledSource("allowed", "v2", ["shared"]);
        var options = BridgeFixture.Options with { AuthorizeSource = static (_, name, _) => ValueTask.FromResult(name == "allowed") };
        await using var app = await BridgeFixture.StartAsync([denied.Registration(), eligible.Registration()], options);
        using var client = BridgeFixture.Client(app);
        for (var index = 0; index < 2; index++)
        {
            using var response = await client.GetAsync("/registry/dirs/g/files/shared$details");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await BridgeFixture.Json(response)).GetProperty("versionid").GetString()).IsEqualTo("v2");
        }
        await Assert.That(denied.Opens).IsEqualTo(0);
        await Assert.That(eligible.Opens).IsEqualTo(2);
        await Assert.That(eligible.Closes).IsEqualTo(2);
    }

    [Test]
    public async Task EmptyDocumentsAndAuthorizedExternalDescriptorsAreDistinct()
    {
        var source = new ControlledSource("local", "v1", ["shared"]);
        var external = false;
        source.Override = (request, _) =>
        {
            if (request.Operation != FederationOperation.Document) { return source.ReadDefault(request); }
            return ValueTask.FromResult(external
                ? FederationReadResult.FromExternalDocument("/dirs/g/files/shared/versions/v1", source.Context,
                    RegistryJson.Parse("""{"kind":"external","uri":"https://documents.example/item.bin"}""").RootElement)
                : FederationReadResult.FromDocument("/dirs/g/files/shared/versions/v1", source.Context,
                    new FederationDocument([], "application/octet-stream")));
        };
        var options = BridgeFixture.Options with { ExternalDocumentOrigins = [new Uri("https://documents.example/")] };
        await using var app = await BridgeFixture.StartAsync([source.Registration()], options);
        using var client = BridgeFixture.Client(app);
        using var empty = await client.GetAsync("/registry/dirs/g/files/shared");
        await Assert.That(empty.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await empty.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
        external = true;
        using var location = await client.GetAsync("/registry/dirs/g/files/shared");
        await Assert.That(location.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(location.Headers.Location!.AbsoluteUri).IsEqualTo("https://documents.example/item.bin");
        await Assert.That((await location.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
    }

    [Test]
    public async Task FrozenFileCompositionReturnsExactBytesThroughTheSamePublicSourceInterface()
    {
        var repository = BridgeFixture.Repository;
        var directory = Path.Combine(repository, "tests", "Conformance", "Sources", "workingdrafts", "bindings", "samples", "mapping");
        var endpoint = new Uri(directory + Path.DirectorySeparatorChar);
        await using var bootstrap = await FileRegistrySource.OpenAsync(endpoint, FileRegistryLayout.DocumentTree, authorizedRoot: endpoint);
        var model = bootstrap.Model;
        var empty = new ControlledSource("empty", "v1", []) { Model = model };
        var file = new FederationSourceRegistration("frozen", FederationRepresentation.DocumentView, async (budget, token) =>
        {
            var opened = await FileRegistrySource.OpenAsync(endpoint, FileRegistryLayout.DocumentTree,
                authorizedRoot: endpoint, budget: budget, cancellationToken: token);
            return new FederationSourceLease(opened, opened.DisposeAsync);
        });
        await using var app = await BridgeFixture.StartAsync([empty.Registration(), file], BridgeFixture.Options with { Model = model });
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/documents/main/assets/item");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Convert.ToHexString(await response.Content.ReadAsByteArrayAsync())).IsEqualTo("7B2268656C6C6F223A22776F726C64227D0A");
        await Assert.That(response.Headers.GetValues("X-Bridge-Binding").Single()).IsEqualTo("file");
        await Assert.That(response.Headers.GetValues("X-Bridge-Root-Sha256").Single())
            .IsEqualTo("7e4c37ca61b2b875ee055a8fc67e5c90c48b344689823a12dc1fb0a6e33232bf");
    }
}

internal static class BridgeFixture
{
    internal const string ModelText = """
        {"attributes":{"site":{"type":"url"}},"groups":{"dirs":{"singular":"dir","resources":{
        "files":{"singular":"file","attributes":{"domain":{"type":"any"}}},
        "notes":{"singular":"note","hasdocument":false,"attributes":{"message":{"type":"string"}}}}}}}
        """;
    internal static RegistryModel Model { get; } = RegistryModel.Compile(RegistryJson.Parse(ModelText));
    internal static string Repository
    {
        get
        {
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                if (Directory.Exists(Path.Combine(folder.FullName, "tests", "Conformance", "Sources"))) { return folder.FullName; }
            }
            throw new InvalidOperationException("Run bridge tests under the repository-contained artifacts path.");
        }
    }

    internal static BridgeHostOptions Options => new()
    {
        Model = Model,
        PublicRoot = new Uri("https://bridge.example/registry"),
        RegistryId = "bridge",
        AuthorizeSource = static (_, _, _) => ValueTask.FromResult(true),
        AuthorizeMount = static (_, _, _, _) => ValueTask.FromResult(true)
    };

    internal static async Task<WebApplication> StartAsync(
        IReadOnlyList<FederationSourceRegistration> sources, BridgeHostOptions? options = null,
        IReadOnlyList<BridgeWriteMount>? mounts = null, Action<WebApplication>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "controlled"), new Claim(ClaimTypes.Role, "admin")], "test"));
            return next(context);
        });
        configure?.Invoke(app);
        BridgeApplication.Map(app, options ?? Options, sources, mounts ?? []);
        try { await app.StartAsync(); return app; }
        catch { await app.DisposeAsync(); throw; }
    }

    internal static HttpClient Client(WebApplication app) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false
    })
    { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };

    internal static async Task<JsonElement> Json(HttpResponseMessage response) =>
        RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;

    internal static JsonObject Common(string xid, string idName, string id, string origin) => new()
    {
        ["xid"] = xid,
        [idName] = id,
        ["self"] = origin + (xid == "/" ? "" : xid),
        ["epoch"] = 1,
        ["createdat"] = "2026-09-04T00:00:00Z",
        ["modifiedat"] = "2026-09-04T00:00:00Z"
    };
}

internal sealed class ControlledSource : IFederationReadSource
{
    private readonly string version;
    private readonly string[] resources;
    internal readonly List<(FederationOperation Operation, string Target)> Reads = [];
    internal int Opens;
    internal int Closes;
    internal string Name { get; }
    internal bool Complete { get; set; } = true;
    internal bool Producer { get; set; }
    internal Func<FederationReadRequest, CancellationToken, ValueTask<FederationReadResult>>? Override { get; set; }

    internal ControlledSource(string name, string version, string[] resources)
    {
        Name = name;
        this.version = version;
        this.resources = resources;
        Context = new NativeRegistryContext("http", $"https://{name}.example/root");
    }

    public NativeRegistryContext Context { get; }
    public RegistryModel Model { get; set; } = BridgeFixture.Model;
    public JsonElement Capabilities => RegistryJson.Parse(Producer
        ? """{"federation":{"resolution":"producer"}}""" : "{}").RootElement;

    internal FederationSourceRegistration Registration() => new(Name, FederationRepresentation.ApiView, (_, _) =>
    {
        Interlocked.Increment(ref Opens);
        return ValueTask.FromResult(new FederationSourceLease(this, () =>
        {
            Interlocked.Increment(ref Closes);
            return ValueTask.CompletedTask;
        }));
    });

    public ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Reads) { Reads.Add((request.Operation, request.Target)); }
        if (Override is not null) { return Override(request, cancellationToken); }
        return ReadDefault(request);
    }

    internal ValueTask<FederationReadResult> ReadDefault(FederationReadRequest request)
    {
        var parts = request.Target.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (request.Operation == FederationOperation.Document && parts.Length >= 4 && resources.Contains(parts[3], StringComparer.Ordinal))
        {
            if (parts.Length == 6 && parts[5] != version) { throw Missing(); }
            return ValueTask.FromResult(FederationReadResult.FromDocument(
                $"/dirs/g/files/{parts[3]}/versions/{version}", Context,
                new FederationDocument([0, 255, 13, 10, 65], "application/octet-stream")));
        }
        if (request.Operation == FederationOperation.Collection)
        {
            var entries = new JsonObject();
            if (request.Target == "/dirs") { entries["g"] = Entity("/dirs/g"); }
            else if (request.Target == "/dirs/g/files")
            {
                foreach (var id in resources) { entries[id] = Entity("/dirs/g/files/" + id); }
            }
            else if (request.Target == "/dirs/g/notes") { }
            else if (parts.Length == 5 && resources.Contains(parts[3], StringComparer.Ordinal))
            {
                entries[version] = Entity(request.Target + "/" + version);
            }
            else { throw Missing(); }
            return ValueTask.FromResult(Metadata(request.Target, new JsonObject
            {
                ["kind"] = "collection",
                ["xid"] = request.Target,
                ["complete"] = Complete,
                ["entities"] = entries
            }));
        }
        return ValueTask.FromResult(Metadata(request.Target, new JsonObject { ["entity"] = Entity(request.Target) }));
    }

    internal JsonObject Entity(string xid)
    {
        var parts = xid.Split('/', StringSplitOptions.RemoveEmptyEntries);
        JsonObject value;
        if (xid == "/")
        {
            value = BridgeFixture.Common("/", "registryid", Name, Context.Source);
            value["specversion"] = "1.0-rc4";
            value["site"] = $"https://{Name}.example/domain";
            value["modelsource"] = JsonNode.Parse(BridgeFixture.ModelText);
            value["capabilities"] = JsonNode.Parse(Capabilities.GetRawText());
            value["dirsurl"] = Context.Source + "/dirs";
            value["dirscount"] = 1;
            return value;
        }
        if (xid == "/dirs/g")
        {
            value = BridgeFixture.Common(xid, "dirid", "g", Context.Source);
            value["filesurl"] = Context.Source + xid + "/files";
            value["filescount"] = resources.Length;
            value["notesurl"] = Context.Source + xid + "/notes";
            value["notescount"] = 0;
            return value;
        }
        if (parts.Length < 4 || parts[0] != "dirs" || parts[1] != "g" || parts[2] != "files" ||
            !resources.Contains(parts[3], StringComparer.Ordinal)) { throw Missing(); }
        var owner = "/dirs/g/files/" + parts[3];
        if (parts.Length == 5 && parts[4] == "meta")
        {
            value = BridgeFixture.Common(xid, "fileid", parts[3], Context.Source);
            value["defaultversionid"] = version;
            value["defaultversionurl"] = Context.Source + owner + "/versions/" + version;
            value["defaultversionsticky"] = false;
            value["readonly"] = false;
            return value;
        }
        if (parts.Length == 6 && parts[5] != version) { throw Missing(); }
        value = BridgeFixture.Common(xid, "versionid", version, Context.Source);
        value["fileid"] = parts[3];
        value["ancestorid"] = version;
        value["isdefault"] = true;
        value["contenttype"] = "application/octet-stream";
        value["domain"] = new JsonObject { ["self"] = $"https://{Name}.example/opaque", ["uri"] = "https://domain.example/document" };
        if (parts.Length == 4)
        {
            value["metaurl"] = Context.Source + owner + "/meta";
            value["versionsurl"] = Context.Source + owner + "/versions";
            value["versionscount"] = 1;
        }
        return value;
    }

    private FederationReadResult Metadata(string xid, JsonObject value) =>
        FederationReadResult.FromMetadata(xid, Context, RegistryJson.Parse(value.ToJsonString()).RootElement);

    private static FederationException Missing() => new(FederationErrorCode.NotFound, "Controlled source has no exact entity.");
}
