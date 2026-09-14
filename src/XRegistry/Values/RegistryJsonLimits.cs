namespace XRegistry;

/// <summary>Finite per-value JSON ingestion budgets. Limits are inclusive.</summary>
public sealed record RegistryJsonLimits
{
    /// <summary>Gets the maximum encoded UTF-8 size; defaults to 4 MiB.</summary>
    public int MaxBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Gets the maximum object/array nesting; defaults to 64, with a hard ceiling of 256.</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>Gets the maximum number of JSON values, including containers; defaults to 100,000.</summary>
    public int MaxNodes { get; init; } = 100_000;

    /// <summary>Gets the maximum number token length, including signs and exponent; defaults to 1,024.</summary>
    public int MaxNumberCharacters { get; init; } = 1024;

    /// <summary>Gets the maximum absolute explicit decimal exponent; defaults to 10,000.</summary>
    public int MaxNumberExponent { get; init; } = 10_000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxDepth, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxNodes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxNumberCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxNumberCharacters, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxNumberExponent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxNumberExponent, 1_000_000);
    }
}
