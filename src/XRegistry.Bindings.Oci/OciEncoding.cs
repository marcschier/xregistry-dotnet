// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

internal static class OciEncoding
{
    internal static byte[] Canonical(JsonNode node, int maxBytes)
    {
        using var stream = new BoundedStream(maxBytes);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = 256 }))
        {
            Write(node, writer);
        }
        return stream.ToArray();

        static void Write(JsonNode? node, Utf8JsonWriter writer)
        {
            if (node is JsonObject obj)
            {
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    Write(property.Value, writer);
                }
                writer.WriteEndObject();
            }
            else if (node is JsonArray array)
            {
                writer.WriteStartArray();
                foreach (var value in array) { Write(value, writer); }
                writer.WriteEndArray();
            }
            else if (node is JsonValue value && value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Number)
            {
                writer.WriteRawValue(RegistryNumber.FromElement(element).ToString());
            }
            else if (node is null) { writer.WriteNullValue(); }
            else { node.WriteTo(writer); }
        }
    }

    internal static byte[] Encode(JsonNode node, int maxBytes)
    {
        using var stream = new BoundedStream(maxBytes);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = 256 }))
        {
            node.WriteTo(writer);
        }
        return stream.ToArray();
    }

    internal static JsonElement Result(JsonNode node, FederationReadBudget budget, CancellationToken cancellationToken)
    {
        var bytes = Encode(node, budget.Limits.MaxResultBytes);
        budget.CheckResultBytes(bytes.Length);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var value = RegistryJson.Parse(bytes, OciJson.Limits(budget) with { MaxBytes = budget.Limits.MaxResultBytes }).RootElement;
            OciJson.Charge(value, budget, cancellationToken);
            return value;
        }
        catch (RegistryException exception) { throw OciJson.CoreError(exception); }
    }

    private sealed class BoundedStream(int limit) : MemoryStream
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

        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }

        private void Check(int count)
        {
            if (count > limit - Length)
            {
                throw new FederationException(FederationErrorCode.LimitExceeded, "Encoded OCI output exceeds its byte budget.");
            }
        }
    }
}
