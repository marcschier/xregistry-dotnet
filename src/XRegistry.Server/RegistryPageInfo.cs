// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Server;

/// <summary>An opaque, trusted-root link to a page in one frozen record set.</summary>
public sealed record RegistryPageLink(string Relation, Uri Target);

/// <summary>Collection pagination metadata, independent of entity epochs and storage generations.</summary>
public sealed class RegistryPageInfo
{
    /// <summary>Creates pagination metadata for a frozen collection result.</summary>
    /// <param name="totalCount">The exact filtered, initially authorized record count across the complete set.</param>
    /// <param name="expiresAt">The fixed expiry of retained links, or null when no continuation state is retained.</param>
    /// <param name="links">The opaque next/prev/first/last links.</param>
    public RegistryPageInfo(ulong totalCount, DateTimeOffset? expiresAt, IReadOnlyList<RegistryPageLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        TotalCount = totalCount;
        ExpiresAt = expiresAt;
        Links = Array.AsReadOnly(links.ToArray());
    }

    /// <summary>Gets the exact filtered, initially authorized record count across the complete set.</summary>
    public ulong TotalCount { get; }
    /// <summary>Gets the fixed expiry of retained links, or null when no continuation state is retained.</summary>
    public DateTimeOffset? ExpiresAt { get; }
    /// <summary>Gets immutable opaque next/prev/first/last links. Clients must use each URL unchanged.</summary>
    public IReadOnlyList<RegistryPageLink> Links { get; }
}
