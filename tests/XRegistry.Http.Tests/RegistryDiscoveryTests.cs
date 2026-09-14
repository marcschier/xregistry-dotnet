using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Client;

namespace XRegistry.Http.Tests;

public class RegistryDiscoveryTests
{
    [Test]
    public async Task RegistryAndHostDiscoveryUseTheirDistinctNormativeLocations()
    {
        var paths = new List<string>();
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
        {
            app.MapGet("/mount/.xregistry", (HttpContext context) =>
            {
                paths.Add(context.Request.Path);
                return Results.Text("""{"registries":["https://other.example/registry"]}""", "application/json");
            });
            app.MapGet("/.well-known/xregistry", (HttpContext context) =>
            {
                paths.Add(context.Request.Path);
                return Results.Text("""{"registries":[]}""", "application/json");
            });
        });
        using var client = new XRegistryHttpClient(new Uri(new Uri(app.Urls.Single()), "/mount/"),
            new() { AllowLoopbackHttp = true });
        var registry = await client.DiscoverAsync();
        var host = await client.DiscoverAsync(RegistryDiscoveryLocation.Host);

        await Assert.That(paths.Count).IsEqualTo(2);
        await Assert.That(paths[0]).IsEqualTo("/mount/.xregistry");
        await Assert.That(paths[1]).IsEqualTo("/.well-known/xregistry");
        await Assert.That(registry.Registries.Single().OriginalString).IsEqualTo("https://other.example/registry");
        await Assert.That(host.Registries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AdvertisementsRemainOrderedOpaqueDataNotNetworkPermissions()
    {
        var requests = 0;
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/.xregistry", () =>
            {
                requests++;
                return Results.Text(
                    """{"registries":["https://127.0.0.1/private","file:///registry/","https://127.0.0.1/private"]}""",
                    "application/json");
            }));
        using var client = new XRegistryHttpClient(new Uri(app.Urls.Single()), new() { AllowLoopbackHttp = true });
        var discovery = await client.DiscoverAsync();

        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(discovery.Registries.Count).IsEqualTo(3);
        await Assert.That(discovery.Registries[0].OriginalString).IsEqualTo("https://127.0.0.1/private");
        await Assert.That(discovery.Registries[1].Scheme).IsEqualTo("file");
        await Assert.That(discovery.Registries[2].OriginalString).IsEqualTo("https://127.0.0.1/private");
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"registries":{}}""")]
    [Arguments("""{"registries":[null]}""")]
    [Arguments("""{"registries":["relative/path"]}""")]
    [Arguments("""{"registries":["https://example.invalid/%"]}""")]
    public async Task MalformedDiscoveryDoesNotBecomeAnEmptySuccess(string body)
    {
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/.xregistry", () => Results.Text(body, "application/json")));
        using var client = new XRegistryHttpClient(new Uri(app.Urls.Single()), new() { AllowLoopbackHttp = true });
        await Assert.That(async () => await client.DiscoverAsync()).Throws<InvalidDataException>();
    }

    [Test]
    public async Task DiscoveryEntryLimitIsEnforcedExactly()
    {
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/.xregistry",
                static () => Results.Text("""{"registries":["https://one.example/","https://two.example/"]}""", "application/json")));
        using var client = new XRegistryHttpClient(new Uri(app.Urls.Single()), new() { AllowLoopbackHttp = true });

        var accepted = await client.DiscoverAsync(maxRegistries: 2);
        await Assert.That(accepted.Registries.Count).IsEqualTo(2);
        await Assert.That(async () => await client.DiscoverAsync(maxRegistries: 1)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task UnavailableRegistryDiscoveryDoesNotSilentlyProbeTheHost()
    {
        var hostRequests = 0;
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
        {
            app.MapGet("/.xregistry", static () => Results.NotFound());
            app.MapGet("/.well-known/xregistry", () =>
            {
                hostRequests++;
                return Results.Text("""{"registries":[]}""", "application/json");
            });
        });
        using var client = new XRegistryHttpClient(new Uri(app.Urls.Single()), new() { AllowLoopbackHttp = true });
        HttpStatusCode? observed = null;
        try
        {
            await client.DiscoverAsync();
        }
        catch (HttpRequestException exception)
        {
            observed = exception.StatusCode;
        }

        await Assert.That(observed).IsEqualTo((HttpStatusCode?)HttpStatusCode.NotFound);
        await Assert.That(hostRequests).IsEqualTo(0);
    }
}
