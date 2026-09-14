using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class DirectoryMappingModelValidationTests
{
    private const string Item = "/documents/main/assets/item";
    private const string Version = Item + "/versions/v1";

    [Test]
    [Arguments("type")]
    [Arguments("unknown")]
    [Arguments("empty-name")]
    [Arguments("required")]
    public async Task OpeningRejectsRootMetadataOutsideTheCapturedModel(string mutation)
    {
        var tree = Changed(record =>
        {
            if (record["entity"]?["xid"]?.GetValue<string>() != "/") { return; }
            var entity = record["entity"]!.AsObject();
            switch (mutation)
            {
                case "type": entity["fixture"] = 7; break;
                case "unknown": entity["unmodeled"] = true; break;
                case "empty-name": entity["name"] = ""; break;
                case "required":
                    entity["modelsource"]!["attributes"]!["fixture"]!["required"] = true;
                    entity.Remove("fixture");
                    break;
            }
        });

        await Check.Error(() => DirectoryMapping.OpenAsync(tree).AsTask(), FederationErrorCode.InvalidPackage);
        await Assert.That(string.Join(",", tree.Reads)).IsEqualTo("registry.json");
        await Assert.That(tree.DisposedStreams).IsEqualTo(1);
    }

    [Test]
    [Arguments("/documents/main", "name", "\"\"")]
    [Arguments(Item + "/meta", "compatibility", "\"unexpected\"")]
    [Arguments(Version, "purpose", "7")]
    [Arguments(Version, "name", "\"\"")]
    [Arguments(Version, "unmodeled", "true")]
    [Arguments("/categories/main/registries/site/versions/v1", "weburl", "7")]
    public async Task EntityReadsRejectInvalidGroupMetaAndVersionMetadata(string xid, string name, string value)
    {
        var tree = Changed(record =>
        {
            if (record["entity"]?["xid"]?.GetValue<string>() == xid)
            {
                record["entity"]![name] = JsonNode.Parse(value);
            }
        });
        await using var mapping = await DirectoryMapping.OpenAsync(tree);

        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Entity, xid)).AsTask(),
            FederationErrorCode.InvalidPackage);
        await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    [Test]
    public async Task DocumentReadsRejectInvalidVersionMetadataBeforeReadingDomainBytes()
    {
        var tree = Changed(record =>
        {
            if (record["entity"]?["xid"]?.GetValue<string>() == Version) { record["entity"]!["purpose"] = 7; }
        });
        await using var mapping = await DirectoryMapping.OpenAsync(tree);

        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Document, Item)).AsTask(),
            FederationErrorCode.InvalidPackage);
        await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SelectedVersionMustSatisfyConditionalRequiredMetadata(bool present)
    {
        var tree = Changed(record =>
        {
            var xid = record["entity"]?["xid"]?.GetValue<string>();
            if (xid == "/")
            {
                record["entity"]!["modelsource"]!["groups"]!["documents"]!["resources"]!["assets"]!["attributes"]!["purpose"]!["ifvalues"] =
                    JsonNode.Parse("""{"primary":{"siblingattributes":{"enabled":{"type":"boolean","required":true}}}}""");
            }
            if (xid == Version && present) { record["entity"]!["enabled"] = false; }
        });
        await using var mapping = await DirectoryMapping.OpenAsync(tree);

        if (present)
        {
            var result = await mapping.ReadAsync(new(FederationOperation.Entity, Version));
            var entity = result.Metadata.GetProperty("entity");
            await Assert.That(entity.GetProperty("purpose").GetString()).IsEqualTo("primary");
            await Assert.That(entity.GetProperty("enabled").GetBoolean()).IsFalse();
            await Assert.That(entity.GetProperty("xid").GetString()).IsEqualTo(Version);
            await Assert.That(entity.GetProperty("self").GetString()).IsEqualTo("#/entity");
        }
        else
        {
            await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Entity, Version)).AsTask(),
                FederationErrorCode.InvalidPackage);
        }
        await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SelectedVersionUsesItsOwningGroupsEqualsConstraint(bool agrees)
    {
        var tree = Changed(record =>
        {
            var xid = record["entity"]?["xid"]?.GetValue<string>();
            if (xid == "/")
            {
                var group = record["entity"]!["modelsource"]!["groups"]!["documents"]!;
                group["attributes"] = JsonNode.Parse("""{"site":{"type":"string"}}""");
                group["constraints"] = JsonNode.Parse("""{"assets.purpose":{"equals":"site"}}""");
            }
            if (xid == "/documents/main") { record["entity"]!["site"] = agrees ? "primary" : "other"; }
        });
        await using var mapping = await DirectoryMapping.OpenAsync(tree);

        if (agrees)
        {
            var result = await mapping.ReadAsync(new(FederationOperation.Document, Item));
            await Assert.That(result.SelectedXid).IsEqualTo(Version);
            await Assert.That(await DirectoryMappingTests.ReadHex(result.Document!)).IsEqualTo("7B2268656C6C6F223A22776F726C64227D0A");
        }
        else
        {
            await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Document, Item)).AsTask(),
                FederationErrorCode.InvalidPackage);
            await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
        }
        await Assert.That(tree.Reads.Count(path => path == "records/n4.json")).IsEqualTo(1);
    }

    [Test]
    public async Task UnverifiedUriTargetConstraintsFailWithoutAcquiringExternalContent()
    {
        var tree = Changed(record =>
        {
            var xid = record["entity"]?["xid"]?.GetValue<string>();
            if (xid == "/")
            {
                record["entity"]!["modelsource"]!["groups"]!["documents"]!["resources"]!["assets"]!["attributes"]!["upstream"] =
                    JsonNode.Parse("""{"type":"uri","target":"/documents/assets[/versions]"}""");
            }
            if (xid == Version) { record["entity"]!["upstream"] = "/documents/g/assets/r"; }
        });
        await using var mapping = await DirectoryMapping.OpenAsync(tree);

        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Document, Item)).AsTask(),
            FederationErrorCode.UnsupportedOperation);
        await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    [Test]
    [Arguments("2016-12-31T23:59:60.000000001Z")]
    [Arguments("2026-01-02T00:00:00.000000001+23:59")]
    public async Task CapturedSystemTimestampsUseTheCoreGrammarWithoutDateTimeNarrowing(string timestamp)
    {
        var tree = Changed(record =>
        {
            if (record["entity"]?["xid"]?.GetValue<string>() == Version) { record["entity"]!["createdat"] = timestamp; }
        });
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var result = await mapping.ReadAsync(new(FederationOperation.Entity, Version));

        await Assert.That(result.Metadata.GetProperty("entity").GetProperty("createdat").GetString()).IsEqualTo(timestamp);
        await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
    }

    private static MemoryTreeReader Changed(Action<JsonObject> change)
    {
        var tree = new MemoryTreeReader();
        foreach (var pair in MappingUriFixture.Rewrite(MemoryTreeReader.Load().Files, change))
        {
            tree.Files.Add(pair.Key, pair.Value);
        }
        return tree;
    }
}
