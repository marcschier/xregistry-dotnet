namespace XRegistry.Bindings.Git.Objects;

/// <summary>
/// Immutable per-operation budgets. Zero permits no work in that dimension.
/// In-memory object/member sizes cannot exceed the CLR array range; larger objects require another reader.
/// </summary>
public sealed record GitReadLimits
{
    /// <summary>Gets the finite default budgets.</summary>
    public static GitReadLimits Default { get; } = new();

    /// <summary>Maximum encoded stream bytes, including framing and checksums (default 256 MiB).</summary>
    public long MaxEncodedBytes { get; init; } = 268_435_456;

    /// <summary>Maximum bytes in one compressed zlib member, including its header/trailer (default 32 MiB).</summary>
    public int MaxCompressedObjectBytes { get; init; } = 33_554_432;

    /// <summary>Maximum pack entries or object-set inputs, including duplicates (default 100,000).</summary>
    public int MaxObjects { get; init; } = 100_000;

    /// <summary>Maximum canonical object payload or expanded delta program (default 16 MiB).</summary>
    public int MaxObjectBytes { get; init; } = 16_777_216;

    /// <summary>Maximum summed inflated bytes plus reconstructed delta results (default 128 MiB).</summary>
    public long MaxTotalDecompressedBytes { get; init; } = 134_217_728;

    /// <summary>Maximum decoded DEFLATE symbols plus stored-block bytes (default 16,777,216).</summary>
    public long MaxInflateSymbols { get; init; } = 16_777_216;

    /// <summary>Maximum DEFLATE blocks across all members (default 65,536).</summary>
    public int MaxDeflateBlocks { get; init; } = 65_536;

    /// <summary>Maximum deferred delta entries (default 100,000); external thin-pack bases are never accepted.</summary>
    public int MaxDeltaObjects { get; init; } = 100_000;

    /// <summary>Maximum number of delta applications along a base chain (default 64).</summary>
    public int MaxDeltaDepth { get; init; } = 64;

    /// <summary>Maximum summed bytes copied or inserted by delta instructions (default 256 MiB).</summary>
    public long MaxDeltaWorkBytes { get; init; } = 268_435_456;

    /// <summary>Maximum delta instructions across the operation (default 1,000,000).</summary>
    public long MaxDeltaInstructions { get; init; } = 1_000_000;

    /// <summary>Maximum commit/tag header bytes through the blank separator (default 1 MiB).</summary>
    public int MaxRecordHeaderBytes { get; init; } = 1_048_576;

    /// <summary>Maximum commit/tag header lines, including continuations (default 16,384).</summary>
    public int MaxRecordHeaders { get; init; } = 16_384;

    /// <summary>Maximum entries in an individual tree (default 100,000).</summary>
    public int MaxTreeEntries { get; init; } = 100_000;

    /// <summary>Maximum raw bytes in one tree name (default 4,096).</summary>
    public int MaxTreeNameBytes { get; init; } = 4_096;

    /// <summary>Maximum slash-separated components in one tree read (default 128).</summary>
    public int MaxTreeDepth { get; init; } = 128;

    /// <summary>Maximum annotated tags peeled before a commit (default 32).</summary>
    public int MaxTagDepth { get; init; } = 32;

    /// <summary>Maximum object visits in one pin/open or path read (default 100,000).</summary>
    public int MaxTraversalObjects { get; init; } = 100_000;

    /// <summary>Maximum summed tree entries inspected in one path read (default 100,000).</summary>
    public long MaxTraversalEntries { get; init; } = 100_000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxEncodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxCompressedObjectBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxCompressedObjectBytes, Array.MaxLength);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxObjects);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxObjectBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxObjectBytes, Array.MaxLength - 64);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxTotalDecompressedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxInflateSymbols);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDeflateBlocks);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDeltaObjects);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDeltaDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDeltaWorkBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDeltaInstructions);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRecordHeaderBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRecordHeaders);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxTreeEntries);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxTreeNameBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxTreeDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxTagDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxTraversalObjects);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxTraversalEntries);
    }
}
