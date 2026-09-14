namespace XRegistry.Federation;

/// <summary>
/// Reopenable virtual-tree reads under one explicitly selected root. No native path or network access is implied.
/// The caller owns this reader; each successful read transfers ownership of a fresh readable stream.
/// </summary>
public interface IDocumentTreeReader
{
    /// <summary>
    /// The selected source and resolved revision, fixed for this operation. Credentials are configured separately.
    /// Mutable readers must not claim immutability from a directory name or a root hash.
    /// </summary>
    NativeRegistryContext Context { get; }

    /// <summary>
    /// Opens exact bytes at the supplied portable root-relative path, without URI decoding.
    /// Null means the object is absent. Readers enforce containment, access, stable handle reads and
    /// observed-mutation detection, reporting other failures explicitly (including during stream disposal).
    /// </summary>
    ValueTask<Stream?> OpenReadAsync(string rootRelativePath, CancellationToken cancellationToken = default);
}
