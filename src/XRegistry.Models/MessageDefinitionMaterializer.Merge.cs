using System.Globalization;
using System.Text.Json;

namespace XRegistry.Models;

public static partial class MessageDefinitionMaterializer
{
    private sealed class MergeNode(JsonElement value, MessageDefinition source)
    {
        internal JsonElement Value { get; } = value;
        internal MessageDefinition Source { get; set; } = source;
        internal Dictionary<string, MergeNode>? Members { get; set; }
    }

    private sealed partial class Operation
    {
        internal Dictionary<string, MessageDefinition> PropertySources { get; } = new(StringComparer.Ordinal);

        internal MergeNode Merge(MergeNode baseline, JsonElement overlay, MessageDefinition source)
        {
            Work();
            if (baseline.Value.ValueKind != JsonValueKind.Object || overlay.ValueKind != JsonValueKind.Object)
            {
                return new(overlay, source);
            }
            if (baseline.Members is null)
            {
                baseline.Members = new(StringComparer.Ordinal);
                foreach (var property in baseline.Value.EnumerateObject())
                {
                    Work();
                    baseline.Members.Add(property.Name, new(property.Value, baseline.Source));
                }
            }
            foreach (var property in overlay.EnumerateObject())
            {
                Work();
                baseline.Members[property.Name] = baseline.Members.TryGetValue(property.Name, out var previous)
                    ? Merge(previous, property.Value, source) : new(property.Value, source);
            }
            baseline.Source = source;
            return baseline;
        }

        internal void Write(Utf8JsonWriter writer, MergeNode node, string path = "")
        {
            if (node.Members is null)
            {
                WriteValue(writer, node.Value, node.Source, path);
                return;
            }
            Work();
            PropertySources[path] = node.Source;
            writer.WriteStartObject();
            foreach (var property in node.Members)
            {
                writer.WritePropertyName(property.Key);
                Write(writer, property.Value, At(path, property.Key));
            }
            writer.WriteEndObject();
        }

        private void WriteValue(Utf8JsonWriter writer, JsonElement value, MessageDefinition source, string path)
        {
            Work();
            PropertySources[path] = source;
            if (value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteValue(writer, property.Value, source, At(path, property.Name));
                }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    WriteValue(writer, item, source, At(path, index.ToString(CultureInfo.InvariantCulture)));
                    index++;
                }
                writer.WriteEndArray();
            }
            else
            {
                value.WriteTo(writer);
            }
        }

        internal void CompleteSources(JsonElement value, string path, MessageDefinition inheritedSource)
        {
            Work();
            if (!PropertySources.TryGetValue(path, out var source))
            {
                source = inheritedSource;
                PropertySources.Add(path, source);
            }
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                {
                    CompleteSources(property.Value, At(path, property.Name), source);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    CompleteSources(item, At(path, index.ToString(CultureInfo.InvariantCulture)), source);
                    index++;
                }
            }
        }
    }
}
