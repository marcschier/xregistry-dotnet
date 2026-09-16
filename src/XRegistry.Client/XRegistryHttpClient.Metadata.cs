// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net.Http.Headers;
using System.Text.Json;
using XRegistry.Http;

namespace XRegistry.Client;

public sealed partial class XRegistryHttpClient
{
    /// <summary>Streams one Document with model-aware metadata headers in a single PUT or POST.</summary>
    /// <param name="method">PUT or POST, as supported by the addressed Resource or Version.</param>
    /// <param name="path">A Registry-relative Document path, not a collection, Meta entity or $details view.</param>
    /// <param name="document">The readable caller-owned stream; it is never disposed or replayed.</param>
    /// <param name="metadata">The requested metadata changes, including optional contenttype; omitted attributes remain absent.</param>
    /// <param name="resource">The explicit effective Resource definition for this path.</param>
    /// <param name="query">Ordered query values, with null flags and empty values preserved.</param>
    /// <param name="cancellationToken">Cancellation covering dispatch and response consumption.</param>
    /// <returns>An owned response; header metadata and Document bytes can be read separately.</returns>
    /// <remarks>
    /// Invalid targets, forbidden inline Documents, unrepresentable values and exhausted metadata budgets
    /// fail before credentials, stream reads or dispatch. Content-Type is taken only from metadata.
    /// Ordinary read-only attributes are ignored; epoch, versionid and the Resource ID remain guards.
    /// Non-null external Document URLs require an empty seekable stream, so emptiness is known without reading.
    /// </remarks>
    public async ValueTask<XRegistryHttpResponse> SendDocumentAsync(
        HttpMethod method, string path, Stream document, RegistryJson metadata, RegistryResourceDefinition resource,
        IReadOnlyList<KeyValuePair<string, string?>>? query = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(resource);
        if (!document.CanRead)
        {
            throw new ArgumentException("The Document stream must be readable.", nameof(document));
        }

        var uri = BuildUri(path, query);
        var target = RegistryPath.Parse(path.StartsWith('/') ? path : "/" + path);
        if (method != HttpMethod.Put && method != HttpMethod.Post || !resource.HasDocument ||
            target.IsDetails || target.Kind is not (RegistryPathKind.Resource or RegistryPathKind.Version) ||
            target.ResourceType != resource.Plural)
        {
            throw new ArgumentException("Model-aware Document headers require a PUT or POST to the matching Document representation.", nameof(path));
        }

        var headers = RegistryHeaderMetadata.Encode(
            metadata, resource, RegistryHeaderMetadataDirection.ClientInput, _options.HeaderMetadataOptions(), target.EscapedPath);
        if (metadata.RootElement.TryGetProperty(resource.Singular + "url", out var documentUrl) &&
            documentUrl.ValueKind != JsonValueKind.Null &&
            (!document.CanSeek || document.Length != document.Position))
        {
            throw new ArgumentException("An external Document URL requires a known empty stream; use a metadata-body request otherwise.", nameof(document));
        }

        using var request = new HttpRequestMessage(method, uri);
        request.Content = new SingleUseDocumentContent(document, _options.MaxDocumentBytes);
        foreach (var header in headers)
        {
            HttpHeaders destination = header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                ? request.Content.Headers : request.Headers;
            if (!destination.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new InvalidOperationException("A validated metadata header could not be added to the HTTP request.");
            }
        }

        return await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
