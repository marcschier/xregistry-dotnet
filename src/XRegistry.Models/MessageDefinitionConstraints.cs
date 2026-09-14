using System.Text.Json;

namespace XRegistry.Models;

/// <summary>The source of the effective Message binding rules; this does not select or execute a transport.</summary>
public enum MessageDefinitionBindingKind
{
    /// <summary>No envelope or protocol binding was declared.</summary>
    Unspecified,
    /// <summary>The envelope's implicit binding rules apply without choosing a concrete transport.</summary>
    EnvelopeDefined,
    /// <summary>The explicit protocol rules take precedence over the envelope's implicit bindings.</summary>
    ExplicitProtocol
}

/// <summary>Read-only envelope, protocol and payload views over a composed Message definition.</summary>
public sealed class MessageDefinitionConstraints
{
    internal MessageDefinitionConstraints(RegistryJson metadata)
    {
        Metadata = metadata;
    }

    /// <summary>The composed, owned definition carrying the constraints.</summary>
    public RegistryJson Metadata { get; }
    /// <summary>The declared envelope, if any.</summary>
    public string? Envelope => Text(Metadata.RootElement, "envelope");
    /// <summary>The explicit protocol, if any.</summary>
    public string? Protocol => Text(Metadata.RootElement, "protocol");
    /// <summary>The effective binding precedence, independent of endpoint availability or support.</summary>
    public MessageDefinitionBindingKind BindingKind => Protocol is not null
        ? MessageDefinitionBindingKind.ExplicitProtocol : Envelope is not null
            ? MessageDefinitionBindingKind.EnvelopeDefined : MessageDefinitionBindingKind.Unspecified;
    /// <summary>The envelope declarations; Undefined means absent.</summary>
    public JsonElement EnvelopeMetadata => Value(Metadata.RootElement, "envelopemetadata");
    /// <summary>The explicit protocol constraints; Undefined means absent.</summary>
    public JsonElement ProtocolOptions => Value(Metadata.RootElement, "protocoloptions");
    /// <summary>The payload schema format, without interpreting or acquiring the schema.</summary>
    public string? DataSchemaFormat => Text(Metadata.RootElement, "dataschemaformat");
    /// <summary>The opaque inline schema; Undefined means absent.</summary>
    public JsonElement DataSchema => Value(Metadata.RootElement, "dataschema");
    /// <summary>The authored or inherited schema reference. Its source context is retained by the materialization result.</summary>
    public string? DataSchemaUri => Text(Metadata.RootElement, "dataschemauri");
    /// <summary>The content type of the Message's outer representation.</summary>
    public string? RepresentationContentType => Text(Metadata.RootElement, "datacontenttype") ??
        ProtocolContentType() ?? (StructuredCloudEvent
            ? Text(Value(Metadata.RootElement, "envelopeoptions"), "format") : EnvelopeContentType);
    /// <summary>The nested payload content type for structured CloudEvents; otherwise the Message representation type.</summary>
    public string? PayloadContentType
    {
        get
        {
            return StructuredCloudEvent ? EnvelopeContentType : RepresentationContentType ?? EnvelopeContentType;
        }
    }

    private bool StructuredCloudEvent => string.Equals(Envelope, "CloudEvents/1.0", StringComparison.OrdinalIgnoreCase) &&
        Text(Value(Metadata.RootElement, "envelopeoptions"), "mode") == "structured";

    private string? EnvelopeContentType => Text(Value(EnvelopeMetadata, "datacontenttype"), "value");

    private string? ProtocolContentType()
    {
        var options = ProtocolOptions;
        switch (Protocol?.ToUpperInvariant())
        {
            case "HTTP":
            case "NATS":
                var headers = Value(options, "headers");
                if (headers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var header in headers.EnumerateArray())
                    {
                        if (string.Equals(Text(header, "name"), "Content-Type", StringComparison.OrdinalIgnoreCase) &&
                            Text(header, "value") is { } contentType)
                        {
                            return contentType;
                        }
                    }
                }
                break;
            case "AMQP/1.0":
                return Text(Value(Value(options, "properties"), "content-type"), "value");
            case "MQTT/5.0":
                return Text(options, "content_type");
            case "KAFKA":
                var kafka = Value(options, "headers");
                if (kafka.ValueKind == JsonValueKind.Object)
                {
                    foreach (var header in kafka.EnumerateObject())
                    {
                        if (string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase) &&
                            Text(header.Value, "value") is { } contentType)
                        {
                            return contentType;
                        }
                    }
                }
                break;
        }
        return null;
    }

    private static JsonElement Value(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) ? value : default;

    private static string? Text(JsonElement owner, string name)
    {
        var value = Value(owner, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
