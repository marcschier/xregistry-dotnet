// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net.Http.Headers;
using XRegistry.Bindings.Git.Objects;

namespace XRegistry.Bindings.Git;

/// <summary>Explicit transport, resource, and trust settings for managed Git acquisition.</summary>
public sealed record GitFetchOptions
{
    /// <summary>Gets cumulative verified object/pack/decompression limits.</summary>
    public GitReadLimits ObjectLimits { get; init; } = new();
    /// <summary>Gets the maximum advertisement or ls-refs response bytes.</summary>
    public int MaxControlBytes { get; init; } = 1024 * 1024;
    /// <summary>Gets the maximum advertised references or capability lines.</summary>
    public int MaxReferences { get; init; } = 16384;
    /// <summary>Gets a deadline covering all requests, bodies, and object verification.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Gets whether an explicit localhost/literal-loopback HTTP test endpoint is permitted.</summary>
    public bool AllowLoopbackHttp { get; init; }
    /// <summary>Gets whether this HTTPS origin may resolve to private network addresses.</summary>
    public bool AllowPrivateOrigin { get; init; }
    /// <summary>Gets an explicitly trusted SHA-256 for registry.json, required for unhardened SHA-1 repositories.</summary>
    /// <remarks>It must come from trusted caller configuration, not from the same untrusted acquisition.</remarks>
    public string? TrustedRegistryRootSha256 { get; init; }
    /// <summary>Gets the Git binding storage root; empty selects the repository tree root.</summary>
    public string RootPath { get; init; } = "xregistry";
    /// <summary>Gets an origin-scoped credential provider; no credential-helper process or automatic retry is used.</summary>
    public Func<Uri, CancellationToken, ValueTask<AuthenticationHeaderValue?>>? AuthorizationProvider { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(ObjectLimits);
        ObjectLimits.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxControlBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxControlBytes, 16 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxReferences, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxReferences, 100000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ObjectLimits.MaxEncodedBytes, 256L * 1024 * 1024);
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }

        XRegistry.Federation.GitBindingSyntax.ValidateRootPath(RootPath);
        if (TrustedRegistryRootSha256 is not null &&
            (TrustedRegistryRootSha256.Length != 64 ||
                !TrustedRegistryRootSha256.All(static value => char.IsAsciiDigit(value) || value is >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A trusted root commitment must be a lowercase SHA-256 digest.", nameof(TrustedRegistryRootSha256));
        }
    }
}
