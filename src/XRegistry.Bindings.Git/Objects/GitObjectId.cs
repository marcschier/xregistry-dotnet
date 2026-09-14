namespace XRegistry.Bindings.Git.Objects;

/// <summary>
/// An immutable, algorithm-tagged Git object ID. An ID establishes content identity, not authorship.
/// SHA-1 operations here are ordinary SHA-1, not collision-detecting SHA1DC.
/// </summary>
public sealed class GitObjectId : IEquatable<GitObjectId>
{
    private readonly byte[] bytes;

    private GitObjectId(GitHashAlgorithm algorithm, byte[] bytes)
    {
        Algorithm = algorithm;
        this.bytes = bytes;
    }

    /// <summary>Gets the object format that determines the digest and reference widths.</summary>
    public GitHashAlgorithm Algorithm { get; }

    /// <summary>Gets the owned, exact digest bytes.</summary>
    public ReadOnlySpan<byte> Bytes => bytes;

    /// <summary>Gets whether all digest bytes are zero (a protocol sentinel, not a valid graph reference).</summary>
    public bool IsZero => bytes.AsSpan().IndexOfAnyExcept((byte)0) < 0;

    /// <summary>Gets the exact binary width of a supported object format.</summary>
    public static int GetByteLength(GitHashAlgorithm algorithm) => algorithm switch
    {
        GitHashAlgorithm.Sha1 => 20,
        GitHashAlgorithm.Sha256 => 32,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    /// <summary>Parses exactly 40 or 64 ASCII hexadecimal characters for the specified format.</summary>
    public static GitObjectId Parse(GitHashAlgorithm algorithm, ReadOnlySpan<char> text)
    {
        if (text.Length != GetByteLength(algorithm) * 2)
        {
            throw new GitDataException(GitFailure.MalformedData, "The Git object ID has the wrong width.");
        }

        var bytes = new byte[text.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            var high = Hex(text[index * 2]);
            var low = Hex(text[(index * 2) + 1]);
            if (high < 0 || low < 0)
            {
                throw new GitDataException(GitFailure.MalformedData, "The Git object ID is not ASCII hexadecimal.");
            }

            bytes[index] = (byte)((high << 4) | low);
        }

        return new GitObjectId(algorithm, bytes);
    }

    /// <summary>Copies an exact-width binary digest into a new immutable ID.</summary>
    public static GitObjectId FromBytes(GitHashAlgorithm algorithm, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != GetByteLength(algorithm))
        {
            throw new GitDataException(GitFailure.MalformedData, "The Git object ID has the wrong binary width.");
        }

        return new GitObjectId(algorithm, bytes.ToArray());
    }

    /// <summary>Hashes the canonical type, decimal byte length, NUL separator, and exact content.</summary>
    public static GitObjectId Compute(GitHashAlgorithm algorithm, GitObjectType type, ReadOnlySpan<byte> content)
    {
        using var hash = GitHash.BeginObject(algorithm, type, content.Length);
        hash.AppendData(content);
        return new GitObjectId(algorithm, hash.GetHashAndReset());
    }

    /// <summary>
    /// Hashes an isolated content stream with an exact nonnegative Int64 length and a finite quota.
    /// Rejects truncation and trailing bytes, leaves the stream open, and checks cancellation between reads.
    /// No length is narrowed to Int32.
    /// </summary>
    public static GitObjectId Compute(
        GitHashAlgorithm algorithm,
        GitObjectType type,
        Stream content,
        long contentLength,
        long maxBytes = 16_777_216,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (contentLength > maxBytes)
        {
            throw new GitDataException(GitFailure.LimitExceeded, "The Git object hash byte budget was exceeded.");
        }

        using var hash = GitHash.BeginObject(algorithm, type, contentLength);
        var buffer = new byte[8192];
        var remaining = contentLength;
        while (remaining != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = content.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new GitDataException(GitFailure.TruncatedInput, "The Git object content is truncated.");
            }

            hash.AppendData(buffer.AsSpan(0, read));
            remaining -= read;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (content.ReadByte() != -1)
        {
            throw new GitDataException(GitFailure.MalformedData, "The Git object content has trailing bytes.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new GitObjectId(algorithm, hash.GetHashAndReset());
    }

    /// <summary>Copies the digest into a destination of at least the algorithm's binary width.</summary>
    public void CopyTo(Span<byte> destination) => bytes.CopyTo(destination);

    /// <summary>Returns exactly 40 or 64 lowercase hexadecimal characters.</summary>
    public override string ToString() => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>Compares both algorithm and digest bytes.</summary>
    public bool Equals(GitObjectId? other)
        => other is not null && Algorithm == other.Algorithm && bytes.AsSpan().SequenceEqual(other.bytes);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is GitObjectId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Algorithm);
        hash.AddBytes(bytes);
        return hash.ToHashCode();
    }

    /// <summary>Compares two nullable IDs by value.</summary>
    public static bool operator ==(GitObjectId? left, GitObjectId? right) => Equals(left, right);

    /// <summary>Compares two nullable IDs for inequality.</summary>
    public static bool operator !=(GitObjectId? left, GitObjectId? right) => !Equals(left, right);

    private static int Hex(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1,
    };
}
