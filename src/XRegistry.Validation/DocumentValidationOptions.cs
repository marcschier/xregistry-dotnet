namespace XRegistry.Validation;

/// <summary>
/// Resolves only explicitly supplied schema references. A null result means unavailable.
/// Returned memory remains unchanged until the validation operation completes.
/// The callback must honor cancellation and impose its own I/O deadlines.
/// </summary>
public delegate ValueTask<ReadOnlyMemory<byte>?> DocumentReferenceResolver(
    string reference, CancellationToken cancellationToken);

/// <summary>Finite, operation-wide parser budgets and explicit reference access.</summary>
public sealed record DocumentValidationOptions
{
    /// <summary>Gets the inclusive byte limit for each document (default 1 MiB).</summary>
    public int MaxDocumentBytes { get; init; } = 1_048_576;

    /// <summary>Gets the inclusive total byte limit, including history and references (default 4 MiB).</summary>
    public long MaxTotalBytes { get; init; } = 4_194_304;

    /// <summary>Gets the inclusive nesting limit, counting the root as one (1..128; default 64).</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>Gets the inclusive count of parsed values/elements/tokens (default 100,000).</summary>
    public int MaxNodes { get; init; } = 100_000;

    /// <summary>Gets the inclusive budget for input bytes and semantic work (default 1,000,000).</summary>
    public long MaxWork { get; init; } = 1_000_000;

    /// <summary>Gets the inclusive external reference count (default 32; zero forbids resolution).</summary>
    public int MaxReferences { get; init; } = 32;

    /// <summary>Gets the inclusive supplied ancestor count (default 128).</summary>
    public int MaxHistory { get; init; } = 128;

    /// <summary>Gets the optional absolute base URI for explicitly resolved references.</summary>
    public Uri? DocumentUri { get; init; }

    /// <summary>Gets the optional reference callback. There is no default network resolver.</summary>
    public DocumentReferenceResolver? ResolveReference { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDocumentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTotalBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxDepth, 128);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxWork);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxReferences);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxHistory);
        if (DocumentUri is { IsAbsoluteUri: false })
        {
            throw new ArgumentException("The document URI must be absolute.", nameof(DocumentUri));
        }
    }
}
