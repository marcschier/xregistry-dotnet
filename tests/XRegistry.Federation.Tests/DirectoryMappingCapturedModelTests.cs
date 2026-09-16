// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class DirectoryMappingCapturedModelTests
{
    [Test]
    [Arguments("""{"$include":7}""")]
    [Arguments("""{"$includes":"https://never-fetch.invalid/model"}""")]
    [Arguments("""{"$include":"https://never-fetch.invalid/model","$includes":[]}""")]
    [Arguments("""{"$include":"https://never-fetch.invalid/model#not-a-pointer"}""")]
    [Arguments("""{"$include":"https://never-fetch.invalid/model#/%ff"}""")]
    [Arguments("""{"$include":"https://never-fetch.invalid/model#/%GG"}""")]
    [Arguments("""{"$include":"https://never-fetch.invalid/model#/bad~2token"}""")]
    public async Task OriginalIncludeSyntaxCannotBeHiddenBehindAValidResolvedCopy(string original)
    {
        var tree = Captured(JsonNode.Parse(original)!);
        await Check.Error(() => DirectoryMapping.OpenAsync(tree).AsTask(), FederationErrorCode.InvalidPackage);
        await Assert.That(string.Join(",", tree.Reads)).IsEqualTo("registry.json");
        await Assert.That(tree.DisposedStreams).IsEqualTo(1);
    }

    [Test]
    [Arguments("""{"$include":"https://never-fetch.invalid/model","description":"authored"}""")]
    [Arguments("""{"$include":"https://never-fetch.invalid/model","attributes":{"fixture":{"type":"integer"}}}""")]
    [Arguments("""{"$include":"https://never-fetch.invalid/model","groups":{}}""")]
    public async Task IncludedModelMaterialCannotOverrideExplicitOriginalMembers(string original)
    {
        var tree = Captured(JsonNode.Parse(original)!);
        await Check.Error(() => DirectoryMapping.OpenAsync(tree).AsTask(), FederationErrorCode.InvalidPackage);
        await Assert.That(tree.Reads.Count).IsEqualTo(1);
    }

    [Test]
    public async Task OriginalAndResolvedSourcesRemainSeparateWithoutAnyIncludeAcquisition()
    {
        var source = JsonNode.Parse("""
            {"$include":"parts/model.json#/model","attributes":{"fixture":{"type":"string"}}}
            """)!;
        var tree = Captured(source);
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var result = await mapping.ReadAsync(new(FederationOperation.Model, "/"));

        await Assert.That(result.Metadata.GetProperty("modelsource").GetProperty("$include").GetString())
            .IsEqualTo("parts/model.json#/model");
        await Assert.That(result.Metadata.GetProperty("resolvedmodelsource").GetProperty("groups").GetProperty("mirrors")
            .GetProperty("ximportresources")[0].GetString()).IsEqualTo("/documents/assets");
        await Assert.That(result.Metadata.GetProperty("modelbase").GetString()).IsEqualTo("https://capture.example/model.json");
        await Assert.That(ReferenceEquals(mapping.Model.Groups["documents"].Resources["assets"],
            mapping.Model.Groups["mirrors"].Resources["assets"])).IsTrue();
        await Assert.That(string.Join(",", tree.Reads)).IsEqualTo("registry.json");
    }

    private static MemoryTreeReader Captured(JsonNode original)
    {
        var tree = MemoryTreeReader.Load();
        var root = JsonNode.Parse(tree.Files["registry.json"])!;
        root["resolvedmodelsource"] = root["entity"]!["modelsource"]!.DeepClone();
        root["modelbase"] = "https://capture.example/model.json";
        root["entity"]!["modelsource"] = original;
        tree.Files["registry.json"] = Encoding.UTF8.GetBytes(root.ToJsonString());
        return tree;
    }
}
