using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Client;

namespace XRegistry.Federation.Tests;

public class HttpFederationReadSourceTests
{
    internal const string ModelSource = """
        {"groups":{"documents":{"singular":"document","resources":{"assets":{"singular":"asset"}}},
        "categories":{"singular":"category","resources":{"registries":{"singular":"registry","hasdocument":false}}}}}
        """;

    [Test]
    [Arguments("/documents/group:one/assets/item@stable/versions/v:1")]
    [Arguments("/documents/group%3Aone/assets/item%40stable/versions/v%3A1")]
    public async Task EncodedAndLiteralReservedIdsPreserveOneTypedHttpIdentity(string target)
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/group:one/assets/item@stable/versions/v:1$details", () => Json(
                """{"xid":"/documents/group%3Aone/assets/item%40stable/versions/v%3A1","assetid":"item@stable","versionid":"v:1"}"""));
        });
        using var source = await HttpFederationReadSource.OpenAsync(
            new Uri(new Uri(app.Urls.Single()), "/mount/"), new() { AllowLoopbackHttp = true });
        var result = await source.ReadAsync(new(FederationOperation.Entity, target,
            representation: FederationRepresentation.ApiView));
        await Assert.That(result.Metadata.GetProperty("entity").GetProperty("xid").GetString())
            .IsEqualTo("/documents/group%3Aone/assets/item%40stable/versions/v%3A1");
        await Assert.That(result.Metadata.GetProperty("entity").GetProperty("assetid").GetString()).IsEqualTo("item@stable");
        await Assert.That(result.Metadata.GetProperty("entity").GetProperty("versionid").GetString()).IsEqualTo("v:1");
    }

    [Test]
    public async Task BootstrapChecksVersionAndReadsChosenDefaultByExplicitVersionUnderTheMount()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/main/assets/item/meta", (HttpContext context) =>
            {
                paths.Add(context.Request.Path);
                return Json("""{"xid":"/documents/main/assets/item/meta","assetid":"item","defaultversionid":"v1","epoch":9}""");
            });
            app.MapGet("/mount/documents/main/assets/item/versions/v1", (HttpContext context) =>
            {
                paths.Add(context.Request.Path);
                context.Response.Headers.ETag = "\"document-1\"";
                context.Response.Headers["xRegistry-versionid"] = "v1";
                return Results.Bytes([0, 255, 10, 13], "application/octet-stream");
            });
        });
        using var source = await HttpFederationReadSource.OpenAsync(
            new Uri(new Uri(app.Urls.Single()), "/mount/"),
            new XRegistryHttpClientOptions { AllowLoopbackHttp = true });
        var result = await source.ReadAsync(new(FederationOperation.Document, "/documents/main/assets/item"));

        await Assert.That(source.SpecVersion).IsEqualTo("1.0-rc4");
        await Assert.That(source.Context.IsImmutable).IsFalse();
        await Assert.That(source.Context.Revision).IsNull();
        await Assert.That(result.SelectedXid).IsEqualTo("/documents/main/assets/item/versions/v1");
        await Assert.That(result.Document!.ContentType).IsEqualTo("application/octet-stream");
        using var bytes = result.Document.OpenRead();
        using var output = new MemoryStream();
        await bytes.CopyToAsync(output);
        await Assert.That(Convert.ToHexString(output.ToArray())).IsEqualTo("00FF0A0D");
        await Assert.That(paths.Contains("/mount/documents/main/assets/item")).IsFalse();
        await Assert.That(paths.Count(path => path == "/mount/documents/main/assets/item/meta")).IsEqualTo(2);
        await Assert.That(source.Observations.Any(observation => observation.ETag == "\"document-1\"")).IsTrue();
    }

    [Test]
    public async Task UniqueLiteralSelectionWaitsForEveryOpaqueUnfilteredPage()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths, capabilities: """{"available":{"entities":{"mutable":false},"model":{"mutable":false},"modelsource":{"mutable":false}},"flags":null,"pagination":false}""");
            app.MapGet("/mount/documents/main/assets", (HttpContext context) =>
            {
                paths.Add(context.Request.Path + context.Request.QueryString);
                if (!context.Request.QueryString.HasValue)
                {
                    context.Response.Headers.Link = "<?opaque=%41+%2B>;rel=next;count=2";
                    return Json("""{"one":{"assetid":"one","xid":"/documents/main/assets/one","labels":{"note":"a*b"}}}""");
                }
                return Json("""{"two":{"assetid":"two","xid":"/documents/main/assets/two","labels":{"note":"axxb"}}}""");
            });
        });
        using var source = await HttpFederationReadSource.OpenAsync(
            new Uri(new Uri(app.Urls.Single()), "/mount/"), new() { AllowLoopbackHttp = true });
        var selected = await source.ReadAsync(new(FederationOperation.Collection,
            "/documents/main/assets", new("note", "A*B"), FederationRepresentation.ApiView));

        await Assert.That(selected.SelectedXid).IsEqualTo("/documents/main/assets/one");
        await Assert.That(selected.Metadata.GetProperty("entity").GetProperty("labels").GetProperty("note").GetString())
            .IsEqualTo("a*b");
        await Assert.That(paths.Count(path => path.StartsWith("/mount/documents/main/assets", StringComparison.Ordinal))).IsEqualTo(2);
        await Assert.That(paths[^1]).IsEqualTo("/mount/documents/main/assets?opaque=%41+%2B");
        await Assert.That(paths.Any(path => path.Contains("filter=", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ExternalDocumentRedirectRemainsAnUnfetchedVerifiedDescriptor()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/main/assets/item/versions/v1", (HttpContext context) =>
            {
                paths.Add(context.Request.Path);
                context.Response.Headers.Location = "https://content.example/document.json";
                return Results.StatusCode(303);
            });
            app.MapGet("/mount/documents/main/assets/item/versions/v1$details", () => Json(
                """{"xid":"/documents/main/assets/item/versions/v1","versionid":"v1","asseturl":"https://content.example/document.json"}"""));
        });
        using var source = await HttpFederationReadSource.OpenAsync(
            new Uri(new Uri(app.Urls.Single()), "/mount/"), new() { AllowLoopbackHttp = true });
        var result = await source.ReadAsync(new(FederationOperation.Document, "/documents/main/assets/item/versions/v1"));

        await Assert.That(result.Document).IsNull();
        await Assert.That(result.ExternalDocument.GetProperty("kind").GetString()).IsEqualTo("external");
        await Assert.That(result.ExternalDocument.GetProperty("uri").GetString()).IsEqualTo("https://content.example/document.json");
        await Assert.That(result.SelectedXid).IsEqualTo("/documents/main/assets/item/versions/v1");
        await Assert.That(source.Observations.All(observation => observation.RequestUri.Host == "127.0.0.1")).IsTrue();
    }

    [Test]
    [Arguments("/documents/main/assets/item", "/mount/documents/main/assets/item$details", "resource")]
    [Arguments("/documents/main/assets/item/meta", "/mount/documents/main/assets/item/meta", "meta")]
    [Arguments("/documents/main/assets/item/versions/v2", "/mount/documents/main/assets/item/versions/v2$details", "version")]
    [Arguments("/categories/dev/registries/source", "/mount/categories/dev/registries/source", "resource")]
    public async Task MetadataUsesModelDefinedDetailsRules(string xid, string path, string kind)
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet(path, (HttpContext context) =>
            {
                paths.Add(context.Request.Path);
                return Json($$"""{"xid":"{{xid}}","label":"exact","epoch":184467440737095516160}""");
            });
        });
        using var source = await HttpFederationReadSource.OpenAsync(
            new Uri(new Uri(app.Urls.Single()), "/mount/"), new() { AllowLoopbackHttp = true });
        var result = await source.ReadAsync(new(FederationOperation.Entity, xid, representation: FederationRepresentation.ApiView));
        await Assert.That(paths[^1]).IsEqualTo(path);
        await Assert.That(result.Metadata.GetProperty("kind").GetString()).IsEqualTo(kind);
        await Assert.That(result.Metadata.GetProperty("entity").GetProperty("epoch").GetRawText()).IsEqualTo("184467440737095516160");
    }

    [Test]
    public async Task MetadataOnlyDocumentsAndUnadvertisedViewsFailBeforeEntityRequests()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app => Bootstrap(app, paths,
            capabilities: """{"available":{"modelsource":{}},"flags":null}"""));
        using var source = await HttpFederationReadSource.OpenAsync(
            new Uri(new Uri(app.Urls.Single()), "/mount/"), new() { AllowLoopbackHttp = true });
        var before = paths.Count;
        var document = await FailureAsync(() => source.ReadAsync(new(FederationOperation.Document, "/categories/dev/registries/source")));
        var view = await FailureAsync(() => source.ReadAsync(new(FederationOperation.Entity, "/documents/main/assets/item")));
        await Assert.That(document.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(view.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(paths.Count).IsEqualTo(before);
    }

    [Test]
    public async Task WrongCoreVersionStopsBootstrapBeforeInterpretingTheModel()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app => Bootstrap(app, paths, version: "1.0-rc3"));
        FederationException? observed = null;
        try
        {
            using var source = await HttpFederationReadSource.OpenAsync(
                new Uri(new Uri(app.Urls.Single()), "/mount/"), new() { AllowLoopbackHttp = true });
        }
        catch (FederationException exception)
        {
            observed = exception;
        }
        await Assert.That(observed!.Code).IsEqualTo(FederationErrorCode.UnsupportedVersion);
        await Assert.That(paths.Count).IsEqualTo(1);
        await Assert.That(paths.Single()).IsEqualTo("/mount/");
    }

    [Test]
    [Arguments(401, "#access_denied", FederationErrorCode.PolicyDenied)]
    [Arguments(403, "#access_denied", FederationErrorCode.PolicyDenied)]
    [Arguments(404, "#not_found", FederationErrorCode.NotFound)]
    [Arguments(404, "#api_not_found", FederationErrorCode.UnsupportedOperation)]
    [Arguments(404, "#unspecified", FederationErrorCode.Unavailable)]
    [Arguments(412, "#precondition_failed", FederationErrorCode.InconsistentSnapshot)]
    [Arguments(206, "#partial", FederationErrorCode.InconsistentSnapshot)]
    [Arguments(503, "#busy", FederationErrorCode.Unavailable)]
    public async Task HttpStatusAndCoreDetailArePreservedWithoutFallback(int status, string type, FederationErrorCode code)
    {
        var paths = new List<string>();
        var requests = 0;
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/main/assets/item$details", () =>
            {
                requests++;
                return Results.Text($$"""{"type":"https://example.test/core{{type}}","detail":"source detail"}""",
                    "application/problem+json", statusCode: status);
            });
        });
        using var source = await HttpFederationReadSource.OpenAsync(
            new Uri(new Uri(app.Urls.Single()), "/mount/"), new() { AllowLoopbackHttp = true });
        var error = await FailureAsync(() => source.ReadAsync(new(FederationOperation.Entity,
            "/documents/main/assets/item", representation: FederationRepresentation.ApiView)));
        await Assert.That(error.Code).IsEqualTo(code);
        await Assert.That(error.HttpStatusCode).IsEqualTo((int?)status);
        await Assert.That(error.HttpProblemDetails.GetProperty("detail").GetString()).IsEqualTo("source detail");
        await Assert.That(requests).IsEqualTo(1);
    }

    [Test]
    [Arguments("ambiguous", FederationErrorCode.Ambiguous)]
    [Arguments("missing", FederationErrorCode.NotFound)]
    [Arguments("later-failure", FederationErrorCode.InconsistentSnapshot)]
    [Arguments("duplicate", FederationErrorCode.InconsistentSnapshot)]
    [Arguments("wrong-xid", FederationErrorCode.InvalidPackage)]
    public async Task CollectionFailuresCannotTurnIntoFirstPageSuccess(string outcome, FederationErrorCode expected)
    {
        var paths = new List<string>();
        var requests = 0;
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/main/assets", (HttpContext context) =>
            {
                requests++;
                if (requests == 1)
                {
                    context.Response.Headers.Link = "<?page=two>;rel=next;count=2";
                    return Json("""{"one":{"xid":"/documents/main/assets/one","labels":{"stage":"production"}}}""");
                }
                return outcome switch
                {
                    "later-failure" => Results.StatusCode(503),
                    "duplicate" => Json("""{"one":{"xid":"/documents/main/assets/one","labels":{"stage":"production"}}}"""),
                    "wrong-xid" => Json("""{"two":{"xid":"/documents/main/assets/different"}}"""),
                    _ => Json("""{"two":{"xid":"/documents/main/assets/two","labels":{"stage":"PRODUCTION"}}}""")
                };
            });
        });
        using var source = await HttpFederationReadSource.OpenAsync(
            new Uri(new Uri(app.Urls.Single()), "/mount/"), new() { AllowLoopbackHttp = true });
        var error = await FailureAsync(() => source.ReadAsync(new(FederationOperation.Collection,
            "/documents/main/assets", new("stage", outcome == "missing" ? "none" : "production"), FederationRepresentation.ApiView)));
        await Assert.That(error.Code).IsEqualTo(expected);
        await Assert.That(requests).IsEqualTo(2);
    }

    [Test]
    public async Task DocumentViewPointersAreRebasedOnlyOnKnownMetadataNavigation()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/main/assets/item$details", (HttpContext context) =>
            {
                paths.Add(context.Request.Path + context.Request.QueryString);
                return Json("""{"xid":"/documents/main/assets/item","self":"#","metaurl":"#/meta","versionsurl":"#/versions","description":"#untouched","asset":{"self":"literal"},"meta":{"xid":"/documents/main/assets/item/meta","self":"#/meta","defaultversionurl":"#/versions/v1"},"versions":{"v1":{"xid":"/documents/main/assets/item/versions/v1","self":"#/versions/v1"}}}""");
            });
        });
        using var source = await HttpFederationReadSource.OpenAsync(
            new Uri(new Uri(app.Urls.Single()), "/mount/"), new() { AllowLoopbackHttp = true });
        var result = await source.ReadAsync(new(FederationOperation.Entity, "/documents/main/assets/item"));
        var entity = result.Metadata.GetProperty("entity");
        await Assert.That(paths[^1]).IsEqualTo("/mount/documents/main/assets/item$details?doc");
        await Assert.That(entity.GetProperty("self").GetString()).IsEqualTo("#/entity");
        await Assert.That(entity.GetProperty("meta").GetProperty("defaultversionurl").GetString()).IsEqualTo("#/entity/versions/v1");
        await Assert.That(entity.GetProperty("versions").GetProperty("v1").GetProperty("self").GetString()).IsEqualTo("#/entity/versions/v1");
        await Assert.That(entity.GetProperty("description").GetString()).IsEqualTo("#untouched");
        await Assert.That(entity.GetProperty("asset").GetProperty("self").GetString()).IsEqualTo("literal");
    }

    [Test]
    public async Task ChangedDefaultInvalidatesDependentDocumentCapture()
    {
        var paths = new List<string>();
        var metaRequests = 0;
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/main/assets/item/meta", () =>
            {
                metaRequests++;
                return Json($$"""{"xid":"/documents/main/assets/item/meta","defaultversionid":"{{(metaRequests == 1 ? "v1" : "v2")}}"}""");
            });
            app.MapGet("/mount/documents/main/assets/item/versions/v1", () => Results.Bytes([1, 2, 3]));
        });
        using var source = await HttpFederationReadSource.OpenAsync(
            new Uri(new Uri(app.Urls.Single()), "/mount/"), new() { AllowLoopbackHttp = true });
        var error = await FailureAsync(() => source.ReadAsync(new(FederationOperation.Document, "/documents/main/assets/item")));
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(metaRequests).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SameRegistryAliasesUseAtMostOneModelCheckedHop(bool secondHop)
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/main/assets/alias/meta", () =>
                Json("""{"xid":"/documents/main/assets/alias/meta","xref":"/documents/main/assets/item"}"""));
            app.MapGet("/mount/documents/main/assets/item/meta", () => Json(secondHop
                ? """{"xid":"/documents/main/assets/item/meta","xref":"/documents/main/assets/third"}"""
                : """{"xid":"/documents/main/assets/item/meta","defaultversionid":"v1"}"""));
            app.MapGet("/mount/documents/main/assets/item/versions/v1", () => Results.Bytes([4, 3, 2, 1]));
        });
        using var source = await HttpFederationReadSource.OpenAsync(
            new Uri(new Uri(app.Urls.Single()), "/mount/"), new() { AllowLoopbackHttp = true });
        if (secondHop)
        {
            var error = await FailureAsync(() => source.ReadAsync(new(FederationOperation.Document, "/documents/main/assets/alias")));
            await Assert.That(error.Code).IsEqualTo(FederationErrorCode.NotFound);
        }
        else
        {
            var result = await source.ReadAsync(new(FederationOperation.Document, "/documents/main/assets/alias"));
            await Assert.That(result.SelectedXid).IsEqualTo("/documents/main/assets/item/versions/v1");
            using var stream = result.Document!.OpenRead();
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes);
            await Assert.That(Convert.ToHexString(bytes.ToArray())).IsEqualTo("04030201");
        }
        await Assert.That(source.Observations.Any(observation => observation.RequestUri.AbsolutePath.Contains("third", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task EmptyVersionDocumentIsPresentAndIndependentOfTheSourceLifetime()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/main/assets/item/versions/v1", () => Results.Bytes([], "application/octet-stream"));
        });
        using var source = await HttpFederationReadSource.OpenAsync(
            new Uri(new Uri(app.Urls.Single()), "/mount/"), new() { AllowLoopbackHttp = true });
        var result = await source.ReadAsync(new(FederationOperation.Document, "/documents/main/assets/item/versions/v1"));
        source.Dispose();
        await Assert.That(result.Document!.Length).IsEqualTo(0L);
        using var stream = result.Document.OpenRead();
        await Assert.That(stream.ReadByte()).IsEqualTo(-1);
        await Assert.That(result.ExternalDocument.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task CumulativeRequestLimitStopsBeforeAnotherSourceRequest()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app => Bootstrap(app, paths));
        using var source = await HttpFederationReadSource.OpenAsync(
            new Uri(new Uri(app.Urls.Single()), "/mount/"), new() { AllowLoopbackHttp = true },
            new() { Limits = new FederationReadLimits(maxRequests: 4) });
        var error = await FailureAsync(() => source.ReadAsync(new(FederationOperation.Document, "/documents/main/assets/item")));
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(paths.Count).IsEqualTo(4);
    }

    [Test]
    public async Task StrongSnapshotRequirementFailsWithoutContactingLiveHttp()
    {
        FederationException? error = null;
        try
        {
            using var source = await HttpFederationReadSource.OpenAsync(
                new Uri("https://example.invalid/"), options: new() { RequireImmutableSnapshot = true });
        }
        catch (FederationException exception)
        {
            error = exception;
        }
        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(error.HttpStatusCode).IsNull();
    }

    [Test]
    public async Task DocumentCollectionPointersKeepTheirOriginalMemberPrefixExactlyOnce()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/main/assets", () =>
                Json("""
                    {"one":{"xid":"/documents/main/assets/one","self":"#/one",
                     "meta":{"xid":"/documents/main/assets/one/meta","self":"#/one/meta","defaultversionid":"v1"},
                     "versions":{"v1":{"xid":"/documents/main/assets/one/versions/v1","self":"#/one/versions/v1","versionid":"v1","labels":{"stage":"prod"}}}}}
                    """));
        });
        using var source = await HttpFederationReadSource.OpenAsync(
            new Uri(new Uri(app.Urls.Single()), "/mount/"), new() { AllowLoopbackHttp = true });
        var collection = await source.ReadAsync(new(FederationOperation.Collection, "/documents/main/assets"));
        var selected = await source.ReadAsync(new(FederationOperation.Collection, "/documents/main/assets", new("stage", "prod")));
        await Assert.That(collection.Metadata.GetProperty("entities").GetProperty("one").GetProperty("self").GetString())
            .IsEqualTo("#/entities/one");
        await Assert.That(selected.Metadata.GetProperty("entity").GetProperty("self").GetString()).IsEqualTo("#/entity");
    }

    [Test]
    public async Task IgnoredDocumentViewCannotBeAcceptedAsDocumentPointers()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/main/assets/item$details", () =>
                Json("""{"xid":"/documents/main/assets/item","self":"https://example.test/api"}"""));
        });
        using var source = await HttpFederationReadSource.OpenAsync(
            new Uri(new Uri(app.Urls.Single()), "/mount/"), new() { AllowLoopbackHttp = true });
        var error = await FailureAsync(() => source.ReadAsync(new(FederationOperation.Entity, "/documents/main/assets/item")));
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
    }

    internal static async Task<FederationException> FailureAsync(Func<ValueTask<FederationReadResult>> action)
    {
        try
        {
            await action();
        }
        catch (FederationException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Expected a failed federation operation.");
    }

    internal static void Bootstrap(WebApplication app, List<string> paths, string version = "1.0-rc4",
        string capabilities = """{"available":{"entities":{"mutable":false},"model":{"mutable":false},"modelsource":{"mutable":false}},"flags":["doc"],"pagination":false}""",
        string? capturedModelSource = null)
    {
        app.MapGet("/mount/", (HttpContext context) =>
        {
            paths.Add(context.Request.Path);
            return Json($$"""{"registryid":"source","specversion":"{{version}}","xid":"/","epoch":1}""");
        });
        app.MapGet("/mount/model", (HttpContext context) =>
        {
            paths.Add(context.Request.Path);
            return Json(RegistryModel.Compile(RegistryJson.Parse(ModelSource)).EffectiveModel.RootElement.GetRawText());
        });
        app.MapGet("/mount/modelsource", (HttpContext context) =>
        {
            paths.Add(context.Request.Path);
            return Json(capturedModelSource ?? ModelSource);
        });
        app.MapGet("/mount/capabilities", (HttpContext context) =>
        {
            paths.Add(context.Request.Path);
            return Json(capabilities);
        });
    }

    internal static IResult Json(string json) => Results.Text(json, "application/json");

    internal static async Task<WebApplication> StartAsync(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var started = false;
        try
        {
            configure(app);
            await app.StartAsync();
            started = true;
            return app;
        }
        finally
        {
            if (!started)
            {
                await app.DisposeAsync();
            }
        }
    }
}
