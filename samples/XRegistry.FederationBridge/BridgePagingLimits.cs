// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Samples.Bridge;

public sealed record BridgePagingLimits
{
    public int MaxPageRecords { get; init; } = 256;
    public int MaxCaptureRecords { get; init; } = 1024;
    public int MaxCaptureBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxCaptures { get; init; } = 16;
    public int MaxTotalBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxTokens { get; init; } = 4096;
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(2);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPageRecords, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxPageRecords, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCaptureRecords, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxCaptureRecords, 16_384);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCaptureBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxCaptureBytes, 32 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCaptures, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxCaptures, 128);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTotalBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxTotalBytes, 128 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTokens, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxTokens, 16_384);
        if (Lifetime <= TimeSpan.Zero || Lifetime > TimeSpan.FromMinutes(5)) { throw new ArgumentOutOfRangeException(nameof(Lifetime)); }
    }
}
