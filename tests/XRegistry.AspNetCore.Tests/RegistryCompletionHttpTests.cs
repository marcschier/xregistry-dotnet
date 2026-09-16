// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
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

public class RegistryCompletionHttpTests
{
    [Test]
    public async Task MutableCapabilitiesAndShortDocumentUrlsUseLiteralHeadersAndTrustedPublicRoot()
    {
        await using var host = await Host.Start();
        using var enabled = await host.Client.PatchAsync("/registry/capabilities", Json("""{"shortself":true}"""));
        await Assert.That(enabled.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var capabilities = RegistryJson.Parse(await enabled.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(capabilities.GetProperty("shortself").GetBoolean()).IsTrue();
        await Assert.That(capabilities.GetProperty("available").GetProperty("capabilities").GetProperty("mutable").GetBoolean()).IsTrue();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/registry/teams/g/files/f") { Content = new ByteArrayContent([0, 255, 123, 0]) };
        request.Headers.Host = "attacker.invalid";
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var created = await host.Client.SendAsync(request);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var shortUrl = created.Headers.GetValues("xRegistry-shortself").Single();
        await Assert.That(shortUrl.StartsWith("https://public.example/registry/~/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(shortUrl.Contains('$', StringComparison.Ordinal)).IsFalse();
        await Assert.That(Convert.ToBase64String(await created.Content.ReadAsByteArrayAsync())).IsEqualTo("AP97AA==");
        using var bytes = await host.Client.GetAsync(new Uri(shortUrl).AbsolutePath);
        await Assert.That(bytes.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Convert.ToBase64String(await bytes.Content.ReadAsByteArrayAsync())).IsEqualTo("AP97AA==");
        using var metadata = await host.Client.GetAsync(new Uri(shortUrl).AbsolutePath + "$details?inline=meta");
        var entity = RegistryJson.Parse(await metadata.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(entity.GetProperty("self").GetString()).IsEqualTo("https://public.example/registry/teams/g/files/f$details");
        await Assert.That(entity.GetProperty("shortself").GetString()).IsEqualTo(shortUrl);
        await Assert.That(entity.GetProperty("meta").GetProperty("shortself").GetString()).IsEqualTo(shortUrl + "/meta");
        using var head = await host.Client.SendAsync(new(HttpMethod.Head, new Uri(shortUrl).AbsolutePath + "/versions/1"));
        await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(head.Content.Headers.ContentLength).IsEqualTo(4L);
        await Assert.That((await head.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
    }

    [Test]
    public async Task ShortMetadataOnlyUrlsAndDocProjectionDoNotRewriteDomainData()
    {
        await using var host = await Host.Start();
        using var enabled = await host.Client.PatchAsync("/registry/capabilities", Json("""{"shortself":true}"""));
        await Assert.That(enabled.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var created = await host.Client.PutAsync("/registry/teams/g/notes/n", Json("""
            {"documentation":"https://public.example/registry/teams/g/notes/n"}
            """));
        var metadata = RegistryJson.Parse(await created.Content.ReadAsByteArrayAsync()).RootElement;
        var shortUrl = metadata.GetProperty("shortself").GetString()!;
        using var details = await host.Client.GetAsync(new Uri(shortUrl).AbsolutePath + "$details");
        var body = RegistryJson.Parse(await details.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(body.GetProperty("self").GetString()).IsEqualTo("https://public.example/registry/teams/g/notes/n");
        await Assert.That(body.GetProperty("documentation").GetString()).IsEqualTo("https://public.example/registry/teams/g/notes/n");
        using var document = await host.Client.GetAsync(new Uri(shortUrl).AbsolutePath + "?doc&inline=*");
        var doc = RegistryJson.Parse(await document.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(doc.TryGetProperty("shortself", out _)).IsFalse();
        await Assert.That(doc.GetProperty("meta").TryGetProperty("shortself", out _)).IsFalse();
        await Assert.That(doc.GetProperty("versions").GetProperty("1").TryGetProperty("shortself", out _)).IsFalse();
    }

    [Test]
    public async Task RejectedCapabilityAndNestedChangesNeverPublishPartialState()
    {
        await using var host = await Host.Start();
        using var failed = await host.Client.PatchAsync("/registry", Json("""
            {"capabilities":{"shortself":true},"teams":{"a":{},"b":{"unknown":1}}}
            """));
        await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var current = await host.Client.GetAsync("/registry?inline=capabilities");
        var root = RegistryJson.Parse(await current.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(root.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
        await Assert.That(root.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That(root.GetProperty("capabilities").GetProperty("shortself").GetBoolean()).IsFalse();
        using var invalidSecurity = await host.Client.PatchAsync("/registry/capabilities", Json("""{"authentication":false}"""));
        await Assert.That(invalidSecurity.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await invalidSecurity.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString()).IsEqualTo("capability_unknown");
    }

    [Test]
    public async Task CapabilityEditingNeverGrantsAnonymousWriteAccess()
    {
        await using var host = await Host.Start(authenticated: false);
        using var denied = await host.Client.PatchAsync("/registry/capabilities", Json("""{"shortself":true}"""));
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var ordinary = await host.Client.PutAsync("/registry/teams/g", Json("{}"));
        await Assert.That(ordinary.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task ReadonlyIgnoreReturnsOnlyProcessedEntriesAndRejectsSingleTargets()
    {
        await using var host = await Host.Start();
        using var created = await host.Client.PostAsync("/registry/teams/g/notes", Json("""{"locked":{"name":"locked"},"open":{"name":"open"}}"""));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await host.LockResource("/teams/g/notes/locked");
        using var changed = await host.Client.PatchAsync("/registry/teams/g/notes?ignore=readonly&ignore=epoch",
            Json("""{"locked":{"name":"not changed","meta":{"readonly":false}},"open":{"epoch":999,"name":"changed"}}"""));
        await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(RegistryJson.Parse(await changed.Content.ReadAsByteArrayAsync()).RootElement.EnumerateObject().Single().Name).IsEqualTo("open");
        using var blocked = await host.Client.PatchAsync("/registry/teams/g/notes/locked?ignore=readonly", Json("{}"));
        await Assert.That(blocked.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await blocked.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString()).IsEqualTo("bad_flag");
        using var locked = await host.Client.GetAsync("/registry/teams/g/notes/locked/meta");
        await Assert.That(RegistryJson.Parse(await locked.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("readonly").GetBoolean()).IsTrue();
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private sealed class Host(WebApplication application, HttpClient client, InMemoryRegistryPersistence store) : IAsyncDisposable
    {
        internal HttpClient Client { get; } = client;
        internal static async Task<Host> Start(bool authenticated = true)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development", Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var application = builder.Build();
            if (authenticated)
            {
                application.Use(async (context, next) =>
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "completion-test")], "isolated-test"));
                    await next(context);
                });
            }

            var persistence = new InMemoryRegistryPersistence();
            var engine = new RegistryEngine(new()
            {
                RegistryId = "completion",
                PublicRoot = new Uri("https://public.example/registry"),
                Model = RegistryModel.Compile(RegistryJson.Parse("""
                    {"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false},"files":{"singular":"file"}}}}}
                    """)),
                AllowCapabilityUpdates = true,
                AllowAnonymousReads = true
            }, persistence, new Permit());
            application.MapXRegistry(engine, new() { MountPath = "/registry" });
            await application.StartAsync();
            var address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(application, new HttpClient { BaseAddress = new Uri(address) }, persistence);
        }

        internal async Task LockResource(string key)
        {
            using var snapshot = await store.ReadSnapshotAsync();
            var value = JsonNode.Parse(snapshot.Find(key)!.Metadata.RootElement.GetRawText())!;
            value["attributes"]!["readonly"] = true;
            using var candidate = await store.PrepareAsync(snapshot.Generation, [RegistryMutation.Put(key, RegistryJson.Parse(value.ToJsonString()))]);
            await candidate.CommitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }

    private sealed class Permit : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }
}
