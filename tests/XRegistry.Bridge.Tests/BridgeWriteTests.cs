using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Samples.Bridge;

namespace XRegistry.Bridge.Tests;

public class BridgeWriteTests
{
    [Test]
    public async Task OneMountPreservesRawQueryMetadataHeadersBodyStatusAndIsolatesCredentials()
    {
        var hits = 0;
        var credentials = 0;
        string? seenTarget = null, seenHost = null, seenAuthorization = null, seenLabel = null;
        byte[]? seenBody = null;
        var sawCookie = false;
        await using var upstream = await StartUpstreamAsync(async http =>
        {
            Interlocked.Increment(ref hits);
            seenTarget = http.Features.Get<IHttpRequestFeature>()!.RawTarget;
            seenHost = http.Request.Host.Value;
            seenAuthorization = http.Request.Headers.Authorization.ToString();
            seenLabel = http.Request.Headers["xRegistry-labels.note"].ToString();
            sawCookie = http.Request.Headers.ContainsKey("Cookie") || http.Request.Headers.ContainsKey("X-Forwarded-Host");
            using var body = new MemoryStream();
            await http.Request.Body.CopyToAsync(body);
            seenBody = body.ToArray();
            http.Response.StatusCode = 201;
            http.Response.ContentType = "application/octet-stream";
            http.Response.Headers["xRegistry-versionid"] = "new-v";
            http.Response.Headers["xRegistry-fileid"] = "item";
            http.Response.Headers.Location = "/authoritative/dirs/g/files/item/versions/new-v";
            http.Response.Headers.ContentLocation = "/authoritative/dirs/g/files/item/versions/new-v";
            http.Response.Headers.Link = "</authoritative>;rel=xregistry-root";
            await http.Response.Body.WriteAsync(new byte[] { 0, 255, 66 });
        });
        var upstreamRoot = new Uri(new Uri(upstream.Urls.Single()), "/authoritative");
        var mount = new BridgeWriteMount
        {
            Name = "one",
            UpstreamRoot = upstreamRoot,
            Model = BridgeFixture.Model,
            AllowLoopbackHttp = true,
            AuthorizationProvider = (uri, _) =>
            {
                Interlocked.Increment(ref credentials);
                if (uri.Host != "127.0.0.1") { throw new InvalidOperationException("Credential destination escaped."); }
                return ValueTask.FromResult<AuthenticationHeaderValue?>(new("Bearer", "fixture-upstream-only"));
            }
        };
        var source = new ControlledSource("unused", "v1", []);
        await using var app = await BridgeFixture.StartAsync([source.Registration()], mounts: [mount]);
        using var client = BridgeFixture.Client(app);
        using var request = new HttpRequestMessage(HttpMethod.Put,
            new Uri(client.BaseAddress!.AbsoluteUri.TrimEnd('/') + "/registries/one/dirs/g/files/item?epoch=7&ignore=%41+%2B&ignore=",
                new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }));
        request.Headers.Authorization = new("Bearer", "fixture-caller-not-forwarded");
        request.Headers.Host = "spoofed-front.example";
        request.Headers.Add("Cookie", "caller=never-forward");
        request.Headers.Add("X-Forwarded-Host", "spoofed-forwarded.example");
        request.Headers.Add("xRegistry-labels.note", "a%20b");
        request.Content = new ByteArrayContent([0, 255, 13, 10, 65]);
        request.Content.Headers.ContentType = new("application/octet-stream");
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(Convert.ToHexString(await response.Content.ReadAsByteArrayAsync())).IsEqualTo("00FF42");
        await Assert.That(response.Headers.GetValues("xRegistry-versionid").Single()).IsEqualTo("new-v");
        await Assert.That(response.Headers.Location!.OriginalString)
            .IsEqualTo(upstreamRoot.GetLeftPart(UriPartial.Authority) + "/authoritative/dirs/g/files/item/versions/new-v");
        await Assert.That(seenTarget).IsEqualTo("/authoritative/dirs/g/files/item?epoch=7&ignore=%41+%2B&ignore=");
        await Assert.That(seenHost).IsEqualTo(upstreamRoot.Authority);
        await Assert.That(seenAuthorization).IsEqualTo("Bearer fixture-upstream-only");
        await Assert.That(seenLabel).IsEqualTo("a%20b");
        await Assert.That(Convert.ToHexString(seenBody!)).IsEqualTo("00FF0D0A41");
        await Assert.That(sawCookie).IsFalse();
        await Assert.That(hits).IsEqualTo(1);
        await Assert.That(credentials).IsEqualTo(1);
        await Assert.That(source.Opens).IsEqualTo(0);
    }

    [Test]
    public async Task AggregateUnknownAndUnsupportedMountOperationsHaveZeroOutboundWork()
    {
        var hits = 0;
        var credentials = 0;
        await using var upstream = await StartUpstreamAsync(http =>
        {
            Interlocked.Increment(ref hits);
            http.Response.StatusCode = 500;
            return Task.CompletedTask;
        });
        var source = new ControlledSource("unused", "v1", []);
        var mount = new BridgeWriteMount
        {
            Name = "one",
            Model = BridgeFixture.Model,
            AllowLoopbackHttp = true,
            UpstreamRoot = new Uri(new Uri(upstream.Urls.Single()), "/authoritative"),
            AuthorizationProvider = (_, _) =>
            {
                Interlocked.Increment(ref credentials);
                return ValueTask.FromResult<AuthenticationHeaderValue?>(null);
            }
        };
        await using var app = await BridgeFixture.StartAsync([source.Registration()], mounts: [mount]);
        using var client = BridgeFixture.Client(app);
        foreach (var (method, path, expected) in new[]
        {
            ("PUT", "/registry/dirs/g/files/a", HttpStatusCode.MethodNotAllowed),
            ("PUT", "/registries/missing/dirs/g/files/a", HttpStatusCode.NotFound),
            ("TRACE", "/registries/one/dirs/g/files/a", HttpStatusCode.MethodNotAllowed),
            ("PUT", "/registries/one/modelsource", HttpStatusCode.NotImplemented),
            ("PUT", "/registries/one/dirs/g/files/a?native=oci", HttpStatusCode.NotImplemented)
        })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new ByteArrayContent([1]) };
            using var response = await client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(expected);
        }
        await Assert.That(hits).IsEqualTo(0);
        await Assert.That(credentials).IsEqualTo(0);
        await Assert.That(source.Opens).IsEqualTo(0);
    }

    [Test]
    public async Task DistinctMountsDoNotFanOutAndPreserveExactUpstreamRejections()
    {
        var firstHits = 0;
        var secondHits = 0;
        await using var first = await StartUpstreamAsync(http =>
        {
            Interlocked.Increment(ref firstHits);
            http.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
        const string problem = """{"type":"urn:literal:conflict","status":409,"detail":"unchanged upstream rejection","opaque":[3,1]}""";
        await using var second = await StartUpstreamAsync(async http =>
        {
            Interlocked.Increment(ref secondHits);
            http.Response.StatusCode = 409;
            http.Response.ContentType = "application/problem+json";
            http.Response.Headers["xRegistry-xregcorrelationid"] = "literal-upstream-correlation";
            await http.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(problem));
        });
        var source = new ControlledSource("unused", "v1", []);
        await using var app = await BridgeFixture.StartAsync([source.Registration()], mounts:
        [
            Mount("first", first), Mount("second", second)
        ]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.DeleteAsync("/registries/second/dirs/g/files/item");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(problem);
        await Assert.That(response.Headers.GetValues("xRegistry-xregcorrelationid").Single()).IsEqualTo("literal-upstream-correlation");
        await Assert.That(firstHits).IsEqualTo(0);
        await Assert.That(secondHits).IsEqualTo(1);
        await Assert.That(source.Opens).IsEqualTo(0);
    }

    [Test]
    [Arguments("truncated")]
    [Arguments("invalid-metadata")]
    [Arguments("escaping-location")]
    [Arguments("escaping-next")]
    [Arguments("invalid-header")]
    public async Task IncompleteOrInvalidSuccessfulMutationResponseIsUnknownWithoutRetry(string failure)
    {
        var hits = 0;
        await using var upstream = await StartUpstreamAsync(async http =>
        {
            Interlocked.Increment(ref hits);
            await http.Request.Body.CopyToAsync(Stream.Null);
            http.Response.StatusCode = failure == "escaping-location" ? 201 : 200;
            http.Response.ContentType = failure == "invalid-metadata" ? "application/json" : "application/octet-stream";
            if (failure == "escaping-location") { http.Response.Headers.Location = "https://not-authorized.example/other"; }
            if (failure == "escaping-next") { http.Response.Headers.Link = "<https://not-authorized.example/next>;rel=NEXT"; }
            if (failure == "invalid-header") { http.Response.Headers["xRegistry-versionid"] = "%ZZ"; }
            if (failure == "truncated")
            {
                http.Response.ContentLength = 10;
                await http.Response.Body.WriteAsync(new byte[] { 1, 2 });
                await http.Response.Body.FlushAsync();
                http.Abort();
            }
            else
            {
                await http.Response.Body.WriteAsync(failure == "invalid-metadata" ? Encoding.UTF8.GetBytes("""{"not":"an entity"}""") : new byte[] { 0, 255 });
            }
        });
        var source = new ControlledSource("unused", "v1", []);
        await using var app = await BridgeFixture.StartAsync([source.Registration()], mounts: [Mount("one", upstream)]);
        using var client = BridgeFixture.Client(app);
        using var request = new HttpRequestMessage(HttpMethod.Put, failure == "invalid-metadata"
            ? "/registries/one/dirs/g/notes/item" : "/registries/one/dirs/g/files/item")
        {
            Content = new ByteArrayContent(failure == "invalid-metadata" ? Encoding.UTF8.GetBytes("""{"message":"literal"}""") : new byte[] { 4 })
        };
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(response.Headers.GetValues("X-Bridge-Mutation-Outcome").Single()).IsEqualTo("unknown");
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("code").GetString()).IsEqualTo("upstream_outcome_unknown");
        await Assert.That(hits).IsEqualTo(1);
    }

    [Test]
    public async Task DeadlineAfterMutationDispatchIsUnknownWithExactlyOneUpstreamRequest()
    {
        var hits = 0;
        await using var upstream = await StartUpstreamAsync(async http =>
        {
            Interlocked.Increment(ref hits);
            await http.Request.Body.CopyToAsync(Stream.Null);
            await Task.Delay(Timeout.Infinite, http.RequestAborted);
        });
        var source = new ControlledSource("unused", "v1", []);
        await using var app = await BridgeFixture.StartAsync([source.Registration()],
            BridgeFixture.Options with { RequestTimeout = TimeSpan.FromMilliseconds(300) }, [Mount("one", upstream)]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.PutAsync("/registries/one/dirs/g/files/item", new ByteArrayContent([1, 2]));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(response.Headers.GetValues("X-Bridge-Mutation-Outcome").Single()).IsEqualTo("unknown");
        await Assert.That(hits).IsEqualTo(1);
    }

    [Test]
    public async Task CallerCancellationAfterDispatchCancelsTheOneUpstreamWithoutRetry()
    {
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hits = 0;
        await using var upstream = await StartUpstreamAsync(async http =>
        {
            Interlocked.Increment(ref hits);
            await http.Request.Body.CopyToAsync(Stream.Null);
            dispatched.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, http.RequestAborted); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); }
        });
        await using var app = await BridgeFixture.StartAsync([new ControlledSource("unused", "v1", []).Registration()],
            mounts: [Mount("one", upstream)]);
        using var client = BridgeFixture.Client(app);
        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/registries/one/dirs/g/files/item")
        {
            Content = new ByteArrayContent([1, 2, 3])
        };
        var pending = client.SendAsync(request, cancellation.Token);
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(hits).IsEqualTo(1);
    }

    [Test]
    public async Task DeniedMountPolicyDoesNotResolveCredentialsOrDispatch()
    {
        var hits = 0;
        var credentials = 0;
        await using var upstream = await StartUpstreamAsync(http =>
        {
            hits++;
            http.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
        var mount = Mount("one", upstream) with
        {
            AuthorizationProvider = (_, _) =>
            {
                credentials++;
                return ValueTask.FromResult<AuthenticationHeaderValue?>(null);
            }
        };
        await using var app = await BridgeFixture.StartAsync([new ControlledSource("unused", "v1", []).Registration()],
            BridgeFixture.Options with { AuthorizeMount = static (_, _, _, _) => ValueTask.FromResult(false) }, [mount]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.PutAsync("/registries/one/dirs/g/files/item", new ByteArrayContent([1]));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(hits).IsEqualTo(0);
        await Assert.That(credentials).IsEqualTo(0);
    }

    [Test]
    public async Task RequestByteBoundaryAndUnsupportedEncodingAreCheckedBeforeCredentials()
    {
        var hits = 0;
        var credentials = 0;
        await using var upstream = await StartUpstreamAsync(async http =>
        {
            hits++;
            await http.Request.Body.CopyToAsync(Stream.Null);
            http.Response.StatusCode = 204;
        });
        var mount = Mount("one", upstream) with
        {
            AuthorizationProvider = (_, _) =>
            {
                credentials++;
                return ValueTask.FromResult<AuthenticationHeaderValue?>(null);
            }
        };
        await using var app = await BridgeFixture.StartAsync([new ControlledSource("unused", "v1", []).Registration()],
            BridgeFixture.Options with { MaxRequestBytes = 1 }, [mount]);
        using var client = BridgeFixture.Client(app);
        using var tooLarge = await client.PutAsync("/registries/one/dirs/g/files/item", new ByteArrayContent([1, 2]));
        await Assert.That(tooLarge.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        using var compressed = new HttpRequestMessage(HttpMethod.Put, "/registries/one/dirs/g/files/item")
        {
            Content = new ByteArrayContent([1])
        };
        compressed.Content.Headers.ContentEncoding.Add("gzip");
        using var unsupported = await client.SendAsync(compressed);
        await Assert.That(unsupported.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
        await Assert.That(hits).IsEqualTo(0);
        await Assert.That(credentials).IsEqualTo(0);
        using var exact = await client.PutAsync("/registries/one/dirs/g/files/item", new ByteArrayContent([1]));
        await Assert.That(exact.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(hits).IsEqualTo(1);
        await Assert.That(credentials).IsEqualTo(1);
    }

    [Test]
    public async Task RelativeNavigationKeepsTheAuthoritativeOriginAndOpaqueQuery()
    {
        await using var upstream = await StartUpstreamAsync(http =>
        {
            http.Response.StatusCode = 204;
            http.Response.Headers.Link = "<?opaque=%41+%2B&empty=>;rel=next;count=2";
            return Task.CompletedTask;
        });
        var mount = Mount("one", upstream);
        await using var app = await BridgeFixture.StartAsync([new ControlledSource("unused", "v1", []).Registration()], mounts: [mount]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.DeleteAsync("/registries/one/dirs/g/files/item");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(response.Headers.GetValues("Link").Single()).IsEqualTo(
            "<" + mount.UpstreamRoot.GetLeftPart(UriPartial.Authority) +
            "/authoritative/dirs/g/files/item?opaque=%41+%2B&empty=>;rel=\"next\";count=\"2\"");
    }

    [Test]
    [Arguments(false, "")]
    [Arguments(true, "")]
    [Arguments(false, "/meta")]
    [Arguments(true, "/meta")]
    [Arguments(false, "/versions/v:1@stable")]
    [Arguments(true, "/versions/v:1@stable")]
    public async Task EquivalentAuthoritativeXidsPreserveExactMetadataBytesWithoutRetry(bool escapedRequest, string suffix)
    {
        const string owner = "/dirs/g/files/item:1@home";
        var rawXid = owner + suffix;
        var escapedXid = rawXid.Replace(":", "%3A", StringComparison.Ordinal).Replace("@", "%40", StringComparison.Ordinal);
        var metadata = new ControlledSource("upstream", "v:1@stable", ["item:1@home"]).Entity(rawXid);
        metadata["xid"] = escapedRequest ? rawXid : escapedXid;
        foreach (var name in new[] { "self", "metaurl", "versionsurl", "defaultversionurl" })
        {
            if (metadata.ContainsKey(name)) { metadata[name] = "#/"; }
        }
        metadata["domain"] = new JsonObject { ["uri"] = "https://domain.example/%252F", ["self"] = "literal:opaque@value" };
        if (suffix == "/meta") { metadata.Remove("domain"); }
        var body = Encoding.UTF8.GetBytes(" \n" + metadata.ToJsonString() + "\n ");
        var hits = 0;
        await using var upstream = await StartUpstreamAsync(async http =>
        {
            Interlocked.Increment(ref hits);
            await http.Request.Body.CopyToAsync(Stream.Null);
            http.Response.ContentType = "application/json";
            await http.Response.Body.WriteAsync(body);
        });
        var source = new ControlledSource("unused", "v1", []);
        await using var app = await BridgeFixture.StartAsync([source.Registration()], mounts: [Mount("one", upstream)]);
        using var client = BridgeFixture.Client(app);
        var path = (escapedRequest ? escapedXid : rawXid) + (suffix == "/meta" ? "" : "$details");
        using var response = await client.PutAsync("/registries/one" + path, new StringContent("{}"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Convert.ToHexString(await response.Content.ReadAsByteArrayAsync())).IsEqualTo(Convert.ToHexString(body));
        await Assert.That(response.Headers.Contains("X-Bridge-Mutation-Outcome")).IsFalse();
        await Assert.That(hits).IsEqualTo(1);
        await Assert.That(source.Opens).IsEqualTo(0);
    }

    [Test]
    [Arguments("xid", "/dirs/g/files/other")]
    [Arguments("xid", "/dirs/g/files/item:1@home/versions/v:1@stable")]
    [Arguments("xid", "/dirs/g/files/item:1@home$details")]
    [Arguments("xid", "/dirs/g/files/item%252Fescape")]
    [Arguments("xid", "/dirs/g/files/item%2Fescape")]
    [Arguments("xid", "/dirs/g/files/item%00escape")]
    [Arguments("xid", "/dirs/g/files/item%GGescape")]
    [Arguments("fileid", "item%3A1%40home")]
    [Arguments("fileid", "other")]
    public async Task InvalidAuthoritativeIdentityAfterDispatchIsUnknownWithoutRetry(string attribute, string invalid)
    {
        var metadata = new ControlledSource("upstream", "v:1@stable", ["item:1@home"]).Entity("/dirs/g/files/item:1@home");
        metadata[attribute] = invalid;
        metadata["self"] = "#/";
        metadata["metaurl"] = "#/meta";
        metadata["versionsurl"] = "#/versions";
        var body = Encoding.UTF8.GetBytes(metadata.ToJsonString());
        var hits = 0;
        await using var upstream = await StartUpstreamAsync(async http =>
        {
            Interlocked.Increment(ref hits);
            await http.Request.Body.CopyToAsync(Stream.Null);
            http.Response.ContentType = "application/json";
            await http.Response.Body.WriteAsync(body);
        });
        var source = new ControlledSource("unused", "v1", []);
        await using var app = await BridgeFixture.StartAsync([source.Registration()], mounts: [Mount("one", upstream)]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.PutAsync("/registries/one/dirs/g/files/item%3A1%40home$details", new StringContent("{}"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(response.Headers.GetValues("X-Bridge-Mutation-Outcome").Single()).IsEqualTo("unknown");
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("code").GetString()).IsEqualTo("upstream_outcome_unknown");
        await Assert.That(hits).IsEqualTo(1);
        await Assert.That(source.Opens).IsEqualTo(0);
    }

    private static BridgeWriteMount Mount(string name, WebApplication upstream) => new()
    {
        Name = name,
        Model = BridgeFixture.Model,
        AllowLoopbackHttp = true,
        UpstreamRoot = new Uri(new Uri(upstream.Urls.Single()), "/authoritative")
    };

    internal static async Task<WebApplication> StartUpstreamAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Map("/{**path}", handler);
        try { await app.StartAsync(); return app; }
        catch { await app.DisposeAsync(); throw; }
    }
}
