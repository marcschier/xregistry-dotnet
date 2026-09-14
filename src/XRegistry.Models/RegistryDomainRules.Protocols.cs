using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XRegistry.Models;

public static partial class RegistryDomainRules
{
    private static void ValidateMessageProtocol(JsonElement message, CancellationToken cancellationToken)
    {
        foreach (var (declaration, path) in MessageDeclarations(message))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (declaration.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
            {
                _ = TemplateLiteral(value.GetString()!, At(path, "value"),
                    SelectorIs(declaration, "type", "uritemplate"));
            }
        }

        if (!TryObject(message, "protocoloptions", out var options))
        {
            return;
        }

        var protocol = OptionalText(message, "protocol", "/protocol");
        string[] fields = protocol?.ToUpperInvariant() switch
        {
            "HTTP" => ["headers", "query", "path", "method", "status"],
            "AMQP/1.0" => ["properties", "application-properties", "message-annotations", "delivery-annotations", "header", "footer"],
            "MQTT/3.1.1" => ["topic_name"],
            "MQTT/5.0" => ["topic_name", "response_topic", "content_type", "user_properties"],
            "KAFKA" => ["topic", "key", "headers"],
            "NATS" => ["subject", "reply-to", "headers"],
            _ => []
        };
        foreach (var name in fields)
        {
            if (options.TryGetProperty(name, out var value))
            {
                ValidateTemplateValues(value, "/protocoloptions/" + name,
                    name is "path" or "topic_name" or "response_topic" or "subject" or "reply-to" or "topic" or "key",
                    cancellationToken);
            }
        }

        if (SelectorIs(message, "protocol", "AMQP/1.0") && TryObject(options, "header", out var headerValues))
        {
            ProtocolInteger(headerValues, "priority", byte.MaxValue, "/protocoloptions/header");
            ProtocolInteger(headerValues, "ttl", uint.MaxValue, "/protocoloptions/header");
            ProtocolInteger(headerValues, "delivery-count", uint.MaxValue, "/protocoloptions/header");
        }
        if (SelectorIs(message, "protocol", "MQTT/3.1.1") || SelectorIs(message, "protocol", "MQTT/5.0"))
        {
            ProtocolInteger(options, "qos", 2, "/protocoloptions");
            ProtocolInteger(options, "payload_format_indicator", 1, "/protocoloptions");
            ProtocolInteger(options, "message_expiry_interval", uint.MaxValue, "/protocoloptions");
        }
        if (SelectorIs(message, "protocol", "MQTT/5.0"))
        {
            ProtocolBinary(options, "correlation_data");
        }
        if (SelectorIs(message, "protocol", "KAFKA"))
        {
            ProtocolBinary(options, "key_base64");
        }

        if (SelectorIs(message, "protocol", "HTTP"))
        {
            if (OptionalText(options, "status", "/protocoloptions/status", nonempty: false) is { } status)
            {
                if (Present(options, "method"))
                {
                    throw Invalid("HTTP method and status declarations are mutually exclusive.", "/protocoloptions/status");
                }
                _ = TemplateLiteral(status, "/protocoloptions/status", true);
                if (!status.Contains('{', StringComparison.Ordinal) &&
                    (status.Length != 3 || status[0] is < '1' or > '5' ||
                        !char.IsAsciiDigit(status[1]) || !char.IsAsciiDigit(status[2])))
                {
                    throw Invalid("The HTTP status declaration must be a three-digit response code from 100 through 599.",
                        "/protocoloptions/status");
                }
            }
            if (OptionalText(options, "method", "/protocoloptions/method", nonempty: false) is { } method)
            {
                ValidateHttpToken(method, "/protocoloptions/method");
            }
            if (options.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var header in headers.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = "/protocoloptions/headers/" + index.ToString(CultureInfo.InvariantCulture) + "/name";
                    if (header.ValueKind == JsonValueKind.Object &&
                        OptionalText(header, "name", path, nonempty: false) is { } name)
                    {
                        ValidateHttpToken(name, path);
                    }
                    index++;
                }
            }
        }
        else if (SelectorIs(message, "protocol", "KAFKA") && Present(options, "key") && Present(options, "key_base64"))
        {
            throw Invalid("Kafka key and key_base64 are mutually exclusive.", "/protocoloptions/key_base64");
        }
    }

    private static void ProtocolInteger(JsonElement options, string name, uint maximum, string path)
    {
        if (options.TryGetProperty(name, out var value) && !IntegerRange(value, 0, maximum))
        {
            throw Invalid("The integer is outside the underlying protocol's permitted range.", At(path, name));
        }
    }

    private static void ProtocolBinary(JsonElement options, string name)
    {
        if (options.TryGetProperty(name, out var value) &&
            (value.ValueKind != JsonValueKind.String || !Base64(value.GetString()!)))
        {
            throw Invalid("The binary protocol declaration must be a canonical base64 string.", At("/protocoloptions", name));
        }
    }

    private static void ValidateTemplateValues(JsonElement value, string path, bool strict, CancellationToken cancellationToken)
    {
        var pending = new Stack<(JsonElement Value, string Path, bool Strict)>();
        pending.Push((value, path, strict));
        while (pending.TryPop(out var next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (next.Value.ValueKind == JsonValueKind.String)
            {
                _ = TemplateLiteral(next.Value.GetString()!, next.Path, next.Strict);
            }
            else if (next.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in next.Value.EnumerateObject())
                {
                    if (property.Name is not ("description" or "specurl" or "type" or "required"))
                    {
                        pending.Push((property.Value, At(next.Path, property.Name), false));
                    }
                }
            }
            else if (next.Value.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in next.Value.EnumerateArray())
                {
                    pending.Push((item, At(next.Path, index.ToString(CultureInfo.InvariantCulture)), false));
                    index++;
                }
            }
        }
    }

    private static void ValidateHttpToken(string value, string path)
    {
        var literal = TemplateLiteral(value, path, true);
        if (literal.Length == 0 || literal.Any(static character =>
            !char.IsAsciiLetterOrDigit(character) && !"!#$%&'*+-.^_`|~".Contains(character, StringComparison.Ordinal)))
        {
            throw Invalid("The HTTP declaration must be a valid field-name or method token.", path);
        }
    }

    private static string TemplateLiteral(string value, string path, bool strict)
    {
        StringBuilder? literal = null;
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '}' && strict)
            {
                throw Invalid("A Message template has an unmatched closing brace.", path);
            }
            if (value[index] != '{')
            {
                continue;
            }
            if (!strict && (index + 1 == value.Length ||
                !char.IsAsciiLetterOrDigit(value[index + 1]) && value[index + 1] != '_' &&
                !"+#./;?&".Contains(value[index + 1], StringComparison.Ordinal)))
            {
                continue;
            }
            var end = value.IndexOf('}', index + 1);
            if (end < 0 || end == index + 1 ||
                value.AsSpan(index + 1, end - index - 1)
                    .ContainsAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_".AsSpan()))
            {
                throw Invalid("Message placeholders must be Level-1 expansions with nonempty symbol names.", path);
            }
            literal ??= new StringBuilder(value.Length);
            literal.Append(value, start, index - start).Append('x');
            index = end;
            start = end + 1;
        }
        return literal is null ? value : literal.Append(value, start, value.Length - start).ToString();
    }
}
