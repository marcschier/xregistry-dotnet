// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

internal static class GraphFixture
{
    internal const string Index = "application/vnd.oci.image.index.v1+json";
    internal const string Manifest = "application/vnd.oci.image.manifest.v1+json";
    internal const string Config = "application/vnd.xregistry.entity.v1+json";
    internal const string Prefix = "io.xregistry.oci.";
    internal const string RegistryConfig = "bbb8431c731d4be5db0dda92683801758a2dc25a2fc7e9908630eccdcd4d5adf";
    internal const string Binary = "47a8404dd5bb287e70354f4aa0f7bf250e4fefe66a72cf2ac5475a802fd45968";

    internal static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static byte[] Encode(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    internal static void Node(MemoryLayout tree, string kind, string xid, Action<JsonObject> mutate, string mediaType = Index) =>
        Rewrite(tree, (node, media) =>
        {
            if (media == mediaType && node["annotations"]?[Prefix + "kind"]?.GetValue<string>() == kind &&
                node["annotations"]?[Prefix + "xid"]?.GetValue<string>() == xid)
            {
                mutate(node);
            }
            return Encode(node);
        });

    internal static void Record(MemoryLayout tree, string xid, Action<JsonObject> mutate) =>
        Rewrite(tree, (node, media) =>
        {
            if (media == Config && node["entity"]?["xid"]?.GetValue<string>() == xid) { mutate(node); }
            return Encode(node);
        });

    internal static void Rewrite(MemoryLayout tree, Func<JsonObject, string, byte[]> transform, string reference = "offline")
    {
        var index = JsonNode.Parse(tree.Files["index.json"])!.AsObject();
        var root = index["manifests"]!.AsArray().Single(e =>
            e!["annotations"]!["org.opencontainers.image.ref.name"]!.GetValue<string>() == reference)!.AsObject();
        var done = new Dictionary<(string, string), (string Digest, long Size)>();
        Update(root);
        tree.Files["index.json"] = Encode(index);

        void Update(JsonObject descriptor)
        {
            var digest = descriptor["digest"]!.GetValue<string>();
            var media = descriptor["mediaType"]!.GetValue<string>();
            if (media is not (Index or Manifest or Config)) { return; }
            if (!done.TryGetValue((digest, media), out var updated))
            {
                var node = JsonNode.Parse(tree.Files["blobs/sha256/" + digest[7..]])!.AsObject();
                if (media == Index)
                {
                    foreach (var edge in node["manifests"]!.AsArray()) { Update(edge!.AsObject()); }
                }
                if (media == Manifest)
                {
                    Update(node["config"]!.AsObject());
                    foreach (var edge in node["layers"]!.AsArray()) { Update(edge!.AsObject()); }
                }
                var bytes = transform(node, media);
                updated = (Hash(bytes), bytes.LongLength);
                tree.Files["blobs/sha256/" + updated.Digest[7..]] = bytes;
                done.Add((digest, media), updated);
            }
            descriptor["digest"] = updated.Digest;
            descriptor["size"] = updated.Size;
        }
    }

    internal static JsonObject Descriptor(string xid, string role = "entity") => new()
    {
        ["mediaType"] = Index,
        ["digest"] = OciBootstrapTests.Offline,
        ["size"] = 903,
        ["annotations"] = new JsonObject { [Prefix + "role"] = role, [Prefix + "xid"] = xid },
    };

    internal static void RepointRoot(MemoryLayout tree, byte[] bytes)
    {
        var digest = Hash(bytes);
        tree.Files["blobs/sha256/" + digest[7..]] = bytes;
        var index = JsonNode.Parse(tree.Files["index.json"])!;
        index["manifests"]![0]!["digest"] = digest;
        index["manifests"]![0]!["size"] = bytes.LongLength;
        tree.Files["index.json"] = Encode(index);
    }

    internal static (List<RegistryJson> Records, Dictionary<string, FederationDocument> Documents) Capture(
        MemoryLayout tree, string reference = "offline")
    {
        using var entrypoint = JsonDocument.Parse(tree.Files["index.json"]);
        var root = entrypoint.RootElement.GetProperty("manifests").EnumerateArray().Single(e =>
            e.GetProperty("annotations").GetProperty("org.opencontainers.image.ref.name").GetString() == reference);
        var records = new List<RegistryJson>();
        var documents = new Dictionary<string, FederationDocument>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Visit(root);
        return (records, documents);

        void Visit(JsonElement descriptor)
        {
            var digest = descriptor.GetProperty("digest").GetString()!;
            if (!visited.Add(digest)) { return; }
            var media = descriptor.GetProperty("mediaType").GetString();
            if (media is not (Index or Manifest)) { return; }
            using var parsed = JsonDocument.Parse(tree.Files["blobs/sha256/" + digest[7..]]);
            var node = parsed.RootElement;
            if (media == Index)
            {
                foreach (var edge in node.GetProperty("manifests").EnumerateArray()) { Visit(edge); }
                return;
            }
            var config = node.GetProperty("config").GetProperty("digest").GetString()!;
            var record = RegistryJson.Parse(tree.Files["blobs/sha256/" + config[7..]]);
            records.Add(record);
            if (record.RootElement.TryGetProperty("document", out var document) &&
                document.GetProperty("mode").GetString() == "embedded")
            {
                var entity = record.RootElement.GetProperty("entity");
                var payload = node.GetProperty("layers")[0].GetProperty("digest").GetString()!;
                documents.Add(entity.GetProperty("xid").GetString()!,
                    new FederationDocument(tree.Files["blobs/sha256/" + payload[7..]]));
            }
        }
    }
}
