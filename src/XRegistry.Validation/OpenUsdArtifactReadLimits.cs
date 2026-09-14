namespace XRegistry.Validation;

/// <summary>Explicit artifact-byte and source-read budgets for one OpenUSD integrity operation.</summary>
public sealed class OpenUsdArtifactReadLimits
{
    /// <summary>Creates immutable limits, allowing empty artifacts and counting the EOF read.</summary>
    public OpenUsdArtifactReadLimits(int maxArtifactBytes, int maxReadOperations)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxArtifactBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxReadOperations, 1);
        MaxArtifactBytes = maxArtifactBytes;
        MaxReadOperations = maxReadOperations;
    }

    /// <summary>Gets the maximum number of artifact bytes retained, including a possible zero-byte budget.</summary>
    public int MaxArtifactBytes { get; }

    /// <summary>Gets the maximum number of reads, including the read that establishes EOF.</summary>
    public int MaxReadOperations { get; }
}
