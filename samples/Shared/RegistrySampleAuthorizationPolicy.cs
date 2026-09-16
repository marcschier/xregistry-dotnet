// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using XRegistry.Server;

namespace XRegistry.Samples;

internal sealed class RegistrySampleAuthorizationPolicy(bool allowAnonymousReads) : IRegistryAuthorizationPolicy
{
    public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(access))
        {
            return ValueTask.FromResult(false);
        }

        var allowed = HasRole(caller, RegistrySampleHosting.AdminRole) ||
            (access == RegistryAccess.Read && (HasRole(caller, RegistrySampleHosting.ReadRole) ||
                (allowAnonymousReads && !caller.Identities.Any(static identity => identity.IsAuthenticated))));
        return ValueTask.FromResult(allowed);
    }

    internal static bool HasRole(ClaimsPrincipal caller, string role) =>
        caller.Identities.Any(identity => identity.IsAuthenticated &&
            identity.AuthenticationType == RegistrySampleHosting.AuthenticationScheme &&
            identity.Claims.Any(claim => claim.Type == ClaimTypes.Role && claim.Value == role &&
                claim.Issuer == RegistrySampleHosting.AuthenticationScheme));

    internal static ClaimsPrincipal Principal(string role, bool demo)
    {
        var name = demo ? "loopback-demo-administrator" :
            role == RegistrySampleHosting.AdminRole ? "sample-administrator" : "sample-reader";
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, name, ClaimValueTypes.String, RegistrySampleHosting.AuthenticationScheme),
            new Claim(ClaimTypes.Name, name, ClaimValueTypes.String, RegistrySampleHosting.AuthenticationScheme),
            new Claim(ClaimTypes.Role, role, ClaimValueTypes.String, RegistrySampleHosting.AuthenticationScheme),
            new Claim("urn:xregistry:sample:mode", demo ? "loopback-demo" : "bearer",
                ClaimValueTypes.String, RegistrySampleHosting.AuthenticationScheme)
        ], RegistrySampleHosting.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role));
    }
}
