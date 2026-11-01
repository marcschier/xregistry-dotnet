// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;

namespace XRegistry.Validation;

internal sealed class JsonSchemaSyntax(string format, ValidationContext context)
{
    private readonly List<SchemaDocument> _documents = [];
    private readonly Dictionary<string, SchemaResource> _byUri = new(StringComparer.Ordinal);
    private readonly List<(SchemaResource Resource, string Reference, string Path)> _references = [];
    private readonly int _draft = format.EndsWith("draft-07", StringComparison.OrdinalIgnoreCase) ? 7
        : format.EndsWith("2019-09", StringComparison.OrdinalIgnoreCase) ? 2019 : 2020;

    internal static bool IsFormat(string format)
        => format.Equals("JsonSchema/draft-07", StringComparison.OrdinalIgnoreCase)
        || format.Equals("JsonSchema/draft/2019-09", StringComparison.OrdinalIgnoreCase)
        || format.Equals("JsonSchema/draft/2020-12", StringComparison.OrdinalIgnoreCase);

    internal async ValueTask ValidateAsync(ReadOnlyMemory<byte> bytes,
        Action<JsonElement, IReadOnlySet<string>>? validatedRoot = null)
    {
        try
        {
            var documentUri = context.Options.DocumentUri?.AbsoluteUri ?? "";
            var fragmentAt = documentUri.IndexOf('#');
            Load(bytes, fragmentAt < 0 ? documentUri : documentUri[..fragmentAt]);
            var declaredPaths = validatedRoot is null ? null : new HashSet<string>(_documents[0].Visited, StringComparer.Ordinal);
            for (var i = 0; i < _references.Count; i++)
            {
                var (source, reference, path) = _references[i];
                context.Work(1, path);
                var referenceFragmentAt = reference.IndexOf('#');
                var location = referenceFragmentAt < 0 ? reference : reference[..referenceFragmentAt];
                var fragment = referenceFragmentAt < 0 ? "" : SchemaJson.DecodeFragment(
                    reference[(referenceFragmentAt + 1)..], path, "reference.pointer", context);
                var target = source;
                if (location.Length != 0)
                {
                    var key = ResolveUri(source.BaseUri, location);
                    if (!_byUri.TryGetValue(key, out target))
                    {
                        target = Load(await context.ResolveAsync(key, path).ConfigureAwait(false), key);
                    }
                }

                if (fragment.Length == 0 || fragment.StartsWith('/'))
                {
                    var (value, localPath) = SchemaJson.DecodedPointer(target.Root, fragment, path, context);
                    var targetPath = target.Path + localPath[1..];
                    Validate(value, Scope(target, targetPath), targetPath);
                }
                else
                {
                    ValidationContext.Require(target.Anchors.TryGetValue(fragment, out var targetPath),
                        path, "reference.not_found", $"The anchor '{fragment}' is not defined.");
                    var (value, _) = SchemaJson.DecodedPointer(target.Document.Json.RootElement, targetPath[1..], path, context);
                    Validate(value, Scope(target, targetPath), targetPath);
                }
            }
            validatedRoot?.Invoke(_documents[0].Json.RootElement, declaredPaths!);
        }
        finally
        {
            foreach (var document in _documents)
            {
                document.Json.Dispose();
            }
        }
    }

    private SchemaResource Load(ReadOnlyMemory<byte> bytes, string uri)
    {
        var document = new SchemaDocument(context.ReadJson(bytes));
        _documents.Add(document);
        var baseUri = uri;
        if (document.Json.RootElement.ValueKind == JsonValueKind.Object
            && !(_draft == 7 && document.Json.RootElement.TryGetProperty("$ref", out _))
            && document.Json.RootElement.TryGetProperty("$id", out var id))
        {
            baseUri = Identifier(SchemaJson.String(id, "$/$id"), uri, "$/$id");
        }
        var resource = new SchemaResource(document, document.Json.RootElement, "$", baseUri);
        document.Resources.Add("$", resource);
        Register(uri, resource, "$");
        Register(baseUri, resource, "$/$id");
        Validate(document.Json.RootElement, resource, "$");
        return resource;
    }

    private void Register(string uri, SchemaResource resource, string path)
    {
        ValidationContext.Require(!_byUri.TryGetValue(uri, out var existing) || existing == resource,
            path, "schema.duplicate_id", "Different schema resources declare the same identifier.");
        _byUri[uri] = resource;
    }

    private SchemaResource Scope(SchemaResource resource, string path)
    {
        var candidate = path;
        while (true)
        {
            context.Work(1, path);
            if (resource.Document.Resources.TryGetValue(candidate, out var scope))
            {
                return scope;
            }
            candidate = candidate[..candidate.LastIndexOf('/')];
        }
    }

    private void Validate(JsonElement schema, SchemaResource resource, string path)
    {
        var document = resource.Document;
        context.Work(1, path);
        if (!document.Visited.Add(path) || schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return;
        }

        SchemaJson.Object(schema, path);
        if (_draft == 7 && schema.TryGetProperty("$ref", out var draftSevenReference))
        {
            var referencePath = SchemaJson.Path(path, "$ref");
            var reference = SchemaJson.String(draftSevenReference, referencePath);
            CheckUri(reference, referencePath, false);
            _references.Add((resource, reference, referencePath));
            return;
        }
        if (path != resource.Path && schema.TryGetProperty("$id", out var embeddedId))
        {
            var idPath = SchemaJson.Path(path, "$id");
            var baseUri = Identifier(SchemaJson.String(embeddedId, idPath), resource.BaseUri, idPath);
            if (_draft == 7)
            {
                Unsupported(idPath, "schema.embedded_resource", "Draft-07 embedded resource identifiers are not implemented.");
            }
            context.Work(baseUri.Length, idPath);
            resource = new SchemaResource(document, schema, path, baseUri);
            document.Resources.Add(path, resource);
            Register(baseUri, resource, idPath);
        }
        foreach (var property in schema.EnumerateObject())
        {
            var childPath = SchemaJson.Path(path, property.Name);
            var value = property.Value;
            switch (property.Name)
            {
                case "$schema":
                    var dialectUri = SchemaJson.String(value, childPath);
                    CheckUri(dialectUri, childPath, true);
                    ValidationContext.Require(path == resource.Path,
                        childPath, "schema.dialect_location", "A dialect declaration requires a schema resource root.");
                    var dialect = dialectUri.TrimEnd('#');
                    var expected = _draft switch
                    {
                        7 => "http://json-schema.org/draft-07/schema",
                        2019 => "https://json-schema.org/draft/2019-09/schema",
                        _ => "https://json-schema.org/draft/2020-12/schema",
                    };
                    if (dialect != expected)
                    {
                        var recognized = dialect is "http://json-schema.org/draft-07/schema"
                            or "https://json-schema.org/draft/2019-09/schema" or "https://json-schema.org/draft/2020-12/schema";
                        var mismatch = recognized && path == "$";
                        ValidationContext.Fail(mismatch ? DocumentValidationStatus.Invalid : DocumentValidationStatus.Unsupported,
                            childPath, mismatch ? "schema.dialect_mismatch" : "schema.dialect_unsupported",
                            "The schema dialect does not match the explicitly supported format.");
                    }
                    break;
                case "$id":
                    break;
                case "$ref":
                    var reference = SchemaJson.String(value, childPath);
                    CheckUri(reference, childPath, false);
                    _references.Add((resource, reference, childPath));
                    break;
                case "$anchor" when _draft != 7:
                    var anchor = SchemaJson.String(value, childPath);
                    CheckAnchor(anchor, childPath);
                    ValidationContext.Require(resource.Anchors.TryAdd(anchor, path), childPath,
                        "schema.duplicate_anchor", "A schema anchor must be unique within a resource.");
                    break;
                case "$dynamicRef" when _draft == 2020:
                case "$recursiveRef" when _draft != 7:
                    CheckUri(SchemaJson.String(value, childPath), childPath, false);
                    Unsupported(childPath, "schema.dynamic_reference", "Dynamic/recursive reference semantics are not implemented.");
                    break;
                case "$dynamicAnchor" when _draft == 2020:
                case "$recursiveAnchor" when _draft == 2020:
                    CheckAnchor(SchemaJson.String(value, childPath), childPath);
                    Unsupported(childPath, "schema.dynamic_reference", "Dynamic/recursive reference semantics are not implemented.");
                    break;
                case "$recursiveAnchor" when _draft == 2019:
                    SchemaJson.Boolean(value, childPath);
                    Unsupported(childPath, "schema.dynamic_reference", "Dynamic/recursive reference semantics are not implemented.");
                    break;
                case "$vocabulary" when _draft != 7:
                    ValidationContext.Require(path == "$", childPath, "schema.vocabulary_location",
                        "A vocabulary declaration must not appear in a subschema.");
                    SchemaJson.Object(value, childPath);
                    foreach (var vocabulary in value.EnumerateObject())
                    {
                        CheckUri(vocabulary.Name, childPath, true);
                        SchemaJson.Boolean(vocabulary.Value, childPath);
                    }
                    break;
                case "type":
                    if (value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var type in SchemaJson.Strings(value, childPath, true))
                        {
                            CheckType(type, childPath);
                        }
                    }
                    else
                    {
                        CheckType(SchemaJson.String(value, childPath), childPath);
                    }
                    break;
                case "$defs" when _draft != 7:
                    goto case "properties";
                case "dependentSchemas" when _draft != 7:
                    goto case "properties";
                case "properties":
                case "definitions":
                case "patternProperties":
                    SchemaJson.Object(value, childPath);
                    foreach (var child in value.EnumerateObject())
                    {
                        if (property.Name == "patternProperties")
                        {
                            SchemaPattern.Validate(child.Name, context, childPath);
                        }
                        Validate(child.Value, resource, SchemaJson.Path(childPath, child.Name));
                    }
                    break;
                case "dependencies":
                    SchemaJson.Object(value, childPath);
                    foreach (var child in value.EnumerateObject())
                    {
                        if (child.Value.ValueKind == JsonValueKind.Array)
                        {
                            SchemaJson.Strings(child.Value, SchemaJson.Path(childPath, child.Name));
                        }
                        else
                        {
                            Validate(child.Value, resource, SchemaJson.Path(childPath, child.Name));
                        }
                    }
                    break;
                case "dependentRequired" when _draft != 7:
                    SchemaJson.Object(value, childPath);
                    foreach (var child in value.EnumerateObject())
                    {
                        SchemaJson.Strings(child.Value, SchemaJson.Path(childPath, child.Name));
                    }
                    break;
                case "required":
                    SchemaJson.Strings(value, childPath);
                    break;
                case "minimum":
                case "maximum":
                case "exclusiveMinimum":
                case "exclusiveMaximum":
                    ValidationContext.Require(value.ValueKind == JsonValueKind.Number,
                        childPath, "schema.number", "A numeric bound is required.");
                    break;
                case "multipleOf":
                    var multiple = JsonNumber.Read(value, context, childPath);
                    ValidationContext.Require(!multiple.Negative && !multiple.IsZero, childPath, "schema.positive", "A positive number is required.");
                    break;
                case "maxContains" when _draft != 7:
                case "minContains" when _draft != 7:
                    goto case "minItems";
                case "minItems":
                case "maxItems":
                case "minLength":
                case "maxLength":
                case "minProperties":
                case "maxProperties":
                    var number = JsonNumber.Read(value, context, childPath);
                    ValidationContext.Require(!number.Negative && number.IsInteger, childPath,
                        "schema.nonnegative_integer", "A nonnegative integer is required.");
                    break;
                case "prefixItems" when _draft == 2020:
                    SchemaArray(value, resource, childPath);
                    break;
                case "items":
                    if (_draft != 2020 && value.ValueKind == JsonValueKind.Array)
                    {
                        SchemaArray(value, resource, childPath);
                        break;
                    }
                    Validate(value, resource, childPath);
                    break;
                case "allOf":
                case "anyOf":
                case "oneOf":
                    SchemaArray(value, resource, childPath);
                    break;
                case "unevaluatedItems" when _draft != 7:
                case "unevaluatedProperties" when _draft != 7:
                case "contentSchema" when _draft != 7:
                    goto case "not";
                case "not":
                case "if":
                case "then":
                case "else":
                case "contains":
                case "additionalItems" when _draft != 2020:
                case "additionalProperties":
                case "propertyNames":
                    Validate(value, resource, childPath);
                    break;
                case "enum":
                    SchemaJson.Array(value, childPath);
                    if (_draft == 7)
                    {
                        ValidationContext.Require(value.GetArrayLength() > 0, childPath, "schema.empty", "Draft-07 enums must not be empty.");
                        SchemaJson.UniqueValues(value, context, childPath);
                    }
                    break;
                case "readOnly":
                case "writeOnly":
                case "deprecated" when _draft != 7:
                case "uniqueItems":
                    SchemaJson.Boolean(value, childPath);
                    break;
                case "pattern":
                    SchemaPattern.Validate(SchemaJson.String(value, childPath), context, childPath);
                    break;
                case "title":
                case "description":
                case "$comment":
                case "format":
                case "contentMediaType":
                case "contentEncoding":
                    SchemaJson.String(value, childPath);
                    break;
                case "examples":
                    SchemaJson.Array(value, childPath);
                    break;
                    // Unknown individual keywords are annotations under the standard dialects.
            }
        }
    }

    private void SchemaArray(JsonElement value, SchemaResource resource, string path)
    {
        SchemaJson.Array(value, path);
        ValidationContext.Require(value.GetArrayLength() > 0, path, "schema.empty", "A schema array must not be empty.");
        for (var i = 0; i < value.GetArrayLength(); i++)
        {
            Validate(value[i], resource, SchemaJson.Path(path, i.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static void CheckType(string type, string path)
        => ValidationContext.Require(type is "null" or "boolean" or "object" or "array"
            or "number" or "integer" or "string", path, "schema.type", $"'{type}' is not a JSON Schema type.");

    private void CheckAnchor(string anchor, string path)
        => ValidationContext.Require(anchor.Length > 0
            && (char.IsAsciiLetter(anchor[0]) || _draft == 2020 && anchor[0] == '_')
            && anchor.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' || _draft == 2019 && c == ':'),
            path, "schema.anchor", "Invalid schema anchor for the selected dialect.");

    private string Identifier(string value, string baseUri, string path)
    {
        CheckUri(value, path, false);
        var resolved = ResolveUri(baseUri, value);
        if (_draft == 7)
        {
            return resolved;
        }
        var fragmentAt = value.IndexOf('#');
        ValidationContext.Require(fragmentAt < 0 || fragmentAt == value.Length - 1,
            path, "schema.id_fragment", "Modern schema identifiers must not have nonempty fragments.");
        ValidationContext.Require(Uri.TryCreate(resolved, UriKind.Absolute, out _),
            path, "schema.id_absolute", "A modern schema identifier must resolve to an absolute URI.");
        var resolvedFragmentAt = resolved.IndexOf('#');
        return resolvedFragmentAt < 0 ? resolved : resolved[..resolvedFragmentAt];
    }

    private static void CheckUri(string value, string path, bool absolute)
    {
        var separator = value.IndexOf('#');
        var document = separator < 0 ? value : value[..separator];
        var fragmentValid = separator < 0 || Uri.IsWellFormedUriString(
            "urn:schema-fragment#" + value[(separator + 1)..], UriKind.Absolute);
        ValidationContext.Require(!value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) &&
            !value.Contains('\\', StringComparison.Ordinal) && fragmentValid &&
            (document.Length == 0 && !absolute || Uri.IsWellFormedUriString(document,
                absolute ? UriKind.Absolute : UriKind.RelativeOrAbsolute)),
            path, "schema.uri", "A valid URI reference is required.");
    }

    private static string ResolveUri(string baseUri, string reference)
        => Uri.TryCreate(baseUri, UriKind.Absolute, out var absolute) && Uri.TryCreate(absolute, reference, out var resolved)
            ? resolved.AbsoluteUri : reference;

    private static void Unsupported(string path, string code, string detail)
        => ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, code, detail);

    private sealed class SchemaDocument(JsonDocument json)
    {
        internal JsonDocument Json { get; } = json;
        internal HashSet<string> Visited { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, SchemaResource> Resources { get; } = new(StringComparer.Ordinal);
    }

    private sealed class SchemaResource(SchemaDocument document, JsonElement root, string path, string baseUri)
    {
        internal SchemaDocument Document { get; } = document;
        internal JsonElement Root { get; } = root;
        internal string Path { get; } = path;
        internal string BaseUri { get; } = baseUri;
        internal Dictionary<string, string> Anchors { get; } = new(StringComparer.Ordinal);
    }
}
