using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Models;

public static partial class MessageDefinitionMaterializer
{
    private static readonly Lazy<RegistryModel> DefinitionModel =
        new(static () => BuiltInRegistryModels.Compile(RegistryModelKind.Message));

    private sealed partial class Operation
    {
        internal RegistryJson Complete(RegistryJson metadata, MessageDefinition definition, List<RegistryValidationObligation> obligations)
        {
            Count(metadata.RootElement);
            var attributes = MessageResource(definition).Attributes;
            var declarations = CompleteDeclarations(metadata, attributes);
            var prepared = metadata;
            if (declarations is not null)
            {
                var raw = JsonNode.Parse(metadata.RootElement.GetRawText())!.AsObject();
                RestoreDeclarations(raw, declarations.RootElement);
                prepared = RegistryJson.Create(writer => raw.WriteTo(writer), options.JsonLimits);
            }
            var definitions = attributes.Where(pair => !pair.Value.IsSystemDefined ||
                metadata.RootElement.TryGetProperty(pair.Key, out _))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            var validated = RegistryMetadataValidator.Validate(prepared, definitions, new()
            {
                Mode = RegistryMetadataMode.CompleteEntity,
                Limits = options.JsonLimits
            }, cancellationToken);
            obligations.AddRange(validated.Obligations.Where(static obligation => obligation.Path != "/basemessage"));
            var normalized = JsonNode.Parse(validated.Metadata.RootElement.GetRawText())!.AsObject();
            if (declarations is not null)
            {
                RestoreDeclarations(normalized, declarations.RootElement);
            }
            foreach (var property in metadata.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    normalized[property.Name] = null;
                }
            }
            var input = RegistryJson.Create(writer => normalized.WriteTo(writer), options.JsonLimits);
            Count(input.RootElement);
            var completed = RegistryDomainRules.CompleteMessageMetadata(input, options.JsonLimits, cancellationToken);
            return InferEnvelope(completed, obligations);
        }
    }

    private static RegistryResourceDefinition MessageResource(MessageDefinition definition)
    {
        var model = definition.Model ?? DefinitionModel.Value;
        if (definition.Path is { } path)
        {
            if (path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version && !path.IsDetails &&
                path.VersionId?.Value is not ("null" or "request") &&
                model.Groups.TryGetValue(path.GroupType!, out var group) &&
                group.Resources.TryGetValue(path.ResourceType!, out var resource) &&
                resource.Annotations.ModelCompatibleWith == "https://xregistry.io/xreg/domains/message/specs/model.json")
            {
                return resource;
            }
            throw Error("invalid_attribute", "", "The supplied definition path must identify a Message Resource or Version.");
        }
        RegistryResourceDefinition? selected = null;
        foreach (var group in model.Groups.Values)
        {
            foreach (var resource in group.Resources.Values)
            {
                if (resource.Annotations.ModelCompatibleWith != "https://xregistry.io/xreg/domains/message/specs/model.json") { continue; }
                if (selected is not null && !ReferenceEquals(selected, resource))
                {
                    throw Error("message_context_required", "", "A definition path is required when the model contains distinct Message types.");
                }
                selected = resource;
            }
        }
        return selected ?? throw Error("message_context_required", "", "The supplied model has no declared Message Resource type.");
    }
}
