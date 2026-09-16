// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Validation;

/// <summary>Explicit Document identity, parser budgets and bounded selector policy.</summary>
public sealed record SchemaObjectSelectionOptions
{
    /// <summary>
    /// Gets the exact raw reference used to acquire the supplied Document, without
    /// its type selector. Required when the reference itself contains a fragment.
    /// An empty string explicitly identifies an unnamed, in-memory Document.
    /// </summary>
    public string? DocumentReference { get; init; }

    /// <summary>Gets the shared parser budgets and optional, explicit dependency resolver.</summary>
    public DocumentValidationOptions Validation { get; init; } = new();

    /// <summary>Gets the inclusive raw URI/reference character limit (default 16,384; maximum 65,536).</summary>
    public int MaxUriLength { get; init; } = 16_384;

    /// <summary>Gets the inclusive raw and decoded selector character limit (default 4,096; maximum 65,536).</summary>
    public int MaxSelectorLength { get; init; } = 4_096;

    /// <summary>Gets the inclusive JSON Pointer or structural XPath step limit (default 64; maximum 128).</summary>
    public int MaxSelectorSegments { get; init; } = 64;

    /// <summary>
    /// Gets up to 32 explicit XPath prefix bindings. The defaults are xs/xsd for
    /// the XML Schema namespace and xml for the XML namespace. Unprefixed steps
    /// match no namespace; Document prefixes are not guessed.
    /// </summary>
    public IReadOnlyDictionary<string, string>? XmlNamespaces { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Validation);
        Validation.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxUriLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxUriLength, 65_536);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSelectorLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxSelectorLength, 65_536);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSelectorSegments, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxSelectorSegments, 128);
    }
}
