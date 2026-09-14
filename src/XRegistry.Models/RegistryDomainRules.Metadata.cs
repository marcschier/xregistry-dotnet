using System.Globalization;
using System.Text.Json;

namespace XRegistry.Models;

public static partial class RegistryDomainRules
{
    /// <summary>Checks the common selectors of completed Message Group metadata.</summary>
    public static void ValidateMessageGroupMetadata(JsonElement group, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireMetadataObject(group);
        ValidateSelector(group, "envelope", true);
        ValidateSelector(group, "protocol", false);
    }

    /// <summary>Checks common Endpoint rules and authored option shapes/templates without consumer protocol checks or acquisition.</summary>
    public static void ValidateEndpointMetadata(JsonElement endpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            EndpointDefinition.ValidateAuthoredElement(endpoint, null, cancellationToken);
        }
        catch (RegistryException exception) when (exception.Diagnostic.Code != "invalid_attribute")
        {
            throw new RegistryException(exception.Diagnostic with { Code = "invalid_attribute" }, exception);
        }
    }

    internal static void ValidateEndpointCommonMetadata(JsonElement endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEndpointUsage(endpoint);
        ValidateSelector(endpoint, "envelope", false);
        ValidateSelector(endpoint, "protocol", false);
        _ = OptionalText(endpoint, "channel", "/channel");
        if (SelectorIs(endpoint, "envelope", "CloudEvents/1.0") &&
            TryObject(endpoint, "envelopeoptions", out var envelopeOptions) &&
            envelopeOptions.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String &&
            mode.GetString() == "binary" && Present(envelopeOptions, "format"))
        {
            throw Invalid("A binary CloudEvents Endpoint must not declare an envelope format.", "/envelopeoptions/format");
        }
        if (!TryObject(endpoint, "protocoloptions", out var options) || !Present(options, "authorization"))
        {
            return;
        }

        var authorization = options.GetProperty("authorization");
        if (authorization.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("Authorization metadata must be an array of objects.", "/protocoloptions/authorization");
        }
        var index = 0;
        foreach (var entry in authorization.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = "/protocoloptions/authorization/" + index.ToString(CultureInfo.InvariantCulture);
            index++;
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("Authorization metadata must contain objects.", path);
            }
            foreach (var name in new[] { "type", "mechanism", "resourceuri", "authorityuri" })
            {
                _ = OptionalText(entry, name, At(path, name));
            }
        }
    }

    private static void ValidateMessageStrings(JsonElement message, CancellationToken cancellationToken)
    {
        ValidateSelector(message, "envelope", true);
        ValidateSelector(message, "protocol", false);
        ValidateSelector(message, "dataschemaformat", true);
        _ = OptionalText(message, "dataschemauri", "/dataschemauri", nonempty: false);
        foreach (var (declaration, path) in MessageDeclarations(message))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = OptionalText(declaration, "description", At(path, "description"));
            if (declaration.TryGetProperty("specurl", out var specification))
            {
                Scalar("uri", specification, At(path, "specurl"), cancellationToken);
            }
        }
    }

    private static IEnumerable<(JsonElement Declaration, string Path)> MessageDeclarations(JsonElement message)
    {
        if (SelectorIs(message, "envelope", "CloudEvents/1.0") && TryObject(message, "envelopemetadata", out var envelope))
        {
            foreach (var property in envelope.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    yield return (property.Value, At("/envelopemetadata", property.Name));
                }
            }
        }
        if (!TryObject(message, "protocoloptions", out var options))
        {
            yield break;
        }

        if (SelectorIs(message, "protocol", "AMQP/1.0"))
        {
            foreach (var name in new[] { "properties", "application-properties", "message-annotations", "delivery-annotations", "footer" })
            {
                if (TryObject(options, name, out var properties))
                {
                    foreach (var property in properties.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Object)
                        {
                            yield return (property.Value, At("/protocoloptions/" + name, property.Name));
                        }
                    }
                }
            }
        }
        else if (SelectorIs(message, "protocol", "KAFKA") && TryObject(options, "headers", out var headers))
        {
            foreach (var property in headers.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    yield return (property.Value, At("/protocoloptions/headers", property.Name));
                }
            }
        }
        else
        {
            string[] names = SelectorIs(message, "protocol", "HTTP") ? ["headers"] :
                SelectorIs(message, "protocol", "MQTT/5.0") ? ["user_properties"] :
                SelectorIs(message, "protocol", "NATS") ? ["headers"] : [];
            foreach (var name in names)
            {
                if (!options.TryGetProperty(name, out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                var index = 0;
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        yield return (item, "/protocoloptions/" + name + "/" + index.ToString(CultureInfo.InvariantCulture));
                    }
                    index++;
                }
            }
        }
    }

    private static void ValidateSelector(JsonElement metadata, string name, bool versionRequired)
    {
        var value = OptionalText(metadata, name, "/" + name);
        if (value is null)
        {
            return;
        }
        var separator = value.IndexOf('/', StringComparison.Ordinal);
        if (separator == 0 || separator == value.Length - 1 || versionRequired && separator < 0)
        {
            throw Invalid("The selector must contain a nonempty specification name and the required version.", "/" + name);
        }
    }

    private static string? OptionalText(JsonElement metadata, string name, string path, bool nonempty = true)
    {
        if (!metadata.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || nonempty && value.GetString()!.Length == 0)
        {
            throw Invalid("The domain attribute must be a nonempty string when supplied.", path);
        }
        return value.GetString();
    }

    private static bool SelectorIs(JsonElement metadata, string name, string expected) =>
        metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool TryObject(JsonElement metadata, string name, out JsonElement value) =>
        metadata.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static void RequireMetadataObject(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Domain metadata must be an object.", "");
        }
    }

    private static string At(string path, string name) =>
        path + "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
