using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace XRegistry.Client;

/// <summary>An owned HTTP result preserving Registry rejections separately from transport failures.</summary>
public sealed partial class XRegistryHttpResponse : IDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly CancellationTokenSource _deadline;
    private readonly XRegistryHttpClientOptions _options;
    private readonly string[] _contentEncodings;
    private int _consumed;
    private int _disposed;
    private long _bodyBytesRead;

    internal XRegistryHttpResponse(
        HttpResponseMessage response, CancellationTokenSource deadline, XRegistryHttpClientOptions options)
    {
        _response = response;
        _deadline = deadline;
        _options = options;
        // Typed header access can normalize away empty entries and excess whitespace.
        _contentEncodings = response.Content.Headers.NonValidated.TryGetValues("Content-Encoding", out var encodings) ?
            encodings.ToArray() : [];
    }

    /// <summary>Gets the unchanged upstream HTTP status, including Registry rejections and redirects.</summary>
    public HttpStatusCode StatusCode => _response.StatusCode;

    /// <summary>Gets the response headers, including links, locations, and correlation identifiers.</summary>
    public HttpResponseHeaders Headers => _response.Headers;

    /// <summary>Gets the unchanged content headers.</summary>
    public HttpContentHeaders ContentHeaders => _response.Content.Headers;

    /// <summary>Gets the number of decoded body bytes successfully copied to the consumer.</summary>
    public long BodyBytesRead => Interlocked.Read(ref _bodyBytesRead);

    /// <summary>Reads metadata or problem details into an independently owned JSON document.</summary>
    /// <param name="cancellationToken">Cancellation in addition to the request's original deadline.</param>
    /// <returns>A document the caller must dispose. Its lifetime is independent of this response.</returns>
    /// <remarks>This consumes the response body once; it does not turn an error status into success.</remarks>
    public ValueTask<JsonDocument> ReadMetadataAsync(CancellationToken cancellationToken = default) =>
        ReadMetadataWithinLimitAsync(_options.MaxMetadataBytes, cancellationToken);

    internal async ValueTask<JsonDocument> ReadMetadataWithinLimitAsync(
        long maximumBytes, CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        await CopyBodyAsync(destination, Math.Min(maximumBytes, _options.MaxMetadataBytes), cancellationToken)
            .ConfigureAwait(false);
        return RegistryJson.ParseDocument(destination.GetBuffer().AsSpan(0, checked((int)destination.Length)),
            new RegistryJsonLimits { MaxBytes = _options.MaxMetadataBytes, MaxDepth = _options.MaxJsonDepth });
    }

    /// <summary>Streams exact decoded Document bytes into a caller-owned destination.</summary>
    /// <param name="destination">A writable stream, which is not disposed by this method.</param>
    /// <param name="cancellationToken">Cancellation in addition to the original request deadline.</param>
    /// <returns>A task completing when the bounded body has been consumed.</returns>
    /// <remarks>A failure can leave partial bytes in the destination; stage file downloads before publication.</remarks>
    public ValueTask CopyDocumentToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("A writable destination is required.", nameof(destination));
        }

        return CopyBodyAsync(destination, _options.MaxDocumentBytes, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _deadline.Cancel();
        }
        finally
        {
            _response.Dispose();
            _deadline.Dispose();
        }
    }

    private async ValueTask CopyBodyAsync(Stream destination, long limit, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
        {
            throw new InvalidOperationException("The response body has already been consumed.");
        }

        var codings = RegistryHttpContentDecoding.GetCodings(_contentEncodings, _options);
        var encodedLimit = codings.Count == 0 ? Math.Min(limit, _options.MaxEncodedResponseBytes) :
            _options.MaxEncodedResponseBytes;
        if (_response.Content.Headers.ContentLength > encodedLimit)
        {
            throw new InvalidDataException("The response exceeds its encoded body byte limit.");
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
        using var stream = await _response.Content.ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false);
        using var reader = RegistryHttpContentDecoding.CreateReader(stream, codings, limit, _options);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            while (true)
            {
                var count = await reader.ReadAsync(buffer, 0, buffer.Length, cancellation.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, count), cancellation.Token).ConfigureAwait(false);
                total += count;
                Interlocked.Exchange(ref _bodyBytesRead, total);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
