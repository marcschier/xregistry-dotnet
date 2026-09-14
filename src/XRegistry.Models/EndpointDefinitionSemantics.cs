using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using static XRegistry.Models.EndpointTemplateExpansion;

namespace XRegistry.Models;

internal static partial class EndpointDefinitionSemantics
{
    private const string OptionsPath = "/protocoloptions";
    private static readonly RegistryJson Defaults = RegistryJson.Parse("""
        {"common":{"deployed":true},
         "HTTP":{"method":"POST","apikeyin":"header","plainscheme":"basic"},
         "AMQP/1.0":{"durable":false,"distribution-mode":"move"},
         "MQTT/3.1.1":{"qos":0,"retain":false,"cleansession":true},
         "MQTT/5.0":{"qos":0,"retain":false},
         "KAFKA":{"acks":1}}
        """);

    internal static string? Protocol(JsonElement endpoint) =>
        endpoint.TryGetProperty("protocol", out var value) && value.ValueKind == JsonValueKind.String
            ? CanonicalProtocol(value.GetString()!)
            : null;

    internal static string CanonicalProtocol(string value) => value.ToUpperInvariant() switch
    {
        "AMQP" or "AMQP/1.0" => "AMQP/1.0",
        "MQTT" or "MQTT/5.0" => "MQTT/5.0",
        "MQTT/3.1.1" => "MQTT/3.1.1",
        "HTTP" => "HTTP",
        "KAFKA" => "KAFKA",
        "NATS" => "NATS",
        _ => value
    };

    internal static JsonElement GetOption(JsonElement endpoint, string name)
    {
        if (endpoint.TryGetProperty("protocoloptions", out var options) && options.TryGetProperty(name, out var value))
        {
            return value;
        }
        if (Defaults.RootElement.GetProperty("common").TryGetProperty(name, out value))
        {
            return value;
        }
        return Protocol(endpoint) is { } protocol && Defaults.RootElement.TryGetProperty(protocol, out var defaults) &&
            defaults.TryGetProperty(name, out value) ? value : default;
    }

    internal static void Validate(JsonElement endpoint, RegistryJsonLimits limits, CancellationToken cancellationToken)
    {
        RegistryDomainRules.ValidateEndpointCommonMetadata(endpoint, cancellationToken);
        foreach (var name in new[] { "protocol", "envelope", "channel" })
        {
            _ = Text(endpoint, name, "", nonempty: true);
        }
        ValidateCommonEnvelopeOptions(endpoint);
        ValidateOptionShapes(endpoint, limits, resolved: true, cancellationToken);
    }

    internal static void ValidateCommonEnvelopeOptions(JsonElement endpoint)
    {
        if (endpoint.TryGetProperty("envelopeoptions", out var envelopeOptions))
        {
            RequireKind(envelopeOptions, JsonValueKind.Object, "/envelopeoptions");
            if (endpoint.TryGetProperty("envelope", out var envelope) &&
                string.Equals(envelope.GetString(), "CloudEvents/1.0", StringComparison.OrdinalIgnoreCase))
            {
                Enum(envelopeOptions, "mode", "/envelopeoptions", ["binary", "structured"]);
                if (Text(envelopeOptions, "format", "/envelopeoptions") is { } format &&
                    !MediaTypeHeaderValue.TryParse(format, out _))
                {
                    throw Invalid("/envelopeoptions/format", "The envelope format must be a media type.");
                }
            }
        }
    }

    internal static void ValidateOptionShapes(JsonElement endpoint, RegistryJsonLimits limits, bool resolved,
        CancellationToken cancellationToken)
    {
        if (!endpoint.TryGetProperty("protocoloptions", out var options))
        {
            return;
        }
        RequireKind(options, JsonValueKind.Object, OptionsPath);
        ValidateAuthorizationShape(options, cancellationToken);
        Boolean(options, "deployed");
        switch (Protocol(endpoint))
        {
            case "HTTP":
                ValidateHttpOptions(options, resolved, cancellationToken);
                break;
            case "AMQP/1.0":
                Strings(options, ["node"], cancellationToken);
                Booleans(options, ["durable", "dynamic"], cancellationToken);
                StringMap(options, "link-properties", cancellationToken);
                StringMap(options, "connection-properties", cancellationToken);
                StringArray(options, "connection-capabilities", cancellationToken);
                StringArray(options, "node-capabilities", cancellationToken);
                OptionalKind(options, "source-filters", JsonValueKind.Object, OptionsPath);
                Enum(options, "distribution-mode", OptionsPath, ["move", "copy"], resolved);
                Enum(options, "terminus-durability", OptionsPath, ["none", "configuration", "unsettled-state"], resolved);
                Enum(options, "expiry-policy", OptionsPath, ["link-detach", "session-end", "connection-close", "never"], resolved);
                Enum(options, "sender-settle-mode", OptionsPath, ["unsettled", "settled", "mixed"], resolved);
                Enum(options, "receiver-settle-mode", OptionsPath, ["first", "second"], resolved);
                Integer(options, "timeout", limits, 0, validateValue: resolved);
                break;
            case "MQTT/3.1.1":
                ValidateMqttTypes(options, limits, resolved, cancellationToken);
                Boolean(options, "cleansession");
                break;
            case "MQTT/5.0":
                ValidateMqttTypes(options, limits, resolved, cancellationToken);
                Strings(options, ["sharedsubscriptiongroup"], cancellationToken);
                Booleans(options, ["cleanstart", "nolocal", "retainaspublished"], cancellationToken);
                Integer(options, "sessionexpiryinterval", limits, 0, uint.MaxValue, resolved);
                Integer(options, "retainhandling", limits, 0, 2, resolved);
                break;
            case "KAFKA":
                Strings(options, ["topic", "key", "consumergroup", "keyserializer", "valueserializer"], cancellationToken);
                Integer(options, "acks", limits, -1, 1, resolved);
                Integer(options, "partition", limits, validateValue: resolved);
                StringMap(options, "headers", cancellationToken);
                Enum(options, "autooffsetreset", OptionsPath, ["earliest", "latest", "none"], resolved);
                Boolean(options, "enableautocommit");
                break;
            case "NATS":
                Strings(options, ["subject", "subjectfilter", "queuegroup"], cancellationToken);
                break;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void ValidateHttpOptions(JsonElement options, bool resolved, CancellationToken cancellationToken)
    {
        Strings(options, ["apikeyname", "plainusernamefield", "plainpasswordfield"], cancellationToken);
        if (Text(options, "method", OptionsPath) is { } method && resolved)
        {
            Token(method, "/protocoloptions/method");
        }
        Enum(options, "apikeyin", OptionsPath, ["header", "query"], resolved);
        Enum(options, "plainscheme", OptionsPath, ["basic", "form", "query"], resolved);
        StringMap(options, "query", cancellationToken);
        if (!options.TryGetProperty("headers", out var headers))
        {
            return;
        }
        RequireKind(headers, JsonValueKind.Array, "/protocoloptions/headers");
        var index = 0;
        foreach (var header in headers.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = At("/protocoloptions/headers", (index++).ToString(CultureInfo.InvariantCulture));
            RequireKind(header, JsonValueKind.Object, path);
            var name = Text(header, "name", path, required: resolved);
            if (resolved && name is not null)
            {
                Token(name, At(path, "name"));
            }
            var value = Text(header, "value", path, required: resolved);
            if (resolved && value is not null && value.Any(static character => char.IsControl(character) && character != '\t'))
            {
                throw Invalid(At(path, "value"), "HTTP field values must not contain line breaks or other control characters.");
            }
        }
    }

    private static void ValidateMqttTypes(JsonElement options, RegistryJsonLimits limits, bool resolved, CancellationToken cancellationToken)
    {
        Strings(options, ["topic", "topicfilter", "willtopic", "willmessage"], cancellationToken);
        Integer(options, "qos", limits, 0, 2, resolved);
        Boolean(options, "retain");
    }

    private static void Strings(JsonElement options, ReadOnlySpan<string> names, CancellationToken cancellationToken)
    {
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Text(options, name, OptionsPath);
        }
    }

    private static void Booleans(JsonElement options, ReadOnlySpan<string> names, CancellationToken cancellationToken)
    {
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Boolean(options, name);
        }
    }

    private static void Boolean(JsonElement options, string name)
    {
        if (options.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid(At(OptionsPath, name), "The option must be a JSON boolean, not a quoted substitution.");
        }
    }

    private static void Integer(JsonElement options, string name, RegistryJsonLimits limits, long? minimum = null, long? maximum = null,
        bool validateValue = true)
    {
        if (!options.TryGetProperty(name, out var value))
        {
            return;
        }
        var path = At(OptionsPath, name);
        RequireKind(value, JsonValueKind.Number, path);
        if (!validateValue)
        {
            return;
        }
        var number = RegistryNumber.FromElement(value, limits);
        if (!number.IsInteger)
        {
            throw Invalid(path, "The option must be an exact integer.");
        }
        if (minimum == 0 && number.Significand.Sign < 0)
        {
            throw Invalid(path, "The option must be nonnegative.");
        }
        if (maximum is null && minimum is null or 0)
        {
            return;
        }
        if (number.Exponent > 19 && !number.Significand.IsZero)
        {
            throw Invalid(path, "The option is outside its protocol-defined integer range.");
        }
        var integer = number.ToBigInteger();
        if (minimum is { } min && integer < min || maximum is { } max && integer > max)
        {
            throw Invalid(path, "The option is outside its protocol-defined integer range.");
        }
    }

    private static void StringMap(JsonElement options, string name, CancellationToken cancellationToken)
    {
        if (!options.TryGetProperty(name, out var map))
        {
            return;
        }
        var path = At(OptionsPath, name);
        RequireKind(map, JsonValueKind.Object, path);
        foreach (var entry in map.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireKind(entry.Value, JsonValueKind.String, At(path, entry.Name));
        }
    }

    private static void StringArray(JsonElement options, string name, CancellationToken cancellationToken)
    {
        if (!options.TryGetProperty(name, out var array))
        {
            return;
        }
        var path = At(OptionsPath, name);
        RequireKind(array, JsonValueKind.Array, path);
        var index = 0;
        foreach (var entry in array.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireKind(entry, JsonValueKind.String, At(path, (index++).ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static void Enum(JsonElement value, string name, string path, ReadOnlySpan<string> allowed, bool validateValue = true)
    {
        var text = Text(value, name, path);
        if (validateValue && text is not null && !allowed.Contains(text))
        {
            throw Invalid(At(path, name), "The option must match a protocol-defined, case-sensitive enum value.");
        }
    }

    private static string? Text(JsonElement value, string name, string path, bool required = false, bool nonempty = false)
    {
        if (!value.TryGetProperty(name, out var member))
        {
            return required ? throw Invalid(At(path, name), "A required string member is missing.") : null;
        }
        RequireKind(member, JsonValueKind.String, At(path, name));
        var text = member.GetString()!;
        if (nonempty && text.Length == 0)
        {
            throw Invalid(At(path, name), "The member must be nonempty.");
        }
        return text;
    }

    private static void OptionalKind(JsonElement value, string name, JsonValueKind kind, string path)
    {
        if (value.TryGetProperty(name, out var member))
        {
            RequireKind(member, kind, At(path, name));
        }
    }

    private static void RequireKind(JsonElement value, JsonValueKind kind, string path)
    {
        if (value.ValueKind != kind)
        {
            throw Invalid(path, $"The member must be a JSON {kind}.");
        }
    }

    private static void Token(string value, string path)
    {
        if (value.Length == 0 || value.Any(static character => !char.IsAsciiLetterOrDigit(character) &&
            !"!#$%&'*+-.^_`|~".Contains(character, StringComparison.Ordinal)))
        {
            throw Invalid(path, "The HTTP name or method must be a nonempty token.");
        }
    }

    private static RegistryException Invalid(string path, string message) => Error("invalid_attribute", path, message);
}
