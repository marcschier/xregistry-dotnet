using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Server;

namespace XRegistry.AspNetCore.Tests;

public class RegistryQueryHttpTests
{
    [Test]
    public async Task PaginationExpiryUsesHttpDateAndKeepsTheOriginalDeadline()
    {
        var clock = new QueryClock();
        await using var host = await QueryHost.Start(clock: clock, queryLimits: new() { CursorLifetime = TimeSpan.FromMinutes(1) });
        await host.Seed();
        using var first = await host.Client.GetAsync("/registry/things?limit=1");
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(first.Content.Headers.GetValues("Expires").Single()).IsEqualTo("Tue, 01 Jan 2030 00:01:00 GMT");
        clock.Advance(TimeSpan.FromSeconds(10));
        using var second = await host.Follow(Links(first)["next"].Target);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(second.Content.Headers.GetValues("Expires").Single()).IsEqualTo("Tue, 01 Jan 2030 00:01:00 GMT");
        clock.Advance(TimeSpan.FromSeconds(50));
        using var expired = await host.Follow(Links(second)["next"].Target);
        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.Gone);
    }

    [Test]
    public async Task EscapedGroupIdsSurviveOpaqueHttpContinuationWithoutPathReinterpretation()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"things":{"singular":"thing","resources":{"items":{"singular":"item","hasdocument":false}}}}}
            """));
        await using var host = await HttpTests.TestHost.StartAsync(model: model, httpOptions: new() { MountPath = "/registry" });
        using var created = await host.Client.PostAsync("/registry/things/g%40one/items", Json("""{"a":{},"b":{}}"""));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var first = await host.Client.GetAsync("/registry/things/g%40one/items?limit=1");
        var next = Links(first)["next"].Target;
        await Assert.That(next.AbsoluteUri.StartsWith("https://public.example/registry/things/g%40one/items?cursor=", StringComparison.Ordinal)).IsTrue();
        var local = new Uri(host.Client.BaseAddress!.AbsoluteUri.TrimEnd('/') + next.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped));
        using var retained = await host.Client.GetAsync(local);
        await Assert.That(retained.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Ids(await retained.Content.ReadAsByteArrayAsync())).IsEqualTo("b");
        await Assert.That(retained.Headers.GetValues("xRegistry-count").Single()).IsEqualTo("2");
    }

    [Test]
    public async Task RawHttpFilterSortAndOpaqueLinksUseTrustedMountCountAndExpiry()
    {
        await using var host = await QueryHost.Start();
        await host.Seed();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/registry/things?filter=rank%3E%3D2&sort=rank%3Ddesc&limit=1");
        request.Headers.Host = "attacker.invalid";
        using var first = await host.Client.SendAsync(request);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Ids(await first.Content.ReadAsByteArrayAsync())).IsEqualTo("c");
        await Assert.That(first.Headers.GetValues("xRegistry-count").Single()).IsEqualTo("3");
        var links = Links(first);
        await Assert.That(links["next"].Count).IsEqualTo(3UL);
        await Assert.That(links["next"].Target.AbsoluteUri.StartsWith("https://public.example/registry/things?cursor=", StringComparison.Ordinal)).IsTrue();
        await Assert.That(links["next"].Target.Query.Contains("filter", StringComparison.Ordinal)).IsFalse();
        await Assert.That(links.ContainsKey("prev")).IsFalse();
        await Assert.That(first.Content.Headers.Expires).IsNotNull();
        using var change = await host.Client.PatchAsync("/registry/things/b", Json("""{"rank":1000,"name":"changed"}"""));
        await Assert.That(change.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var second = await host.Follow(links["next"].Target);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var data = RegistryJson.Parse(await second.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(data.EnumerateObject().Single().Name).IsEqualTo("b");
        await Assert.That(data.GetProperty("b").GetProperty("rank").GetInt32()).IsEqualTo(10);
        await Assert.That(second.Content.Headers.Expires).IsEqualTo(first.Content.Headers.Expires);
        using var last = await host.Follow(Links(second)["next"].Target);
        await Assert.That(Ids(await last.Content.ReadAsByteArrayAsync())).IsEqualTo("a");
        await Assert.That(last.Headers.GetValues("xRegistry-count").Single()).IsEqualTo("3");
        await Assert.That(Links(last).ContainsKey("next")).IsFalse();
    }

    [Test]
    public async Task RawRepeatedFiltersAreOrAndQuotedValuesAreNotSqlLiterals()
    {
        await using var host = await QueryHost.Start();
        await host.Seed();
        using var response = await host.Client.GetAsync("/registry/things?filter=rank%3D2%2Cactive%3Dfalse&filter=name%3DbEtA");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Ids(await response.Content.ReadAsByteArrayAsync())).IsEqualTo("a,b");
        using var quoted = await host.Client.GetAsync("/registry/things?filter=name%3D%22Beta%22");
        await Assert.That(Ids(await quoted.Content.ReadAsByteArrayAsync())).IsEqualTo("");
        await Assert.That(quoted.Headers.GetValues("xRegistry-count").Single()).IsEqualTo("0");
    }

    [Test]
    public async Task AlteredForgedAndExpiredContinuationsAreExplicitProblems()
    {
        var clock = new QueryClock();
        await using var host = await QueryHost.Start(clock: clock);
        await host.Seed();
        using var first = await host.Client.GetAsync("/registry/things?limit=1");
        var next = Links(first)["next"].Target;
        using var changed = await host.Follow(new Uri(next.AbsoluteUri + "&sort=name"));
        await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await changed.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString()).IsEqualTo("bad_cursor");
        using var forged = await host.Client.GetAsync("/registry/things?cursor=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        clock.Advance(TimeSpan.FromMinutes(2));
        using var expired = await host.Follow(next);
        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.Gone);
        await Assert.That(RegistryJson.Parse(await expired.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString()).IsEqualTo("cursor_expired");
    }

    [Test]
    public async Task CurrentAuthorizationIsRecheckedForRetainedHttpPages()
    {
        var policy = new QueryPolicy();
        await using var host = await QueryHost.Start(policy: policy);
        await host.Seed();
        using var first = await host.Client.GetAsync("/registry/things?limit=1");
        policy.Revoked.Add("/things/b");
        using var blocked = await host.Follow(Links(first)["next"].Target);
        await Assert.That(blocked.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var body = RegistryJson.Parse(await blocked.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("forbidden");
        await Assert.That(body.TryGetProperty("b", out _)).IsFalse();
    }

    [Test]
    public async Task MetadataDocumentProjectionAndHeadKeepPageRepresentationStable()
    {
        await using var host = await QueryHost.Start();
        await host.Seed();
        using var first = await host.Client.GetAsync("/registry/things?limit=1&doc&inline=*");
        var firstMetadata = RegistryJson.Parse(await first.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(firstMetadata.GetProperty("a").GetProperty("self").GetString()).IsEqualTo("#/a");
        var next = Links(first)["next"].Target;
        using var page = await host.Follow(next);
        await Assert.That(RegistryJson.Parse(await page.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("b").GetProperty("self").GetString()).IsEqualTo("#/b");
        using var head = await host.Follow(next, HttpMethod.Head);
        await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await head.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
        await Assert.That(head.Content.Headers.ContentLength).IsEqualTo(page.Content.Headers.ContentLength);
        await Assert.That(head.Headers.GetValues("xRegistry-count").Single()).IsEqualTo("4");
    }

    [Test]
    [Arguments("limit=0", "bad_flag")]
    [Arguments("limit=18446744073709551616", "bad_flag")]
    [Arguments("sort=unknown", "bad_sort")]
    [Arguments("filter=active%3DTRUE", "bad_filter")]
    public async Task InvalidQueryCasesHaveIndependent400Errors(string query, string code)
    {
        await using var host = await QueryHost.Start();
        await host.Seed();
        using var response = await host.Client.GetAsync("/registry/things?" + query);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString()).IsEqualTo(code);
    }

    private static string Ids(byte[] json) => string.Join(',', RegistryJson.Parse(json).RootElement.EnumerateObject().Select(static property => property.Name));
    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private static Dictionary<string, (Uri Target, ulong Count)> Links(HttpResponseMessage response)
    {
        var result = new Dictionary<string, (Uri, ulong)>(StringComparer.Ordinal);
        foreach (var field in response.Headers.GetValues("Link").SelectMany(static value => value.Split(", ")))
        {
            var parts = field.Split(';');
            var relation = parts.Single(static part => part.StartsWith("rel=", StringComparison.Ordinal))[4..];
            if (relation == "xregistry-root")
            {
                continue;
            }

            result.Add(relation, (new Uri(parts[0][1..^1], UriKind.Absolute),
                ulong.Parse(parts.Single(static part => part.StartsWith("count=", StringComparison.Ordinal))[6..], CultureInfo.InvariantCulture)));
        }

        return result;
    }

    private sealed class QueryClock : TimeProvider
    {
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class QueryPolicy : IRegistryAuthorizationPolicy
    {
        internal HashSet<string> Revoked { get; } = new(StringComparer.Ordinal);
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(access != RegistryAccess.Read || !Revoked.Contains(path.EscapedPath));
    }

    private sealed class QueryHost(WebApplication application, HttpClient client) : IAsyncDisposable
    {
        internal HttpClient Client { get; } = client;
        internal static async Task<QueryHost> Start(TimeProvider? clock = null, QueryPolicy? policy = null,
            RegistryQueryLimits? queryLimits = null)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development", Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var application = builder.Build();
            application.Use(async (context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "isolated-query-test")], "test"));
                await next(context);
            });
            var model = RegistryModel.Compile(RegistryJson.Parse("""
                {"groups":{"things":{"singular":"thing","attributes":{"rank":{"type":"decimal"},"active":{"type":"boolean"}}}}}
                """));
            var engine = new RegistryEngine(new()
            {
                RegistryId = "query-http",
                PublicRoot = new Uri("https://public.example/registry"),
                Model = model,
                TimeProvider = clock ?? TimeProvider.System,
                QueryLimits = queryLimits ?? new()
            }, new InMemoryRegistryPersistence(), policy ?? new());
            application.MapXRegistry(engine, new() { MountPath = "/registry" });
            await application.StartAsync();
            var address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(application, new HttpClient { BaseAddress = new Uri(address) });
        }

        internal async Task Seed()
        {
            using var result = await Client.PutAsync("/registry", Json("""
                {"things":{"a":{"name":"Alpha","rank":2,"active":false},"b":{"name":"Beta","rank":10,"active":true},
                  "c":{"name":"Gamma","rank":30,"active":true},"d":{"description":""}}}
                """));
            await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        internal Task<HttpResponseMessage> Follow(Uri uri, HttpMethod? method = null)
        {
            var local = new Uri(Client.BaseAddress!.AbsoluteUri.TrimEnd('/') + uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped));
            return Client.SendAsync(new HttpRequestMessage(method ?? HttpMethod.Get, local));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }
}
