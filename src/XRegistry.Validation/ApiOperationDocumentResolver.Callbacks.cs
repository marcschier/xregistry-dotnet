// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.RegularExpressions;

namespace XRegistry.Validation;

public static partial class ApiOperationDocumentResolver
{
    private static ApiOperationResolutionResult? ValidateOpenApiPathItemMetadata(
        JsonElement pathItem, string pointer, string format, CancellationToken cancellationToken)
    {
        if (format == "Swagger/2.0") { return null; }
        foreach (var field in new[] { "summary", "description", "$ref" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!pathItem.TryGetProperty(field, out var value)) { continue; }
            if (value.ValueKind != JsonValueKind.String)
            {
                return Failure(ApiOperationResolutionStatus.Invalid,
                    field == "$ref" ? "dependency.path_item" : "document.path_item",
                    "Path Item metadata requires strings.", pointer + "/" + field);
            }
            if (field == "$ref" && !IsHttpMetadataUriReference(value.GetString()!))
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "dependency.uri",
                    "A Path Item $ref requires a URI reference.", pointer + "/$ref");
            }
        }
        return null;
    }

    private static async ValueTask<ApiOperationResolutionResult?> CollectOperationMatchesAsync(
        OperationLocation location, string? operationId, bool includeCallbacks, List<OperationLocation> matches,
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
        var metadataFailure = ValidateHttpOperationMetadata(location.Operation, location.Pointer, references.Format,
            location.HttpMethod, cancellationToken);
        if (metadataFailure is not null) { return metadataFailure; }
        var responseFailure = await ValidateEffectiveHttpDeclarationsAsync(location.Operation, location.Source,
            location.Pointer, location.HttpMethod, references, cancellationToken).ConfigureAwait(false);
        if (responseFailure is not null) { return responseFailure; }
        if (location.PathTemplate is not null)
        {
            var pathFailure = await ValidateOpenApiPathParameterCorrespondenceAsync(location.PathTemplate,
                location.PathParameterOwner, new ReferenceTarget(location.Source, location.Operation, location.Pointer),
                references, cancellationToken).ConfigureAwait(false);
            if (pathFailure is not null) { return pathFailure; }
        }
        var serverFailure = ValidateOpenApiServers(location.Operation, location.Pointer, references.Format, cancellationToken);
        if (serverFailure is not null) { return serverFailure; }
        var nameFailure = ValidateHttpSecurityNames(location.Operation, location.Pointer,
            references.Root.Root, !includeCallbacks, cancellationToken);
        if (nameFailure is not null) { return nameFailure; }
        var securityFailure = await ValidateEffectiveHttpSecurityAsync(location.Operation, location.Pointer,
            references, cancellationToken).ConfigureAwait(false);
        if (securityFailure is not null) { return securityFailure; }
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

    private static ApiOperationResolutionResult? ValidateOpenApiServers(
        JsonElement value, string pointer, string format, CancellationToken cancellationToken)
    {
        if (format == "Swagger/2.0" || !value.TryGetProperty("servers", out var servers)) { return null; }
        var path = pointer + "/servers";
        if (servers.ValueKind != JsonValueKind.Array)
        {
            return InvalidOpenApiServer("OpenAPI servers must be an array.", path);
        }
        var index = 0;
        foreach (var server in servers.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var serverPath = path + "/" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var failure = ValidateOpenApiServer(server, serverPath, format, cancellationToken);
            if (failure is not null) { return failure; }
        }
        return null;
    }

    private static ApiOperationResolutionResult? ValidateOpenApiServer(
        JsonElement server, string serverPath, string format, CancellationToken cancellationToken)
    {
        if (server.ValueKind != JsonValueKind.Object)
        {
            return InvalidOpenApiServer("A server declaration must be a Server Object.", serverPath);
        }
        if (!server.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
        {
            return InvalidOpenApiServer("A Server Object requires a string url.", serverPath + "/url");
        }
        if (server.TryGetProperty("description", out var description) && description.ValueKind != JsonValueKind.String)
        {
            return InvalidOpenApiServer("A server description must be a string.", serverPath + "/description");
        }
        if (!server.TryGetProperty("variables", out var variables))
        {
            return ValidateOpenApiServerUrl(url.GetString()!, variables, serverPath + "/url", cancellationToken);
        }
        if (variables.ValueKind != JsonValueKind.Object)
        {
            return InvalidOpenApiServer("Server variables must be a map of Server Variable Objects.", serverPath + "/variables");
        }
        foreach (var variable in variables.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var variablePath = serverPath + "/variables/" + Escape(variable.Name);
            if (variable.Value.ValueKind != JsonValueKind.Object)
            {
                return InvalidOpenApiServer("A server variable must be an object.", variablePath);
            }
            if (!variable.Value.TryGetProperty("default", out var defaultValue) || defaultValue.ValueKind != JsonValueKind.String)
            {
                return InvalidOpenApiServer("A server variable requires a string default.", variablePath + "/default");
            }
            if (variable.Value.TryGetProperty("description", out var variableDescription) &&
                variableDescription.ValueKind != JsonValueKind.String)
            {
                return InvalidOpenApiServer("A server variable description must be a string.", variablePath + "/description");
            }
            if (!variable.Value.TryGetProperty("enum", out var enumeration)) { continue; }
            if (enumeration.ValueKind != JsonValueKind.Array ||
                format == "OpenAPI/3.1" && enumeration.GetArrayLength() == 0)
            {
                return InvalidOpenApiServer("A server variable enum must be a string array, nonempty in OpenAPI 3.1.",
                    variablePath + "/enum");
            }
            var defaultFound = false;
            var enumIndex = 0;
            foreach (var member in enumeration.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (member.ValueKind != JsonValueKind.String)
                {
                    return InvalidOpenApiServer("Server variable enum members must be strings.",
                        variablePath + "/enum/" + enumIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                defaultFound |= string.Equals(member.GetString(), defaultValue.GetString(), StringComparison.Ordinal);
                enumIndex++;
            }
            if (format == "OpenAPI/3.1" && !defaultFound)
            {
                return InvalidOpenApiServer("The server variable default must occur in its enum.", variablePath + "/default");
            }
        }
        return ValidateOpenApiServerUrl(url.GetString()!, variables, serverPath + "/url", cancellationToken);
    }

    private static ApiOperationResolutionResult? ValidateOpenApiServerUrl(
        string template, JsonElement variables, string pointer, CancellationToken cancellationToken)
    {
        const int maxExpandedCharacters = 32768;
        var expanded = new System.Text.StringBuilder();
        var index = 0;
        while (index < template.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var marker = template.AsSpan(index).IndexOfAny('{', '}');
            var literalLength = marker < 0 ? template.Length - index : marker;
            if (literalLength > maxExpandedCharacters - expanded.Length) { return ExpansionLimit(); }
            expanded.Append(template.AsSpan(index, literalLength));
            index += literalLength;
            if (marker < 0) { break; }
            if (template[index] == '}') { return InvalidTemplate(); }
            var end = template.IndexOf('}', index + 1);
            if (end < 0 || template.AsSpan(index + 1, end - index - 1).Contains('{'))
            {
                return InvalidTemplate();
            }
            var name = template[(index + 1)..end];
            if (variables.ValueKind != JsonValueKind.Object || !variables.TryGetProperty(name, out var variable))
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.server_url",
                    "Every server URL template variable must have a declared default.", pointer);
            }
            var replacement = variable.GetProperty("default").GetString()!;
            if (replacement.Length > maxExpandedCharacters - expanded.Length) { return ExpansionLimit(); }
            expanded.Append(replacement);
            index = end + 1;
        }
        return IsHttpMetadataUriReference(expanded.ToString()) ? null :
            Failure(ApiOperationResolutionStatus.Invalid, "document.server_url",
                "The server URL with its declared defaults must be a valid URI reference.", pointer);

        ApiOperationResolutionResult InvalidTemplate() =>
            Failure(ApiOperationResolutionStatus.Invalid, "document.server_url",
                "Server URL template variables must use balanced, non-nested braces.", pointer);

        ApiOperationResolutionResult ExpansionLimit() =>
            Failure(ApiOperationResolutionStatus.Indeterminate, "limit.server_url",
                "The server URL default expansion exceeds the 32768-character limit.", pointer);
    }

    private static ApiOperationResolutionResult InvalidOpenApiServer(string message, string pointer) =>
        Failure(ApiOperationResolutionStatus.Invalid, "document.servers", message, pointer);

    private static ApiOperationResolutionResult? ValidateHttpInfoDeclaration(
        JsonElement root, string format, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("info", out var info)) { return null; }
        if (info.ValueKind != JsonValueKind.Object)
        {
            return InvalidInfo("HTTP info must be an Info Object.", "$/info");
        }
        foreach (var field in new[] { "title", "version", "description", "termsOfService", "summary" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (field == "summary" && format != "OpenAPI/3.1") { continue; }
            if (!info.TryGetProperty(field, out var value))
            {
                if (field is "title" or "version")
                {
                    return InvalidInfo("An Info Object requires string title and version fields.", "$/info/" + field);
                }
                continue;
            }
            if (value.ValueKind != JsonValueKind.String)
            {
                return InvalidInfo("An Info Object metadata field must be a string.", "$/info/" + field);
            }
            if (field == "termsOfService" && format != "Swagger/2.0" &&
                !IsHttpMetadataUriReference(value.GetString()!))
            {
                return InvalidInfo("OpenAPI termsOfService must be a valid URI reference.", "$/info/" + field);
            }
        }
        foreach (var name in new[] { "contact", "license" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!info.TryGetProperty(name, out var declaration)) { continue; }
            var path = "$/info/" + name;
            if (declaration.ValueKind != JsonValueKind.Object)
            {
                return InvalidInfo("Info contact and license declarations must be objects.", path);
            }
            foreach (var field in new[] { "name", "url", "email", "identifier" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (field == "email" && name != "contact" ||
                    field == "identifier" && (name != "license" || format != "OpenAPI/3.1"))
                {
                    continue;
                }
                if (!declaration.TryGetProperty(field, out var value))
                {
                    if (name == "license" && field == "name")
                    {
                        return InvalidInfo("A License Object requires a string name.", path + "/name");
                    }
                    continue;
                }
                if (value.ValueKind != JsonValueKind.String)
                {
                    return InvalidInfo("A Contact or License Object metadata field must be a string.", path + "/" + field);
                }
                if (field == "url" && !IsHttpMetadataUriReference(value.GetString()!))
                {
                    return InvalidInfo("A Contact or License Object url must be a valid URI reference.", path + "/url");
                }
            }
            if (name == "license" && format == "OpenAPI/3.1" &&
                declaration.TryGetProperty("identifier", out _) && declaration.TryGetProperty("url", out _))
            {
                return InvalidInfo("OpenAPI 3.1 license identifier and url are mutually exclusive.", path + "/identifier");
            }
        }
        return null;

        static ApiOperationResolutionResult InvalidInfo(string message, string path) =>
            Failure(ApiOperationResolutionStatus.Invalid, "document.info", message, path);
    }

    private static ApiOperationResolutionResult? ValidateHttpOperationMetadata(
        JsonElement operation, string pointer, string format, string? httpMethod, CancellationToken cancellationToken)
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
        if (SupportsOpenApiRequestBody(format, httpMethod) && operation.TryGetProperty("requestBody", out var body))
        {
            var failure = ValidateOpenApiRequestBodyDeclaration(body, pointer + "/requestBody", format, cancellationToken);
            if (failure is not null) { return failure; }
        }
        var parameterFailure = ValidateOpenApiParameters(operation, pointer, format, cancellationToken);
        if (parameterFailure is not null) { return parameterFailure; }
        var responseFailure = ValidateHttpResponses(operation, pointer, format, cancellationToken);
        if (responseFailure is not null) { return responseFailure; }
        var securityFailure = ValidateHttpSecurityShapes(operation, pointer, cancellationToken);
        return securityFailure ?? ValidateHttpDocumentationShapes(operation, pointer);
    }

    private static ApiOperationResolutionResult? ValidateHttpResponses(
        JsonElement operation, string pointer, string format, CancellationToken cancellationToken)
    {
        if (!operation.TryGetProperty("responses", out var responses)) { return null; }
        var path = pointer + "/responses";
        if (responses.ValueKind != JsonValueKind.Object)
        {
            return InvalidHttpResponse("Operation responses must be a Responses Object.", path);
        }
        var hasResponse = false;
        foreach (var response in responses.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (response.Name.StartsWith("x-", StringComparison.Ordinal)) { continue; }
            var key = response.Name;
            var validCode = key.Length == 3 && key[0] is >= '1' and <= '5' &&
                (char.IsAsciiDigit(key[1]) && char.IsAsciiDigit(key[2]) ||
                    format != "Swagger/2.0" && key[1] == 'X' && key[2] == 'X');
            var responsePath = path + "/" + Escape(key);
            if (key != "default" && !validCode)
            {
                return InvalidHttpResponse("A response key must be default, an HTTP status code or an allowed OpenAPI status range.", responsePath);
            }
            hasResponse = true;
            var failure = ValidateHttpResponseDeclaration(response.Value, responsePath, format,
                allowReference: true, cancellationToken);
            if (failure is not null) { return failure; }
        }
        return hasResponse ? null : InvalidHttpResponse("A Responses Object must contain at least one response.", path);
    }

    private static ApiOperationResolutionResult? ValidateHttpResponseDeclaration(
        JsonElement response, string pointer, string format, bool allowReference, CancellationToken cancellationToken)
    {
        if (response.ValueKind != JsonValueKind.Object)
        {
            return InvalidHttpResponse("A response declaration must be a Response or Reference Object.", pointer);
        }
        if (allowReference && response.TryGetProperty("$ref", out var reference))
        {
            if (reference.ValueKind != JsonValueKind.String || !IsHttpMetadataUriReference(reference.GetString()!))
            {
                return InvalidHttpResponse("A response reference requires a string URI reference.", pointer + "/$ref");
            }
            if (format != "OpenAPI/3.1") { return null; }
            foreach (var field in new[] { "summary", "description" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (response.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.String)
                {
                    return InvalidHttpResponse("OpenAPI 3.1 Reference Object metadata must be strings.", pointer + "/" + field);
                }
            }
            return null;
        }
        if (!response.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String)
        {
            return InvalidHttpResponse("A Response Object requires a string description.", pointer + "/description");
        }
        foreach (var field in format == "Swagger/2.0"
            ? new[] { "headers", "schema", "examples" } : new[] { "headers", "content", "links" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (response.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Object)
            {
                return InvalidHttpResponse("A Response Object structured field must be an object.", pointer + "/" + field);
            }
        }
        if (response.TryGetProperty("headers", out var headers))
        {
            foreach (var header in headers.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (format != "Swagger/2.0" && header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { continue; }
                var headerPath = pointer + "/headers/" + Escape(header.Name);
                var failure = format == "Swagger/2.0"
                    ? ValidateSwaggerScalarDeclaration(header.Value, headerPath, "Header", false, false, cancellationToken)
                    : ValidateOpenApiHeaderDeclaration(header.Value, headerPath, format, cancellationToken);
                if (failure is not null) { return failure; }
            }
        }
        if (format == "Swagger/2.0" && response.TryGetProperty("examples", out var examples))
        {
            foreach (var example in examples.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsDeclaredMediaType(example.Name))
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.response_example",
                        "A Swagger response example key must be a concrete media type.",
                        pointer + "/examples/" + Escape(example.Name));
                }
            }
        }
        if (format != "Swagger/2.0" && response.TryGetProperty("links", out var links))
        {
            foreach (var link in links.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = pointer + "/links/" + Escape(link.Name);
                if (!IsOpenApiComponentName(link.Name))
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.link",
                        "A response link name must contain only ASCII letters, digits, dots, hyphens or underscores.", path);
                }
                var failure = ValidateOpenApiLinkDeclaration(link.Value, path, format, cancellationToken);
                if (failure is not null) { return failure; }
            }
        }
        if (format != "Swagger/2.0" && response.TryGetProperty("content", out var content))
        {
            return ValidateOpenApiMediaDeclarations(content, pointer + "/content", format, cancellationToken);
        }
        return null;
    }

    private static ApiOperationResolutionResult? ValidateSwaggerScalarDeclaration(
        JsonElement header, string pointer, string kind, bool allowFile, bool allowMulti, CancellationToken cancellationToken)
    {
        var declaration = header;
        var path = pointer;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (declaration.ValueKind != JsonValueKind.Object)
            {
                return InvalidScalar("Swagger scalar declarations and their items must be objects.", path);
            }
            if (!declaration.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                !(type.GetString() is "string" or "number" or "integer" or "boolean" or "array" ||
                    allowFile && path == pointer && type.GetString() == "file"))
            {
                return InvalidScalar("Swagger scalar declarations require a native primitive, array or applicable file type.", path + "/type");
            }
            if (path == pointer && declaration.TryGetProperty("description", out var description) &&
                description.ValueKind != JsonValueKind.String)
            {
                return InvalidScalar("A Swagger scalar description must be a string.", path + "/description");
            }
            if (declaration.TryGetProperty("format", out var format) && format.ValueKind != JsonValueKind.String)
            {
                return InvalidScalar("A Swagger scalar format must be a string.", path + "/format");
            }
            if (declaration.TryGetProperty("collectionFormat", out var collection) &&
                (collection.ValueKind != JsonValueKind.String ||
                    !(collection.GetString() is "csv" or "ssv" or "tsv" or "pipes" ||
                        allowMulti && path == pointer && collection.GetString() == "multi")))
            {
                return InvalidScalar("Swagger collectionFormat must be native to the declaration's location.", path + "/collectionFormat");
            }
            foreach (var field in new[] { "minimum", "maximum", "multipleOf", "exclusiveMinimum", "exclusiveMaximum",
                "minLength", "maxLength", "minItems", "maxItems", "uniqueItems", "pattern", "enum" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!declaration.TryGetProperty(field, out var value)) { continue; }
                var valid = field switch
                {
                    "exclusiveMinimum" or "exclusiveMaximum" or "uniqueItems" =>
                        value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "pattern" => value.ValueKind == JsonValueKind.String,
                    "enum" => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0,
                    _ => value.ValueKind == JsonValueKind.Number
                };
                if (!valid)
                {
                    return InvalidScalar("A Swagger scalar constraint must have its native JSON type.", path + "/" + field);
                }
                if (value.ValueKind == JsonValueKind.Number)
                {
                    if (!JsonNumber.TryRead(value, out var number))
                    {
                        return Failure(ApiOperationResolutionStatus.Indeterminate, "limit.number",
                            "Numeric Swagger constraints are limited to 256 source characters.", path + "/" + field);
                    }
                    if (field == "multipleOf" && (number.Negative || number.IsZero))
                    {
                        return InvalidScalar("Swagger multipleOf requires a positive number.", path + "/" + field);
                    }
                    if (field is "minLength" or "maxLength" or "minItems" or "maxItems" &&
                        (number.Negative || value.GetRawText().AsSpan().IndexOfAny('.', 'e', 'E') >= 0))
                    {
                        return InvalidScalar("A Swagger length or item count requires a nonnegative Draft4 integer token without fraction or exponent.", path + "/" + field);
                    }
                }
                if (field is "exclusiveMinimum" or "exclusiveMaximum" &&
                    !declaration.TryGetProperty(field == "exclusiveMinimum" ? "minimum" : "maximum", out _))
                {
                    return InvalidScalar("An exclusive Swagger bound requires its corresponding numeric bound.", path + "/" + field);
                }
            }
            if (!declaration.TryGetProperty("items", out var items))
            {
                return type.GetString() == "array"
                    ? InvalidScalar("A Swagger array requires an Items Object.", path + "/items")
                    : ValidateSwaggerScalarDefaults(header, pointer, kind, cancellationToken);
            }
            declaration = items;
            path += "/items";
        }

        ApiOperationResolutionResult InvalidScalar(string message, string path) =>
            Failure(ApiOperationResolutionStatus.Invalid, kind == "Header" ? "document.header" : "document.parameter", message, path);
    }

    private static ApiOperationResolutionResult? ValidateSwaggerScalarDefaults(
        JsonElement declaration, string pointer, string kind, CancellationToken cancellationToken)
    {
        var defaults = new Stack<(JsonElement Schema, JsonElement Value, string Pointer)>();
        var schema = declaration;
        var schemaPath = pointer;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (schema.TryGetProperty("default", out var value)) { defaults.Push((schema, value, schemaPath + "/default")); }
            if (!schema.TryGetProperty("items", out schema)) { break; }
            schemaPath += "/items";
        }
        while (defaults.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = item.Schema.GetProperty("type").GetString();
            if (type == "file")
            {
                return Failure(ApiOperationResolutionStatus.Indeterminate, "document.file_default",
                    "Swagger does not define a JSON representation for a file-typed default.", item.Pointer);
            }
            var valid = type switch
            {
                "string" => item.Value.ValueKind == JsonValueKind.String,
                "boolean" => item.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "number" or "integer" => item.Value.ValueKind == JsonValueKind.Number,
                "array" => item.Value.ValueKind == JsonValueKind.Array,
                _ => false
            };
            if (valid && type == "integer")
            {
                if (!JsonNumber.TryRead(item.Value, out _))
                {
                    return Failure(ApiOperationResolutionStatus.Indeterminate, "limit.number",
                        "Integer Swagger default tokens are limited to 256 source characters.", item.Pointer);
                }
                valid = item.Value.GetRawText().AsSpan().IndexOfAny('.', 'e', 'E') < 0;
            }
            if (!valid)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, kind == "Header" ? "document.header" : "document.parameter",
                    "A Swagger default must conform to its declared type and nested item types.", item.Pointer);
            }
            if (type != "array") { continue; }
            var items = item.Schema.GetProperty("items");
            for (var index = item.Value.GetArrayLength() - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                defaults.Push((items, item.Value[index], item.Pointer + "/" +
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        return null;
    }

    private static ApiOperationResolutionResult? ValidateSwaggerResponseEnums(
        JsonElement response, string pointer, ValidationContext budget)
    {
        if (!response.TryGetProperty("headers", out var headers)) { return null; }
        foreach (var header in headers.EnumerateObject())
        {
            var failure = ValidateSwaggerScalarEnums(header.Value, pointer + "/headers/" + Escape(header.Name), "Header", budget);
            if (failure is not null) { return failure; }
        }
        return null;
    }

    private static ApiOperationResolutionResult? ValidateSwaggerScalarEnums(
        JsonElement declaration, string pointer, string kind, ValidationContext budget)
    {
        if (kind == "Parameter" && declaration.GetProperty("in").GetString() == "body") { return null; }
        while (true)
        {
            budget.CancellationToken.ThrowIfCancellationRequested();
            if (declaration.TryGetProperty("enum", out var values))
            {
                var path = pointer + "/enum";
                try
                {
                    SchemaJson.UniqueValues(values, budget, path);
                }
                catch (ValidationFailure failure)
                {
                    return Failure(failure.Status == DocumentValidationStatus.Indeterminate
                            ? ApiOperationResolutionStatus.Indeterminate : ApiOperationResolutionStatus.Invalid,
                        failure.Status == DocumentValidationStatus.Indeterminate ? failure.Diagnostic.Code
                            : kind == "Header" ? "document.header" : "document.parameter",
                        failure.Diagnostic.Detail, path);
                }
            }
            if (!declaration.TryGetProperty("items", out declaration)) { return null; }
            pointer += "/items";
        }
    }

    private static ApiOperationResolutionResult? ValidateOpenApiMediaDeclarations(
        JsonElement content, string pointer, string format, CancellationToken cancellationToken)
    {
        foreach (var media in content.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = pointer + "/" + Escape(media.Name);
            if (!IsResponseMediaRange(media.Name))
            {
                return InvalidMedia("A content key must be a media type or media range.", path);
            }
            var declaration = media.Value;
            if (declaration.ValueKind != JsonValueKind.Object)
            {
                return InvalidMedia("Content entries must be Media Type Objects.", path);
            }
            if (declaration.TryGetProperty("schema", out var schema) &&
                schema.ValueKind != JsonValueKind.Object &&
                !(format == "OpenAPI/3.1" && schema.ValueKind is JsonValueKind.True or JsonValueKind.False))
            {
                return InvalidMedia("A media schema must be a Schema Object or, in OpenAPI 3.1, a boolean.", path + "/schema");
            }
            foreach (var field in new[] { "examples", "encoding" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (declaration.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Object)
                {
                    return InvalidMedia("Media examples and encoding declarations must be objects.", path + "/" + field);
                }
            }
            if (declaration.TryGetProperty("example", out _) && declaration.TryGetProperty("examples", out _))
            {
                return InvalidMedia("Media example and examples are mutually exclusive.", path + "/examples");
            }
            if (declaration.TryGetProperty("examples", out var examples))
            {
                foreach (var example in examples.EnumerateObject())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var failure = ValidateOpenApiExampleDeclaration(example.Value,
                        path + "/examples/" + Escape(example.Name), format, cancellationToken);
                    if (failure is not null) { return failure; }
                }
            }
        }
        return null;

        static ApiOperationResolutionResult InvalidMedia(string message, string path) =>
            Failure(ApiOperationResolutionStatus.Invalid, "document.response_media", message, path);
    }

    private static ApiOperationResolutionResult InvalidHttpResponse(string message, string pointer) =>
        Failure(ApiOperationResolutionStatus.Invalid, "document.responses", message, pointer);

    private static bool SupportsOpenApiRequestBody(string format, string? httpMethod) =>
        format == "OpenAPI/3.1" || (format == "OpenAPI/3.0" && httpMethod is "post" or "put" or "patch");

    private static ApiOperationResolutionResult? ValidateOpenApiRequestBodyDeclaration(
        JsonElement body, string pointer, string format, CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return InvalidBody("A request body declaration must be a Request Body or Reference Object.", pointer);
        }
        if (body.TryGetProperty("$ref", out var reference))
        {
            if (reference.ValueKind != JsonValueKind.String || !IsHttpMetadataUriReference(reference.GetString()!))
            {
                return InvalidBody("A request body reference requires a string URI reference.", pointer + "/$ref");
            }
            if (format != "OpenAPI/3.1") { return null; }
            foreach (var field in new[] { "summary", "description" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (body.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.String)
                {
                    return InvalidBody("Reference Object metadata must be strings.", pointer + "/" + field);
                }
            }
            return null;
        }
        foreach (var field in new[] { "description", "required" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!body.TryGetProperty(field, out var value)) { continue; }
            var valid = field == "description" ? value.ValueKind == JsonValueKind.String
                : value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            if (!valid) { return InvalidBody("Request body metadata must have its declared native JSON type.", pointer + "/" + field); }
        }
        if (!body.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
        {
            return InvalidBody("A Request Body Object requires a content map.", pointer + "/content");
        }
        var mediaFailure = ValidateOpenApiMediaDeclarations(content, pointer + "/content", format, cancellationToken);
        return mediaFailure ?? ValidateOpenApiRequestEncodings(content, pointer + "/content", format, cancellationToken);

        static ApiOperationResolutionResult InvalidBody(string message, string path) =>
            Failure(ApiOperationResolutionStatus.Invalid, "document.request_body", message, path);
    }

    private static (bool Applies, bool Multipart, bool Serialization) OpenApiEncodingApplicability(string media, string format)
    {
        var type = media.AsSpan();
        var separator = type.IndexOf(';');
        if (separator >= 0) { type = type[..separator]; }
        type = type.Trim();
        var multipart = type.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase);
        var form = type.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
        return (multipart || form, multipart, form ||
            format == "OpenAPI/3.1" && type.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase));
    }

    private static ApiOperationResolutionResult? ValidateOpenApiRequestEncodings(
        JsonElement content, string pointer, string format, CancellationToken cancellationToken)
    {
        foreach (var media in content.EnumerateObject())
        {
            var applicability = OpenApiEncodingApplicability(media.Name, format);
            if (!applicability.Applies || !media.Value.TryGetProperty("encoding", out var encodings)) { continue; }
            foreach (var encoding in encodings.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = pointer + "/" + Escape(media.Name) + "/encoding/" + Escape(encoding.Name);
                var value = encoding.Value;
                if (value.ValueKind != JsonValueKind.Object)
                {
                    return InvalidEncoding("An encoding declaration must be an Encoding Object.", path);
                }
                var hasSerialization = false;
                if (applicability.Serialization)
                {
                    foreach (var field in new[] { "style", "explode", "allowReserved" })
                    {
                        if (!value.TryGetProperty(field, out var metadata)) { continue; }
                        hasSerialization = true;
                        var valid = field == "style"
                            ? metadata.ValueKind == JsonValueKind.String &&
                                metadata.GetString() is "form" or "spaceDelimited" or "pipeDelimited" or "deepObject"
                            : metadata.ValueKind is JsonValueKind.True or JsonValueKind.False;
                        if (!valid)
                        {
                            return InvalidEncoding("Encoding serialization fields require query styles and native boolean flags.", path + "/" + field);
                        }
                    }
                }
                if (!(format == "OpenAPI/3.1" && hasSerialization) && value.TryGetProperty("contentType", out var contentType) &&
                    (contentType.ValueKind != JsonValueKind.String || !IsOpenApiEncodingContentType(contentType.GetString()!, cancellationToken)))
                {
                    return InvalidEncoding("Encoding contentType requires a comma-separated list of media types or ranges.", path + "/contentType");
                }
                if (applicability.Multipart && value.TryGetProperty("headers", out var headers))
                {
                    if (headers.ValueKind != JsonValueKind.Object)
                    {
                        return InvalidEncoding("Encoding headers must be a Header Object map.", path + "/headers");
                    }
                    foreach (var header in headers.EnumerateObject())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { continue; }
                        var failure = ValidateOpenApiHeaderDeclaration(header.Value,
                            path + "/headers/" + Escape(header.Name), format, cancellationToken);
                        if (failure is not null) { return failure; }
                    }
                }
            }
        }
        return null;

        static ApiOperationResolutionResult InvalidEncoding(string message, string path) =>
            Failure(ApiOperationResolutionStatus.Invalid, "document.encoding", message, path);
    }

    private static bool IsOpenApiEncodingContentType(string value, CancellationToken cancellationToken)
    {
        var start = 0;
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (escaped) { escaped = false; continue; }
            var character = value[index];
            if (quoted && character == '\\') { escaped = true; continue; }
            if (character == '"') { quoted = !quoted; }
            if (character != ',' || quoted) { continue; }
            if (!IsResponseMediaRange(value[start..index])) { return false; }
            start = index + 1;
        }
        return !quoted && !escaped && IsResponseMediaRange(value[start..]);
    }

    private static ApiOperationResolutionResult? ValidateOpenApiHeaderDeclaration(
        JsonElement header, string pointer, string format, CancellationToken cancellationToken)
    {
        if (header.ValueKind != JsonValueKind.Object)
        {
            return InvalidHeader("A header declaration must be a Header or Reference Object.", pointer);
        }
        if (header.TryGetProperty("$ref", out var reference))
        {
            if (reference.ValueKind != JsonValueKind.String || !IsHttpMetadataUriReference(reference.GetString()!))
            {
                return InvalidHeader("A header reference requires a string URI reference.", pointer + "/$ref");
            }
            if (format != "OpenAPI/3.1") { return null; }
            foreach (var field in new[] { "summary", "description" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (header.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.String)
                {
                    return InvalidHeader("Reference Object metadata must be strings.", pointer + "/" + field);
                }
            }
            return null;
        }
        foreach (var field in new[] { "name", "in", "allowEmptyValue", "allowReserved" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (header.TryGetProperty(field, out _))
            {
                return InvalidHeader("A Header Object cannot declare parameter names, locations or query-only traits.", pointer + "/" + field);
            }
        }
        foreach (var field in new[] { "description", "required", "deprecated", "explode", "style" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!header.TryGetProperty(field, out var value)) { continue; }
            var valid = field switch
            {
                "description" => value.ValueKind == JsonValueKind.String,
                "style" => value.ValueKind == JsonValueKind.String && value.GetString() == "simple",
                _ => value.ValueKind is JsonValueKind.True or JsonValueKind.False
            };
            if (!valid) { return InvalidHeader("Header metadata must have its declared type and header serialization style.", pointer + "/" + field); }
        }
        return ValidateOpenApiParameterSerialization(header, pointer, format, "Header", cancellationToken);

        static ApiOperationResolutionResult InvalidHeader(string message, string path) =>
            Failure(ApiOperationResolutionStatus.Invalid, "document.header", message, path);
    }

    private static ApiOperationResolutionResult? ValidateOpenApiParameterSerialization(
        JsonElement declaration, string pointer, string format, string kind, CancellationToken cancellationToken)
    {
        var hasSchema = declaration.TryGetProperty("schema", out var schema);
        var hasContent = declaration.TryGetProperty("content", out var content);
        if (!hasSchema && !hasContent)
        {
            return InvalidSerialization($"A {kind} Object requires schema or content.", pointer + "/schema");
        }
        if (hasContent)
        {
            if (hasSchema || content.ValueKind != JsonValueKind.Object || content.EnumerateObject().Count() != 1)
            {
                return InvalidSerialization($"{kind} content must contain one media type and is exclusive of schema.", pointer + "/content");
            }
            foreach (var media in content.EnumerateObject())
            {
                if (!IsResponseMediaRange(media.Name, allowWildcards: false))
                {
                    return InvalidSerialization($"A {kind} content key must be a concrete media type.", pointer + "/content/" + Escape(media.Name));
                }
            }
            var failure = ValidateOpenApiMediaDeclarations(content, pointer + "/content", format, cancellationToken);
            if (failure is not null) { return failure; }
        }
        else if (schema.ValueKind != JsonValueKind.Object &&
            !(format == "OpenAPI/3.1" && schema.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            return InvalidSerialization($"A {kind} schema must be a Schema Object or, in OpenAPI 3.1, a boolean.", pointer + "/schema");
        }
        if (declaration.TryGetProperty("examples", out var examples))
        {
            if (examples.ValueKind != JsonValueKind.Object || declaration.TryGetProperty("example", out _))
            {
                return InvalidSerialization($"{kind} examples must be an object and are exclusive of example.", pointer + "/examples");
            }
            foreach (var example in examples.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var failure = ValidateOpenApiExampleDeclaration(example.Value,
                    pointer + "/examples/" + Escape(example.Name), format, cancellationToken);
                if (failure is not null) { return failure; }
            }
        }
        return null;

        ApiOperationResolutionResult InvalidSerialization(string message, string path) =>
            Failure(ApiOperationResolutionStatus.Invalid, kind == "Header" ? "document.header" : "document.parameter", message, path);
    }

    private static bool IsIgnoredOpenApiHeaderParameter(JsonElement parameter) =>
        parameter.TryGetProperty("in", out var location) && location.ValueKind == JsonValueKind.String &&
        location.GetString() == "header" && parameter.TryGetProperty("name", out var name) &&
        name.ValueKind == JsonValueKind.String &&
        (string.Equals(name.GetString(), "Accept", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(name.GetString(), "Content-Type", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(name.GetString(), "Authorization", StringComparison.OrdinalIgnoreCase));

    private static ApiOperationResolutionResult? ValidateOpenApiParameterDeclaration(
        JsonElement parameter, string pointer, string format, CancellationToken cancellationToken)
    {
        if (parameter.ValueKind != JsonValueKind.Object)
        {
            return InvalidParameter("A parameter declaration must be a Parameter or Reference Object.", pointer);
        }
        if (parameter.TryGetProperty("$ref", out var reference))
        {
            if (reference.ValueKind != JsonValueKind.String || !IsHttpMetadataUriReference(reference.GetString()!))
            {
                return InvalidParameter("A parameter reference requires a string URI reference.", pointer + "/$ref");
            }
            if (format != "OpenAPI/3.1") { return null; }
            foreach (var field in new[] { "summary", "description" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (parameter.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.String)
                {
                    return InvalidParameter("Reference Object metadata must be strings.", pointer + "/" + field);
                }
            }
            return null;
        }
        if (format == "Swagger/2.0")
        {
            return ValidateSwaggerParameterDeclaration(parameter, pointer, cancellationToken);
        }
        if (!parameter.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
        {
            return InvalidParameter("A Parameter Object requires a string name.", pointer + "/name");
        }
        if (!parameter.TryGetProperty("in", out var location) || location.ValueKind != JsonValueKind.String ||
            location.GetString() is not ("query" or "path" or "header" or "cookie"))
        {
            return InvalidParameter("A Parameter Object requires a native parameter location.", pointer + "/in");
        }
        if (IsIgnoredOpenApiHeaderParameter(parameter)) { return null; }
        var nativeLocation = location.GetString()!;
        foreach (var field in new[] { "description", "required", "deprecated", "explode", "allowEmptyValue", "allowReserved", "style" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!parameter.TryGetProperty(field, out var value)) { continue; }
            var valid = field switch
            {
                "description" => value.ValueKind == JsonValueKind.String,
                "style" => value.ValueKind == JsonValueKind.String && nativeLocation switch
                {
                    "path" => value.GetString() is "matrix" or "label" or "simple",
                    "query" => value.GetString() is "form" or "spaceDelimited" or "pipeDelimited" or "deepObject",
                    "header" => value.GetString() == "simple",
                    _ => value.GetString() == "form"
                },
                _ => value.ValueKind is JsonValueKind.True or JsonValueKind.False
            };
            if (!valid || field == "allowEmptyValue" && nativeLocation != "query")
            {
                return InvalidParameter("Parameter metadata must have its declared native type and applicable serialization style.", pointer + "/" + field);
            }
        }
        if (nativeLocation == "path" && (!parameter.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.True))
        {
            return InvalidParameter("A path parameter requires required to be true.", pointer + "/required");
        }
        return ValidateOpenApiParameterSerialization(parameter, pointer, format, "Parameter", cancellationToken);

        static ApiOperationResolutionResult InvalidParameter(string message, string path) =>
            Failure(ApiOperationResolutionStatus.Invalid, "document.parameter", message, path);
    }

    private static ApiOperationResolutionResult? ValidateSwaggerParameterDeclaration(
        JsonElement parameter, string pointer, CancellationToken cancellationToken)
    {
        if (!parameter.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
        {
            return InvalidParameter("A Swagger parameter requires a string name.", pointer + "/name");
        }
        if (!parameter.TryGetProperty("in", out var location) || location.ValueKind != JsonValueKind.String ||
            location.GetString() is not ("query" or "header" or "path" or "formData" or "body"))
        {
            return InvalidParameter("A Swagger parameter requires a native location.", pointer + "/in");
        }
        foreach (var field in new[] { "description", "required" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (parameter.TryGetProperty(field, out var value) && !(field == "description"
                ? value.ValueKind == JsonValueKind.String : value.ValueKind is JsonValueKind.True or JsonValueKind.False))
            {
                return InvalidParameter("Swagger parameter metadata must have its native JSON type.", pointer + "/" + field);
            }
        }
        var nativeLocation = location.GetString()!;
        if (nativeLocation == "path" && (!parameter.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.True))
        {
            return InvalidParameter("A Swagger path parameter requires required to be true.", pointer + "/required");
        }
        if (nativeLocation == "body")
        {
            return !parameter.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Object
                ? InvalidParameter("A Swagger body parameter requires a Schema Object.", pointer + "/schema") : null;
        }
        if (parameter.TryGetProperty("allowEmptyValue", out var allowEmpty) &&
            (nativeLocation is not ("query" or "formData") || allowEmpty.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
        {
            return InvalidParameter("Swagger allowEmptyValue requires a boolean on a query or formData parameter.", pointer + "/allowEmptyValue");
        }
        return ValidateSwaggerScalarDeclaration(parameter, pointer, "Parameter", nativeLocation == "formData",
            nativeLocation is "query" or "formData", cancellationToken);

        static ApiOperationResolutionResult InvalidParameter(string message, string path) =>
            Failure(ApiOperationResolutionStatus.Invalid, "document.parameter", message, path);
    }

    private static ApiOperationResolutionResult? ValidateOpenApiParameters(
        JsonElement owner, string pointer, string format, CancellationToken cancellationToken)
    {
        if (!owner.TryGetProperty("parameters", out var parameters)) { return null; }
        if (parameters.ValueKind != JsonValueKind.Array)
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.parameters",
                "Parameter declarations must be an array.", pointer + "/parameters");
        }
        var names = new HashSet<(string, string)>();
        var index = 0;
        foreach (var parameter in parameters.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = pointer + "/parameters/" + index;
            var failure = ValidateOpenApiParameterDeclaration(parameter, path, format, cancellationToken);
            if (failure is not null) { return failure; }
            if (!parameter.TryGetProperty("$ref", out _) && (format == "Swagger/2.0" || !IsIgnoredOpenApiHeaderParameter(parameter)) &&
                !names.Add((parameter.GetProperty("in").GetString()!, parameter.GetProperty("name").GetString()!)))
            {
                return DuplicateParameter(path);
            }
            index++;
        }
        return null;
    }

    private static ApiOperationResolutionResult DuplicateParameter(string path) =>
        Failure(ApiOperationResolutionStatus.Invalid, "document.parameters",
            "A parameter list cannot repeat the same name and location.", path);

    private static async ValueTask<ApiOperationResolutionResult?> ValidateOpenApiPathParameterCorrespondenceAsync(
        string pathTemplate, ReferenceTarget? pathOwner, ReferenceTarget operation,
        ReferenceContext references, CancellationToken cancellationToken)
    {
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in PathTemplateExpression().Matches(pathTemplate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            references.Work(operation.Pointer);
            templates.Add(match.Value[1..^1]);
        }
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var effective = references.Format == "Swagger/2.0"
            ? new Dictionary<(string, string), ReferenceTarget>() : null;
        foreach (var owner in new[] { pathOwner, operation })
        {
            if (owner is null || !owner.Value.TryGetProperty("parameters", out var parameters)) { continue; }
            var index = 0;
            foreach (var parameter in parameters.EnumerateArray())
            {
                var resolution = await ResolveHttpDeclarationAsync(
                    new ReferenceTarget(owner.Document, parameter, owner.Pointer + "/parameters/" + index),
                    references, ValidateOpenApiParameterDeclaration, "parameter", cancellationToken).ConfigureAwait(false);
                if (resolution.Failure is not null) { return resolution.Failure; }
                var target = resolution.Target!;
                if (effective is not null)
                {
                    var enumFailure = ValidateSwaggerScalarEnums(target.Value, target.Pointer, "Parameter", references.Budget);
                    if (enumFailure is not null) { return enumFailure; }
                    effective[(target.Value.GetProperty("in").GetString()!, target.Value.GetProperty("name").GetString()!)] = target;
                }
                if (target.Value.GetProperty("in").GetString() == "path")
                {
                    var name = target.Value.GetProperty("name").GetString()!;
                    if (!templates.Contains(name))
                    {
                        return Failure(ApiOperationResolutionStatus.Invalid, "document.path_parameter",
                            "A path parameter name must match an expression in the authored path.", target.Pointer + "/name");
                    }
                    declared.Add(name);
                }
                index++;
            }
        }
        if (!templates.IsSubsetOf(declared))
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.path_parameter",
                "Each authored path expression requires a path parameter at the Path Item or operation level.",
                operation.Pointer + "/parameters");
        }
        return effective is null ? null : ValidateSwaggerEffectivePayload(effective.Values, operation, references, cancellationToken);
    }

    private static ApiOperationResolutionResult? ValidateSwaggerEffectivePayload(
        IEnumerable<ReferenceTarget> parameters, ReferenceTarget operation, ReferenceContext references,
        CancellationToken cancellationToken)
    {
        var bodies = 0;
        var hasForm = false;
        foreach (var parameter in parameters)
        {
            references.Work(parameter.Pointer);
            var location = parameter.Value.GetProperty("in").GetString();
            if (location == "body") { bodies++; }
            if (location == "formData") { hasForm = true; }
        }
        if (bodies > 1 || bodies != 0 && hasForm)
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.parameters",
                "Swagger permits at most one body parameter and forbids combining body and formData parameters.",
                operation.Pointer + "/parameters");
        }
        if (!hasForm) { return null; }
        var transportFailure = ValidateSwaggerTransportDeclarations(operation.Value, operation.Pointer, cancellationToken);
        if (transportFailure is not null) { return transportFailure; }
        var consumes = operation.Value.TryGetProperty("consumes", out var local) ? local :
            references.Root.Root.TryGetProperty("consumes", out var inherited) ? inherited : default;
        if (consumes.ValueKind == JsonValueKind.Array && consumes.GetArrayLength() > 0)
        {
            var valid = true;
            foreach (var media in consumes.EnumerateArray())
            {
                references.Work(operation.Pointer + "/consumes");
                var name = media.GetString()!.Split(';', 2)[0].Trim();
                if (!name.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                {
                    valid = false;
                    break;
                }
            }
            if (valid) { return null; }
        }
        return Failure(ApiOperationResolutionStatus.Invalid, "document.parameters",
            "Swagger formData parameters require effective form media types in consumes.", operation.Pointer + "/consumes");
    }

    private static async ValueTask<ApiOperationResolutionResult?> ValidateEffectiveOpenApiParametersAsync(
        JsonElement owner, ReferencedDocument source, string pointer, ReferenceContext references, CancellationToken cancellationToken)
    {
        if (!owner.TryGetProperty("parameters", out var parameters)) { return null; }
        var names = new HashSet<(string, string)>();
        var bodies = 0;
        var hasForm = false;
        var index = 0;
        foreach (var parameter in parameters.EnumerateArray())
        {
            var path = pointer + "/parameters/" + index;
            var resolution = await ResolveHttpDeclarationAsync(new ReferenceTarget(source, parameter, path), references,
                ValidateOpenApiParameterDeclaration, "parameter", cancellationToken).ConfigureAwait(false);
            if (resolution.Failure is not null) { return resolution.Failure; }
            var target = resolution.Target!;
            if (references.Format == "Swagger/2.0" || !IsIgnoredOpenApiHeaderParameter(target.Value))
            {
                if (!names.Add((target.Value.GetProperty("in").GetString()!, target.Value.GetProperty("name").GetString()!)))
                {
                    return DuplicateParameter(path);
                }
                if (references.Format == "Swagger/2.0")
                {
                    var enumFailure = ValidateSwaggerScalarEnums(target.Value, target.Pointer, "Parameter", references.Budget);
                    if (enumFailure is not null) { return enumFailure; }
                    var location = target.Value.GetProperty("in").GetString();
                    if (location == "body") { bodies++; }
                    if (location == "formData") { hasForm = true; }
                    if (bodies > 1 || bodies != 0 && hasForm)
                    {
                        return Failure(ApiOperationResolutionStatus.Invalid, "document.parameters",
                            "Swagger permits at most one body parameter and forbids combining body and formData parameters.", path);
                    }
                    index++;
                    continue;
                }
                var failure = await ValidateEffectiveOpenApiExamplesAsync(target, references, cancellationToken).ConfigureAwait(false);
                if (failure is not null) { return failure; }
                failure = await ValidateEffectiveOpenApiContentAsync(target, references, cancellationToken).ConfigureAwait(false);
                if (failure is not null) { return failure; }
            }
            index++;
        }
        return null;
    }

    private static ApiOperationResolutionResult? ValidateOpenApiLinkDeclaration(
        JsonElement link, string pointer, string format, CancellationToken cancellationToken)
    {
        if (link.ValueKind != JsonValueKind.Object)
        {
            return InvalidLink("A link declaration must be a Link or Reference Object.", pointer);
        }
        if (link.TryGetProperty("$ref", out var reference))
        {
            if (reference.ValueKind != JsonValueKind.String || !IsHttpMetadataUriReference(reference.GetString()!))
            {
                return InvalidLink("A link reference requires a string URI reference.", pointer + "/$ref");
            }
            if (format != "OpenAPI/3.1") { return null; }
            foreach (var field in new[] { "summary", "description" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (link.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.String)
                {
                    return InvalidLink("Reference Object metadata must be strings.", pointer + "/" + field);
                }
            }
            return null;
        }
        var hasId = link.TryGetProperty("operationId", out var operationId);
        var hasRef = link.TryGetProperty("operationRef", out var operationRef);
        if (!hasId && !hasRef)
        {
            return InvalidLink("A Link Object requires operationId or operationRef.", pointer + "/operationId");
        }
        if (hasId && operationId.ValueKind != JsonValueKind.String)
        {
            return InvalidLink("A link operationId must be a string.", pointer + "/operationId");
        }
        if (hasRef && (hasId || operationRef.ValueKind != JsonValueKind.String ||
            !IsHttpMetadataUriReference(operationRef.GetString()!)))
        {
            return InvalidLink("A link operationRef must be a string URI reference, exclusive of operationId.", pointer + "/operationRef");
        }
        foreach (var field in new[] { "description", "parameters", "server" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (link.TryGetProperty(field, out var value) &&
                value.ValueKind != (field == "description" ? JsonValueKind.String : JsonValueKind.Object))
            {
                return InvalidLink("Link metadata must have its declared native JSON type.", pointer + "/" + field);
            }
        }
        return link.TryGetProperty("server", out var server)
            ? ValidateOpenApiServer(server, pointer + "/server", format, cancellationToken) : null;

        static ApiOperationResolutionResult InvalidLink(string message, string path) =>
            Failure(ApiOperationResolutionStatus.Invalid, "document.link", message, path);
    }

    private static bool IsResponseMediaRange(string value, bool allowWildcards = true)
    {
        if (!ResponseMediaRangeSyntax().IsMatch(value) ||
            !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(value, out var mediaType) ||
            mediaType.MediaType is not { } name ||
            mediaType.Parameters.Any(static parameter => string.IsNullOrEmpty(parameter.Value)))
        {
            return false;
        }
        var slash = name.IndexOf('/');
        return slash > 0 && slash < name.Length - 1 &&
            (allowWildcards
                ? name.AsSpan(0, slash) is not "*" || name.AsSpan(slash + 1) is "*"
                : name.AsSpan(0, slash) is not "*" && name.AsSpan(slash + 1) is not "*");
    }

    [GeneratedRegex(
        """\A[ \t]*[!#$%&'*+.^_`|~0-9A-Za-z-]+/[!#$%&'*+.^_`|~0-9A-Za-z-]+(?:[ \t]*;[ \t]*[!#$%&'*+.^_`|~0-9A-Za-z-]+=(?:[!#$%&'*+.^_`|~0-9A-Za-z-]+|"(?:[\t !#-\[\]-~\u0080-\u00ff]|\\[\t \x21-\x7e\u0080-\u00ff])*"))*[ \t]*\z""",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ResponseMediaRangeSyntax();

    private static ApiOperationResolutionResult? ValidateOpenApiExampleDeclaration(
        JsonElement example, string pointer, string format, CancellationToken cancellationToken)
    {
        if (example.ValueKind != JsonValueKind.Object)
        {
            return InvalidExample("An example declaration must be an Example or Reference Object.", pointer);
        }
        var isReference = example.TryGetProperty("$ref", out var reference);
        if (isReference)
        {
            if (reference.ValueKind != JsonValueKind.String || !IsHttpMetadataUriReference(reference.GetString()!))
            {
                return InvalidExample("An example reference requires a string URI reference.", pointer + "/$ref");
            }
            if (format != "OpenAPI/3.1") { return null; }
        }
        foreach (var field in new[] { "summary", "description" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (example.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.String)
            {
                return InvalidExample("Example metadata must be strings.", pointer + "/" + field);
            }
        }
        if (isReference) { return null; }
        if (example.TryGetProperty("externalValue", out var external))
        {
            if (external.ValueKind != JsonValueKind.String || !IsHttpMetadataUriReference(external.GetString()!))
            {
                return InvalidExample("An external example requires a string URI reference.", pointer + "/externalValue");
            }
            if (example.TryGetProperty("value", out _))
            {
                return InvalidExample("Example value and externalValue are mutually exclusive.", pointer + "/externalValue");
            }
        }
        return null;

        static ApiOperationResolutionResult InvalidExample(string message, string path) =>
            Failure(ApiOperationResolutionStatus.Invalid, "document.example", message, path);
    }

    private static async ValueTask<ApiOperationResolutionResult?> ValidateEffectiveHttpDeclarationsAsync(
        JsonElement operation, ReferencedDocument source, string pointer, string? httpMethod, ReferenceContext references,
        CancellationToken cancellationToken)
    {
        var parameterFailure = await ValidateEffectiveOpenApiParametersAsync(operation, source, pointer,
            references, cancellationToken).ConfigureAwait(false);
        if (parameterFailure is not null) { return parameterFailure; }
        var bodyFailure = await ValidateEffectiveOpenApiRequestBodyAsync(
            new ReferenceTarget(source, operation, pointer), httpMethod, references, cancellationToken).ConfigureAwait(false);
        if (bodyFailure is not null) { return bodyFailure; }
        return await ValidateEffectiveHttpResponsesAsync(operation, source, pointer, references, true,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ApiOperationResolutionResult?> ValidateEffectiveHttpResponsesAsync(
        JsonElement operation, ReferencedDocument source, string pointer, ReferenceContext references,
        bool followLinks, CancellationToken cancellationToken)
    {
        if (!operation.TryGetProperty("responses", out var responses)) { return null; }
        foreach (var entry in responses.EnumerateObject())
        {
            if (entry.Name.StartsWith("x-", StringComparison.Ordinal)) { continue; }
            var resolution = await ResolveHttpDeclarationAsync(
                new ReferenceTarget(source, entry.Value, pointer + "/responses/" + Escape(entry.Name)),
                references, static (value, path, format, token) =>
                    ValidateHttpResponseDeclaration(value, path, format, allowReference: true, token),
                "response", cancellationToken).ConfigureAwait(false);
            if (resolution.Failure is not null) { return resolution.Failure; }
            if (references.Format == "Swagger/2.0")
            {
                var enumFailure = ValidateSwaggerResponseEnums(resolution.Target!.Value, resolution.Target.Pointer, references.Budget);
                if (enumFailure is not null) { return enumFailure; }
                var exampleFailure = ValidateSwaggerResponseExampleProduces(resolution.Target, operation, pointer, references,
                    cancellationToken);
                if (exampleFailure is not null) { return exampleFailure; }
                continue;
            }
            var response = resolution.Target!;
            var headerFailure = await ValidateEffectiveOpenApiHeadersAsync(response, references, cancellationToken).ConfigureAwait(false);
            if (headerFailure is not null) { return headerFailure; }
            if (followLinks && response.Value.TryGetProperty("links", out var links))
            {
                foreach (var link in links.EnumerateObject())
                {
                    var target = await ResolveHttpDeclarationAsync(
                        new ReferenceTarget(response.Document, link.Value, response.Pointer + "/links/" + Escape(link.Name)),
                        references, ValidateOpenApiLinkDeclaration, "link", cancellationToken).ConfigureAwait(false);
                    if (target.Failure is not null) { return target.Failure; }
                    if (target.Target!.Value.TryGetProperty("operationId", out _))
                    {
                        references.Work(target.Target.Pointer);
                        references.OperationIdLinks.Add(target.Target);
                    }
                    else
                    {
                        var linkFailure = await ValidateOpenApiLinkOperationRefAsync(target.Target, references,
                            cancellationToken).ConfigureAwait(false);
                        if (linkFailure is not null) { return linkFailure; }
                    }
                }
            }
            var failure = await ValidateEffectiveOpenApiContentAsync(response, references, cancellationToken).ConfigureAwait(false);
            if (failure is not null) { return failure; }
        }
        return null;
    }

    private static async ValueTask<ApiOperationResolutionResult?> ValidateOpenApiLinkOperationRefAsync(
        ReferenceTarget link, ReferenceContext references, CancellationToken cancellationToken)
    {
        var resolution = await references.ResolveAsync(link.Document,
            link.Value.GetProperty("operationRef").GetString()!, link.Pointer + "/operationRef",
            cancellationToken).ConfigureAwait(false);
        if (resolution.Failure is not null) { return resolution.Failure; }
        var target = resolution.Target!;
        if (target.Value.ValueKind != JsonValueKind.Object)
        {
            return InvalidTarget("A Link operationRef must point to an Operation Object.", target.Pointer);
        }
        foreach (var field in target.Value.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            references.Work(target.Pointer + "/" + Escape(field.Name));
            if (!field.Name.StartsWith("x-", StringComparison.Ordinal) &&
                field.Name is not ("tags" or "summary" or "description" or "externalDocs" or "operationId" or
                    "parameters" or "requestBody" or "responses" or "callbacks" or "deprecated" or "security" or "servers"))
            {
                return InvalidTarget("A Link operationRef target must use Operation Object fields or specification extensions.",
                    target.Pointer + "/" + Escape(field.Name));
            }
        }
        if (references.Format == "OpenAPI/3.0" && !target.Value.TryGetProperty("responses", out _))
        {
            return InvalidTarget("An OpenAPI 3.0 linked Operation Object requires responses.", target.Pointer + "/responses");
        }
        if (target.Value.TryGetProperty("callbacks", out var callbacks) && callbacks.ValueKind != JsonValueKind.Object)
        {
            return InvalidTarget("Linked operation callbacks must be an object.", target.Pointer + "/callbacks");
        }
        string? httpMethod = null;
        if (references.Format == "OpenAPI/3.0" && target.Value.TryGetProperty("requestBody", out _))
        {
            references.Work(target.Pointer);
            var tokens = target.Pointer == "$" ? [] : target.Pointer[2..].Split('/')
                .Select(static token => token.Replace("~1", "/", StringComparison.Ordinal)
                    .Replace("~0", "~", StringComparison.Ordinal)).ToArray();
            if (ValidateHttpOperationPointer(references.Format, tokens, out _) is not null)
            {
                return Failure(ApiOperationResolutionStatus.Indeterminate, "document.link_operation_method",
                    "OpenAPI 3.0 linked request-body admission requires an HTTP method from a supported operation location.",
                    target.Pointer + "/requestBody");
            }
            httpMethod = tokens[^1];
        }
        var metadataFailure = ValidateHttpOperationMetadata(target.Value, target.Pointer, references.Format, httpMethod,
            cancellationToken);
        if (metadataFailure is not null) { return metadataFailure; }
        var serverFailure = ValidateOpenApiServers(target.Value, target.Pointer, references.Format, cancellationToken);
        if (serverFailure is not null) { return serverFailure; }
        var parameterFailure = await ValidateEffectiveOpenApiParametersAsync(target.Value, target.Document, target.Pointer,
            references, cancellationToken).ConfigureAwait(false);
        if (parameterFailure is not null) { return parameterFailure; }
        var bodyFailure = await ValidateEffectiveOpenApiRequestBodyAsync(target, httpMethod, references,
            cancellationToken).ConfigureAwait(false);
        return bodyFailure ?? await ValidateEffectiveHttpResponsesAsync(target.Value, target.Document, target.Pointer,
            references, false, cancellationToken).ConfigureAwait(false);

        static ApiOperationResolutionResult InvalidTarget(string message, string path) =>
            Failure(ApiOperationResolutionStatus.Invalid, "document.link_operation_ref", message, path);
    }

    private static async ValueTask<ApiOperationResolutionResult?> ValidateEffectiveOpenApiRequestBodyAsync(
        ReferenceTarget operation, string? httpMethod, ReferenceContext references, CancellationToken cancellationToken)
    {
        if (!SupportsOpenApiRequestBody(references.Format, httpMethod) ||
            !operation.Value.TryGetProperty("requestBody", out var body)) { return null; }
        var resolution = await ResolveHttpDeclarationAsync(
            new ReferenceTarget(operation.Document, body, operation.Pointer + "/requestBody"), references,
            ValidateOpenApiRequestBodyDeclaration, "request body", cancellationToken).ConfigureAwait(false);
        if (resolution.Failure is not null) { return resolution.Failure; }
        var failure = await ValidateEffectiveOpenApiContentAsync(resolution.Target!, references, cancellationToken).ConfigureAwait(false);
        return failure ?? await ValidateEffectiveOpenApiRequestEncodingsAsync(resolution.Target!, references,
            cancellationToken).ConfigureAwait(false);
    }

    private static ApiOperationResolutionResult? ValidateSwaggerResponseExampleProduces(
        ReferenceTarget response, JsonElement operation, string operationPointer, ReferenceContext references,
        CancellationToken cancellationToken)
    {
        if (!response.Value.TryGetProperty("examples", out var examples) || !examples.EnumerateObject().Any()) { return null; }
        var failure = ValidateSwaggerTransportDeclarations(operation, operationPointer, cancellationToken);
        if (failure is not null) { return failure; }
        var produces = operation.TryGetProperty("produces", out var local) ? local :
            references.Root.Root.TryGetProperty("produces", out var inherited) ? inherited : default;
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (produces.ValueKind == JsonValueKind.Array)
        {
            foreach (var media in produces.EnumerateArray())
            {
                references.Work(operationPointer + "/produces");
                names.Add(media.GetString()!);
            }
        }
        foreach (var example in examples.EnumerateObject())
        {
            var path = response.Pointer + "/examples/" + Escape(example.Name);
            references.Work(path);
            if (!names.Contains(example.Name))
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.response_example",
                    "A Swagger response example key must be one of the operation's effective produces values.", path);
            }
        }
        return null;
    }

    private static async ValueTask<ApiOperationResolutionResult?> ValidateEffectiveOpenApiRequestEncodingsAsync(
        ReferenceTarget body, ReferenceContext references, CancellationToken cancellationToken)
    {
        foreach (var media in body.Value.GetProperty("content").EnumerateObject())
        {
            if (!OpenApiEncodingApplicability(media.Name, references.Format).Multipart ||
                !media.Value.TryGetProperty("encoding", out var encodings)) { continue; }
            foreach (var encoding in encodings.EnumerateObject())
            {
                var pointer = body.Pointer + "/content/" + Escape(media.Name) + "/encoding/" + Escape(encoding.Name);
                references.Work(pointer);
                var failure = await ValidateEffectiveOpenApiHeadersAsync(
                    new ReferenceTarget(body.Document, encoding.Value, pointer), references, cancellationToken).ConfigureAwait(false);
                if (failure is not null) { return failure; }
            }
        }
        return null;
    }

    private static async ValueTask<ApiOperationResolutionResult?> ValidateEffectiveOpenApiHeadersAsync(
        ReferenceTarget location, ReferenceContext references, CancellationToken cancellationToken)
    {
        if (!location.Value.TryGetProperty("headers", out var headers)) { return null; }
        foreach (var header in headers.EnumerateObject())
        {
            if (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { continue; }
            var target = await ResolveHttpDeclarationAsync(
                new ReferenceTarget(location.Document, header.Value, location.Pointer + "/headers/" + Escape(header.Name)),
                references, ValidateOpenApiHeaderDeclaration, "header", cancellationToken).ConfigureAwait(false);
            if (target.Failure is not null) { return target.Failure; }
            var resolved = target.Target!;
            var exampleFailure = await ValidateEffectiveOpenApiExamplesAsync(resolved, references, cancellationToken).ConfigureAwait(false);
            if (exampleFailure is not null) { return exampleFailure; }
            var contentFailure = await ValidateEffectiveOpenApiContentAsync(resolved, references, cancellationToken).ConfigureAwait(false);
            if (contentFailure is not null) { return contentFailure; }
        }
        return null;
    }

    private static async ValueTask<ApiOperationResolutionResult?> ValidateEffectiveOpenApiContentAsync(
        ReferenceTarget location, ReferenceContext references, CancellationToken cancellationToken)
    {
        if (!location.Value.TryGetProperty("content", out var content)) { return null; }
        foreach (var media in content.EnumerateObject())
        {
            var pointer = location.Pointer + "/content/" + Escape(media.Name);
            references.Work(pointer);
            var failure = await ValidateEffectiveOpenApiExamplesAsync(
                new ReferenceTarget(location.Document, media.Value, pointer), references, cancellationToken).ConfigureAwait(false);
            if (failure is not null) { return failure; }
        }
        return null;
    }

    private static async ValueTask<ApiOperationResolutionResult?> ValidateEffectiveOpenApiExamplesAsync(
        ReferenceTarget location, ReferenceContext references, CancellationToken cancellationToken)
    {
        if (!location.Value.TryGetProperty("examples", out var examples)) { return null; }
        foreach (var example in examples.EnumerateObject())
        {
            var target = await ResolveHttpDeclarationAsync(
                new ReferenceTarget(location.Document, example.Value, location.Pointer + "/examples/" + Escape(example.Name)),
                references, ValidateOpenApiExampleDeclaration, "example", cancellationToken).ConfigureAwait(false);
            if (target.Failure is not null) { return target.Failure; }
        }
        return null;
    }

    private static async ValueTask<ReferenceTargetResolution> ResolveHttpDeclarationAsync(
        ReferenceTarget location, ReferenceContext references,
        Func<JsonElement, string, string, CancellationToken, ApiOperationResolutionResult?> validate,
        string kind, CancellationToken cancellationToken)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            references.Work(location.Pointer);
            var failure = validate(location.Value, location.Pointer, references.Format, cancellationToken);
            if (failure is not null) { return ReferenceTargetResolution.Failed(failure); }
            if (!location.Value.TryGetProperty("$ref", out var reference))
            {
                return ReferenceTargetResolution.Succeeded(location);
            }
            if (!active.Add(location.Document.Identity + "#" + location.Pointer))
            {
                return ReferenceTargetResolution.Failed(Failure(ApiOperationResolutionStatus.Indeterminate, "dependency.cycle",
                    $"A {kind} reference cycle prevents operation selection.", location.Pointer + "/$ref"));
            }
            if (active.Count > Capabilities.MaxReferenceDepth)
            {
                return ReferenceTargetResolution.Failed(Failure(ApiOperationResolutionStatus.Indeterminate, "limit.reference_depth",
                    $"The {kind} reference-chain depth limit was exceeded.", location.Pointer + "/$ref"));
            }
            var target = await references.ResolveAsync(location.Document, reference.GetString()!, location.Pointer + "/$ref",
                cancellationToken).ConfigureAwait(false);
            if (target.Failure is not null) { return target; }
            location = target.Target!;
        }
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
        if (!IsHttpMetadataUriReference(url.GetString()!))
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.external_docs_uri",
                "An External Documentation Object url must be a valid URI reference.", path + "/url");
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
        if (pointer == "$")
        {
            foreach (var field in new[] { "host", "basePath" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!value.TryGetProperty(field, out var address)) { continue; }
                if (address.ValueKind != JsonValueKind.String)
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.swagger_endpoint",
                        "Swagger host and basePath declarations must be strings.", "$/" + field);
                }
                var text = address.GetString()!;
                if (field == "basePath" ? !SwaggerBasePath().IsMatch(text) : !IsSwaggerHost(text))
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.swagger_endpoint",
                        "Swagger host must contain a URI host and optional decimal port; basePath must be a slash-prefixed URI path without templates.",
                        "$/" + field);
                }
            }
        }
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

    private static bool IsSwaggerHost(string text)
    {
        if (!text.StartsWith('[')) { return SwaggerRegisteredHost().IsMatch(text); }
        var end = text.IndexOf(']');
        if (end < 0 || end + 1 < text.Length &&
            (text[end + 1] != ':' || text.AsSpan(end + 2).ContainsAnyExcept("0123456789".AsSpan())))
        {
            return false;
        }
        var literal = text.AsSpan(1, end - 1);
        if (SwaggerFutureIpLiteral().IsMatch(literal)) { return true; }
        return !literal.ContainsAnyExcept("0123456789ABCDEFabcdef:.".AsSpan()) &&
            System.Net.IPAddress.TryParse(literal, out var address) &&
            address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
    }

    [GeneratedRegex(@"\A(?:[A-Za-z0-9._~!$&'()*+,;=-]|%[A-Fa-f0-9]{2})+(?::[0-9]*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SwaggerRegisteredHost();

    [GeneratedRegex(@"\A[vV][A-Fa-f0-9]+\.[A-Za-z0-9._~!$&'()*+,;=:-]+\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SwaggerFutureIpLiteral();

    [GeneratedRegex(@"\A/(?:[A-Za-z0-9._~!$&'()*+,;=:@/-]|%[A-Fa-f0-9]{2})*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SwaggerBasePath();

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

    private static ApiOperationResolutionResult? ValidateHttpSecurityShapes(
        JsonElement value, string pointer, CancellationToken cancellationToken)
    {
        if (!value.TryGetProperty("security", out var security)) { return null; }
        var path = pointer + "/security";
        if (security.ValueKind != JsonValueKind.Array)
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.security",
                "HTTP security must be an array of Security Requirement Objects.", path);
        }
        var index = 0;
        foreach (var requirement in security.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemPath = path + "/" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (requirement.ValueKind != JsonValueKind.Object)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.security",
                    "A Security Requirement must be an object.", itemPath);
            }
            foreach (var scheme in requirement.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var schemePath = itemPath + "/" + Escape(scheme.Name);
                if (scheme.Value.ValueKind != JsonValueKind.Array)
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.security",
                        "A Security Requirement scheme value must be an array of strings.", schemePath);
                }
                var scopeIndex = 0;
                foreach (var scope in scheme.Value.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (scope.ValueKind != JsonValueKind.String)
                    {
                        return Failure(ApiOperationResolutionStatus.Invalid, "document.security",
                            "Security Requirement scope or role names must be strings.",
                            schemePath + "/" + scopeIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    scopeIndex++;
                }
            }
        }
        return null;
    }

    private static ApiOperationResolutionResult? ValidateHttpSecurityScheme(JsonElement scheme, string pointer,
        string format, CancellationToken cancellationToken)
    {
        var swagger = format == "Swagger/2.0";
        if (!swagger && scheme.TryGetProperty("$ref", out _))
        {
            var referenceFailure = ValidateSecurityString(scheme, "$ref", pointer, required: true);
            referenceFailure ??= ValidateSecurityUri(scheme, "$ref", pointer);
            if (referenceFailure is not null || format != "OpenAPI/3.1") { return referenceFailure; }
            return ValidateSecurityString(scheme, "summary", pointer, required: false) ??
                ValidateSecurityString(scheme, "description", pointer, required: false);
        }
        var failure = ValidateSecurityString(scheme, "type", pointer, required: true);
        if (failure is not null) { return failure; }
        var type = scheme.GetProperty("type").GetString()!;
        var valid = swagger ? type is "basic" or "apiKey" or "oauth2"
            : type is "apiKey" or "http" or "oauth2" or "openIdConnect" ||
                format == "OpenAPI/3.1" && type == "mutualTLS";
        if (!valid) { return InvalidSecurityScheme("The security scheme type is not declared by the selected API format.", pointer + "/type"); }
        failure = ValidateSecurityString(scheme, "description", pointer, required: false);
        if (failure is not null) { return failure; }
        switch (type)
        {
            case "apiKey":
                failure = ValidateSecurityString(scheme, "name", pointer, required: true) ??
                    ValidateSecurityString(scheme, "in", pointer, required: true);
                if (failure is not null) { return failure; }
                var location = scheme.GetProperty("in").GetString();
                return location is "query" or "header" || !swagger && location == "cookie"
                    ? null : InvalidSecurityScheme("The API key location is not declared by the selected API format.", pointer + "/in");
            case "http":
                failure = ValidateSecurityString(scheme, "scheme", pointer, required: true);
                if (failure is not null) { return failure; }
                if (!IsHttpToken(scheme.GetProperty("scheme").GetString()!))
                {
                    return InvalidSecurityScheme("An HTTP authentication scheme name must be a nonempty RFC token.",
                        pointer + "/scheme");
                }
                return string.Equals(scheme.GetProperty("scheme").GetString(), "bearer", StringComparison.OrdinalIgnoreCase)
                    ? ValidateSecurityString(scheme, "bearerFormat", pointer, required: false) : null;
            case "openIdConnect":
                return ValidateSecurityString(scheme, "openIdConnectUrl", pointer, required: true) ??
                    ValidateSecurityUri(scheme, "openIdConnectUrl", pointer);
            case "oauth2":
                if (swagger)
                {
                    failure = ValidateSecurityString(scheme, "flow", pointer, required: true);
                    if (failure is not null) { return failure; }
                    var flow = scheme.GetProperty("flow").GetString()!;
                    if (flow is not ("implicit" or "password" or "application" or "accessCode"))
                    {
                        return InvalidSecurityScheme("The Swagger OAuth flow is not a declared flow name.", pointer + "/flow");
                    }
                    return ValidateHttpOAuthFlow(scheme, pointer, flow, swagger, cancellationToken);
                }
                if (!scheme.TryGetProperty("flows", out var flows) || flows.ValueKind != JsonValueKind.Object)
                {
                    return InvalidSecurityScheme("An OAuth security scheme requires a flows object.", pointer + "/flows");
                }
                foreach (var flow in flows.EnumerateObject())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (flow.Name is not ("implicit" or "password" or "clientCredentials" or "authorizationCode")) { continue; }
                    var flowPointer = pointer + "/flows/" + Escape(flow.Name);
                    if (flow.Value.ValueKind != JsonValueKind.Object)
                    {
                        return InvalidSecurityScheme("An OAuth flow declaration must be an object.", flowPointer);
                    }
                    failure = ValidateHttpOAuthFlow(flow.Value, flowPointer, flow.Name, swagger, cancellationToken);
                    if (failure is not null) { return failure; }
                }
                return null;
            default:
                return null;
        }
    }

    private static ApiOperationResolutionResult? ValidateHttpOAuthFlow(JsonElement value, string pointer,
        string flow, bool swagger, CancellationToken cancellationToken)
    {
        var failure = ValidateSecurityString(value, "authorizationUrl", pointer,
            required: flow is "implicit" or "accessCode" or "authorizationCode") ??
            ValidateSecurityString(value, "tokenUrl", pointer, required: flow != "implicit");
        if (failure is not null) { return failure; }
        if (!swagger)
        {
            failure = ValidateSecurityString(value, "refreshUrl", pointer, required: false);
            if (failure is not null) { return failure; }
            foreach (var field in new[] { "authorizationUrl", "tokenUrl", "refreshUrl" })
            {
                failure = ValidateSecurityUri(value, field, pointer);
                if (failure is not null) { return failure; }
            }
        }
        if (!value.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Object)
        {
            return InvalidSecurityScheme("An OAuth flow requires a scopes object.", pointer + "/scopes");
        }
        foreach (var scope in scopes.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (swagger && scope.Name.StartsWith("x-", StringComparison.Ordinal)) { continue; }
            if (scope.Value.ValueKind != JsonValueKind.String)
            {
                return InvalidSecurityScheme("An OAuth scope description must be a string.",
                    pointer + "/scopes/" + Escape(scope.Name));
            }
        }
        return null;
    }

    private static ApiOperationResolutionResult? ValidateSecurityString(
        JsonElement value, string field, string pointer, bool required)
    {
        if (!value.TryGetProperty(field, out var declaration))
        {
            return required ? InvalidSecurityScheme($"A security declaration requires a string {field}.", pointer + "/" + field) : null;
        }
        return declaration.ValueKind == JsonValueKind.String ? null :
            InvalidSecurityScheme($"A security declaration {field} must be a string.", pointer + "/" + field);
    }

    private static ApiOperationResolutionResult InvalidSecurityScheme(string message, string pointer) =>
        Failure(ApiOperationResolutionStatus.Invalid, "document.security_scheme", message, pointer);

    private static ApiOperationResolutionResult? ValidateSecurityUri(JsonElement value, string field, string pointer)
    {
        if (!value.TryGetProperty(field, out var uri) || uri.ValueKind != JsonValueKind.String) { return null; }
        return IsHttpMetadataUriReference(uri.GetString()!) ? null :
            Failure(ApiOperationResolutionStatus.Invalid, "document.security_uri",
                "A security declaration address must be a valid URI reference.", pointer + "/" + field);
    }

    private static ApiOperationResolutionResult? ValidateHttpSecurityNames(JsonElement value, string pointer,
        JsonElement entry, bool swagger, CancellationToken cancellationToken)
    {
        if (!value.TryGetProperty("security", out var requirements)) { return null; }
        var schemes = default(JsonElement);
        if (swagger) { entry.TryGetProperty("securityDefinitions", out schemes); }
        else if (entry.TryGetProperty("components", out var components))
        {
            components.TryGetProperty("securitySchemes", out schemes);
        }
        var index = 0;
        foreach (var requirement in requirements.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var scheme in requirement.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (schemes.ValueKind != JsonValueKind.Object || !schemes.TryGetProperty(scheme.Name, out _))
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.security_scheme_name",
                        "A Security Requirement name must identify a scheme declaration in the entry document.",
                        pointer + "/security/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        "/" + Escape(scheme.Name));
                }
            }
            index++;
        }
        return null;
    }

    private static async ValueTask<ApiOperationResolutionResult?> ValidateEffectiveHttpSecurityAsync(
        JsonElement value, string pointer, ReferenceContext references, CancellationToken cancellationToken)
    {
        if (!value.TryGetProperty("security", out var requirements)) { return null; }
        var swagger = references.Format == "Swagger/2.0";
        var entry = references.Root.Root;
        var schemes = swagger ? entry.TryGetProperty("securityDefinitions", out var definitions) ? definitions : default
            : entry.TryGetProperty("components", out var components) &&
                components.TryGetProperty("securitySchemes", out var declarations) ? declarations : default;
        var index = 0;
        foreach (var requirement in requirements.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var name in requirement.EnumerateObject())
            {
                references.Work(pointer);
                var scheme = schemes.GetProperty(name.Name);
                var source = references.Root;
                var schemePointer = (swagger ? "$/securityDefinitions/" : "$/components/securitySchemes/") + Escape(name.Name);
                var active = new HashSet<string>(StringComparer.Ordinal);
                while (true)
                {
                    references.Work(schemePointer);
                    if (scheme.ValueKind != JsonValueKind.Object)
                    {
                        return InvalidSecurityScheme("A referenced security scheme must be an object.", schemePointer);
                    }
                    if (swagger || !scheme.TryGetProperty("$ref", out var reference)) { break; }
                    var referenceFailure = ValidateHttpSecurityScheme(scheme, schemePointer, references.Format, cancellationToken);
                    if (referenceFailure is not null) { return referenceFailure; }
                    if (!active.Add(source.Identity + "#" + schemePointer))
                    {
                        return Failure(ApiOperationResolutionStatus.Indeterminate, "dependency.cycle",
                            "A security scheme reference cycle prevents operation selection.", schemePointer + "/$ref");
                    }
                    if (active.Count > Capabilities.MaxReferenceDepth)
                    {
                        return Failure(ApiOperationResolutionStatus.Indeterminate, "limit.reference_depth",
                            "The security scheme reference-chain depth limit was exceeded.", schemePointer + "/$ref");
                    }
                    var target = await references.ResolveAsync(source, reference.GetString()!, schemePointer + "/$ref",
                        cancellationToken).ConfigureAwait(false);
                    if (target.Failure is not null) { return target.Failure; }
                    var resolved = target.Target!;
                    scheme = resolved.Value;
                    source = resolved.Document;
                    schemePointer = resolved.Pointer;
                }
                var failure = ValidateHttpSecurityScheme(scheme, schemePointer, references.Format, cancellationToken);
                if (failure is not null) { return failure; }
                var type = scheme.GetProperty("type").GetString();
                if (references.Format != "OpenAPI/3.1" && type is not ("oauth2" or "openIdConnect") &&
                    name.Value.GetArrayLength() != 0)
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.security_scopes",
                        "A non-OAuth Security Requirement must have an empty array in the selected API format.",
                        pointer + "/security/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        "/" + Escape(name.Name));
                }
            }
            index++;
        }
        return null;
    }

    private static async ValueTask<ApiOperationResolutionResult?> CollectCallbackMatchesAsync(
        JsonElement callback, ReferencedDocument source, string pointer, string? operationId,
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
            return IsHttpToken(expression[7..]);
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

    private static bool IsHttpToken(ReadOnlySpan<char> token)
    {
        if (token.IsEmpty) { return false; }
        foreach (var character in token)
        {
            if (!char.IsAsciiLetterOrDigit(character) && !"!#$%&'*+-.^_`|~".Contains(character))
            {
                return false;
            }
        }
        return true;
    }
}
