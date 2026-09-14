namespace XRegistry.Bindings.Git.Objects;

/// <summary>An immutable, owned canonical Git object with a recomputed identity.</summary>
public sealed class GitObject
{
    private readonly byte[] content;

    internal GitObject(GitObjectId id, GitObjectType type, byte[] content)
    {
        Id = id;
        Type = type;
        this.content = content;
    }

    /// <summary>Gets the verified, algorithm-tagged canonical object ID.</summary>
    public GitObjectId Id { get; }

    /// <summary>Gets the canonical object type, never a delta storage type.</summary>
    public GitObjectType Type { get; }

    /// <summary>Gets the exact payload byte length, excluding the canonical hash header.</summary>
    public long Length => content.LongLength;

    /// <summary>Gets the owned exact payload; no newline, filter, or text conversion is applied.</summary>
    public ReadOnlySpan<byte> Content => content;

    /// <summary>Opens a caller-owned read-only stream without exposing or copying the owned content array.</summary>
    public Stream OpenRead() => new MemoryStream(content, 0, content.Length, writable: false, publiclyVisible: false);

    /// <summary>Copies and verifies a bounded payload against a caller-supplied expected identity.</summary>
    public static GitObject Verify(
        GitObjectId expectedId,
        GitObjectType type,
        ReadOnlySpan<byte> content,
        GitReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(expectedId);
        limits ??= GitReadLimits.Default;
        limits.Validate();
        if (content.Length > limits.MaxObjectBytes || content.Length > limits.MaxTotalDecompressedBytes)
        {
            throw new GitDataException(GitFailure.LimitExceeded, "The Git object byte budget was exceeded.");
        }

        var owned = content.ToArray();
        var actual = GitObjectId.Compute(expectedId.Algorithm, type, owned);
        if (actual != expectedId)
        {
            throw new GitDataException(GitFailure.IntegrityMismatch, "The canonical Git object ID does not match.");
        }

        return new GitObject(actual, type, owned);
    }
}
