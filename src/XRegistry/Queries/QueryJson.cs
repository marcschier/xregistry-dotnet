using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Queries;

internal static class QueryJson
{
    internal static JsonObject Object(RegistryJson value) =>
        JsonNode.Parse(value.RootElement.GetRawText(), documentOptions: new() { MaxDepth = 256 }) as JsonObject
        ?? throw Diagnostics.Error("invalid_query_source", "", "The source entity must be a metadata object.");

    internal static RegistryJson Own(JsonNode value, RegistryJsonLimits limits) =>
        RegistryJson.Parse(Encode(value, limits.MaxBytes), limits);

    internal static byte[] Encode(JsonNode value, int limit)
    {
        using var stream = new LimitedStream(limit);
        using (var writer = new Utf8JsonWriter(stream, new() { MaxDepth = 256 }))
        {
            value.WriteTo(writer);
        }

        return stream.ToArray();
    }

    internal static string Key(RegistryPath path)
    {
        var key = path.GroupType is null ? path.Kind == RegistryPathKind.Export ? "/" : path.EscapedPath : "/" + path.GroupType;
        if (path.GroupId is not null)
        {
            key += "/" + Uri.EscapeDataString(path.GroupId.Value);
        }

        if (path.ResourceType is not null)
        {
            key += "/" + path.ResourceType;
        }

        if (path.ResourceId is not null)
        {
            key += "/" + Uri.EscapeDataString(path.ResourceId.Value);
        }

        if (path.Kind == RegistryPathKind.Meta)
        {
            key += "/meta";
        }
        else if (path.Kind is RegistryPathKind.VersionCollection or RegistryPathKind.Version)
        {
            key += "/versions";
            if (path.VersionId is not null)
            {
                key += "/" + Uri.EscapeDataString(path.VersionId.Value);
            }
        }

        return key;
    }

    internal static bool Under(string key, string parent) =>
        parent == "/" || key == parent || key.StartsWith(parent + "/", StringComparison.Ordinal);
    internal static bool IsCollection(RegistryPathKind kind) =>
        kind is RegistryPathKind.GroupCollection or RegistryPathKind.ResourceCollection or RegistryPathKind.VersionCollection;
    internal static string LeafId(string key) => Uri.UnescapeDataString(key[(key.LastIndexOf('/') + 1)..]);
    internal static string? Text(JsonObject value, string name) => value[name] is JsonValue scalar &&
        scalar.TryGetValue<string>(out var text) ? text : null;

    private sealed class LimitedStream(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        private void Check(int count)
        {
            if (count > limit - Length)
            {
                throw Diagnostics.Error("too_large", "", "The query fact exceeds its encoded byte budget.");
            }
        }
    }
}
