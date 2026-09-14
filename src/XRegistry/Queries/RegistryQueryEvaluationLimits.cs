namespace XRegistry.Queries;

/// <summary>Inclusive, cumulative query limits, independent of transport and retained-page storage.</summary>
public sealed record RegistryQueryEvaluationLimits
{
    /// <summary>Gets the maximum expressions across all AND/OR branches.</summary>
    public int MaxFilterExpressions { get; init; } = 128;
    /// <summary>Gets the maximum dot-path segments, with a hard ceiling of 128.</summary>
    public int MaxPathSegments { get; init; } = 32;
    /// <summary>Gets the maximum input or generated collection-link query characters.</summary>
    public int MaxQueryCharacters { get; init; } = 8192;
    /// <summary>Gets cumulative traversal, parsing and comparison work.</summary>
    public int MaxWork { get; init; } = 250_000;
    /// <summary>Gets cumulative entity/first-attribute fact projections.</summary>
    public int MaxEntities { get; init; } = 4096;
    /// <summary>Gets cumulative encoded fact bytes.</summary>
    public int MaxFactBytes { get; init; } = 8 * 1024 * 1024;
    /// <summary>Gets cumulative source entity reads, including hidden results.</summary>
    public int MaxSourceReads { get; init; } = 8192;
    /// <summary>Gets cumulative collection members, including cached membership paths.</summary>
    public int MaxCollectionMembers { get; init; } = 16_384;
    /// <summary>Gets cumulative source metadata, Document, navigation/default context and membership-path bytes.</summary>
    public long MaxSourceBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Gets the maximum exact Document bytes in one source result.</summary>
    public int MaxDocumentBytes { get; init; } = 8 * 1024 * 1024;
    /// <summary>Gets the shared duration, including source, authorization and response-preparation callbacks.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Gets strict JSON, depth, node and exact-number limits.</summary>
    public RegistryJsonLimits Json { get; init; } = new();

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFilterExpressions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPathSegments, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxPathSegments, 128);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxQueryCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxWork, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEntities, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFactBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSourceReads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCollectionMembers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSourceBytes, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDocumentBytes);
        if (MaxDuration <= TimeSpan.Zero || MaxDuration > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDuration));
        }

        ArgumentNullException.ThrowIfNull(Json);
        Json.Validate();
    }
}
