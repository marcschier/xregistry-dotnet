// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Security.Authentication;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Samples;
using XRegistry.Server;

namespace XRegistry.SampleHosting.Tests;

public class RegistrySampleHostingTests
{
    [Test]
    public async Task ProductionDefaultsFailClosedWithoutImplicitDemo()
    {
        await using var fixture = new SampleHostFixture();
        var builder = fixture.Builder();
        await Assert.That(() => RegistrySampleHosting.Configure(builder,
            new() { PublicRoot = new Uri("https://registry.example/") }, fixture.GetSecret)).Throws<ArgumentException>();
        await Assert.That(fixture.SecretReads).IsEqualTo(0);
    }

    [Test]
    [Arguments("relative")]
    [Arguments("http://registry.example/")]
    [Arguments("ftp://registry.example/")]
    [Arguments("https://user:password@registry.example/")]
    [Arguments("https://@registry.example/")]
    [Arguments("https://registry.example/?query=value")]
    [Arguments("https://registry.example/?")]
    [Arguments("https://registry.example/#fragment")]
    [Arguments("https://registry.example/#")]
    [Arguments("https://registry.example/\\outside")]
    public async Task UntrustedPublicRootsAreRejectedBeforeSecretLookup(string root)
    {
        await using var fixture = new SampleHostFixture();
        var builder = fixture.Builder();
        var options = fixture.Options with { PublicRoot = new Uri(root, UriKind.RelativeOrAbsolute) };
        await Assert.That(() => RegistrySampleHosting.Configure(builder, options, fixture.GetSecret)).Throws<ArgumentException>();
        await Assert.That(fixture.SecretReads).IsEqualTo(0);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(65536)]
    public async Task InvalidListenerPortsFailBeforeSecretLookup(int port)
    {
        await using var fixture = new SampleHostFixture();
        await Assert.That(() => RegistrySampleHosting.Configure(fixture.Builder(),
            fixture.Options with { ListenPort = port }, fixture.GetSecret)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(fixture.SecretReads).IsEqualTo(0);
    }

    [Test]
    [Arguments("missing")]
    [Arguments("empty")]
    [Arguments("short")]
    [Arguments("long")]
    [Arguments("whitespace")]
    [Arguments("unicode")]
    public async Task InvalidAdministrativeTokensFailConfiguration(string kind)
    {
        await using var fixture = new SampleHostFixture();
        fixture.SetSecret(SampleHostFixture.AdminEnvironment, kind switch
        {
            "missing" => null,
            "empty" => "",
            "short" => new string('a', 31),
            "long" => new string('a', 513),
            "whitespace" => new string('a', 32) + " ",
            "unicode" => new string('\u00e9', 32),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });
        await Assert.That(() => RegistrySampleHosting.Configure(fixture.Builder(), fixture.Options, fixture.GetSecret))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments("")]
    [Arguments("9TOKEN")]
    [Arguments("TOKEN=VALUE")]
    [Arguments("TOKEN NAME")]
    public async Task SecretEnvironmentNamesMustBeExplicitAndPortable(string name)
    {
        await using var fixture = new SampleHostFixture();
        await Assert.That(() => RegistrySampleHosting.Configure(fixture.Builder(),
            fixture.Options with { WriteTokenEnvironment = name }, fixture.GetSecret)).Throws<ArgumentException>();
        await Assert.That(fixture.SecretReads).IsEqualTo(0);
    }

    [Test]
    public async Task ReadAndAdministrativeCredentialsCannotAlias()
    {
        await using var fixture = new SampleHostFixture();
        fixture.SetSecret(SampleHostFixture.ReadEnvironment, fixture.AdminToken);
        await Assert.That(() => RegistrySampleHosting.Configure(fixture.Builder(), fixture.Options, fixture.GetSecret))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments("expired")]
    [Arguments("client")]
    [Arguments("public-only")]
    [Arguments("encipherment")]
    public async Task CertificatesMustBeCurrentServerCertificatesWithPrivateKeys(string kind)
    {
        await using var fixture = new SampleHostFixture(kind);
        await Assert.That(() => RegistrySampleHosting.Configure(fixture.Builder(), fixture.Options, fixture.GetSecret))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task MissingOrIncorrectPfxPasswordIsNotSilentlyIgnored()
    {
        await using var fixture = new SampleHostFixture();
        fixture.SetSecret(SampleHostFixture.PasswordEnvironment, null);
        await Assert.That(() => RegistrySampleHosting.Configure(fixture.Builder(), fixture.Options, fixture.GetSecret))
            .Throws<ArgumentException>();
        fixture.SetSecret(SampleHostFixture.PasswordEnvironment, "fixture-only-incorrect-password");
        await Assert.That(() => RegistrySampleHosting.Configure(fixture.Builder(), fixture.Options, fixture.GetSecret))
            .Throws<System.Security.Cryptography.CryptographicException>();
    }

    [Test]
    [Arguments(SslProtocols.Tls12)]
    [Arguments(SslProtocols.Tls13)]
    public async Task SlimHostExplicitlyServesTlsWithCustomCertificateTrust(SslProtocols protocol)
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync();
        using var client = fixture.Client(protocols: protocol);
        using var request = fixture.Request(HttpMethod.Get, "whoami", "admin");
        using var response = await client.SendAsync(request);
        var principal = await response.Content.ReadAsStringAsync();
        var fields = principal.Split('|');
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(fields[0]).IsEqualTo(RegistrySampleHosting.AuthenticationScheme);
        await Assert.That(fields[1]).IsEqualTo("sample-administrator");
        await Assert.That(fields[2]).IsEqualTo(RegistrySampleHosting.AdminRole);
        await Assert.That(IPAddress.Parse(fields[3]).MapToIPv4()).IsEqualTo(IPAddress.Loopback);
        await Assert.That(fields[4]).IsEqualTo(protocol.ToString());
        await Assert.That(fixture.Address.Scheme).IsEqualTo("https");
    }

    [Test]
    public async Task UnknownCertificateRootsAreNotTrusted()
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync();
        using var client = fixture.Client(trustCertificate: false);
        using var request = fixture.Request(HttpMethod.Get, "registry", "admin");
        await Assert.That(async () =>
        {
            using var response = await client.SendAsync(request);
        }).Throws<HttpRequestException>();
        await Assert.That(fixture.EngineCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments("missing")]
    [Arguments("wrong")]
    public async Task MissingAndIncorrectCredentialsChallengeWithoutEnteringTheEngine(string credential)
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync();
        using var client = fixture.Client();
        using var request = fixture.Request(HttpMethod.Get, "registry", credential);
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Headers.WwwAuthenticate.Single().Scheme).IsEqualTo("Bearer");
        await Assert.That(response.Headers.WwwAuthenticate.Single().Parameter).IsEqualTo("realm=\"xregistry-sample\"");
        await Assert.That(fixture.EngineCalls).IsEqualTo(0);
    }

    [Test]
    public async Task AuthenticatedAdministratorWritesThroughTheCorePolicy()
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync();
        using var client = fixture.Client();
        using var write = fixture.Request(HttpMethod.Patch, "registry", "admin");
        using var result = await client.SendAsync(write);
        using var metadata = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(metadata.RootElement.GetProperty("name").GetString()).IsEqualTo("updated");
        await Assert.That(metadata.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(1);
        await Assert.That(fixture.EngineCalls).IsEqualTo(1);
    }

    [Test]
    public async Task ReaderCanReadButReceives403ForBothHostAndCoreWriteChecks()
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync();
        using var client = fixture.Client();
        using var read = fixture.Request(HttpMethod.Get, "registry", "read");
        using var readResponse = await client.SendAsync(read);
        await Assert.That(readResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var write = fixture.Request(HttpMethod.Patch, "registry", "read");
        using var writeResponse = await client.SendAsync(write);
        await Assert.That(writeResponse.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(writeResponse.Headers.WwwAuthenticate.Count).IsEqualTo(0);
        await Assert.That(fixture.EngineCalls).IsEqualTo(1);
        using var coreCheck = fixture.Request(HttpMethod.Get, "core-admin-check", "read");
        using var coreResponse = await client.SendAsync(coreCheck);
        await Assert.That(coreResponse.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(fixture.EngineCalls).IsEqualTo(2);
        using var verify = fixture.Request(HttpMethod.Get, "registry", "admin");
        using var verifyResponse = await client.SendAsync(verify);
        using var root = JsonDocument.Parse(await verifyResponse.Content.ReadAsStringAsync());
        await Assert.That(root.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false, true, 401)]
    [Arguments(false, true, true, 401)]
    [Arguments(true, false, true, 401)]
    [Arguments(true, true, false, 401)]
    [Arguments(true, true, true, 200)]
    public async Task AnonymousReadsRequireHostOptInEndpointMarkerAndCoreApproval(
        bool enabled, bool marked, bool coreAllows, int status)
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync(fixture.Options with { AllowAnonymousReads = enabled },
            markAnonymousReads: marked, coreAllowsAnonymous: coreAllows);
        using var client = fixture.Client();
        using var response = await client.GetAsync("registry");
        await Assert.That((int)response.StatusCode).IsEqualTo(status);
        await Assert.That(fixture.EngineCalls).IsEqualTo(enabled && marked ? 1 : 0);
        if (status == 200)
        {
            using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            await Assert.That(metadata.RootElement.GetProperty("registryid").GetString()).IsEqualTo("sample-fixture");
        }
        else
        {
            await Assert.That(response.Headers.WwwAuthenticate.Single().Scheme).IsEqualTo("Bearer");
        }
    }

    [Test]
    public async Task AnonymousReadPermissionDoesNotExposeHealthOrAnyWrite()
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync(fixture.Options with { AllowAnonymousReads = true }, markAnonymousReads: true);
        using var client = fixture.Client();
        using var health = await client.GetAsync("health");
        using var publicHealth = await client.GetAsync("public-health");
        using var write = await client.PatchAsync("registry", null);
        using var publicWrite = await client.PostAsync("public-write", null);
        await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(publicHealth.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await publicHealth.Content.ReadAsStringAsync()).IsEqualTo("public");
        await Assert.That(write.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(publicWrite.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(fixture.EngineCalls).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidCredentialsNeverFallBackToAnAnonymousRead()
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync(fixture.Options with { AllowAnonymousReads = true }, markAnonymousReads: true);
        using var client = fixture.Client();
        using var request = fixture.Request(HttpMethod.Get, "registry", "wrong");
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(fixture.EngineCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments("X-User", "administrator")]
    [Arguments("X-User-Roles", "registry.admin")]
    [Arguments("X-Forwarded-User", "administrator")]
    [Arguments("Forwarded", "for=127.0.0.1;proto=https;host=public.registry.example")]
    [Arguments("X-Forwarded-Proto", "https")]
    public async Task IdentityAndForwardingHeadersCannotAuthenticateCallers(string header, string value)
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync();
        using var client = fixture.Client();
        using var request = fixture.Request(HttpMethod.Get, "registry");
        request.Headers.TryAddWithoutValidation(header, value);
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(fixture.EngineCalls).IsEqualTo(0);
    }

    [Test]
    public async Task AmbientPrincipalsAreClearedAndPublicMetadataNeverUsesHostHeaders()
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync(beforeSecurity: app => app.Use((HttpContext context, RequestDelegate next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, RegistrySampleHosting.AdminRole, ClaimValueTypes.String, RegistrySampleHosting.AuthenticationScheme)],
                RegistrySampleHosting.AuthenticationScheme));
            return next(context);
        }));
        using var client = fixture.Client();
        using var unauthenticated = await client.GetAsync("registry");
        await Assert.That(unauthenticated.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var request = fixture.Request(HttpMethod.Get, "registry", "admin");
        request.Headers.Host = "localhost";
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "spoofed.invalid");
        using var response = await client.SendAsync(request);
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(metadata.RootElement.GetProperty("self").GetString())
            .IsEqualTo("https://public.registry.example/catalog");
    }

    [Test]
    [Arguments(32)]
    [Arguments(512)]
    public async Task AdministrativeTokenBoundariesAreAcceptedExactly(int length)
    {
        await using var fixture = new SampleHostFixture();
        var token = new string('a', length);
        fixture.SetSecret(SampleHostFixture.AdminEnvironment, token);
        await fixture.StartAsync();
        using var client = fixture.Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, "registry");
        request.Headers.TryAddWithoutValidation("Authorization", "bEaReR " + token);
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments("basic")]
    [Arguments("empty")]
    [Arguments("duplicate")]
    [Arguments("over-token-limit")]
    [Arguments("over-header-limit")]
    [Arguments("whitespace")]
    public async Task AuthorizationParsingIsBoundedAndNeverFallsBack(string invalid)
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync();
        using var client = fixture.Client();
        using var request = fixture.Request(HttpMethod.Get, "registry");
        if (invalid == "duplicate")
        {
            request.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + fixture.AdminToken, "Bearer " + fixture.AdminToken });
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Authorization", invalid switch
            {
                "basic" => "Basic " + fixture.AdminToken,
                "empty" => "Bearer",
                "over-token-limit" => "Bearer " + new string('a', RegistrySampleHosting.MaxTokenBytes + 1),
                "over-header-limit" => new string('a', RegistrySampleHosting.MaxAuthorizationHeaderBytes + 1),
                "whitespace" => "Bearer  " + fixture.AdminToken,
                _ => throw new ArgumentOutOfRangeException(nameof(invalid))
            });
        }

        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(fixture.EngineCalls).IsEqualTo(0);
        await Assert.That(fixture.Logs.Entries.Any(entry =>
            entry.Message.Contains(fixture.AdminToken, StringComparison.Ordinal) ||
            entry.Message.Contains(fixture.ReadToken, StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ExplicitLoopbackDemoSynthesizesAnIdentifiedPrincipalAndWarns()
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync(DemoOptions());
        using var client = fixture.Client();
        using var identity = await client.GetAsync("whoami");
        await Assert.That(await identity.Content.ReadAsStringAsync())
            .IsEqualTo($"{RegistrySampleHosting.AuthenticationScheme}|loopback-demo-administrator|{RegistrySampleHosting.AdminRole}|127.0.0.1|");
        using var write = await client.PatchAsync("registry", null);
        await Assert.That(write.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(fixture.Address.Host).IsEqualTo("127.0.0.1");
        await Assert.That(fixture.Address.Scheme).IsEqualTo("http");
        await Assert.That(fixture.SecretReads).IsEqualTo(0);
        await Assert.That(fixture.Logs.Entries.Any(entry => entry.Level == LogLevel.Warning &&
            entry.Message.Contains("INSECURE LOOPBACK DEMO", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments("http://public.registry.example/")]
    [Arguments("http://0.0.0.0/")]
    [Arguments("http://192.0.2.1/")]
    [Arguments("http://127.0.0.1.example/")]
    [Arguments("https://localhost/")]
    public async Task DemoCannotUseNonLoopbackAliasesOrImplicitTlsConfiguration(string root)
    {
        await using var fixture = new SampleHostFixture();
        await Assert.That(() => RegistrySampleHosting.Configure(fixture.Builder(),
            DemoOptions() with { PublicRoot = new Uri(root) }, fixture.GetSecret)).Throws<ArgumentException>();
        await Assert.That(fixture.SecretReads).IsEqualTo(0);
    }

    [Test]
    public async Task DemoDoesNotIgnorePresentedBearerCredentials()
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync(DemoOptions());
        using var client = fixture.Client();
        using var request = fixture.Request(HttpMethod.Get, "registry", "admin");
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(fixture.EngineCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments("192.0.2.1", 403)]
    [Arguments("", 403)]
    [Arguments("::ffff:127.0.0.1", 200)]
    public async Task DemoChecksTheConnectionPeerRatherThanForwardedHeaders(string address, int status)
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync(DemoOptions(), beforeSecurity: app => app.Use((HttpContext context, RequestDelegate next) =>
        {
            context.Connection.RemoteIpAddress = address.Length == 0 ? null : IPAddress.Parse(address);
            return next(context);
        }));
        using var client = fixture.Client();
        using var request = fixture.Request(HttpMethod.Get, "registry");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1");
        using var response = await client.SendAsync(request);
        await Assert.That((int)response.StatusCode).IsEqualTo(status);
        await Assert.That(fixture.EngineCalls).IsEqualTo(status == 200 ? 1 : 0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AmbientListenerAndProxyConfigurationCannotAddAnInsecureEndpoint(bool demo)
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync(demo ? DemoOptions() : fixture.Options, configureBuilder: builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["urls"] = "http://0.0.0.0:0",
                ["preferHostingUrls"] = "true",
                ["http_ports"] = "0",
                ["https_ports"] = "0",
                ["FORWARDEDHEADERS_ENABLED"] = "true",
                ["Kestrel:Endpoints:Backdoor:Url"] = "http://0.0.0.0:0",
                ["Kestrel:Certificates:Default:Path"] = "missing-ambient-certificate.pfx"
            }));
        using var client = fixture.Client();
        using var request = fixture.Request(HttpMethod.Get, "whoami", demo ? "missing" : "admin");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "192.0.2.1");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "http");
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var fields = (await response.Content.ReadAsStringAsync()).Split('|');
        await Assert.That(IPAddress.Parse(fields[3]).MapToIPv4()).IsEqualTo(IPAddress.Loopback);
        await Assert.That(fixture.Address.Scheme).IsEqualTo(demo ? "http" : "https");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AdditionalCodeConfiguredListenersFailStartup(bool demo)
    {
        await using var fixture = new SampleHostFixture();
        await Assert.That(async () => await fixture.StartAsync(demo ? DemoOptions() : fixture.Options,
            configureBuilder: builder => builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0))))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task SchemeHeadersCannotReplacePhysicalTlsFeatures()
    {
        await using var fixture = new SampleHostFixture();
        await Assert.That(async () => await fixture.StartAsync(
            configureBuilder: builder => builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0)),
            beforeSecurity: app => app.Use((HttpContext context, RequestDelegate next) =>
            {
                context.Request.Scheme = "https";
                return next(context);
            }))).Throws<InvalidOperationException>();
        var addresses = fixture.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var plaintext = addresses.Select(static address => new Uri(address)).Single(static address => address.Scheme == "http");
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = plaintext,
            Timeout = TimeSpan.FromSeconds(5)
        };
        using var request = fixture.Request(HttpMethod.Get, "registry");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(fixture.EngineCalls).IsEqualTo(0);
    }

    [Test]
    public async Task MissingOrMismatchedSecurityMiddlewareFailsBeforeServing()
    {
        await using var missing = new SampleHostFixture();
        await Assert.That(async () => await missing.StartAsync(useSecurity: false)).Throws<InvalidOperationException>();
        await using var mismatched = new SampleHostFixture();
        await Assert.That(async () => await mismatched.StartAsync(
            securityOptions: mismatched.Options with { AllowAnonymousReads = true })).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task DuplicateConfigurationAndMiddlewareAreRejected()
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync();
        await Assert.That(() => RegistrySampleHosting.UseSecurity(fixture.App, fixture.Options))
            .Throws<InvalidOperationException>();
        var builder = fixture.Builder();
        RegistrySampleHosting.Configure(builder, DemoOptions(), fixture.GetSecret);
        await Assert.That(() => RegistrySampleHosting.Configure(builder, DemoOptions(), fixture.GetSecret))
            .Throws<InvalidOperationException>();
        await using var unused = builder.Build();
    }

    [Test]
    public async Task CancellationReachesAuthenticatedRequestsAndSecretsAreLoadedOnlyAtStartup()
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync();
        using var client = fixture.Client();
        using var request = fixture.Request(HttpMethod.Get, "cancel", "admin");
        using var cancellation = new CancellationTokenSource();
        var pending = client.SendAsync(request, cancellation.Token);
        await fixture.RequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () =>
        {
            using var response = await pending;
        }).Throws<OperationCanceledException>();
        await fixture.RequestCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(fixture.SecretReads).IsEqualTo(3);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CorePolicyGrantsOnlyExplicitRolesAndChecksCancellation(bool allowAnonymous)
    {
        var policy = RegistrySampleHosting.CreateAuthorizationPolicy(DemoOptions() with { AllowAnonymousReads = allowAnonymous });
        var path = RegistryPath.Parse("/");
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var untrusted = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, RegistrySampleHosting.AdminRole)], "untrusted"));
        foreach (var access in Enum.GetValues<RegistryAccess>())
        {
            await Assert.That(await policy.AuthorizeAsync(anonymous, access, path))
                .IsEqualTo(allowAnonymous && access == RegistryAccess.Read);
            await Assert.That(await policy.AuthorizeAsync(untrusted, access, path)).IsFalse();
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await policy.AuthorizeAsync(anonymous, RegistryAccess.Read, path, cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task BundledIntermediateCertificatesArePresentedWithoutAmbientStores()
    {
        await using var fixture = new SampleHostFixture("chain");
        await fixture.StartAsync();
        using var client = fixture.Client();
        using var request = fixture.Request(HttpMethod.Get, "registry", "admin");
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(fixture.EngineCalls).IsEqualTo(1);
    }

    [Test]
    public async Task ExplicitCustomTrustStillRejectsTheWrongServerName()
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync();
        using var client = fixture.Client();
        using var request = fixture.Request(HttpMethod.Get, "registry", "admin");
        request.Headers.Host = "not-the-certificate.invalid";
        await Assert.That(async () =>
        {
            using var response = await client.SendAsync(request);
        }).Throws<HttpRequestException>();
        await Assert.That(fixture.EngineCalls).IsEqualTo(0);
    }

    [Test]
    public async Task ACertificateDoesNotMakeAnUnnamedAdministrativeTokenOptional()
    {
        await using var fixture = new SampleHostFixture();
        await Assert.That(() => RegistrySampleHosting.Configure(fixture.Builder(),
            fixture.Options with { WriteTokenEnvironment = null }, fixture.GetSecret)).Throws<ArgumentException>();
        await Assert.That(fixture.SecretReads).IsEqualTo(0);
    }

    [Test]
    public async Task OptionalReadCredentialsMustBeExplicitlyConfigured()
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync(fixture.Options with { ReadTokenEnvironment = null });
        using var client = fixture.Client();
        using var read = fixture.Request(HttpMethod.Get, "registry", "read");
        using var denied = await client.SendAsync(read);
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var admin = fixture.Request(HttpMethod.Get, "registry", "admin");
        using var allowed = await client.SendAsync(admin);
        await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(fixture.SecretReads).IsEqualTo(2);
        await Assert.That(fixture.EngineCalls).IsEqualTo(1);
    }

    [Test]
    public async Task ConfiguredReadCredentialsCannotBeMissingOrWeak()
    {
        await using var fixture = new SampleHostFixture();
        fixture.SetSecret(SampleHostFixture.ReadEnvironment, null);
        await Assert.That(() => RegistrySampleHosting.Configure(fixture.Builder(), fixture.Options, fixture.GetSecret))
            .Throws<ArgumentException>();
        fixture.SetSecret(SampleHostFixture.ReadEnvironment, "short-fixture-value");
        await Assert.That(() => RegistrySampleHosting.Configure(fixture.Builder(), fixture.Options, fixture.GetSecret))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments("certificate")]
    [Arguments("password")]
    [Arguments("admin")]
    [Arguments("reader")]
    public async Task DemoCannotSilentlyIgnoreProductionSecurityConfiguration(string setting)
    {
        await using var fixture = new SampleHostFixture();
        var options = setting switch
        {
            "certificate" => DemoOptions() with { CertificatePath = fixture.CertificatePath },
            "password" => DemoOptions() with { CertificatePasswordEnvironment = SampleHostFixture.PasswordEnvironment },
            "admin" => DemoOptions() with { WriteTokenEnvironment = SampleHostFixture.AdminEnvironment },
            "reader" => DemoOptions() with { ReadTokenEnvironment = SampleHostFixture.ReadEnvironment },
            _ => throw new ArgumentOutOfRangeException(nameof(setting))
        };
        await Assert.That(() => RegistrySampleHosting.Configure(fixture.Builder(), options, fixture.GetSecret))
            .Throws<ArgumentException>();
        await Assert.That(fixture.SecretReads).IsEqualTo(0);
    }

    [Test]
    public async Task DemoLocalhostMetadataStillBindsALiteralLoopbackListener()
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync(DemoOptions() with { PublicRoot = new Uri("http://localhost/registry/") });
        using var client = fixture.Client();
        using var response = await client.GetAsync("registry");
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(fixture.Address.Host).IsEqualTo("127.0.0.1");
        await Assert.That(metadata.RootElement.GetProperty("self").GetString()).IsEqualTo("http://localhost/registry");
    }

    [Test]
    public async Task FailedListenerValidationDoesNotLeaveAuthenticatedTlsRequestsServing()
    {
        await using var fixture = new SampleHostFixture();
        await Assert.That(async () => await fixture.StartAsync(configureBuilder: builder =>
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0))))
            .Throws<InvalidOperationException>();
        var addresses = fixture.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var secure = addresses.Select(static address => new Uri(address)).Single(static address => address.Scheme == "https");
        using var client = fixture.Client(address: new UriBuilder(secure) { Host = "127.0.0.1" }.Uri);
        using var request = fixture.Request(HttpMethod.Get, "registry", "admin");
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(fixture.EngineCalls).IsEqualTo(0);
    }

    [Test]
    public async Task PlainHttpCannotEnterTheConfiguredHttpsListener()
    {
        await using var fixture = new SampleHostFixture();
        await fixture.StartAsync();
        using var client = fixture.Client(address: new UriBuilder(fixture.Address) { Scheme = "http" }.Uri);
        using var request = fixture.Request(HttpMethod.Get, "registry");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        await Assert.That(async () =>
        {
            using var response = await client.SendAsync(request);
        }).Throws<HttpRequestException>();
        await Assert.That(fixture.EngineCalls).IsEqualTo(0);
        using var secureClient = fixture.Client();
        using var secureRequest = fixture.Request(HttpMethod.Get, "registry", "admin");
        using var secureResponse = await secureClient.SendAsync(secureRequest);
        await Assert.That(secureResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(fixture.EngineCalls).IsEqualTo(1);
    }

    [Test]
    public async Task AnonymousReadOptInCannotOverrideADenyingCoreAuthorizationPolicy()
    {
        await using var fixture = new SampleHostFixture();
        var options = fixture.Options with { AllowAnonymousReads = true };
        await fixture.StartAsync(options, markAnonymousReads: true,
            enginePolicy: RegistrySampleHosting.CreateAuthorizationPolicy(options with { AllowAnonymousReads = false }));
        using var client = fixture.Client();
        using var response = await client.GetAsync("registry");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(fixture.EngineCalls).IsEqualTo(1);
    }

    [Test]
    [Arguments(RegistrySampleHosting.ReadRole)]
    [Arguments(RegistrySampleHosting.AdminRole)]
    public async Task TrustedCoreRolesHaveOnlyTheirExplicitPermissions(string role)
    {
        var policy = RegistrySampleHosting.CreateAuthorizationPolicy(DemoOptions());
        var caller = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role, ClaimValueTypes.String, RegistrySampleHosting.AuthenticationScheme)],
            RegistrySampleHosting.AuthenticationScheme));
        foreach (var access in Enum.GetValues<RegistryAccess>())
        {
            await Assert.That(await policy.AuthorizeAsync(caller, access, RegistryPath.Parse("/")))
                .IsEqualTo(role == RegistrySampleHosting.AdminRole || access == RegistryAccess.Read);
        }

        await Assert.That(await policy.AuthorizeAsync(caller, (RegistryAccess)int.MaxValue, RegistryPath.Parse("/"))).IsFalse();
    }

    private static RegistrySampleHostOptions DemoOptions() => new()
    {
        PublicRoot = new Uri("http://127.0.0.1/registry/"),
        ListenPort = 0,
        DemoLoopback = true
    };
}
