// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Models;

public static partial class RegistryDomainRules
{
    private static readonly RegistryModel PropertyScalars = RegistryModel.Compile(RegistryJson.Parse("""
        {"attributes":{"values":{"type":"object","attributes":{
          "timestamp":{"type":"timestamp"},"uri":{"type":"uri"},"uriabsolute":{"type":"uriabsolute"}
        }}}}
        """));

    /// <summary>Applies the Message domain's declaration defaults without interpreting opaque schema contents or acquiring references.</summary>
    public static RegistryJson CompleteMessageMetadata(RegistryJson metadata, RegistryJsonLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= new();
        var bounded = RegistryJson.FromElement(metadata.RootElement, limits);
        ValidateMessageMetadata(bounded.RootElement, cancellationToken);
        var result = JsonNode.Parse(bounded.RootElement.GetRawText())!.AsObject();
        foreach (var (declaration, path) in PropertyDeclarations(bounded.RootElement))
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonNode node = result;
            foreach (var token in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                node = node is JsonArray array
                    ? array[int.Parse(token, System.Globalization.CultureInfo.InvariantCulture)]!
                    : node[token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)]!;
            }
            var name = path[(path.LastIndexOf('/') + 1)..];
            node["type"] ??= DefaultPropertyType(bounded.RootElement, path, name);
            node["required"] ??= path.StartsWith("/envelopemetadata/", StringComparison.Ordinal) && name is "specversion" or "id" or "source" or "type";
            if (path == "/envelopemetadata/specversion" && !declaration.TryGetProperty("value", out _)) { node["value"] = "1.0"; }
            if (path == "/envelopemetadata/time" && !declaration.TryGetProperty("value", out _)) { node["value"] = "0000-01-01T00:00:00Z"; }
        }
        return RegistryJson.Create(writer => result.WriteTo(writer), limits);
    }

    private static IEnumerable<(JsonElement Declaration, string Path)> PropertyDeclarations(JsonElement message)
    {
        if (SelectorIs(message, "envelope", "CloudEvents/1.0") && message.TryGetProperty("envelopemetadata", out var envelope))
        {
            if (envelope.ValueKind != JsonValueKind.Object) { throw Invalid("Envelope declarations must be an object.", "/envelopemetadata"); }
            foreach (var property in envelope.EnumerateObject())
            {
                if (property.Name.Length == 0 || property.Name.Any(static c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c)))
                {
                    throw Invalid("CloudEvents attribute names must be lowercase alphanumeric.", At("/envelopemetadata", property.Name));
                }
                yield return (property.Value, At("/envelopemetadata", property.Name));
            }
        }
        if (!TryObject(message, "protocoloptions", out var options)) { yield break; }
        if (!SelectorIs(message, "protocol", "AMQP/1.0"))
        {
            foreach (var header in HeaderDeclarations(message, options)) { yield return header; }
            yield break;
        }
        foreach (var name in new[] { "properties", "application-properties", "message-annotations", "delivery-annotations", "footer" })
        {
            if (!options.TryGetProperty(name, out var map)) { continue; }
            var path = "/protocoloptions/" + name;
            if (map.ValueKind != JsonValueKind.Object) { throw Invalid("AMQP property declarations must form an object.", path); }
            foreach (var property in map.EnumerateObject())
            {
                if (name != "properties" && !Symbol(property.Name))
                {
                    throw Invalid("AMQP property names must be symbols.", At(path, property.Name));
                }
                if (name == "properties" && property.Name is not ("message-id" or "user-id" or "to" or "subject" or "reply-to" or
                    "correlation-id" or "content-type" or "content-encoding" or "absolute-expiry-time" or "group-id" or
                    "group-sequence" or "reply-to-group-id"))
                {
                    throw Invalid("Unknown fixed AMQP property.", At(path, property.Name));
                }
                yield return (property.Value, At(path, property.Name));
            }
        }
    }

    private static void ValidateMessageProperties(JsonElement message, CancellationToken cancellationToken)
    {
        foreach (var (declaration, path) in PropertyDeclarations(message))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (declaration.ValueKind != JsonValueKind.Object) { throw Invalid("A property declaration must be an object.", path); }
            var cloud = path.StartsWith("/envelopemetadata/", StringComparison.Ordinal);
            var header = !cloud && !SelectorIs(message, "protocol", "AMQP/1.0");
            foreach (var field in declaration.EnumerateObject())
            {
                if (field.Name is not ("description" or "required" or "specurl" or "type" or "value") &&
                    !(header && field.Name == "name"))
                {
                    throw Invalid("Unknown property declaration member.", At(path, field.Name));
                }
                if (field.Name is "description" or "type" or "specurl" && field.Value.ValueKind != JsonValueKind.String)
                {
                    throw Invalid("This property declaration member must be a string.", At(path, field.Name));
                }
            }
            _ = OptionalText(declaration, "description", At(path, "description"));
            if (declaration.TryGetProperty("required", out var required) && required.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw Invalid("The required declaration must be boolean.", At(path, "required"));
            }
            var name = path[(path.LastIndexOf('/') + 1)..];
            var type = OptionalText(declaration, "type", At(path, "type")) ?? DefaultPropertyType(message, path, name);
            if (type is not ("any" or "binary" or "boolean" or "duration" or "integer" or "number" or "string" or
                "symbol" or "timestamp" or "uri" or "uritemplate" or "ulong" or "uuid" or "stringified integer"))
            {
                throw Invalid("Unknown Message property type.", At(path, "type"));
            }
            if (header)
            {
                if (!declaration.TryGetProperty("name", out var headerName) || headerName.ValueKind != JsonValueKind.String ||
                    headerName.GetString() is not { Length: > 0 })
                {
                    throw Invalid("A header declaration requires a nonempty name.", At(path, "name"));
                }
                ValidatePropertyValue(headerName, "string", At(path, "name"), true, false, false, cancellationToken);
                if (type is not ("any" or "string" or "symbol" or "uri" or "uritemplate" or "stringified integer" or
                    "timestamp" or "duration" or "uuid") && !(type == "binary" && SelectorIs(message, "protocol", "KAFKA")))
                {
                    throw Invalid("A header type must refine the protocol's native string or byte value.", At(path, "type"));
                }
            }
            if (cloud && name is "specversion" or "id" or "source" or "type" && required.ValueKind == JsonValueKind.False)
            {
                throw Invalid("A mandatory CloudEvents attribute cannot be optional.", At(path, "required"));
            }
            if (cloud && name == "specversion" && type != "string")
            {
                throw Invalid("CloudEvents specversion must be a string.", At(path, "type"));
            }
            if (cloud && name == "time" && type != "timestamp")
            {
                throw Invalid("CloudEvents time must retain its timestamp type.", At(path, "type"));
            }
            if (cloud && name is "id" or "type" or "subject" or "source" or "dataschema" or "datacontenttype" &&
                type is not ("string" or "uri" or "uritemplate" or "symbol" or "stringified integer"))
            {
                throw Invalid("A CloudEvents string attribute can only be refined by a string-valued type.", At(path, "type"));
            }
            if (!cloud && path.StartsWith("/protocoloptions/properties/", StringComparison.Ordinal))
            {
                var expected = DefaultPropertyType(message, path, name);
                var valid = name == "message-id" ? type is "ulong" or "uuid" or "binary" or "string" or "uritemplate" :
                    expected is "string" or "symbol" or "uritemplate" ? type is "string" or "symbol" or "uri" or "uritemplate" :
                    type == expected;
                if (!valid) { throw Invalid("The declared type contradicts the fixed AMQP property.", At(path, "type")); }
            }
            if (!declaration.TryGetProperty("value", out var value)) { continue; }
            var at = At(path, "value");
            if (header)
            {
                if (SelectorIs(message, "protocol", "KAFKA") && type == "any" && value.ValueKind == JsonValueKind.Null) { continue; }
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw Invalid("A header literal must retain its native string or encoded-byte representation.", at);
                }
                ValidatePropertyValue(value, type == "any" ? "string" : type, at, true, false, false, cancellationToken);
                continue;
            }
            if (cloud && name == "specversion" && (value.ValueKind != JsonValueKind.String || value.GetString() != "1.0"))
            {
                throw Invalid("CloudEvents specversion must be 1.0.", at);
            }
            if (cloud && name is "id" or "source" or "type" or "subject" or "dataschema" &&
                value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0)
            {
                throw Invalid("The declared CloudEvents context attribute value must not be empty.", at);
            }
            ValidatePropertyValue(value, type, at, !cloud, name is "content-type" or "content-encoding",
                path == "/protocoloptions/properties/group-sequence", cancellationToken);
        }
    }

    private static IEnumerable<(JsonElement Declaration, string Path)> HeaderDeclarations(JsonElement message, JsonElement options)
    {
        var map = SelectorIs(message, "protocol", "KAFKA");
        var field = SelectorIs(message, "protocol", "MQTT/5.0") ? "user_properties" :
            map || SelectorIs(message, "protocol", "HTTP") || SelectorIs(message, "protocol", "NATS") ? "headers" : null;
        if (field is null || !options.TryGetProperty(field, out var headers)) { yield break; }
        var path = "/protocoloptions/" + field;
        if (headers.ValueKind != (map ? JsonValueKind.Object : JsonValueKind.Array))
        {
            throw Invalid("Header declarations must retain their protocol's map or array shape.", path);
        }
        if (map)
        {
            foreach (var property in headers.EnumerateObject()) { yield return (property.Value, At(path, property.Name)); }
        }
        else
        {
            var index = 0;
            foreach (var item in headers.EnumerateArray())
            {
                yield return (item, At(path, (index++).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
    }

    private static string DefaultPropertyType(JsonElement message, string path, string name) =>
        path.StartsWith("/envelopemetadata/", StringComparison.Ordinal) ? name switch
        {
            "source" or "dataschema" => "uritemplate",
            "time" => "timestamp",
            _ => "string",
        } : SelectorIs(message, "protocol", "AMQP/1.0") && path.StartsWith("/protocoloptions/properties/", StringComparison.Ordinal)
            ? name switch
            {
                "user-id" => "binary",
                "to" or "reply-to" or "reply-to-group-id" => "uritemplate",
                "absolute-expiry-time" => "timestamp",
                "group-sequence" => "integer",
                "content-type" or "content-encoding" => "symbol",
                _ => "string",
            } : "string";

    private static void ValidatePropertyValue(JsonElement value, string type, string path, bool amqp, bool mediaSymbol,
        bool unsignedSequence, CancellationToken cancellationToken)
    {
        if (type == "any") { return; }
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var valid = type switch
        {
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => IntegerRange(value, unsignedSequence ? 0 : int.MinValue, unsignedSequence ? uint.MaxValue : int.MaxValue),
            "ulong" => IntegerRange(value, 0, ulong.MaxValue),
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
            "binary" => text is not null && Base64(text),
            "uuid" => text is not null && Guid.TryParseExact(text, "D", out _),
            "stringified integer" => text is not null && text.Length != 0 &&
                (text[0] == '-' ? text.Length > 1 && text.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0 :
                    text.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0),
            "duration" => text is not null && Duration(text),
            _ => text is not null,
        };
        if (!valid) { throw Invalid("The literal value does not match its Message property type.", path); }
        if (text is null) { return; }
        if (type is "string" or "symbol" or "uri" or "uritemplate")
        {
            foreach (var rune in text.EnumerateRunes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rune.Value < 0x20 || rune.Value is >= 0x7f and <= 0x9f or >= 0xfdd0 and <= 0xfdef ||
                    (rune.Value & 0xffff) >= 0xfffe)
                {
                    throw Invalid("The property string contains a forbidden control or Unicode noncharacter.", path);
                }
            }
        }
        var literal = TemplateLiteral(text, path, type == "uritemplate" || amqp && type is "uri" or "symbol");
        if (type == "symbol" && !(amqp && mediaSymbol) && !Symbol(literal))
        {
            throw Invalid("A symbol contains only alphanumeric characters and underscores.", path);
        }
        if (type == "uri")
        {
            Scalar("uriabsolute", RegistryJson.Create(writer => writer.WriteStringValue(amqp ? literal : text)).RootElement, path, cancellationToken);
        }
        if (type == "timestamp" && text != "0000-01-01T00:00:00Z")
        {
            Scalar("timestamp", value, path, cancellationToken);
        }
    }

    private static bool IntegerRange(JsonElement value, BigInteger minimum, BigInteger maximum)
    {
        if (value.ValueKind != JsonValueKind.Number) { return false; }
        var number = RegistryNumber.FromElement(value);
        if (!number.IsInteger) { return false; }
        var integer = number.ToBigInteger();
        return integer >= minimum && integer <= maximum;
    }

    private static void Scalar(string type, JsonElement value, string path, CancellationToken cancellationToken)
    {
        try
        {
            RegistryMetadataValidator.Validate(RegistryJson.Create(writer =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName(type);
                value.WriteTo(writer);
                writer.WriteEndObject();
            }), PropertyScalars.Attributes["values"].Attributes, cancellationToken: cancellationToken);
        }
        catch (RegistryException exception)
        {
            throw new RegistryException(new("invalid_attribute", path, "The declared property value has invalid " + type + " syntax."), exception);
        }
    }

    private static bool Symbol(string text) => text.Length != 0 && text.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static bool Base64(string text)
    {
        try { return Convert.ToBase64String(Convert.FromBase64String(text)) == text; }
        catch (FormatException) { return false; }
    }

    private static bool Duration(string text)
    {
        var value = text.AsSpan();
        if (value.StartsWith("-")) { value = value[1..]; }
        if (value.Length < 2 || value[0] != 'P') { return false; }
        var time = false;
        var units = 0;
        var previous = -1;
        for (var index = 1; index < value.Length;)
        {
            if (value[index] == 'T')
            {
                if (time || ++index == value.Length) { return false; }
                time = true;
                previous = -1;
                continue;
            }
            var start = index;
            while (index < value.Length && char.IsAsciiDigit(value[index])) { index++; }
            if (index == start) { return false; }
            if (index < value.Length && value[index] == '.')
            {
                var fraction = ++index;
                while (index < value.Length && char.IsAsciiDigit(value[index])) { index++; }
                if (index == fraction || index >= value.Length || value[index] != 'S' || !time) { return false; }
            }
            if (index == value.Length) { return false; }
            var unit = (time ? "HMS" : "YMD").IndexOf(value[index++], StringComparison.Ordinal);
            if (unit < 0 || unit <= previous) { return false; }
            previous = unit;
            units++;
        }
        return units != 0;
    }
}
