// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.RegularExpressions;

namespace XRegistry.Validation;

public static partial class ApiOperationDocumentResolver
{
    [GeneratedRegex(@"\A(?:_*[A-Za-z0-9][A-Za-z0-9_]*\.)*_*[A-Za-z0-9][A-Za-z0-9_]*#_*[A-Za-z0-9][A-Za-z0-9_]*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SmithyRootShapeId();

    private static ApiOperationResolutionResult ResolveSmithyOperation(JsonElement root, string shapeId,
        Uri? documentUri, ValidationContext budget)
    {
        try
        {
            ValidationContext.Require(root.ValueKind == JsonValueKind.Object, "$", "document.root",
                "A Smithy JSON AST must be an object.");
            ValidationContext.Require(root.TryGetProperty("smithy", out var version) &&
                version.ValueKind == JsonValueKind.String && version.GetString() is "2" or "2.0",
                "$/smithy", "document.smithy_version", "The Smithy AST version must be '2' or '2.0'.");
            ValidationContext.Require(root.TryGetProperty("shapes", out var shapes) &&
                shapes.ValueKind == JsonValueKind.Object, "$/shapes", "document.shapes",
                "A Smithy API document must contain a shapes object.");
            ValidationContext.Require(IsSmithyShapeId(shapeId), "$/selector", "selector.shape_id",
                "A Smithy operation selector must be an absolute root shape ID.");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasOperation = false;
            foreach (var shape in shapes.EnumerateObject())
            {
                budget.Work();
                var path = "$/shapes/" + Escape(shape.Name);
                ValidationContext.Require(shape.Value.ValueKind == JsonValueKind.Object &&
                    shape.Value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String,
                    path, "document.shape", "A Smithy shape must be an object with a type.");
                var kind = shape.Value.GetProperty("type").GetString();
                if (kind == "apply" || shape.Value.TryGetProperty("mixins", out _))
                {
                    return Failure(ApiOperationResolutionStatus.Unsupported, "smithy.assembly",
                        "Supply an assembled Smithy AST without apply entries or mixins.", path);
                }
                ValidationContext.Require(IsSmithyShapeId(shape.Name) && names.Add(shape.Name),
                    path, "document.shape_id", "Smithy root shape IDs must be absolute and case-insensitively unique.");
                hasOperation |= kind == "operation";
            }
            ValidationContext.Require(hasOperation, "$/shapes", "document.operations",
                "A Smithy API model must contain an operation shape.");
            if (!shapes.TryGetProperty(shapeId, out var operation))
            {
                return Failure(ApiOperationResolutionStatus.NotFound, "selection.not_found",
                    "The exact Smithy operation shape ID is absent.");
            }
            if (operation.GetProperty("type").GetString() != "operation")
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "selection.not_operation",
                    "The selected Smithy shape is not an operation.", "$/shapes/" + Escape(shapeId));
            }
            new SmithyClosure(shapes, budget).Visit(shapeId, 0, "operation");
            return Selected(operation, "$/shapes/" + Escape(shapeId), documentUri);
        }
        catch (ValidationFailure failure)
        {
            return failure.Status == DocumentValidationStatus.Unsupported
                ? Failure(ApiOperationResolutionStatus.Unsupported, failure.Diagnostic.Code,
                    failure.Diagnostic.Detail, failure.Diagnostic.Path)
                : ParseFailure(failure, failure.Diagnostic.Code, failure.Diagnostic.Path);
        }
    }

    private static bool IsSmithyShapeId(string value) =>
        SmithyRootShapeId().IsMatch(value) &&
        value.Split('.', '#').All(static part => part[0] is not (>= '0' and <= '9'));

    private sealed class SmithyClosure(JsonElement shapes, ValidationContext budget)
    {
        private readonly HashSet<string> _visited = new(StringComparer.Ordinal);

        internal void Visit(string id, int depth, string? expected = null, bool error = false, bool traitDefinition = false)
        {
            budget.Work();
            var path = "$/shapes/" + Escape(id);
            if (depth > Capabilities.MaxReferenceDepth)
            {
                ValidationContext.Fail(DocumentValidationStatus.Indeterminate, path, "limit.reference_depth",
                    "The Smithy shape dependency depth limit was exceeded.");
            }
            if (!shapes.TryGetProperty(id, out var shape))
            {
                var preludeType = id switch
                {
                    "smithy.api#Unit" => "structure",
                    "smithy.api#String" => "string",
                    "smithy.api#Blob" or "smithy.api#Boolean" or "smithy.api#Document" or
                    "smithy.api#Byte" or "smithy.api#Short" or "smithy.api#Integer" or "smithy.api#Long" or
                    "smithy.api#Float" or "smithy.api#Double" or "smithy.api#BigInteger" or
                    "smithy.api#BigDecimal" or "smithy.api#Timestamp" => "simple",
                    _ => null
                };
                if (preludeType is null)
                {
                    ValidationContext.Fail(DocumentValidationStatus.Indeterminate, path, "dependency.unresolved",
                        "The assembled Smithy AST does not supply a required shape.");
                }
                ValidationContext.Require(!error && !traitDefinition && (expected is null || expected == preludeType),
                    path, "document.target_type", "A Smithy reference targets the wrong shape type.");
                return;
            }
            var kind = shape.GetProperty("type").GetString();
            ValidationContext.Require(expected is null ? kind is not ("operation" or "service" or "resource") :
                kind == expected, path, "document.target_type", "A Smithy reference targets the wrong shape type.");
            ValidationContext.Require(traitDefinition || !shape.TryGetProperty("traits", out var applied) ||
                applied.ValueKind != JsonValueKind.Object || !applied.TryGetProperty("smithy.api#trait", out _),
                path, "document.target_type", "A Smithy data reference cannot target a trait definition.");
            if (error)
            {
                ValidationContext.Require(shape.TryGetProperty("traits", out var traits) &&
                    traits.ValueKind == JsonValueKind.Object && traits.TryGetProperty("smithy.api#error", out var value) &&
                    value.ValueKind == JsonValueKind.String && value.GetString() is "client" or "server",
                    path, "document.error_trait", "A declared Smithy error must be a structure marked with an error trait.");
            }
            if (!_visited.Add(id)) { return; }
            Traits(shape, path, depth);
            switch (kind)
            {
                case "operation":
                    foreach (var name in new[] { "input", "output" })
                    {
                        if (shape.TryGetProperty(name, out var target))
                        {
                            Reference(target, path + "/" + name, depth, "structure");
                        }
                    }
                    if (shape.TryGetProperty("errors", out var errors))
                    {
                        ValidationContext.Require(errors.ValueKind == JsonValueKind.Array,
                            path + "/errors", "document.errors", "Smithy operation errors must be an array.");
                        foreach (var target in errors.EnumerateArray())
                        {
                            Reference(target, path + "/errors", depth, "structure", error: true);
                        }
                    }
                    break;
                case "structure":
                case "union":
                case "enum":
                case "intEnum":
                    if (shape.TryGetProperty("members", out var members))
                    {
                        ValidationContext.Require(members.ValueKind == JsonValueKind.Object,
                            path + "/members", "document.members", "Smithy members must be an object.");
                        ValidationContext.Require(kind == "structure" || members.EnumerateObject().Any(),
                            path + "/members", "document.members", "Smithy union and enum shapes require at least one member.");
                        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var member in members.EnumerateObject())
                        {
                            ValidationContext.Require(IsSmithyShapeId("example#" + member.Name) && names.Add(member.Name),
                                path + "/members", "document.member_name", "Smithy member names must be valid and case-insensitively unique.");
                            Reference(member.Value, path + "/members/" + Escape(member.Name), depth);
                        }
                    }
                    else
                    {
                        ValidationContext.Require(kind == "structure", path, "document.members",
                            "Smithy union and enum shapes require members.");
                    }
                    break;
                case "list":
                    RequiredReference(shape, "member", path, depth);
                    break;
                case "map":
                    RequiredReference(shape, "key", path, depth, "string");
                    RequiredReference(shape, "value", path, depth);
                    break;
                case "blob": case "boolean": case "document": case "string": case "byte": case "short":
                case "integer": case "long": case "float": case "double": case "bigInteger":
                case "bigDecimal": case "timestamp":
                    break;
                default:
                    ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, "smithy.shape_type",
                        "The required Smithy shape type is not supported.");
                    break;
            }
        }

        private void RequiredReference(JsonElement shape, string name, string path, int depth, string? expected = null)
        {
            ValidationContext.Require(shape.TryGetProperty(name, out var value), path + "/" + name,
                "document.target", "The Smithy shape is missing a required member reference.");
            Reference(value, path + "/" + name, depth, expected);
        }

        private void Reference(JsonElement value, string path, int depth, string? expected = null, bool error = false)
        {
            budget.Work();
            ValidationContext.Require(value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.String &&
                IsSmithyShapeId(target.GetString()!), path, "document.target",
                "A Smithy shape reference must have an absolute root target ID.");
            Traits(value, path, depth);
            Visit(value.GetProperty("target").GetString()!, depth + 1, expected, error);
        }

        private void Traits(JsonElement shape, string path, int depth)
        {
            if (!shape.TryGetProperty("traits", out var traits)) { return; }
            ValidationContext.Require(traits.ValueKind == JsonValueKind.Object,
                path + "/traits", "document.traits", "Smithy traits must be an object.");
            foreach (var trait in traits.EnumerateObject())
            {
                budget.Work();
                ValidationContext.Require(IsSmithyShapeId(trait.Name), path + "/traits", "document.trait_id",
                    "A Smithy trait ID must be an absolute root shape ID.");
                if (trait.Name is "smithy.api#error" or "smithy.api#documentation" or "smithy.api#required" or
                    "smithy.api#input" or "smithy.api#output" or "smithy.api#readonly" or "smithy.api#idempotent" or
                    "smithy.api#default" or "smithy.api#enumValue" or "smithy.api#trait") { continue; }
                if (shapes.TryGetProperty(trait.Name, out var definition))
                {
                    ValidationContext.Require(definition.TryGetProperty("traits", out var markers) &&
                        markers.ValueKind == JsonValueKind.Object && markers.TryGetProperty("smithy.api#trait", out _),
                        path + "/traits", "document.trait_definition", "An applied custom trait must target a trait definition.");
                }
                Visit(trait.Name, depth + 1, traitDefinition: true);
            }
        }
    }
}
