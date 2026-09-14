using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed partial class Request
    {
        private RegistryMetadataValidationResult ValidateMetadata(RegistryJson input,
            IReadOnlyDictionary<string, RegistryAttributeDefinition> definitions,
            RegistryMetadataValidationOptions options, string subject, JsonObject? original = null)
        {
            try
            {
                return RegistryMetadataValidator.Validate(input, definitions, options, _ct);
            }
            catch (RegistryException exception)
            {
                if (exception.Diagnostic.Code == "invalid_attribute" &&
                    InvalidXid(input.RootElement, definitions, exception.Diagnostic.Path) is { } xid)
                {
                    throw ServerErrors.Wrap(exception, "malformed_xid", _engine.Url(ServerJson.Key(_operation.Path)), ("xid", xid));
                }

                throw MetadataError(exception, subject, options.Resource is not null,
                    original is not null && IsExplicitDeletion(input.RootElement, original, exception.Diagnostic.Path));
            }
        }

        private static bool IsExplicitDeletion(JsonElement current, JsonNode? previous, string pointer)
        {
            foreach (var escaped in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var part = escaped.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (current.ValueKind == JsonValueKind.Object && previous is JsonObject previousObject)
                {
                    if (!current.TryGetProperty(part, out current) || !previousObject.TryGetPropertyValue(part, out previous))
                    {
                        return false;
                    }
                }
                else if (current.ValueKind == JsonValueKind.Array && previous is JsonArray previousArray &&
                    int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) &&
                    index < current.GetArrayLength() && index < previousArray.Count)
                {
                    current = current[index];
                    previous = previousArray[index];
                }
                else
                {
                    return false;
                }
            }

            return current.ValueKind == JsonValueKind.Null && previous is not null;
        }

        private static string? InvalidXid(JsonElement value, IReadOnlyDictionary<string, RegistryAttributeDefinition> definitions, string pointer)
        {
            RegistryAttributeDefinition? definition = null;
            foreach (var escaped in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var part = escaped.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (definition is null || definition.Type == RegistryValueType.Object)
                {
                    definitions = definition?.Attributes ?? definitions;
                    definition = definitions.GetValueOrDefault(part) ?? definitions.GetValueOrDefault("*");
                }
                else if (definition.Type is RegistryValueType.Map or RegistryValueType.Array)
                {
                    definition = definition.Item;
                }
                else
                {
                    return null;
                }

                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(part, out var property))
                {
                    value = property;
                }
                else if (value.ValueKind == JsonValueKind.Array && int.TryParse(part,
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) &&
                    index < value.GetArrayLength())
                {
                    value = value[index];
                }
                else
                {
                    return null;
                }
            }

            if (definition?.Type != RegistryValueType.Xid || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = value.GetString()!;
            try
            {
                var path = RegistryPath.Parse(text);
                return !path.IsDetails && path.Kind is RegistryPathKind.Registry or RegistryPathKind.Group or
                    RegistryPathKind.Resource or RegistryPathKind.Meta or RegistryPathKind.Version ? null : text;
            }
            catch (RegistryException)
            {
                return text;
            }
        }

        private static RegistryException MetadataError(RegistryException exception, string subject, bool versionConstraints = false,
            bool explicitDeletion = false)
        {
            var pointer = exception.Diagnostic.Path;
            var name = string.Join('.', pointer.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(static part => part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)));
            var code = exception.Diagnostic.Code;
            if (!explicitDeletion && code == "invalid_attribute" && exception.Diagnostic.Message is
                "A required attribute has no value." or "The completed entity is missing a required value.")
            {
                code = "required_attribute_missing";
            }

            if (code == "constraint_failure" && versionConstraints)
            {
                var path = RegistryPath.Parse(subject);
                var kind = exception.Diagnostic.Message == "The value is not allowed by the owning Group constraint." ? "enum" : "equals";
                return ServerErrors.Wrap(exception, code, ResourceKey(path), ("kind", kind), ("path", name));
            }

            return code switch
            {
                "required_attribute_missing" => ServerErrors.Wrap(exception, code, subject, ("list", name)),
                "unknown_attribute" or "invalid_attribute" => ServerErrors.Wrap(exception, code, subject, ("name", name)),
                _ => ServerErrors.Wrap(exception, code, subject)
            };
        }

        private static string ErrorValue(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
            ? text : value?.ToJsonString() ?? "null";

        private RegistryId ParseId(string value)
        {
            try
            {
                return RegistryId.Parse(value);
            }
            catch (RegistryException exception)
            {
                throw ServerErrors.Wrap(exception, "malformed_id", _engine.Url(ServerJson.Key(_operation.Path)), ("id", value));
            }
        }
    }
}
