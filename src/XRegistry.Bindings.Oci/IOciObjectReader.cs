// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

/// <summary>Caller-authorized OCI acquisition with distinct Distribution manifest and blob routes.</summary>
/// <remarks>
/// The caller owns the reader. Each successful call transfers an exact, untransformed response stream.
/// Null means absent; authentication, policy, transport and observed-mutation failures must be explicit.
/// OciSnapshot accounts for requests, bytes, objects and traversal under a cumulative finite budget.
/// Implementations must additionally bound transport deadlines and response headers.
/// </remarks>
public interface IOciObjectReader
{
    /// <summary>The fixed, credential-free repository or layout context.</summary>
    NativeRegistryContext Context { get; }

    /// <summary>Opens an index or manifest by reference. Only the initial root request may use a tag.</summary>
    ValueTask<OciObjectResponse?> OpenManifestAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>Opens config, document or placeholder bytes by validated SHA-256 digest, never by an XID.</summary>
    ValueTask<OciObjectResponse?> OpenBlobAsync(string digest, CancellationToken cancellationToken = default);
}

/// <summary>An owned exact-byte response and optional transport integrity evidence.</summary>
public sealed class OciObjectResponse : IAsyncDisposable
{
    private readonly IDisposable? owner;
    private bool disposed;

    /// <summary>Transfers ownership of a readable stream and optional response owner.</summary>
    /// <param name="content">Exact bytes, without decompression, transcoding or newline changes.</param>
    /// <param name="mediaType">HTTP Content-Type, including optional parameters; absent for a local layout.</param>
    /// <param name="contentDigest">Optional Docker-Content-Digest evidence; not a replacement for hashing.</param>
    /// <param name="contentLength">Optional transport length, verified against received bytes.</param>
    /// <param name="owner">An optional containing response disposed after the stream.</param>
    public OciObjectResponse(Stream content, string? mediaType = null, string? contentDigest = null,
        long? contentLength = null, IDisposable? owner = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead) { throw new ArgumentException("A readable stream is required.", nameof(content)); }
        if (contentLength < 0) { throw new ArgumentOutOfRangeException(nameof(contentLength)); }
        Content = content;
        MediaType = mediaType;
        ContentDigest = contentDigest;
        ContentLength = contentLength;
        this.owner = owner;
    }

    /// <summary>The owned stream; consumers must dispose this response, not retain the stream.</summary>
    public Stream Content { get; }
    /// <summary>Optional transport Content-Type.</summary>
    public string? MediaType { get; }
    /// <summary>Optional transport content digest.</summary>
    public string? ContentDigest { get; }
    /// <summary>Optional declared transport length.</summary>
    public long? ContentLength { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed) { return; }
        disposed = true;
        try { await Content.DisposeAsync().ConfigureAwait(false); }
        finally { owner?.Dispose(); }
    }
}
