// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace XRegistry.Federation.Tests;

internal static class MappingUriFixture
{
    private static readonly string[] VersionFields = ["versionid", "ancestorid", "defaultversionid"];
    private static readonly string[] ReferenceFields = ["collections", "entries"];

    internal static Dictionary<string, byte[]> Load(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(root, path).Replace('\\', '/'), System.IO.File.ReadAllBytes, StringComparer.Ordinal);

    internal static Dictionary<string, byte[]> Renamed(string root, bool escaped) =>
        Rewrite(Load(root), node =>
        {
            if (node["entity"] is JsonObject entity)
            {
                var original = entity["xid"]!.GetValue<string>();
                entity["xid"] = Map(original, escaped);
                if (entity["xref"] is JsonNode xref) { entity["xref"] = Map(xref.GetValue<string>(), escaped); }
                if (original == "/documents/main") { entity["documentid"] = "group:one"; }
                if (original.StartsWith("/documents/main/assets/item", StringComparison.Ordinal) &&
                    entity["assetid"]?.GetValue<string>() == "item")
                {
                    entity["assetid"] = "item@stable";
                }
                foreach (var name in VersionFields)
                {
                    if (entity[name] is JsonNode value)
                    {
                        entity[name] = value.GetValue<string>().Replace("v1", "v:1", StringComparison.Ordinal)
                            .Replace("v2", "v:2", StringComparison.Ordinal);
                    }
                }
            }
            else { node["xid"] = Map(node["xid"]!.GetValue<string>(), escaped); }
            foreach (var name in ReferenceFields)
            {
                if (node[name] is not JsonArray values) { continue; }
                foreach (var reference in values)
                {
                    reference!["xid"] = Map(reference["xid"]!.GetValue<string>(), false);
                }
                node[name] = new JsonArray(values.OrderBy(value => value!["xid"]!.GetValue<string>(), StringComparer.Ordinal)
                    .Select(value => value!.DeepClone()).ToArray());
            }
            if (node["meta"] is JsonNode meta) { meta["xid"] = Map(meta["xid"]!.GetValue<string>(), false); }
        });

    internal static Dictionary<string, byte[]> Rewrite(IReadOnlyDictionary<string, byte[]> original, Action<JsonObject> transform)
    {
        var result = original.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Visit("registry.json");
        return result;

        void Visit(string path)
        {
            if (!visited.Add(path)) { return; }
            var node = JsonNode.Parse(original[path])!.AsObject();
            foreach (var name in ReferenceFields)
            {
                if (node[name] is JsonArray references)
                {
                    foreach (var reference in references) { Update(reference!.AsObject()); }
                }
            }
            if (node["meta"] is JsonObject meta) { Update(meta); }
            transform(node);
            result[path] = Encoding.UTF8.GetBytes(node.ToJsonString());
        }

        void Update(JsonObject reference)
        {
            var path = reference["href"]!.GetValue<string>();
            Visit(path);
            reference["size"] = result[path].LongLength;
            reference["sha256"] = Convert.ToHexString(SHA256.HashData(result[path])).ToLowerInvariant();
        }
    }

    private static string Map(string value, bool escaped)
    {
        value = value.Replace("/documents/main", "/documents/group:one", StringComparison.Ordinal)
            .Replace("/documents/group:one/assets/item", "/documents/group:one/assets/item@stable", StringComparison.Ordinal)
            .Replace("/versions/v1", "/versions/v:1", StringComparison.Ordinal)
            .Replace("/versions/v2", "/versions/v:2", StringComparison.Ordinal);
        if (!escaped || value == "/") { return value; }
        return "/" + string.Join('/', value[1..].Split('/').Select((part, index) =>
            index is 0 or 2 or 4 ? "%" + ((int)part[0]).ToString("x2", System.Globalization.CultureInfo.InvariantCulture) +
                Uri.EscapeDataString(part[1..]) : Uri.EscapeDataString(part)));
    }
}
