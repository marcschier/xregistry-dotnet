// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net.Http.Headers;
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

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z_0-9]*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SmithyEnumConstantName();

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt][0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?[Zz]\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SmithyUtcTimestamp();

    [GeneratedRegex(@"\A-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SmithyEncodedNumber();

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z_0-9-]*(?::[A-Za-z_][A-Za-z_0-9-]*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SmithyXmlName();

    [GeneratedRegex(@"\A(?:[A-Za-z0-9._~!$&'()*+,;=:@/?-]|%[A-Fa-f0-9]{2})*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SmithyUriFragment();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SmithyMediaTypeComponent();

    [GeneratedRegex(@"\A[!#$%&'*+.^_`|~0-9A-Za-z-]+\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SmithyHttpHeaderName();

    [GeneratedRegex(@"\A(?:[A-Za-z0-9._~!$&'()*+,;=-]|%[A-Fa-f0-9]{2})*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SmithyHostPrefixLiteral();

    private static bool IsSmithyMediaTypeName(string value)
    {
        var slash = value.IndexOf('/');
        return slash is >= 1 and <= 127 && value.Length - slash - 1 is >= 1 and <= 127 &&
            SmithyMediaTypeComponent().IsMatch(value.AsSpan(0, slash)) &&
            SmithyMediaTypeComponent().IsMatch(value.AsSpan(slash + 1));
    }

    private static bool IsSmithyNamespaceUri(string value)
    {
        if (Uri.IsWellFormedUriString(value, UriKind.RelativeOrAbsolute)) { return true; }
        // System.Uri rejects fragments on relative references; validate the RFC 3986 fragment separately.
        var fragment = value.IndexOf('#');
        return fragment >= 0 &&
            (fragment == 0 || Uri.IsWellFormedUriString(value[..fragment], UriKind.Relative)) &&
            SmithyUriFragment().IsMatch(value[(fragment + 1)..]);
    }

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
            var preludeNames = new HashSet<string>(s_smithyPreludeKinds.Keys, StringComparer.OrdinalIgnoreCase);
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
                ValidationContext.Require(IsSmithyShapeId(shape.Name) && names.Add(shape.Name) &&
                    (!preludeNames.Contains(shape.Name) || SmithyPreludeKind(shape.Name) is not null),
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
            closure.ValidateUniqueItems();
            closure.ValidateStreaming();
            closure.ValidateTraitNodes();
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

    private static readonly Dictionary<string, string> s_smithyPreludeKinds = new(StringComparer.Ordinal)
    {
        ["smithy.api#Unit"] = "structure",
        ["smithy.api#String"] = "string",
        ["smithy.api#Blob"] = "blob",
        ["smithy.api#Boolean"] = "boolean",
        ["smithy.api#Document"] = "document",
        ["smithy.api#Byte"] = "byte",
        ["smithy.api#Short"] = "short",
        ["smithy.api#Integer"] = "integer",
        ["smithy.api#Long"] = "long",
        ["smithy.api#Float"] = "float",
        ["smithy.api#Double"] = "double",
        ["smithy.api#BigInteger"] = "bigInteger",
        ["smithy.api#BigDecimal"] = "bigDecimal",
        ["smithy.api#Timestamp"] = "timestamp",
        ["smithy.api#PrimitiveBoolean"] = "boolean",
        ["smithy.api#PrimitiveByte"] = "byte",
        ["smithy.api#PrimitiveShort"] = "short",
        ["smithy.api#PrimitiveInteger"] = "integer",
        ["smithy.api#PrimitiveLong"] = "long",
        ["smithy.api#PrimitiveFloat"] = "float",
        ["smithy.api#PrimitiveDouble"] = "double"
    };

    private static string? SmithyPreludeKind(string id) => s_smithyPreludeKinds.GetValueOrDefault(id);

    private static bool IsSmithyPrimitivePrelude(string id) =>
        id is "smithy.api#PrimitiveBoolean" or "smithy.api#PrimitiveByte" or "smithy.api#PrimitiveShort" or
            "smithy.api#PrimitiveInteger" or "smithy.api#PrimitiveLong" or "smithy.api#PrimitiveFloat" or
            "smithy.api#PrimitiveDouble";

    private static bool IsSmithyHttpBinding(string id) =>
        id is "smithy.api#httpLabel" or "smithy.api#httpQuery" or "smithy.api#httpHeader" or
            "smithy.api#httpPrefixHeaders" or "smithy.api#httpPayload" or
            "smithy.api#httpQueryParams" or "smithy.api#httpResponseCode";

    private static bool IsSmithyAuthenticationScheme(string id) =>
        id is "smithy.api#httpBasicAuth" or "smithy.api#httpDigestAuth" or
            "smithy.api#httpBearerAuth" or "smithy.api#httpApiKeyAuth";

    private sealed class SmithyClosure(JsonElement shapes, ValidationContext budget)
    {
        private readonly Dictionary<string, JsonElement> _visited = new(StringComparer.Ordinal);
        private readonly List<(string Id, JsonElement Value, string Path)> _traitNodes = [];

        internal void Visit(string id, int depth, string? expected = null, bool error = false, bool traitDefinition = false,
            string? sourceId = null, string? referencePath = null)
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
            if (sourceId is not null && shape.TryGetProperty("traits", out var accessTraits) &&
                accessTraits.ValueKind == JsonValueKind.Object && accessTraits.TryGetProperty("smithy.api#private", out _))
            {
                ValidationContext.Require(sourceId.AsSpan(0, sourceId.IndexOf('#'))
                    .SequenceEqual(id.AsSpan(0, id.IndexOf('#'))),
                    referencePath ?? path, "document.private_access",
                    "A private Smithy shape can only be referenced from its own namespace.");
            }
            var kind = shape.GetProperty("type").GetString();
            ValidatePreludeShape(id, shape, kind, path);
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
            Traits(shape, path, depth, id, kind);
            switch (kind)
            {
                case "operation":
                    foreach (var name in new[] { "input", "output" })
                    {
                        if (shape.TryGetProperty(name, out var target))
                        {
                            Reference(target, path + "/" + name, depth, id, "structure");
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
                                depth, id, "structure", error: true);
                        }
                    }
                    ValidateRequestCompression(shape, path);
                    ValidateHttpOperation(shape, path);
                    ValidateHttpPayloads(shape);
                    ValidateEndpoint(shape, path);
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
                        var jsonNames = new HashSet<string>(StringComparer.Ordinal);
                        var queryNames = new HashSet<string>(StringComparer.Ordinal);
                        var headerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        string? headerPrefix = null;
                        var enumStrings = new HashSet<string>(StringComparer.Ordinal);
                        var enumIntegers = new HashSet<JsonNumber>();
                        foreach (var member in members.EnumerateObject())
                        {
                            ValidationContext.Require(IsSmithyShapeId("example#" + member.Name) && names.Add(member.Name),
                                path + "/members", "document.member_name", "Smithy member names must be valid and case-insensitively unique.");
                            var memberPath = path + "/members/" + Escape(member.Name);
                            Reference(member.Value, memberPath, depth, id,
                                kind == "union" && TryTrait(shape, "smithy.api#streaming", out _) ? "structure" : null,
                                memberContainer: kind);
                            if (TryTrait(member.Value, "smithy.api#jsonName", out var jsonName))
                            {
                                budget.Work(jsonName.GetString()!.Length, memberPath);
                                ValidationContext.Require(jsonNames.Add(jsonName.GetString()!), memberPath + "/traits/smithy.api#jsonName",
                                    "document.json_name", "Smithy structure or union members cannot repeat an explicit JSON name.");
                            }
                            if (TryTrait(member.Value, "smithy.api#httpQuery", out var queryName))
                            {
                                ValidationContext.Require(queryNames.Add(queryName.GetString()!),
                                    memberPath + "/traits/smithy.api#httpQuery", "document.http_query_name",
                                    "Smithy HTTP query names must be case-sensitively unique within a structure.");
                            }
                            if (TryTrait(member.Value, "smithy.api#httpHeader", out var headerName))
                            {
                                ValidationContext.Require(headerNames.Add(headerName.GetString()!),
                                    memberPath + "/traits/smithy.api#httpHeader", "document.http_header_name",
                                    "Smithy HTTP header names must be case-insensitively unique within a structure.");
                            }
                            if (TryTrait(member.Value, "smithy.api#httpPrefixHeaders", out var prefix))
                            {
                                headerPrefix = prefix.GetString()!;
                            }
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
                        ValidationContext.Require(string.IsNullOrEmpty(headerPrefix) ||
                            !headerNames.Any(name => name.StartsWith(headerPrefix, StringComparison.OrdinalIgnoreCase)),
                            path + "/members", "document.http_header_prefix",
                            "An explicit HTTP header cannot start with a nonempty httpPrefixHeaders prefix.");
                    }
                    else
                    {
                        ValidationContext.Require(kind == "structure", path, "document.members",
                            "Smithy union and enum shapes require members.");
                    }
                    break;
                case "list":
                    RequiredReference(shape, "member", path, depth, id);
                    break;
                case "map":
                    RequiredReference(shape, "key", path, depth, id, "string");
                    RequiredReference(shape, "value", path, depth, id);
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
                else if (IsSmithyPrimitivePrelude(id))
                {
                    using var implicitDefault = JsonDocument.Parse(kind == "boolean" ? "false" : "0");
                    ValidateDefault(kind, shape, implicitDefault.RootElement, path, member: false);
                }
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
                    else if (IsSmithyPrimitivePrelude(targetId))
                    {
                        ValidationContext.Require(hasDefault && (memberDefault.ValueKind == JsonValueKind.Null ||
                            MatchesPrimitiveDefault(targetKind, memberDefault, memberPath + "/traits/smithy.api#default")),
                            memberPath + "/traits/smithy.api#default", "document.default_repeat",
                            "A structure member must repeat its primitive prelude default or explicitly override it with null.");
                    }
                    if (hasDefault) { ValidateDefault(targetKind, target, memberDefault, memberPath, member: true,
                        memberShape: member.Value); }
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
                if (owners.ContainsKey("smithy.api#eventPayload"))
                {
                    foreach (var member in members.EnumerateObject())
                    {
                        var path = "$/shapes/" + Escape(id) + "/members/" + Escape(member.Name);
                        budget.Work(path: path);
                        ValidationContext.Require(TryTrait(member.Value, "smithy.api#eventPayload", out _) ||
                            TryTrait(member.Value, "smithy.api#eventHeader", out _), path, "document.event_binding",
                            "Every other member of a structure with an event payload must be an event header.");
                    }
                }

                void Check(JsonElement value, string mode, string memberName, string path)
                {
                    if (!value.TryGetProperty("traits", out var traits)) { return; }
                    foreach (var trait in traits.EnumerateObject())
                    {
                        budget.Work(path: path);
                        if (trait.Name is "smithy.api#idempotencyToken" or "smithy.api#eventPayload" or
                            "smithy.api#httpResponseCode" or "smithy.api#httpPayload" or
                            "smithy.api#httpQueryParams" or "smithy.api#httpPrefixHeaders" && mode == "member")
                        {
                            ValidationContext.Require(owners.TryAdd(trait.Name, memberName), path,
                                "document.trait_exclusivity",
                                "Only one member of a Smithy structure can carry this exclusive member trait.");
                            continue;
                        }
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

        internal void ValidateUniqueItems()
        {
            foreach (var (id, shape) in _visited)
            {
                budget.Work();
                if (shape.GetProperty("type").GetString() != "list" ||
                    !shape.TryGetProperty("traits", out var traits) ||
                    !traits.TryGetProperty("smithy.api#uniqueItems", out _)) { continue; }
                var path = "$/shapes/" + Escape(id) + "/traits/smithy.api#uniqueItems";
                var pending = new Stack<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                pending.Push(shape.GetProperty("member").GetProperty("target").GetString()!);
                while (pending.TryPop(out var targetId))
                {
                    budget.Work(path: path);
                    if (!seen.Add(targetId)) { continue; }
                    var target = shapes.TryGetProperty(targetId, out var supplied) ? supplied : default;
                    var kind = target.ValueKind == JsonValueKind.Object
                        ? target.GetProperty("type").GetString() : SmithyPreludeKind(targetId);
                    ValidationContext.Require(kind is not ("float" or "double" or "document"), path,
                        "document.unique_items_target",
                        "A Smithy uniqueItems list cannot transitively contain floats, doubles or documents.");
                    if (target.ValueKind != JsonValueKind.Object) { continue; }
                    switch (kind)
                    {
                        case "structure":
                        case "union":
                            if (target.TryGetProperty("members", out var members))
                            {
                                foreach (var member in members.EnumerateObject())
                                {
                                    budget.Work(path: path);
                                    pending.Push(member.Value.GetProperty("target").GetString()!);
                                }
                            }
                            break;
                        case "list":
                            pending.Push(target.GetProperty("member").GetProperty("target").GetString()!);
                            break;
                        case "map":
                            pending.Push(target.GetProperty("key").GetProperty("target").GetString()!);
                            pending.Push(target.GetProperty("value").GetProperty("target").GetString()!);
                            break;
                    }
                }
            }
        }

        private void ValidateRequestCompression(JsonElement operation, string path)
        {
            if (!TryTrait(operation, "smithy.api#requestCompression", out _) ||
                !operation.TryGetProperty("input", out var input) ||
                !shapes.TryGetProperty(input.GetProperty("target").GetString()!, out var structure) ||
                !structure.TryGetProperty("members", out var members)) { return; }
            foreach (var member in members.EnumerateObject())
            {
                budget.Work();
                if (!shapes.TryGetProperty(member.Value.GetProperty("target").GetString()!, out var target)) { continue; }
                ValidationContext.Require(!TryTrait(target, "smithy.api#streaming", out _) ||
                    !TryTrait(target, "smithy.api#requiresLength", out _),
                    path + "/traits/smithy.api#requestCompression", "document.request_compression",
                    "Request compression cannot combine streaming and requiresLength on an input member target.");
            }
        }

        internal void ValidateStreaming()
        {
            var streams = _visited.Where(static entry => TryTrait(entry.Value, "smithy.api#streaming", out _))
                .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);
            if (streams.Count == 0) { return; }
            var inputs = new HashSet<string>(StringComparer.Ordinal);
            var outputs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var shape in shapes.EnumerateObject())
            {
                budget.Work();
                if (shape.Value.GetProperty("type").GetString() != "operation") { continue; }
                foreach (var slot in new[] { "input", "output" })
                {
                    if (shape.Value.TryGetProperty(slot, out var reference) &&
                        reference.ValueKind == JsonValueKind.Object &&
                        reference.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.String)
                    {
                        (slot == "input" ? inputs : outputs).Add(target.GetString()!);
                    }
                }
            }
            var containers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var shape in shapes.EnumerateObject())
            {
                budget.Work();
                var kind = shape.Value.GetProperty("type").GetString();
                if (!Members(shape.Name, shape.Value, kind).Any(entry =>
                    entry.Member.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.String &&
                    streams.ContainsKey(target.GetString()!))) { continue; }
                var count = 0;
                foreach (var (member, memberPath) in Members(shape.Name, shape.Value, kind))
                {
                    if (!member.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.String ||
                        !shapes.TryGetProperty(target.GetString()!, out var stream) ||
                        !TryTrait(stream, "smithy.api#streaming", out _)) { continue; }
                    ValidationContext.Require(kind == "structure" &&
                        (inputs.Contains(shape.Name) || outputs.Contains(shape.Name)), memberPath,
                        "document.streaming_usage", "Streaming shapes require top-level operation input or output members.");
                    ValidationContext.Require(++count == 1, memberPath, "document.trait_exclusivity",
                        "Only one member of a Smithy structure can target a streaming shape.");
                    if (stream.GetProperty("type").GetString() == "blob")
                    {
                        ValidationContext.Require(TryTrait(member, "smithy.api#required", out _) ||
                            TryDefault(member, out _), memberPath, "document.streaming_member",
                            "A member targeting a streaming blob requires a required or default trait.");
                    }
                    if (TryTrait(stream, "smithy.api#requiresLength", out _))
                    {
                        ValidationContext.Require(inputs.Contains(shape.Name) && !outputs.Contains(shape.Name), memberPath,
                            "document.streaming_usage", "A requiresLength blob can only be referenced by operation input members.");
                    }
                    containers.Add(shape.Name);
                }
            }
            foreach (var shape in shapes.EnumerateObject())
            {
                budget.Work();
                foreach (var (member, memberPath) in Members(shape.Name, shape.Value, shape.Value.GetProperty("type").GetString()))
                {
                    ValidationContext.Require(!member.TryGetProperty("target", out var target) ||
                        target.ValueKind != JsonValueKind.String || !containers.Contains(target.GetString()!),
                        memberPath, "document.streaming_usage",
                        "A structure containing a streaming member cannot be targeted by another member.");
                }
            }

            IEnumerable<(JsonElement Member, string Path)> Members(string id, JsonElement shape, string? kind)
            {
                var path = "$/shapes/" + Escape(id);
                if (kind is "structure" or "union" or "enum" or "intEnum" &&
                    shape.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Object)
                {
                    foreach (var member in members.EnumerateObject())
                    {
                        budget.Work();
                        if (member.Value.ValueKind == JsonValueKind.Object)
                        { yield return (member.Value, path + "/members/" + Escape(member.Name)); }
                    }
                }
                foreach (var name in kind == "list" ? new[] { "member" } : kind == "map" ? new[] { "key", "value" } : [])
                {
                    budget.Work();
                    if (shape.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.Object)
                    { yield return (member, path + "/" + name); }
                }
            }
        }

        private static bool TryDefault(JsonElement shape, out JsonElement value)
        {
            value = default;
            return shape.TryGetProperty("traits", out var traits) && traits.ValueKind == JsonValueKind.Object &&
                traits.TryGetProperty("smithy.api#default", out value);
        }

        private bool MatchesPrimitiveDefault(string kind, JsonElement value, string path) =>
            kind == "boolean" ? value.ValueKind == JsonValueKind.False :
                value.ValueKind == JsonValueKind.Number && JsonNumber.Read(value, budget, path).IsZero;

        private void ValidatePreludeShape(string id, JsonElement shape, string? kind, string path)
        {
            var preludeKind = SmithyPreludeKind(id);
            if (preludeKind is null) { return; }
            ValidationContext.Require(kind == preludeKind, path + "/type", "document.prelude_shape",
                "A supplied Smithy prelude data shape must retain its predefined kind.");
            if (id == "smithy.api#Unit")
            {
                ValidationContext.Require(!shape.TryGetProperty("members", out var members) ||
                    members.ValueKind == JsonValueKind.Object && !members.EnumerateObject().Any(),
                    path + "/members", "document.prelude_shape",
                    "The Smithy Unit prelude shape cannot declare members.");
            }
            if (IsSmithyPrimitivePrelude(id) && TryDefault(shape, out var value))
            {
                ValidationContext.Require(MatchesPrimitiveDefault(preludeKind, value, path + "/traits/smithy.api#default"),
                    path + "/traits/smithy.api#default", "document.prelude_default",
                    "A supplied primitive Smithy prelude shape must retain its predefined default.");
            }
        }

        private void ValidateDefault(string kind, JsonElement target, JsonElement value, string path, bool member,
            JsonElement memberShape = default)
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
                if (TryTrait(memberShape, "smithy.api#range", out _) || TryTrait(target, "smithy.api#range", out _))
                {
                    ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, "smithy.special_range_default",
                        "Range checks on special floating-point defaults are not supported.");
                }
                return;
            }
            var encodedNumber = value.ValueKind == JsonValueKind.String && kind is "bigInteger" or "bigDecimal"
                ? ReadSmithyNumber(value, path, "smithy.numeric_string_default") : (JsonNumber?)null;
            var valid = kind switch
            {
                "string" or "enum" or "blob" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "bigInteger" or "bigDecimal" => value.ValueKind == JsonValueKind.Number || encodedNumber.HasValue,
                "list" => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0,
                "map" => value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any(),
                "document" => value.ValueKind != JsonValueKind.Array && value.ValueKind != JsonValueKind.Object ||
                    value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0 ||
                    value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any(),
                "timestamp" => value.ValueKind is JsonValueKind.Number or JsonValueKind.String,
                _ => value.ValueKind == JsonValueKind.Number
            };
            ValidationContext.Require(valid, path, "document.default_value",
                "The Smithy default value is incompatible with its target type.");
            if (kind == "timestamp")
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    ValidateTimestampString(value.GetString()!, path);
                    return;
                }
                ValidateTimestampNumber(value, path, "document.default_timestamp");
                return;
            }
            var length = 0;
            if (kind == "blob")
            {
                var text = value.GetString()!;
                budget.Work(text.Length, path);
                ValidationContext.Require(Convert.TryFromBase64String(text, new byte[text.Length], out length),
                    path, "document.default_value", "A Smithy blob default must contain valid base64.");
            }
            if (kind is "byte" or "short" or "integer" or "long" or "bigInteger" or "intEnum")
            {
                var number = encodedNumber ?? JsonNumber.Read(value, budget, path);
                ValidationContext.Require(number.IsInteger && FitsNumericRange(kind, number), path, "document.default_value",
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
            if (kind == "string" && TryTrait(target, "smithy.api#enum", out var legacyEnum))
            {
                var found = false;
                foreach (var item in legacyEnum.EnumerateArray())
                {
                    var declared = item.GetProperty("value").GetString()!;
                    budget.Work(declared.Length, path);
                    found |= string.Equals(value.GetString(), declared, StringComparison.Ordinal);
                }
                ValidationContext.Require(found, path, "document.default_enum",
                    "A Smithy string default must match a declared enum trait value.");
            }
            if (kind is "string" or "enum" &&
                (TryTrait(memberShape, "smithy.api#pattern", out _) || TryTrait(target, "smithy.api#pattern", out _)))
            {
                ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, "smithy.pattern_default",
                    "Pattern matching on Smithy defaults is not supported.");
            }
            if (kind is "string" or "enum" or "blob" or "list" or "map")
            {
                var hasLength = TryTrait(memberShape, "smithy.api#length", out var constraint) ||
                    TryTrait(target, "smithy.api#length", out constraint);
                if (!hasLength) { return; }
                if (kind is "string" or "enum")
                {
                    var text = value.GetString()!;
                    budget.Work(text.Length, path);
                    foreach (var rune in text.EnumerateRunes()) { length++; }
                }
                var number = new JsonNumber(false, length.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
                if (constraint.TryGetProperty("min", out var min))
                {
                    ValidationContext.Require(number.CompareTo(JsonNumber.Read(min, budget, path)) >= 0,
                        path, "document.default_length", "The Smithy default is shorter than its effective length minimum.");
                }
                if (constraint.TryGetProperty("max", out var max))
                {
                    ValidationContext.Require(number.CompareTo(JsonNumber.Read(max, budget, path)) <= 0,
                        path, "document.default_length", "The Smithy default exceeds its effective length maximum.");
                }
            }
            if (IsNumericKind(kind) &&
                (TryTrait(memberShape, "smithy.api#range", out var range) || TryTrait(target, "smithy.api#range", out range)))
            {
                var number = encodedNumber ?? JsonNumber.Read(value, budget, path);
                if (range.TryGetProperty("min", out var min))
                {
                    ValidationContext.Require(number.CompareTo(ReadSmithyNumber(min, path, "smithy.numeric_string_range")) >= 0,
                        path, "document.default_range", "The Smithy default is below its effective range minimum.");
                }
                if (range.TryGetProperty("max", out var max))
                {
                    ValidationContext.Require(number.CompareTo(ReadSmithyNumber(max, path, "smithy.numeric_string_range")) <= 0,
                        path, "document.default_range", "The Smithy default exceeds its effective range maximum.");
                }
            }
        }

        private static bool TryTrait(JsonElement shape, string name, out JsonElement value)
        {
            value = default;
            return shape.ValueKind == JsonValueKind.Object &&
                shape.TryGetProperty("traits", out var traits) && traits.ValueKind == JsonValueKind.Object &&
                traits.TryGetProperty(name, out value);
        }

        private JsonNumber ReadSmithyNumber(JsonElement value, string path, string unsupportedCode)
        {
            if (value.ValueKind != JsonValueKind.String) { return JsonNumber.Read(value, budget, path); }
            var text = value.GetString()!;
            budget.Work(text.Length, path);
            if (text.Length > 256)
            {
                ValidationContext.Fail(DocumentValidationStatus.Indeterminate, path, "limit.number",
                    "Numeric values are limited to 256 source characters.");
            }
            if (!SmithyEncodedNumber().IsMatch(text))
            {
                ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, unsupportedCode,
                    "Only JSON-number spellings of string-encoded Smithy numbers are supported.");
            }
            using var parsed = JsonDocument.Parse(text);
            return JsonNumber.Read(parsed.RootElement, budget, path);
        }

        private void ValidateTimestampNumber(JsonElement value, string path, string code)
        {
            var seconds = JsonNumber.Read(value, budget, path);
            ValidationContext.Require((seconds.IsZero || seconds.Exponent >= -3) &&
                seconds.CompareTo(new(true, "62135596800", 0)) >= 0 &&
                seconds.CompareTo(new(false, "253402300799999", -3)) <= 0,
                path, code,
                "A Smithy timestamp must be epoch seconds within years 0001 through 9999 at millisecond resolution.");
        }

        private void ValidateTimestampString(string text, string path)
        {
            budget.Work(text.Length, path);
            ValidationContext.Require(SmithyUtcTimestamp().IsMatch(text), path, "document.default_timestamp",
                "A Smithy string timestamp default must use RFC 3339 UTC Z syntax without an offset.");
            var year = Part(0, 4);
            var month = Part(5, 2);
            var day = Part(8, 2);
            var hour = Part(11, 2);
            var minute = Part(14, 2);
            var second = Part(17, 2);
            ValidationContext.Require(year >= 1 && month is >= 1 and <= 12 &&
                day >= 1 && day <= DateTime.DaysInMonth(year, month) &&
                hour <= 23 && minute <= 59 && second <= 60,
                path, "document.default_timestamp", "The Smithy timestamp default must contain a valid Gregorian date and time.");
            if (second == 60 || text.Length > 24 && text.AsSpan(23, text.Length - 24).ContainsAnyExcept('0'))
            {
                ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, "smithy.timestamp_default",
                    "Leap seconds and sub-millisecond string timestamp defaults are not supported.");
            }

            int Part(int start, int count) => int.Parse(text.AsSpan(start, count),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool IsNumericKind(string? kind) => kind is "byte" or "short" or "integer" or "long" or
            "float" or "double" or "bigInteger" or "bigDecimal" or "intEnum";

        private static readonly string s_floatMaximum = new System.Numerics.BigInteger(float.MaxValue)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        private static readonly string s_doubleMaximum = new System.Numerics.BigInteger(double.MaxValue)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static bool FitsNumericRange(string? kind, JsonNumber number)
        {
            var bounds = kind switch
            {
                "byte" => ("128", "127"),
                "short" => ("32768", "32767"),
                "integer" or "intEnum" => ("2147483648", "2147483647"),
                "long" => ("9223372036854775808", "9223372036854775807"),
                "float" => (s_floatMaximum, s_floatMaximum),
                "double" => (s_doubleMaximum, s_doubleMaximum),
                _ => ((string, string)?)null
            };
            return bounds is null || number.CompareTo(new(true, bounds.Value.Item1, 0)) >= 0 &&
                number.CompareTo(new(false, bounds.Value.Item2, 0)) <= 0;
        }

        private void ValidateEndpoint(JsonElement operation, string path)
        {
            if (!TryTrait(operation, "smithy.api#endpoint", out var endpoint)) { return; }
            var prefixPath = path + "/traits/smithy.api#endpoint/hostPrefix";
            var prefix = endpoint.GetProperty("hostPrefix").GetString()!;
            budget.Work(prefix.Length, prefixPath);
            var labels = new HashSet<string>(StringComparer.Ordinal);
            var position = 0;
            var previousLabel = false;
            while (position < prefix.Length)
            {
                var open = prefix.IndexOf('{', position);
                var end = open < 0 ? prefix.Length : open;
                var literal = prefix.AsSpan(position, end - position);
                ValidationContext.Require(SmithyHostPrefixLiteral().IsMatch(literal),
                    prefixPath, "document.endpoint_prefix",
                    "A Smithy host prefix permits only RFC 3986 registered-name literals and label placeholders.");
                if (open < 0) { break; }
                var close = prefix.IndexOf('}', open + 1);
                ValidationContext.Require(close >= 0 && (!previousLabel || literal.Length > 0),
                    prefixPath, "document.endpoint_prefix",
                    "Smithy endpoint labels must be closed and cannot be adjacent.");
                var name = prefix[(open + 1)..close];
                ValidationContext.Require(IsSmithyShapeId("example#" + name) && labels.Add(name),
                    prefixPath, "document.endpoint_prefix",
                    "Smithy endpoint labels require valid, distinct member names.");
                position = close + 1;
                previousLabel = true;
            }
            if (labels.Count == 0) { return; }
            var members = default(JsonElement);
            if (operation.TryGetProperty("input", out var input) &&
                shapes.TryGetProperty(input.GetProperty("target").GetString()!, out var inputShape))
            {
                inputShape.TryGetProperty("members", out members);
            }
            foreach (var name in labels)
            {
                budget.Work(path: prefixPath);
                ValidationContext.Require(members.ValueKind == JsonValueKind.Object &&
                    members.TryGetProperty(name, out var member) &&
                    TryTrait(member, "smithy.api#required", out _) &&
                    TryTrait(member, "smithy.api#hostLabel", out _),
                    prefixPath, "document.endpoint_label",
                    "Every endpoint label requires an exact-name required string input member marked with hostLabel.");
            }
        }

        private void ValidateHttpBindingTarget(JsonElement member, string trait, string path)
        {
            var kind = TargetKind(member, out var target);
            if (trait is "smithy.api#httpPrefixHeaders" or "smithy.api#httpQueryParams")
            {
                ValidationContext.Require(kind is null or "map", path, "document.trait_target",
                    "This Smithy HTTP binding requires a map target.");
                if (kind is null) { return; }
                ValidationContext.Require(trait != "smithy.api#httpPrefixHeaders" ||
                    !TryTrait(target, "smithy.api#sparse", out _), path, "document.trait_target",
                    "Smithy httpPrefixHeaders cannot target a sparse map.");
                if (!target.TryGetProperty("value", out var value)) { return; }
                kind = TargetKind(value, out target);
            }
            if (kind == "list" && trait != "smithy.api#httpPrefixHeaders")
            {
                if (!target.TryGetProperty("member", out var item)) { return; }
                kind = TargetKind(item, out _);
            }
            var allowed = kind is null or "string" or "enum" ||
                trait is "smithy.api#httpHeader" or "smithy.api#httpQuery" &&
                (kind is "boolean" or "timestamp" or "intEnum" || IsNumericKind(kind));
            ValidationContext.Require(allowed, path, "document.trait_target",
                "The Smithy HTTP binding target does not match its required scalar, list or map value types.");

            string? TargetKind(JsonElement reference, out JsonElement shape)
            {
                shape = default;
                if (reference.ValueKind != JsonValueKind.Object ||
                    !reference.TryGetProperty("target", out var id) || id.ValueKind != JsonValueKind.String) { return null; }
                return shapes.TryGetProperty(id.GetString()!, out shape)
                    ? shape.GetProperty("type").GetString() : SmithyPreludeKind(id.GetString()!);
            }
        }

        private void ValidateHttpPayloads(JsonElement operation)
        {
            var httpBound = TryTrait(operation, "smithy.api#http", out _);
            foreach (var name in new[] { "input", "output" })
            {
                if (operation.TryGetProperty(name, out var reference))
                {
                    ValidateContainer(reference, isInput: name == "input", requireStreamingPayload: httpBound);
                }
            }
            if (operation.TryGetProperty("errors", out var errors))
            {
                foreach (var reference in errors.EnumerateArray())
                {
                    ValidateContainer(reference, isInput: false, requireStreamingPayload: false);
                }
            }

            void ValidateContainer(JsonElement reference, bool isInput, bool requireStreamingPayload)
            {
                var id = reference.GetProperty("target").GetString()!;
                if (!shapes.TryGetProperty(id, out var container) ||
                    !container.TryGetProperty("members", out var members)) { return; }
                var path = "$/shapes/" + Escape(id) + "/members";
                var hasPayload = false;
                foreach (var member in members.EnumerateObject())
                {
                    var memberPath = path + "/" + Escape(member.Name);
                    budget.Work(path: memberPath);
                    var payload = TryTrait(member.Value, "smithy.api#httpPayload", out _);
                    hasPayload |= payload;
                    if (requireStreamingPayload)
                    {
                        var targetId = member.Value.GetProperty("target").GetString()!;
                        ValidationContext.Require(payload || !shapes.TryGetProperty(targetId, out var target) ||
                            !TryTrait(target, "smithy.api#streaming", out _),
                            memberPath, "document.http_streaming_payload",
                            "An HTTP-bound operation must mark streaming input and output members with httpPayload.");
                    }
                }
                if (!hasPayload) { return; }
                foreach (var member in members.EnumerateObject())
                {
                    var memberPath = path + "/" + Escape(member.Name);
                    budget.Work(path: memberPath);
                    ValidationContext.Require(TryTrait(member.Value, "smithy.api#httpPayload", out _) ||
                        TryTrait(member.Value, "smithy.api#httpHeader", out _) ||
                        TryTrait(member.Value, "smithy.api#httpPrefixHeaders", out _) ||
                        isInput && (TryTrait(member.Value, "smithy.api#httpLabel", out _) ||
                            TryTrait(member.Value, "smithy.api#httpQuery", out _) ||
                            TryTrait(member.Value, "smithy.api#httpQueryParams", out _)),
                        memberPath, "document.http_payload_binding",
                        "A top-level HTTP payload requires every sibling member to use an allowed non-body binding.");
                }
            }
        }

        private void ValidateHttpOperation(JsonElement operation, string path)
        {
            if (!TryTrait(operation, "smithy.api#http", out var http)) { return; }
            var uriPath = path + "/traits/smithy.api#http/uri";
            var uri = http.GetProperty("uri").GetString()!;
            budget.Work(uri.Length, uriPath);
            var queryIndex = uri.IndexOf('?');
            var pattern = queryIndex < 0 ? uri : uri[..queryIndex];
            ValidationContext.Require(uri.StartsWith('/') && !pattern.Contains("//", StringComparison.Ordinal) &&
                !uri.Contains('#') && !uri.EndsWith('?') &&
                (queryIndex < 0 || SmithyUriFragment().IsMatch(uri.AsSpan(queryIndex + 1))),
                uriPath, "document.http_uri", "A Smithy HTTP URI requires a valid origin-form pattern without empty path segments or fragments.");
            var labels = new Dictionary<string, bool>(StringComparer.Ordinal);
            var hasGreedyLabel = false;
            foreach (var segment in pattern.Split('/'))
            {
                budget.Work(path: uriPath);
                if (segment.StartsWith('{') && segment.EndsWith('}'))
                {
                    var name = segment[1..^1];
                    var greedy = name.EndsWith('+');
                    if (greedy) { name = name[..^1]; }
                    ValidationContext.Require(IsSmithyShapeId("example#" + name) &&
                        labels.TryAdd(name, greedy) && (!greedy || !hasGreedyLabel),
                        uriPath, "document.http_uri", "HTTP URI labels require distinct member names and at most one greedy label.");
                    hasGreedyLabel |= greedy;
                }
                else
                {
                    ValidationContext.Require(segment is not ("." or "..") && SmithyUriFragment().IsMatch(segment),
                        uriPath, "document.http_uri", "HTTP path segments must be URI literals or whole-segment labels, without dot segments.");
                }
            }
            var members = default(JsonElement);
            if (operation.TryGetProperty("input", out var input) &&
                shapes.TryGetProperty(input.GetProperty("target").GetString()!, out var inputShape))
            {
                inputShape.TryGetProperty("members", out members);
            }
            foreach (var (name, greedy) in labels)
            {
                budget.Work(path: uriPath);
                ValidationContext.Require(members.ValueKind == JsonValueKind.Object &&
                    members.TryGetProperty(name, out var member) &&
                    TryTrait(member, "smithy.api#required", out _) &&
                    TryTrait(member, "smithy.api#httpLabel", out _),
                    uriPath, "document.http_label", "Every HTTP URI label requires an exact-name required input member marked with httpLabel.");
                var targetId = members.GetProperty(name).GetProperty("target").GetString()!;
                var targetKind = shapes.TryGetProperty(targetId, out var target)
                    ? target.GetProperty("type").GetString() : SmithyPreludeKind(targetId);
                ValidationContext.Require(!greedy || targetKind is "string" or "enum",
                    uriPath, "document.http_label", "A greedy HTTP URI label must target a string.");
            }
            if (members.ValueKind != JsonValueKind.Object) { return; }
            foreach (var member in members.EnumerateObject())
            {
                budget.Work(path: uriPath);
                ValidationContext.Require(!TryTrait(member.Value, "smithy.api#httpLabel", out _) ||
                    labels.ContainsKey(member.Name), uriPath, "document.http_label",
                    "Every httpLabel input member requires a matching HTTP URI label.");
            }
        }

        private void RequiredReference(JsonElement shape, string name, string path, int depth, string sourceId,
            string? expected = null)
        {
            ValidationContext.Require(shape.TryGetProperty(name, out var value), path + "/" + name,
                "document.target", "The Smithy shape is missing a required member reference.");
            Reference(value, path + "/" + name, depth, sourceId, expected,
                memberContainer: shape.GetProperty("type").GetString());
        }

        private void Reference(JsonElement value, string path, int depth, string sourceId,
            string? expected = null, bool error = false,
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
            if (memberContainer is not null) { Traits(value, path, depth, sourceId, memberContainer: memberContainer); }
            Visit(value.GetProperty("target").GetString()!, depth + 1, expected, error,
                sourceId: sourceId, referencePath: path);
        }

        private void Traits(JsonElement shape, string path, int depth, string sourceId, string? kind = null,
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
                ValidationContext.Require(trait.Name != "smithy.api#box", traitPath, "document.smithy2_box",
                    "The box trait cannot be used in Smithy 2.0 models.");
                var targetAllowed = trait.Name switch
                {
                    "smithy.api#unitType" => memberContainer is null && kind == "structure" &&
                        sourceId == "smithy.api#Unit",
                    "smithy.api#auth" => memberContainer is null && kind is "service" or "operation",
                    "smithy.api#traitValidators" => memberContainer is null &&
                        traits.TryGetProperty("smithy.api#trait", out _),
                    "smithy.api#authDefinition" or "smithy.api#protocolDefinition" =>
                        memberContainer is null && kind == "structure" &&
                        traits.TryGetProperty("smithy.api#trait", out _),
                    "smithy.api#retryable" => memberContainer is null && kind == "structure" &&
                        traits.TryGetProperty("smithy.api#error", out _),
                    "smithy.api#httpError" => memberContainer is null && kind == "structure" &&
                        traits.TryGetProperty("smithy.api#error", out _),
                    "smithy.api#http" or "smithy.api#endpoint" or "smithy.api#httpChecksumRequired" or
                        "smithy.api#optionalAuth" =>
                        memberContainer is null && kind == "operation",
                    "smithy.api#hostLabel" => memberContainer == "structure",
                    "smithy.api#httpLabel" or "smithy.api#httpPayload" or "smithy.api#httpHeader" or
                        "smithy.api#httpPrefixHeaders" or "smithy.api#httpQuery" or "smithy.api#httpQueryParams" =>
                        memberContainer == "structure",
                    "smithy.api#httpResponseCode" => memberContainer == "structure" &&
                        !TryTrait(shapes.GetProperty(sourceId), "smithy.api#input", out _),
                    "smithy.api#idempotencyToken" => memberContainer == "structure",
                    "smithy.api#eventPayload" or "smithy.api#eventHeader" => memberContainer == "structure",
                    "smithy.api#jsonName" => memberContainer is "structure" or "union",
                    "smithy.api#mediaType" => memberContainer is null && kind is "blob" or "string" or "enum",
                    "smithy.api#xmlName" => memberContainer is not null ||
                        kind is "structure" or "union",
                    "smithy.api#xmlAttribute" => memberContainer == "structure",
                    "smithy.api#xmlFlattened" => memberContainer is "structure" or "union",
                    "smithy.api#xmlNamespace" => memberContainer is not null || kind is
                        "service" or "blob" or "boolean" or "document" or "string" or
                        "byte" or "short" or "integer" or "long" or "float" or "double" or
                        "bigInteger" or "bigDecimal" or "timestamp" or "enum" or "intEnum" or
                        "list" or "map" or "structure" or "union",
                    "smithy.api#requestCompression" => memberContainer is null && kind == "operation",
                    "smithy.api#streaming" => memberContainer is null && kind is "blob" or "union",
                    "smithy.api#requiresLength" => memberContainer is null && kind == "blob" &&
                        traits.TryGetProperty("smithy.api#streaming", out _),
                    "smithy.api#sensitive" => memberContainer is null && kind is not ("service" or "operation" or "resource"),
                    "smithy.api#enum" => memberContainer is null && kind == "string",
                    "smithy.api#required" or "smithy.api#clientOptional" or "smithy.api#recommended" =>
                        memberContainer == "structure",
                    "smithy.api#addedDefault" => memberContainer == "structure" &&
                        traits.TryGetProperty("smithy.api#default", out _),
                    "smithy.api#sparse" => kind is "list" or "map",
                    "smithy.api#uniqueItems" => kind == "list",
                    "smithy.api#enumValue" => memberContainer is "enum" or "intEnum",
                    "smithy.api#input" or "smithy.api#output" or "smithy.api#error" => kind == "structure",
                    "smithy.api#readonly" or "smithy.api#idempotent" or "smithy.api#examples" or
                        "smithy.api#longPoll" => kind == "operation",
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
                    "smithy.api#recommended" => traits.TryGetProperty("smithy.api#required", out _),
                    "smithy.api#eventPayload" => traits.TryGetProperty("smithy.api#eventHeader", out _),
                    "smithy.api#eventHeader" => traits.TryGetProperty("smithy.api#eventPayload", out _),
                    "smithy.api#xmlAttribute" => traits.TryGetProperty("smithy.api#xmlNamespace", out _),
                    "smithy.api#xmlNamespace" => traits.TryGetProperty("smithy.api#xmlAttribute", out _),
                    _ when IsSmithyHttpBinding(trait.Name) => traits.EnumerateObject().Any(other =>
                        other.Name != trait.Name && IsSmithyHttpBinding(other.Name)),
                    "smithy.api#required" => traits.TryGetProperty("smithy.api#recommended", out _),
                    "smithy.api#readonly" => traits.TryGetProperty("smithy.api#idempotent", out _),
                    "smithy.api#input" => traits.TryGetProperty("smithy.api#output", out _) ||
                        traits.TryGetProperty("smithy.api#error", out _),
                    "smithy.api#output" => traits.TryGetProperty("smithy.api#input", out _) ||
                        traits.TryGetProperty("smithy.api#error", out _),
                    "smithy.api#error" => traits.TryGetProperty("smithy.api#trait", out _),
                    "smithy.api#sparse" => traits.TryGetProperty("smithy.api#uniqueItems", out _),
                    "smithy.api#uniqueItems" => traits.TryGetProperty("smithy.api#sparse", out _),
                    _ => false
                };
                ValidationContext.Require(!conflict, traitPath, "document.trait_conflict",
                    "Conflicting Smithy prelude traits cannot be applied to the same shape.");
                switch (trait.Name)
                {
                    case "smithy.api#traitValidators":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "Smithy traitValidators requires an object keyed by nonempty event IDs.");
                        foreach (var validator in trait.Value.EnumerateObject())
                        {
                            var validatorPath = traitPath + "/" + Escape(validator.Name);
                            budget.Work(validator.Name.Length, validatorPath);
                            ValidationContext.Require(validator.Name.Length > 0 &&
                                validator.Value.ValueKind == JsonValueKind.Object, validatorPath,
                                "document.trait_validator", "A Smithy trait validator requires a nonempty event ID and an object value.");
                            ValidationContext.Require(validator.Value.TryGetProperty("selector", out var validatorSelector) &&
                                validatorSelector.ValueKind == JsonValueKind.String, validatorPath + "/selector",
                                "document.trait_validator", "A Smithy trait validator requires a selector string.");
                            if (validator.Value.TryGetProperty("message", out var validatorMessage))
                            {
                                ValidationContext.Require(validatorMessage.ValueKind == JsonValueKind.String,
                                    validatorPath + "/message", "document.trait_validator",
                                    "A Smithy trait validator message must be a string.");
                            }
                            if (validator.Value.TryGetProperty("severity", out var validatorSeverity))
                            {
                                ValidationContext.Require(validatorSeverity.ValueKind == JsonValueKind.String &&
                                    validatorSeverity.GetString() is "ERROR" or "DANGER" or "WARNING" or "NOTE",
                                    validatorPath + "/severity", "document.trait_validator",
                                    "A Smithy trait validator severity must be recognized.");
                            }
                        }
                        continue;
                    case "smithy.api#auth":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Array, traitPath,
                            "document.trait_value", "The Smithy auth trait requires an ordered array of authentication scheme IDs.");
                        var schemes = new HashSet<string>(StringComparer.Ordinal);
                        var schemeIndex = 0;
                        foreach (var scheme in trait.Value.EnumerateArray())
                        {
                            var schemePath = traitPath + "/" +
                                (schemeIndex++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            budget.Work(path: schemePath);
                            ValidationContext.Require(scheme.ValueKind == JsonValueKind.String &&
                                IsSmithyShapeId(scheme.GetString()!) && schemes.Add(scheme.GetString()!),
                                schemePath, "document.auth_scheme",
                                "Smithy authentication scheme IDs must be absolute root IDs without duplicates.");
                            var schemeId = scheme.GetString()!;
                            if (shapes.TryGetProperty(schemeId, out var schemeShape))
                            {
                                ValidationContext.Require(TryTrait(schemeShape, "smithy.api#trait", out _) &&
                                    TryTrait(schemeShape, "smithy.api#authDefinition", out _),
                                    schemePath, "document.auth_scheme",
                                    "An explicit authentication scheme must be a trait marked with authDefinition.");
                                Visit(schemeId, depth + 1, traitDefinition: true, sourceId: sourceId,
                                    referencePath: schemePath);
                            }
                            else if (!IsSmithyAuthenticationScheme(schemeId))
                            {
                                Visit(schemeId, depth + 1, traitDefinition: true, sourceId: sourceId,
                                    referencePath: schemePath);
                            }
                        }
                        continue;
                    case "smithy.api#authDefinition":
                    case "smithy.api#protocolDefinition":
                        var isProtocolDefinition = trait.Name == "smithy.api#protocolDefinition";
                        var requiredTraitCode = isProtocolDefinition ?
                            "document.protocol_required_trait" : "document.auth_required_trait";
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "A Smithy authentication or protocol definition requires an object value.");
                        if (isProtocolDefinition && trait.Value.TryGetProperty("noInlineDocumentSupport", out var noInline))
                        {
                            ValidationContext.Require(noInline.ValueKind is JsonValueKind.True or JsonValueKind.False,
                                traitPath + "/noInlineDocumentSupport", "document.trait_value",
                                "The Smithy protocol inline-document support flag requires a Boolean value.");
                        }
                        if (trait.Value.TryGetProperty("traits", out var requiredTraits))
                        {
                            ValidationContext.Require(requiredTraits.ValueKind == JsonValueKind.Array,
                                traitPath + "/traits", "document.trait_value",
                                "Smithy authentication or protocol required traits must be an array of trait shape IDs.");
                            var requiredIndex = 0;
                            foreach (var requiredTrait in requiredTraits.EnumerateArray())
                            {
                                var requiredPath = traitPath + "/traits/" +
                                    (requiredIndex++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                                budget.Work(path: requiredPath);
                                ValidationContext.Require(requiredTrait.ValueKind == JsonValueKind.String &&
                                    IsSmithyShapeId(requiredTrait.GetString()!), requiredPath,
                                    requiredTraitCode, "An authentication or protocol dependency requires an absolute trait shape ID.");
                                var requiredId = requiredTrait.GetString()!;
                                if (shapes.TryGetProperty(requiredId, out var requiredShape))
                                {
                                    ValidationContext.Require(TryTrait(requiredShape, "smithy.api#trait", out _),
                                        requiredPath, requiredTraitCode,
                                        "An authentication or protocol dependency must refer to a trait definition.");
                                }
                                Visit(requiredId, depth + 1, traitDefinition: true, sourceId: sourceId,
                                    referencePath: requiredPath);
                            }
                        }
                        continue;
                    case "smithy.api#unstable":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "The Smithy unstable trait requires an object value.");
                        if (trait.Value.TryGetProperty("featureId", out var featureId))
                        {
                            var featurePath = traitPath + "/featureId";
                            ValidationContext.Require(featureId.ValueKind == JsonValueKind.String, featurePath,
                                "document.trait_value", "A Smithy unstable featureId must be a string.");
                            var featureText = featureId.GetString()!;
                            budget.Work(featureText.Length, featurePath);
                            var featureLength = 0;
                            foreach (var rune in featureText.EnumerateRunes()) { featureLength++; }
                            ValidationContext.Require(featureLength <= 100, featurePath, "document.unstable_feature",
                                "A Smithy unstable featureId cannot exceed 100 Unicode scalar values.");
                        }
                        continue;
                    case "smithy.api#endpoint":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "The Smithy endpoint trait requires an object value.");
                        ValidationContext.Require(trait.Value.TryGetProperty("hostPrefix", out var hostPrefix) &&
                            hostPrefix.ValueKind == JsonValueKind.String, traitPath + "/hostPrefix",
                            "document.endpoint_prefix", "The Smithy endpoint trait requires a string hostPrefix.");
                        continue;
                    case "smithy.api#hostLabel":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "The Smithy hostLabel annotation requires an object value.");
                        var hostTargetId = shape.GetProperty("target").GetString()!;
                        var hostKind = shapes.TryGetProperty(hostTargetId, out var hostTarget)
                            ? hostTarget.GetProperty("type").GetString() : SmithyPreludeKind(hostTargetId);
                        ValidationContext.Require(traits.TryGetProperty("smithy.api#required", out _) &&
                            hostKind is null or "string" or "enum", traitPath, "document.trait_target",
                            "A Smithy hostLabel requires a required structure member targeting a string.");
                        continue;
                    case "smithy.api#httpHeader":
                    case "smithy.api#httpPrefixHeaders":
                    case "smithy.api#httpQuery":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.String, traitPath,
                            "document.trait_value", "Smithy named HTTP bindings require string values.");
                        var bindingName = trait.Value.GetString()!;
                        budget.Work(bindingName.Length, traitPath);
                        ValidationContext.Require(trait.Name == "smithy.api#httpPrefixHeaders" ||
                            bindingName.Length > 0, traitPath, "document.trait_value",
                            "A Smithy HTTP header or query binding name cannot be empty.");
                        ValidationContext.Require(trait.Name != "smithy.api#httpHeader" ||
                            SmithyHttpHeaderName().IsMatch(bindingName), traitPath, "document.http_header_name",
                            "The Smithy HTTP header name must satisfy the HTTP field-name token grammar.");
                        ValidateHttpBindingTarget(shape, trait.Name, traitPath);
                        continue;
                    case "smithy.api#httpQueryParams":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "The Smithy httpQueryParams annotation requires an object value.");
                        ValidateHttpBindingTarget(shape, trait.Name, traitPath);
                        continue;
                    case "smithy.api#http":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "The Smithy http trait requires an object value.");
                        foreach (var field in new[] { "method", "uri" })
                        {
                            ValidationContext.Require(trait.Value.TryGetProperty(field, out var text) &&
                                text.ValueKind == JsonValueKind.String, traitPath + "/" + field,
                                "document.http_binding", "The Smithy http trait requires string method and uri values.");
                        }
                        if (trait.Value.TryGetProperty("code", out var code))
                        {
                            ValidationContext.Require(code.ValueKind == JsonValueKind.Number, traitPath + "/code",
                                "document.http_code", "The Smithy HTTP success code requires an integer from 100 to 999.");
                            var status = JsonNumber.Read(code, budget, traitPath + "/code");
                            ValidationContext.Require(status.IsInteger && status.CompareTo(new(false, "100", 0)) >= 0 &&
                                status.CompareTo(new(false, "999", 0)) <= 0, traitPath + "/code",
                                "document.http_code", "The Smithy HTTP success code requires an integer from 100 to 999.");
                        }
                        continue;
                    case "smithy.api#httpLabel":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "The Smithy httpLabel annotation requires an object value.");
                        var labelTargetId = shape.GetProperty("target").GetString()!;
                        var labelKind = shapes.TryGetProperty(labelTargetId, out var labelTarget)
                            ? labelTarget.GetProperty("type").GetString() : SmithyPreludeKind(labelTargetId);
                        ValidationContext.Require(traits.TryGetProperty("smithy.api#required", out _) &&
                            (labelKind is null or "string" or "enum" or "boolean" or "timestamp" or "intEnum" ||
                                IsNumericKind(labelKind)),
                            traitPath, "document.trait_target", "A Smithy httpLabel requires a required structure member targeting a string, number, boolean or timestamp.");
                        continue;
                    case "smithy.api#httpError":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Number, traitPath,
                            "document.trait_value", "The Smithy httpError trait requires an integer value.");
                        var errorStatus = JsonNumber.Read(trait.Value, budget, traitPath);
                        ValidationContext.Require(errorStatus.IsInteger && FitsNumericRange("integer", errorStatus),
                            traitPath, "document.http_error", "The Smithy HTTP error code must fit a signed 32-bit integer.");
                        continue;
                    case "smithy.api#httpResponseCode":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "The Smithy httpResponseCode annotation requires an object value.");
                        var statusTargetId = shape.GetProperty("target").GetString()!;
                        var statusKind = shapes.TryGetProperty(statusTargetId, out var statusTarget)
                            ? statusTarget.GetProperty("type").GetString() : SmithyPreludeKind(statusTargetId);
                        ValidationContext.Require(statusKind is null or "integer" or "intEnum", traitPath,
                            "document.trait_target", "A Smithy HTTP response code member must target an integer.");
                        continue;
                    case "smithy.api#longPoll":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "The Smithy longPoll trait requires an object value.");
                        var timeoutPath = traitPath + "/timeoutMillis";
                        ValidationContext.Require(trait.Value.TryGetProperty("timeoutMillis", out var timeout) &&
                            timeout.ValueKind == JsonValueKind.Number, timeoutPath, "document.long_poll_timeout",
                            "Smithy longPoll requires a numeric timeoutMillis value.");
                        var millis = JsonNumber.Read(timeout, budget, timeoutPath);
                        ValidationContext.Require(millis.IsInteger && FitsNumericRange("integer", millis) &&
                            millis.CompareTo(new(false, "1", 0)) >= 0, timeoutPath, "document.long_poll_timeout",
                            "Smithy longPoll timeoutMillis must be a positive signed 32-bit integer.");
                        continue;
                    case "smithy.api#mediaType":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.String, traitPath,
                            "document.trait_value", "The Smithy mediaType trait requires a string value.");
                        var mediaTypeText = trait.Value.GetString()!;
                        budget.Work(mediaTypeText.Length, traitPath);
                        ValidationContext.Require(MediaTypeHeaderValue.TryParse(mediaTypeText, out var mediaType) &&
                            mediaType.MediaType is not null && IsSmithyMediaTypeName(mediaType.MediaType) &&
                            mediaType.Parameters.All(static parameter => parameter.Value is not null),
                            traitPath, "document.media_type",
                            "The Smithy media type requires RFC 6838 type/subtype names and valid parameters.");
                        continue;
                    case "smithy.api#xmlNamespace":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "The Smithy xmlNamespace trait requires an object value.");
                        ValidationContext.Require(trait.Value.TryGetProperty("uri", out var namespaceUri) &&
                            namespaceUri.ValueKind == JsonValueKind.String && namespaceUri.GetString()!.Length > 0,
                            traitPath + "/uri", "document.xml_namespace", "An XML namespace requires a nonempty URI reference.");
                        var namespaceText = namespaceUri.GetString()!;
                        budget.Work(namespaceText.Length, traitPath + "/uri");
                        ValidationContext.Require(IsSmithyNamespaceUri(namespaceText),
                            traitPath + "/uri", "document.xml_namespace", "The XML namespace URI reference is malformed.");
                        if (trait.Value.TryGetProperty("prefix", out var namespacePrefix))
                        {
                            ValidationContext.Require(namespacePrefix.ValueKind == JsonValueKind.String,
                                traitPath + "/prefix", "document.xml_namespace", "An XML namespace prefix must be a string.");
                            var prefixText = namespacePrefix.GetString()!;
                            budget.Work(prefixText.Length, traitPath + "/prefix");
                            ValidationContext.Require(!prefixText.Contains(':') && SmithyXmlName().IsMatch(prefixText),
                                traitPath + "/prefix", "document.xml_namespace",
                                "An XML namespace prefix must follow the Smithy ASCII identifier grammar.");
                        }
                        continue;
                    case "smithy.api#xmlName":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.String, traitPath,
                            "document.trait_value", "The Smithy xmlName trait requires a string value.");
                        var xmlName = trait.Value.GetString()!;
                        budget.Work(xmlName.Length, traitPath);
                        ValidationContext.Require(SmithyXmlName().IsMatch(xmlName), traitPath,
                            "document.xml_name", "The Smithy XML name must follow its ASCII identifier or prefix:identifier grammar.");
                        continue;
                    case "smithy.api#xmlAttribute":
                    case "smithy.api#xmlFlattened":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "Smithy XML binding annotations require object values.");
                        var xmlTargetId = shape.GetProperty("target").GetString()!;
                        var xmlKind = shapes.TryGetProperty(xmlTargetId, out var xmlTarget)
                            ? xmlTarget.GetProperty("type").GetString() : SmithyPreludeKind(xmlTargetId);
                        ValidationContext.Require(xmlKind is null || (trait.Name == "smithy.api#xmlFlattened"
                            ? xmlKind is "list" or "map"
                            : IsNumericKind(xmlKind) || xmlKind is "intEnum" or "boolean" or "string" or "enum" or "timestamp"),
                            traitPath, "document.trait_target", "The Smithy XML binding has an ineligible member target.");
                        continue;
                    case "smithy.api#jsonName":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.String, traitPath,
                            "document.trait_value", "The Smithy jsonName trait requires a string value.");
                        continue;
                    case "smithy.api#idRef":
                        var referenceKind = kind;
                        if (memberContainer is not null)
                        {
                            var referenceTargetId = shape.GetProperty("target").GetString()!;
                            referenceKind = shapes.TryGetProperty(referenceTargetId, out var referenceTarget)
                                ? referenceTarget.GetProperty("type").GetString() : SmithyPreludeKind(referenceTargetId);
                        }
                        ValidationContext.Require(memberContainer is not null && referenceKind is null ||
                            referenceKind is "string" or "enum", traitPath, "document.trait_target",
                            "Smithy idRef requires a string or a member targeting one.");
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "The Smithy idRef trait requires an object value.");
                        foreach (var referenceProperty in trait.Value.EnumerateObject())
                        {
                            budget.Work(path: traitPath);
                            if (referenceProperty.Name is "selector" or "errorMessage")
                            {
                                ValidationContext.Require(referenceProperty.Value.ValueKind == JsonValueKind.String,
                                    traitPath + "/" + referenceProperty.Name, "document.trait_value",
                                    "Smithy reference selectors and error messages require string values.");
                            }
                            else if (referenceProperty.Name == "failWhenMissing")
                            {
                                ValidationContext.Require(referenceProperty.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                                    traitPath + "/failWhenMissing", "document.trait_value",
                                    "The Smithy reference missing-target flag requires a Boolean value.");
                            }
                        }
                        continue;
                    case "smithy.api#timestampFormat":
                        var timestampKind = kind;
                        if (memberContainer is not null)
                        {
                            var targetId = shape.GetProperty("target").GetString()!;
                            timestampKind = shapes.TryGetProperty(targetId, out var timestampTarget)
                                ? timestampTarget.GetProperty("type").GetString() : SmithyPreludeKind(targetId);
                        }
                        ValidationContext.Require(memberContainer is not null && timestampKind is null ||
                            timestampKind == "timestamp", traitPath, "document.trait_target",
                            "Smithy timestampFormat requires a timestamp or a member targeting one.");
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.String &&
                            trait.Value.GetString() is "date-time" or "http-date" or "epoch-seconds", traitPath,
                            "document.trait_value", "The Smithy timestamp format must be a recognized format.");
                        continue;
                    case "smithy.api#retryable":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "The Smithy retryable trait requires an object value.");
                        if (trait.Value.TryGetProperty("throttling", out var throttling))
                        {
                            ValidationContext.Require(throttling.ValueKind is JsonValueKind.True or JsonValueKind.False,
                                traitPath + "/throttling", "document.trait_value",
                                "Smithy retryable throttling must be a Boolean value.");
                        }
                        continue;
                    case "smithy.api#eventPayload":
                    case "smithy.api#eventHeader":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "Smithy event binding annotations require object values.");
                        var eventTargetId = shape.GetProperty("target").GetString()!;
                        var eventKind = shapes.TryGetProperty(eventTargetId, out var eventTarget)
                            ? eventTarget.GetProperty("type").GetString() : SmithyPreludeKind(eventTargetId);
                        ValidationContext.Require(eventKind is null || (trait.Name == "smithy.api#eventPayload"
                            ? eventKind is "blob" or "string" or "enum" or "structure" or "union"
                            : eventKind is "boolean" or "byte" or "short" or "integer" or "intEnum" or "long" or
                                "blob" or "string" or "enum" or "timestamp"),
                            traitPath, "document.trait_target", "The Smithy event binding has an ineligible member target.");
                        continue;
                    case "smithy.api#idempotencyToken":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "The Smithy idempotencyToken annotation requires an object value.");
                        var tokenTargetId = shape.GetProperty("target").GetString()!;
                        var tokenKind = shapes.TryGetProperty(tokenTargetId, out var tokenTarget)
                            ? tokenTarget.GetProperty("type").GetString() : SmithyPreludeKind(tokenTargetId);
                        ValidationContext.Require(tokenKind is null or "string" or "enum", traitPath,
                            "document.trait_target", "A Smithy idempotency token must target a string.");
                        continue;
                    case "smithy.api#examples":
                        ValidateExamples(shape, trait.Value, traitPath);
                        continue;
                    case "smithy.api#suppress":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Array, traitPath,
                            "document.trait_value", "Smithy suppress requires an array of nonempty event ID strings.");
                        var suppressionIndex = 0;
                        foreach (var suppression in trait.Value.EnumerateArray())
                        {
                            var suppressionPath = traitPath + "/" +
                                (suppressionIndex++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            budget.Work(path: suppressionPath);
                            ValidationContext.Require(suppression.ValueKind == JsonValueKind.String &&
                                suppression.GetString()!.Length > 0, suppressionPath,
                                "document.trait_value", "A Smithy suppressed event ID must be a nonempty string.");
                        }
                        continue;
                    case "smithy.api#tags":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Array, traitPath,
                            "document.trait_value", "Smithy tags require an array of strings.");
                        var tagIndex = 0;
                        foreach (var tag in trait.Value.EnumerateArray())
                        {
                            var tagPath = traitPath + "/" + (tagIndex++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            budget.Work(path: tagPath);
                            ValidationContext.Require(tag.ValueKind == JsonValueKind.String, tagPath,
                                "document.trait_value", "Smithy tags must be strings.");
                        }
                        continue;
                    case "smithy.api#deprecated":
                    case "smithy.api#recommended":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "Smithy lifecycle annotations require object values.");
                        foreach (var name in trait.Name == "smithy.api#deprecated"
                            ? new[] { "message", "since" } : new[] { "reason" })
                        {
                            budget.Work(path: traitPath);
                            if (trait.Value.TryGetProperty(name, out var text))
                            {
                                ValidationContext.Require(text.ValueKind == JsonValueKind.String, traitPath + "/" + name,
                                    "document.trait_value", "Smithy lifecycle annotation text must be a string.");
                            }
                        }
                        continue;
                    case "smithy.api#enum":
                        ValidateLegacyEnum(trait.Value, traitPath);
                        continue;
                    case "smithy.api#pattern":
                        var patternKind = kind;
                        if (memberContainer is not null)
                        {
                            var targetId = shape.GetProperty("target").GetString()!;
                            patternKind = shapes.TryGetProperty(targetId, out var patternTarget)
                                ? patternTarget.GetProperty("type").GetString() : SmithyPreludeKind(targetId);
                        }
                        ValidationContext.Require(memberContainer is not null && patternKind is null ||
                            patternKind is "string" or "enum", traitPath, "document.trait_target",
                            "Smithy pattern requires a string or a member targeting a string.");
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.String, traitPath,
                            "document.trait_value", "The Smithy pattern trait requires a string value.");
                        SchemaPattern.Validate(trait.Value.GetString()!, budget, traitPath);
                        continue;
                    case "smithy.api#range":
                        var rangeKind = kind;
                        if (memberContainer is not null)
                        {
                            var targetId = shape.GetProperty("target").GetString()!;
                            rangeKind = shapes.TryGetProperty(targetId, out var rangeTarget)
                                ? rangeTarget.GetProperty("type").GetString() : SmithyPreludeKind(targetId);
                        }
                        ValidationContext.Require(memberContainer is not null && rangeKind is null || IsNumericKind(rangeKind),
                            traitPath, "document.trait_target",
                            "Smithy range requires a numeric shape or a member targeting one.");
                        ValidateRangeDeclaration(trait.Value, rangeKind, traitPath);
                        continue;
                    case "smithy.api#length":
                        var lengthKind = kind;
                        if (memberContainer is not null)
                        {
                            var targetId = shape.GetProperty("target").GetString()!;
                            lengthKind = shapes.TryGetProperty(targetId, out var lengthTarget)
                                ? lengthTarget.GetProperty("type").GetString() : SmithyPreludeKind(targetId);
                        }
                        ValidationContext.Require(memberContainer is not null && lengthKind is null ||
                            lengthKind is "string" or "enum" or "blob" or "list" or "map",
                            traitPath, "document.trait_target",
                            "Smithy length requires a string, blob, list or map, or a member targeting one.");
                        ValidateLengthDeclaration(trait.Value, traitPath);
                        continue;
                    case "smithy.api#requestCompression":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object, traitPath,
                            "document.trait_value", "The Smithy requestCompression trait requires an object value.");
                        ValidationContext.Require(trait.Value.TryGetProperty("encodings", out var encodings) &&
                            encodings.ValueKind == JsonValueKind.Array && encodings.GetArrayLength() > 0,
                            traitPath + "/encodings", "document.request_compression",
                            "Request compression requires a nonempty encodings array.");
                        var encodingIndex = 0;
                        foreach (var encoding in encodings.EnumerateArray())
                        {
                            budget.Work();
                            ValidationContext.Require(encoding.ValueKind == JsonValueKind.String &&
                                string.Equals(encoding.GetString(), "gzip", StringComparison.OrdinalIgnoreCase),
                                traitPath + "/encodings/" + (encodingIndex++).ToString(System.Globalization.CultureInfo.InvariantCulture),
                                "document.request_compression", "Only the case-insensitive gzip compression algorithm is defined.");
                        }
                        continue;
                    case "smithy.api#externalDocumentation":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.Object &&
                            trait.Value.EnumerateObject().Any(), traitPath, "document.external_documentation",
                            "Smithy external documentation requires a nonempty map of named URLs.");
                        foreach (var link in trait.Value.EnumerateObject())
                        {
                            var linkPath = traitPath + "/" + Escape(link.Name);
                            budget.Work(link.Name.Length, linkPath);
                            ValidationContext.Require(link.Name.Length > 0, linkPath, "document.external_documentation",
                                "An external documentation link requires a nonempty name.");
                            ValidationContext.Require(link.Value.ValueKind == JsonValueKind.String &&
                                link.Value.GetString()!.Length > 0, linkPath, "document.external_documentation",
                                "An external documentation link requires a nonempty URL string.");
                            var linkText = link.Value.GetString()!;
                            budget.Work(linkText.Length, linkPath);
                            ValidationContext.Require(Uri.TryCreate(linkText, UriKind.Absolute, out _) &&
                                Uri.IsWellFormedUriString(linkText, UriKind.Absolute), linkPath,
                                "document.external_documentation", "An external documentation link requires a well-formed absolute URL.");
                        }
                        continue;
                    case "smithy.api#documentation":
                    case "smithy.api#title":
                    case "smithy.api#since":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.String, traitPath,
                            "document.trait_value", "The Smithy documentation annotation requires a string value.");
                        continue;
                    case "smithy.api#error":
                        ValidationContext.Require(trait.Value.ValueKind == JsonValueKind.String &&
                            trait.Value.GetString() is "client" or "server", traitPath,
                            "document.trait_value", "The Smithy error trait requires client or server.");
                        continue;
                    case "smithy.api#required":
                    case "smithy.api#httpChecksumRequired":
                    case "smithy.api#optionalAuth":
                    case "smithy.api#httpPayload":
                    case "smithy.api#streaming":
                    case "smithy.api#requiresLength":
                    case "smithy.api#unitType":
                    case "smithy.api#internal":
                    case "smithy.api#sensitive":
                    case "smithy.api#clientOptional":
                    case "smithy.api#addedDefault":
                    case "smithy.api#sparse":
                    case "smithy.api#uniqueItems":
                    case "smithy.api#private":
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
                Visit(trait.Name, depth + 1, traitDefinition: true, sourceId: sourceId, referencePath: traitPath);
                _traitNodes.Add((trait.Name, trait.Value, traitPath));
            }
        }

        internal void ValidateTraitNodes()
        {
            foreach (var (id, value, path) in _traitNodes)
            {
                ValidateTraitNode(id, value, path, 0);
            }
        }

        private void ValidateTraitNode(string id, JsonElement value, string path, int depth,
            JsonElement memberShape = default)
        {
            budget.Work(path: path);
            if (depth > Capabilities.MaxReferenceDepth)
            {
                ValidationContext.Fail(DocumentValidationStatus.Indeterminate, path, "limit.reference_depth",
                    "The Smithy trait value dependency depth limit was exceeded.");
            }
            var supplied = shapes.TryGetProperty(id, out var shape);
            var kind = supplied ? shape.GetProperty("type").GetString() : SmithyPreludeKind(id);
            if (kind == "document") { return; }
            var compatible = kind switch
            {
                "string" or "enum" or "blob" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "timestamp" or "bigInteger" or "bigDecimal" or "float" or "double" =>
                    value.ValueKind is JsonValueKind.String or JsonValueKind.Number,
                "byte" or "short" or "integer" or "long" or "intEnum" => value.ValueKind == JsonValueKind.Number,
                "list" => value.ValueKind == JsonValueKind.Array,
                "map" or "structure" or "union" => value.ValueKind == JsonValueKind.Object,
                _ => false
            };
            ValidationContext.Require(compatible, path, "document.trait_node",
                "The Smithy trait node value is incompatible with its declared shape.");
            if (kind == "blob")
            {
                var text = value.GetString()!;
                budget.Work(text.Length, path);
                ValidationContext.Require(Convert.TryFromBase64String(text, new byte[text.Length], out _),
                    path, "document.trait_node", "A Smithy blob trait value must contain valid base64.");
            }
            if (IsNumericKind(kind) || kind == "intEnum")
            {
                if (kind is "float" or "double" && value.ValueKind == JsonValueKind.String)
                {
                    ValidationContext.Require(value.GetString() is "NaN" or "Infinity" or "-Infinity",
                        path, "document.trait_node", "A floating-point trait string must be NaN, Infinity or -Infinity.");
                }
                else
                {
                    var number = kind is "bigInteger" or "bigDecimal"
                        ? ReadSmithyNumber(value, path, "smithy.numeric_string_trait")
                        : JsonNumber.Read(value, budget, path);
                    ValidationContext.Require((kind is not ("byte" or "short" or "integer" or "long" or "bigInteger" or "intEnum") ||
                        number.IsInteger) && FitsNumericRange(kind!, number),
                        path, "document.trait_node", "A numeric Smithy trait value must fit its declared type.");
                    if (kind is "float" or "double")
                    {
                        ValidationContext.Require(kind == "float"
                            ? value.TryGetSingle(out var single) && float.IsFinite(single)
                            : value.TryGetDouble(out var real) && double.IsFinite(real),
                            path, "document.trait_node", "A floating-point Smithy trait value must fit its declared type.");
                    }
                }
            }
            if (kind == "timestamp")
            {
                if (value.ValueKind == JsonValueKind.String) { ValidateTimestampString(value.GetString()!, path); }
                else { ValidateTimestampNumber(value, path, "document.trait_timestamp"); }
            }
            ValidateTraitNodeConstraints(kind!, shape, memberShape, value, path);
            if (kind == "list")
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    var itemPath = path + "/" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    budget.Work(path: itemPath);
                    if (item.ValueKind == JsonValueKind.Null && TryTrait(shape, "smithy.api#sparse", out _)) { continue; }
                    var listMember = shape.GetProperty("member");
                    ValidateTraitNode(listMember.GetProperty("target").GetString()!, item, itemPath, depth + 1, listMember);
                }
            }
            if (kind == "map")
            {
                foreach (var item in value.EnumerateObject())
                {
                    var itemPath = path + "/" + Escape(item.Name);
                    budget.Work(item.Name.Length, itemPath);
                    var keyMember = shape.GetProperty("key");
                    using var keyDocument = JsonDocument.Parse("\"" + JsonEncodedText.Encode(item.Name).ToString() + "\"");
                    ValidateTraitNode(keyMember.GetProperty("target").GetString()!,
                        keyDocument.RootElement, itemPath, depth + 1, keyMember);
                    if (item.Value.ValueKind == JsonValueKind.Null && TryTrait(shape, "smithy.api#sparse", out _)) { continue; }
                    var valueMember = shape.GetProperty("value");
                    ValidateTraitNode(valueMember.GetProperty("target").GetString()!, item.Value, itemPath, depth + 1, valueMember);
                }
            }
            if (kind is "structure" or "union")
            {
                var hasMembers = supplied && shape.TryGetProperty("members", out _);
                var members = hasMembers ? shape.GetProperty("members") : default;
                ValidationContext.Require(kind != "union" || value.EnumerateObject().Count() == 1,
                    path, "document.trait_node", "A Smithy union trait value must select exactly one member.");
                foreach (var item in value.EnumerateObject())
                {
                    var itemPath = path + "/" + Escape(item.Name);
                    budget.Work(path: itemPath);
                    ValidationContext.Require(hasMembers && members.TryGetProperty(item.Name, out _),
                        itemPath, "document.trait_node", "A Smithy trait value contains an undeclared structure or union member.");
                    var structureMember = members.GetProperty(item.Name);
                    ValidateTraitNode(structureMember.GetProperty("target").GetString()!,
                        item.Value, itemPath, depth + 1, structureMember);
                }
                if (kind == "structure" && hasMembers)
                {
                    foreach (var member in members.EnumerateObject())
                    {
                        budget.Work(path: path);
                        ValidationContext.Require(!TryTrait(member.Value, "smithy.api#required", out _) ||
                            value.TryGetProperty(member.Name, out _), path + "/" + Escape(member.Name),
                            "document.trait_node", "A Smithy trait value must supply every required structure member.");
                    }
                }
            }
        }

        private void ValidateTraitNodeConstraints(string kind, JsonElement shape, JsonElement member,
            JsonElement value, string path)
        {
            if (TryTrait(member, "smithy.api#length", out var length) ||
                TryTrait(shape, "smithy.api#length", out length))
            {
                var count = 0;
                if (kind is "string" or "enum")
                {
                    var text = value.GetString()!;
                    budget.Work(text.Length, path);
                    foreach (var rune in text.EnumerateRunes()) { count++; }
                }
                else if (kind == "blob")
                {
                    var text = value.GetString()!;
                    budget.Work(text.Length, path);
                    ValidationContext.Require(Convert.TryFromBase64String(text, new byte[text.Length], out count),
                        path, "document.trait_node", "A Smithy blob trait value must contain valid base64.");
                }
                else if (kind == "list") { count = value.GetArrayLength(); }
                else
                {
                    foreach (var item in value.EnumerateObject()) { budget.Work(path: path); count++; }
                }
                var size = new JsonNumber(false, count.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
                ValidationContext.Require(
                    (!length.TryGetProperty("min", out var min) || size.CompareTo(JsonNumber.Read(min, budget, path)) >= 0) &&
                    (!length.TryGetProperty("max", out var max) || size.CompareTo(JsonNumber.Read(max, budget, path)) <= 0),
                    path, "document.trait_length", "A Smithy trait value must satisfy its effective length constraint.");
            }
            if (TryTrait(member, "smithy.api#range", out var range) ||
                TryTrait(shape, "smithy.api#range", out range))
            {
                if (value.ValueKind == JsonValueKind.String && kind is "float" or "double")
                {
                    ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, "smithy.special_range_trait",
                        "Range checks on special floating-point trait values are not supported.");
                }
                var number = kind is "bigInteger" or "bigDecimal"
                    ? ReadSmithyNumber(value, path, "smithy.numeric_string_trait")
                    : JsonNumber.Read(value, budget, path);
                ValidationContext.Require(
                    (!range.TryGetProperty("min", out var min) ||
                        number.CompareTo(ReadSmithyNumber(min, path, "smithy.numeric_string_range")) >= 0) &&
                    (!range.TryGetProperty("max", out var max) ||
                        number.CompareTo(ReadSmithyNumber(max, path, "smithy.numeric_string_range")) <= 0),
                    path, "document.trait_range", "A Smithy trait value must satisfy its effective range constraint.");
            }
            if (TryTrait(member, "smithy.api#pattern", out _) || TryTrait(shape, "smithy.api#pattern", out _))
            {
                ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, "smithy.pattern_trait",
                    "Pattern matching on Smithy trait values is not supported.");
            }
        }

        private void ValidateLegacyEnum(JsonElement value, string path)
        {
            ValidationContext.Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0,
                path, "document.enum_trait", "A Smithy enum trait requires a nonempty array.");
            var values = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            bool? named = null;
            foreach (var item in value.EnumerateArray())
            {
                var at = path + "/" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                budget.Work(path: at);
                ValidationContext.Require(item.ValueKind == JsonValueKind.Object, at, "document.enum_trait",
                    "A Smithy enum definition must be an object.");
                ValidationContext.Require(item.TryGetProperty("value", out var entry) &&
                    entry.ValueKind == JsonValueKind.String && entry.GetString()!.Length > 0,
                    at + "/value", "document.enum_trait_value", "A Smithy enum definition requires a nonempty string value.");
                var text = entry.GetString()!;
                budget.Work(text.Length, at + "/value");
                ValidationContext.Require(values.Add(text), at + "/value", "document.enum_trait_value",
                    "Smithy enum trait values must be unique.");
                var hasName = item.TryGetProperty("name", out var name);
                ValidationContext.Require(named is null || named == hasName, at + "/name", "document.enum_trait_name",
                    "Smithy enum trait definitions must either all declare names or all omit them.");
                named = hasName;
                if (hasName)
                {
                    ValidationContext.Require(name.ValueKind == JsonValueKind.String, at + "/name",
                        "document.enum_trait_name", "A Smithy enum constant name must be a string.");
                    var label = name.GetString()!;
                    budget.Work(label.Length, at + "/name");
                    ValidationContext.Require(SmithyEnumConstantName().IsMatch(label) && names.Add(label),
                        at + "/name", "document.enum_trait_name",
                        "Smithy enum constant names must be valid ASCII identifiers and unique.");
                }
                if (item.TryGetProperty("documentation", out var documentation))
                {
                    ValidationContext.Require(documentation.ValueKind == JsonValueKind.String,
                        at + "/documentation", "document.enum_trait_metadata", "Enum documentation must be a string.");
                }
                if (item.TryGetProperty("deprecated", out var deprecated))
                {
                    ValidationContext.Require(deprecated.ValueKind is JsonValueKind.True or JsonValueKind.False,
                        at + "/deprecated", "document.enum_trait_metadata", "Enum deprecated must be a boolean.");
                }
                if (item.TryGetProperty("tags", out var tags))
                {
                    ValidationContext.Require(tags.ValueKind == JsonValueKind.Array, at + "/tags",
                        "document.enum_trait_metadata", "Enum tags must be an array of nonempty strings.");
                    var tagIndex = 0;
                    foreach (var tag in tags.EnumerateArray())
                    {
                        var tagPath = at + "/tags/" + (tagIndex++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        budget.Work(path: tagPath);
                        ValidationContext.Require(tag.ValueKind == JsonValueKind.String && tag.GetString()!.Length > 0,
                            tagPath, "document.enum_trait_metadata", "Enum tags must be nonempty strings.");
                    }
                }
            }
        }

        private void ValidateLengthDeclaration(JsonElement value, string path)
        {
            ValidationContext.Require(value.ValueKind == JsonValueKind.Object &&
                (value.TryGetProperty("min", out _) || value.TryGetProperty("max", out _)),
                path, "document.length_bounds", "A Smithy length requires at least one min or max bound.");
            foreach (var name in new[] { "min", "max" })
            {
                if (!value.TryGetProperty(name, out var bound)) { continue; }
                var at = path + "/" + name;
                budget.Work(path: at);
                ValidationContext.Require(bound.ValueKind == JsonValueKind.Number, at, "document.length_bound",
                    "A Smithy length bound must be a signed 64-bit integer.");
                var number = JsonNumber.Read(bound, budget, at);
                ValidationContext.Require(number.IsInteger &&
                    number.CompareTo(new(true, "9223372036854775808", 0)) >= 0 &&
                    number.CompareTo(new(false, "9223372036854775807", 0)) <= 0,
                    at, "document.length_bound", "A Smithy length bound must be a signed 64-bit integer.");
            }
        }

        private void ValidateRangeDeclaration(JsonElement value, string? kind, string path)
        {
            ValidationContext.Require(value.ValueKind == JsonValueKind.Object &&
                (value.TryGetProperty("min", out _) || value.TryGetProperty("max", out _)),
                path, "document.range_bounds", "A Smithy range requires at least one min or max bound.");
            foreach (var name in new[] { "min", "max" })
            {
                if (!value.TryGetProperty(name, out var bound)) { continue; }
                var at = path + "/" + name;
                budget.Work(path: at);
                ValidationContext.Require(bound.ValueKind is JsonValueKind.Number or JsonValueKind.String, at, "document.range_bound",
                    "A Smithy range bound must be numeric.");
                var number = ReadSmithyNumber(bound, at, "smithy.numeric_string_range");
                ValidationContext.Require((kind is null or "float" or "double" or "bigDecimal" || number.IsInteger) &&
                    FitsNumericRange(kind, number), at, "document.range_bound",
                    "A Smithy range bound must fit its target numeric kind.");
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

        private HashSet<string> DeclaredErrors(JsonElement operation, string path)
        {
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
            return declaredErrors;
        }

        private void ValidateExamples(JsonElement operation, JsonElement trait, string path)
        {
            ValidationContext.Require(trait.ValueKind == JsonValueKind.Array, path, "document.examples",
                "Smithy operation examples require an array.");
            var declaredErrors = DeclaredErrors(operation, path);
            var index = 0;
            foreach (var example in trait.EnumerateArray())
            {
                var at = path + "/" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                budget.Work(path: at);
                ValidationContext.Require(example.ValueKind == JsonValueKind.Object, at, "document.example",
                    "A Smithy operation example must be an object.");
                ValidationContext.Require(example.TryGetProperty("title", out var title) &&
                    title.ValueKind == JsonValueKind.String, at + "/title", "document.example_title",
                    "A Smithy operation example requires a string title.");
                if (example.TryGetProperty("documentation", out var documentation))
                {
                    ValidationContext.Require(documentation.ValueKind == JsonValueKind.String, at + "/documentation",
                        "document.example_documentation", "Smithy example documentation must be a string.");
                }
                var hasError = example.TryGetProperty("error", out var error);
                ValidationContext.Require(!hasError || !example.TryGetProperty("output", out _), at,
                    "document.example_conflict", "A Smithy example cannot define both output and error.");
                if (example.TryGetProperty("allowConstraintErrors", out var allow))
                {
                    ValidationContext.Require(hasError && allow.ValueKind is JsonValueKind.True or JsonValueKind.False,
                        at + "/allowConstraintErrors", "document.example_constraints",
                        "Smithy allowConstraintErrors requires a Boolean value and an error example.");
                }
                if (!hasError) { continue; }
                ValidationContext.Require(error.ValueKind == JsonValueKind.Object, at + "/error",
                    "document.example_error", "A Smithy error example must be an object.");
                if (error.TryGetProperty("shapeId", out var shapeId))
                {
                    var targetPath = at + "/error/shapeId";
                    ValidationContext.Require(shapeId.ValueKind == JsonValueKind.String &&
                        IsSmithyShapeId(shapeId.GetString()!) && declaredErrors.Contains(shapeId.GetString()!),
                        targetPath, "document.example_error",
                        "A Smithy example error shape ID must be declared in this operation's errors list.");
                }
            }
        }

        private void ValidateIdempotent(JsonElement operation, JsonElement trait, string path)
        {
            ValidationContext.Require(!trait.TryGetProperty("exists", out _) ||
                !trait.TryGetProperty("notFound", out _), path, "document.idempotent_conflict",
                "Smithy idempotent exists and notFound properties are mutually exclusive.");
            var declaredErrors = DeclaredErrors(operation, path);
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
