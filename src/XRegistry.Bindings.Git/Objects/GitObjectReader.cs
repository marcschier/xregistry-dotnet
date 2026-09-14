namespace XRegistry.Bindings.Git.Objects;

/// <summary>Bounded, synchronous binary ingestion without repository configuration, paths, or network acquisition.</summary>
public static class GitObjectReader
{
    /// <summary>
    /// Reads one isolated self-contained pack v2/v3 stream, verifies its checksum and canonical objects,
    /// and publishes an immutable set only after complete validation. Leaves the source open.
    /// Caller-selected expected IDs must subsequently be required from the returned set.
    /// </summary>
    public static GitObjectSet ReadPack(
        Stream source,
        GitHashAlgorithm algorithm,
        GitReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ = GitObjectId.GetByteLength(algorithm);
        limits ??= GitReadLimits.Default;
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        return GitPack.Read(source, algorithm, limits, cancellationToken);
    }

    /// <summary>
    /// Verifies one complete loose zlib representation against an expected object ID.
    /// Rejects noncanonical headers, trailing members/data and truncation. Leaves the source open.
    /// Cancellation is checked between bounded reads and decoding work.
    /// </summary>
    public static GitObject ReadLoose(
        Stream source,
        GitObjectId expectedId,
        GitReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(expectedId);
        limits ??= GitReadLimits.Default;
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var budget = new GitReadBudget(limits, cancellationToken);
        var input = new GitInput(source, budget);
        var decoded = GitZlib.Read(input, budget, (long)limits.MaxObjectBytes + 64);
        input.RequireEnd();
        var separator = decoded.AsSpan().IndexOf((byte)0);
        if (separator < 0 || separator > 63)
        {
            throw new GitDataException(GitFailure.MalformedData, "The loose Git object header is missing or oversized.");
        }

        var header = decoded.AsSpan(0, separator);
        var space = header.IndexOf((byte)' ');
        if (space < 0)
        {
            throw new GitDataException(GitFailure.MalformedData, "The loose Git object header has no length separator.");
        }

        var type = GitHash.ParseType(header[..space]);
        var length = ParseDecimal(header[(space + 1)..]);
        budget.CheckObjectSize(length);
        var content = decoded.AsSpan(separator + 1);
        if (length != (ulong)content.Length)
        {
            throw new GitDataException(GitFailure.MalformedData, "The loose Git object length does not match its payload.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var value = GitObject.Verify(expectedId, type, content, limits);
        cancellationToken.ThrowIfCancellationRequested();
        return value;
    }

    private static ulong ParseDecimal(ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty || (text.Length > 1 && text[0] == '0'))
        {
            throw new GitDataException(GitFailure.MalformedData, "The loose Git object length is not canonical decimal.");
        }

        ulong value = 0;
        foreach (var digit in text)
        {
            if (digit is < (byte)'0' or > (byte)'9' || value > (ulong.MaxValue - (uint)(digit - '0')) / 10)
            {
                throw new GitDataException(GitFailure.MalformedData, "The loose Git object length is invalid or overflows.");
            }

            value = (value * 10) + (uint)(digit - '0');
        }

        return value;
    }
}
