using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class DirectoryMappingMetadataOnlyFieldsTests
{
    private const string Version = "/categories/main/registries/site/versions/v1";

    [Test]
    [Arguments("registry", """{"nested":null,"value":42}""")]
    [Arguments("registrybase64", "\"not a document encoding\"")]
    [Arguments("registryurl", "\"https://must-not-fetch.invalid/blob\"")]
    public async Task ExplicitlyModeledMetadataOnlyFieldsDoNotAcquireDocumentSemantics(string field, string json)
    {
        var tree = Changed(field, "any", json);
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var result = await mapping.ReadAsync(new(FederationOperation.Entity, Version));
        await Assert.That(result.Metadata.GetProperty("entity").GetProperty(field).GetRawText()).IsEqualTo(json);
        await Assert.That(result.Document).IsNull();
        await Assert.That(result.ExternalDocument.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Undefined);
        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Document, Version)).AsTask(),
            FederationErrorCode.UnsupportedOperation);
        await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
        await mapping.ValidateAsync();
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    [Test]
    [Arguments("registry")]
    [Arguments("registrybase64")]
    [Arguments("registryurl")]
    public async Task OrdinaryDocumentLikeNamesStillObeyTheirDeclaredMetadataTypes(string field)
    {
        var tree = Changed(field, "string", "42");
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Entity, Version)).AsTask(),
            FederationErrorCode.InvalidPackage);
        await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("asset")]
    [Arguments("assetbase64")]
    [Arguments("asseturl")]
    public async Task ActualDocumentFieldsRemainForbiddenInDetachedLocalVersionMetadata(string field)
    {
        var tree = Rewrite(record =>
        {
            if (record["entity"]?["xid"]?.GetValue<string>() == "/documents/main/assets/item/versions/v1")
            {
                record["entity"]![field] = "still forbidden";
            }
        });
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Entity,
            "/documents/main/assets/item/versions/v1")).AsTask(), FederationErrorCode.InvalidPackage);
        await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
    }

    private static MemoryTreeReader Changed(string field, string type, string json) => Rewrite(record =>
    {
        var xid = record["entity"]?["xid"]?.GetValue<string>();
        if (xid == "/")
        {
            record["entity"]!["modelsource"]!["groups"]!["categories"]!["resources"]!["registries"]!["attributes"]![field] =
                new JsonObject { ["type"] = type };
        }
        if (xid == Version) { record["entity"]![field] = JsonNode.Parse(json); }
    });

    private static MemoryTreeReader Rewrite(Action<JsonObject> change)
    {
        var tree = new MemoryTreeReader();
        foreach (var pair in MappingUriFixture.Rewrite(MemoryTreeReader.Load().Files, change))
        {
            tree.Files.Add(pair.Key, pair.Value);
        }
        return tree;
    }
}
