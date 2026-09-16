// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Http;

/// <summary>Converts Document metadata using the model-dependent xRegistry HTTP header binding.</summary>
/// <remarks>
/// This is a representation codec, not complete entity validation. Client input cannot losslessly
/// represent a literal string equal to null, an empty map, or a non-string any value.
/// Use a metadata-body request for those values. Map member names are never percent-decoded.
/// Conditional definitions use the same activation machinery as RegistryMetadataValidator.
/// Partial headers must provide the discriminator context needed to select their types.
/// </remarks>
public static class RegistryHeaderMetadata
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);
    private static readonly JsonElement s_null = RegistryJson.Parse("null").RootElement;

    /// <summary>Encodes a Document's metadata into an independently owned, case-insensitive header dictionary.</summary>
    /// <remarks>
    /// Content-Type is separate from xRegistry headers. Client nulls request deletion; a null Content-Type
    /// is omitted. Responses omit nulls, Document content and unrepresentable complex attributes.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Encode(
        RegistryJson metadata, RegistryResourceDefinition resource, RegistryHeaderMetadataDirection direction,
        RegistryHeaderMetadataOptions? options = null, string path = "")
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(path);
        options = Validate(direction, options);
        if (metadata.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Header metadata must be a JSON object.", nameof(metadata));
        }

        if (direction == RegistryHeaderMetadataDirection.ClientInput)
        {
            _ = RegistryJson.FromElement(metadata.RootElement, options.Json);
        }

        var definitions = HeaderDefinitions(resource, definition =>
        {
            if (direction == RegistryHeaderMetadataDirection.ClientInput && definition.ReadOnly)
            {
                return default;
            }

            if (metadata.RootElement.TryGetProperty(definition.Name, out var value))
            {
                return value;
            }

            return direction == RegistryHeaderMetadataDirection.ClientInput && definition.Name == "contenttype" ? s_null : default;
        }, options.Json, path, partialContext: direction == RegistryHeaderMetadataDirection.ClientInput);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var budget = new HeaderBudget(options, direction, path);
        foreach (var attribute in metadata.RootElement.EnumerateObject())
        {
            var name = "xRegistry-" + attribute.Name;
            if (attribute.Name == resource.Singular || attribute.Name == resource.Singular + "base64")
            {
                if (direction == RegistryHeaderMetadataDirection.ClientInput)
                {
                    throw Error("extra_xregistry_header", path, "This attribute must not be sent as an xRegistry header.", name);
                }

                continue;
            }

            var definition = definitions.Find(attribute.Name, name);
            if (direction == RegistryHeaderMetadataDirection.ClientInput)
            {
                if (!RegistryNames.IsAttribute(attribute.Name) || definition is null)
                {
                    throw Error("header_error", path, "The header attribute is not defined by the model.", name);
                }

                if (Ignored(definition, attribute.Name, resource, direction))
                {
                    continue;
                }
            }
            else if (attribute.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (attribute.Name == "contenttype")
            {
                if (attribute.Value.ValueKind != JsonValueKind.Null)
                {
                    var value = attribute.Value.ValueKind == JsonValueKind.String ? attribute.Value.GetString()! : "";
                    ValidateContentType(value, path);
                    budget.Add("Content-Type", value);
                    headers.Add("Content-Type", value);
                }
            }
            else if (attribute.Value.ValueKind == JsonValueKind.Null ||
                IsScalar(attribute.Value) && (direction == RegistryHeaderMetadataDirection.Response || Scalar(definition!.Type)))
            {
                if (direction == RegistryHeaderMetadataDirection.ClientInput && attribute.Value.ValueKind != JsonValueKind.Null)
                {
                    ValidateClientScalar(attribute.Value, definition!.Type, options.Json, path, name);
                }

                Add(headers, name, attribute.Value, budget, options.MaxHeaderBytes, path);
            }
            else if (attribute.Value.ValueKind == JsonValueKind.Object &&
                definition is { Type: RegistryValueType.Map, Item: { } item } && Scalar(item.Type) &&
                (direction == RegistryHeaderMetadataDirection.ClientInput ||
                    attribute.Value.EnumerateObject().All(static member => IsScalar(member.Value))))
            {
                if (direction == RegistryHeaderMetadataDirection.ClientInput && !attribute.Value.EnumerateObject().Any())
                {
                    throw Error("header_error", path,
                        "An empty map cannot be represented by member headers; use a metadata-body request.", name);
                }

                foreach (var member in attribute.Value.EnumerateObject())
                {
                    var memberName = name + "." + member.Name;
                    if (member.Name.Length == 0)
                    {
                        throw Error("header_error", path, "The map member header name is empty or duplicated.", memberName);
                    }

                    if (direction == RegistryHeaderMetadataDirection.ClientInput && member.Value.ValueKind != JsonValueKind.Null)
                    {
                        ValidateClientScalar(member.Value, item.Type, options.Json, path, memberName);
                    }

                    Add(headers, memberName, member.Value, budget, options.MaxHeaderBytes, path);
                }
            }
            else if (direction == RegistryHeaderMetadataDirection.ClientInput)
            {
                throw Error("header_error", path, "Complex attributes require a metadata-body request.", name);
            }
        }

        return new ReadOnlyDictionary<string, string>(headers);
    }

    /// <summary>Decodes raw field values without consuming a Document body or borrowing the supplied headers.</summary>
    /// <remarks>
    /// Pass uncombined field values, including Content-Type. Ordinary HTTP headers and recognized
    /// non-attribute xRegistry response control fields are not metadata. Unknown attribute headers fail.
    /// Any-typed values use the binding's string fallback. Read-only client values remain ignored,
    /// except epoch, versionid and the Resource ID, which participate in server guards.
    /// Fields are bounded and buffered before conditional lookup, so header order does not select types.
    /// Missing or ignored discriminators do not activate defaults or select a speculative wildcard.
    /// </remarks>
    public static RegistryJson Decode(
        IEnumerable<KeyValuePair<string, IEnumerable<string?>>> headers, RegistryResourceDefinition resource,
        RegistryHeaderMetadataDirection direction, RegistryHeaderMetadataOptions? options = null, string path = "")
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(path);
        options = Validate(direction, options);
        var metadata = new JsonObject();
        var budget = new HeaderBudget(options, direction, path);
        var fields = ReadFields(headers, budget);
        var byName = fields.ToLookup(static field => field.Name, StringComparer.OrdinalIgnoreCase);
        var definitions = HeaderDefinitions(resource, definition =>
        {
            if (direction == RegistryHeaderMetadataDirection.ClientInput && definition.ReadOnly)
            {
                return default;
            }

            var candidates = byName[definition.Name == "contenttype" ? "Content-Type" : "xRegistry-" + definition.Name].ToArray();
            if (candidates.Length == 0)
            {
                return direction == RegistryHeaderMetadataDirection.ClientInput && definition.Name == "contenttype" ? s_null : default;
            }

            if (candidates.Length != 1)
            {
                throw Error("header_error", path, "The attribute header is duplicated.", candidates[1].Name);
            }

            var field = candidates[0];
            if (field.Count != 1)
            {
                throw Error("header_error", path, "An attribute header must contain one field value.", field.Name);
            }

            JsonNode? value;
            if (definition.Name == "contenttype")
            {
                ValidateContentType(field.Value, path);
                value = JsonValue.Create(field.Value);
            }
            else
            {
                value = Value(DecodeField(field, options.MaxHeaderBytes, path), definition.Type, direction, options.Json, path, field.Name);
            }

            return value is null ? s_null : JsonSerializer.SerializeToElement(value, RegistryMetadataJsonContext.Default.JsonNode);
        }, options.Json, path, partialContext: true);
        foreach (var field in fields)
        {
            if (field.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (field.Count != 1 || metadata.ContainsKey("contenttype"))
                {
                    throw Error("header_error", path, "An attribute header must contain one field value.", field.Name);
                }

                ValidateContentType(field.Value, path);
                metadata["contenttype"] = field.Value;
                continue;
            }

            if (!field.Name.StartsWith("xRegistry-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = field.Name[10..];
            var dot = suffix.IndexOf('.');
            var attribute = (dot < 0 ? suffix : suffix[..dot]).ToLowerInvariant();
            if (attribute == resource.Singular || attribute == resource.Singular + "base64" || attribute == "contenttype")
            {
                throw Error("extra_xregistry_header", path, "This attribute must not be sent as an xRegistry header.", field.Name);
            }

            if (direction == RegistryHeaderMetadataDirection.Response && dot < 0 &&
                attribute is "xregcorrelationid" or "count" or "commit-outcome" &&
                !definitions.IsDeclared(attribute))
            {
                continue;
            }

            var definition = definitions.Find(attribute, field.Name);
            if (definition is null)
            {
                throw Error("header_error", path, "The header attribute is not defined by the model.", field.Name);
            }

            if (Ignored(definition, attribute, resource, direction))
            {
                continue;
            }

            var decoded = DecodeField(field, options.MaxHeaderBytes, path);

            if (dot >= 0)
            {
                if (definition.Type != RegistryValueType.Map || definition.Item is null || !Scalar(definition.Item.Type))
                {
                    throw Error("header_error", path, "Only scalar-valued maps may be represented by member headers.", field.Name);
                }

                var map = metadata[attribute] as JsonObject;
                if (map is null && metadata.ContainsKey(attribute))
                {
                    throw Error("header_error", path, "A map deletion cannot be mixed with map member headers.", field.Name);
                }

                if (map is null)
                {
                    map = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
                    metadata[attribute] = map;
                }

                var key = suffix[(dot + 1)..];
                if (key.Length == 0 || map.ContainsKey(key))
                {
                    throw Error("header_error", path, "The map member header name is empty or duplicated.", field.Name);
                }

                map[key] = Value(decoded, definition.Item.Type, direction, options.Json, path, field.Name);
            }
            else
            {
                if (metadata.ContainsKey(attribute))
                {
                    throw Error("header_error", path, "The attribute header is duplicated.", field.Name);
                }

                if (!Scalar(definition.Type) && decoded != "null")
                {
                    throw Error("header_error", path, "Complex attributes require a metadata-body request.", field.Name);
                }

                metadata[attribute] = Value(decoded, definition.Type, direction, options.Json, path, field.Name);
            }
        }

        return RegistryJson.Create(writer => metadata.WriteTo(writer), options.Json);
    }

    private static List<HeaderField> ReadFields(IEnumerable<KeyValuePair<string, IEnumerable<string?>>> headers, HeaderBudget budget)
    {
        var fields = new List<HeaderField>();
        foreach (var header in headers)
        {
            ArgumentNullException.ThrowIfNull(header.Key);
            ArgumentNullException.ThrowIfNull(header.Value);
            var count = 0;
            var raw = "";
            foreach (var value in header.Value)
            {
                budget.Add(header.Key, value ?? "");
                if (count++ == 0)
                {
                    raw = value ?? "";
                }
            }

            if (count == 0)
            {
                budget.Add(header.Key, "");
            }

            fields.Add(new(header.Key, raw, count));
        }

        return fields;
    }

    private static string DecodeField(HeaderField field, int limit, string path)
    {
        if (field.Count != 1)
        {
            throw Error("header_error", path, "An attribute header must contain one field value.", field.Name);
        }

        if (!field.Name.All(IsToken))
        {
            throw Error("header_error", path, "A metadata header name cannot be represented unambiguously by HTTP.", field.Name);
        }

        try
        {
            return field.Decoded ??= RegistryHeaderEncoding.Decode(field.Value, limit);
        }
        catch (FormatException exception)
        {
            throw Error("header_error", path, "An attribute header has invalid quoting or percent encoding.", field.Name, exception.GetType().Name);
        }
    }

    private static HeaderModel HeaderDefinitions(
        RegistryResourceDefinition resource, Func<RegistryAttributeDefinition, JsonElement> runtimeValue,
        RegistryJsonLimits limits, string path, bool partialContext)
    {
        var declared = new Dictionary<string, RegistryAttributeDefinition>(resource.Attributes, StringComparer.Ordinal);
        foreach (var definition in resource.ResourceAttributes)
        {
            declared.TryAdd(definition.Key, definition.Value);
        }

        var uncertain = new HashSet<string>(StringComparer.Ordinal);
        if (!declared.Values.Any(static definition => definition.IfValues.Count != 0))
        {
            return new(declared, uncertain, path);
        }

        var work = 0;
        var missing = new Stack<(RegistryAttributeDefinition Definition, int Depth)>();
        try
        {
            var active = RegistryConditionalAttributes.Resolve(declared, (definition, depth) =>
            {
                Work(depth);
                if (definition.IfValues.Count == 0)
                {
                    return default;
                }

                var value = runtimeValue(definition);
                if (partialContext && value.ValueKind == JsonValueKind.Undefined)
                {
                    missing.Push((definition, depth));
                }

                return value;
            }, "", (_, depth) => Work(depth));
            while (missing.TryPop(out var next))
            {
                foreach (var branch in next.Definition.IfValues.Values)
                {
                    foreach (var sibling in branch.Values)
                    {
                        Work(next.Depth + 1);
                        uncertain.Add(sibling.Name);
                        if (sibling.IfValues.Count != 0)
                        {
                            missing.Push((sibling, next.Depth + 1));
                        }
                    }
                }
            }

            return new(active, uncertain, path);
        }
        catch (RegistryException exception) when (exception.Diagnostic.Code == "invalid_attribute")
        {
            throw Error("invalid_attribute", path, exception.Diagnostic.Message,
                exception.Diagnostic.Path[1..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        }

        void Work(int depth)
        {
            if (++work > limits.MaxNodes)
            {
                throw Diagnostics.Error("node_limit", path, "The conditional metadata lookup budget was exceeded.");
            }

            if (depth > limits.MaxDepth)
            {
                throw Diagnostics.Error("depth_limit", path, "The conditional metadata lookup depth was exceeded.");
            }
        }
    }

    private static RegistryHeaderMetadataOptions Validate(RegistryHeaderMetadataDirection direction, RegistryHeaderMetadataOptions? options)
    {
        if (direction is not (RegistryHeaderMetadataDirection.ClientInput or RegistryHeaderMetadataDirection.Response))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        options ??= new();
        options.Validate();
        return options;
    }

    private static bool Ignored(RegistryAttributeDefinition definition, string attribute,
        RegistryResourceDefinition resource, RegistryHeaderMetadataDirection direction) =>
        direction == RegistryHeaderMetadataDirection.ClientInput && definition.ReadOnly &&
        attribute != "epoch" && attribute != "versionid" && attribute != resource.Singular + "id";

    private static JsonNode? Value(string value, RegistryValueType type, RegistryHeaderMetadataDirection direction,
        RegistryJsonLimits limits, string path, string name)
    {
        if (value == "null" && (direction == RegistryHeaderMetadataDirection.ClientInput ||
            type is RegistryValueType.Integer or RegistryValueType.UInteger or RegistryValueType.Decimal or RegistryValueType.Boolean || !Scalar(type)))
        {
            return null;
        }

        if (type is RegistryValueType.Integer or RegistryValueType.UInteger or RegistryValueType.Decimal or RegistryValueType.Boolean)
        {
            RegistryJson parsed;
            try
            {
                parsed = RegistryJson.Parse(value, limits);
            }
            catch (RegistryException exception)
            {
                throw Error("header_error", path, "A typed scalar header is not a valid JSON scalar.", name, exception.Diagnostic.Code);
            }

            if (type == RegistryValueType.Boolean ? parsed.RootElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                : parsed.RootElement.ValueKind != JsonValueKind.Number)
            {
                throw Error("header_error", path, "A typed scalar header has the wrong value kind.", name);
            }

            if (direction == RegistryHeaderMetadataDirection.Response)
            {
                ValidateClientScalar(parsed.RootElement, type, limits, path, name);
            }

            return JsonNode.Parse(parsed.RootElement.GetRawText());
        }

        return JsonValue.Create(value);
    }

    private static void ValidateClientScalar(JsonElement value, RegistryValueType type, RegistryJsonLimits limits, string path, string name)
    {
        var valid = type == RegistryValueType.Boolean ? value.ValueKind is JsonValueKind.True or JsonValueKind.False :
            type is RegistryValueType.Integer or RegistryValueType.UInteger or RegistryValueType.Decimal ?
                value.ValueKind == JsonValueKind.Number : Scalar(type) && value.ValueKind == JsonValueKind.String;
        if (valid && value.ValueKind == JsonValueKind.Number)
        {
            var number = RegistryNumber.FromElement(value, limits);
            valid = type == RegistryValueType.Decimal || number.IsInteger && (type != RegistryValueType.UInteger || number.Significand.Sign >= 0);
        }

        if (!valid)
        {
            throw Error("header_error", path, "The value cannot be represented by the model's scalar header type; use a metadata-body request.", name);
        }

        if (value.ValueKind == JsonValueKind.String && value.GetString() == "null")
        {
            throw Error("header_error", path, "The literal string null is indistinguishable from deletion in a request header; use a metadata-body request.", name);
        }
    }

    private static void ValidateContentType(string value, string path)
    {
        if (!MediaTypeHeaderValue.TryParse(value, out _) || value.Any(static character => character is < ' ' or > '~'))
        {
            throw Error("header_error", path, "The Document Content-Type cannot be represented as an HTTP media type.", "Content-Type");
        }
    }

    private static void Add(Dictionary<string, string> headers, string name, JsonElement value,
        HeaderBudget budget, int maximum, string path)
    {
        if (!name.All(IsToken) || headers.ContainsKey(name))
        {
            throw Error("header_error", path, "A metadata header name cannot be represented unambiguously by HTTP.", name);
        }

        string encoded;
        try
        {
            encoded = RegistryHeaderEncoding.Encode(ScalarValues.Text(value), maximum);
        }
        catch (FormatException exception)
        {
            throw Error("header_error", path, "A metadata value cannot be represented within the HTTP header budget.", name, exception.GetType().Name);
        }

        budget.Add(name, encoded);
        headers.Add(name, encoded);
    }

    private static RegistryException Error(string code, string path, string message, string name, string? cause = null)
    {
        var failure = new RegistryException(new(code, path, message));
        failure.Data["xregistry.args"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = name };
        if (cause is not null)
        {
            failure.Data["xregistry.cause"] = cause;
        }

        return failure;
    }

    private static bool IsToken(char value) => char.IsAsciiLetterOrDigit(value) || value is '!' or '#' or '$' or '%' or '&' or '\'' or '*'
        or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
    private static bool IsScalar(JsonElement value) => value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
    private static bool Scalar(RegistryValueType type) => type is not (RegistryValueType.Array or RegistryValueType.Map or RegistryValueType.Object or RegistryValueType.Binary);

    private sealed class HeaderField(string name, string value, int count)
    {
        internal string Name { get; } = name;
        internal string Value { get; } = value;
        internal int Count { get; } = count;
        internal string? Decoded { get; set; }
    }

    private sealed class HeaderModel(Dictionary<string, RegistryAttributeDefinition> active, HashSet<string> uncertain, string path)
    {
        internal bool IsDeclared(string attribute) => active.ContainsKey(attribute);

        internal RegistryAttributeDefinition? Find(string attribute, string header)
        {
            if (uncertain.Contains(attribute) || !active.ContainsKey(attribute) && uncertain.Contains("*"))
            {
                throw Error("header_error", path,
                    "A conditional header needs an explicit discriminator context; a discriminator is absent or ignored.", header);
            }

            return active.GetValueOrDefault(attribute) ?? active.GetValueOrDefault("*");
        }
    }

    private sealed class HeaderBudget(RegistryHeaderMetadataOptions options, RegistryHeaderMetadataDirection direction, string path)
    {
        private long _bytes;
        private long _count;

        internal void Add(string name, string value)
        {
            try
            {
                _bytes += (long)s_utf8.GetByteCount(name) + s_utf8.GetByteCount(value) + 4;
            }
            catch (EncoderFallbackException)
            {
                throw Error("header_error", path, "An attribute header has invalid quoting or percent encoding.", name, nameof(EncoderFallbackException));
            }

            if (++_count > options.MaxHeaderCount || _bytes > options.MaxHeaderBytes)
            {
                var request = direction == RegistryHeaderMetadataDirection.ClientInput;
                throw new RegistryException(new(request ? "request_headers_too_large" : "too_large", request ? path : "",
                    _count > options.MaxHeaderCount ? "The headers exceed their field count budget." :
                        request ? "The request headers exceed their byte budget." : "The prepared response headers exceed their byte budget."));
            }
        }
    }
}
