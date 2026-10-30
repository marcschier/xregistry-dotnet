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

    [GeneratedRegex(@"\A(?:/(?:[^~]|~[01])*)*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SmithyTraitDiffPointer();

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
            var closure = new SmithyClosure(shapes, budget);
            closure.Visit(shapeId, 0, "operation");
            closure.ValidateRecursion();
            closure.ValidateInputOutputUsage();
            closure.ValidateDefaults();
            closure.ValidateStructuralExclusivity();
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

    private static string? SmithyPreludeKind(string id) => id switch
    {
        "smithy.api#Unit" => "structure",
        "smithy.api#String" => "string",
        "smithy.api#Blob" => "blob",
        "smithy.api#Boolean" or "smithy.api#PrimitiveBoolean" => "boolean",
        "smithy.api#Document" => "document",
        "smithy.api#Byte" or "smithy.api#PrimitiveByte" => "byte",
        "smithy.api#Short" or "smithy.api#PrimitiveShort" => "short",
        "smithy.api#Integer" or "smithy.api#PrimitiveInteger" => "integer",
        "smithy.api#Long" or "smithy.api#PrimitiveLong" => "long",
        "smithy.api#Float" or "smithy.api#PrimitiveFloat" => "float",
        "smithy.api#Double" or "smithy.api#PrimitiveDouble" => "double",
        "smithy.api#BigInteger" => "bigInteger",
        "smithy.api#BigDecimal" => "bigDecimal",
        "smithy.api#Timestamp" => "timestamp",
        _ => null
    };

    private sealed class SmithyClosure(JsonElement shapes, ValidationContext budget)
    {
        private readonly Dictionary<string, JsonElement> _visited = new(StringComparer.Ordinal);

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
                var preludeType = SmithyPreludeKind(id);
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
                kind == expected || expected == "string" && kind == "enum",
                path, "document.target_type", "A Smithy reference targets the wrong shape type.");
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
            if (!_visited.TryAdd(id, shape)) { return; }
            Traits(shape, path, depth, kind);
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
                        var index = 0;
                        foreach (var target in errors.EnumerateArray())
                        {
                            Reference(target, path + "/errors/" +
                                (index++).ToString(System.Globalization.CultureInfo.InvariantCulture),
                                depth, "structure", error: true);
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
                        var enumStrings = new HashSet<string>(StringComparer.Ordinal);
                        var enumIntegers = new HashSet<JsonNumber>();
                        foreach (var member in members.EnumerateObject())
                        {
                            ValidationContext.Require(IsSmithyShapeId("example#" + member.Name) && names.Add(member.Name),
                                path + "/members", "document.member_name", "Smithy member names must be valid and case-insensitively unique.");
                            var memberPath = path + "/members/" + Escape(member.Name);
                            Reference(member.Value, memberPath, depth, memberContainer: kind);
                            if (kind is "enum" or "intEnum")
                            {
                                ValidationContext.Require(member.Value.GetProperty("target").GetString() == "smithy.api#Unit",
                                    memberPath, "document.enum_target", "Smithy enum members must target smithy.api#Unit.");
                                var value = member.Value.TryGetProperty("traits", out var traits) &&
                                    traits.TryGetProperty("smithy.api#enumValue", out var enumValue) ? enumValue : default;
                                if (kind == "enum")
                                {
                                    ValidationContext.Require(value.ValueKind == JsonValueKind.Undefined ||
                                        value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 0,
                                        memberPath, "document.enum_value", "A Smithy enum value must be a nonempty string.");
                                    ValidationContext.Require(enumStrings.Add(value.ValueKind == JsonValueKind.Undefined ?
                                        member.Name : value.GetString()!), memberPath, "document.enum_value",
                                        "Smithy enum values must be unique.");
                                }
                                else
                                {
                                    ValidationContext.Require(value.ValueKind == JsonValueKind.Number,
                                        memberPath, "document.enum_value",
                                        "A Smithy intEnum member requires a signed 32-bit integer enumValue.");
                                    var number = JsonNumber.Read(value, budget, memberPath);
                                    ValidationContext.Require(number.IsInteger &&
                                        number.CompareTo(new(true, "2147483648", 0)) >= 0 &&
                                        number.CompareTo(new(false, "2147483647", 0)) <= 0,
                                        memberPath, "document.enum_value",
                                        "A Smithy intEnum value must fit a signed 32-bit integer.");
                                    ValidationContext.Require(enumIntegers.Add(number), memberPath,
                                        "document.enum_value", "Smithy intEnum values must be unique.");
                                }
                            }
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

        internal void ValidateRecursion()
        {
            var containers = new Dictionary<string, (bool AnyTarget, List<string> Targets)>(StringComparer.Ordinal);
            var values = new Dictionary<string, (bool AnyTarget, List<string> Targets)>(StringComparer.Ordinal);
            foreach (var (id, shape) in _visited)
            {
                budget.Work();
                var kind = shape.GetProperty("type").GetString();
                if (kind is "list" or "map")
                {
                    var targets = new List<string>();
                    foreach (var name in kind == "list" ? new[] { "member" } : new[] { "key", "value" })
                    {
                        targets.Add(shape.GetProperty(name).GetProperty("target").GetString()!);
                    }
                    containers.Add(id, (false, targets));
                }
                else if (kind is "structure" or "union")
                {
                    var targets = new List<string>();
                    if (shape.TryGetProperty("members", out var members))
                    {
                        foreach (var member in members.EnumerateObject())
                        {
                            budget.Work();
                            if (kind == "union" || member.Value.TryGetProperty("traits", out var traits) &&
                                traits.TryGetProperty("smithy.api#required", out _))
                            {
                                targets.Add(member.Value.GetProperty("target").GetString()!);
                            }
                        }
                    }
                    values.Add(id, (kind == "union", targets));
                }
            }
            RequireTerminatingShapes(containers,
                "A recursive Smithy list or map must pass through a structure or union.");
            RequireTerminatingShapes(values,
                "Recursive Smithy structures and unions must permit a finite value.");
        }

        private void RequireTerminatingShapes(
            Dictionary<string, (bool AnyTarget, List<string> Targets)> nodes, string detail)
        {
            var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
            var parents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var ready = new Queue<string>();
            foreach (var (id, node) in nodes)
            {
                budget.Work();
                var pending = 0;
                var terminal = false;
                foreach (var target in node.Targets)
                {
                    budget.Work();
                    if (!nodes.ContainsKey(target))
                    {
                        terminal = true;
                        continue;
                    }
                    pending++;
                    if (!parents.TryGetValue(target, out var references))
                    {
                        references = [];
                        parents.Add(target, references);
                    }
                    references.Add(id);
                }
                remaining.Add(id, pending);
                if (node.AnyTarget ? terminal : pending == 0) { ready.Enqueue(id); }
            }

            var terminating = new HashSet<string>(StringComparer.Ordinal);
            while (ready.TryDequeue(out var id))
            {
                budget.Work();
                if (!terminating.Add(id) || !parents.TryGetValue(id, out var references)) { continue; }
                foreach (var parent in references)
                {
                    budget.Work();
                    if (nodes[parent].AnyTarget || --remaining[parent] == 0) { ready.Enqueue(parent); }
                }
            }
            foreach (var id in nodes.Keys)
            {
                budget.Work();
                ValidationContext.Require(terminating.Contains(id), "$/shapes/" + Escape(id),
                    "document.recursion", detail);
            }
        }

        internal void ValidateInputOutputUsage()
        {
            var directions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (id, shape) in _visited)
            {
                budget.Work();
                if (shape.GetProperty("type").GetString() != "structure" ||
                    !shape.TryGetProperty("traits", out var traits)) { continue; }
                if (traits.TryGetProperty("smithy.api#input", out _)) { directions.Add(id, "input"); }
                else if (traits.TryGetProperty("smithy.api#output", out _)) { directions.Add(id, "output"); }
            }
            if (directions.Count == 0) { return; }
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var shape in shapes.EnumerateObject())
            {
                budget.Work();
                var kind = shape.Value.GetProperty("type").GetString();
                var path = "$/shapes/" + Escape(shape.Name);
                CheckTraits(shape.Value, path);
                switch (kind)
                {
                    case "operation":
                        CheckSingle("input");
                        CheckSingle("output");
                        CheckArray("errors");
                        break;
                    case "structure": case "union": case "enum": case "intEnum":
                        CheckMap("members");
                        break;
                    case "list":
                        CheckSingle("member");
                        break;
                    case "map":
                        CheckSingle("key");
                        CheckSingle("value");
                        break;
                    case "service":
                        CheckArray("operations");
                        CheckArray("resources");
                        CheckArray("errors");
                        break;
                    case "resource":
                        CheckMap("identifiers");
                        CheckMap("properties");
                        foreach (var name in new[] { "create", "put", "read", "update", "delete", "list" })
                        {
                            CheckSingle(name);
                        }
                        CheckArray("operations");
                        CheckArray("collectionOperations");
                        CheckArray("resources");
                        break;
                }

                void CheckTarget(string target, string referencePath, string? slot = null)
                {
                    budget.Work(path: referencePath);
                    if (!directions.TryGetValue(target, out var direction)) { return; }
                    ValidationContext.Require(kind == "operation" && slot == direction, referencePath,
                        "document.input_output_usage",
                        "A Smithy input or output structure can only be referenced in its matching operation slot.");
                    ValidationContext.Require(owners.TryAdd(target, shape.Name) || owners[target] == shape.Name,
                        referencePath, "document.input_output_usage",
                        "A specialized Smithy input or output structure cannot be shared by multiple operations.");
                }

                void CheckTraits(JsonElement value, string referencePath)
                {
                    if (!value.TryGetProperty("traits", out var traits) || traits.ValueKind != JsonValueKind.Object) { return; }
                    foreach (var trait in traits.EnumerateObject())
                    {
                        CheckTarget(trait.Name, referencePath + "/traits/" + Escape(trait.Name));
                    }
                }

                void CheckReference(JsonElement value, string referencePath, string? slot = null)
                {
                    budget.Work(path: referencePath);
                    if (value.ValueKind != JsonValueKind.Object) { return; }
                    CheckTraits(value, referencePath);
                    if (value.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.String)
                    {
                        CheckTarget(target.GetString()!, referencePath, slot);
                    }
                }

                void CheckSingle(string name)
                {
                    if (shape.Value.TryGetProperty(name, out var value)) { CheckReference(value, path + "/" + name, name); }
                }

                void CheckArray(string name)
                {
                    if (!shape.Value.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array) { return; }
                    var index = 0;
                    foreach (var value in values.EnumerateArray())
                    {
                        CheckReference(value, path + "/" + name + "/" +
                            (index++).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                void CheckMap(string name)
                {
                    if (!shape.Value.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Object) { return; }
                    foreach (var value in values.EnumerateObject())
                    {
                        CheckReference(value.Value, path + "/" + name + "/" + Escape(value.Name));
                    }
                }
            }
        }

        internal void ValidateDefaults()
        {
            foreach (var (id, shape) in _visited)
            {
                budget.Work();
                var path = "$/shapes/" + Escape(id);
                var kind = shape.GetProperty("type").GetString()!;
                if (TryDefault(shape, out var value)) { ValidateDefault(kind, shape, value, path, member: false); }
                if (kind != "structure" || !shape.TryGetProperty("members", out var members)) { continue; }
                foreach (var member in members.EnumerateObject())
                {
                    budget.Work();
                    var targetId = member.Value.GetProperty("target").GetString()!;
                    var target = shapes.TryGetProperty(targetId, out var definition) ? definition : default;
                    var targetKind = target.ValueKind == JsonValueKind.Object
                        ? target.GetProperty("type").GetString()! : SmithyPreludeKind(targetId)!;
                    var memberPath = path + "/members/" + Escape(member.Name);
                    var hasDefault = TryDefault(member.Value, out var memberDefault);
                    if (target.ValueKind == JsonValueKind.Object && TryDefault(target, out var targetDefault))
                    {
                        ValidationContext.Require(hasDefault && (memberDefault.ValueKind == JsonValueKind.Null ||
                            SchemaJson.Equal(memberDefault, targetDefault, budget)), memberPath + "/traits/smithy.api#default",
                            "document.default_repeat",
                            "A structure member must repeat its target's default or explicitly override it with null.");
                    }
                    else if (target.ValueKind == JsonValueKind.Undefined &&
                        targetId is "smithy.api#PrimitiveBoolean" or "smithy.api#PrimitiveByte" or
                            "smithy.api#PrimitiveShort" or "smithy.api#PrimitiveInteger" or
                            "smithy.api#PrimitiveLong" or "smithy.api#PrimitiveFloat" or "smithy.api#PrimitiveDouble")
                    {
                        ValidationContext.Require(hasDefault && (memberDefault.ValueKind == JsonValueKind.Null ||
                            (targetKind == "boolean" ? memberDefault.ValueKind == JsonValueKind.False :
                                memberDefault.ValueKind == JsonValueKind.Number &&
                                JsonNumber.Read(memberDefault, budget, memberPath + "/traits/smithy.api#default").IsZero)),
                            memberPath + "/traits/smithy.api#default", "document.default_repeat",
                            "A structure member must repeat its primitive prelude default or explicitly override it with null.");
                    }
                    if (hasDefault) { ValidateDefault(targetKind, target, memberDefault, memberPath, member: true); }
                }
            }
        }

        internal void ValidateStructuralExclusivity()
        {
            foreach (var (id, shape) in _visited)
            {
                budget.Work();
                if (shape.GetProperty("type").GetString() != "structure" ||
                    !shape.TryGetProperty("members", out var members)) { continue; }
                var owners = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var member in members.EnumerateObject())
                {
                    var path = "$/shapes/" + Escape(id) + "/members/" + Escape(member.Name);
                    budget.Work(path: path);
                    Check(member.Value, "member", member.Name, path);
                    if (shapes.TryGetProperty(member.Value.GetProperty("target").GetString()!, out var target))
                    {
                        Check(target, "target", member.Name, path);
                    }
                }

                void Check(JsonElement value, string mode, string memberName, string path)
                {
                    if (!value.TryGetProperty("traits", out var traits)) { return; }
                    foreach (var trait in traits.EnumerateObject())
                    {
                        budget.Work(path: path);
                        if (!shapes.TryGetProperty(trait.Name, out var definition) ||
                            !definition.TryGetProperty("traits", out var markers) ||
                            markers.ValueKind != JsonValueKind.Object ||
                            !markers.TryGetProperty("smithy.api#trait", out var marker) ||
                            marker.ValueKind != JsonValueKind.Object ||
                            !marker.TryGetProperty("structurallyExclusive", out var exclusive) ||
                            exclusive.ValueKind != JsonValueKind.String ||
                            exclusive.GetString() != mode) { continue; }
                        ValidationContext.Require(owners.TryAdd(trait.Name, memberName) ||
                            owners[trait.Name] == memberName, path, "document.trait_exclusivity",
                            "A structurally exclusive Smithy trait can select only one member of a structure.");
                    }
                }
            }
        }

        private static bool TryDefault(JsonElement shape, out JsonElement value)
        {
            value = default;
            return shape.TryGetProperty("traits", out var traits) && traits.ValueKind == JsonValueKind.Object &&
                traits.TryGetProperty("smithy.api#default", out value);
        }

        private void ValidateDefault(string kind, JsonElement target, JsonElement value, string path, bool member)
        {
            path += "/traits/smithy.api#default";
            budget.Work(path: path);
            ValidationContext.Require(kind is "blob" or "boolean" or "document" or "string" or
                "byte" or "short" or "integer" or "long" or "float" or "double" or
                "bigInteger" or "bigDecimal" or "timestamp" or "enum" or "intEnum" or "list" or "map",
                path, "document.trait_target", "Smithy defaults require a simple, list or map target.");
            if (value.ValueKind == JsonValueKind.Null)
            {
                ValidationContext.Require(member, path, "document.default_value",
                    "A root Smithy default cannot be null.");
                return;
            }
            if (value.ValueKind == JsonValueKind.String && kind is "float" or "double")
            {
                ValidationContext.Require(value.GetString() is "NaN" or "Infinity" or "-Infinity", path,
                    "document.default_value", "A string floating-point default must be NaN, Infinity or -Infinity.");
                return;
            }
            if (value.ValueKind == JsonValueKind.String && kind is "bigInteger" or "bigDecimal")
            {
                ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, "smithy.numeric_string_default",
                    "String-encoded Smithy numeric defaults are not supported.");
            }
            var valid = kind switch
            {
                "string" or "enum" or "blob" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "list" => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0,
                "map" => value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any(),
                "document" => value.ValueKind != JsonValueKind.Array && value.ValueKind != JsonValueKind.Object ||
                    value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0 ||
                    value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any(),
                "timestamp" => true,
                _ => value.ValueKind == JsonValueKind.Number
            };
            ValidationContext.Require(valid, path, "document.default_value",
                "The Smithy default value is incompatible with its target type.");
            if (kind == "timestamp")
            {
                ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, "smithy.timestamp_default",
                    "Timestamp default validation is not supported.");
            }
            if (kind == "blob")
            {
                var text = value.GetString()!;
                budget.Work(text.Length, path);
                ValidationContext.Require(Convert.TryFromBase64String(text, new byte[text.Length], out _),
                    path, "document.default_value", "A Smithy blob default must contain valid base64.");
            }
            if (kind is "byte" or "short" or "integer" or "long" or "bigInteger" or "intEnum")
            {
                var number = JsonNumber.Read(value, budget, path);
                var bounds = kind switch
                {
                    "byte" => ("128", "127"),
                    "short" => ("32768", "32767"),
                    "integer" or "intEnum" => ("2147483648", "2147483647"),
                    "long" => ("9223372036854775808", "9223372036854775807"),
                    _ => ((string, string)?)null
                };
                ValidationContext.Require(number.IsInteger && (bounds is null ||
                    number.CompareTo(new(true, bounds.Value.Item1, 0)) >= 0 &&
                    number.CompareTo(new(false, bounds.Value.Item2, 0)) <= 0), path, "document.default_value",
                    "A Smithy integer default must be integral and fit its target range.");
            }
            if (kind is "float" or "double")
            {
                ValidationContext.Require(kind == "float"
                    ? value.TryGetSingle(out var single) && float.IsFinite(single)
                    : value.TryGetDouble(out var number) && double.IsFinite(number),
                    path, "document.default_value", "A Smithy floating-point default must fit its target range.");
            }
            if (kind is "enum" or "intEnum")
            {
                var found = false;
                foreach (var item in target.GetProperty("members").EnumerateObject())
                {
                    budget.Work(path: path);
                    if (item.Value.TryGetProperty("traits", out var traits) &&
                        traits.TryGetProperty("smithy.api#enumValue", out var enumValue))
                    {
                        found |= SchemaJson.Equal(value, enumValue, budget);
                    }
                    else { found |= kind == "enum" && value.GetString() == item.Name; }
                }
                ValidationContext.Require(found, path, "document.default_value",
                    "A Smithy enum default must match a declared value.");
            }
        }

        private void RequiredReference(JsonElement shape, string name, string path, int depth, string? expected = null)
        {
            ValidationContext.Require(shape.TryGetProperty(name, out var value), path + "/" + name,
                "document.target", "The Smithy shape is missing a required member reference.");
            Reference(value, path + "/" + name, depth, expected,
                memberContainer: shape.GetProperty("type").GetString());
        }

        private void Reference(JsonElement value, string path, int depth, string? expected = null, bool error = false,
            string? memberContainer = null)
        {
            budget.Work();
            ValidationContext.Require(value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.String &&
                IsSmithyShapeId(target.GetString()!), path, "document.target",
                "A Smithy shape reference must have an absolute root target ID.");
            foreach (var property in value.EnumerateObject())
            {
                var propertyPath = path + "/" + Escape(property.Name);
                budget.Work(path: propertyPath);
                ValidationContext.Require(property.Name == "target" ||
                    memberContainer is not null && property.Name == "traits",
                    propertyPath, "document.reference_property",
                    "A Smithy shape reference permits only target; a member can also declare traits.");
            }
            if (memberContainer is not null) { Traits(value, path, depth, memberContainer: memberContainer); }
            Visit(value.GetProperty("target").GetString()!, depth + 1, expected, error);
        }

        private void Traits(JsonElement shape, string path, int depth, string? kind = null,
            string? memberContainer = null)
        {
            if (!shape.TryGetProperty("traits", out var traits)) { return; }
            ValidationContext.Require(traits.ValueKind == JsonValueKind.Object,
                path + "/traits", "document.traits", "Smithy traits must be an object.");
            foreach (var trait in traits.EnumerateObject())
            {
                budget.Work();
                ValidationContext.Require(IsSmithyShapeId(trait.Name), path + "/traits", "document.trait_id",
                    "A Smithy trait ID must be an absolute root shape ID.");
                var traitPath = path + "/traits/" + Escape(trait.Name);
                var targetAllowed = trait.Name switch
                {
                    "smithy.api#required" or "smithy.api#clientOptional" => memberContainer == "structure",
                    "smithy.api#addedDefault" => memberContainer == "structure" &&
                        traits.TryGetProperty("smithy.api#default", out _),
                    "smithy.api#sparse" => kind is "list" or "map",
                    "smithy.api#enumValue" => memberContainer is "enum" or "intEnum",
                    "smithy.api#input" or "smithy.api#output" or "smithy.api#error" => kind == "structure",
                    "smithy.api#readonly" or "smithy.api#idempotent" => kind == "operation",
                    "smithy.api#trait" => memberContainer is null && kind is
                        "blob" or "boolean" or "document" or "string" or "byte" or "short" or "integer" or "long" or
                        "float" or "double" or "bigInteger" or "bigDecimal" or "timestamp" or
                        "list" or "map" or "structure" or "union" or "enum" or "intEnum",
                    _ => true
                };
                ValidationContext.Require(targetAllowed, traitPath, "document.trait_target",
                    "The recognized Smithy prelude trait is not applicable to this shape.");
                var conflict = trait.Name switch
                {
                    "smithy.api#readonly" => traits.TryGetProperty("smithy.api#idempotent", out _),
                    "smithy.api#input" => traits.TryGetProperty("smithy.api#output", out _) ||
                        traits.TryGetProperty("smithy.api#error", out _),
                    "smithy.api#output" => traits.TryGetProperty("smithy.api#input", out _) ||
                        traits.TryGetProperty("smithy.api#error", out _),
                    "smithy.api#error" => traits.TryGetProperty("smithy.api#trait", out _),
                    _ => false
                };
                ValidationContext.Require(!conflict, traitPath, "document.trait_conflict",
                    "Conflicting Smithy prelude traits cannot be applied to the same shape.");
                switch (trait.Name)
                {
                    case "smithy.api#documentation":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.String, traitPath,
                            "document.trait_value", "The Smithy documentation trait requires a string value.");
                        continue;
                    case "smithy.api#error":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.String &&
                            trait.Value.GetString() is "client" or "server", traitPath,
                            "document.trait_value", "The Smithy error trait requires client or server.");
                        continue;
                    case "smithy.api#required":
                    case "smithy.api#clientOptional":
                    case "smithy.api#addedDefault":
                    case "smithy.api#sparse":
                    case "smithy.api#input":
                    case "smithy.api#output":
                    case "smithy.api#readonly":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "Smithy annotation traits require object values.");
                        continue;
                    case "smithy.api#idempotent":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "The Smithy idempotent trait requires an object value.");
                        ValidateIdempotent(shape, trait.Value, traitPath);
                        continue;
                    case "smithy.api#trait":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "The Smithy trait marker requires an object value.");
                        ValidateTraitMarker(trait.Value, traitPath);
                        continue;
                    case "smithy.api#default":
                        ValidationContext.Require(memberContainer is null && kind is not null ||
                            memberContainer == "structure", traitPath, "document.trait_target",
                            "A Smithy default requires a root shape or structure member.");
                        continue;
                    case "smithy.api#enumValue":
                        continue;
                }
                if (shapes.TryGetProperty(trait.Name, out var definition))
                {
                    ValidationContext.Require(definition.TryGetProperty("traits", out var markers) &&
                        markers.ValueKind == JsonValueKind.Object && markers.TryGetProperty("smithy.api#trait", out _),
                        path + "/traits", "document.trait_definition", "An applied custom trait must target a trait definition.");
                    var marker = definition.GetProperty("traits").GetProperty("smithy.api#trait");
                    if (marker.ValueKind == JsonValueKind.Object && marker.TryGetProperty("conflicts", out var conflicts) &&
                        conflicts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var other in conflicts.EnumerateArray())
                        {
                            budget.Work(path: traitPath);
                            ValidationContext.Require(other.ValueKind != JsonValueKind.String ||
                                !traits.TryGetProperty(other.GetString()!, out _), traitPath, "document.trait_conflict",
                                "An applied Smithy trait conflicts with another trait on this shape.");
                        }
                    }
                }
                Visit(trait.Name, depth + 1, traitDefinition: true);
            }
        }

        private void ValidateTraitMarker(JsonElement marker, string path)
        {
            if (marker.TryGetProperty("selector", out var selector))
            {
                ValidationContext.Require(selector.ValueKind == JsonValueKind.String, path + "/selector",
                    "document.trait_selector", "A Smithy trait selector must be a string.");
            }
            if (marker.TryGetProperty("structurallyExclusive", out var exclusive))
            {
                ValidationContext.Require(exclusive.ValueKind == JsonValueKind.String &&
                    exclusive.GetString() is "member" or "target", path + "/structurallyExclusive",
                    "document.trait_exclusive", "Smithy structural exclusivity must be member or target.");
            }
            if (marker.TryGetProperty("breakingChanges", out var rules))
            {
                ValidateBreakingChanges(rules, path + "/breakingChanges");
            }
            if (!marker.TryGetProperty("conflicts", out var conflicts)) { return; }
            ValidationContext.Require(conflicts.ValueKind == JsonValueKind.Array, path + "/conflicts",
                "document.trait_conflicts", "Smithy trait conflicts must be an array of absolute trait IDs.");
            var index = 0;
            foreach (var conflict in conflicts.EnumerateArray())
            {
                var at = path + "/conflicts/" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                budget.Work(path: at);
                ValidationContext.Require(conflict.ValueKind == JsonValueKind.String &&
                    IsSmithyShapeId(conflict.GetString()!), at, "document.trait_conflicts",
                    "A Smithy trait conflict must name an absolute root trait ID.");
            }
        }

        private void ValidateBreakingChanges(JsonElement rules, string path)
        {
            ValidationContext.Require(rules.ValueKind == JsonValueKind.Array && rules.GetArrayLength() > 0,
                path, "document.trait_breaking_changes", "Smithy breakingChanges must be a nonempty array.");
            var index = 0;
            foreach (var rule in rules.EnumerateArray())
            {
                var at = path + "/" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                budget.Work(path: at);
                ValidationContext.Require(rule.ValueKind == JsonValueKind.Object, at,
                    "document.trait_breaking_changes", "A Smithy breaking-change rule must be an object.");
                ValidationContext.Require(rule.TryGetProperty("change", out var change) &&
                    change.ValueKind == JsonValueKind.String &&
                    change.GetString() is "add" or "remove" or "update" or "any" or "presence",
                    at + "/change", "document.trait_change", "A Smithy breaking-change rule requires a valid change type.");
                if (rule.TryGetProperty("severity", out var severity))
                {
                    ValidationContext.Require(severity.ValueKind == JsonValueKind.String &&
                        severity.GetString() is "ERROR" or "DANGER" or "WARNING" or "NOTE",
                        at + "/severity", "document.trait_severity", "A Smithy breaking-change severity must be recognized.");
                }
                if (rule.TryGetProperty("message", out var message))
                {
                    ValidationContext.Require(message.ValueKind == JsonValueKind.String, at + "/message",
                        "document.trait_message", "A Smithy breaking-change message must be a string.");
                }
                if (rule.TryGetProperty("path", out var pointer))
                {
                    ValidationContext.Require(pointer.ValueKind == JsonValueKind.String, at + "/path",
                        "document.trait_pointer", "A Smithy breaking-change path must be a JSON Pointer string.");
                    var text = pointer.GetString()!;
                    budget.Work(text.Length, at + "/path");
                    ValidationContext.Require(SmithyTraitDiffPointer().IsMatch(text), at + "/path",
                        "document.trait_pointer", "A Smithy breaking-change path must have valid JSON Pointer syntax.");
                }
            }
        }

        private void ValidateIdempotent(JsonElement operation, JsonElement trait, string path)
        {
            ValidationContext.Require(!trait.TryGetProperty("exists", out _) ||
                !trait.TryGetProperty("notFound", out _), path, "document.idempotent_conflict",
                "Smithy idempotent exists and notFound properties are mutually exclusive.");
            var declaredErrors = new HashSet<string>(StringComparer.Ordinal);
            if (operation.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errors.EnumerateArray())
                {
                    budget.Work(path: path);
                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("target", out var target) &&
                        target.ValueKind == JsonValueKind.String)
                    {
                        declaredErrors.Add(target.GetString()!);
                    }
                }
            }
            foreach (var member in new[] { "exists", "notFound" })
            {
                if (!trait.TryGetProperty(member, out var values)) { continue; }
                var memberPath = path + "/" + member;
                ValidationContext.Require(values.ValueKind == JsonValueKind.Array, memberPath,
                    "document.idempotent_errors", "Smithy idempotent error declarations must be arrays.");
                var index = 0;
                foreach (var value in values.EnumerateArray())
                {
                    var valuePath = memberPath + "/" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    budget.Work(path: valuePath);
                    ValidationContext.Require(value.ValueKind == JsonValueKind.String &&
                        IsSmithyShapeId(value.GetString()!), valuePath, "document.idempotent_error",
                        "A Smithy idempotent error must be an absolute root shape ID.");
                    ValidationContext.Require(declaredErrors.Contains(value.GetString()!), valuePath, "document.idempotent_error",
                        "A Smithy idempotent error must be declared in this operation's errors list.");
                }
            }
        }
    }
}
