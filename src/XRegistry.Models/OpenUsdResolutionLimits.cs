namespace XRegistry.Models;

/// <summary>Inclusive source, cumulative metadata, and probe limits for forward OpenUSD resolution.</summary>
public sealed class OpenUsdResolutionLimits
{
    /// <summary>Creates finite limits; at most two metadata probes are permitted.</summary>
    public OpenUsdResolutionLimits(int maxSourceUtf8Bytes, int maxMetadataBytes, int maxProbes = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxSourceUtf8Bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxMetadataBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxProbes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxProbes, 2);
        MaxSourceUtf8Bytes = maxSourceUtf8Bytes;
        MaxMetadataBytes = maxMetadataBytes;
        MaxProbes = maxProbes;
    }

    /// <summary>Gets the UTF-8 source byte limit, applied before normalization or percent decoding.</summary>
    public int MaxSourceUtf8Bytes { get; }

    /// <summary>Gets the cumulative UTF-8 response byte limit across all metadata probes.</summary>
    public int MaxMetadataBytes { get; }

    /// <summary>Gets the maximum number of callback invocations, from zero through two.</summary>
    public int MaxProbes { get; }
}
