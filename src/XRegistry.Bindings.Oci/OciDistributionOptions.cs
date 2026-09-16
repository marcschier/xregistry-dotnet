// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net.Http.Headers;

namespace XRegistry.Bindings.Oci;

/// <summary>Finite transport limits and explicit repository-scoped credentials, independent of graph budgets.</summary>
public sealed record OciDistributionOptions
{
    /// <summary>A deadline covering credentials, headers and exact response-body consumption.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Maximum exact response bytes, before any graph interpretation; inclusive.</summary>
    public long MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Maximum exact producer request bytes, including document uploads; inclusive.</summary>
    public long MaxUploadBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Maximum encoded repository, reference or upload-session URI length.</summary>
    public int MaxUriLength { get; init; } = 16_384;
    /// <summary>Credentials for an already policy-authorized repository request. No challenge or redirect replay occurs.</summary>
    public Func<Uri, CancellationToken, ValueTask<AuthenticationHeaderValue?>>? AuthorizationProvider { get; init; }

    internal void Validate()
    {
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "A finite deadline up to ten minutes is required.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxResponseBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxUploadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxUriLength, 1);
    }
}
