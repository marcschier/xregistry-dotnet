using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using XRegistry.Federation;
using XRegistry.Http;
using XRegistry.Queries;

namespace XRegistry.Samples.Bridge;

public static class BridgeApplication
{
    public static IEndpointConventionBuilder Map(WebApplication app, BridgeHostOptions options,
        IReadOnlyList<FederationSourceRegistration> sources, IReadOnlyList<BridgeWriteMount> mounts)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (sources.Count is < 1 or > 8) { throw new ArgumentException("The bridge requires one to eight explicitly ordered sources.", nameof(sources)); }
        if (mounts.Count > 8 || mounts.Select(mount => mount.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mounts.Count)
        {
            throw new ArgumentException("At most eight uniquely named fixed write-through mounts are supported.", nameof(mounts));
        }
        foreach (var mount in mounts) { BridgeWriteThrough.Validate(mount); }
        var handler = new Handler(options, sources.ToArray(), mounts.ToArray(), app.Lifetime.ApplicationStopping);
        app.Lifetime.ApplicationStopped.Register(handler.Dispose);
        return app.Map("/{**bridgePath}", (RequestDelegate)handler.InvokeAsync);
    }

    private sealed class Handler(BridgeHostOptions options, IReadOnlyList<FederationSourceRegistration> sources,
        IReadOnlyList<BridgeWriteMount> mounts, CancellationToken stopping) : IDisposable
    {
        private readonly SemaphoreSlim _admission = new(options.MaxConcurrentRequests, options.MaxConcurrentRequests);
        private readonly BridgePageStore _pages = new(options, sources);
        private static readonly Action<ILogger, Exception?> s_failure = LoggerMessage.Define(
            LogLevel.Error, new EventId(1, "BridgeReadFailure"), "The bounded bridge read failed.");

        internal async Task InvokeAsync(HttpContext http)
        {
            using var deadline = new CancellationTokenSource(options.RequestTimeout);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, stopping, deadline.Token);
            var token = lifetime.Token;
            var entered = false;
            try
            {
                var raw = http.Features.Get<IHttpRequestFeature>()?.RawTarget ?? throw new BridgeHttpException(400, "bad_target", "An exact HTTP request target is required.");
                if (raw.Length > 8192 + options.MaxQueryCharacters) { throw new BridgeHttpException(413, "too_large", "The request target exceeds its budget."); }
                var queryAt = raw.IndexOf('?');
                var rawPath = queryAt < 0 ? raw : raw[..queryAt];
                if (!AtMount(rawPath, options.AggregatePath))
                {
                    var mount = mounts.FirstOrDefault(candidate => AtMount(rawPath, options.MountPrefix + "/" + candidate.Name))
                        ?? throw new BridgeHttpException(404, "unknown_mount", "No configured bridge mount matches the exact path.");
                    entered = await _admission.WaitAsync(0, token).ConfigureAwait(false);
                    if (!entered) { throw new BridgeHttpException(503, "bridge_busy", "The bridge's bounded request capacity is in use."); }
                    var forwarded = await BridgeWriteThrough.ForwardAsync(http, mount,
                        rawPath[(options.MountPrefix.Length + 1 + mount.Name.Length)..],
                        queryAt < 0 ? "" : raw[queryAt..], options, token).ConfigureAwait(false);
                    try { await forwarded.WriteAsync(http, token).ConfigureAwait(false); }
                    catch (Exception exception) when (http.Request.Method is "PUT" or "PATCH" or "POST" or "DELETE" &&
                        exception is IOException or OperationCanceledException or InvalidOperationException)
                    {
                        throw new BridgeMutationUnknownException(exception);
                    }
                    return;
                }
                if (http.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
                {
                    http.Response.Headers.Allow = "GET, HEAD, OPTIONS";
                    throw new BridgeHttpException(405, "read_only_aggregate", "The producer-resolved aggregate is read-only.");
                }
                if (!options.AllowAnonymousReads && http.User.Identity?.IsAuthenticated != true)
                {
                    throw new BridgeHttpException(401, "unauthorized", "An authenticated reader is required.");
                }
                var presentation = BridgeViewQuery.Parse(queryAt < 0 ? "" : raw[(queryAt + 1)..], options.MaxQueryCharacters);
                CheckHeaders(http.Request.Headers, options.MaxHeaderBytes);
                var relative = rawPath[options.AggregatePath.Length..];
                var path = RegistryPath.Parse(relative is "" or "/" ? "/" : relative);
                CheckModel(options.Model, path);
                if (presentation.Limit is not null || presentation.Cursor is not null)
                {
                    if (path.Kind is not (RegistryPathKind.GroupCollection or RegistryPathKind.ResourceCollection or RegistryPathKind.VersionCollection) ||
                        http.Request.Method is not ("GET" or "HEAD"))
                    {
                        throw new BridgeHttpException(400, "bad_flag", "Paging applies only to collection reads.");
                    }
                    if (options.AuthorizeRetainedRead is null) { throw new BridgeHttpException(501, "paging_not_configured", "No retained-read authorization policy is configured."); }
                    if (presentation.Limit > options.PagingLimits.MaxPageRecords) { throw new BridgeHttpException(413, "too_large", "The requested page size exceeds its bound."); }
                }
                if (http.Request.Method == "OPTIONS")
                {
                    await new BridgeResponse(204, [], new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Allow"] = "GET, HEAD, OPTIONS",
                        ["Link"] = "<" + options.PublicRoot.AbsoluteUri.TrimEnd('/') + ">;rel=xregistry-root"
                    }).WriteAsync(http, token).ConfigureAwait(false);
                    return;
                }
                entered = await _admission.WaitAsync(0, token).ConfigureAwait(false);
                if (!entered) { throw new BridgeHttpException(503, "bridge_busy", "The bridge's bounded request capacity is in use."); }
                if (presentation.Cursor is { } cursor)
                {
                    using var replayBudget = new RegistryQueryBudget(options.QueryLimits, options.TimeProvider, token);
                    var page = await _pages.ReadAsync(http.User, path, cursor, replayBudget).ConfigureAwait(false);
                    await page.WriteAsync(http, replayBudget.CancellationToken).ConfigureAwait(false);
                    return;
                }
                var eligible = new List<FederationSourceRegistration>();
                foreach (var source in sources)
                {
                    if (await options.AuthorizeSource(http.User, source.Name, token).ConfigureAwait(false)) { eligible.Add(source); }
                }
                if (eligible.Count == 0) { throw new BridgeHttpException(403, "forbidden_source", "The caller has no eligible configured read source."); }
                BridgeResponse response;
                var view = new ProducerRegistryView(options.Model, options.PublicRoot, options.RegistryId,
                    eligible, new FederationReadBudget(options.ReadLimits), Capabilities(options));
                await using (view.ConfigureAwait(false))
                {
                    using var queryBudget = new RegistryQueryBudget(options.QueryLimits, options.TimeProvider, token);
                    var result = presentation.HasQuery
                        ? await view.ReadQueryAsync(new RegistryQueryRequest(path, options.PublicRoot)
                        { Filters = presentation.Filters, Sort = presentation.Sort, DefaultSortById = presentation.Limit is not null },
                            presentation.View, queryBudget).ConfigureAwait(false)
                        : await view.ReadAsync(path, presentation.View, token).ConfigureAwait(false);
                    response = await PrepareAsync(result, path, view.Resource(path), options,
                        presentation.HasQuery ? queryBudget.CancellationToken : token).ConfigureAwait(false);
                    if (presentation.HasQuery) { queryBudget.Spend(0); }
                    if (presentation.Limit is { } limit)
                    {
                        response = await _pages.CreateAsync(http.User, path, queryAt < 0 ? "" : raw[(queryAt + 1)..],
                            limit, result, response, queryBudget).ConfigureAwait(false);
                    }
                }
                await response.WriteAsync(http, token).ConfigureAwait(false);
            }
            catch (BridgeMutationUnknownException exception)
            {
                Log(http, exception);
                if (!http.Response.HasStarted) { http.Response.Headers["X-Bridge-Mutation-Outcome"] = "unknown"; }
                await ProblemAsync(http, 503, "upstream_outcome_unknown", exception.Message, options.PublicRoot).ConfigureAwait(false);
            }
            catch (BridgeHttpException exception)
            {
                await ProblemAsync(http, exception.Status, exception.Code, exception.Message, options.PublicRoot).ConfigureAwait(false);
            }
            catch (RegistryException exception)
            {
                var code = exception.Diagnostic.Code;
                var status = code switch
                {
                    "not_found" => 404,
                    "too_large" => 413,
                    "server_busy" => 503,
                    "query_source_incomplete" or "invalid_query_source" => 502,
                    _ => 400
                };
                await ProblemAsync(http, status, code, "The bridge request or shared query could not be satisfied.", options.PublicRoot).ConfigureAwait(false);
            }
            catch (FormatException)
            {
                await ProblemAsync(http, 400, "invalid_header", "A request header or protocol value is malformed.", options.PublicRoot).ConfigureAwait(false);
            }
            catch (FederationException exception)
            {
                var status = exception.Code switch
                {
                    FederationErrorCode.NotFound => 404,
                    FederationErrorCode.PolicyDenied => 403,
                    FederationErrorCode.UnsupportedBinding or FederationErrorCode.UnsupportedOperation or FederationErrorCode.UnsupportedVersion => 501,
                    FederationErrorCode.LimitExceeded => 413,
                    FederationErrorCode.Unavailable => 503,
                    _ => 502
                };
                if (exception.Diagnostic == "alias_origin_conflict") { status = 409; }
                if (exception.Diagnostic is "malformed_xref" or "cannot_doc_xref" or "bad_inline" or "bad_flag") { status = 400; }
                Log(http, exception);
                var code = exception.Diagnostic is "alias_origin_conflict" or "malformed_xref" or "cannot_doc_xref" or "bad_inline" or "bad_flag"
                    ? exception.Diagnostic : "source_" + exception.Code.ToString().ToLowerInvariant();
                await ProblemAsync(http, status, code,
                    "The selected read view could not satisfy the request; no alternate Version origin was used.", options.PublicRoot).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (http.RequestAborted.IsCancellationRequested || stopping.IsCancellationRequested) { http.Abort(); }
                else { await ProblemAsync(http, 504, "bridge_timeout", "The bridge request deadline expired.", options.PublicRoot).ConfigureAwait(false); }
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException or AggregateException)
            {
                Log(http, exception);
                await ProblemAsync(http, 502, "source_failure", "The selected source could not complete the response.", options.PublicRoot).ConfigureAwait(false);
            }
            finally { if (entered) { _admission.Release(); } }
        }

        private static void Log(HttpContext http, Exception exception) =>
            s_failure(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("XRegistry.Bridge"), exception);

        public void Dispose() { _pages.Clear(); _admission.Dispose(); }
    }

    private static JsonElement Capabilities(BridgeHostOptions options)
    {
        var value = JsonNode.Parse(ProducerRegistryView.Capabilities.GetRawText())!.AsObject();
        value["pagination"] = options.AuthorizeRetainedRead is not null;
        return RegistryJson.Parse(value.ToJsonString()).RootElement;
    }

    private static async ValueTask<BridgeResponse> PrepareAsync(ProducerRegistryResult result, RegistryPath path,
        RegistryResourceDefinition? resource, BridgeHostOptions options, CancellationToken token)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Link"] = "<" + options.PublicRoot.AbsoluteUri.TrimEnd('/') + ">;rel=xregistry-root",
            ["Cache-Control"] = "no-store",
            ["X-Bridge-Sources"] = string.Join(",", result.Origins.Select(origin => origin.Name)),
            ["X-Bridge-Consistency"] = result.Origins.All(origin => origin.Context.IsImmutable) && result.Origins.Count != 0
                ? "per-source-pins" : "live-best-effort"
        };
        if (result.Origins.Count == 1)
        {
            var origin = result.Origins[0];
            headers["X-Bridge-Binding"] = RegistryHeaderEncoding.Encode(origin.Context.Binding);
            if (origin.Context.Revision is { } revision) { headers["X-Bridge-Revision"] = RegistryHeaderEncoding.Encode(revision); }
            if (origin.Context.RootSha256 is { } hash) { headers["X-Bridge-Root-Sha256"] = hash; }
        }
        var documentRequested = result.IsDocument;
        byte[] body;
        var status = 200;
        if (documentRequested)
        {
            EncodeMetadataHeaders(result.Metadata, resource!, headers, options.MaxHeaderBytes);
            if (result.SelectedXid.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 6)
            {
                headers["Content-Location"] = PublicUrl(options.PublicRoot, result.SelectedXid);
            }
            if (result.Document is { } document)
            {
                if (document.Length > options.MaxResponseBytes) { throw new BridgeHttpException(413, "too_large", "The exact Document exceeds the response budget."); }
                using var stream = document.OpenRead();
                body = await ReadBoundedAsync(stream, options.MaxResponseBytes, token).ConfigureAwait(false);
                headers["Content-Type"] = document.ContentType ?? "application/octet-stream";
                if (result.Metadata.TryGetProperty("contenttype", out var declared) &&
                    declared.GetString() != headers["Content-Type"])
                {
                    throw new BridgeHttpException(502, "inconsistent_document", "The captured Document and metadata disagree about content type.");
                }
            }
            else if (result.ExternalDocument.ValueKind == JsonValueKind.Object)
            {
                if (!result.ExternalDocument.TryGetProperty("kind", out var kind) || kind.GetString() != "external" ||
                    !result.ExternalDocument.TryGetProperty("uri", out var value) || value.ValueKind != JsonValueKind.String ||
                    !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
                    uri.Scheme is not ("http" or "https") || !options.ExternalDocumentOrigins.Any(origin => SameOrigin(origin, uri)))
                {
                    throw new BridgeHttpException(403, "external_document_denied", "The explicit external Document origin is not authorized.");
                }
                headers["Location"] = uri.OriginalString;
                status = 303;
                body = [];
            }
            else { throw new BridgeHttpException(502, "missing_document", "No exact Document or explicit external descriptor was returned."); }
        }
        else
        {
            headers["Content-Type"] = "application/json; charset=utf-8";
            body = Encoding.UTF8.GetBytes(result.Metadata.GetRawText());
            if (body.Length > options.MaxResponseBytes) { throw new BridgeHttpException(413, "too_large", "The metadata response exceeds its byte budget."); }
        }
        ValidateResponseHeaders(headers, options.MaxHeaderBytes);
        return new(status, body, headers);
    }

    internal static RegistryResourceDefinition? CheckModel(RegistryModel model, RegistryPath path)
    {
        if (path.GroupType is null) { return null; }
        if (!model.Groups.TryGetValue(path.GroupType, out var group)) { throw new BridgeHttpException(404, "unknown_type", "The Group type is not part of the configured model."); }
        if (path.ResourceType is null) { return null; }
        return group.Resources.TryGetValue(path.ResourceType, out var resource) ? resource :
            throw new BridgeHttpException(404, "unknown_type", "The Resource type is not part of the configured model.");
    }

    internal static bool AtMount(string path, string mount) =>
        path == mount || path.StartsWith(mount + "/", StringComparison.Ordinal);

    internal static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    internal static string PublicUrl(Uri root, string xid) =>
        root.AbsoluteUri.TrimEnd('/') + (xid == "/" ? "" : "/" + string.Join('/', xid[1..].Split('/')
            .Select(segment => Uri.EscapeDataString(RegistryId.ParseEscaped(segment).Value))));

    internal static async ValueTask<byte[]> ReadBoundedAsync(Stream input, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(maximum + 1, 8192)];
        while (true)
        {
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) { break; }
            if (count > maximum - output.Length) { throw new BridgeHttpException(413, "too_large", "An HTTP body exceeds its byte budget."); }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static void CheckHeaders(IHeaderDictionary headers, int maximum)
    {
        long bytes = 0;
        foreach (var header in headers)
        {
            bytes += header.Key.Length + header.Value.Sum(value => value?.Length ?? 0);
            if (bytes > maximum) { throw new BridgeHttpException(413, "too_large", "The HTTP headers exceed their byte budget."); }
        }
    }

    private static void EncodeMetadataHeaders(JsonElement metadata, RegistryResourceDefinition resource,
        Dictionary<string, string> headers, int maximum)
    {
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name == resource.Singular || property.Name == resource.Singular + "base64" || property.Value.ValueKind == JsonValueKind.Null) { continue; }
            if (property.Name == "contenttype") { headers["Content-Type"] = property.Value.GetString()!; }
            else if (Scalar(property.Value)) { Add("xRegistry-" + property.Name, property.Value); }
            else if (property.Value.ValueKind == JsonValueKind.Object &&
                resource.Attributes.TryGetValue(property.Name, out var definition) && definition.Type == RegistryValueType.Map &&
                definition.Item is { Type: not (RegistryValueType.Array or RegistryValueType.Map or RegistryValueType.Object or RegistryValueType.Binary) })
            {
                foreach (var entry in property.Value.EnumerateObject()) { Add("xRegistry-" + property.Name + "." + entry.Name, entry.Value); }
            }
        }
        void Add(string name, JsonElement value)
        {
            if (!name.All(IsToken) || !Scalar(value) || headers.ContainsKey(name))
            {
                throw new BridgeHttpException(502, "header_representation", "Source metadata cannot be represented unambiguously in HTTP headers.");
            }
            headers.Add(name, RegistryHeaderEncoding.Encode(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText(), maximum));
        }
    }

    internal static bool IsToken(char value) => char.IsAsciiLetterOrDigit(value) ||
        value is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    internal static void ValidateResponseHeaders(Dictionary<string, string> headers, int maximum)
    {
        long size = 0;
        foreach (var header in headers)
        {
            size += header.Key.Length + header.Value.Length + 4;
            if (!header.Key.All(IsToken) || header.Value.Any(character => character is < ' ' or > '~'))
            {
                throw new BridgeHttpException(502, "header_representation", "The response headers are not safely representable.");
            }
        }
        if (size > maximum) { throw new BridgeHttpException(413, "too_large", "The response headers exceed their budget."); }
        if (headers.TryGetValue("Content-Type", out var media) && !MediaTypeHeaderValue.TryParse(media, out _))
        {
            throw new BridgeHttpException(502, "header_representation", "The response Content-Type is invalid.");
        }
    }

    private static bool Scalar(JsonElement value) =>
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;

    internal static async Task ProblemAsync(HttpContext http, int status, string code, string detail, Uri root)
    {
        if (http.Response.HasStarted || http.RequestAborted.IsCancellationRequested) { http.Abort(); return; }
        var allow = http.Response.Headers.Allow;
        var outcome = http.Response.Headers["X-Bridge-Mutation-Outcome"];
        http.Response.Headers.Clear();
        if (allow.Count != 0) { http.Response.Headers.Allow = allow; }
        if (outcome.Count != 0) { http.Response.Headers["X-Bridge-Mutation-Outcome"] = outcome; }
        using var bytes = new MemoryStream();
        using (var json = new Utf8JsonWriter(bytes))
        {
            json.WriteStartObject();
            json.WriteString("type", "urn:xregistry-dotnet:bridge:" + code);
            json.WriteString("code", code);
            json.WriteNumber("status", status);
            json.WriteString("title", detail);
            json.WriteEndObject();
        }
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/problem+json";
        http.Response.Headers.Link = "<" + root.AbsoluteUri.TrimEnd('/') + ">;rel=xregistry-root";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.ContentLength = bytes.Length;
        if (http.Request.Method != "HEAD")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await http.Response.Body.WriteAsync(bytes.ToArray(), timeout.Token).ConfigureAwait(false);
        }
    }
}

internal sealed class BridgeResponse(int status, byte[] body, Dictionary<string, string> headers)
{
    internal int StatusCode => status;
    internal Dictionary<string, string> Headers => headers;
    internal async Task WriteAsync(HttpContext http, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        http.Response.StatusCode = status;
        foreach (var header in headers) { http.Response.Headers[header.Key] = header.Value; }
        if (status != 204 && !headers.ContainsKey("Content-Length")) { http.Response.ContentLength = body.Length; }
        if (http.Request.Method != "HEAD" && body.Length != 0) { await http.Response.Body.WriteAsync(body, token).ConfigureAwait(false); }
    }
}

internal sealed class BridgeHttpException(int status, string code, string message) : Exception(message)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
}
