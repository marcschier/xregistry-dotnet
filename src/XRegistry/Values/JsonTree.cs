using System.Text.Json;

namespace XRegistry;

internal sealed class JsonTree
{
    internal JsonTree(JsonElement scalar) => Scalar = scalar;
    internal JsonTree(Dictionary<string, JsonTree> members) => Members = members;
    internal JsonTree(List<JsonTree> items) => Items = items;
    internal JsonElement Scalar { get; }
    internal Dictionary<string, JsonTree>? Members { get; }
    internal List<JsonTree>? Items { get; }
    internal JsonValueKind Kind => Members is not null ? JsonValueKind.Object :
        Items is not null ? JsonValueKind.Array : Scalar.ValueKind;

    internal static JsonTree FromElement(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => new JsonTree(value.EnumerateObject()
            .ToDictionary(static property => property.Name, static property => FromElement(property.Value), StringComparer.Ordinal)),
        JsonValueKind.Array => new JsonTree(value.EnumerateArray().Select(FromElement).ToList()),
        _ => new JsonTree(value)
    };

    internal RegistryJson ToJson(RegistryJsonLimits limits) => RegistryJson.Create(Write, limits);

    internal void Write(Utf8JsonWriter writer)
    {
        if (Members is not null)
        {
            writer.WriteStartObject();
            foreach (var property in Members)
            {
                writer.WritePropertyName(property.Key);
                property.Value.Write(writer);
            }

            writer.WriteEndObject();
        }
        else if (Items is not null)
        {
            writer.WriteStartArray();
            foreach (var item in Items)
            {
                item.Write(writer);
            }

            writer.WriteEndArray();
        }
        else
        {
            Scalar.WriteTo(writer);
        }
    }
}
