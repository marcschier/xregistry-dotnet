// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using XRegistry.Federation;

namespace XRegistry.Samples.Bridge;

internal sealed record BridgeReadQuery(ProducerViewOptions View, IReadOnlyList<string> Filters, string? Sort, int? Limit = null, string? Cursor = null)
{
    internal bool HasQuery => Filters.Count != 0 || Sort is not null || Limit is not null;
}

internal static class BridgeViewQuery
{
    internal static BridgeReadQuery Parse(string query, int maximum)
    {
        if (query.Length > maximum || query.Any(character => character is <= ' ' or > '~' or '#' or '\\'))
        {
            throw new BridgeHttpException(400, "bad_flag", "The query is malformed or exceeds its byte budget.");
        }
        var doc = false;
        var binary = false;
        var collections = false;
        var inline = new List<string>();
        var filters = new List<string>();
        string? sort = null;
        int? limit = null;
        string? cursor = null;
        var singletons = new HashSet<string>(StringComparer.Ordinal);
        if (query.Length == 0) { return new(new(), [], null); }
        var parameters = query.Split('&');
        if (parameters.Length > 64) { throw new BridgeHttpException(413, "too_large", "Too many view parameters."); }
        foreach (var parameter in parameters)
        {
            for (var index = 0; index < parameter.Length; index++)
            {
                if (parameter[index] == '%' && (index + 2 >= parameter.Length ||
                    !Uri.IsHexDigit(parameter[index + 1]) || !Uri.IsHexDigit(parameter[index + 2])))
                {
                    throw new BridgeHttpException(400, "bad_flag", "A query escape is incomplete.");
                }
            }
            var equals = parameter.IndexOf('=');
            var key = Uri.UnescapeDataString(equals < 0 ? parameter : parameter[..equals]);
            var value = equals < 0 ? null : Uri.UnescapeDataString(parameter[(equals + 1)..].Replace("+", " ", StringComparison.Ordinal));
            if (key is "doc" or "binary" or "collections" or "sort" or "limit" or "cursor" && !singletons.Add(key))
            {
                throw new BridgeHttpException(400, "bad_flag", "A single-valued view flag was repeated.");
            }
            switch (key)
            {
                case "doc": doc = true; break;
                case "binary": binary = true; break;
                case "collections": collections = true; break;
                case "inline": inline.AddRange(string.IsNullOrEmpty(value) ? ["*"] : value.Split(',')); break;
                case "filter":
                    if (string.IsNullOrEmpty(value)) { throw new BridgeHttpException(400, "bad_filter", "A filter expression is required."); }
                    filters.Add(value);
                    break;
                case "sort":
                    if (string.IsNullOrEmpty(value)) { throw new BridgeHttpException(400, "bad_sort", "A sort projection is required."); }
                    sort = value;
                    break;
                case "limit":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0)
                    {
                        throw new BridgeHttpException(400, "bad_flag", "limit must be a positive bounded page size.");
                    }
                    limit = size;
                    break;
                case "cursor":
                    if (parameters.Length != 1 || string.IsNullOrEmpty(value))
                    {
                        throw new BridgeHttpException(400, "bad_cursor", "Use a continuation URL without added or changed parameters.");
                    }
                    cursor = value;
                    break;
                default:
                    throw new BridgeHttpException(501, "unsupported_query",
                        "Unknown or unsupported selectors are not forwarded to sources.");
            }
        }
        return new(new() { DocumentView = doc, Binary = binary, Collections = collections, Inline = inline }, filters.AsReadOnly(), sort, limit, cursor);
    }
}
