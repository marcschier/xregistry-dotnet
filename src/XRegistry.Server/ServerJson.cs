using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Server;

internal static class ServerJson
{
    internal static JsonObject Object(RegistryJson value) => JsonNode.Parse(value.RootElement.GetRawText()) as JsonObject
        ?? throw ServerErrors.Create("bad_request", "", "The metadata body must be an object.");

    internal static RegistryJson Own(JsonNode node, RegistryJsonLimits limits) => RegistryJson.Parse(Encode(node, limits.MaxBytes), limits);

    internal static byte[] Encode(JsonNode node, int limit)
    {
        using var stream = new LimitedMemoryStream(limit);
        using (var writer = new Utf8JsonWriter(stream))
        {
            node.WriteTo(writer);
        }

        return stream.ToArray();
    }

    internal static JsonNode Integer(BigInteger value) => JsonNode.Parse(value.ToString(CultureInfo.InvariantCulture))!;

    internal static BigInteger Unsigned(JsonNode value, string path)
    {
        var element = RegistryJson.Parse(value.ToJsonString()).RootElement;
        if (element.ValueKind != JsonValueKind.Number)
        {
            throw ServerErrors.Create("invalid_attribute", path, "An unsigned integer is required.");
        }

        var number = RegistryNumber.FromElement(element);
        if (!number.IsInteger || number.Significand.Sign < 0)
        {
            throw ServerErrors.Create("invalid_attribute", path, "An unsigned integer is required.");
        }

        return number.ToBigInteger();
    }

    internal static string? Text(JsonObject obj, string name) => obj[name]?.GetValue<string>();
    internal static bool Boolean(JsonObject obj, string name) => obj[name]?.GetValue<bool>() ?? false;
    internal static int CompareTimestamps(string first, string second)
    {
        var comparison = string.CompareOrdinal(first[..19], second[..19]);
        return comparison != 0 ? comparison : string.CompareOrdinal(
            first[19..^1].TrimEnd('0').TrimEnd('.'), second[19..^1].TrimEnd('0').TrimEnd('.'));
    }
    internal static bool SameTimestamp(JsonNode? left, JsonNode? right)
    {
        if (left is not JsonValue a || right is not JsonValue b ||
            !a.TryGetValue<string>(out var first) || !b.TryGetValue<string>(out var second) ||
            first.Length < 20 || second.Length < 20)
        {
            return JsonNode.DeepEquals(left, right);
        }

        return first[..19] == second[..19] && first[19..^1].TrimEnd('0').TrimEnd('.') == second[19..^1].TrimEnd('0').TrimEnd('.');
    }
    internal static bool Equal(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.GetValueKind() == JsonValueKind.Number && right.GetValueKind() == JsonValueKind.Number)
        {
            return RegistryNumber.Parse(left.ToJsonString()).Equals(RegistryNumber.Parse(right.ToJsonString()));
        }

        if (left is JsonObject a && right is JsonObject b)
        {
            return a.Count == b.Count && a.All(property => b.ContainsKey(property.Key) && Equal(property.Value, b[property.Key]));
        }

        if (left is JsonArray aa && right is JsonArray ba)
        {
            return aa.Count == ba.Count && aa.Select((value, index) => Equal(value, ba[index])).All(static equal => equal);
        }

        return JsonNode.DeepEquals(left, right);
    }
    internal static string Timestamp(TimeProvider timeProvider) =>
        timeProvider.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    internal static string Key(RegistryPath path)
    {
        var result = path.GroupType is null ? path.EscapedPath : "/" + path.GroupType;
        if (path.GroupId is not null)
        {
            result += "/" + Uri.EscapeDataString(path.GroupId.Value);
        }

        if (path.ResourceType is not null)
        {
            result += "/" + path.ResourceType;
        }

        if (path.ResourceId is not null)
        {
            result += "/" + Uri.EscapeDataString(path.ResourceId.Value);
        }

        if (path.Kind == RegistryPathKind.Meta)
        {
            result += "/meta";
        }
        else if (path.Kind is RegistryPathKind.VersionCollection or RegistryPathKind.Version)
        {
            result += "/versions";
            if (path.VersionId is not null)
            {
                result += "/" + Uri.EscapeDataString(path.VersionId.Value);
            }
        }

        return result;
    }

    private sealed class LimitedMemoryStream(int limit) : MemoryStream
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
                throw ServerErrors.Create("too_large", "", "The prepared JSON exceeds its byte budget.");
            }
        }
    }
}
