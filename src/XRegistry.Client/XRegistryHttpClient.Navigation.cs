namespace XRegistry.Client;

public sealed partial class XRegistryHttpClient
{
    /// <summary>Performs one GET of an opaque response link after applying the same Registry-root and origin checks as paging.</summary>
    /// <returns>The exact resolved request URI and an owned response. Dispose the response after consumption.</returns>
    /// <remarks>No initial query is copied, no redirects are followed, and no credential is requested for rejected links.</remarks>
    public async ValueTask<(Uri RequestUri, XRegistryHttpResponse Response)> GetLinkAsync(
        Uri responseRequestUri, string reference, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(responseRequestUri);
        ArgumentNullException.ThrowIfNull(reference);
        if (!_policy.IsOriginAllowed(responseRequestUri) ||
            !responseRequestUri.AbsolutePath.StartsWith(_root.AbsolutePath, StringComparison.Ordinal))
        {
            throw new ArgumentException("The link context is outside the configured Registry.", nameof(responseRequestUri));
        }
        var uri = ResolveContinuation(responseRequestUri, reference);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        return (uri, await SendRequestAsync(request, cancellationToken).ConfigureAwait(false));
    }
}
