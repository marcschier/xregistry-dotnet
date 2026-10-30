// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;

namespace XRegistry.Validation;

public static partial class ApiOperationDocumentResolver
{
    private static async ValueTask<ApiOperationResolutionResult?> ValidateOpenRpcMethodAsync(
        OperationLocation location, ReferenceContext references, ValidationContext budget,
        CancellationToken cancellationToken)
    {
        var method = location.Operation;
        var path = location.Pointer;
        budget.Work(path: path);
        ValidateOpenRpcAnnotations(method, path);
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
        return null;
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
}
