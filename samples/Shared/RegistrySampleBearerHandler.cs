// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace XRegistry.Samples;

internal sealed class RegistrySampleBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    RegistrySampleSecurityState state) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Context.RequestAborted.ThrowIfCancellationRequested();
        var values = Request.Headers.Authorization;
        if (values.Count == 0)
        {
            return Task.FromResult(state.Options.DemoLoopback
                ? Success(RegistrySampleHosting.AdminRole, demo: true)
                : AuthenticateResult.NoResult());
        }

        var header = values.Count == 1 ? values[0] : null;
        if (state.Options.DemoLoopback || header is null ||
            header.Length > RegistrySampleHosting.MaxAuthorizationHeaderBytes ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            !RegistrySampleSecurityState.IsToken(header.AsSpan(7)))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid sample bearer credentials."));
        }

        Span<byte> bytes = stackalloc byte[RegistrySampleHosting.MaxTokenBytes];
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        try
        {
            var length = Encoding.UTF8.GetBytes(header.AsSpan(7), bytes);
            SHA256.HashData(bytes[..length], digest);
            var administrator = CryptographicOperations.FixedTimeEquals(digest, state.AdminTokenHash);
            var reader = state.ReadTokenHash is { } readHash && CryptographicOperations.FixedTimeEquals(digest, readHash);
            return Task.FromResult(administrator ? Success(RegistrySampleHosting.AdminRole, demo: false) :
                reader ? Success(RegistrySampleHosting.ReadRole, demo: false) :
                AuthenticateResult.Fail("Invalid sample bearer credentials."));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = RegistrySampleHosting.AuthenticationChallenge;
        Response.Headers.CacheControl = "no-store";
        Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.Headers.CacheControl = "no-store";
        Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    private AuthenticateResult Success(string role, bool demo) =>
        AuthenticateResult.Success(new AuthenticationTicket(
            RegistrySampleAuthorizationPolicy.Principal(role, demo), Scheme.Name));
}
