using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XRegistry.Server;

namespace XRegistry.AspNetCore;

/// <summary>Maps a registry using an explicit RequestDelegate, without reflection-based endpoint binding or JSON.</summary>
public static class RegistryEndpointRouteBuilderExtensions
{
    /// <summary>Maps every model-defined route below the explicit local mount. The caller owns the engine and host lifetime.</summary>
    public static IEndpointConventionBuilder MapXRegistry(this IEndpointRouteBuilder endpoints, RegistryEngine engine,
        RegistryHttpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(engine);
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxErrorBytes, 1024);
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The request timeout must be finite and at most ten minutes.");
        }

        if (options.AuthenticationChallenge?.Any(static character => character is < ' ' or > '~') == true)
        {
            throw new ArgumentException("AuthenticationChallenge must be a printable ASCII HTTP field value.", nameof(options));
        }

        var mount = options.MountPath;
        if (mount.Length != 0 && (!mount.StartsWith('/') || mount.EndsWith('/') ||
            mount.Contains('{', StringComparison.Ordinal) || mount.Contains('}', StringComparison.Ordinal) ||
            mount.Contains('?', StringComparison.Ordinal) || mount.Contains('#', StringComparison.Ordinal) ||
            mount.Contains('%', StringComparison.Ordinal) || mount.Contains('\\', StringComparison.Ordinal) ||
            mount.Split('/').Skip(1).Any(static segment => segment.Length == 0 || segment is "." or "..")))
        {
            throw new ArgumentException("MountPath must be an unescaped absolute path prefix without a trailing slash or route parameters.", nameof(options));
        }

        return endpoints.Map(mount + "/{**xregistryPath}", new Handler(engine, options).InvokeAsync);
    }

    private sealed class Handler(RegistryEngine engine, RegistryHttpOptions options)
    {
        private static readonly Action<ILogger, Exception?> s_infrastructureFailure = LoggerMessage.Define(
            LogLevel.Error, new EventId(1, "RegistryInfrastructureFailure"), "The registry request failed at the persistence boundary.");

        internal async Task InvokeAsync(HttpContext http)
        {
            using var timeout = new CancellationTokenSource(options.RequestTimeout);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, timeout.Token);
            var cancellationToken = lifetime.Token;
            RegistryRouteDescription? route = null;
            RegistryPath? path = null;
            PreparedResponse? prepared = null;
            var executed = false;
            var requestPath = "/";
            try
            {
                var raw = http.Features.Get<IHttpRequestFeature>()?.RawTarget ?? http.Request.Path.Value ?? "/";
                var queryAt = raw.IndexOf('?');
                var rawPath = queryAt < 0 ? raw : raw[..queryAt];
                var pathBase = http.Request.PathBase.Value ?? "";
                var prefix = pathBase + options.MountPath;
                if (!rawPath.StartsWith(prefix, StringComparison.Ordinal) ||
                    rawPath.Length > prefix.Length && rawPath[prefix.Length] != '/')
                {
                    throw Error("api_not_found", rawPath, "The path is outside the configured Registry mount.");
                }

                var relative = rawPath[prefix.Length..];
                if (relative.Length > 1 && relative.EndsWith('/'))
                {
                    relative = relative[..^1];
                }
                requestPath = relative.Length == 0 ? "/" : relative;

                var knownAction = TryAction(http.Request.Method, out var action);
                var caller = options.CallerMapper is { } mapper
                    ? mapper(http) ?? throw new InvalidOperationException("The trusted caller mapper returned no principal.")
                    : http.User;
                var context = new RegistryOperationContext(caller)
                {
                    PrepareResponseAsync = async (result, ct) =>
                        prepared = await PreparedResponse.CreateAsync(result, engine, ct).ConfigureAwait(false)
                };
                if (requestPath.Split('/') is { Length: > 2 } segments &&
                    segments[1] is "model" or "modelsource" or "capabilities" or "capabilitiesoffered" or "export" or ".xregistry")
                {
                    await engine.AuthorizeRequestAsync(RegistryAction.Read, RegistryPath.Parse("/"), context, cancellationToken).ConfigureAwait(false);
                    throw Error("api_not_found", requestPath, "The administrative API path is not supported.");
                }

                try
                {
                    path = await engine.ResolvePathAsync(action, requestPath, context, cancellationToken).ConfigureAwait(false);
                }
                catch (RegistryException exception) when (exception.Diagnostic.Code is "malformed_path" or "malformed_id" or "bad_details")
                {
                    if (exception.Diagnostic.Code == "malformed_id")
                    {
                        var parts = (requestPath.EndsWith("$details", StringComparison.Ordinal) ? requestPath[..^8] : requestPath).Split('/');
                        for (var index = 2; index < parts.Length; index += 2)
                        {
                            try
                            {
                                _ = RegistryId.ParseEscaped(parts[index]);
                            }
                            catch (RegistryException)
                            {
                                throw Error("malformed_id", engine.PublicRoot.AbsoluteUri.TrimEnd('/') + requestPath,
                                    "The identifier does not satisfy the Core ID grammar.", ("id", parts[index]));
                            }
                        }
                    }

                    throw Error(exception.Diagnostic.Code == "bad_details" ? "bad_details" : "bad_request", requestPath,
                        exception.Diagnostic.Message);
                }

                CheckHeaders(http.Request.Headers, engine.Limits.MaxHeaderBytes, requestPath);
                var parameters = ParseQuery(queryAt < 0 ? "" : raw[(queryAt + 1)..], engine.Limits.MaxQueryCharacters, requestPath);
                route = await engine.DescribeAsync(action, path, context, parameters, cancellationToken).ConfigureAwait(false);
                if (!knownAction || !route.AllowedActions.Contains(action))
                {
                    var detailsRequired = action == RegistryAction.Patch && route.ResourceDefinition?.HasDocument == true &&
                        path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version && !path.IsDetails;
                    throw Error(detailsRequired ? "details_required" : "action_not_supported", requestPath,
                        detailsRequired ? "PATCH requires the metadata view for document-bearing entities." : "The HTTP method is not supported.",
                        ("action", http.Request.Method));
                }

                RegistryJson? metadata = null;
                Stream? document = null;
                var write = action is RegistryAction.Replace or RegistryAction.Patch or RegistryAction.Post;
                var documentInput = write && path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version &&
                    route.ResourceDefinition!.HasDocument && !path.IsDetails;
                if (documentInput)
                {
                    if (http.Request.ContentLength > engine.Limits.MaxDocumentBytes)
                    {
                        throw Error("request_too_large", requestPath, "The request Document exceeds its byte budget.");
                    }

                    metadata = HeaderMetadata.Decode(http.Request.Headers, route.ResourceDefinition!, engine.Limits, requestPath);
                    document = http.Request.Body;
                }
                else if (write || action == RegistryAction.Delete)
                {
                    var extra = http.Request.Headers.FirstOrDefault(static header => header.Key.StartsWith("xRegistry-", StringComparison.OrdinalIgnoreCase));
                    if (extra.Key is not null)
                    {
                        throw Error("extra_xregistry_header", requestPath, "Metadata-body requests must not contain xRegistry headers.", ("name", extra.Key));
                    }

                    if (http.Request.ContentLength > engine.Limits.Json.MaxBytes)
                    {
                        throw Error("request_too_large", requestPath, "The metadata body exceeds its byte budget.");
                    }

                    var bytes = await ReadBodyAsync(http.Request.Body, engine.Limits.Json.MaxBytes, cancellationToken).ConfigureAwait(false);
                    if (bytes.Length == 0)
                    {
                        if (write)
                        {
                            throw Error("missing_body", requestPath, "The request is missing metadata; use '{}'.");
                        }
                    }
                    else
                    {
                        try
                        {
                            metadata = RegistryJson.Parse(bytes, engine.Limits.Json);
                        }
                        catch (RegistryException exception)
                        {
                            throw Error(exception.Diagnostic.Code is "byte_limit" or "node_limit" or "depth_limit" or "number_limit"
                                ? "request_too_large" : "parsing_data", requestPath, exception.Diagnostic.Message);
                        }
                    }
                }

                await engine.ExecuteAsync(new(action, path)
                {
                    Metadata = metadata,
                    Document = document,
                    ContentType = http.Request.ContentType,
                    Parameters = parameters,
                    ExpectedModelRevision = route.ModelRevision
                }, context, cancellationToken).ConfigureAwait(false);
                executed = true;
                if (prepared is null)
                {
                    throw new InvalidOperationException("The engine did not prepare a response.");
                }

                await prepared.WriteAsync(http, action == RegistryAction.Head, cancellationToken).ConfigureAwait(false);
            }
            catch (RegistryException exception)
            {
                if (executed)
                {
                    http.Abort();
                    return;
                }

                var diagnostic = exception.Diagnostic.Path.Length == 0
                    ? exception.Diagnostic with { Path = requestPath } : exception.Diagnostic;
                await WriteProblemAsync(http, diagnostic, route, exception.Data["xregistry.args"] as IReadOnlyDictionary<string, string>)
                    .ConfigureAwait(false);
            }
            catch (RegistryConcurrencyException)
            {
                await WriteProblemAsync(http, new("server_busy", path?.EscapedPath ?? "/", "The registry changed before publication; no changes were committed."), route)
                    .ConfigureAwait(false);
            }
            catch (RegistryCommitOutcomeUnknownException exception)
            {
                LogFailure(http, exception);
                http.Response.Headers["xRegistry-commit-outcome"] = "unknown";
                await WriteProblemAsync(http, new("commit_outcome_unknown", path?.EscapedPath ?? "/",
                    "The commit outcome is unknown. Do not automatically retry this mutation."), route).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                http.Abort();
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (executed || http.Response.HasStarted)
                {
                    http.Abort();
                }
                else
                {
                    await WriteProblemAsync(http, new("request_timeout", path?.EscapedPath ?? "/",
                        "The request lifetime budget was exhausted before publication."), route).ConfigureAwait(false);
                }
            }
            catch (BadHttpRequestException exception)
            {
                await WriteProblemAsync(http, new(exception.StatusCode == StatusCodes.Status413PayloadTooLarge ? "request_too_large" : "bad_request",
                    path?.EscapedPath ?? "/", "The HTTP request body could not be read."), route).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                LogFailure(http, exception);
                if (executed || http.Response.HasStarted)
                {
                    http.Abort();
                }
                else
                {
                    var code = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) ? "data_retrieval_error" : "server_error";
                    await WriteProblemAsync(http, new(code, path?.EscapedPath ?? "/", "The registry infrastructure could not complete the request."), route)
                        .ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException exception)
            {
                var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("XRegistry.AspNetCore");
                s_infrastructureFailure(logger, exception);
                if (executed)
                {
                    http.Abort();
                }
                else
                {
                    await WriteProblemAsync(http, new("server_error", path?.EscapedPath ?? "/",
                        "An infrastructure contract could not be satisfied."), route).ConfigureAwait(false);
                }
            }
        }

        private async Task WriteProblemAsync(HttpContext http, RegistryDiagnostic diagnostic, RegistryRouteDescription? route,
            IReadOnlyDictionary<string, string>? arguments = null)
        {
            if (http.Response.HasStarted || http.RequestAborted.IsCancellationRequested)
            {
                http.Abort();
                return;
            }

            PreparedResponse response;
            try
            {
                var rawTarget = http.Features.Get<IHttpRequestFeature>()?.RawTarget ??
                    http.Request.PathBase.Add(http.Request.Path).ToUriComponent();
                var queryAt = rawTarget.IndexOf('?');
                var requestPath = queryAt < 0 ? rawTarget : rawTarget[..queryAt];
                if (!requestPath.StartsWith('/'))
                {
                    requestPath = http.Request.PathBase.Add(http.Request.Path).ToUriComponent();
                }
                response = PreparedResponse.Problem(diagnostic, engine.PublicRoot, options.MaxErrorBytes, route, arguments,
                    http.Request.Method, requestPath);
            }
            catch (InvalidOperationException exception)
            {
                var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("XRegistry.AspNetCore");
                s_infrastructureFailure(logger, exception);
                response = PreparedResponse.ErrorContractFailure(engine.PublicRoot, options.MaxErrorBytes);
            }
            if (response.StatusCode == StatusCodes.Status401Unauthorized && options.AuthenticationChallenge is { } challenge)
            {
                http.Response.Headers.WWWAuthenticate = challenge;
            }

            await response.WriteAsync(http, HttpMethods.IsHead(http.Request.Method), http.RequestAborted).ConfigureAwait(false);
        }

        private static void LogFailure(HttpContext http, IOException exception)
        {
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("XRegistry.AspNetCore");
            s_infrastructureFailure(logger, exception);
        }
    }

    internal static string Method(RegistryAction action) => action switch
    {
        RegistryAction.Read => "GET",
        RegistryAction.Head => "HEAD",
        RegistryAction.Options => "OPTIONS",
        RegistryAction.Replace => "PUT",
        RegistryAction.Patch => "PATCH",
        RegistryAction.Post => "POST",
        RegistryAction.Delete => "DELETE",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static bool TryAction(string method, out RegistryAction action)
    {
        action = method switch
        {
            "GET" => RegistryAction.Read,
            "HEAD" => RegistryAction.Head,
            "OPTIONS" => RegistryAction.Options,
            "PUT" => RegistryAction.Replace,
            "PATCH" => RegistryAction.Patch,
            "POST" => RegistryAction.Post,
            "DELETE" => RegistryAction.Delete,
            _ => RegistryAction.Options
        };
        return method is "GET" or "HEAD" or "OPTIONS" or "PUT" or "PATCH" or "POST" or "DELETE";
    }

    private static void CheckHeaders(IHeaderDictionary headers, int limit, string path)
    {
        long size = 0;
        foreach (var header in headers)
        {
            size += header.Key.Length + 4;
            foreach (var value in header.Value)
            {
                size += value?.Length ?? 0;
            }
        }

        if (size > limit)
        {
            throw Error("request_headers_too_large", path, "The request headers exceed their byte budget.");
        }
    }

    private static List<KeyValuePair<string, string?>> ParseQuery(string query, int limit, string path)
    {
        if (query.Length > limit)
        {
            throw Error("request_target_too_large", path, "The query exceeds its character budget.");
        }

        var result = new List<KeyValuePair<string, string?>>();
        foreach (var field in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = field.IndexOf('=');
            var rawName = equals < 0 ? field : field[..equals];
            try
            {
                result.Add(new(DecodeQuery(rawName), equals < 0 ? null : DecodeQuery(field[(equals + 1)..])));
            }
            catch (RegistryException exception)
            {
                throw Error("bad_flag", path, exception.Diagnostic.Message, ("flag", rawName));
            }
        }

        return result;
    }

    private static string DecodeQuery(string value)
    {
        var bytes = new byte[value.Length];
        var count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '%')
            {
                if (index + 2 >= value.Length || !char.IsAsciiHexDigit(value[index + 1]) || !char.IsAsciiHexDigit(value[index + 2]))
                {
                    throw Error("bad_flag", "", "The query contains an invalid percent escape.");
                }

                bytes[count++] = Convert.ToByte(value.Substring(index + 1, 2), 16);
                index += 2;
            }
            else if (value[index] <= 127)
            {
                bytes[count++] = (byte)(value[index] == '+' ? ' ' : value[index]);
            }
            else
            {
                throw Error("bad_flag", "", "Query characters must use UTF-8 percent encoding.");
            }
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes, 0, count);
        }
        catch (DecoderFallbackException exception)
        {
            throw new RegistryException(new("bad_flag", "", "The query contains invalid UTF-8."), exception);
        }
    }

    private static async ValueTask<byte[]> ReadBodyAsync(Stream body, int maximum, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(8192, maximum)];
        while (true)
        {
            var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximum - output.Length + 1)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (read > maximum - output.Length)
            {
                throw Error("request_too_large", "", "The metadata body exceeds its byte budget.");
            }

            output.Write(buffer, 0, read);
        }
    }

    internal static RegistryException Error(string code, string path, string message, params (string Name, string Value)[] arguments)
    {
        var exception = new RegistryException(new(code, path, message));
        if (arguments.Length != 0)
        {
            exception.Data["xregistry.args"] = arguments.ToDictionary(static pair => pair.Name, static pair => pair.Value, StringComparer.Ordinal);
        }

        return exception;
    }
}
