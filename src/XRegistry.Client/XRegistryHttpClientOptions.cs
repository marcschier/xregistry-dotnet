using System.Net.Http.Headers;

namespace XRegistry.Client;

/// <summary>Finite transport budgets and explicitly scoped network permissions for one HTTP Registry.</summary>
public sealed record XRegistryHttpClientOptions
{
    /// <summary>Gets whether an explicit loopback HTTP Registry is allowed.</summary>
    public bool AllowLoopbackHttp { get; init; }

    /// <summary>Gets whether this HTTPS origin may resolve to private RFC1918/ULA addresses.</summary>
    public bool AllowPrivateOrigin { get; init; }

    /// <summary>Gets the deadline covering response headers and subsequent body consumption.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the maximum metadata request/response bytes.</summary>
    public int MaxMetadataBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Gets the aggregate model-aware header byte limit, including field names and framing.</summary>
    /// <remarks>Applies to emitted metadata fields and to all fields supplied to response-header decoding.</remarks>
    public int MaxHeaderBytes { get; init; } = 32 * 1024;

    /// <summary>Gets the model-aware header field-value limit, including repetitions.</summary>
    public int MaxHeaderCount { get; init; } = 128;

    /// <summary>Gets the maximum Document response bytes.</summary>
    public long MaxDocumentBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>Gets whether gzip, zlib-wrapped deflate and Brotli response decoding is requested and allowed.</summary>
    /// <remarks>The default requests identity only. Content headers always describe the original encoded response.</remarks>
    public bool EnableContentDecoding { get; init; }

    /// <summary>Gets the maximum encoded response bytes, also applied separately to each intermediate coding layer.</summary>
    /// <remarks>Decoded metadata and Documents additionally obey their own limits, including the remaining paging budget.</remarks>
    public long MaxEncodedResponseBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>Gets the maximum Content-Encoding token count, including identity tokens; at most sixteen.</summary>
    public int MaxContentCodingDepth { get; init; } = 4;

    /// <summary>Gets the maximum cumulative gzip member count across all coding layers; at most 4096.</summary>
    public int MaxGzipMembers { get; init; } = 128;

    /// <summary>Gets the maximum cumulative gzip header bytes across all members and layers; at most one MiB.</summary>
    public int MaxGzipHeaderBytes { get; init; } = 16 * 1024;

    /// <summary>Gets the maximum JSON metadata nesting depth.</summary>
    public int MaxJsonDepth { get; init; } = 64;

    /// <summary>Gets the maximum encoded request URI characters.</summary>
    public int MaxUriLength { get; init; } = 16384;

    /// <summary>Gets the maximum number of query parameters, including repetitions.</summary>
    public int MaxQueryParameters { get; init; } = 128;

    /// <summary>Gets an optional credential provider called only for the validated configured origin.</summary>
    /// <remarks>The host owns credentials. No authentication-challenge replay or forwarding to redirects occurs.</remarks>
    public Func<Uri, CancellationToken, ValueTask<AuthenticationHeaderValue?>>? AuthorizationProvider { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxMetadataBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxMetadataBytes, 256 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxHeaderBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxHeaderBytes, 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxHeaderCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxHeaderCount, 65536);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDocumentBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxEncodedResponseBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxContentCodingDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxContentCodingDepth, 16);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxGzipMembers);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxGzipMembers, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxGzipHeaderBytes, 10);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxGzipHeaderBytes, 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxJsonDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxJsonDepth, 256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxUriLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxUriLength, 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxQueryParameters);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxQueryParameters, 4096);
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "A finite deadline up to ten minutes is required.");
        }
    }

    internal Http.RegistryHeaderMetadataOptions HeaderMetadataOptions() => new()
    {
        MaxHeaderBytes = MaxHeaderBytes,
        MaxHeaderCount = MaxHeaderCount,
        Json = new() { MaxBytes = MaxMetadataBytes, MaxDepth = MaxJsonDepth }
    };
}
