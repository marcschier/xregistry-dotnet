// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Client;

namespace XRegistry.Http.Tests;

public class RegistryHttpTransportTests
{
    [Test]
    public async Task OwnedClientConnectsToActualAuthorizedLoopbackAddress()
    {
        await using var app = await StartAsync(static app =>
            app.MapGet("/value", static () => Results.Text("actual-loopback-value")));
        var root = new Uri(app.Urls.Single());
        using var client = new RegistryHttpConnectionPolicy(root, allowLoopbackHttp: true).CreateClient();

        var result = await client.GetStringAsync(new Uri(root, "/value"));

        await Assert.That(result).IsEqualTo("actual-loopback-value");
    }

    [Test]
    public async Task RedirectAndCookiesCannotTriggerHiddenRequestsOrCredentialReuse()
    {
        var targetRequests = 0;
        await using var app = await StartAsync(app =>
        {
            app.MapGet("/redirect", static (HttpContext context) =>
            {
                context.Response.Cookies.Append("secret", "not-forwarded");
                context.Response.Redirect("/target");
                return Task.CompletedTask;
            });
            app.MapGet("/target", (HttpContext context) =>
            {
                Interlocked.Increment(ref targetRequests);
                return Results.Text(context.Request.Headers.Cookie.ToString());
            });
        });
        var root = new Uri(app.Urls.Single());
        using var client = new RegistryHttpConnectionPolicy(root, allowLoopbackHttp: true).CreateClient();

        using var redirect = await client.GetAsync(new Uri(root, "/redirect"));
        await Assert.That(redirect.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(targetRequests).IsEqualTo(0);
        var cookies = await client.GetStringAsync(new Uri(root, "/target"));
        await Assert.That(cookies).IsEqualTo("");
        await Assert.That(targetRequests).IsEqualTo(1);
    }

    [Test]
    public async Task CrossOriginAndHostOverrideFailBeforeDispatch()
    {
        var requests = 0;
        await using var app = await StartAsync(app => app.MapGet("/", () =>
        {
            Interlocked.Increment(ref requests);
            return Results.Text("unexpected");
        }));
        var root = new Uri(app.Urls.Single());
        using var client = new RegistryHttpConnectionPolicy(root, allowLoopbackHttp: true).CreateClient();
        using var overrideRequest = new HttpRequestMessage(HttpMethod.Get, root);
        overrideRequest.Headers.Host = "foreign.example";
        await Assert.That(async () =>
        {
            using var response = await client.SendAsync(overrideRequest);
        }).Throws<HttpRequestException>();

        using var foreign = new HttpRequestMessage(HttpMethod.Get, "http://example.invalid/");
        await Assert.That(() =>
        {
            using var response = client.Send(foreign);
        }).Throws<HttpRequestException>();
        await Assert.That(requests).IsEqualTo(0);
    }

    [Test]
    public async Task HttpsLoopbackIsDeniedBeforeOpeningAnUnapprovedConnection()
    {
        var requests = 0;
        await using var app = await StartAsync(app => app.MapGet("/", () =>
        {
            Interlocked.Increment(ref requests);
            return Results.Text("unexpected");
        }));
        var uri = new UriBuilder(app.Urls.Single()) { Scheme = "https" }.Uri;
        using var client = new RegistryHttpConnectionPolicy(uri).CreateClient();

        await Assert.That(async () =>
        {
            using var response = await client.GetAsync(uri);
        }).Throws<HttpRequestException>();
        await Assert.That(requests).IsEqualTo(0);
    }

    [Test]
    public async Task CancellationAndFiniteTimeoutArePreserved()
    {
        var policy = new RegistryHttpConnectionPolicy(new Uri("https://example.invalid/"));
        await Assert.That(() => policy.CreateClient(Timeout.InfiniteTimeSpan))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => policy.CreateClient(TimeSpan.FromMinutes(11)))
            .Throws<ArgumentOutOfRangeException>();
        using var client = policy.CreateClient(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.That(async () =>
        {
            using var response = await client.GetAsync("https://example.invalid/", cancellation.Token);
        }).Throws<OperationCanceledException>();
        await Assert.That(client.Timeout).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplication> configure)
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
