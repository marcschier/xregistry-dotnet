using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XRegistry;
using XRegistry.AspNetCore;
using XRegistry.Client;
using XRegistry.Server;

namespace EmbeddingConsumer;

internal sealed class EmbeddingHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly EmbeddingCredentials _credentials;
    private RegistryEngine? _engine;
    private long _httpRequests;
    private bool _disposed;

    private EmbeddingHost(WebApplication app, EmbeddingCredentials credentials)
    {
        _app = app;
        _credentials = credentials;
    }

    internal RegistryEngine Engine => _engine ?? throw new InvalidOperationException("The Registry endpoint is not ready.");
    internal long HttpRequests => Interlocked.Read(ref _httpRequests);
    internal ConcurrentQueue<string> Requests { get; } = new();
    internal Uri Origin => new(_app.Urls.Single());

    internal static async Task<EmbeddingHost> StartAsync(RegistryModel model,
        ApplicationRegistryPersistence persistence, ApplicationRegistryPersistence readOnly,
        EmbeddingAuthorizationPolicy authorization, bool advertiseBoundRoot = false)
    {
        var credentials = new EmbeddingCredentials();
        credentials.Initialize();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, "");
        builder.WebHost.UseSetting(WebHostDefaults.PreferHostingUrlsKey, "false");
        builder.Services.PostConfigure<KestrelServerOptions>(server =>
        {
            server.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
            server.Listen(IPAddress.Loopback, 0);
        });
        builder.Services.AddSingleton(credentials);
        builder.Services.AddAuthentication(EmbeddingCredentials.Scheme)
            .AddScheme<AuthenticationSchemeOptions, EmbeddingAuthenticationHandler>(EmbeddingCredentials.Scheme, static _ => { });
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var app = builder.Build();
        var transferred = false;
        try
        {
            var options = new RegistryEngineOptions
            {
                RegistryId = "application-registry",
                PublicRoot = new Uri("https://embedding.example/registry"),
                Model = model,
                AllowAnonymousReads = false,
                Limits = new RegistryLimits
                {
                    MaxDocumentBytes = 64 * 1024,
                    Json = new RegistryJsonLimits { MaxBytes = 64 * 1024, MaxDepth = 64 }
                }
            };
            var host = new EmbeddingHost(app, credentials);
            app.Use((HttpContext context, RequestDelegate next) =>
            {
                if (context.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }

                Interlocked.Increment(ref host._httpRequests);
                host.Requests.Enqueue(context.Request.Method + " " + context.Features.Get<IHttpRequestFeature>()!.RawTarget);
                return next(context);
            });
            app.UseAuthentication();
            if (advertiseBoundRoot)
            {
                // A dynamic loopback port is test-only; install routing before discovering its actual address.
                app.MapGet("/embedding-starting", (RequestDelegate)(context =>
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return Task.CompletedTask;
                }));
                app.UseRouting();
                app.UseEndpoints(static _ => { });
            }
            else
            {
                Map(options);
            }
            await app.StartAsync().ConfigureAwait(false);
            if (advertiseBoundRoot)
            {
                Map(options with { PublicRoot = new Uri(host.Origin, "/registry") });
            }
            transferred = true;
            return host;

            void Map(RegistryEngineOptions configured)
            {
                host._engine = new RegistryEngine(configured, persistence, authorization);
                var frozen = new RegistryEngine(configured, readOnly, authorization);
                app.MapXRegistry(host._engine, new RegistryHttpOptions { MountPath = "/registry", AuthenticationChallenge = "Bearer" });
                app.MapXRegistry(frozen, new RegistryHttpOptions { MountPath = "/frozen", AuthenticationChallenge = "Bearer" });
            }
        }
        finally
        {
            if (!transferred)
            {
                await app.DisposeAsync().ConfigureAwait(false);
                credentials.Dispose();
            }
        }
    }

    internal XRegistryHttpClient Client(string role, string mount = "registry") =>
        new(new Uri(Origin, "/" + mount + "/"), TransportOptions(role));

    internal XRegistryHttpClientOptions TransportOptions(string role) => new()
    {
        AllowLoopbackHttp = true,
        RequestTimeout = TimeSpan.FromSeconds(10),
        MaxMetadataBytes = 64 * 1024,
        MaxDocumentBytes = 64 * 1024,
        AuthorizationProvider = (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<System.Net.Http.Headers.AuthenticationHeaderValue?>(role switch
            {
                "writer" => new("Bearer", _credentials.WriterToken),
                "reader" => new("Bearer", _credentials.ReaderToken),
                "anonymous" => null,
                _ => throw new ArgumentOutOfRangeException(nameof(role))
            });
        }
    };

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _credentials.Dispose();
        }
    }
}
