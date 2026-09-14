using System.Text.Json;

namespace XRegistry.Models;

public static partial class RegistryDomainRules
{
    private const string MessageModelUri = "https://xregistry.io/xreg/domains/message/specs/model.json";
    private const string EndpointModelUri = "https://xregistry.io/xreg/domains/endpoint/specs/model.json";

    /// <summary>Identifies explicitly opted-in Message resources inside Message or Endpoint Groups.</summary>
    public static bool HasMessageGroupContract(RegistryGroupDefinition group, RegistryResourceDefinition resource)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(resource);
        return group.Annotations.ModelCompatibleWith is MessageModelUri or EndpointModelUri &&
            resource.Annotations.ModelCompatibleWith == MessageModelUri;
    }

    /// <summary>Checks owning-group selectors against an embedded or projected Message without changing either value.</summary>
    public static void ValidateMessageGroupConstraints(JsonElement message, RegistryGroupDefinition group,
        RegistryResourceDefinition resource, JsonElement groupMetadata, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasMessageGroupContract(group, resource))
        {
            return;
        }
        RequireMetadataObject(message);
        RequireMetadataObject(groupMetadata);
        if (OptionalText(groupMetadata, "protocol", "/protocol") is { } protocol)
        {
            var selected = OptionalText(message, "protocol", "/protocol");
            // An unbound Message can use the Group's protocol without materializing its own selector.
            if (selected is not null && !string.Equals(CanonicalProtocol(protocol), CanonicalProtocol(selected), StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("The Message protocol must agree with its owning Group.", "/protocol");
            }
        }
        if (OptionalText(groupMetadata, "envelope", "/envelope") is { } envelope)
        {
            var selected = OptionalText(message, "envelope", "/envelope");
            var matches = selected is not null && (group.Annotations.ModelCompatibleWith == EndpointModelUri
                ? RefinesEnvelope(envelope, selected)
                : string.Equals(envelope, selected, StringComparison.OrdinalIgnoreCase));
            if (!matches)
            {
                throw Invalid("The Message envelope must satisfy its owning Group's envelope constraint.", "/envelope");
            }
        }
    }

    private static string CanonicalProtocol(string protocol) => protocol.ToUpperInvariant() switch
    {
        "AMQP" => "AMQP/1.0",
        "MQTT" => "MQTT/5.0",
        _ => protocol
    };

    private static bool RefinesEnvelope(string parent, string child)
    {
        var parentSeparator = parent.IndexOf('/', StringComparison.Ordinal);
        var childSeparator = child.IndexOf('/', StringComparison.Ordinal);
        var parentName = parentSeparator < 0 ? parent : parent[..parentSeparator];
        var childName = childSeparator < 0 ? child : child[..childSeparator];
        if (!string.Equals(parentName, childName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (parentSeparator < 0)
        {
            return true;
        }
        if (childSeparator < 0)
        {
            return false;
        }
        var parentVersion = parent[(parentSeparator + 1)..];
        var childVersion = child[(childSeparator + 1)..];
        return string.Equals(parentVersion, childVersion, StringComparison.OrdinalIgnoreCase) ||
            childVersion.StartsWith(parentVersion + ".", StringComparison.OrdinalIgnoreCase);
    }
}
