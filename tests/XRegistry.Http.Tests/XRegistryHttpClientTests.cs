// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Client;

namespace XRegistry.Http.Tests;

public class XRegistryHttpClientTests
{
    [Test]
    public async Task RegistryPrefixAndOrderedRepeatedParametersArePreserved()
    {
        string? target = null;
        await using var app = await StartAsync(app => app.MapGet("/mount/dirs/d1", (HttpContext context) =>
        {
            target = context.Request.Path + context.Request.QueryString;
            return Results.Text("""{"dirid":"d1","epoch":0}""", "application/json");
        }));
        using var client = new XRegistryHttpClient(
            new Uri(new Uri(app.Urls.Single()), "/mount"), new XRegistryHttpClientOptions { AllowLoopbackHttp = true });
        using var response = await client.SendAsync(
            HttpMethod.Get, "/dirs/d1",
            query: [new("filter", "a=1"), new("filter", "a=2"), new("inline", null), new("empty", "")]);
        using var body = await response.ReadMetadataAsync();

        await Assert.That(target).IsEqualTo("/mount/dirs/d1?filter=a%3D1&filter=a%3D2&inline&empty=");
        await Assert.That(body.RootElement.GetProperty("dirid").GetString()).IsEqualTo("d1");
        await Assert.That(body.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task RawDocumentBytesAndErrorResponsesRemainDistinct()
    {
        await using var app = await StartAsync(app =>
        {
            app.MapGet("/registry/document", static () => Results.Bytes([0, 255, 13, 10], "application/octet-stream"));
            app.MapGet("/registry/rejection", static () =>
                Results.Text("""{"type":"urn:xregistry:bad_request","detail":"rejected"}""", "application/problem+json", statusCode: 400));
        });
        using var client = new XRegistryHttpClient(
            new Uri(new Uri(app.Urls.Single()), "/registry/"), new XRegistryHttpClientOptions { AllowLoopbackHttp = true });
        using var document = await client.SendAsync(HttpMethod.Get, "document");
        using var destination = new MemoryStream();
        await document.CopyDocumentToAsync(destination);
        await Assert.That(Convert.ToHexString(destination.ToArray())).IsEqualTo("00FF0D0A");

        using var rejection = await client.SendAsync(HttpMethod.Get, "rejection");
        using var error = await rejection.ReadMetadataAsync();
        await Assert.That(rejection.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(error.RootElement.GetProperty("detail").GetString()).IsEqualTo("rejected");
    }

    [Test]
    public async Task MutationIsSentExactlyOnceAndPresentEmptyBodyRemainsPresent()
    {
        var requests = 0;
        long? length = null;
        await using var app = await StartAsync(app => app.MapPut("/registry/item", (HttpContext context) =>
        {
            Interlocked.Increment(ref requests);
            length = context.Request.ContentLength;
            return Results.StatusCode(503);
        }));
        using var client = new XRegistryHttpClient(
            new Uri(new Uri(app.Urls.Single()), "/registry/"), new XRegistryHttpClientOptions { AllowLoopbackHttp = true });
        using var response = await client.SendAsync(
            HttpMethod.Put, "item", ReadOnlyMemory<byte>.Empty, "application/octet-stream");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(length).IsEqualTo(0);
    }

    [Test]
    [Arguments("../outside")]
    [Arguments("%2e%2e/outside")]
    [Arguments("/%2Foutside")]
    [Arguments("//outside")]
    [Arguments("https://foreign.invalid/")]
    [Arguments("item?filter=evil")]
    [Arguments("item#fragment")]
    [Arguments("item\\outside")]
    [Arguments("item/%")]
    public async Task UnsafeRequestTargetsAreRejectedBeforeNetwork(string path)
    {
        using var client = new XRegistryHttpClient(new Uri("https://registry.example/mount/"));

        await Assert.That(async () =>
        {
            using var response = await client.SendAsync(HttpMethod.Get, path);
        }).Throws<ArgumentException>();
    }

    [Test]
    public async Task MetadataAndDocumentLimitsRejectActualExtraBytes()
    {
        await using var app = await StartAsync(app => app.MapGet("/registry/item",
            static () => Results.Text("""{"value":42}""", "application/json")));
        using var client = new XRegistryHttpClient(
            new Uri(new Uri(app.Urls.Single()), "/registry/"),
            new XRegistryHttpClientOptions { AllowLoopbackHttp = true, MaxMetadataBytes = 11, MaxDocumentBytes = 11 });
        using var response = await client.SendAsync(HttpMethod.Get, "item");
        await Assert.That(async () =>
        {
            using var body = await response.ReadMetadataAsync();
        }).Throws<InvalidDataException>();

        using var second = await client.SendAsync(HttpMethod.Get, "item");
        using var destination = new MemoryStream();
        await Assert.That(async () => await second.CopyDocumentToAsync(destination)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task RepeatedResponseBodyConsumptionIsRejected()
    {
        await using var app = await StartAsync(app => app.MapGet("/registry/item", static () => Results.Text("{}")));
        using var client = new XRegistryHttpClient(
            new Uri(new Uri(app.Urls.Single()), "/registry/"), new XRegistryHttpClientOptions { AllowLoopbackHttp = true });
        using var response = await client.SendAsync(HttpMethod.Get, "item");
        using var first = await response.ReadMetadataAsync();

        await Assert.That(async () =>
        {
            using var second = await response.ReadMetadataAsync();
        }).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task AuthorizationRunsOnlyForValidatedRequestsAndNeverRetriesAChallenge()
    {
        var credentialRequests = 0;
        var receivedRequests = 0;
        string? authorization = null;
        await using var app = await StartAsync(app => app.MapGet("/registry/item", (HttpContext context) =>
        {
            Interlocked.Increment(ref receivedRequests);
            authorization = context.Request.Headers.Authorization.ToString();
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.StatusCode(401);
        }));
        var root = new Uri(new Uri(app.Urls.Single()), "/registry/");
        using var client = new XRegistryHttpClient(root, new XRegistryHttpClientOptions
        {
            AllowLoopbackHttp = true,
            AuthorizationProvider = (uri, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (uri.AbsolutePath != "/registry/item")
                {
                    throw new InvalidOperationException("The provider received an unexpected target.");
                }

                Interlocked.Increment(ref credentialRequests);
                return ValueTask.FromResult<AuthenticationHeaderValue?>(new("Bearer", "fixture-only"));
            }
        });

        await Assert.That(async () =>
        {
            using var invalid = await client.SendAsync(HttpMethod.Get, "../outside");
        }).Throws<ArgumentException>();
        await Assert.That(credentialRequests).IsEqualTo(0);
        using var response = await client.SendAsync(HttpMethod.Get, "item");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(authorization).IsEqualTo("Bearer fixture-only");
        await Assert.That(credentialRequests).IsEqualTo(1);
        await Assert.That(receivedRequests).IsEqualTo(1);
    }

    [Test]
    public async Task OriginalDeadlineRemainsActiveAfterHeadersArrive()
    {
        await using var app = await StartAsync(app => app.MapGet("/registry/slow", static async (HttpContext context) =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.StartAsync();
            await context.Response.WriteAsync("{", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
                await context.Response.WriteAsync("}", context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The test deliberately cancels body consumption after receiving headers.
            }
        }));
        using var client = new XRegistryHttpClient(
            new Uri(new Uri(app.Urls.Single()), "/registry/"),
            new XRegistryHttpClientOptions
            {
                AllowLoopbackHttp = true,
                RequestTimeout = TimeSpan.FromSeconds(2)
            });
        using var response = await client.SendAsync(HttpMethod.Get, "slow");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(async () =>
        {
            using var metadata = await response.ReadMetadataAsync();
        }).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task InvalidLimitsAndDisposedClientsFailBeforeDispatch()
    {
        var root = new Uri("https://example.invalid/");
        await Assert.That(() => new XRegistryHttpClient(root,
            new XRegistryHttpClientOptions { MaxMetadataBytes = 0 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new XRegistryHttpClient(root,
            new XRegistryHttpClientOptions { MaxJsonDepth = 257 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new XRegistryHttpClient(root,
            new XRegistryHttpClientOptions { MaxDocumentBytes = -1 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new XRegistryHttpClient(root,
            new XRegistryHttpClientOptions { RequestTimeout = Timeout.InfiniteTimeSpan }))
            .Throws<ArgumentOutOfRangeException>();

        using var client = new XRegistryHttpClient(root);
        client.Dispose();
        await Assert.That(async () =>
        {
            using var response = await client.SendAsync(HttpMethod.Get);
        }).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task DisposingAResponseCancelsAnActiveDestinationWrite()
    {
        await using var app = await StartAsync(app => app.MapGet("/registry/item",
            static () => Results.Bytes([1, 2, 3], "application/octet-stream")));
        using var client = new XRegistryHttpClient(
            new Uri(new Uri(app.Urls.Single()), "/registry/"),
            new XRegistryHttpClientOptions { AllowLoopbackHttp = true });
        using var response = await client.SendAsync(HttpMethod.Get, "item");
        using var destination = new CancellationAwareDestination();
        var copy = response.CopyDocumentToAsync(destination).AsTask();
        await destination.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        response.Dispose();

        await Assert.That(async () => await copy.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task StreamingUploadPreservesExactBytesAndCallerStreamOwnership()
    {
        string? received = null;
        var requests = 0;
        await using var app = await StartAsync(app => app.MapPut("/registry/document", async (HttpContext context) =>
        {
            requests++;
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            received = Convert.ToHexString(body.ToArray());
            return Results.StatusCode(201);
        }));
        using var source = new MemoryStream([0, 255, 13, 10, 1]);
        using var client = new XRegistryHttpClient(
            new Uri(new Uri(app.Urls.Single()), "/registry/"),
            new XRegistryHttpClientOptions { AllowLoopbackHttp = true });
        using var response = await client.SendDocumentAsync(HttpMethod.Put, "document", source, "application/octet-stream");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(received).IsEqualTo("00FF0D0A01");
        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(source.CanRead).IsTrue();
    }

    [Test]
    public async Task OversizedSeekableUploadsAreRejectedBeforeCredentialsOrDispatch()
    {
        var credentials = 0;
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var client = new XRegistryHttpClient(new Uri("https://example.invalid/"), new()
        {
            MaxDocumentBytes = 3,
            AuthorizationProvider = (_, _) =>
            {
                credentials++;
                return ValueTask.FromResult<AuthenticationHeaderValue?>(null);
            }
        });
        await Assert.That(async () =>
        {
            using var response = await client.SendDocumentAsync(HttpMethod.Put, "document", source, "application/octet-stream");
        }).Throws<ArgumentException>();
        await Assert.That(credentials).IsEqualTo(0);
        await Assert.That(source.Position).IsEqualTo(0L);
        await Assert.That(source.CanRead).IsTrue();
    }

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

    private sealed class CancellationAwareDestination : Stream
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
