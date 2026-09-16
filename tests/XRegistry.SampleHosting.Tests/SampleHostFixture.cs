// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XRegistry.Samples;
using XRegistry.Server;

namespace XRegistry.SampleHosting.Tests;

internal sealed class SampleHostFixture : IAsyncDisposable
{
    internal const string AdminEnvironment = "FIXTURE_REGISTRY_ADMIN";
    internal const string ReadEnvironment = "FIXTURE_REGISTRY_READ";
    internal const string PasswordEnvironment = "FIXTURE_REGISTRY_PFX";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xregistry-sample-host-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _secrets = new(StringComparer.Ordinal);
    private int _secretReads;
    private int _engineCalls;
    private WebApplication? _app;

    internal SampleHostFixture(string certificateKind = "server")
    {
        Directory.CreateDirectory(_directory);
        AdminToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        ReadToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _secrets.Add(AdminEnvironment, AdminToken);
        _secrets.Add(ReadEnvironment, ReadToken);
        _secrets.Add(PasswordEnvironment, password);

        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=xRegistry fixture root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(3));
        RootCertificate = X509CertificateLoader.LoadCertificate(root.Export(X509ContentType.Cert));
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafRequest = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            certificateKind == "encipherment" ? X509KeyUsageFlags.KeyEncipherment : X509KeyUsageFlags.DigitalSignature, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(certificateKind == "client" ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        leafRequest.CertificateExtensions.Add(names.Build());
        var expires = certificateKind == "expired" ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(1);
        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = new CertificateRequest("CN=xRegistry fixture intermediate", intermediateKey, HashAlgorithmName.SHA256);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        intermediateRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intermediateRequest.PublicKey, false));
        using var intermediateIssued = intermediateRequest.Create(root, DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddDays(2), RandomNumberGenerator.GetBytes(16));
        using var intermediate = intermediateIssued.CopyWithPrivateKey(intermediateKey);
        using var issued = leafRequest.Create(certificateKind == "chain" ? intermediate : root,
            DateTimeOffset.UtcNow.AddDays(certificateKind == "chain" ? -1 : -2), expires, RandomNumberGenerator.GetBytes(16));
        using var leaf = issued.CopyWithPrivateKey(leafKey);
        CertificatePath = Path.Combine(_directory, "fixture.pfx");
        X509Certificate2Collection pfx = [certificateKind == "public-only" ? issued : leaf];
        if (certificateKind == "chain")
        {
            pfx.Add(intermediateIssued);
            pfx.Add(RootCertificate);
        }

        File.WriteAllBytes(CertificatePath, pfx.Export(X509ContentType.Pfx, password)!);
        Options = new()
        {
            PublicRoot = new Uri("https://public.registry.example/catalog/"),
            ListenPort = 0,
            CertificatePath = CertificatePath,
            CertificatePasswordEnvironment = PasswordEnvironment,
            WriteTokenEnvironment = AdminEnvironment,
            ReadTokenEnvironment = ReadEnvironment
        };
    }

    internal RegistrySampleHostOptions Options { get; }
    internal string AdminToken { get; }
    internal string ReadToken { get; }
    internal string CertificatePath { get; }
    internal X509Certificate2 RootCertificate { get; }
    internal CapturedLogs Logs { get; } = new();
    internal WebApplication App => _app ?? throw new InvalidOperationException("The fixture has not started.");
    internal RegistryEngine Engine { get; private set; } = null!;
    internal int SecretReads => Volatile.Read(ref _secretReads);
    internal int EngineCalls => Volatile.Read(ref _engineCalls);
    internal TaskCompletionSource RequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource RequestCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void SetSecret(string name, string? value) => _secrets[name] = value;

    internal string? GetSecret(string name)
    {
        Interlocked.Increment(ref _secretReads);
        return _secrets.TryGetValue(name, out var value) ? value : null;
    }

    internal WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = "Production",
            ContentRootPath = _directory
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(Logs);
        return builder;
    }

    internal async Task StartAsync(RegistrySampleHostOptions? options = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        Action<WebApplication>? beforeSecurity = null,
        bool markAnonymousReads = false,
        bool coreAllowsAnonymous = true,
        bool useSecurity = true,
        RegistrySampleHostOptions? securityOptions = null,
        IRegistryAuthorizationPolicy? enginePolicy = null)
    {
        options ??= Options;
        var builder = Builder();
        configureBuilder?.Invoke(builder);
        RegistrySampleHosting.Configure(builder, options, GetSecret);
        _app = builder.Build();
        beforeSecurity?.Invoke(_app);
        if (useSecurity)
        {
            RegistrySampleHosting.UseSecurity(_app, securityOptions ?? options);
        }

        Engine = new RegistryEngine(new()
        {
            PublicRoot = options.PublicRoot,
            RegistryId = "sample-fixture",
            Model = RegistryModel.Compile(RegistryJson.Parse("""{"groups":{}}""")),
            AllowAnonymousReads = options.AllowAnonymousReads && coreAllowsAnonymous
        }, new InMemoryRegistryPersistence(), enginePolicy ?? _app.Services.GetRequiredService<IRegistryAuthorizationPolicy>());
        var registry = _app.Map("/registry", (RequestDelegate)InvokeRegistryAsync);
        if (markAnonymousReads)
        {
            registry.AllowAnonymousRegistryReads();
        }

        _app.MapGet("/core-admin-check", (RequestDelegate)(context => InvokeCoreAsync(context, RegistryAction.Patch)));
        _app.MapGet("/health", (RequestDelegate)(context => context.Response.WriteAsync("healthy", context.RequestAborted)));
        _app.MapGet("/public-health", (RequestDelegate)(context => context.Response.WriteAsync("public", context.RequestAborted)))
            .AllowAnonymous();
        _app.MapPost("/public-write", (RequestDelegate)(context => context.Response.WriteAsync("must remain protected", context.RequestAborted)))
            .AllowAnonymous();
        _app.MapGet("/whoami", (RequestDelegate)(context => context.Response.WriteAsync(
            $"{context.User.Identity?.AuthenticationType}|{context.User.Identity?.Name}|{context.User.FindFirst(ClaimTypes.Role)?.Value}|{context.Connection.RemoteIpAddress}|{context.Features.Get<ITlsHandshakeFeature>()?.Protocol}",
            context.RequestAborted)));
        _app.MapGet("/cancel", (RequestDelegate)(async context =>
        {
            RequestEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                RequestCanceled.TrySetResult();
            }
        }));
        await _app.StartAsync();
    }

    internal Uri Address
    {
        get
        {
            var server = App.Services.GetRequiredService<IServer>();
            var bound = new Uri(server.Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            return new UriBuilder(bound) { Host = "127.0.0.1" }.Uri;
        }
    }

    internal HttpClient Client(bool trustCertificate = true, SslProtocols? protocols = null, Uri? address = null)
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
        if (trustCertificate)
        {
            handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
                VerificationFlags = X509VerificationFlags.NoFlag
            };
            handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(RootCertificate);
        }

        if (protocols is { } selected)
        {
            handler.SslOptions.EnabledSslProtocols = selected;
        }

        return new HttpClient(handler) { BaseAddress = address ?? Address, Timeout = TimeSpan.FromSeconds(10) };
    }

    internal HttpRequestMessage Request(HttpMethod method, string path, string credential = "missing")
    {
        var request = new HttpRequestMessage(method, path);
        if (credential != "missing")
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential switch
            {
                "admin" => AdminToken,
                "read" => ReadToken,
                "wrong" => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
                _ => throw new ArgumentOutOfRangeException(nameof(credential))
            });
        }

        return request;
    }

    private Task InvokeRegistryAsync(HttpContext context) =>
        InvokeCoreAsync(context, HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
            ? RegistryAction.Read : HttpMethods.IsOptions(context.Request.Method) ? RegistryAction.Options : RegistryAction.Patch);

    private async Task InvokeCoreAsync(HttpContext context, RegistryAction action)
    {
        Interlocked.Increment(ref _engineCalls);
        try
        {
            var result = await Engine.ExecuteAsync(new(action, RegistryPath.Parse("/"))
            {
                Metadata = action == RegistryAction.Patch ? RegistryJson.Parse("""{"name":"updated"}""") : null
            }, new RegistryOperationContext(context.User), context.RequestAborted);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(result.Metadata?.RootElement.GetRawText() ?? "{}", context.RequestAborted);
        }
        catch (RegistryException exception) when (exception.Diagnostic.Code is "unauthorized" or "forbidden")
        {
            context.Response.StatusCode = exception.Diagnostic.Code == "unauthorized" ? 401 : 403;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        RootCertificate.Dispose();
        File.Delete(CertificatePath);
        Directory.Delete(_directory);
    }
}

internal sealed class CapturedLogs : ILoggerProvider
{
    internal ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new CaptureLogger(Entries);
    public void Dispose() { }

    private sealed class CaptureLogger(ConcurrentQueue<(LogLevel Level, string Message)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception)));
    }
}
