// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;

namespace XRegistry.Validation;

public static partial class ApiOperationDocumentResolver
{
    private static void ValidateOpenRpcDocumentMetadata(JsonElement root, ValidationContext budget)
    {
        ValidateOpenRpcServers(root, "$", budget);
        ValidateOpenRpcDocumentation(root, "$");
        ValidateOpenRpcOptionalString(root, "$schema", "$");
        ValidationContext.Require(root.TryGetProperty("info", out var info) &&
            info.ValueKind == JsonValueKind.Object, "$/info",
            "document.info", "OpenRPC requires an info object.");
        RequireOpenRpcString(info, "title", "$/info", "document.info_title");
        RequireOpenRpcString(info, "version", "$/info", "document.info_version");
        ValidateOpenRpcOptionalString(info, "description", "$/info");
        ValidateOpenRpcOptionalString(info, "termsOfService", "$/info");
        ValidateOpenRpcUrl(info, "termsOfService", "$/info");
        foreach (var member in new[] { "contact", "license" })
        {
            if (!info.TryGetProperty(member, out var metadata)) { continue; }
            var path = "$/info/" + member;
            ValidationContext.Require(metadata.ValueKind == JsonValueKind.Object, path,
                "document.info_metadata", "OpenRPC contact and license must be objects.");
            ValidateOpenRpcOptionalString(metadata, "name", path);
            ValidateOpenRpcOptionalString(metadata, "url", path);
            ValidateOpenRpcUrl(metadata, "url", path);
            if (member == "contact") { ValidateOpenRpcOptionalString(metadata, "email", path); }
        }
    }

    private static async ValueTask<ApiOperationResolutionResult?> ValidateOpenRpcMethodAsync(
        OperationLocation location, ReferenceContext references, ValidationContext budget,
        List<OperationLocation> links, CancellationToken cancellationToken)
    {
        var method = location.Operation;
        var path = location.Pointer;
        budget.Work(path: path);
        ValidateOpenRpcAnnotations(method, path);
        ValidateOpenRpcDocumentation(method, path);
        ValidateOpenRpcServers(method, path, budget);
        if (method.TryGetProperty("links", out var methodLinks))
        {
            ValidationContext.Require(methodLinks.ValueKind == JsonValueKind.Array, path + "/links",
                "document.links", "OpenRPC links must be an array.");
            var linkIndex = 0;
            foreach (var link in methodLinks.EnumerateArray())
            {
                budget.Work(path: path);
                var resolved = await ResolveOperationAsync(link, location.Source,
                    path + "/links/" + (linkIndex++).ToString(CultureInfo.InvariantCulture), references,
                    new(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
                if (resolved.Failure is not null) { return resolved.Failure; }
                var item = resolved.Operation!;
                ValidateOpenRpcOptionalString(item.Operation, "summary", item.Pointer);
                ValidateOpenRpcOptionalString(item.Operation, "description", item.Pointer);
                ValidateOpenRpcOptionalString(item.Operation, "method", item.Pointer);
                if (item.Operation.TryGetProperty("server", out var server))
                {
                    ValidateOpenRpcServer(server, item.Pointer + "/server", budget);
                }
                links.Add(item);
            }
        }
        if (method.TryGetProperty("tags", out var tags))
        {
            ValidationContext.Require(tags.ValueKind == JsonValueKind.Array, path + "/tags",
                "document.tags", "OpenRPC tags must be an array.");
            var tagIndex = 0;
            foreach (var tag in tags.EnumerateArray())
            {
                budget.Work(path: path);
                var resolved = await ResolveOperationAsync(tag, location.Source,
                    path + "/tags/" + (tagIndex++).ToString(CultureInfo.InvariantCulture), references,
                    new(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
                if (resolved.Failure is not null) { return resolved.Failure; }
                var item = resolved.Operation!;
                RequireOpenRpcString(item.Operation, "name", item.Pointer, "document.tag_name");
                ValidateOpenRpcOptionalString(item.Operation, "description", item.Pointer);
                ValidateOpenRpcDocumentation(item.Operation, item.Pointer);
            }
        }
        if (method.TryGetProperty("paramStructure", out var structure))
        {
            ValidationContext.Require(structure.ValueKind == JsonValueKind.String &&
                structure.GetString() is "by-name" or "by-position" or "either",
                path + "/paramStructure", "document.param_structure",
                "OpenRPC paramStructure must be by-name, by-position or either.");
        }
        ValidationContext.Require(method.TryGetProperty("params", out var parameters) &&
            parameters.ValueKind == JsonValueKind.Array, path + "/params", "document.params",
            "An OpenRPC method requires a params array.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var optional = false;
        var index = 0;
        foreach (var parameter in parameters.EnumerateArray())
        {
            budget.Work(path: path);
            var resolved = await ResolveOperationAsync(parameter, location.Source,
                path + "/params/" + (index++).ToString(CultureInfo.InvariantCulture), references,
                new(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null) { return resolved.Failure; }
            var descriptor = resolved.Operation!;
            var name = ValidateOpenRpcDescriptor(descriptor);
            ValidationContext.Require(names.Add(name), descriptor.Pointer + "/name",
                "document.parameter_duplicate", "OpenRPC parameter names must be unique.");
            var required = descriptor.Operation.TryGetProperty("required", out var flag) && flag.GetBoolean();
            ValidationContext.Require(!required || !optional, descriptor.Pointer + "/required",
                "document.parameter_order", "Required OpenRPC parameters must precede optional parameters.");
            optional |= !required;
        }
        if (method.TryGetProperty("result", out var result))
        {
            var resolved = await ResolveOperationAsync(result, location.Source, path + "/result", references,
                new(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null) { return resolved.Failure; }
            ValidateOpenRpcDescriptor(resolved.Operation!);
        }
        if (method.TryGetProperty("errors", out var errors))
        {
            ValidationContext.Require(errors.ValueKind == JsonValueKind.Array, path + "/errors",
                "document.errors", "OpenRPC errors must be an array.");
            var codes = new HashSet<JsonNumber>();
            index = 0;
            foreach (var error in errors.EnumerateArray())
            {
                budget.Work(path: path);
                var resolved = await ResolveOperationAsync(error, location.Source,
                    path + "/errors/" + (index++).ToString(CultureInfo.InvariantCulture), references,
                    new(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
                if (resolved.Failure is not null) { return resolved.Failure; }
                var item = resolved.Operation!;
                ValidationContext.Require(item.Operation.TryGetProperty("code", out var code) &&
                    code.ValueKind == JsonValueKind.Number, item.Pointer + "/code",
                    "document.error_code", "An OpenRPC error requires an integer code.");
                var number = JsonNumber.Read(code, budget, item.Pointer + "/code");
                ValidationContext.Require(number.IsInteger, item.Pointer + "/code",
                    "document.error_code", "An OpenRPC error code must be an integer.");
                ValidationContext.Require(codes.Add(number), item.Pointer + "/code",
                    "document.error_duplicate", "OpenRPC errors must have unique integer codes.");
                ValidationContext.Require(item.Operation.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String, item.Pointer + "/message",
                    "document.error_message", "An OpenRPC error requires a message string.");
            }
        }
        return await ValidateOpenRpcExamplesAsync(location, references, budget, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<ApiOperationResolutionResult?> ValidateOpenRpcExamplesAsync(
        OperationLocation location, ReferenceContext references, ValidationContext budget,
        CancellationToken cancellationToken)
    {
        if (!location.Operation.TryGetProperty("examples", out var examples)) { return null; }
        var path = location.Pointer + "/examples";
        ValidationContext.Require(examples.ValueKind == JsonValueKind.Array, path,
            "document.examples", "OpenRPC examples must be an array.");
        var index = 0;
        foreach (var example in examples.EnumerateArray())
        {
            budget.Work(path: path);
            var resolved = await ResolveOperationAsync(example, location.Source,
                path + "/" + (index++).ToString(CultureInfo.InvariantCulture), references,
                new(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null) { return resolved.Failure; }
            var pairing = resolved.Operation!;
            RequireOpenRpcString(pairing.Operation, "name", pairing.Pointer, "document.example_pairing_name");
            ValidateOpenRpcOptionalString(pairing.Operation, "description", pairing.Pointer);
            ValidationContext.Require(pairing.Operation.TryGetProperty("params", out var parameters) &&
                parameters.ValueKind == JsonValueKind.Array, pairing.Pointer + "/params",
                "document.example_params", "An OpenRPC example pairing requires a params array.");
            var parameterIndex = 0;
            foreach (var parameter in parameters.EnumerateArray())
            {
                var failure = await ValidateOpenRpcExampleAsync(parameter, pairing.Source,
                    pairing.Pointer + "/params/" + (parameterIndex++).ToString(CultureInfo.InvariantCulture),
                    references, budget, cancellationToken).ConfigureAwait(false);
                if (failure is not null) { return failure; }
            }
            if (pairing.Operation.TryGetProperty("result", out var result))
            {
                var failure = await ValidateOpenRpcExampleAsync(result, pairing.Source, pairing.Pointer + "/result",
                    references, budget, cancellationToken).ConfigureAwait(false);
                if (failure is not null) { return failure; }
            }
        }
        return null;
    }

    private static async ValueTask<ApiOperationResolutionResult?> ValidateOpenRpcExampleAsync(
        JsonElement example, ReferencedDocument source, string path, ReferenceContext references,
        ValidationContext budget, CancellationToken cancellationToken)
    {
        budget.Work(path: path);
        var resolved = await ResolveOperationAsync(example, source, path, references,
            new(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) { return resolved.Failure; }
        var item = resolved.Operation!;
        RequireOpenRpcString(item.Operation, "name", item.Pointer, "document.example_name");
        ValidateOpenRpcOptionalString(item.Operation, "summary", item.Pointer);
        ValidateOpenRpcOptionalString(item.Operation, "description", item.Pointer);
        ValidationContext.Require(item.Operation.TryGetProperty("value", out _), item.Pointer + "/value",
            "document.example_value", "An OpenRPC example requires an embedded literal value.");
        ValidationContext.Require(!item.Operation.TryGetProperty("externalValue", out _),
            item.Pointer + "/externalValue", "document.example_value_conflict",
            "OpenRPC example value and externalValue are mutually exclusive.");
        return null;
    }

    private static void ValidateOpenRpcServers(JsonElement value, string path, ValidationContext budget)
    {
        if (!value.TryGetProperty("servers", out var servers)) { return; }
        ValidationContext.Require(servers.ValueKind == JsonValueKind.Array, path + "/servers",
            "document.servers", "OpenRPC servers must be an array.");
        var index = 0;
        foreach (var server in servers.EnumerateArray())
        {
            ValidateOpenRpcServer(server,
                path + "/servers/" + (index++).ToString(CultureInfo.InvariantCulture), budget);
        }
    }

    private static void ValidateOpenRpcServer(JsonElement server, string path, ValidationContext budget)
    {
        budget.Work(path: path);
        ValidationContext.Require(server.ValueKind == JsonValueKind.Object, path,
            "document.server", "An OpenRPC server must be an object.");
        RequireOpenRpcString(server, "url", path, "document.server_url");
        ValidateOpenRpcOptionalString(server, "name", path);
        ValidateOpenRpcOptionalString(server, "summary", path);
        ValidateOpenRpcOptionalString(server, "description", path);
        if (!server.TryGetProperty("variables", out var variables)) { return; }
        ValidationContext.Require(variables.ValueKind == JsonValueKind.Object, path + "/variables",
            "document.server_variables", "OpenRPC server variables must be an object.");
        foreach (var variable in variables.EnumerateObject())
        {
            var variablePath = path + "/variables/" + Escape(variable.Name);
            budget.Work(path: variablePath);
            ValidationContext.Require(variable.Value.ValueKind == JsonValueKind.Object, variablePath,
                "document.server_variable", "An OpenRPC server variable must be an object.");
            RequireOpenRpcString(variable.Value, "default", variablePath, "document.server_default");
            ValidateOpenRpcOptionalString(variable.Value, "description", variablePath);
            if (!variable.Value.TryGetProperty("enum", out var values)) { continue; }
            ValidationContext.Require(values.ValueKind == JsonValueKind.Array, variablePath + "/enum",
                "document.server_enum", "An OpenRPC server variable enum must be a string array.");
            var index = 0;
            foreach (var value in values.EnumerateArray())
            {
                budget.Work(path: variablePath);
                ValidationContext.Require(value.ValueKind == JsonValueKind.String,
                    variablePath + "/enum/" + (index++).ToString(CultureInfo.InvariantCulture),
                    "document.server_enum", "OpenRPC server variable enum values must be strings.");
            }
        }
    }

    private static string ValidateOpenRpcDescriptor(OperationLocation location)
    {
        var value = location.Operation;
        var path = location.Pointer;
        ValidationContext.Require(value.TryGetProperty("name", out var name) &&
            name.ValueKind == JsonValueKind.String, path + "/name", "document.content_name",
            "An OpenRPC content descriptor requires a name string.");
        ValidationContext.Require(value.TryGetProperty("schema", out var schema) &&
            schema.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False,
            path + "/schema", "document.content_schema",
            "An OpenRPC content descriptor requires an object or boolean schema.");
        ValidateOpenRpcAnnotations(value, path);
        if (value.TryGetProperty("required", out var required))
        {
            ValidationContext.Require(required.ValueKind is JsonValueKind.True or JsonValueKind.False,
                path + "/required", "document.content_required",
                "An OpenRPC content descriptor required flag must be a boolean.");
        }
        return name.GetString()!;
    }

    private static void ValidateOpenRpcAnnotations(JsonElement value, string path)
    {
        foreach (var property in new[] { "summary", "description" })
        {
            if (value.TryGetProperty(property, out var text))
            {
                ValidationContext.Require(text.ValueKind == JsonValueKind.String, path + "/" + property,
                    "document.annotation", "OpenRPC summary and description fields must be strings.");
            }
        }
        if (value.TryGetProperty("deprecated", out var deprecated))
        {
            ValidationContext.Require(deprecated.ValueKind is JsonValueKind.True or JsonValueKind.False,
                path + "/deprecated", "document.deprecated", "OpenRPC deprecated must be a boolean.");
        }
    }

    private static void RequireOpenRpcString(JsonElement value, string member, string path, string code)
    {
        ValidationContext.Require(value.TryGetProperty(member, out var text) &&
            text.ValueKind == JsonValueKind.String, path + "/" + member, code,
            $"An OpenRPC {member} string is required.");
    }

    private static void ValidateOpenRpcOptionalString(JsonElement value, string member, string path)
    {
        if (value.TryGetProperty(member, out var text))
        {
            ValidationContext.Require(text.ValueKind == JsonValueKind.String, path + "/" + member,
                "document.annotation", $"OpenRPC {member} must be a string.");
        }
    }

    private static void ValidateOpenRpcDocumentation(JsonElement value, string path)
    {
        if (!value.TryGetProperty("externalDocs", out var documentation)) { return; }
        var documentationPath = path + "/externalDocs";
        ValidationContext.Require(documentation.ValueKind == JsonValueKind.Object, documentationPath,
            "document.external_docs", "OpenRPC externalDocs must be an object.");
        RequireOpenRpcString(documentation, "url", documentationPath, "document.external_docs_url");
        ValidateOpenRpcUrl(documentation, "url", documentationPath);
        ValidateOpenRpcOptionalString(documentation, "description", documentationPath);
    }

    private static void ValidateOpenRpcUrl(JsonElement value, string member, string path)
    {
        if (!value.TryGetProperty(member, out var url)) { return; }
        var text = url.GetString()!;
        ValidationContext.Require(!text.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) &&
            Uri.IsWellFormedUriString(text, UriKind.Absolute),
            path + "/" + member, "document.url", "An OpenRPC metadata URL must be a well-formed absolute URL.");
    }
}
