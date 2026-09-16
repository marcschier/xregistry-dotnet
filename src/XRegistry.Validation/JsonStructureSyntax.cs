// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace XRegistry.Validation;

internal sealed class JsonStructureSyntax(ValidationContext context)
{
    private static readonly HashSet<string> Primitive = new(StringComparer.Ordinal)
    {
        "string", "number", "integer", "boolean", "null", "binary", "int8", "uint8", "int16", "uint16",
        "int32", "uint32", "int64", "uint64", "int128", "uint128", "float", "double", "decimal",
        "date", "datetime", "time", "duration", "uuid", "uri", "jsonpointer"
    };
    private static readonly HashSet<string> Compound = new(StringComparer.Ordinal)
    {
        "object", "array", "set", "map", "tuple", "choice", "any"
    };
    private JsonElement _root;
    private readonly List<(string Reference, string Path)> _references = [];
    private readonly HashSet<string> _schemaPaths = new(StringComparer.Ordinal);

    internal void Validate(ReadOnlyMemory<byte> bytes, Action<JsonElement, IReadOnlySet<string>>? validatedRoot = null)
    {
        using var document = context.ReadJson(bytes);
        _root = document.RootElement;
        SchemaJson.Object(_root, "$");
        Absolute(SchemaJson.Member(_root, "$schema", "$"), "$/$schema");
        Absolute(SchemaJson.Member(_root, "$id", "$"), "$/$id");
        Name(SchemaJson.String(SchemaJson.Member(_root, "name", "$"), "$/name"), "$/name");
        var meta = _root.GetProperty("$schema").GetString();
        if (meta != "https://json-structure.org/meta/core/v0/#")
        {
            ValidationContext.Fail(DocumentValidationStatus.Unsupported, "$/$schema", "structure.metaschema",
                "Only the declared draft-04 core meta-schema is qualified; extensions require a separate policy.");
        }

        if (_root.TryGetProperty("definitions", out var definitions))
        {
            Namespace(definitions, "$/definitions", 1);
        }

        if (_root.TryGetProperty("$root", out var rootReference))
        {
            Require(!_root.TryGetProperty("type", out _), "$/$root", "The root type and $root are mutually exclusive.");
            var reference = SchemaJson.String(rootReference, "$/$root");
            Require(reference.StartsWith("/definitions/", StringComparison.Ordinal), "$/$root", "$root must select a reusable type.");
            _references.Add(("#" + reference, "$/$root"));
            Annotations(_root, null, "$", true);
        }
        else if (_root.TryGetProperty("type", out _))
        {
            Schema(_root, "$", 1, true, _root.GetProperty("name").GetString());
        }
        else
        {
            Annotations(_root, null, "$", true);
        }

        foreach (var reference in _references)
        {
            context.Work();
            var target = SchemaJson.Pointer(_root, reference.Reference, reference.Path, context);
            Require(target.Path.StartsWith("$/definitions/", StringComparison.Ordinal) &&
                target.Value.ValueKind == JsonValueKind.Object && target.Value.TryGetProperty("type", out _),
                reference.Path, "A reference must select a reusable type declaration, not a namespace or inline property.");
        }
        validatedRoot?.Invoke(_root, _schemaPaths);
    }

    private void Namespace(JsonElement value, string path, int depth)
    {
        context.Node(depth, path);
        SchemaJson.Object(value, path);
        foreach (var member in value.EnumerateObject())
        {
            var at = SchemaJson.Path(path, member.Name);
            Name(member.Name, at);
            SchemaJson.Object(member.Value, at);
            if (member.Value.TryGetProperty("type", out _))
            {
                Schema(member.Value, at, depth + 1, false, member.Name);
            }
            else
            {
                Namespace(member.Value, at, depth + 1);
            }
        }
    }

    private void Schema(JsonElement schema, string path, int depth, bool root = false, string? inheritedName = null)
    {
        context.Node(depth, path);
        _schemaPaths.Add(path);
        SchemaJson.Object(schema, path);
        var typeValue = SchemaJson.Member(schema, "type", path);
        string? type = null;
        if (typeValue.ValueKind == JsonValueKind.String)
        {
            type = typeValue.GetString()!;
            Require(Primitive.Contains(type) || Compound.Contains(type), path + "/type", "The type is not defined by JSON Structure Core.");
        }
        else if (typeValue.ValueKind == JsonValueKind.Object)
        {
            Require(!root, path + "/type", "The document root cannot use a type reference.");
            Reference(typeValue, path + "/type");
        }
        else if (typeValue.ValueKind == JsonValueKind.Array)
        {
            Require(!root && typeValue.GetArrayLength() > 0, path + "/type", "A nonempty union cannot appear directly at the document root.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in typeValue.EnumerateArray())
            {
                context.Work();
                if (item.ValueKind == JsonValueKind.String)
                {
                    var name = item.GetString()!;
                    Require(Primitive.Contains(name) && names.Add(name), path + "/type", "Union members must be distinct primitives or references.");
                }
                else
                {
                    Reference(item, path + "/type");
                    Require(names.Add(item.GetRawText()), path + "/type", "Union reference members must be unique.");
                }
            }
        }
        else
        {
            Require(false, path + "/type", "The type must be a name, reference, or nonempty union.");
        }

        if (schema.TryGetProperty("name", out var nameValue))
        {
            Name(SchemaJson.String(nameValue, path + "/name"), path + "/name");
        }
        else if (type is "object" or "tuple" or "choice")
        {
            Require(inheritedName is not null, path + "/name", "An inline compound type needs an explicit or inherited name.");
        }

        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("properties", out var properties))
        {
            Require(type is "object" or "tuple", path + "/properties", "properties applies only to objects and tuples.");
            SchemaJson.Object(properties, path + "/properties");
            foreach (var property in properties.EnumerateObject())
            {
                var at = SchemaJson.Path(path + "/properties", property.Name);
                Name(property.Name, at);
                propertyNames.Add(property.Name);
                Schema(property.Value, at, depth + 1, inheritedName: property.Name);
            }
        }

        if (type is "object" or "tuple")
        {
            Require(propertyNames.Count > 0, path + "/properties", "Objects and tuples require declared properties.");
        }

        if (schema.TryGetProperty("required", out var required))
        {
            Require(type == "object", path + "/required", "required applies only to objects.");
            SchemaJson.Array(required, path + "/required");
            var alternatives = required.GetArrayLength() > 0 && required[0].ValueKind == JsonValueKind.Array;
            if (alternatives)
            {
                SchemaJson.UniqueValues(required, context, path + "/required");
                foreach (var set in required.EnumerateArray()) { RequiredNames(set, propertyNames, path + "/required"); }
            }
            else { RequiredNames(required, propertyNames, path + "/required"); }
        }

        if (schema.TryGetProperty("tuple", out var tuple))
        {
            Require(type == "tuple", path + "/tuple", "tuple ordering applies only to tuples.");
            var names = SchemaJson.Strings(tuple, path + "/tuple");
            Require(names.SetEquals(propertyNames), path + "/tuple", "A tuple must list every declared property exactly once.");
        }
        else { Require(type != "tuple", path, "A tuple needs its property order."); }

        if (schema.TryGetProperty("items", out var items))
        {
            Require(type is "array" or "set", path + "/items", "items applies only to arrays and sets.");
            Schema(items, path + "/items", depth + 1);
        }
        else { Require(type is not ("array" or "set"), path, "Arrays and sets require an item schema."); }

        if (schema.TryGetProperty("values", out var values))
        {
            Require(type == "map", path + "/values", "values applies only to maps.");
            Schema(values, path + "/values", depth + 1);
        }
        else { Require(type != "map", path, "Maps require a value schema."); }

        if (schema.TryGetProperty("additionalProperties", out var additional))
        {
            Require(type == "object", path + "/additionalProperties", "additionalProperties applies only to objects.");
            if (additional.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                Schema(additional, path + "/additionalProperties", depth + 1);
            }
        }

        if (schema.TryGetProperty("choices", out var choices))
        {
            Require(type == "choice", path + "/choices", "choices applies only to choice types.");
            SchemaJson.Object(choices, path + "/choices");
            Require(choices.EnumerateObject().Any(), path + "/choices", "A choice requires at least one alternative.");
            foreach (var choice in choices.EnumerateObject())
            {
                Name(choice.Name, path + "/choices");
                Schema(choice.Value, SchemaJson.Path(path + "/choices", choice.Name), depth + 1, inheritedName: choice.Name);
            }
        }
        else { Require(type != "choice", path, "A choice needs an alternatives map."); }

        if (schema.TryGetProperty("selector", out var selector))
        {
            Require(type == "choice", path + "/selector", "selector applies only to choice types.");
            Name(SchemaJson.String(selector, path + "/selector"), path + "/selector");
            ValidationContext.Fail(DocumentValidationStatus.Unsupported, path + "/selector", "structure.inheritance",
                "Inline choice inheritance needs an explicitly qualified extension policy.");
        }

        foreach (var keyword in new[] { "const", "enum" })
        {
            if (!schema.TryGetProperty(keyword, out var restriction)) { continue; }
            Require(type is not null && Primitive.Contains(type), path + "/" + keyword, "const and enum require a direct primitive type.");
            if (keyword == "enum")
            {
                SchemaJson.UniqueValues(restriction, context, path + "/enum");
                foreach (var member in restriction.EnumerateArray()) { PrimitiveValue(type!, member, path + "/enum"); }
            }
            else { PrimitiveValue(type!, restriction, path + "/const"); }
        }

        Annotations(schema, type, path, root);
    }

    private void Annotations(JsonElement schema, string? type, string path, bool root)
    {
        foreach (var property in schema.EnumerateObject())
        {
            context.Work();
            var at = SchemaJson.Path(path, property.Name);
            if (!root && property.Name is "$id" or "$schema" or "$root" or "definitions")
            {
                Require(false, at, "This keyword is restricted to the document root.");
            }

            switch (property.Name)
            {
                case "$ref":
                    Require(false, at, "$ref must be the only member of a type reference object.");
                    break;
                case "$uses":
                    var uses = SchemaJson.Strings(property.Value, at);
                    if (uses.Count != 0)
                    {
                        ValidationContext.Fail(DocumentValidationStatus.Unsupported, at, "structure.extension", "Required extensions are not implicitly supported.");
                    }
                    break;
                case "$offers":
                    SchemaJson.Strings(property.Value, at);
                    break;
                case "$extends":
                case "abstract":
                    ValidationContext.Fail(DocumentValidationStatus.Unsupported, at, "structure.inheritance", "Inheritance requires an explicitly qualified policy.");
                    break;
                case "maxLength":
                    Require(type == "string", at, "maxLength applies only to string.");
                    Nonnegative(property.Value, at);
                    break;
                case "precision":
                case "scale":
                    Require(type is "number" or "decimal", at, "Precision and scale apply only to number and decimal.");
                    var number = Nonnegative(property.Value, at);
                    Require(property.Name != "precision" || number > 0, at, "Precision must be positive.");
                    break;
                case "contentEncoding":
                    Require(type == "binary", at, "contentEncoding applies only to binary.");
                    Require(SchemaJson.String(property.Value, at) is "base64" or "base64url" or "base16" or "base32" or "base32hex",
                        at, "Unknown binary encoding.");
                    break;
                case "contentCompression":
                    Require(type == "binary", at, "contentCompression applies only to binary.");
                    Require(SchemaJson.String(property.Value, at) is "gzip" or "deflate" or "zlib" or "brotli", at, "Unknown compression.");
                    break;
                case "contentMediaType":
                    Require(type == "binary", at, "contentMediaType applies only to binary.");
                    SchemaJson.String(property.Value, at);
                    break;
                case "description":
                    SchemaJson.String(property.Value, at);
                    break;
                case "examples":
                    SchemaJson.Array(property.Value, at);
                    break;
            }
        }

        if (schema.TryGetProperty("precision", out var precision) && schema.TryGetProperty("scale", out var scale))
        {
            Require(Nonnegative(scale, path + "/scale") <= Nonnegative(precision, path + "/precision"),
                path + "/scale", "Scale cannot exceed precision.");
        }
    }

    private void Reference(JsonElement value, string path)
    {
        SchemaJson.Object(value, path);
        Require(value.EnumerateObject().Count() == 1 && value.TryGetProperty("$ref", out _),
            path, "A type reference object contains only $ref.");
        var reference = SchemaJson.String(value.GetProperty("$ref"), path + "/$ref");
        Require(reference.StartsWith("#/definitions/", StringComparison.Ordinal), path, "Type references are local reusable JSON Pointers.");
        _references.Add((reference, path));
    }

    private static void RequiredNames(JsonElement names, HashSet<string> properties, string path)
    {
        var required = SchemaJson.Strings(names, path);
        Require(required.IsSubsetOf(properties), path, "Required names must identify declared properties.");
    }

    private void PrimitiveValue(string type, JsonElement value, string path)
    {
        context.Work();
        if (type == "null") { Require(value.ValueKind == JsonValueKind.Null, path, "The restriction must be null."); return; }
        if (type == "boolean") { SchemaJson.Boolean(value, path); return; }
        if (type is "number" or "float" or "double")
        {
            Require(value.ValueKind == JsonValueKind.Number, path, "A numeric restriction is required.");
            return;
        }

        if (type is "int8" or "uint8" or "int16" or "uint16" or "integer" or "int32" or "uint32" or
            "int64" or "uint64" or "int128" or "uint128")
        {
            var bits = type == "integer" ? 32 : type.EndsWith("128", StringComparison.Ordinal) ? 128 :
                type.EndsWith("64", StringComparison.Ordinal) ? 64 : type.EndsWith("32", StringComparison.Ordinal) ? 32 :
                type.EndsWith("16", StringComparison.Ordinal) ? 16 : 8;
            Require(value.ValueKind == (bits > 32 ? JsonValueKind.String : JsonValueKind.Number),
                path, "The integer restriction uses the wrong JSON representation.");
            var text = bits > 32 ? value.GetString()! : value.GetRawText();
            var digits = text.StartsWith('-') ? text[1..] : text;
            Require(digits.Length > 0 && digits.All(char.IsAsciiDigit) &&
                (digits.Length == 1 || digits[0] != '0'), path, "Integer literals are canonical decimal without exponents.");
            var number = BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var unsigned = type.StartsWith('u');
            Require(number >= (unsigned ? 0 : -(BigInteger.One << (bits - 1))) &&
                number < (BigInteger.One << (unsigned ? bits : bits - 1)), path, "The integer restriction is outside its type range.");
            return;
        }

        SchemaJson.String(value, path);
        if (type == "uuid") { Require(Guid.TryParseExact(value.GetString(), "D", out _), path, "The UUID restriction is invalid."); }
        else if (type == "uri") { Require(Uri.IsWellFormedUriString(value.GetString(), UriKind.RelativeOrAbsolute), path, "The URI restriction is invalid."); }
        else if (type != "string")
        {
            ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, "structure.primitive_restriction",
                "A const/enum for this extended primitive requires an additional qualified lexical policy.");
        }
    }

    private static BigInteger Nonnegative(JsonElement value, string path)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.GetRawText().All(char.IsAsciiDigit),
            path, "A nonnegative integer annotation is required.");
        return BigInteger.Parse(value.GetRawText(), CultureInfo.InvariantCulture);
    }

    private static void Absolute(JsonElement value, string path)
    {
        var text = SchemaJson.String(value, path);
        Require(Uri.TryCreate(text, UriKind.Absolute, out _) && Uri.IsWellFormedUriString(text, UriKind.Absolute),
            path, "An absolute schema identifier is required.");
    }

    private static void Name(string name, string path) => Require(SchemaJson.Identifier(name), path, "A valid case-sensitive schema identifier is required.");
    private static void Require(bool condition, string path, string detail) => ValidationContext.Require(condition, path, "structure.syntax", detail);
}
