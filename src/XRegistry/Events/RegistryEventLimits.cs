// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry;

/// <summary>Inclusive interaction-event preparation bounds.</summary>
public sealed record RegistryEventLimits
{
    /// <summary>Maximum final subject/action event entries, including separate deprecation events.</summary>
    public int MaxEvents { get; init; } = 4096;
    /// <summary>Maximum mutation observations, including repeated observations of the same subject.</summary>
    public int MaxObservations { get; init; } = 65536;
    /// <summary>Maximum names supplied in one observation or retained in a merged event.</summary>
    public int MaxChangedAttributes { get; init; } = 4096;
    /// <summary>Maximum UTF-8 bytes in one changed attribute name.</summary>
    public int MaxChangedNameBytes { get; init; } = 256;
    /// <summary>Maximum total retained UTF-8 changed-name bytes across the interaction.</summary>
    public int MaxTotalChangedNameBytes { get; init; } = 4 * 1024 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEvents);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxEvents, 65536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxObservations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxChangedAttributes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxChangedAttributes, 65536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxChangedNameBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxChangedNameBytes, 8192);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTotalChangedNameBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxTotalChangedNameBytes, 8 * 1024 * 1024);
    }
}
