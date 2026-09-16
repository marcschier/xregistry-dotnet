// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace XRegistry.Client;

public sealed partial class XRegistryHttpClient
{
    /// <summary>Streams owned JSON-object collection pages by following opaque same-Registry next links.</summary>
    /// <remarks>
    /// Initial query values are never copied to continuation links. Redirects, ambiguous links, cycles,
    /// duplicate records, changing counts and exhausted cumulative budgets fail explicitly.
    /// Already yielded pages are not rolled back if a later response fails.
    /// </remarks>
    public async IAsyncEnumerable<RegistryCollectionPage> ReadCollectionPagesAsync(
        string path,
        IReadOnlyList<KeyValuePair<string, string?>>? query = null,
        RegistryPaginationOptions? pagination = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        pagination ??= new RegistryPaginationOptions();
        pagination.Validate();
        var nextUri = BuildUri(path, query);
        ulong? requestedLimit = null;
        if (query is not null)
        {
            foreach (var parameter in query.Where(parameter => parameter.Key == "limit"))
            {
                if (requestedLimit is not null || !ulong.TryParse(parameter.Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var limit) || limit == 0)
                {
                    throw new ArgumentException("Pagination requires at most one positive UInt64 limit.", nameof(query));
                }

                requestedLimit = limit;
            }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(pagination.Timeout);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var recordIds = new HashSet<string>(StringComparer.Ordinal);
        var pages = 0;
        long bytes = 0;
        ulong records = 0;
        ulong? expectedCount = null;
        while (nextUri is not null)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (++pages > pagination.MaxPages || !visited.Add(nextUri.AbsoluteUri))
            {
                throw new InvalidDataException("The pagination traversal exceeded its page budget or repeated a page.");
            }

            if (bytes == pagination.MaxTotalBytes)
            {
                throw new InvalidDataException("The pagination traversal exhausted its cumulative byte budget.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, nextUri);
            using var response = await SendRequestAsync(request, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new HttpRequestException("The collection page did not return HTTP 200.", null, response.StatusCode);
            }

            using var metadata = await response.ReadMetadataWithinLimitAsync(
                pagination.MaxTotalBytes - bytes, deadline.Token).ConfigureAwait(false);
            if (response.BodyBytesRead > pagination.MaxTotalBytes - bytes)
            {
                throw new InvalidDataException("The pagination traversal exceeded its cumulative byte budget.");
            }

            bytes += response.BodyBytesRead;
            if (metadata.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("A Registry collection page must be a JSON object keyed by IDs.");
            }

            var links = RegistryHttpLink.Parse(
                response.Headers.TryGetValues("Link", out var fields) ? fields : []);
            RegistryHttpLink? next = null;
            foreach (var link in links)
            {
                if (!link.Relations.Any(relation => relation.Equals("next", StringComparison.OrdinalIgnoreCase) ||
                    relation.Equals("prev", StringComparison.OrdinalIgnoreCase) ||
                    relation.Equals("first", StringComparison.OrdinalIgnoreCase) ||
                    relation.Equals("last", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (link.Parameters.ContainsKey("anchor"))
                {
                    throw new NotSupportedException("Anchored pagination links require a separately selected context.");
                }

                if (link.Parameters.TryGetValue("count", out var countText))
                {
                    if (!ulong.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                        (expectedCount is not null && expectedCount != count))
                    {
                        throw new InvalidDataException("The pagination record count is invalid or changed within the set.");
                    }

                    expectedCount = count;
                }

                if (link.HasRelation("next"))
                {
                    if (next is not null && next.Reference != link.Reference)
                    {
                        throw new InvalidDataException("The collection response has ambiguous next links.");
                    }

                    next = link;
                }
            }

            ulong pageRecords = 0;
            foreach (var record in metadata.RootElement.EnumerateObject())
            {
                if (records == pagination.MaxRecords || !recordIds.Add(record.Name))
                {
                    throw new InvalidDataException("The pagination traversal exceeded its record budget or repeated a record ID.");
                }

                records++;
                pageRecords++;
            }

            if ((requestedLimit is not null && pageRecords > requestedLimit) ||
                (expectedCount is not null && records > expectedCount) ||
                (next is null && expectedCount is not null && records != expectedCount))
            {
                throw new InvalidDataException("The collection pages do not match their limit or advertised total count.");
            }

            var currentUri = nextUri;
            nextUri = next is null ? null : ResolveContinuation(currentUri, next.Reference);
            var page = new RegistryCollectionPage(currentUri, metadata.RootElement, links, expectedCount);
            response.Dispose();
            yield return page;
        }
    }

    private Uri ResolveContinuation(Uri context, string reference)
    {
        if (reference.Length > _options.MaxUriLength || reference.Contains('#', StringComparison.Ordinal) ||
            reference.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidDataException("The pagination URI-reference is invalid or exceeds the URI budget.");
        }

        for (var index = 0; index < reference.Length; index++)
        {
            if (reference[index] == '%' && (index + 2 >= reference.Length ||
                !Uri.IsHexDigit(reference[index + 1]) || !Uri.IsHexDigit(reference[index + 2])))
            {
                throw new InvalidDataException("The pagination URI-reference contains an invalid percent escape.");
            }
        }

        if (!Uri.TryCreate(new Uri(context.OriginalString, UriKind.Absolute), reference, out var result) ||
            !_policy.IsOriginAllowed(result) ||
            result.Fragment.Length != 0 || result.AbsoluteUri.Length > _options.MaxUriLength ||
            !result.AbsolutePath.StartsWith(_root.AbsolutePath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The pagination target escapes the configured Registry origin or root.");
        }

        var queryIndex = reference.IndexOf('?');
        var referencePath = queryIndex < 0 ? reference : reference[..queryIndex];
        var rawQuery = queryIndex < 0 ? "" : reference[queryIndex..];
        string rawPath;
        var wireOptions = new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true };
        if (reference.StartsWith("//", StringComparison.Ordinal) ||
        (Uri.TryCreate(reference, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"))
        {
            var rawAbsolute = new Uri(reference.StartsWith("//", StringComparison.Ordinal)
                ? context.Scheme + ":" + reference : reference, wireOptions);
            rawPath = rawAbsolute.AbsolutePath;
            rawQuery = rawAbsolute.Query;
        }
        else if (referencePath.Length == 0)
        {
            rawPath = context.AbsolutePath;
            if (queryIndex < 0)
            {
                rawQuery = context.Query;
            }
        }
        else
        {
            rawPath = referencePath.StartsWith('/') ? referencePath :
                context.AbsolutePath[..(context.AbsolutePath.LastIndexOf('/') + 1)] + referencePath;
        }

        var segments = new List<string>();
        var rawSegments = rawPath.Split('/');
        for (var index = 0; index < rawSegments.Length; index++)
        {
            var segment = rawSegments[index];
            if (segment is "." or "..")
            {
                if (segment == ".." && segments.Count > 1)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                if (index == rawSegments.Length - 1)
                {
                    segments.Add("");
                }

                continue;
            }

            var decoded = Uri.UnescapeDataString(segment);
            if (decoded is "." or ".." || decoded.IndexOfAny(['/', '\\']) >= 0 || decoded.Any(char.IsControl))
            {
                throw new InvalidDataException("The pagination path contains an unsafe encoded segment.");
            }

            segments.Add(segment);
        }

        // Validate with normal URI semantics first, but do not let Uri canonicalize an opaque cursor or escaped path.
        return new Uri(result.GetLeftPart(UriPartial.Authority) + string.Join('/', segments) + rawQuery, wireOptions);
    }
}
