using System.Globalization;
using System.Text.Json;

namespace XRegistry;

internal static class ModelWriter
{
    internal static void Write(Utf8JsonWriter writer, RegistryModel model)
    {
        writer.WriteStartObject();
        WriteAnnotations(writer, model.Annotations);
        WriteAttributes(writer, "attributes", model.Attributes);
        writer.WriteStartObject("groups");
        foreach (var group in model.Groups.OrderBy(static entry => entry.Key, StringComparer.Ordinal).Select(static entry => entry.Value))
        {
            writer.WriteStartObject(group.Plural);
            writer.WriteString("plural", group.Plural);
            writer.WriteString("singular", group.Singular);
            WriteAnnotations(writer, group.Annotations);
            WriteAttributes(writer, "attributes", group.Attributes);
            WriteConstraints(writer, group.Constraints);
            writer.WriteStartObject("resources");
            foreach (var resource in group.Resources.OrderBy(static entry => entry.Key, StringComparer.Ordinal).Select(static entry => entry.Value))
            {
                writer.WriteStartObject(resource.Plural);
                writer.WriteString("plural", resource.Plural);
                writer.WriteString("singular", resource.Singular);
                WriteAnnotations(writer, resource.Annotations);
                writer.WritePropertyName("maxversions");
                writer.WriteRawValue(resource.MaxVersions.ToString(CultureInfo.InvariantCulture));
                writer.WriteBoolean("setversionid", resource.SetVersionId);
                writer.WriteBoolean("hasdocument", resource.HasDocument);
                writer.WriteString("versionmode", resource.VersionMode);
                writer.WriteBoolean("singleversionroot", resource.SingleVersionRoot);
                writer.WriteBoolean("validateformat", resource.ValidateFormat);
                writer.WriteBoolean("validatecompatibility", resource.ValidateCompatibility);
                writer.WriteBoolean("strictvalidation", resource.StrictValidation);
                writer.WriteStartObject("typemap");
                foreach (var mapping in resource.TypeMap.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
                {
                    writer.WriteString(mapping.Key, mapping.Value switch
                    {
                        RegistryDocumentFormat.Json => "json",
                        RegistryDocumentFormat.String => "string",
                        _ => "binary"
                    });
                }

                writer.WriteEndObject();
                WriteAttributes(writer, "attributes", resource.Attributes);
                WriteAttributes(writer, "resourceattributes", resource.ResourceAttributes);
                WriteAttributes(writer, "metaattributes", resource.MetaAttributes);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteAnnotations(Utf8JsonWriter writer, RegistryModelAnnotations annotations)
    {
        OptionalString(writer, "description", annotations.Description);
        OptionalString(writer, "documentation", annotations.Documentation);
        OptionalString(writer, "icon", annotations.Icon);
        OptionalString(writer, "modelversion", annotations.ModelVersion);
        OptionalString(writer, "modelcompatiblewith", annotations.ModelCompatibleWith);
        if (annotations.Labels.Count > 0)
        {
            writer.WriteStartObject("labels");
            foreach (var label in annotations.Labels.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                writer.WriteString(label.Key, label.Value);
            }

            writer.WriteEndObject();
        }
    }

    private static void WriteConstraints(Utf8JsonWriter writer, IReadOnlyDictionary<string, RegistryConstraint> constraints)
    {
        if (constraints.Count == 0)
        {
            return;
        }

        writer.WriteStartObject("constraints");
        foreach (var entry in constraints.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            writer.WriteStartObject(entry.Key);
            WriteValue(writer, "default", entry.Value.DefaultValue);
            WriteEnum(writer, entry.Value.EnumValues);
            OptionalString(writer, "equals", entry.Value.EqualsAttribute);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteAttributes(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, RegistryAttributeDefinition> attributes)
    {
        writer.WriteStartObject(name);
        foreach (var attribute in attributes.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(attribute.Key);
            WriteAttribute(writer, attribute.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteAttribute(Utf8JsonWriter writer, RegistryAttributeDefinition attribute)
    {
        writer.WriteStartObject();
        if (attribute.Name.Length > 0)
        {
            writer.WriteString("name", attribute.Name);
        }

        writer.WriteString("type", attribute.TypeName);
        OptionalString(writer, "target", attribute.Target);
        if (attribute.Type == RegistryValueType.Object)
        {
            writer.WriteString("namecharset", attribute.NameCharset);
            WriteAttributes(writer, "attributes", attribute.Attributes);
        }

        if (attribute.Item is not null)
        {
            writer.WritePropertyName("item");
            WriteAttribute(writer, attribute.Item);
        }

        if (attribute.Name.Length > 0)
        {
            OptionalString(writer, "description", attribute.Description);
            writer.WriteBoolean("strict", attribute.Strict);
            writer.WriteBoolean("required", attribute.Required);
            writer.WriteBoolean("readonly", attribute.ReadOnly);
            writer.WriteBoolean("immutable", attribute.Immutable);
            writer.WriteBoolean("matchversions", attribute.MatchVersions);
            WriteEnum(writer, attribute.EnumValues);
            WriteValue(writer, "default", attribute.DefaultValue);
            if (attribute.IfValues.Count > 0)
            {
                writer.WriteStartObject("ifvalues");
                foreach (var branch in attribute.IfValues.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartObject(branch.Key);
                    WriteAttributes(writer, "siblingattributes", branch.Value);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteEnum(Utf8JsonWriter writer, IReadOnlyList<JsonElement> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        writer.WriteStartArray("enum");
        foreach (var value in values)
        {
            value.WriteTo(writer);
        }

        writer.WriteEndArray();
    }

    private static void WriteValue(Utf8JsonWriter writer, string name, JsonElement value)
    {
        if (ModelCompiler.HasValue(value))
        {
            writer.WritePropertyName(name);
            value.WriteTo(writer);
        }
    }

    private static void OptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}
