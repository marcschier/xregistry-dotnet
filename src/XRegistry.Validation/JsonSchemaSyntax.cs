using System.Globalization;
using System.Text.Json;

namespace XRegistry.Validation;

internal sealed class JsonSchemaSyntax(string format, ValidationContext context)
{
    private readonly List<SchemaDocument> _documents = [];
    private readonly Dictionary<string, SchemaDocument> _byUri = new(StringComparer.Ordinal);
    private readonly List<(SchemaDocument Document, string Reference, string Path)> _references = [];
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
            Load(bytes, context.Options.DocumentUri?.AbsoluteUri ?? "");
            var declaredPaths = validatedRoot is null ? null : new HashSet<string>(_documents[0].Visited, StringComparer.Ordinal);
            for (var i = 0; i < _references.Count; i++)
            {
                var (source, reference, path) = _references[i];
                context.Work(1, path);
                var fragmentAt = reference.IndexOf('#');
                var location = fragmentAt < 0 ? reference : reference[..fragmentAt];
                var fragment = fragmentAt < 0 ? "#" : reference[fragmentAt..];
                var target = source;
                if (location.Length != 0)
                {
                    var key = ResolveUri(source.BaseUri, location);
                    if (!_byUri.TryGetValue(key, out target))
                    {
                        target = Load(await context.ResolveAsync(key, path).ConfigureAwait(false), key);
                    }
                }

                if (fragment == "#" || fragment.StartsWith("#/", StringComparison.Ordinal) || fragment.StartsWith("#%", StringComparison.Ordinal))
                {
                    var (value, targetPath) = SchemaJson.Pointer(target.Json.RootElement, fragment, path, context);
                    Validate(value, target, targetPath);
                }
                else
                {
                    ValidationContext.Require(target.Anchors.TryGetValue(fragment[1..], out var targetPath),
                        path, "reference.not_found", $"The anchor '{fragment}' is not defined.");
                    var (value, _) = SchemaJson.DecodedPointer(target.Json.RootElement, targetPath[1..], path, context);
                    Validate(value, target, targetPath);
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

    private SchemaDocument Load(ReadOnlyMemory<byte> bytes, string uri)
    {
        var document = new SchemaDocument(context.ReadJson(bytes), uri);
        _documents.Add(document);
        _byUri[uri] = document;
        if (document.Json.RootElement.ValueKind == JsonValueKind.Object
            && document.Json.RootElement.TryGetProperty("$id", out var id))
        {
            document.BaseUri = ResolveUri(uri, SchemaJson.String(id, "$/$id"));
            ValidationContext.Require(!_byUri.TryGetValue(document.BaseUri, out var existing) || existing == document,
                "$/$id", "schema.duplicate_id", "Different supplied documents declare the same schema identifier.");
            _byUri[document.BaseUri] = document;
        }
        Validate(document.Json.RootElement, document, "$");
        return document;
    }

    private void Validate(JsonElement schema, SchemaDocument document, string path)
    {
        context.Work(1, path);
        if (!document.Visited.Add(path) || schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return;
        }

        SchemaJson.Object(schema, path);
        foreach (var property in schema.EnumerateObject())
        {
            var childPath = SchemaJson.Path(path, property.Name);
            var value = property.Value;
            switch (property.Name)
            {
                case "$schema":
                    var dialect = SchemaJson.String(value, childPath).TrimEnd('#');
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
                        ValidationContext.Fail(recognized ? DocumentValidationStatus.Invalid : DocumentValidationStatus.Unsupported,
                            childPath, recognized ? "schema.dialect_mismatch" : "schema.dialect_unsupported",
                            "The schema dialect does not match the explicitly supported format.");
                    }
                    break;
                case "$id":
                    CheckUri(SchemaJson.String(value, childPath), childPath, false);
                    if (path != "$")
                    {
                        Unsupported(childPath, "schema.embedded_resource", "Embedded resource identifiers are not implemented.");
                    }
                    if (_draft != 7)
                    {
                        ValidationContext.Require(!value.GetString()!.TrimEnd('#').Contains('#'), childPath,
                            "schema.id_fragment", "Modern schema identifiers must not have nonempty fragments.");
                    }
                    break;
                case "$ref":
                    var reference = SchemaJson.String(value, childPath);
                    CheckUri(reference, childPath, false);
                    _references.Add((document, reference, childPath));
                    break;
                case "$anchor":
                    Modern(childPath);
                    var anchor = SchemaJson.String(value, childPath);
                    ValidationContext.Require(anchor.Length > 0 && (char.IsAsciiLetter(anchor[0]) || anchor[0] == '_')
                        && anchor.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'), childPath,
                        "schema.anchor", "Invalid schema anchor.");
                    ValidationContext.Require(document.Anchors.TryAdd(anchor, path), childPath,
                        "schema.duplicate_anchor", "A schema anchor must be unique within a document.");
                    break;
                case "$dynamicRef":
                case "$recursiveRef":
                case "$dynamicAnchor":
                    SchemaJson.String(value, childPath);
                    Unsupported(childPath, "schema.dynamic_reference", "Dynamic/recursive reference semantics are not implemented.");
                    break;
                case "$recursiveAnchor":
                    SchemaJson.Boolean(value, childPath);
                    Unsupported(childPath, "schema.dynamic_reference", "Dynamic/recursive reference semantics are not implemented.");
                    break;
                case "$vocabulary":
                    Modern(childPath);
                    SchemaJson.Object(value, childPath);
                    foreach (var vocabulary in value.EnumerateObject())
                    {
                        CheckUri(vocabulary.Name, childPath, true);
                        SchemaJson.Boolean(vocabulary.Value, childPath);
                        var prefix = $"https://json-schema.org/draft/{(_draft == 2019 ? "2019-09" : "2020-12")}/vocab/";
                        var known = vocabulary.Name.StartsWith(prefix, StringComparison.Ordinal)
                            && vocabulary.Name[prefix.Length..] is "core" or "applicator" or "validation" or "meta-data"
                                or "format" or "format-annotation" or "format-assertion" or "content" or "unevaluated";
                        if (vocabulary.Value.GetBoolean() && !known)
                        {
                            Unsupported(childPath, "schema.vocabulary_unsupported", $"Required vocabulary '{vocabulary.Name}' is unsupported.");
                        }
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
                case "$defs":
                    Modern(childPath);
                    goto case "properties";
                case "dependentSchemas":
                    Modern(childPath);
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
                        Validate(child.Value, document, SchemaJson.Path(childPath, child.Name));
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
                            Validate(child.Value, document, SchemaJson.Path(childPath, child.Name));
                        }
                    }
                    break;
                case "dependentRequired":
                    Modern(childPath);
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
                case "maxContains":
                case "minContains":
                    Modern(childPath);
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
                case "prefixItems":
                    if (_draft != 2020)
                    {
                        Unsupported(childPath, "schema.keyword_version", "prefixItems requires draft 2020-12.");
                    }
                    SchemaArray(value, document, childPath);
                    break;
                case "items":
                    if (_draft != 2020 && value.ValueKind == JsonValueKind.Array)
                    {
                        SchemaArray(value, document, childPath);
                        break;
                    }
                    Validate(value, document, childPath);
                    break;
                case "allOf":
                case "anyOf":
                case "oneOf":
                    SchemaArray(value, document, childPath);
                    break;
                case "unevaluatedItems":
                case "unevaluatedProperties":
                case "contentSchema":
                    Modern(childPath);
                    goto case "not";
                case "not":
                case "if":
                case "then":
                case "else":
                case "contains":
                case "additionalItems":
                case "additionalProperties":
                case "propertyNames":
                    Validate(value, document, childPath);
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
                case "deprecated":
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

    private void SchemaArray(JsonElement value, SchemaDocument document, string path)
    {
        SchemaJson.Array(value, path);
        ValidationContext.Require(value.GetArrayLength() > 0, path, "schema.empty", "A schema array must not be empty.");
        for (var i = 0; i < value.GetArrayLength(); i++)
        {
            Validate(value[i], document, SchemaJson.Path(path, i.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private void Modern(string path)
    {
        if (_draft == 7)
        {
            Unsupported(path, "schema.keyword_version", "This keyword requires a modern JSON Schema dialect.");
        }
    }

    private static void CheckType(string type, string path)
        => ValidationContext.Require(type is "null" or "boolean" or "object" or "array"
            or "number" or "integer" or "string", path, "schema.type", $"'{type}' is not a JSON Schema type.");

    private static void CheckUri(string value, string path, bool absolute)
        => ValidationContext.Require(!value.Any(char.IsWhiteSpace) && Uri.TryCreate(value,
            absolute ? UriKind.Absolute : UriKind.RelativeOrAbsolute, out _), path, "schema.uri", "A valid URI reference is required.");

    private static string ResolveUri(string baseUri, string reference)
        => Uri.TryCreate(baseUri, UriKind.Absolute, out var absolute) && Uri.TryCreate(absolute, reference, out var resolved)
            ? resolved.AbsoluteUri : reference;

    private static void Unsupported(string path, string code, string detail)
        => ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, code, detail);

    private sealed class SchemaDocument(JsonDocument json, string baseUri)
    {
        internal JsonDocument Json { get; } = json;
        internal string BaseUri { get; set; } = baseUri;
        internal HashSet<string> Visited { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> Anchors { get; } = new(StringComparer.Ordinal);
    }
}
