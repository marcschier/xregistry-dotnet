using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using XRegistry.Server;

namespace XRegistry.AspNetCore;

internal sealed class PreparedResponse(int statusCode, Dictionary<string, string> headers, byte[] body)
{
    internal int StatusCode => statusCode;

    internal static async ValueTask<PreparedResponse> CreateAsync(RegistryResult result, RegistryEngine engine, CancellationToken cancellationToken)
    {
        var headers = RootHeaders(engine.PublicRoot);
        var status = result.Kind switch
        {
            RegistryResultKind.Success => StatusCodes.Status200OK,
            RegistryResultKind.Created => StatusCodes.Status201Created,
            RegistryResultKind.NoContent => StatusCodes.Status204NoContent,
            RegistryResultKind.SeeOther => StatusCodes.Status303SeeOther,
            _ => throw new InvalidOperationException("The engine returned an unknown result kind.")
        };
        byte[] body = [];
        if (result.IsDocument)
        {
            HeaderMetadata.Encode(result, headers, engine.Limits.MaxHeaderBytes);
            if (result.Document is { } document)
            {
                if (document.Length > engine.Limits.MaxResponseBytes)
                {
                    throw RegistryEndpointRouteBuilderExtensions.Error("too_large", result.Path.EscapedPath, "The response Document exceeds its byte budget.");
                }

                body = new byte[document.Length];
                using var input = document.OpenRead();
                await input.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
                headers["Content-Disposition"] = result.Path.ResourceId!.Value;
            }
        }
        else if (result.Metadata is { } metadata)
        {
            headers["Content-Type"] = "application/json; charset=utf-8";
            body = Encode(metadata.RootElement.GetRawText(), engine.Limits.MaxResponseBytes);
        }

        if (result.Location is not null)
        {
            headers["Location"] = result.Location.AbsoluteUri;
        }

        if (result.ContentLocation is not null)
        {
            headers["Content-Location"] = result.ContentLocation.AbsoluteUri;
        }

        if (result.CorrelationId is not null)
        {
            headers["xRegistry-xregcorrelationid"] = result.CorrelationId;
        }

        if (result.Page is { } page)
        {
            var count = page.TotalCount.ToString(CultureInfo.InvariantCulture);
            headers["xRegistry-count"] = count;
            headers["Cache-Control"] = "private, no-store";
            foreach (var link in page.Links)
            {
                headers["Link"] += ", <" + link.Target.AbsoluteUri + ">;rel=" + link.Relation + ";count=" + count;
            }

            if (page.ExpiresAt is { } expires)
            {
                headers["Expires"] = expires.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);
            }
        }

        if (result.AllowedActions.Count != 0)
        {
            var allow = string.Join(", ", result.AllowedActions.Select(RegistryEndpointRouteBuilderExtensions.Method));
            headers["Allow"] = allow;
            headers["Access-Control-Allow-Methods"] = allow;
        }

        ValidateHeaders(headers, engine.Limits.MaxHeaderBytes);
        cancellationToken.ThrowIfCancellationRequested();
        return new(status, headers, body);
    }

    internal static PreparedResponse Problem(RegistryDiagnostic diagnostic, Uri root, int maximum, RegistryRouteDescription? route,
        IReadOnlyDictionary<string, string>? arguments, string method, string requestPath)
    {
        var code = diagnostic.Code switch
        {
            "invalid_json" or "invalid_utf8" or "invalid_unicode" or "duplicate_member" => "parsing_data",
            "byte_limit" or "node_limit" or "depth_limit" or "number_limit" or "event_limit" => "too_large",
            "malformed_path" => "bad_request",
            _ => diagnostic.Code
        };
        if (!ProblemDisclosure.ValidCode(code))
        {
            code = "server_error";
        }
        var definition = ProblemCatalog.Find(code);
        var status = definition?.Status ?? (code switch
        {
            "not_found" or "api_not_found" => StatusCodes.Status404NotFound,
            "cursor_expired" => StatusCodes.Status410Gone,
            "action_not_supported" or "details_required" => StatusCodes.Status405MethodNotAllowed,
            "unauthorized" => StatusCodes.Status401Unauthorized,
            "forbidden" => StatusCodes.Status403Forbidden,
            "request_timeout" => StatusCodes.Status408RequestTimeout,
            "request_too_large" => StatusCodes.Status413PayloadTooLarge,
            "request_headers_too_large" => StatusCodes.Status431RequestHeaderFieldsTooLarge,
            "request_target_too_large" => StatusCodes.Status414UriTooLong,
            "operation_limit" => StatusCodes.Status422UnprocessableEntity,
            "server_busy" or "commit_outcome_unknown" => StatusCodes.Status503ServiceUnavailable,
            "server_error" => StatusCodes.Status500InternalServerError,
            "query_source_incomplete" or "invalid_query_source" or "error_contract" => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status400BadRequest
        });
        var type = definition?.Type(code) ?? "urn:xregistry-dotnet:problem:" + code;
        var subject = string.IsNullOrEmpty(diagnostic.Path) ? route?.Path.EscapedPath ?? requestPath : diagnostic.Path;
        if (definition?.UsesRequestPath == true)
        {
            subject = requestPath;
        }
        if (code.StartsWith("capability_", StringComparison.Ordinal))
        {
            subject = "/capabilities";
        }

        if (code.StartsWith("model_", StringComparison.Ordinal))
        {
            subject = "/model";
        }
        if (definition is null && !KnownExtension(code))
        {
            subject = requestPath;
        }
        subject = ProblemDisclosure.Subject(code, subject, requestPath, root);

        var substitutions = arguments is null ? new Dictionary<string, string>(StringComparer.Ordinal) :
            new Dictionary<string, string>(arguments, StringComparer.Ordinal);
        if (code == "action_not_supported")
        {
            substitutions["action"] = method;
        }

        var required = definition is null ? [] : ProblemCatalog.ArgumentNames(definition);
        if (required.Contains("error_detail", StringComparer.Ordinal))
        {
            substitutions.TryAdd("error_detail", ProblemDisclosure.Detail(diagnostic.Message));
        }

        var used = substitutions.Where(pair => required.Contains(pair.Key, StringComparer.Ordinal))
            .ToDictionary(static pair => pair.Key, static pair => pair.Key == "error_detail" ?
                ProblemDisclosure.Detail(pair.Value) : ProblemDisclosure.Argument(pair.Value), StringComparer.Ordinal);
        var detail = ProblemDisclosure.Detail(diagnostic.Message);
        var title = definition is null ? ExtensionTitle(code, subject) : ProblemCatalog.Format(definition, subject, used);
        var headers = RootHeaders(root);
        headers["Content-Type"] = definition is null ? "application/problem+json; charset=utf-8" : "application/json; charset=utf-8";
        if (status == StatusCodes.Status405MethodNotAllowed && route is not null)
        {
            headers["Allow"] = string.Join(", ", route.AllowedActions.Select(RegistryEndpointRouteBuilderExtensions.Method));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteString("title", title);
            writer.WriteNumber("status", status);
            if (definition is not null || KnownExtension(code))
            {
                writer.WriteString("detail", detail + ".");
            }
            writer.WriteString("subject", subject);
            writer.WriteString("source", root.AbsoluteUri.TrimEnd('/'));
            writer.WriteString("code", code);
            if (route is not null)
            {
                writer.WriteString("instance", root.AbsoluteUri.TrimEnd('/') + (route.Path.EscapedPath == "/" ? "" : route.Path.EscapedPath));
            }

            if (used.Count != 0)
            {
                writer.WriteStartObject("args");
                foreach (var argument in used)
                {
                    writer.WriteString(argument.Key, argument.Value);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        if (stream.Length > maximum)
        {
            throw new InvalidOperationException("The configured error budget cannot represent this diagnostic.");
        }

        return new(status, headers, stream.ToArray());
    }

    internal static PreparedResponse ErrorContractFailure(Uri root, int maximum)
    {
        var headers = RootHeaders(root);
        headers["Content-Type"] = "application/problem+json; charset=utf-8";
        var body = Encode("""
            {"type":"urn:xregistry-dotnet:problem:error_contract","title":"The registry could not safely represent the error response.","status":500,"code":"error_contract"}
            """, maximum);
        return new(StatusCodes.Status500InternalServerError, headers, body);
    }

    private static bool KnownExtension(string code) => code is "unauthorized" or "forbidden" or "request_timeout" or
        "request_too_large" or "request_headers_too_large" or "request_target_too_large" or "operation_limit" or
        "bad_cursor" or "cursor_expired" or "commit_outcome_unknown" or
        "obligation_policy_required" or "document_reference_policy_required" or "invalid_query_source" or "query_source_incomplete";

    private static string ExtensionTitle(string code, string subject) => code switch
    {
        "unauthorized" => "Authentication is required for: " + subject + ".",
        "forbidden" => "The caller is not authorized for: " + subject + ".",
        "request_timeout" => "The request exceeded its permitted lifetime.",
        "request_too_large" => "The request body exceeds its permitted size.",
        "request_headers_too_large" => "The request headers exceed their permitted size.",
        "request_target_too_large" => "The request target exceeds its permitted size.",
        "operation_limit" => "The operation exceeds the host's finite processing or persistence budget.",
        "bad_cursor" => "The requested page cursor is not valid for this request.",
        "cursor_expired" => "The requested page cursor has expired.",
        "commit_outcome_unknown" => "The commit outcome is unknown; do not automatically retry this mutation.",
        "obligation_policy_required" => "The metadata requires a host validation policy.",
        "document_reference_policy_required" => "External Document references require a host authorization policy.",
        "invalid_query_source" or "query_source_incomplete" => "The query source did not provide a valid complete view.",
        _ => "The host rejected the request."
    };

    internal async Task WriteAsync(HttpContext context, bool head, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        foreach (var header in headers)
        {
            context.Response.Headers[header.Key] = header.Value;
        }

        if (statusCode != StatusCodes.Status204NoContent)
        {
            context.Response.ContentLength = body.Length;
        }

        if (!head && body.Length != 0)
        {
            await context.Response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, string> RootHeaders(Uri root) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Link"] = "<" + root.AbsoluteUri.TrimEnd('/') + ">;rel=xregistry-root"
    };

    private static byte[] Encode(string json, int maximum)
    {
        if (Encoding.UTF8.GetByteCount(json) > maximum)
        {
            throw RegistryEndpointRouteBuilderExtensions.Error("too_large", "", "The response metadata exceeds its byte budget.");
        }

        return Encoding.UTF8.GetBytes(json);
    }

    private static void ValidateHeaders(Dictionary<string, string> headers, int maximum)
    {
        long count = 0;
        foreach (var header in headers)
        {
            count += header.Key.Length + header.Value.Length + 4;
            if (header.Value.Any(static character => character is < ' ' or > '~'))
            {
                throw RegistryEndpointRouteBuilderExtensions.Error("header_error", "", "A response header contains unrepresentable characters.",
                    ("name", header.Key));
            }
        }

        if (count > maximum)
        {
            throw RegistryEndpointRouteBuilderExtensions.Error("too_large", "", "The prepared response headers exceed their byte budget.");
        }
    }
}
