// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Client;

/// <summary>Cumulative budgets for one complete collection traversal, in addition to per-response limits.</summary>
public sealed record RegistryPaginationOptions
{
    /// <summary>Gets the maximum number of pages, including empty pages.</summary>
    public int MaxPages { get; init; } = 1024;

    /// <summary>Gets the maximum number of records across all pages.</summary>
    public ulong MaxRecords { get; init; } = 1_000_000;

    /// <summary>Gets the maximum cumulative decoded response bytes.</summary>
    public long MaxTotalBytes { get; init; } = 128 * 1024 * 1024;

    /// <summary>Gets the deadline for the entire traversal, including time between requests.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPages);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxPages, 65536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTotalBytes);
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout), "A finite traversal deadline up to ten minutes is required.");
        }
    }
}
