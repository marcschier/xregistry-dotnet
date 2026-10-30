// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;

namespace XRegistry.Validation;

public static partial class ApiOperationDocumentResolver
{
    private static ApiOperationResolutionResult? ValidateAvroProtocolDeclarations(JsonElement root,
        CancellationToken cancellationToken)
    {
        var protocol = root.GetProperty("protocol").GetString()!;
        if (!AvroFullName(protocol))
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.protocol",
                "The Avro protocol name must follow fullname syntax.", "$/protocol");
        }
        var ns = root.TryGetProperty("namespace", out var namespaceValue) ? namespaceValue.GetString()! : "";
        if (!protocol.Contains('.') && ns.Length > 0 && !AvroFullName(ns))
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.namespace",
                "An Avro namespace must be empty or a dot-separated sequence of names.", "$/namespace");
        }
        if (root.TryGetProperty("doc", out var doc) && doc.ValueKind != JsonValueKind.String)
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.doc",
                "Avro protocol documentation must be a string.", "$/doc");
        }
        if (root.TryGetProperty("types", out var types))
        {
            if (types.ValueKind != JsonValueKind.Array)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.types",
                    "Protocol types must be an array of named type declarations.", "$/types");
            }
            var dot = protocol.LastIndexOf('.');
            if (dot >= 0) { ns = protocol[..dot]; }
            var names = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var type in types.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = "$/types/" + (index++).ToString(CultureInfo.InvariantCulture);
                if (type.ValueKind != JsonValueKind.Object ||
                    !type.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String ||
                    kind.GetString() is not ("record" or "error" or "enum" or "fixed"))
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.type_declaration",
                        "A protocol types entry must declare a record, error, enum or fixed type.", path);
                }
                if (!type.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                    !AvroFullName(name.GetString()!))
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.type_name",
                        "A named Avro type requires a valid fullname.", path + "/name");
                }
                var fullName = name.GetString()!;
                if (type.TryGetProperty("namespace", out var declaredNamespace) &&
                    declaredNamespace.ValueKind != JsonValueKind.String)
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.namespace",
                        "A type namespace must be a string.", path + "/namespace");
                }
                if (!fullName.Contains('.'))
                {
                    var typeNamespace = type.TryGetProperty("namespace", out declaredNamespace)
                        ? declaredNamespace.GetString()! : ns;
                    if (typeNamespace.Length > 0 && !AvroFullName(typeNamespace))
                    {
                        return Failure(ApiOperationResolutionStatus.Invalid, "document.namespace",
                            "An Avro namespace must be empty or a dot-separated sequence of names.", path + "/namespace");
                    }
                    fullName = typeNamespace.Length == 0 ? fullName : typeNamespace + "." + fullName;
                }
                if (!names.Add(fullName))
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.duplicate_type",
                        "Protocol named types must have unique fullnames.", path + "/name");
                }
            }
        }
        return null;
    }

    private static ApiOperationResolutionResult? ValidateAvroRequest(
        JsonElement message, JsonElement request, string path, CancellationToken cancellationToken)
    {
        if (message.TryGetProperty("doc", out var documentation) && documentation.ValueKind != JsonValueKind.String)
        {
            return Failure(ApiOperationResolutionStatus.Invalid, "document.doc",
                "Avro message documentation must be a string.", path + "/doc");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var parameter in request.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameterPath = path + "/request/" + (index++).ToString(CultureInfo.InvariantCulture);
            if (parameter.ValueKind != JsonValueKind.Object)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.parameter",
                    "An Avro request parameter must be a field declaration.", parameterPath);
            }
            if (!parameter.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !SchemaJson.Identifier(name.GetString()!))
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.parameter_name",
                    "An Avro request parameter requires a valid field name.", parameterPath + "/name");
            }
            if (!names.Add(name.GetString()!))
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.duplicate_parameter",
                    "Avro request parameter names must be unique.", parameterPath + "/name");
            }
            if (!parameter.TryGetProperty("type", out var type) ||
                type.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.parameter_type",
                    "An Avro request parameter requires a type schema.", parameterPath + "/type");
            }
            if (parameter.TryGetProperty("doc", out var doc) && doc.ValueKind != JsonValueKind.String)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.doc",
                    "Avro field documentation must be a string.", parameterPath + "/doc");
            }
            if (parameter.TryGetProperty("order", out var order) &&
                (order.ValueKind != JsonValueKind.String || order.GetString() is not ("ascending" or "descending" or "ignore")))
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.parameter_order",
                    "A field order must be ascending, descending or ignore.", parameterPath + "/order");
            }
            if (parameter.TryGetProperty("aliases", out var aliases))
            {
                if (aliases.ValueKind != JsonValueKind.Array ||
                    aliases.EnumerateArray().Any(static alias => alias.ValueKind != JsonValueKind.String))
                {
                    return Failure(ApiOperationResolutionStatus.Invalid, "document.parameter_aliases",
                        "Field aliases must be an array of strings.", parameterPath + "/aliases");
                }
            }
        }
        return null;
    }

    private static bool AvroFullName(string name) => name.Split('.').All(SchemaJson.Identifier);
}
