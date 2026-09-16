// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Http;

/// <summary>The direction of a Document's model-dependent HTTP metadata representation.</summary>
public enum RegistryHeaderMetadataDirection
{
    /// <summary>Ignore ordinary read-only attributes and interpret decoded null as a deletion.</summary>
    ClientInput,
    /// <summary>Retain read-only attributes; a string-valued null header is literal string data.</summary>
    Response
}

/// <summary>Inclusive budgets for model-dependent HTTP metadata conversion.</summary>
public sealed record RegistryHeaderMetadataOptions
{
    /// <summary>Gets the aggregate header byte budget, including names and four framing bytes per field value.</summary>
    /// <remarks>Encoding counts the emitted metadata fields. Decoding counts all supplied fields, even unrelated ones.</remarks>
    public int MaxHeaderBytes { get; init; } = 32 * 1024;

    /// <summary>Gets the maximum field-value count, including repetitions and fields without values.</summary>
    public int MaxHeaderCount { get; init; } = 128;

    /// <summary>Gets limits for metadata JSON and conditional-definition lookup work and depth.</summary>
    /// <remarks>
    /// Response encoding projects already validated metadata; omitted complex values are not reparsed.
    /// Conditional lookup uses MaxNodes for definition visits/additions and MaxDepth for conditional nesting.
    /// </remarks>
    public RegistryJsonLimits Json { get; init; } = new();

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxHeaderBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxHeaderCount);
        ArgumentNullException.ThrowIfNull(Json);
        Json.Validate();
    }
}
