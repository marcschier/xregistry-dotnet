// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Server;

/// <summary>An opaque, trusted-root link to a page in one frozen record set.</summary>
public sealed record RegistryPageLink(string Relation, Uri Target);

/// <summary>Collection pagination metadata, independent of entity epochs and storage generations.</summary>
public sealed class RegistryPageInfo
{
    internal RegistryPageInfo(ulong totalCount, DateTimeOffset? expiresAt, IReadOnlyList<RegistryPageLink> links)
    {
        TotalCount = totalCount;
        ExpiresAt = expiresAt;
        Links = links;
    }

    /// <summary>Gets the exact filtered, initially authorized record count across the complete set.</summary>
    public ulong TotalCount { get; }
    /// <summary>Gets the fixed expiry of retained links, or null when no continuation state is retained.</summary>
    public DateTimeOffset? ExpiresAt { get; }
    /// <summary>Gets immutable opaque next/prev/first/last links. Clients must use each URL unchanged.</summary>
    public IReadOnlyList<RegistryPageLink> Links { get; }
}
