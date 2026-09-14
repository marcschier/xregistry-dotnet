namespace XRegistry.Storage.File;

/// <summary>
/// An immutable, store-owned candidate. Dispose abandoned candidates. A commit attempt that
/// starts publication consumes it; candidates cannot be transferred between store instances.
/// </summary>
public sealed class StorageCandidate : IDisposable
{
    private int _disposed;

    internal StorageCandidate(LocalFileStore owner, long expectedGeneration, PreparedMutation[] mutations, long metadataBytes)
    {
        Owner = owner;
        ExpectedGeneration = expectedGeneration;
        Mutations = mutations;
        MetadataBytes = metadataBytes;
    }

    /// <summary>Gets the required storage generation, not an entity Epoch.</summary>
    public long ExpectedGeneration { get; }
    /// <summary>Gets the number of changes published together.</summary>
    public int ChangeCount => Mutations.Length;
    internal LocalFileStore Owner { get; }
    internal PreparedMutation[] Mutations { get; }
    internal long MetadataBytes { get; }
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    internal bool IsConsumed { get; set; }

    /// <summary>Releases staged files and copied metadata accounting. Caller-supplied streams remain caller-owned.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Owner.ReleaseCandidate(this);
        }
    }
}

internal sealed record PreparedMutation(string Key, byte[]? Metadata)
{
    internal bool IsDelete => Metadata is null;
    internal DocumentReference? Document { get; init; }
    internal string? StagingPath { get; init; }
}
