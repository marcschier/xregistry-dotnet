// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Models;

public static partial class MessageDefinitionMaterializer
{
    private sealed partial class Operation
    {
        private RegistryJson InferEnvelope(RegistryJson metadata, List<RegistryValidationObligation> obligations)
        {
            var root = metadata.RootElement;
            if (!root.TryGetProperty("envelope", out var selector) || selector.ValueKind != JsonValueKind.String ||
                !string.Equals(selector.GetString(), "CloudEvents/1.0", StringComparison.OrdinalIgnoreCase))
            {
                return metadata;
            }
            var result = JsonNode.Parse(root.GetRawText())!.AsObject();
            var envelope = result["envelopemetadata"]!.AsObject();
            var envelopeSource = PropertySources["/envelope"];
            foreach (var name in new[] { "specversion", "id", "type", "source", "time" })
            {
                Work();
                if (!envelope.ContainsKey(name))
                {
                    envelope[name] = new JsonObject();
                    PropertySources[At("/envelopemetadata", name)] = envelopeSource;
                }
            }
            if (!HasValue(envelope, "dataschema"))
            {
                if (root.TryGetProperty("dataschemauri", out var schemaUri) && schemaUri.ValueKind == JsonValueKind.String)
                {
                    SetInferred(envelope, "dataschema", schemaUri.GetString()!, PropertySources["/dataschemauri"]);
                }
                else if (root.TryGetProperty("dataschema", out var schema) && schema.ValueKind != JsonValueKind.Null)
                {
                    obligations.Add(new("message_schema_uri", "/envelopemetadata/dataschema/value",
                        "An inline schema has no independently established URI; the materializer does not invent one."));
                }
            }
            if (!HasValue(envelope, "datacontenttype") &&
                root.TryGetProperty("dataschemaformat", out var format) && format.ValueKind == JsonValueKind.String)
            {
                Work();
                var name = format.GetString()!;
                var inferred = options.PayloadContentTypeResolver?.Invoke(name);
                cancellationToken.ThrowIfCancellationRequested();
                if (inferred is null && name.StartsWith("JSONSchema/", StringComparison.OrdinalIgnoreCase))
                {
                    inferred = "application/json";
                }
                if (inferred is null)
                {
                    obligations.Add(new("message_payload_content_type", "/envelopemetadata/datacontenttype/value",
                        "This schema format needs a caller-defined payload media-type mapping."));
                }
                else
                {
                    SetInferred(envelope, "datacontenttype", inferred, PropertySources["/dataschemaformat"]);
                }
            }
            var inferredMetadata = RegistryJson.Create(writer => result.WriteTo(writer), options.JsonLimits);
            Count(inferredMetadata.RootElement);
            return RegistryDomainRules.CompleteMessageMetadata(inferredMetadata, options.JsonLimits, cancellationToken);
        }

        private static bool HasValue(JsonObject envelope, string name) =>
            envelope[name] is JsonObject declaration && declaration.ContainsKey("value");

        private void SetInferred(JsonObject envelope, string name, string value, MessageDefinition source)
        {
            Work();
            if (envelope[name] is not JsonObject declaration)
            {
                declaration = new JsonObject();
                envelope[name] = declaration;
                PropertySources[At("/envelopemetadata", name)] = source;
            }
            declaration["value"] = value;
            PropertySources[At(At("/envelopemetadata", name), "value")] = source;
        }
    }
}
