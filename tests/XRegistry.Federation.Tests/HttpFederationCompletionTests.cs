// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Federation.Tests.HttpFederationReadSourceTests;

namespace XRegistry.Federation.Tests;

public class HttpFederationCompletionTests
{
    [Test]
    public async Task EnabledFlagNamesRetainCoreCaseInsensitiveSemantics()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths, capabilities: """{"available":{"modelsource":{}},"flags":["DOC"]}""");
            app.MapGet("/mount/documents/g/assets/a$details", () => Json("""
                {"xid":"/documents/g/assets/a","assetid":"a","self":"#"}
                """));
        });
        using var source = await HttpFederationReadSource.OpenAsync(new Uri(new Uri(app.Urls.Single()), "/mount/"),
            new() { AllowLoopbackHttp = true });
        var result = await source.ReadAsync(new(FederationOperation.Entity, "/documents/g/assets/a"));
        await Assert.That(result.Metadata.GetProperty("entity").GetProperty("self").GetString()).IsEqualTo("#/entity");
    }

    [Test]
    public async Task CaseCollidingCollectionMembersAreNotACompleteCoreCollection()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/g/assets", () => Json("""
                {"a":{"xid":"/documents/g/assets/a","assetid":"a"},
                 "A":{"xid":"/documents/g/assets/A","assetid":"A"}}
                """));
        });
        using var source = await HttpFederationReadSource.OpenAsync(new Uri(new Uri(app.Urls.Single()), "/mount/"),
            new() { AllowLoopbackHttp = true });
        var error = await FailureAsync(() => source.ReadAsync(new(FederationOperation.Collection,
            "/documents/g/assets", representation: FederationRepresentation.ApiView)));
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task DocumentViewLabelSelectionUsesTheExplicitDefaultVersionMetadata()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/g/assets", () => Json("""
                {"a":{"xid":"/documents/g/assets/a","assetid":"a","self":"#/a",
                  "meta":{"xid":"/documents/g/assets/a/meta","assetid":"a","self":"#/a/meta","defaultversionid":"v1"},
                  "versions":{"v1":{"xid":"/documents/g/assets/a/versions/v1","versionid":"v1","self":"#/a/versions/v1","labels":{"stage":"production"}},
                              "v2":{"xid":"/documents/g/assets/a/versions/v2","versionid":"v2","self":"#/a/versions/v2","labels":{"stage":"development"}}}}}
                """));
        });
        using var source = await HttpFederationReadSource.OpenAsync(new Uri(new Uri(app.Urls.Single()), "/mount/"),
            new() { AllowLoopbackHttp = true });
        var result = await source.ReadAsync(new(FederationOperation.Collection, "/documents/g/assets", new("stage", "PRODUCTION")));
        await Assert.That(result.SelectedXid).IsEqualTo("/documents/g/assets/a");
        await Assert.That(result.Metadata.GetProperty("entity").GetProperty("self").GetString()).IsEqualTo("#/entity");
        await Assert.That(result.Metadata.GetProperty("entity").GetProperty("versions").GetProperty("v1").GetProperty("labels")
            .GetProperty("stage").GetString()).IsEqualTo("production");
    }

    [Test]
    public async Task DocumentVersionHeadersAreDecodedBeforeComparingCoreIdentity()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/g/assets/a/versions/v:1", (HttpContext context) =>
            {
                context.Response.Headers["xRegistry-versionid"] = "%76%3A1";
                return Results.Bytes(new byte[] { 0, 255 }, "application/octet-stream");
            });
        });
        using var source = await HttpFederationReadSource.OpenAsync(new Uri(new Uri(app.Urls.Single()), "/mount/"),
            new() { AllowLoopbackHttp = true });
        var result = await source.ReadAsync(new(FederationOperation.Document, "/documents/g/assets/a/versions/v%3A1"));
        await Assert.That(result.SelectedXid).IsEqualTo("/documents/g/assets/a/versions/v%3A1");
        await Assert.That(await DirectoryMappingTests.ReadHex(result.Document!)).IsEqualTo("00FF");
    }

    [Test]
    public async Task APreviouslySelectedDefaultCannotBecomeASuccessfulAbsentCapture()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/g/assets/a/meta", () => Json("""
                {"xid":"/documents/g/assets/a/meta","defaultversionid":"v1"}
                """));
            app.MapGet("/mount/documents/g/assets/a/versions/v1", () => Results.Text(
                """{"type":"https://example.test/core#not_found","detail":"removed"}""", "application/problem+json", statusCode: 404));
        });
        using var source = await HttpFederationReadSource.OpenAsync(new Uri(new Uri(app.Urls.Single()), "/mount/"),
            new() { AllowLoopbackHttp = true });
        var error = await FailureAsync(() => source.ReadAsync(new(FederationOperation.Document, "/documents/g/assets/a")));
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(error.HttpStatusCode).IsEqualTo(404);
        await Assert.That(error.HttpProblemDetails.GetProperty("detail").GetString()).IsEqualTo("removed");
    }

    [Test]
    public async Task EnabledCapabilitiesReadsHonorTheSameResultBudgetAsOtherMetadata()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app => Bootstrap(app, paths));
        using var source = await HttpFederationReadSource.OpenAsync(new Uri(new Uri(app.Urls.Single()), "/mount/"),
            new() { AllowLoopbackHttp = true }, new() { Limits = new(maxResultBytes: 1) });
        var before = paths.Count;
        var error = await FailureAsync(() => source.ReadAsync(new(FederationOperation.Capabilities, "/")));
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(paths.Count).IsEqualTo(before);
    }

    [Test]
    [Arguments("%GG", FederationErrorCode.InvalidPackage)]
    [Arguments("other", FederationErrorCode.InconsistentSnapshot)]
    public async Task InvalidOrMismatchedVersionHeadersFailWithStructuredOutcomes(string header, FederationErrorCode expected)
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/g/assets/a/versions/v1", (HttpContext context) =>
            {
                context.Response.Headers["xRegistry-versionid"] = header;
                return Results.Bytes(new byte[] { 0, 255 }, "application/octet-stream");
            });
        });
        using var source = await HttpFederationReadSource.OpenAsync(new Uri(new Uri(app.Urls.Single()), "/mount/"),
            new() { AllowLoopbackHttp = true });
        var error = await FailureAsync(() => source.ReadAsync(new(FederationOperation.Document, "/documents/g/assets/a/versions/v1")));
        await Assert.That(error.Code).IsEqualTo(expected);
    }

    [Test]
    [Arguments("v1", true)]
    [Arguments("v2", false)]
    public async Task UninlinedDocumentViewLabelsRequireMetadataOnlyAcquisitionAtTheSameDefault(string returnedVersion, bool matches)
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/g/assets", () => Json("""
                {"a":{"xid":"/documents/g/assets/a","self":"#/a","meta":{"defaultversionid":"v1","self":"#/a/meta"}}}
                """));
            app.MapGet("/mount/documents/g/assets/a$details", (HttpContext context) =>
            {
                paths.Add(context.Request.Path);
                return Json($$$"""{"xid":"/documents/g/assets/a","versionid":"{{{returnedVersion}}}","labels":{"stage":"prod"}}""");
            });
            app.MapGet("/mount/documents/g/assets/a", (Func<IResult>)(() =>
                throw new InvalidOperationException("Selection must not acquire domain bytes.")));
        });
        using var source = await HttpFederationReadSource.OpenAsync(new Uri(new Uri(app.Urls.Single()), "/mount/"),
            new() { AllowLoopbackHttp = true });
        var request = new FederationReadRequest(FederationOperation.Collection, "/documents/g/assets", new("stage", "prod"));
        if (matches)
        {
            var result = await source.ReadAsync(request);
            await Assert.That(result.Metadata.GetProperty("entity").GetProperty("self").GetString()).IsEqualTo("#/entity");
            await Assert.That(result.Metadata.GetProperty("entity").TryGetProperty("labels", out _)).IsFalse();
        }
        else
        {
            var error = await FailureAsync(() => source.ReadAsync(request));
            await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        }
        await Assert.That(paths.Count(path => path == "/mount/documents/g/assets/a$details")).IsEqualTo(1);
    }

    [Test]
    public async Task UnresolvedModelIncludesAreAnExplicitUnsupportedCapabilityRatherThanMalformedData()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app => Bootstrap(app, paths,
            capturedModelSource: """{"$include":"https://models.example/complete"}"""));
        var error = await Assert.That(async () =>
        {
            using var source = await HttpFederationReadSource.OpenAsync(new Uri(new Uri(app.Urls.Single()), "/mount/"),
                new() { AllowLoopbackHttp = true });
        }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected explicit unresolved model failure.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(paths.Count).IsEqualTo(4);
    }

    [Test]
    public async Task ModelIncludesUseOnlyTheExplicitResolverAndShareTheSessionBudgets()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app => Bootstrap(app, paths,
            capturedModelSource: """{"$include":"https://models.example/complete"}"""));
        var resolver = new CapturedModelResolver();
        using var source = await HttpFederationReadSource.OpenAsync(new Uri(new Uri(app.Urls.Single()), "/mount/"),
            new() { AllowLoopbackHttp = true }, new() { ModelResolver = resolver });
        await Assert.That(resolver.Calls).IsEqualTo(1);
        await Assert.That(resolver.Requested!.AbsoluteUri).IsEqualTo("https://models.example/complete");
        await Assert.That(source.Model.Groups["documents"].Resources["assets"].HasDocument).IsTrue();
        await Assert.That(source.Model.Source.RootElement.GetProperty("$include").GetString()).IsEqualTo("https://models.example/complete");
        await Assert.That(paths.Count).IsEqualTo(4);

        var limited = new CapturedModelResolver();
        var failure = await Assert.That(async () =>
        {
            using var rejected = await HttpFederationReadSource.OpenAsync(new Uri(new Uri(app.Urls.Single()), "/mount/"),
                new() { AllowLoopbackHttp = true }, new() { ModelResolver = limited, Limits = new(maxRequests: 4) });
        }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected the shared request budget to reject.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(limited.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, FederationErrorCode.Unavailable)]
    [Arguments(true, FederationErrorCode.InconsistentSnapshot)]
    public async Task InterruptedDocumentReadsDistinguishExplicitRequestsFromEstablishedCaptures(bool capture, FederationErrorCode expected)
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/g/assets/a/meta", () => Json("""
                {"xid":"/documents/g/assets/a/meta","defaultversionid":"v1"}
                """));
            app.MapGet("/mount/documents/g/assets/a/versions/v1", (HttpContext context) =>
            {
                context.Abort();
                return Task.CompletedTask;
            });
        });
        using var source = await HttpFederationReadSource.OpenAsync(new Uri(new Uri(app.Urls.Single()), "/mount/"),
            new() { AllowLoopbackHttp = true });
        var error = await FailureAsync(() => source.ReadAsync(new(FederationOperation.Document,
            capture ? "/documents/g/assets/a" : "/documents/g/assets/a/versions/v1")));
        await Assert.That(error.Code).IsEqualTo(expected);
    }

    [Test]
    public async Task NotModifiedWithoutAStoredRepresentationIsAnIncompleteCapture()
    {
        var paths = new List<string>();
        await using var app = await StartAsync(app =>
        {
            Bootstrap(app, paths);
            app.MapGet("/mount/documents/g/assets/a$details", () => Results.StatusCode(304));
        });
        using var source = await HttpFederationReadSource.OpenAsync(new Uri(new Uri(app.Urls.Single()), "/mount/"),
            new() { AllowLoopbackHttp = true });
        var error = await FailureAsync(() => source.ReadAsync(new(FederationOperation.Entity, "/documents/g/assets/a",
            representation: FederationRepresentation.ApiView)));
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(error.HttpStatusCode).IsEqualTo(304);
    }

    private sealed class CapturedModelResolver : IRegistryModelResolver
    {
        internal int Calls { get; private set; }
        internal Uri? Requested { get; private set; }

        public RegistryJson Resolve(Uri documentUri)
        {
            Calls++;
            Requested = documentUri;
            return RegistryJson.Parse(ModelSource);
        }
    }
}
