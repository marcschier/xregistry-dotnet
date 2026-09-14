using System.Net.Http.Headers;
using System.Text;

namespace XRegistry.Client;

/// <summary>Performs single-dispatch HTTP operations against arbitrary model-defined Registry paths.</summary>
public sealed partial class XRegistryHttpClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly RegistryHttpConnectionPolicy _policy;
    private readonly Uri _root;
    private readonly XRegistryHttpClientOptions _options;
    private bool _disposed;

    /// <summary>Constructs an owned, origin-isolated Registry HTTP client.</summary>
    /// <param name="registryRoot">The configured Registry API root.</param>
    /// <param name="options">Finite budgets and explicit loopback/private-origin permissions.</param>
    public XRegistryHttpClient(Uri registryRoot, XRegistryHttpClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registryRoot);
        _options = options ?? new XRegistryHttpClientOptions();
        _options.Validate();
        _policy = new RegistryHttpConnectionPolicy(
            registryRoot, _options.AllowLoopbackHttp, _options.AllowPrivateOrigin);
        _root = new Uri(registryRoot.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _client = _policy.CreateClient(_options.RequestTimeout);
        RegistryHttpContentDecoding.ConfigureAcceptEncoding(_client.DefaultRequestHeaders, _options);
    }

    /// <summary>Gets the configured, normalized Registry root; upstream links do not change it.</summary>
    public Uri Root => _root;

    /// <summary>Sends exactly one complete HTTP request and returns its unchanged result.</summary>
    /// <param name="method">The HTTP method required by the binding for the addressed representation.</param>
    /// <param name="path">An escaped Registry-relative path, optionally starting with one slash.</param>
    /// <param name="body">Optional request bytes; an empty value is present, unlike null.</param>
    /// <param name="contentType">The Content-Type for a present body.</param>
    /// <param name="query">Ordered query values; null values are flags and empty values retain the equals sign.</param>
    /// <param name="cancellationToken">Cancellation covering dispatch and response consumption.</param>
    /// <returns>An owned response whose disposal releases the connection and deadline.</returns>
    /// <remarks>
    /// No mutation is retried. An exception after dispatch can leave an unknown mutation outcome.
    /// Registry error statuses remain response values. This low-level API does not infer model
    /// capabilities or replace a single operation with multiple requests.
    /// </remarks>
    public async ValueTask<XRegistryHttpResponse> SendAsync(
        HttpMethod method,
        string path = "",
        ReadOnlyMemory<byte>? body = null,
        string? contentType = null,
        IReadOnlyList<KeyValuePair<string, string?>>? query = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(method);
        var uri = BuildUri(path, query);
        if (body is { Length: var length } && length > _options.MaxMetadataBytes)
        {
            throw new ArgumentException("Buffered request bytes exceed the configured limit.", nameof(body));
        }

        if (body is null && contentType is not null)
        {
            throw new ArgumentException("Content-Type requires a present request body.", nameof(contentType));
        }

        using var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body.Value.ToArray());
            if (contentType is not null)
            {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
        }

        return await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Streams one caller-owned Document body once, enforcing actual bytes and the complete request deadline.</summary>
    /// <remarks>The stream is not disposed. A failed upload can have an unknown remote outcome; it is never retried by this client.</remarks>
    public async ValueTask<XRegistryHttpResponse> SendDocumentAsync(
        HttpMethod method, string path, Stream document, string contentType,
        IReadOnlyList<KeyValuePair<string, string?>>? query = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (!document.CanRead)
        {
            throw new ArgumentException("The Document stream must be readable.", nameof(document));
        }

        using var request = new HttpRequestMessage(method, BuildUri(path, query));
        request.Content = new SingleUseDocumentContent(document, _options.MaxDocumentBytes);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<XRegistryHttpResponse> SendRequestAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.RequestTimeout);
        var transferred = false;
        try
        {
            if (_options.AuthorizationProvider is not null)
            {
                request.Headers.Authorization =
                    await _options.AuthorizationProvider(request.RequestUri!, deadline.Token).ConfigureAwait(false);
            }

            var response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            transferred = true;
            return new XRegistryHttpResponse(response, deadline, _options);
        }
        finally
        {
            if (!transferred)
            {
                deadline.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
    }

    private Uri BuildUri(string path, IReadOnlyList<KeyValuePair<string, string?>>? query)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length > _options.MaxUriLength || path.IndexOfAny(['\\', '?', '#']) >= 0 ||
            path.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("The Registry-relative path is malformed.", nameof(path));
        }

        var relative = path.StartsWith('/') ? path[1..] : path;
        foreach (var segment in relative.Split('/'))
        {
            if (segment.Length == 0 && relative.Length != 0)
            {
                throw new ArgumentException("Empty path segments are not supported.", nameof(path));
            }

            for (var index = 0; index < segment.Length; index++)
            {
                if (segment[index] == '%' &&
                    (index + 2 >= segment.Length || !Uri.IsHexDigit(segment[index + 1]) ||
                        !Uri.IsHexDigit(segment[index + 2])))
                {
                    throw new ArgumentException("The path contains an invalid escape.", nameof(path));
                }
            }

            var decoded = Uri.UnescapeDataString(segment);
            if (decoded is "." or ".." || decoded.IndexOfAny(['/', '\\']) >= 0 ||
                decoded.Any(char.IsControl))
            {
                throw new ArgumentException("The path contains an unsafe segment.", nameof(path));
            }
        }

        var builder = new StringBuilder(_root.AbsoluteUri);
        builder.Append(relative);
        if (query is not null)
        {
            if (query.Count > _options.MaxQueryParameters)
            {
                throw new ArgumentException("The request exceeds its query parameter limit.", nameof(query));
            }

            for (var index = 0; index < query.Count; index++)
            {
                var parameter = query[index];
                ArgumentException.ThrowIfNullOrEmpty(parameter.Key);
                if (parameter.Key.Length > _options.MaxUriLength ||
                    parameter.Value?.Length > _options.MaxUriLength)
                {
                    throw new ArgumentException("A query parameter exceeds the URI byte budget.", nameof(query));
                }

                builder.Append(index == 0 ? '?' : '&').Append(Uri.EscapeDataString(parameter.Key));
                if (parameter.Value is not null)
                {
                    builder.Append('=').Append(Uri.EscapeDataString(parameter.Value));
                }

                if (builder.Length > _options.MaxUriLength)
                {
                    throw new ArgumentException("The request exceeds its URI byte budget.", nameof(query));
                }
            }
        }

        if (builder.Length > _options.MaxUriLength)
        {
            throw new ArgumentException("The request exceeds its URI byte budget.", nameof(path));
        }

        var result = new Uri(builder.ToString(), UriKind.Absolute);
        if (result.AbsoluteUri.Length > _options.MaxUriLength ||
            !result.AbsolutePath.StartsWith(_root.AbsolutePath, StringComparison.Ordinal))
        {
            throw new ArgumentException("The request escapes the configured Registry root.", nameof(path));
        }

        return result;
    }
}
