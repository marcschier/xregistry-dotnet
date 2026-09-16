// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Models;

/// <summary>Inclusive, finite budgets for pure Endpoint authoring validation and materialization.</summary>
public sealed record EndpointTemplateOptions
{
    /// <summary>Gets limits applied separately to authored metadata, arguments, and resolved JSON.</summary>
    public RegistryJsonLimits JsonLimits { get; init; } = new();

    /// <summary>Gets the maximum supplied bindings or distinct authored placeholder names; defaults to 256.</summary>
    public int MaxVariables { get; init; } = 256;

    /// <summary>Gets the maximum placeholder occurrences across keys and values; defaults to 4,096.</summary>
    public int MaxExpansions { get; init; } = 4096;

    /// <summary>Gets the maximum UTF-8 bytes in one authored/resolved protocol-option string or key; defaults to 64 KiB.</summary>
    public int MaxExpandedStringBytes { get; init; } = 64 * 1024;

    /// <summary>Gets the total encoded substitution bytes, counting every occurrence; defaults to 1 MiB.</summary>
    public int MaxExpansionBytes { get; init; } = 1024 * 1024;

    /// <summary>Gets the maximum explicitly deferred obligations; defaults to 1,024.</summary>
    public int MaxDeferredChecks { get; init; } = 1024;

    /// <summary>Gets the aggregate UTF-8 bytes in deferred codes, paths, and messages; defaults to 64 KiB.</summary>
    public int MaxDeferredBytes { get; init; } = 64 * 1024;

    /// <summary>Gets explicitly supplied Groups, keyed by the exact messagegroups reference; nothing is fetched.</summary>
    public RegistryJson? MessageGroups { get; init; }

    /// <summary>Gets the maximum supplied Groups and declared Group references, each counted separately; defaults to 256.</summary>
    public int MaxMessageGroups { get; init; } = 256;

    /// <summary>Gets the maximum combined inline and referenced Message candidates; defaults to 4,096.</summary>
    public int MaxMessages { get; init; } = 4096;

    /// <summary>Gets the aggregate UTF-8 bytes of candidate JSON, IDs, and Group references; defaults to 4 MiB.</summary>
    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Gets whether duplicate IDs across distinct collections fail materialization; defaults to false.</summary>
    public bool RejectDuplicateMessageIds { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(JsonLimits);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxVariables);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxExpansions);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxExpandedStringBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxExpansionBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDeferredChecks);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDeferredBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxMessageGroups);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxMessages);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxMessageBytes);
    }
}
