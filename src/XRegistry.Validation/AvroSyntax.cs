using System.Globalization;
using System.Text.Json;

namespace XRegistry.Validation;

internal sealed class AvroSyntax(string format, ValidationContext context)
{
    private readonly Dictionary<string, AvroType> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<AvroField, JsonElement> _defaults = [];
    private readonly bool _enumDefaults = format.Equals("Avro/1.11.0", StringComparison.OrdinalIgnoreCase);
    internal IReadOnlyDictionary<string, AvroType> NamedTypes => _names;

    internal static bool IsFormat(string format)
        => format.Equals("Avro/1.8.2", StringComparison.OrdinalIgnoreCase)
        || format.Equals("Avro/1.11.0", StringComparison.OrdinalIgnoreCase);

    internal AvroType Parse(ReadOnlyMemory<byte> bytes, Action<JsonElement, AvroType>? validatedRoot = null)
    {
        using var json = context.ReadJson(bytes);
        var schema = Read(json.RootElement, "", "$");
        foreach (var (field, value) in _defaults)
        {
            Default(field.Type, value, "$/fields/" + field.Name + "/default", 1);
        }
        validatedRoot?.Invoke(json.RootElement, schema);
        return schema;
    }

    private AvroType Read(JsonElement schema, string ns, string path)
    {
        context.Work(1, path);
        if (schema.ValueKind == JsonValueKind.String)
        {
            return Reference(schema.GetString()!, ns, path);
        }
        if (schema.ValueKind == JsonValueKind.Array)
        {
            ValidationContext.Require(schema.GetArrayLength() > 0, path, "avro.union", "An Avro union requires at least one branch.");
            var union = new AvroType("union");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in schema.EnumerateArray())
            {
                ValidationContext.Require(element.ValueKind != JsonValueKind.Array, path, "avro.union", "Unions cannot directly contain unions.");
                var branch = Read(element, ns, path + "/" + union.Branches.Count.ToString(CultureInfo.InvariantCulture));
                ValidationContext.Require(keys.Add(branch.Name ?? branch.Kind), path, "avro.union", "A union cannot repeat a type or fullname.");
                union.Branches.Add(branch);
            }
            return union;
        }

        SchemaJson.Object(schema, path);
        var kind = SchemaJson.String(SchemaJson.Member(schema, "type", path), path + "/type");
        if (schema.TryGetProperty("logicalType", out var logicalType))
        {
            SchemaJson.String(logicalType, path + "/logicalType");
            ValidationContext.Fail(DocumentValidationStatus.Unsupported, path + "/logicalType", "avro.logical_type",
                "Logical-type annotations are outside the implemented Avro wire-schema policy.");
        }
        if (schema.TryGetProperty("doc", out var documentation))
        {
            SchemaJson.String(documentation, path + "/doc");
        }
        if (kind is not ("record" or "enum" or "fixed" or "array" or "map"))
        {
            return Reference(kind, ns, path + "/type");
        }

        var type = new AvroType(kind);
        if (kind is "record" or "enum" or "fixed")
        {
            var name = SchemaJson.String(SchemaJson.Member(schema, "name", path), path + "/name");
            var declaredNamespace = schema.TryGetProperty("namespace", out var namespaceValue)
                ? SchemaJson.String(namespaceValue, path + "/namespace") : ns;
            ValidationContext.Require(FullName(name) && !Primitive(name.Split('.')[^1]), path + "/name",
                "avro.name", "An Avro fullname cannot be invalid or redefine a primitive.");
            if (!name.Contains('.'))
            {
                ValidationContext.Require(declaredNamespace.Length == 0 || FullName(declaredNamespace), path + "/namespace",
                    "avro.namespace", "The namespace must be a dot-separated sequence of names.");
                name = declaredNamespace.Length == 0 ? name : declaredNamespace + "." + name;
            }
            type.Name = name;
            type.DeclarationPath = path;
            var lastDot = name.LastIndexOf('.');
            ns = lastDot < 0 ? "" : name[..lastDot];
            ValidationContext.Require(_names.TryAdd(name, type), path + "/name", "avro.duplicate_name", "An Avro fullname is already defined.");
            if (schema.TryGetProperty("aliases", out var aliases))
            {
                type.Aliases.AddRange(SchemaJson.Strings(aliases, path + "/aliases"));
            }
        }

        switch (kind)
        {
            case "record":
                var fields = SchemaJson.Member(schema, "fields", path);
                SchemaJson.Array(fields, path + "/fields");
                foreach (var fieldValue in fields.EnumerateArray())
                {
                    var fieldPath = path + "/fields/" + type.Fields.Count.ToString(CultureInfo.InvariantCulture);
                    SchemaJson.Object(fieldValue, fieldPath);
                    var name = SchemaJson.String(SchemaJson.Member(fieldValue, "name", fieldPath), fieldPath + "/name");
                    ValidationContext.Require(SchemaJson.Identifier(name), fieldPath + "/name", "avro.field_name", "Invalid record field name.");
                    var fieldType = Read(SchemaJson.Member(fieldValue, "type", fieldPath), ns, fieldPath + "/type");
                    var field = new AvroField(name, fieldType, fieldValue.TryGetProperty("default", out var defaultValue));
                    ValidationContext.Require(type.Fields.TryAdd(name, field), fieldPath, "avro.duplicate_field", "Record field names must be unique.");
                    if (field.HasDefault)
                    {
                        _defaults.Add(field, defaultValue);
                    }
                    if (fieldValue.TryGetProperty("aliases", out var aliases))
                    {
                        field.Aliases.AddRange(SchemaJson.Strings(aliases, fieldPath + "/aliases"));
                    }
                    if (fieldValue.TryGetProperty("doc", out var doc))
                    {
                        SchemaJson.String(doc, fieldPath + "/doc");
                    }
                    if (fieldValue.TryGetProperty("order", out var order))
                    {
                        ValidationContext.Require(SchemaJson.String(order, fieldPath + "/order") is "ascending" or "descending" or "ignore",
                            fieldPath + "/order", "avro.order", "Unknown field ordering.");
                    }
                }
                break;
            case "enum":
                var symbols = SchemaJson.Member(schema, "symbols", path);
                foreach (var symbol in SchemaJson.Strings(symbols, path + "/symbols"))
                {
                    ValidationContext.Require(SchemaJson.Identifier(symbol), path + "/symbols", "avro.symbol", "Invalid enum symbol.");
                    type.Symbols.Add(symbol);
                }
                if (_enumDefaults && schema.TryGetProperty("default", out var enumDefault))
                {
                    type.EnumDefault = SchemaJson.String(enumDefault, path + "/default");
                    ValidationContext.Require(type.Symbols.Contains(type.EnumDefault), path + "/default", "avro.default", "An enum default must name a declared symbol.");
                }
                break;
            case "fixed":
                var size = SchemaJson.Member(schema, "size", path);
                ValidationContext.Require(size.ValueKind == JsonValueKind.Number && size.TryGetInt32(out var length) && length >= 0,
                    path + "/size", "avro.fixed_size", "A fixed size must be a nonnegative 32-bit integer.");
                type.Size = size.GetInt32();
                break;
            case "array":
                type.Item = Read(SchemaJson.Member(schema, "items", path), ns, path + "/items");
                break;
            case "map":
                type.Item = Read(SchemaJson.Member(schema, "values", path), ns, path + "/values");
                break;
        }
        return type;
    }

    private AvroType Reference(string name, string ns, string path)
    {
        if (Primitive(name))
        {
            return new(name);
        }
        var fullName = name.Contains('.') || ns.Length == 0 ? name : ns + "." + name;
        ValidationContext.Require(_names.TryGetValue(fullName, out var type), path, "avro.unknown_name",
            $"'{name}' is not a primitive or a previously declared fullname.");
        return type;
    }

    private void Default(AvroType type, JsonElement value, string path, int depth)
    {
        context.Work(1, path);
        if (depth > context.Options.MaxDepth)
        {
            ValidationContext.Fail(DocumentValidationStatus.Indeterminate, path, "limit.depth", "Avro default expansion exceeded the nesting limit.");
        }
        var valid = type.Kind switch
        {
            "null" => value.ValueKind == JsonValueKind.Null,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "int" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            "long" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "float" => value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number) && float.IsFinite(number),
            "double" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
            "string" => value.ValueKind == JsonValueKind.String,
            "bytes" or "fixed" => value.ValueKind == JsonValueKind.String && value.GetString()!.All(c => c <= 255)
                && (type.Kind == "bytes" || value.GetString()!.Length == type.Size),
            "enum" => value.ValueKind == JsonValueKind.String && type.Symbols.Contains(value.GetString()!),
            "record" or "map" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "union" => true,
            _ => false,
        };
        ValidationContext.Require(valid, path, "avro.default", $"The default does not conform to its '{type.Kind}' schema.");
        switch (type.Kind)
        {
            case "union":
                Default(type.Branches[0], value, path, depth + 1);
                break;
            case "array":
                foreach (var item in value.EnumerateArray())
                {
                    Default(type.Item!, item, path, depth + 1);
                }
                break;
            case "map":
                foreach (var property in value.EnumerateObject())
                {
                    Default(type.Item!, property.Value, SchemaJson.Path(path, property.Name), depth + 1);
                }
                break;
            case "record":
                foreach (var field in type.Fields.Values)
                {
                    if (!value.TryGetProperty(field.Name, out var fieldValue))
                    {
                        ValidationContext.Require(_defaults.TryGetValue(field, out fieldValue), SchemaJson.Path(path, field.Name),
                            "avro.default", "A record default is missing a field with no default.");
                    }
                    Default(field.Type, fieldValue, SchemaJson.Path(path, field.Name), depth + 1);
                }
                break;
        }
    }

    private static bool FullName(string name) => name.Split('.').All(SchemaJson.Identifier);
    internal static bool Primitive(string name) => name is "null" or "boolean" or "int" or "long" or "float" or "double" or "bytes" or "string";
}

internal sealed class AvroType(string kind)
{
    internal string Kind { get; } = kind;
    internal string? Name { get; set; }
    internal string DeclarationPath { get; set; } = "$";
    internal AvroType? Item { get; set; }
    internal List<AvroType> Branches { get; } = [];
    internal Dictionary<string, AvroField> Fields { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> Symbols { get; } = new(StringComparer.Ordinal);
    internal List<string> Aliases { get; } = [];
    internal string? EnumDefault { get; set; }
    internal int Size { get; set; }
}

internal sealed class AvroField(string name, AvroType type, bool hasDefault)
{
    internal string Name { get; } = name;
    internal AvroType Type { get; } = type;
    internal bool HasDefault { get; } = hasDefault;
    internal List<string> Aliases { get; } = [];
}
