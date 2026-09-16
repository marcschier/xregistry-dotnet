// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Models;

public static partial class MessageDefinitionMaterializer
{
    private static readonly string[] AmqpDeclarationSections =
        ["properties", "application-properties", "message-annotations", "delivery-annotations", "footer"];

    private sealed partial class Operation
    {
        private RegistryJson? CompleteDeclarations(RegistryJson metadata,
            IReadOnlyDictionary<string, RegistryAttributeDefinition> attributes)
        {
            var root = metadata.RootElement;
            var projection = new JsonObject();
            if (EffectiveSelector(root, attributes, "envelope", "CloudEvents/1.0") &&
                root.TryGetProperty("envelopemetadata", out var envelope))
            {
                Count(envelope);
                projection["envelope"] = "CloudEvents/1.0";
                projection["envelopemetadata"] = JsonNode.Parse(envelope.GetRawText());
            }
            if (EffectiveSelector(root, attributes, "protocol", "AMQP/1.0") &&
                root.TryGetProperty("protocoloptions", out var protocolOptions) && protocolOptions.ValueKind == JsonValueKind.Object)
            {
                var protocol = new JsonObject();
                foreach (var name in AmqpDeclarationSections)
                {
                    if (protocolOptions.TryGetProperty(name, out var section))
                    {
                        Count(section);
                        protocol[name] = JsonNode.Parse(section.GetRawText());
                    }
                }
                if (protocol.Count != 0)
                {
                    projection["protocol"] = "AMQP/1.0";
                    projection["protocoloptions"] = protocol;
                }
            }
            return projection.Count == 0 ? null : RegistryDomainRules.CompleteMessageMetadata(
                RegistryJson.Create(writer => projection.WriteTo(writer), options.JsonLimits),
                options.JsonLimits, cancellationToken);
        }

        private void RestoreDeclarations(JsonObject normalized, JsonElement declarations)
        {
            if (Selected(declarations, "envelope", "CloudEvents/1.0") &&
                declarations.TryGetProperty("envelopemetadata", out var envelope))
            {
                Count(envelope);
                normalized["envelopemetadata"] = JsonNode.Parse(envelope.GetRawText());
            }
            if (!Selected(declarations, "protocol", "AMQP/1.0") ||
                !declarations.TryGetProperty("protocoloptions", out var protocolOptions) ||
                protocolOptions.ValueKind != JsonValueKind.Object ||
                normalized["protocoloptions"] is not JsonObject protocol)
            {
                return;
            }
            foreach (var name in AmqpDeclarationSections)
            {
                if (protocolOptions.TryGetProperty(name, out var section))
                {
                    Count(section);
                    protocol[name] = JsonNode.Parse(section.GetRawText());
                }
            }
        }

        private static bool Selected(JsonElement metadata, string name, string expected) =>
            metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase);

        private static bool EffectiveSelector(JsonElement metadata,
            IReadOnlyDictionary<string, RegistryAttributeDefinition> attributes, string name, string expected)
        {
            var present = metadata.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;
            if (!present && attributes.TryGetValue(name, out var definition))
            {
                value = definition.DefaultValue;
            }
            return value.ValueKind == JsonValueKind.String &&
                string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase);
        }
    }
}
