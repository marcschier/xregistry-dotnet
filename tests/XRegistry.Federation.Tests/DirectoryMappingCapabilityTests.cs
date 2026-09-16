// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class DirectoryMappingCapabilityTests
{
    [Test]
    [Arguments("capabilities")]
    [Arguments("entities")]
    [Arguments("model")]
    public async Task SnapshotCapabilitiesCannotOmitCoreRequiredAccess(string name)
    {
        var tree = Capabilities(capabilities => capabilities["available"]!.AsObject().Remove(name));
        await Check.Error(() => DirectoryMapping.OpenAsync(tree).AsTask(), FederationErrorCode.InvalidPackage);
        await Assert.That(string.Join(",", tree.Reads)).IsEqualTo("registry.json");
    }

    [Test]
    [Arguments("capabilities")]
    [Arguments("entities")]
    [Arguments("model")]
    public async Task EveryAvailableAccessEntryRequiresAnExplicitMutableBoolean(string name)
    {
        var tree = Capabilities(capabilities => capabilities["available"]![name]!.AsObject().Remove("mutable"));
        await Check.Error(() => DirectoryMapping.OpenAsync(tree).AsTask(), FederationErrorCode.InvalidPackage);
        await Assert.That(tree.DisposedStreams).IsEqualTo(1);
    }

    [Test]
    [Arguments("\"FILTER\"")]
    [Arguments("\"sort\"")]
    [Arguments("7")]
    public async Task MappingCannotAdvertiseUnimplementedOrMalformedFlags(string flag)
    {
        var tree = Capabilities(capabilities => capabilities["flags"] = JsonNode.Parse("[" + flag + "]"));
        await Check.Error(() => DirectoryMapping.OpenAsync(tree).AsTask(), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task DefinedCapabilityNamesAreCaseInsensitiveAndDocumentViewFlagsAreRetained()
    {
        var tree = Capabilities(capabilities =>
        {
            var available = capabilities["available"]!.AsObject();
            var entities = available["entities"]!.DeepClone();
            available.Remove("entities");
            available["ENTITIES"] = entities;
            capabilities["flags"] = JsonNode.Parse("""["DOC","inline"]""");
        });
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var result = await mapping.ReadAsync(new(FederationOperation.Capabilities, "/"));

        await Assert.That(result.Metadata.GetProperty("available").GetProperty("ENTITIES").GetProperty("mutable").GetBoolean()).IsFalse();
        await Assert.That(result.Metadata.GetProperty("flags")[0].GetString()).IsEqualTo("DOC");
        await Assert.That(tree.Reads.Count).IsEqualTo(1);
    }

    private static MemoryTreeReader Capabilities(Action<JsonObject> change)
    {
        var tree = MemoryTreeReader.Load();
        var root = JsonNode.Parse(tree.Files["registry.json"])!;
        change(root["entity"]!["capabilities"]!.AsObject());
        tree.Files["registry.json"] = Encoding.UTF8.GetBytes(root.ToJsonString());
        return tree;
    }
}
