// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.AspNetCore;
using XRegistry.Client;
using XRegistry.Federation;
using XRegistry.Models;
using XRegistry.Server;
using XRegistry.Storage.File;
using XRegistry.Storage.File.Tests;

namespace XRegistry.Http.Tests;

public class WotIncrementalHttpWorkflowTests
{
    private const string Identity = "urn:example:pump";
    private const string Line = "urn:example:line:a";
    private const string OtherLine = "urn:example:line:b";
    private const string Generated = "{\r\n  \"id\": \"urn:example:pump\", \"value\": 42\r\n}\r\n";
    private static readonly RegistryModel Model = WotRegistryProfiles.Compile(WotVersionProfile.Incremental);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LinelessHttpPublicationLogsAnAdvisoryAfterCommitWithoutAddingWireMetadata(bool description)
    {
        var store = new InMemoryRegistryPersistence();
        await using var app = await StartAsync(new(), store);
        var logs = new NoticeLoggerProvider
        {
            ObserveGeneration = () =>
            {
                using var observed = store.ReadSnapshotAsync().AsTask().GetAwaiter().GetResult();
                return observed.Generation;
            }
        };
        app.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        using var client = Client(app);
        await PublishAsync(client, description, Generated, null, "1", HttpMethod.Put);
        var log = logs.Entries.Single();
        await Assert.That(log.Category).IsEqualTo("XRegistry.AspNetCore");
        await Assert.That(log.Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(log.Event.Name).IsEqualTo("RegistryOperationNotice");
        await Assert.That(log.Generation).IsEqualTo(1L);
        await Assert.That(log.Fields["Code"] as string).IsEqualTo("wot-line-ineligible");
        await Assert.That(log.Fields["RegistryPath"] as string).IsEqualTo("/" + Resource(description) + "/versions/1");
        await Assert.That(log.Fields["Message"] as string)
            .IsEqualTo("This revision is not eligible for automatic compatibility-line selection.");
        using var snapshot = await store.ReadSnapshotAsync();
        await Assert.That(snapshot.Find("$wotalloc/" + Resource(description))!.Metadata.RootElement
            .GetProperty("highwater").GetString()).IsEqualTo("1");
        using (var read = await client.SendAsync(HttpMethod.Get, Resource(description) + "/versions/1"))
        {
            using var bytes = new MemoryStream();
            await read.CopyDocumentToAsync(bytes);
            await Assert.That(Encoding.UTF8.GetString(bytes.ToArray())).IsEqualTo(Generated);
            await Assert.That(read.ReadHeaderMetadata(Definition(description)).RootElement
                .TryGetProperty("notices", out _)).IsFalse();
        }
        using var source = await HttpFederationReadSource.OpenAsync(client.Root, new() { AllowLoopbackHttp = true });
        var pinned = await WotRegistryResolver.ResolveAsync(source, Collection(description),
            Request(description) with { VersionIdPin = "1" });
        await Assert.That(pinned.Version.VersionId).IsEqualTo("1");
        await Assert.That(pinned.Version.CompatibilityLine).IsNull();
        await Assert.That(pinned.Version.ContentDigest).IsEqualTo(Digest(Generated));
        var failure = await Assert.That(async () => await WotRegistryResolver.ResolveAsync(source,
            Collection(description), Request(description))).Throws<RegistryException>()
            ?? throw new InvalidOperationException("A lineless revision must not satisfy line selection.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("unsatisfied");
        await PublishAsync(client, description, Generated, Line, "2", HttpMethod.Post);
        using var rejected = await UploadAsync(client, description, Generated, "relative", HttpMethod.Post);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(logs.Entries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RejectedPersistencePreparationDoesNotLogAProposedLinelessPublicationNotice()
    {
        var store = new ConflictingPublicationPersistence();
        await using var app = await StartAsync(new(), store);
        var logs = new NoticeLoggerProvider();
        app.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        using var client = Client(app);
        using var response = await UploadAsync(client, false, Generated, null, HttpMethod.Put);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var problem = await response.ReadMetadataAsync();
        await Assert.That(problem.RootElement.GetProperty("code").GetString()).IsEqualTo("server_busy");
        await Assert.That(store.Prepared).IsTrue();
        await Assert.That(logs.Entries.Count).IsEqualTo(0);
        using var snapshot = await store.ReadSnapshotAsync();
        await Assert.That(snapshot.Generation).IsEqualTo(0L);
    }

    [Test]
    [Arguments(false, "preserved")]
    [Arguments(true, "preserved")]
    [Arguments(false, "resource")]
    [Arguments(true, "resource")]
    [Arguments(false, "group")]
    [Arguments(true, "group")]
    public async Task DurableHttpPublicationAndResolutionRetainAllocationAcrossDeletionAndSeparateAdapterRestarts(
        bool description, string history)
    {
        const string authoredId = "184467440737095516160001";
        const string firstGenerated = "184467440737095516160002";
        const string secondGenerated = "184467440737095516160003";
        using var directory = new TestDirectory();
        using (var store = LocalFileStore.Initialize(directory.Path))
        using (var persistence = new LocalRegistryPersistence(store))
        {
            await using var app = await StartAsync(new(), persistence);
            using var client = Client(app);
            await PublishAsync(client, description, Authored(description, authoredId), Line, authoredId, HttpMethod.Put);
            using var source = await HttpFederationReadSource.OpenAsync(client.Root, new() { AllowLoopbackHttp = true });
            var original = await WotRegistryResolver.ResolveAsync(source, Collection(description), Request(description));
            await Assert.That(original.Version.VersionId).IsEqualTo(authoredId);
            await Assert.That(original.Version.ContentDigest).IsEqualTo(Digest(Authored(description, authoredId)));
            if (history != "preserved")
            {
                var target = history == "resource" ? Resource(description) :
                    description ? "thingdescriptiongroups/g" : "thingmodelgroups/g";
                using var deletion = await client.SendAsync(HttpMethod.Delete, target);
                await Assert.That(deletion.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }
        }
        using (var store = LocalFileStore.Open(directory.Path))
        using (var persistence = new LocalRegistryPersistence(store))
        {
            await using var app = await StartAsync(new(), persistence);
            using var client = Client(app);
            await PublishAsync(client, description, Generated, Line, firstGenerated, HttpMethod.Post);
            using var source = await HttpFederationReadSource.OpenAsync(client.Root, new() { AllowLoopbackHttp = true });
            var selected = await WotRegistryResolver.ResolveAsync(source, Collection(description),
                Request(description) with { MinimumVersion = firstGenerated });
            await Assert.That(selected.Version.VersionId).IsEqualTo(firstGenerated);
            await Assert.That(selected.Version.Profile).IsEqualTo(WotVersionProfile.Incremental);
            await Assert.That(selected.Version.CompatibilityLine).IsEqualTo(Line);
            await Assert.That(selected.Version.ContentDigest).IsEqualTo(Digest(Generated));
            await Assert.That(selected.Version.Origin).IsEqualTo(client.Root.AbsoluteUri);
            var pin = Request(description) with { VersionIdPin = authoredId };
            if (history == "preserved")
            {
                var retained = await WotRegistryResolver.ResolveAsync(source, Collection(description), pin);
                await Assert.That(retained.Version.ContentDigest).IsEqualTo(Digest(Authored(description, authoredId)));
            }
            else
            {
                var failure = await Assert.That(async () => await WotRegistryResolver.ResolveAsync(source,
                    Collection(description), pin)).Throws<RegistryException>()
                    ?? throw new InvalidOperationException("The deleted revision must remain absent.");
                await Assert.That(failure.Diagnostic.Code).IsEqualTo("not-found");
            }
        }
        using (var store = LocalFileStore.Open(directory.Path))
        using (var persistence = new LocalRegistryPersistence(store))
        {
            await using var app = await StartAsync(new(), persistence);
            using var client = Client(app);
            await PublishAsync(client, description, Generated, Line, secondGenerated, HttpMethod.Post);
            using var source = await HttpFederationReadSource.OpenAsync(client.Root, new() { AllowLoopbackHttp = true });
            var repeated = await WotRegistryResolver.ResolveAsync(source, Collection(description), Request(description));
            await Assert.That(repeated.Version.VersionId).IsEqualTo(secondGenerated);
            await Assert.That(repeated.Version.CompatibilityLine).IsEqualTo(Line);
            await Assert.That(repeated.Version.ContentDigest).IsEqualTo(Digest(Generated));
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PublishedHttpRevisionsResolveByExactLineAndRetainTheirBytesAndLocalIdentity(bool description)
    {
        var documents = new ConcurrentQueue<string>();
        await using var app = await StartAsync(documents);
        using var client = Client(app);
        var path = Resource(description);
        await SeedAsync(client, description);
        var budget = new FederationReadBudget();
        using var source = await HttpFederationReadSource.OpenAsync(client.Root,
            new() { AllowLoopbackHttp = true }, null, budget);
        await Assert.That(WotRegistryProfiles.Read(source.Model)).IsEqualTo(WotVersionProfile.Incremental);
        var selected = await WotRegistryResolver.ResolveAsync(source, Collection(description),
            Request(description) with { MinimumVersion = "10" }, budget);
        await Assert.That(selected.Version.VersionId).IsEqualTo("10");
        await Assert.That(selected.Version.Profile).IsEqualTo(WotVersionProfile.Incremental);
        await Assert.That(selected.Version.CompatibilityLine).IsEqualTo(Line);
        await Assert.That(selected.Version.Origin).IsEqualTo(client.Root.AbsoluteUri);
        await Assert.That(selected.Version.ContentDigest).IsEqualTo(Digest(Generated));
        await Assert.That(selected.ResourceXid).IsEqualTo("/" + path);
        using var bytes = selected.Document.OpenRead();
        using var output = new MemoryStream();
        await bytes.CopyToAsync(output);
        await Assert.That(output.ToArray().SequenceEqual(Encoding.UTF8.GetBytes(Generated))).IsTrue();
        await Assert.That(string.Join(",", documents)).IsEqualTo("/registry/" + path + "/versions/10");

        var requests = source.Observations.Count;
        var chargedBytes = budget.BytesRead;
        source.Dispose();
        await app.StopAsync();
        var local = await WotRegistryResolver.ResolveLocalAsync(selected,
            Request(description) with { IsDocumentLocal = true, MinimumVersion = "10" }, budget);
        var fragment = await WotRegistryResolver.SelectFragmentAsync(local, "#/value");
        await Assert.That(ReferenceEquals(local, selected)).IsTrue();
        await Assert.That(fragment.Value.GetInt32()).IsEqualTo(42);
        await Assert.That(fragment.ContainingDocument.Version).IsEqualTo(selected.Version);
        await Assert.That(budget.BytesRead).IsEqualTo(chargedBytes);
        await Assert.That(source.Observations.Count).IsEqualTo(requests);
        await Assert.That(documents.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HttpStickyFailureCannotFallForwardAndRawPinsRetainTheirActualLines(bool description)
    {
        var documents = new ConcurrentQueue<string>();
        await using var app = await StartAsync(documents);
        using var client = Client(app);
        await SeedAsync(client, description);
        using (var update = await client.SendAsync(HttpMethod.Patch, Resource(description) + "/meta",
            Encoding.UTF8.GetBytes("""{"defaultversionid":"9","defaultversionsticky":true}"""), "application/json"))
        {
            await Assert.That(update.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        using var source = await HttpFederationReadSource.OpenAsync(client.Root, new() { AllowLoopbackHttp = true });
        var failure = await Assert.That(async () => await WotRegistryResolver.ResolveAsync(source,
            Collection(description), Request(description) with { MinimumVersion = "10" })).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a sticky constraint failure.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("unsatisfied");
        await Assert.That(documents.Count).IsEqualTo(0);

        var invalidSelectors = Request(description) with { MinimumVersion = "not numeric", CompatibilityLine = "relative" };
        var exact = await WotRegistryResolver.ResolveAsync(source, Collection(description),
            invalidSelectors with { VersionIdPin = "9" });
        await Assert.That(exact.Version.VersionId).IsEqualTo("9");
        await Assert.That(exact.Version.CompatibilityLine).IsEqualTo(Line);
        await Assert.That(exact.Version.ContentDigest).IsEqualTo(Digest(Authored(description)));
        await Assert.That(string.Join(",", documents)).IsEqualTo("/registry/" + Resource(description) + "/versions/9");
        documents.Clear();
        var digest = await WotRegistryResolver.ResolveAsync(source, Collection(description),
            invalidSelectors with { DigestPin = Digest(Generated) });
        await Assert.That(digest.Version.VersionId).IsEqualTo("12");
        await Assert.That(digest.Version.CompatibilityLine).IsNull();
        await Assert.That(digest.Version.ContentDigest).IsEqualTo(Digest(Generated));
        await Assert.That(digest.Version.Origin).IsEqualTo(client.Root.AbsoluteUri);
        await Assert.That(documents.Count).IsEqualTo(4);
        await Assert.That(documents.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(4);
    }

    [Test]
    [Arguments(false, "longer")]
    [Arguments(true, "longer")]
    [Arguments(false, "shorter")]
    [Arguments(true, "shorter")]
    [Arguments(false, "different")]
    [Arguments(true, "different")]
    public async Task RejectedHttpWritesCannotAllocateOrReplacePublishedIncrementalRevisions(bool description, string change)
    {
        await using var app = await StartAsync(new());
        using var client = Client(app);
        await PublishAsync(client, description, Authored(description), Line, "9", HttpMethod.Put);
        var originalBytes = Authored(description);
        var replacement = change switch
        {
            "longer" => originalBytes + " ",
            "shorter" => originalBytes.Replace(",\"value\":9", "", StringComparison.Ordinal),
            "different" => originalBytes.Replace(Identity, "urn:example:puma", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        using (var changed = await UploadAsync(client, description, replacement, Line,
            HttpMethod.Put, "/versions/9"))
        {
            using var problem = await changed.ReadMetadataAsync();
            await Assert.That(problem.RootElement.GetProperty("code").GetString()).IsEqualTo("invalid_attribute");
            await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(problem.RootElement.GetProperty("args").GetProperty("name").GetString())
                .IsEqualTo(description ? "thingdescription" : "thingmodel");
        }
        using (var invalid = await UploadAsync(client, description, Generated, "relative", HttpMethod.Post))
        {
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
        await PublishAsync(client, description, Generated, Line, "10", HttpMethod.Post);
        using var source = await HttpFederationReadSource.OpenAsync(client.Root, new() { AllowLoopbackHttp = true });
        var original = await WotRegistryResolver.ResolveAsync(source, Collection(description),
            Request(description) with { VersionIdPin = "9" });
        await Assert.That(original.Version.ContentDigest).IsEqualTo(Digest(Authored(description)));
        var next = await WotRegistryResolver.ResolveAsync(source, Collection(description), Request(description));
        await Assert.That(next.Version.VersionId).IsEqualTo("10");
        await Assert.That(next.Version.ContentDigest).IsEqualTo(Digest(Generated));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExactHttpReplayRetainsItsRevisionAndDoesNotConsumeTheNextAllocation(bool description)
    {
        await using var app = await StartAsync(new());
        using var client = Client(app);
        await PublishAsync(client, description, Authored(description), Line, "9", HttpMethod.Put);
        using (var replay = await UploadAsync(client, description, Authored(description), Line,
            HttpMethod.Put, "/versions/9"))
        {
            await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var metadata = replay.ReadHeaderMetadata(Definition(description));
            await Assert.That(metadata.RootElement.GetProperty("versionid").GetString()).IsEqualTo("9");
            await Assert.That(metadata.RootElement.GetProperty("versionorigin").GetString()).IsEqualTo("authored");
            await Assert.That(metadata.RootElement.GetProperty("compatibilityline").GetString()).IsEqualTo(Line);
        }
        await PublishAsync(client, description, Generated, Line, "10", HttpMethod.Post);
    }

    [Test]
    [Arguments(false, "relative-line")]
    [Arguments(true, "relative-line")]
    [Arguments(false, "fixed-line")]
    [Arguments(true, "fixed-line")]
    [Arguments(false, "compact-line")]
    [Arguments(true, "compact-line")]
    [Arguments(false, "conflicting-line")]
    [Arguments(true, "conflicting-line")]
    [Arguments(false, "reused-authored")]
    [Arguments(true, "reused-authored")]
    [Arguments(false, "invalid-id")]
    [Arguments(true, "invalid-id")]
    [Arguments(false, "mismatched-id")]
    [Arguments(true, "mismatched-id")]
    [Arguments(false, "conflicting-id")]
    [Arguments(true, "conflicting-id")]
    public async Task HttpProfileFailuresCarryCoreArgumentsAndLeaveTheNextAllocationAvailable(bool description, string scenario)
    {
        await using var app = await StartAsync(new());
        using var client = Client(app);
        await PublishAsync(client, description, Authored(description), Line, "9", HttpMethod.Put);
        using var rejected = scenario switch
        {
            "relative-line" => await UploadAsync(client, description, Generated, "relative", HttpMethod.Post),
            "fixed-line" => await client.SendAsync(HttpMethod.Patch, Resource(description) + "/versions/9$details",
                Encoding.UTF8.GetBytes("""{"compatibilityline":"urn:example:line:b"}"""), "application/json"),
            "compact-line" => await UploadAsync(client, description,
                """{"id":"urn:example:pump","uav:compatibilityLine":"urn:example:line:a"}""", Line, HttpMethod.Post),
            "conflicting-line" => await UploadAsync(client, description,
                """{"id":"urn:example:pump","http://opcfoundation.org/UA/WoT-Binding/compatibilityLine":"urn:example:line:b"}""",
                Line, HttpMethod.Post),
            "reused-authored" => await UploadAsync(client, description,
                Authored(description).Replace("\"9\"", "\"8\"", StringComparison.Ordinal), Line, HttpMethod.Post),
            "invalid-id" => await UploadAsync(client, description, Generated, Line, HttpMethod.Put, "/versions/01"),
            "mismatched-id" => await UploadAsync(client, description, Generated, Line, HttpMethod.Put, "/versions/77"),
            "conflicting-id" => await UploadAsync(client, description, Generated, Line, HttpMethod.Put, "/versions/77", "78"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        using var problem = await rejected.ReadMetadataAsync();
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var mismatch = scenario is "mismatched-id" or "conflicting-id";
        await Assert.That(problem.RootElement.GetProperty("code").GetString())
            .IsEqualTo(mismatch ? "mismatched_id" : "invalid_attribute");
        var args = problem.RootElement.GetProperty("args");
        if (mismatch)
        {
            await Assert.That(args.GetProperty("singular").GetString()).IsEqualTo("version");
            await Assert.That(args.GetProperty("invalid_id").GetString()).IsEqualTo(scenario == "conflicting-id" ? "78" : "77");
            await Assert.That(args.GetProperty("expected_id").GetString()).IsEqualTo(scenario == "conflicting-id" ? "77" : "10");
        }
        else
        {
            await Assert.That(args.GetProperty("name").GetString()).IsEqualTo(
                scenario.EndsWith("line", StringComparison.Ordinal) ? "compatibilityline" : "versionid");
        }
        await PublishAsync(client, description, Generated, Line, "10", HttpMethod.Post);
    }

    private static async Task SeedAsync(XRegistryHttpClient client, bool description)
    {
        await PublishAsync(client, description, Authored(description), Line, "9", HttpMethod.Put);
        await PublishAsync(client, description, Generated, Line, "10", HttpMethod.Post);
        await PublishAsync(client, description, Generated, OtherLine, "11", HttpMethod.Post);
        await PublishAsync(client, description, Generated, null, "12", HttpMethod.Post);
    }

    private static async Task PublishAsync(XRegistryHttpClient client, bool description, string document,
        string? line, string id, HttpMethod method)
    {
        using var response = await UploadAsync(client, description, document, line, method);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var metadata = response.ReadHeaderMetadata(Definition(description));
        await Assert.That(metadata.RootElement.GetProperty("versionid").GetString()).IsEqualTo(id);
        await Assert.That(metadata.RootElement.GetProperty("wotid").GetString()).IsEqualTo(Identity);
        await Assert.That(metadata.RootElement.TryGetProperty("compatibilityline", out var value)).IsEqualTo(line is not null);
        if (line is not null)
        {
            await Assert.That(value.GetString()).IsEqualTo(line);
        }
    }

    private static async Task<XRegistryHttpResponse> UploadAsync(XRegistryHttpClient client, bool description,
        string document, string? line, HttpMethod method, string suffix = "", string? versionId = null)
    {
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(document));
        var values = new JsonObject { ["contenttype"] = "application/json" };
        if (line is not null) { values["compatibilityline"] = line; }
        if (versionId is not null) { values["versionid"] = versionId; }
        var metadata = RegistryJson.Parse(values.ToJsonString());
        return await client.SendDocumentAsync(method, Resource(description) + suffix, bytes,
            metadata, Definition(description));
    }

    private static async Task<WebApplication> StartAsync(ConcurrentQueue<string> documents,
        IRegistryPersistence? persistence = null)
    {
        var app = await XRegistryHttpClientTests.StartAsync(app =>
        {
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value!;
                if (context.Request.Method == "GET" && path.Contains("/versions/", StringComparison.Ordinal) &&
                    !path.EndsWith("$details", StringComparison.Ordinal))
                {
                    documents.Enqueue(path);
                }
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "wot-workflow")], "test"));
                await next(context);
            });
            app.Map("/starting", (HttpContext context) =>
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return Task.CompletedTask;
            });
            app.UseRouting();
            app.UseEndpoints(static _ => { });
        });
        var transferred = false;
        try
        {
            var engine = new RegistryEngine(new()
            {
                RegistryId = "wot-workflow", PublicRoot = new Uri(new Uri(app.Urls.Single()), "/registry"),
                Model = Model, AllowAnonymousReads = true
            }, persistence ?? new InMemoryRegistryPersistence(), new PermitPolicy());
            app.MapXRegistry(engine, new() { MountPath = "/registry" });
            transferred = true;
            return app;
        }
        finally
        {
            if (!transferred) { await app.DisposeAsync(); }
        }
    }

    private static string Collection(bool description) => description
        ? "/thingdescriptiongroups/g/thingdescriptions" : "/thingmodelgroups/g/thingmodels";
    private static string Resource(bool description) => Collection(description)[1..] + "/pump";
    private static RegistryResourceDefinition Definition(bool description) => description
        ? Model.Groups["thingdescriptiongroups"].Resources["thingdescriptions"]
        : Model.Groups["thingmodelgroups"].Resources["thingmodels"];
    private static string Authored(bool description, string versionId = "9") =>
        """{"id":"urn:example:pump","version":{"FIELD":"REVISION"},"value":9}"""
            .Replace("FIELD", description ? "instance" : "model", StringComparison.Ordinal)
            .Replace("REVISION", versionId, StringComparison.Ordinal);
    private static WotVersionResolutionRequest Request(bool description) => new(Identity,
        description ? WotReferenceTargetKind.ThingDescription : WotReferenceTargetKind.ThingModel,
        description ? WotReferenceOperator.ThingDescription : WotReferenceOperator.ThingModel)
        { CompatibilityLine = Line };
    private static XRegistryHttpClient Client(WebApplication app) =>
        new(new Uri(new Uri(app.Urls.Single()), "/registry/"), new() { AllowLoopbackHttp = true });
    private static string Digest(string document) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document))).ToLowerInvariant();

    private sealed class PermitPolicy : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed record NoticeLog(string Category, LogLevel Level, EventId Event,
        IReadOnlyDictionary<string, object?> Fields, long? Generation);

    private sealed class NoticeLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<NoticeLog> Entries { get; } = new();
        public Func<long>? ObserveGeneration { get; init; }
        public ILogger CreateLogger(string categoryName) => new NoticeLogger(categoryName, Entries, ObserveGeneration);
        public void Dispose() { }
    }

    private sealed class NoticeLogger(string category, ConcurrentQueue<NoticeLog> entries,
        Func<long>? observeGeneration) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (category == "XRegistry.AspNetCore" && eventId.Name == "RegistryOperationNotice" &&
                state is IEnumerable<KeyValuePair<string, object?>> fields)
            {
                entries.Enqueue(new(category, logLevel, eventId, fields.ToDictionary(
                    field => field.Key, field => field.Value, StringComparer.Ordinal), observeGeneration?.Invoke()));
            }
        }
    }

    private sealed class ConflictingPublicationPersistence : IRegistryPersistence
    {
        private readonly InMemoryRegistryPersistence _inner = new();
        public bool IsReadOnly => false;
        public bool Prepared { get; private set; }
        public ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default) =>
            _inner.ReadSnapshotAsync(cancellationToken);
        public ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration,
            IReadOnlyList<RegistryMutation> mutations, CancellationToken cancellationToken = default)
        {
            Prepared = true;
            throw new RegistryConcurrencyException();
        }
    }
}
