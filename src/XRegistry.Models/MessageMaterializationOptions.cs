// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Models;

/// <summary>Finite, inclusive budgets for one independent Message definition materialization.</summary>
public sealed record MessageMaterializationOptions
{
    /// <summary>The maximum base-reference depth; the authored root is depth zero.</summary>
    public int MaxDepth { get; init; } = 16;
    /// <summary>The maximum aggregate UTF-8 bytes of root and acquired definition metadata.</summary>
    public long MaxTotalBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>The maximum explicitly charged traversal and composition work.</summary>
    public int MaxWork { get; init; } = 1_000_000;
    /// <summary>The per-definition and output JSON budgets, including exact-number limits.</summary>
    public RegistryJsonLimits JsonLimits { get; init; } = new();
    /// <summary>Optional pure schema-format to payload media-type mapping for formats without a defined built-in inference.</summary>
    /// <remarks>This is not a schema acquisition callback. Null means inference remains an explicit obligation.</remarks>
    public Func<string, string?>? PayloadContentTypeResolver { get; init; }
}
