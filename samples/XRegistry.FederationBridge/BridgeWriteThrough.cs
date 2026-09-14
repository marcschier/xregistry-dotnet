using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using XRegistry.Client;
using XRegistry.Http;

namespace XRegistry.Samples.Bridge;

internal static class BridgeWriteThrough
{
    private static readonly HashSet<string> s_flags = new(StringComparer.Ordinal)
    {
        "binary", "collections", "doc", "epoch", "filter", "ignore", "inline",
        "setdefaultversionid", "specversion", "limit", "offset"
    };
    private static readonly HashSet<string> s_requestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "If-Match", "If-None-Match", "If-Modified-Since", "If-Unmodified-Since", "Range", "If-Range"
    };
    private static readonly HashSet<string> s_responseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Content-Language", "Content-Disposition", "Content-Location",
        "Location", "Link", "ETag", "Last-Modified", "Cache-Control", "Expires", "Vary",
        "Retry-After", "Allow", "WWW-Authenticate", "Accept-Ranges", "Content-Range"
    };

    internal static void Validate(BridgeWriteMount mount)
    {
        RegistryId.Parse(mount.Name);
        ArgumentNullException.ThrowIfNull(mount.Model);
        ArgumentNullException.ThrowIfNull(mount.UpstreamRoot);
        _ = new RegistryHttpConnectionPolicy(mount.UpstreamRoot, mount.AllowLoopbackHttp, mount.AllowPrivateOrigin);
        if (mount.UpstreamRoot.OriginalString.Contains('\\', StringComparison.Ordinal) ||
            mount.UpstreamRoot.AbsolutePath.Split('/').Any(part => part is "." or ".."))
        {
            throw new ArgumentException("A mount needs a canonical, explicitly authorized Registry root.");
        }
    }

    internal static async ValueTask<BridgeResponse> ForwardAsync(HttpContext http, BridgeWriteMount mount,
        string relative, string rawQuery, BridgeHostOptions options, CancellationToken token)
    {
        var method = http.Request.Method;
        if (method is not ("GET" or "HEAD" or "OPTIONS" or "PUT" or "PATCH" or "POST" or "DELETE"))
        {
            throw new BridgeHttpException(405, "unsupported_method", "The mount accepts only core HTTP methods.");
        }
        var mutation = method is "PUT" or "PATCH" or "POST" or "DELETE";
        if (http.User.Identity?.IsAuthenticated != true && (mutation || !options.AllowAnonymousReads))
        {
            throw new BridgeHttpException(401, "unauthorized", "The mount requires an authenticated caller.");
        }
        var path = RegistryPath.Parse(relative is "" or "/" ? "/" : relative);
        var resource = BridgeApplication.CheckModel(mount.Model, path);
        if (mutation && path.Kind is RegistryPathKind.Model or RegistryPathKind.ModelSource or
            RegistryPathKind.Capabilities or RegistryPathKind.CapabilitiesOffered or RegistryPathKind.Export or RegistryPathKind.Discovery)
        {
            throw new BridgeHttpException(501, "unsupported_administration", "This fixed-model mount does not enable upstream model/capability administration.");
        }
        if (method == "PATCH" && resource?.HasDocument == true && !path.IsDetails &&
            path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version)
        {
            throw new BridgeHttpException(405, "details_required", "Document-bearing PATCH requests require the metadata path.");
        }
        ValidateQuery(rawQuery, options.MaxQueryCharacters);
        BridgeApplication.CheckHeaders(http.Request.Headers, options.MaxHeaderBytes);
        if (http.Request.Headers.ContentEncoding.Count != 0 &&
            http.Request.Headers.ContentEncoding.ToString() != "identity")
        {
            throw new BridgeHttpException(415, "unsupported_encoding", "The bounded write-through profile requires identity request bytes.");
        }
        if (!mutation && (http.Request.ContentLength > 0 || http.Request.Headers.TransferEncoding.Count != 0))
        {
            throw new BridgeHttpException(400, "unexpected_body", "Read-only mount operations do not accept a body.");
        }
        if (http.Request.ContentLength > options.MaxRequestBytes)
        {
            throw new BridgeHttpException(413, "too_large", "The request body exceeds its byte budget.");
        }
        var document = resource?.HasDocument == true && !path.IsDetails &&
            path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version;
        ValidateMetadataHeaders(http.Request.Headers, resource, document && mutation, options.MaxHeaderBytes);
        if (!await options.AuthorizeMount(http.User, mount.Name, new HttpMethod(method), token).ConfigureAwait(false))
        {
            throw new BridgeHttpException(403, "forbidden_mount", "The caller is not authorized for this configured mount.");
        }
        var body = mutation
            ? await BridgeApplication.ReadBoundedAsync(http.Request.Body, options.MaxRequestBytes, token).ConfigureAwait(false)
            : [];
        if (mutation && !document)
        {
            if (body.Length == 0 && method != "DELETE")
            {
                throw new BridgeHttpException(400, "missing_body", "A metadata mutation requires an explicit JSON body.");
            }
            if (body.Length != 0)
            {
                var metadata = RegistryJson.Parse(body, new RegistryJsonLimits { MaxBytes = options.MaxRequestBytes });
                if (metadata.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new BridgeHttpException(400, "invalid_metadata", "A metadata mutation requires an object.");
                }
            }
        }
        var root = mount.UpstreamRoot.AbsoluteUri.TrimEnd('/');
        var uri = new Uri(root + (path.Kind == RegistryPathKind.Registry ? "" : path.EscapedPath) + rawQuery,
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        var policy = new RegistryHttpConnectionPolicy(mount.UpstreamRoot, mount.AllowLoopbackHttp, mount.AllowPrivateOrigin);
        CheckUri(policy, mount.UpstreamRoot, uri);
        using var client = policy.CreateClient(options.RequestTimeout);
        using var request = new HttpRequestMessage(new HttpMethod(method), uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.ConnectionClose = true;
        request.Headers.AcceptEncoding.Add(new("identity"));
        foreach (var header in http.Request.Headers)
        {
            if (s_requestHeaders.Contains(header.Key) || header.Key.StartsWith("xRegistry-", StringComparison.OrdinalIgnoreCase))
            {
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                {
                    throw new BridgeHttpException(400, "invalid_header", "A deliberate protocol header could not be forwarded.");
                }
            }
        }
        if (mutation && (body.Length != 0 || document || http.Request.ContentLength is not null || http.Request.Headers.TransferEncoding.Count != 0))
        {
            request.Content = new SingleDispatchContent(body);
            if (http.Request.ContentType is { } contentType) { request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType); }
        }
        if (mount.AuthorizationProvider is { } credentials)
        {
            request.Headers.Authorization = await credentials(uri, token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        var dispatched = false;
        try
        {
            dispatched = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != uri)
            {
                throw new BridgeHttpException(502, "redirected_transport", "The upstream transport changed the authorized request URI.");
            }
            if (response.Content.Headers.ContentEncoding.Any(encoding => encoding != "identity"))
            {
                throw new BridgeHttpException(502, "unsupported_encoding", "The upstream ignored the identity-only response profile.");
            }
            if (method != "HEAD" && response.Content.Headers.ContentLength > options.MaxResponseBytes)
            {
                throw new BridgeHttpException(502, "upstream_too_large", "The upstream representation exceeds its byte budget.");
            }
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var bytes = await BridgeApplication.ReadBoundedAsync(stream, options.MaxResponseBytes, token).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers.NonValidated.Concat(response.Content.Headers.NonValidated))
            {
                if (s_responseHeaders.Contains(header.Key) || header.Key.StartsWith("xRegistry-", StringComparison.OrdinalIgnoreCase))
                {
                    if (!headers.TryAdd(header.Key, string.Join(", ", header.Value)))
                    {
                        throw new BridgeHttpException(502, "duplicate_header", "Upstream protocol headers are ambiguous.");
                    }
                }
            }
            BridgeApplication.ValidateResponseHeaders(headers, options.MaxHeaderBytes);
            ValidateNavigation(headers, policy, mount.UpstreamRoot, uri);
            BridgeApplication.ValidateResponseHeaders(headers, options.MaxHeaderBytes);
            if (method != "HEAD" && response.Content.Headers.ContentLength is { } length && bytes.LongLength != length)
            {
                throw new BridgeHttpException(502, "incomplete_response", "The upstream response body is incomplete.");
            }
            if (response.StatusCode == HttpStatusCode.Created && !headers.ContainsKey("Location"))
            {
                throw new BridgeHttpException(502, "incomplete_response", "A created response requires its authoritative Location.");
            }
            if (response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NoContent && method is not ("HEAD" or "OPTIONS"))
            {
                if (document) { ValidateDocumentHeaders(headers, resource!, path, options.MaxHeaderBytes); }
                else { ValidateMetadataBody(bytes, mount.Model, path, options.MaxResponseBytes, policy, mount.UpstreamRoot, uri, token); }
            }
            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotModified && bytes.Length != 0)
            {
                throw new BridgeHttpException(502, "unexpected_body", "The upstream status forbids a response body.");
            }
            return new BridgeResponse((int)response.StatusCode, bytes, headers);
        }
        catch (Exception exception) when (mutation && dispatched &&
            exception is HttpRequestException or IOException or OperationCanceledException or FormatException or BridgeHttpException)
        {
            throw new BridgeMutationUnknownException(exception);
        }
    }

    private static void ValidateQuery(string rawQuery, int maximum)
    {
        if (rawQuery.Length > maximum || rawQuery.Any(value => value is <= ' ' or > '~' or '#' or '\\'))
        {
            throw new BridgeHttpException(400, "invalid_query", "The raw query is malformed or exceeds its budget.");
        }
        if (rawQuery.Length == 0) { return; }
        if (rawQuery[0] != '?') { throw new BridgeHttpException(400, "invalid_query", "A raw query must retain its delimiter."); }
        var parameters = rawQuery[1..].Split('&');
        if (parameters.Length > 64) { throw new BridgeHttpException(413, "too_large", "Too many query parameters."); }
        foreach (var parameter in parameters)
        {
            for (var index = 0; index < parameter.Length; index++)
            {
                if (parameter[index] == '%' && (index + 2 >= parameter.Length ||
                    !Uri.IsHexDigit(parameter[index + 1]) || !Uri.IsHexDigit(parameter[index + 2])))
                {
                    throw new BridgeHttpException(400, "invalid_query", "A query escape is incomplete.");
                }
            }
            var equals = parameter.IndexOf('=');
            var key = Uri.UnescapeDataString(equals < 0 ? parameter : parameter[..equals]);
            if (!s_flags.Contains(key))
            {
                throw new BridgeHttpException(501, "unsupported_query", "The mount forwards only the declared core query parameters, not native views.");
            }
        }
    }

    private static void ValidateMetadataHeaders(IHeaderDictionary headers, RegistryResourceDefinition? resource,
        bool document, int maximum)
    {
        foreach (var header in headers.Where(header => header.Key.StartsWith("xRegistry-", StringComparison.OrdinalIgnoreCase)))
        {
            if (!document || resource is null)
            {
                throw new BridgeHttpException(400, "extra_xregistry_header", "Metadata headers require a Document mutation.");
            }
            var suffix = header.Key[10..];
            var dot = suffix.IndexOf('.');
            var name = (dot < 0 ? suffix : suffix[..dot]).ToLowerInvariant();
            if (name == resource.Singular || name == resource.Singular + "base64" || name == "contenttype")
            {
                throw new BridgeHttpException(400, "extra_xregistry_header", "This attribute is not an xRegistry request header.");
            }
            if ((!resource.Attributes.TryGetValue(name, out var definition) &&
                !resource.ResourceAttributes.TryGetValue(name, out definition) && !resource.Attributes.TryGetValue("*", out definition)) ||
                header.Value.Count != 1 || !header.Key.All(BridgeApplication.IsToken))
            {
                throw new BridgeHttpException(400, "invalid_header", "The metadata header is unknown or ambiguous.");
            }
            try { _ = RegistryHeaderEncoding.Decode(header.Value[0] ?? "", maximum); }
            catch (FormatException) { throw new BridgeHttpException(400, "invalid_header", "The metadata header has invalid encoding."); }
            if (dot >= 0 && (suffix.Length == dot + 1 || definition.Type != RegistryValueType.Map ||
                definition.Item is not { Type: not (RegistryValueType.Object or RegistryValueType.Array or RegistryValueType.Map or RegistryValueType.Binary) }))
            {
                throw new BridgeHttpException(400, "invalid_header", "A member header requires a scalar-valued modeled map.");
            }
        }
    }

    private static void CheckUri(RegistryHttpConnectionPolicy policy, Uri root, Uri value)
    {
        var prefix = root.AbsolutePath.TrimEnd('/');
        if (!policy.IsOriginAllowed(value) || value.Fragment.Length != 0 ||
            !(value.AbsolutePath == prefix || value.AbsolutePath.StartsWith(prefix + "/", StringComparison.Ordinal)))
        {
            throw new BridgeHttpException(502, "upstream_root_escape", "An upstream navigation target escaped its explicitly authorized Registry root.");
        }
    }

    private static void ValidateNavigation(Dictionary<string, string> headers, RegistryHttpConnectionPolicy policy, Uri root, Uri request)
    {
        foreach (var name in new[] { "Location", "Content-Location" })
        {
            if (headers.TryGetValue(name, out var text))
            {
                var location = ResolveReference(request, text);
                CheckUri(policy, root, location);
                headers[name] = location.OriginalString;
            }
        }
        if (headers.TryGetValue("Link", out var links))
        {
            var prepared = new List<string>();
            foreach (var link in RegistryHttpLink.Parse([links]))
            {
                if (link.Parameters.ContainsKey("anchor"))
                {
                    throw new BridgeHttpException(502, "invalid_link", "An upstream navigation link has an unsupported context.");
                }
                var target = ResolveReference(request, link.Reference);
                if (link.HasRelation("next") || link.HasRelation("prev") || link.HasRelation("first") ||
                    link.HasRelation("last") || link.HasRelation("xregistry-root"))
                {
                    CheckUri(policy, root, target);
                }
                prepared.Add("<" + target.OriginalString + ">" + string.Concat(link.Parameters.Select(parameter =>
                    ";" + parameter.Key + "=\"" + parameter.Value.Replace("\\", "\\\\", StringComparison.Ordinal)
                        .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"")));
            }
            headers["Link"] = string.Join(", ", prepared);
        }
    }

    private static Uri ResolveReference(Uri context, string reference)
    {
        if (!Uri.TryCreate(new Uri(context.OriginalString), reference, out var resolved) || resolved.UserInfo.Length != 0 ||
            reference.Contains('\\', StringComparison.Ordinal) || resolved.Fragment.Length != 0)
        {
            throw new BridgeHttpException(502, "invalid_link", "An upstream URI reference is malformed.");
        }
        var queryAt = reference.IndexOf('?');
        var query = queryAt < 0 ? resolved.Query : reference[queryAt..];
        return new Uri(resolved.GetLeftPart(UriPartial.Path) + query,
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }

    private static void ValidateDocumentHeaders(Dictionary<string, string> headers, RegistryResourceDefinition resource,
        RegistryPath path, int maximum)
    {
        foreach (var header in headers.Where(header => header.Key.StartsWith("xRegistry-", StringComparison.OrdinalIgnoreCase)))
        {
            var name = header.Key[10..];
            if (name.Equals(resource.Singular, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(resource.Singular + "base64", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeHttpException(502, "invalid_document_header", "The upstream serialized Document bytes into a forbidden header.");
            }
            var value = RegistryHeaderEncoding.Decode(header.Value, maximum);
            if ((name.Equals(resource.Singular + "id", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("versionid", StringComparison.OrdinalIgnoreCase)) && !RegistryId.IsValid(value))
            {
                throw new BridgeHttpException(502, "inconsistent_response", "The upstream returned an invalid entity identity header.");
            }
            if (name.Equals(resource.Singular + "id", StringComparison.OrdinalIgnoreCase) && value != path.ResourceId?.Value ||
                path.VersionId is not null && name.Equals("versionid", StringComparison.OrdinalIgnoreCase) && value != path.VersionId.Value)
            {
                throw new BridgeHttpException(502, "inconsistent_response", "The authoritative response names a different entity.");
            }
        }
    }

    private static void ValidateMetadataBody(byte[] bytes, RegistryModel model, RegistryPath path, int maximum,
        RegistryHttpConnectionPolicy policy, Uri root, Uri request, CancellationToken token)
    {
        var json = RegistryJson.Parse(bytes, new RegistryJsonLimits { MaxBytes = maximum });
        if (json.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new BridgeHttpException(502, "invalid_representation", "The upstream metadata representation is not an object.");
        }
        if (path.Kind is RegistryPathKind.Model or RegistryPathKind.ModelSource or RegistryPathKind.Capabilities or
            RegistryPathKind.CapabilitiesOffered or RegistryPathKind.Export or RegistryPathKind.Discovery)
        {
            return;
        }
        var resource = BridgeApplication.CheckModel(model, path);
        var collection = path.Kind is RegistryPathKind.GroupCollection or RegistryPathKind.ResourceCollection or RegistryPathKind.VersionCollection;
        if (collection)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in json.RootElement.EnumerateObject())
            {
                RegistryId.Parse(entry.Name);
                if (!ids.Add(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new BridgeHttpException(502, "invalid_representation", "The upstream collection representation has ambiguous members.");
                }
                ValidateMetadataBody(System.Text.Encoding.UTF8.GetBytes(entry.Value.GetRawText()), model,
                    RegistryPath.Parse(path.EscapedPath + "/" + Uri.EscapeDataString(entry.Name)), maximum, policy, root, request, token);
            }
            return;
        }
        IReadOnlyDictionary<string, RegistryAttributeDefinition> definitions = path.Kind switch
        {
            RegistryPathKind.Registry => model.Attributes,
            RegistryPathKind.Group => model.Groups[path.GroupType!].Attributes,
            RegistryPathKind.Meta => resource!.MetaAttributes,
            RegistryPathKind.Version => resource!.Attributes,
            _ => ResourceDefinitions(resource!)
        };
        var validated = RegistryMetadataValidator.Validate(json, definitions,
            new RegistryMetadataValidationOptions { Model = model, Limits = new RegistryJsonLimits { MaxBytes = maximum } }, token);
        if (validated.Obligations.Count != 0)
        {
            throw new BridgeHttpException(502, "unsupported_obligation", "The mount cannot waive a state-dependent representation obligation.");
        }
        if (!json.RootElement.TryGetProperty("xid", out var xid) || xid.ValueKind != JsonValueKind.String ||
            !SameEntityXid(xid.GetString()!, path) ||
            path.Kind == RegistryPathKind.Group &&
                json.RootElement.GetProperty(model.Groups[path.GroupType!].Singular + "id").GetString() != path.GroupId!.Value ||
            path.ResourceId is not null && json.RootElement.GetProperty(resource!.Singular + "id").GetString() != path.ResourceId.Value ||
            path.VersionId is not null && json.RootElement.GetProperty("versionid").GetString() != path.VersionId.Value)
        {
            throw new BridgeHttpException(502, "inconsistent_response", "The upstream metadata names another entity.");
        }
        var navigation = new HashSet<string>(StringComparer.Ordinal) { "self", "shortself", "metaurl", "defaultversionurl" };
        var collections = path.Kind switch
        {
            RegistryPathKind.Registry => model.Groups.Keys,
            RegistryPathKind.Group => model.Groups[path.GroupType!].Resources.Keys,
            RegistryPathKind.Resource => ["versions"],
            _ => Enumerable.Empty<string>()
        };
        foreach (var name in collections) { navigation.Add(name + "url"); }
        foreach (var name in navigation)
        {
            if (!json.RootElement.TryGetProperty(name, out var value)) { continue; }
            var link = value.GetString() ?? throw new BridgeHttpException(502, "invalid_link", "A modeled navigation link is not a string.");
            if (link.StartsWith('#')) { continue; }
            if (!Uri.TryCreate(new Uri(request.OriginalString), link, out var target))
            {
                throw new BridgeHttpException(502, "invalid_link", "A modeled navigation link is malformed.");
            }
            CheckUri(policy, root, target);
        }
    }

    private static bool SameEntityXid(string xid, RegistryPath expected)
    {
        RegistryPath actual;
        try { actual = RegistryPath.Parse(xid); }
        catch (RegistryException)
        {
            throw new BridgeHttpException(502, "inconsistent_response", "The upstream metadata contains a malformed XID.");
        }
        return !actual.IsDetails && actual.Kind == expected.Kind &&
            actual.GroupType == expected.GroupType && actual.GroupId?.Value == expected.GroupId?.Value &&
            actual.ResourceType == expected.ResourceType && actual.ResourceId?.Value == expected.ResourceId?.Value &&
            actual.VersionId?.Value == expected.VersionId?.Value;
    }

    private static Dictionary<string, RegistryAttributeDefinition> ResourceDefinitions(RegistryResourceDefinition resource)
    {
        var result = new Dictionary<string, RegistryAttributeDefinition>(resource.Attributes, StringComparer.Ordinal);
        foreach (var item in resource.ResourceAttributes) { result[item.Key] = item.Value; }
        return result;
    }

    private sealed class SingleDispatchContent(byte[] bytes) : HttpContent
    {
        private int _serialized;
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => WriteAsync(stream, cancellationToken);
        private Task WriteAsync(Stream stream, CancellationToken token)
        {
            if (Interlocked.Exchange(ref _serialized, 1) != 0) { throw new InvalidOperationException("A mutation body cannot be replayed."); }
            return stream.WriteAsync(bytes, token).AsTask();
        }
    }
}

internal sealed class BridgeMutationUnknownException(Exception inner)
    : Exception("The mutation outcome is UNKNOWN after dispatch. No retry, rollback or compensation was attempted.", inner);
