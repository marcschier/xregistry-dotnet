namespace XRegistry.Bindings.Git;

/// <summary>Distinguishes failures of the managed Git binary readers.</summary>
public enum GitFailure
{
    /// <summary>The representation violates its binary format.</summary>
    MalformedData,
    /// <summary>The input ends before the representation is complete.</summary>
    TruncatedInput,
    /// <summary>A checksum or expected object identity does not match.</summary>
    IntegrityMismatch,
    /// <summary>A configured resource budget was exceeded.</summary>
    LimitExceeded,
    /// <summary>A packet or operation is not valid in the current state.</summary>
    UnexpectedState,
    /// <summary>A format, version, or feature is unsupported.</summary>
    UnsupportedFormat,
    /// <summary>A self-contained pack has missing or cyclic delta dependencies.</summary>
    UnresolvedDeltaBase,
    /// <summary>A required pinned object is absent from the supplied object set.</summary>
    MissingObject,
    /// <summary>A requested byte-exact tree path does not exist.</summary>
    PathNotFound,
    /// <summary>A visited symbolic link is prohibited.</summary>
    PolicyDenied,
    /// <summary>A visited gitlink or LFS pointer requires an unsupported indirection.</summary>
    UnsupportedIndirection,
    /// <summary>The remote sideband reported a fatal error.</summary>
    RemoteFatal,
}

/// <summary>A fail-closed Git parsing or object-reading failure; no partial result is returned.</summary>
public sealed class GitDataException : IOException
{
    /// <summary>Creates a malformed-data failure.</summary>
    public GitDataException()
        : this(GitFailure.MalformedData, "Invalid Git data.")
    {
    }

    /// <summary>Creates a malformed-data failure with a diagnostic message.</summary>
    public GitDataException(string? message)
        : base(message)
    {
    }

    /// <summary>Creates a malformed-data failure with an underlying cause.</summary>
    public GitDataException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates a categorized failure without including untrusted content in the message.</summary>
    public GitDataException(GitFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    /// <summary>Gets the machine-readable failure category.</summary>
    public GitFailure Failure { get; }
}
