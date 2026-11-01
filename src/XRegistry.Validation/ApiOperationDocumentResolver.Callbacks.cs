// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace XRegistry.Validation;

public static partial class ApiOperationDocumentResolver
{
    private static async ValueTask<ApiOperationResolutionResult?> CollectOperationMatchesAsync(
        OperationLocation location, string operationId, bool includeCallbacks, List<OperationLocation> matches,
        Dictionary<string, int> operationCounts, ReferenceContext references, ValidationContext budget,
        HashSet<string> active, int depth,
        CancellationToken cancellationToken)
    {
        budget.Work(path: location.Pointer);
        if (depth > Capabilities.MaxReferenceDepth)
        {
            return Failure(ApiOperationResolutionStatus.Indeterminate, "limit.callback_depth",
                "The callback operation traversal depth limit was exceeded.", location.Pointer);
        }
        var identity = location.Source.Identity + "#" + location.Pointer;
        if (!active.Add(identity))
        {
            return Failure(ApiOperationResolutionStatus.Indeterminate, "dependency.cycle",
                "A callback operation cycle prevents operationId selection.", location.Pointer);
        }
        var metadataFailure = ValidateHttpOperationMetadata(location.Operation, location.Pointer, cancellationToken);
        if (metadataFailure is not null) { return metadataFailure; }
        if (!includeCallbacks)
        {
            var transportFailure = ValidateSwaggerTransportDeclarations(location.Operation, location.Pointer, cancellationToken);
            if (transportFailure is not null) { return transportFailure; }
        }
        if (location.Operation.TryGetProperty("operationId", out var id))
        {
            var nativeId = id.GetString()!;
            operationCounts[nativeId] = Math.Min(2, operationCounts.GetValueOrDefault(nativeId) + 1);
            if (operationCounts[nativeId] > 1 && !string.Equals(nativeId, operationId, StringComparison.Ordinal))
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.operation_id_duplicate",
                    "Operation IDs must be unique among the API's operation occurrences.",
                    location.Pointer + "/operationId");
            }
            if (string.Equals(id.GetString(), operationId, StringComparison.Ordinal) && matches.Count < 2)
            {
                matches.Add(location);
            }
        }
        if (includeCallbacks && location.Operation.TryGetProperty("callbacks", out var callbacks))
        {
            if (callbacks.ValueKind != JsonValueKind.Object)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.callbacks",
                    "Operation callbacks must be an object.", location.Pointer + "/callbacks");
            }
            foreach (var callback in callbacks.EnumerateObject())
            {
                budget.Work(path: location.Pointer);
                var failure = await CollectCallbackMatchesAsync(callback.Value, location.Source,
                    location.Pointer + "/callbacks/" + Escape(callback.Name), operationId, matches, operationCounts,
                    references, budget, active, depth + 1, cancellationToken).ConfigureAwait(false);
                if (failure is not null) { return failure; }
            }
        }
        active.Remove(identity);
        return null;
    }

    private static ApiOperationResolutionResult? ValidateHttpOperationMetadata(
        JsonElement operation, string pointer, CancellationToken cancellationToken)
    {
        foreach (var field in new[] { "operationId", "summary", "description", "deprecated", "tags" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!operation.TryGetProperty(field, out var value)) { continue; }
            var expected = field switch
            {
                "deprecated" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "tags" => value.ValueKind == JsonValueKind.Array,
                _ => value.ValueKind == JsonValueKind.String
            };
            if (!expected)
            {
                return Failure(ApiOperationResolutionStatus.Invalid,
                    field == "operationId" ? "document.operation_id" : "document.operation_metadata",
                    $"An HTTP operation {field} must have its declared native JSON type.", pointer + "/" + field);
            }
            if (field != "tags") { continue; }
            var index = 0;
            foreach (var tag in value.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (tag.ValueKind != JsonValueKind.String)
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.operation_metadata",
                        "An HTTP operation tag must be a string.", pointer + "/tags/" +
                        index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                index++;
            }
        }
        return ValidateHttpDocumentationShapes(operation, pointer);
    }

    private static ApiOperationResolutionResult? ValidateHttpDocumentationShapes(JsonElement value, string pointer)
    {
        if (!value.TryGetProperty("externalDocs", out var documentation)) { return null; }
        var path = pointer + "/externalDocs";
        if (documentation.ValueKind != JsonValueKind.Object)
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.external_docs",
                "HTTP externalDocs must be an External Documentation Object.", path);
        }
        if (!documentation.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.external_docs_url",
                "An External Documentation Object requires a string url.", path + "/url");
        }
        if (documentation.TryGetProperty("description", out var description) &&
            description.ValueKind != JsonValueKind.String)
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.external_docs_description",
                "An External Documentation Object description must be a string.", path + "/description");
        }
        return null;
    }

    private static ApiOperationResolutionResult? ValidateSwaggerTransportDeclarations(
        JsonElement value, string pointer, CancellationToken cancellationToken)
    {
        foreach (var field in new[] { "schemes", "consumes", "produces" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!value.TryGetProperty(field, out var declarations)) { continue; }
            var path = pointer + "/" + field;
            if (declarations.ValueKind != JsonValueKind.Array)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.swagger_transport",
                    $"Swagger {field} must be an array of strings.", path);
            }
            var index = 0;
            foreach (var declaration in declarations.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (declaration.ValueKind != JsonValueKind.String ||
                    field == "schemes" && declaration.GetString() is not ("http" or "https" or "ws" or "wss"))
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.swagger_transport",
                        "Swagger transport declarations must be strings; schemes must be http, https, ws or wss.",
                        path + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                if (field != "schemes" && !IsDeclaredMediaType(declaration.GetString()!))
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.media_type",
                        "Swagger media declarations require RFC 6838 type/subtype names and valid parameters.",
                        path + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                index++;
            }
        }
        return null;
    }

    private static ApiOperationResolutionResult? ValidateHttpRootTags(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("tags", out var tags)) { return null; }
        if (tags.ValueKind != JsonValueKind.Array)
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.tags",
                "Root HTTP tags must be an array of Tag Objects.", "$/tags");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var tag in tags.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = "$/tags/" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (tag.ValueKind != JsonValueKind.Object)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.tag",
                    "A root tag declaration must be a Tag Object.", path);
            }
            if (!tag.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.tag_name",
                    "A Tag Object requires a string name.", path + "/name");
            }
            if (tag.TryGetProperty("description", out var description) && description.ValueKind != JsonValueKind.String)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.tag_description",
                    "A Tag Object description must be a string.", path + "/description");
            }
            var documentationFailure = ValidateHttpDocumentationShapes(tag, path);
            if (documentationFailure is not null) { return documentationFailure; }
            if (!names.Add(name.GetString()!))
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.tag_duplicate",
                    "Root HTTP tag names must be unique.", path + "/name");
            }
        }
        return null;
    }

    private static async ValueTask<ApiOperationResolutionResult?> CollectCallbackMatchesAsync(
        JsonElement callback, ReferencedDocument source, string pointer, string operationId,
        List<OperationLocation> matches, Dictionary<string, int> operationCounts,
        ReferenceContext references, ValidationContext budget,
        HashSet<string> active, int depth, CancellationToken cancellationToken)
    {
        var referencePath = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            budget.Work(path: pointer);
            if (callback.ValueKind != JsonValueKind.Object)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.callback",
                    "A Callback Object or reference target must be an object.", pointer);
            }
            if (!callback.TryGetProperty("$ref", out var reference)) { break; }
            if (reference.ValueKind != JsonValueKind.String)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "dependency.callback",
                    "A Callback reference must be a string.", pointer + "/$ref");
            }
            if (referencePath.Count >= Capabilities.MaxReferenceDepth)
            {
                return Failure(ApiOperationResolutionStatus.Indeterminate, "limit.reference_depth",
                    "The Callback reference-chain depth limit was exceeded.", pointer + "/$ref");
            }
            var target = await references.ResolveAsync(source, reference.GetString()!, pointer + "/$ref",
                cancellationToken).ConfigureAwait(false);
            if (target.Failure is not null) { return target.Failure; }
            var resolved = target.Target!;
            if (!referencePath.Add(resolved.Document.Identity + "#" + resolved.Pointer))
            {
                return Failure(ApiOperationResolutionStatus.Indeterminate, "dependency.cycle",
                    "A Callback reference cycle prevents operationId selection.", pointer + "/$ref");
            }
            callback = resolved.Value;
            source = resolved.Document;
            pointer = resolved.Pointer;
        }
        foreach (var expression in callback.EnumerateObject())
        {
            budget.Work(path: pointer);
            if (expression.Name.StartsWith("x-", StringComparison.Ordinal)) { continue; }
            if (!IsCallbackExpression(expression.Name))
            {
                return InvalidCallbackExpression(pointer + "/" + Escape(expression.Name));
            }
            var pathItem = await ResolvePathItemAsync(expression.Value, source, pointer + "/" + Escape(expression.Name),
                references, new(StringComparer.Ordinal), true, cancellationToken).ConfigureAwait(false);
            if (pathItem.Failure is not null) { return pathItem.Failure; }
            foreach (var operation in pathItem.Operations.Values)
            {
                var failure = await CollectOperationMatchesAsync(operation, operationId, true, matches, operationCounts,
                    references, budget, active, depth, cancellationToken).ConfigureAwait(false);
                if (failure is not null) { return failure; }
            }
        }
        return null;
    }

    private static ApiOperationResolutionResult InvalidCallbackExpression(string path) =>
        Failure(ApiOperationResolutionStatus.Invalid, "document.callback_expression",
            "The callback key contains a malformed OpenAPI runtime expression.", path);

    private static bool IsCallbackExpression(string value)
    {
        if (value.StartsWith('$')) { return IsRuntimeExpression(value.AsSpan()); }
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '}') { return false; }
            if (value[index] != '{') { continue; }
            var end = value.IndexOf('}', index + 1);
            if (end < 0 || !IsRuntimeExpression(value.AsSpan(index + 1, end - index - 1)))
            {
                return false;
            }
            index = end;
        }
        return true;
    }

    private static bool IsRuntimeExpression(ReadOnlySpan<char> expression)
    {
        if (expression is "$url" or "$method" or "$statusCode") { return true; }
        if (expression.StartsWith("$request.", StringComparison.Ordinal))
        {
            expression = expression[9..];
        }
        else if (expression.StartsWith("$response.", StringComparison.Ordinal))
        {
            expression = expression[10..];
        }
        else { return false; }

        if (expression.StartsWith("header.", StringComparison.Ordinal))
        {
            var token = expression[7..];
            if (token.IsEmpty) { return false; }
            foreach (var character in token)
            {
                if (!char.IsAsciiLetterOrDigit(character) &&
                    !"!#$%&'*+-.^_`|~".Contains(character))
                {
                    return false;
                }
            }
            return true;
        }
        if (expression.StartsWith("query.", StringComparison.Ordinal) ||
            expression.StartsWith("path.", StringComparison.Ordinal))
        {
            // JSON decoding has already checked the RFC 7159 CHAR production.
            return true;
        }
        if (expression.SequenceEqual("body")) { return true; }
        if (!expression.StartsWith("body#", StringComparison.Ordinal)) { return false; }
        var pointer = expression[5..];
        if (!pointer.IsEmpty && pointer[0] != '/') { return false; }
        for (var index = 0; index < pointer.Length; index++)
        {
            if (pointer[index] == '~' && (++index >= pointer.Length || pointer[index] is not ('0' or '1')))
            {
                return false;
            }
        }
        return true;
    }
}
