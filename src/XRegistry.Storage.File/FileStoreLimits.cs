namespace XRegistry.Storage.File;

/// <summary>Finite per-store budgets. Limits are fixed for an open store instance.</summary>
public sealed record FileStoreLimits
{
    /// <summary>Maximum UTF-8 bytes in an opaque record key.</summary>
    public int MaxKeyBytes { get; init; } = 1024;
    /// <summary>Maximum UTF-8 JSON bytes in one metadata record.</summary>
    public int MaxMetadataBytesPerRecord { get; init; } = 256 * 1024;
    /// <summary>Maximum committed metadata bytes.</summary>
    public long MaxMetadataBytes { get; init; } = 8 * 1024 * 1024;
    /// <summary>Maximum committed metadata records.</summary>
    public int MaxRecords { get; init; } = 4096;
    /// <summary>Maximum changes in one candidate.</summary>
    public int MaxMutations { get; init; } = 1024;
    /// <summary>Maximum JSON nesting depth.</summary>
    public int MaxJsonDepth { get; init; } = 64;
    /// <summary>Maximum JSON tokens in one candidate or stored snapshot.</summary>
    public int MaxJsonTokens { get; init; } = 1_000_000;
    /// <summary>Maximum bytes in one Document, including a present zero-byte Document.</summary>
    public long MaxDocumentBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>Maximum committed document references, including references sharing a digest.</summary>
    public int MaxDocumentReferences { get; init; } = 1024;
    /// <summary>Maximum distinct committed document bytes.</summary>
    public long MaxReferencedDocumentBytes { get; init; } = 256 * 1024 * 1024;
    /// <summary>Maximum physical blob files, including unreferenced files.</summary>
    public int MaxBlobFiles { get; init; } = 4096;
    /// <summary>Maximum physical blob bytes, including unreferenced files.</summary>
    public long MaxBlobBytes { get; init; } = 512 * 1024 * 1024;
    /// <summary>Maximum staging files, including abandoned files.</summary>
    public int MaxTemporaryFiles { get; init; } = 64;
    /// <summary>Maximum staging bytes, including abandoned files.</summary>
    public long MaxTemporaryBytes { get; init; } = 128 * 1024 * 1024;
    /// <summary>Maximum simultaneously prepared candidates.</summary>
    public int MaxPreparedCandidates { get; init; } = 4;
    /// <summary>Maximum copied metadata bytes held by prepared candidates.</summary>
    public long MaxPreparedMetadataBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Maximum live snapshot leases.</summary>
    public int MaxReadSnapshots { get; init; } = 32;
    /// <summary>Maximum metadata and key bytes held by live snapshot leases.</summary>
    public long MaxReadSnapshotBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>Maximum open document stream leases.</summary>
    public int MaxOpenDocumentStreams { get; init; } = 32;
    /// <summary>Maximum SQLite main database bytes, enforced through its page ceiling.</summary>
    public long MaxDatabaseBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>SQLite's suggested page-cache size; large transactions also remain database-bounded.</summary>
    public int SqliteCacheBytes { get; init; } = 2 * 1024 * 1024;
    /// <summary>Maximum files removed by one explicit orphan collection.</summary>
    public int MaxOrphansPerCollection { get; init; } = 128;

    internal void Validate()
    {
        Positive(MaxKeyBytes, nameof(MaxKeyBytes));
        Positive(MaxMetadataBytesPerRecord, nameof(MaxMetadataBytesPerRecord));
        Positive(MaxMetadataBytes, nameof(MaxMetadataBytes));
        Positive(MaxRecords, nameof(MaxRecords));
        Positive(MaxMutations, nameof(MaxMutations));
        Positive(MaxJsonDepth, nameof(MaxJsonDepth));
        Positive(MaxJsonTokens, nameof(MaxJsonTokens));
        Nonnegative(MaxDocumentBytes, nameof(MaxDocumentBytes));
        Nonnegative(MaxDocumentReferences, nameof(MaxDocumentReferences));
        Nonnegative(MaxReferencedDocumentBytes, nameof(MaxReferencedDocumentBytes));
        Positive(MaxBlobFiles, nameof(MaxBlobFiles));
        Nonnegative(MaxBlobBytes, nameof(MaxBlobBytes));
        Positive(MaxTemporaryFiles, nameof(MaxTemporaryFiles));
        Nonnegative(MaxTemporaryBytes, nameof(MaxTemporaryBytes));
        Positive(MaxPreparedCandidates, nameof(MaxPreparedCandidates));
        Positive(MaxPreparedMetadataBytes, nameof(MaxPreparedMetadataBytes));
        Positive(MaxReadSnapshots, nameof(MaxReadSnapshots));
        Positive(MaxReadSnapshotBytes, nameof(MaxReadSnapshotBytes));
        Positive(MaxOpenDocumentStreams, nameof(MaxOpenDocumentStreams));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDatabaseBytes, 65536);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxDatabaseBytes, 1L << 40);
        ArgumentOutOfRangeException.ThrowIfLessThan(SqliteCacheBytes, 4096);
        Positive(MaxOrphansPerCollection, nameof(MaxOrphansPerCollection));
    }

    private static void Positive(long value, string name) => ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, name);
    private static void Nonnegative(long value, string name) => ArgumentOutOfRangeException.ThrowIfNegative(value, name);
}
