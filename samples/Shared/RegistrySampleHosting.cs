using System.Security.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XRegistry.Server;

namespace XRegistry.Samples;

/// <summary>Explicit sample-host configuration, independent of incoming HTTP headers.</summary>
public sealed record RegistrySampleHostOptions
{
    public required Uri PublicRoot { get; init; }
    public int ListenPort { get; init; } = 8443;
    public bool DemoLoopback { get; init; }
    public bool AllowAnonymousReads { get; init; }
    public string? CertificatePath { get; init; }
    public string? CertificatePasswordEnvironment { get; init; }
    public string? WriteTokenEnvironment { get; init; }
    public string? ReadTokenEnvironment { get; init; }
}

/// <summary>Configures explicit listeners, sample authentication and fail-closed request authorization.</summary>
public static class RegistrySampleHosting
{
    public const string AuthenticationScheme = "RegistrySampleBearer";
    public const string AuthenticationChallenge = "Bearer realm=\"xregistry-sample\"";
    public const string ReadRole = "registry.read";
    public const string AdminRole = "registry.admin";
    public const int MaxAuthorizationHeaderBytes = 1024;
    public const int MaxTokenBytes = 512;

    /// <summary>Call once before Build. The optional provider resolves only the explicitly named secrets.</summary>
    public static void Configure(WebApplicationBuilder builder, RegistrySampleHostOptions options,
        Func<string, string?>? secretProvider = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        if (builder.Services.Any(static service => service.ServiceType == typeof(RegistrySampleSecurityState)))
        {
            throw new InvalidOperationException("Sample hosting has already been configured.");
        }

        var state = RegistrySampleSecurityState.Create(options, secretProvider ?? Environment.GetEnvironmentVariable);
        var registered = false;
        try
        {
            builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, "");
            builder.WebHost.UseSetting(WebHostDefaults.PreferHostingUrlsKey, "false");
            builder.WebHost.UseSetting("http_ports", "");
            builder.WebHost.UseSetting("https_ports", "");
            builder.WebHost.UseSetting("FORWARDEDHEADERS_ENABLED", "false");
            builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
            builder.WebHost.UseKestrelHttpsConfiguration();
            builder.Services.PostConfigure<ForwardedHeadersOptions>(
                static forwarded => forwarded.ForwardedHeaders = ForwardedHeaders.None);
            builder.Services.PostConfigure<KestrelServerOptions>(server =>
            {
                // Replace the ambient Kestrel loader, including endpoint/certificate reload configuration.
                server.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
                server.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
                server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                if (options.DemoLoopback)
                {
                    server.Listen(state.DemoAddress, options.ListenPort,
                        static listener => listener.Protocols = HttpProtocols.Http1AndHttp2);
                }
                else
                {
                    server.ListenAnyIP(options.ListenPort, listener =>
                    {
                        listener.Protocols = HttpProtocols.Http1AndHttp2;
                        listener.UseHttps(https =>
                        {
                            https.ServerCertificate = state.Certificate;
                            https.ServerCertificateChain = state.Certificates;
                            https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                            https.ClientCertificateMode = ClientCertificateMode.NoCertificate;
                            https.HandshakeTimeout = TimeSpan.FromSeconds(10);
                        });
                    });
                }
            });

            builder.Services.AddSingleton(_ => state);
            builder.Services.AddSingleton<IStartupFilter, RegistrySampleStartupGuard>();
            builder.Services.AddSingleton<IHostedService, RegistrySampleListenerGuard>();
            builder.Services.AddSingleton(CreateAuthorizationPolicy(options));
            builder.Services.AddAuthentication(authentication =>
            {
                authentication.DefaultScheme = AuthenticationScheme;
                authentication.DefaultAuthenticateScheme = AuthenticationScheme;
                authentication.DefaultChallengeScheme = AuthenticationScheme;
                authentication.DefaultForbidScheme = AuthenticationScheme;
            }).AddScheme<AuthenticationSchemeOptions, RegistrySampleBearerHandler>(AuthenticationScheme, static _ => { });
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddAuthorization(authorization =>
            {
                var policy = new AuthorizationPolicyBuilder(AuthenticationScheme)
                    .RequireAssertion(context => AuthorizeHttp(context, options))
                    .Build();
                authorization.DefaultPolicy = policy;
                authorization.FallbackPolicy = policy;
            });
            registered = true;
        }
        finally
        {
            if (!registered)
            {
                state.Dispose();
            }
        }
    }

    /// <summary>Call immediately after Build, before response-producing middleware or endpoints.</summary>
    public static void UseSecurity(WebApplication app, RegistrySampleHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        var state = app.Services.GetRequiredService<RegistrySampleSecurityState>();
        if (state.Options != options)
        {
            throw new InvalidOperationException("Security options must match the configured sample host.");
        }

        state.MarkSecurityApplied();
        app.Use((HttpContext context, RequestDelegate next) => CheckTransportAsync(context, next, state));
        app.UseRouting();
        app.UseAuthentication();
        app.Use((HttpContext context, RequestDelegate next) => CheckAuthenticationAsync(context, next));
        app.UseAuthorization();
    }

    /// <summary>Creates the explicit policy also registered as IRegistryAuthorizationPolicy by Configure.</summary>
    public static IRegistryAuthorizationPolicy CreateAuthorizationPolicy(RegistrySampleHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new RegistrySampleAuthorizationPolicy(options.AllowAnonymousReads);
    }

    /// <summary>Marks a Registry endpoint for anonymous reads, still gated by host options and the core policy.</summary>
    public static TBuilder AllowAnonymousRegistryReads<TBuilder>(this TBuilder endpoint)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        endpoint.Add(static builder => builder.Metadata.Add(new RegistrySampleAnonymousReadMetadata()));
        return endpoint;
    }

    private static bool AuthorizeHttp(AuthorizationHandlerContext context, RegistrySampleHostOptions options)
    {
        if (RegistrySampleAuthorizationPolicy.HasRole(context.User, AdminRole))
        {
            return true;
        }

        if (context.Resource is not HttpContext http || !IsReadMethod(http.Request.Method))
        {
            return false;
        }

        return RegistrySampleAuthorizationPolicy.HasRole(context.User, ReadRole) ||
            (options.AllowAnonymousReads && !context.User.Identities.Any(static identity => identity.IsAuthenticated) &&
                http.GetEndpoint()?.Metadata.GetMetadata<RegistrySampleAnonymousReadMetadata>() is not null);
    }

    private static async Task CheckTransportAsync(HttpContext context, RequestDelegate next, RegistrySampleSecurityState state)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        var allowed = state.Options.DemoLoopback
            ? RegistrySampleSecurityState.IsLoopback(context.Connection.RemoteIpAddress)
            : context.Features.Get<ITlsConnectionFeature>() is not null &&
                context.Features.Get<ITlsHandshakeFeature>()?.Protocol is SslProtocols.Tls12 or SslProtocols.Tls13;
        if (!allowed)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentLength = 0;
            return;
        }

        var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        if (!state.ListenerVerified || !lifetime.ApplicationStarted.IsCancellationRequested ||
            lifetime.ApplicationStopping.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentLength = 0;
            return;
        }

        // Neither an ambient host principal nor a header-mapping middleware authenticates this sample.
        context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        context.Response.OnStarting(static state =>
        {
            var response = (HttpResponse)state;
            if (response.StatusCode == StatusCodes.Status401Unauthorized && response.Headers.WWWAuthenticate.Count == 0)
            {
                response.Headers.WWWAuthenticate = AuthenticationChallenge;
            }

            return Task.CompletedTask;
        }, context.Response);
        await next(context).ConfigureAwait(false);
    }

    private static async Task CheckAuthenticationAsync(HttpContext context, RequestDelegate next)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        var authentication = await context.AuthenticateAsync(AuthenticationScheme).ConfigureAwait(false);
        if (authentication.Failure is not null ||
            (!IsReadMethod(context.Request.Method) && !authentication.Succeeded))
        {
            await context.ChallengeAsync(AuthenticationScheme).ConfigureAwait(false);
            return;
        }

        if (!IsReadMethod(context.Request.Method) &&
            !RegistrySampleAuthorizationPolicy.HasRole(context.User, AdminRole))
        {
            await context.ForbidAsync(AuthenticationScheme).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private static bool IsReadMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private sealed class RegistrySampleAnonymousReadMetadata;
}
