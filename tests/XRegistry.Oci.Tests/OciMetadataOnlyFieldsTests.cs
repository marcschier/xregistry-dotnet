using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciMetadataOnlyFieldsTests
{
    private const string Version = "/dirs/main/notes/info/versions/v1";

    [Test]
    [Arguments("note", """{"nested":null,"value":42}""")]
    [Arguments("notebase64", "\"not a document encoding\"")]
    [Arguments("noteurl", "\"https://must-not-fetch.invalid/blob\"")]
    [Arguments("note", "null")]
    [Arguments("notebase64", "null")]
    [Arguments("noteurl", "null")]
    public async Task MetadataOnlyDocumentLikeNamesPreserveValuesWithoutDocumentSemantics(string field, string json)
    {
        var tree = Changed(field, new JsonObject { ["type"] = "any" }, json);
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var result = await snapshot.ReadAsync(new(FederationOperation.Entity, Version));
        await Assert.That(result.Value.GetProperty(field).GetRawText()).IsEqualTo(json);
        await Assert.That(result.Document).IsNull();
        await Assert.That(result.ExternalDocument.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        var reads = tree.Reads.Count;
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Document, Version)).AsTask(),
            FederationErrorCode.UnsupportedOperation);
        await Check.Error(() => snapshot.ReadExternalDocumentDescriptorAsync(Version).AsTask(),
            FederationErrorCode.UnsupportedOperation);
        await Assert.That(tree.Reads.Count).IsEqualTo(reads);
        await Assert.That((await snapshot.ValidateAsync()).Configs).IsEqualTo(25);

        var capture = GraphFixture.Capture(tree);
        var package = await OciSnapshotWriter.CreateAsync(new(capture.Records, capture.Documents));
        await using var produced = await OciSnapshot.OpenAsync(package.CreateReader(), package.RootDigest);
        var roundTrip = await produced.ReadAsync(new(FederationOperation.Entity, Version));
        await Assert.That(roundTrip.Value.GetProperty(field).GetRawText()).IsEqualTo(json);
        await Assert.That(package.Validation.Documents).IsEqualTo(4);
        await snapshot.DisposeAsync();
        await Assert.That(result.Value.GetProperty(field).GetRawText()).IsEqualTo(json);
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    [Test]
    [Arguments("note", false)]
    [Arguments("notebase64", false)]
    [Arguments("noteurl", false)]
    [Arguments("note", true)]
    [Arguments("notebase64", true)]
    [Arguments("noteurl", true)]
    public async Task MetadataOnlyDocumentLikeDefaultsSurviveReadAndPublication(string field, bool explicitNull)
    {
        var tree = Changed(field, new JsonObject
        {
            ["type"] = "string",
            ["required"] = true,
            ["default"] = "ordinary metadata default",
        }, explicitNull ? "null" : null);
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await snapshot.ValidateAsync();
        var result = await snapshot.ReadAsync(new(FederationOperation.Entity, Version));
        await Assert.That(result.Value.GetProperty(field).GetString()).IsEqualTo("ordinary metadata default");

        var capture = GraphFixture.Capture(tree);
        var package = await OciSnapshotWriter.CreateAsync(new(capture.Records, capture.Documents));
        await using var produced = await OciSnapshot.OpenAsync(package.CreateReader(), package.RootDigest);
        var roundTrip = await produced.ReadAsync(new(FederationOperation.Entity, Version));
        await Assert.That(roundTrip.Value.GetProperty(field).GetString()).IsEqualTo("ordinary metadata default");
        await Check.Error(() => produced.ReadAsync(new(FederationOperation.Document, Version)).AsTask(),
            FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    [Arguments("note")]
    [Arguments("notebase64")]
    [Arguments("noteurl")]
    public async Task OrdinaryDocumentLikeNamesStillEnforceTheirModeledTypes(string field)
    {
        var tree = Changed(field, new JsonObject { ["type"] = "string" }, "42");
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Entity, Version)).AsTask(),
            FederationErrorCode.InvalidPackage);
        var capture = GraphFixture.Capture(tree);
        await Check.Error(() => OciSnapshotWriter.CreateAsync(new(capture.Records, capture.Documents)).AsTask(),
            FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("file")]
    [Arguments("filebase64")]
    [Arguments("fileurl")]
    public async Task ActualDocumentFieldsRemainForbiddenOnEmbeddedVersions(string field)
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Record(tree, OciDocumentTests.Sample + "/versions/v1",
            record => record["entity"]![field] = "still forbidden");
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Entity,
            OciDocumentTests.Sample + "/versions/v1")).AsTask(), FederationErrorCode.InvalidPackage);
        var capture = GraphFixture.Capture(tree);
        await Check.Error(() => OciSnapshotWriter.CreateAsync(new(capture.Records, capture.Documents)).AsTask(),
            FederationErrorCode.InvalidPackage);
    }

    private static MemoryLayout Changed(string field, JsonObject definition, string? json)
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Rewrite(tree, (record, media) =>
        {
            if (media != GraphFixture.Config) { return GraphFixture.Encode(record); }
            var entity = record["entity"]!;
            var xid = entity["xid"]!.GetValue<string>();
            if (xid == "/")
            {
                var source = record["modelresolved"]!;
                source["groups"]!["dirs"]!["resources"]!["notes"]!["attributes"] =
                    new JsonObject { [field] = definition.DeepClone() };
                entity["modelsource"] = source.DeepClone();
                entity["model"] = JsonNode.Parse(RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString()))
                    .EffectiveModel.RootElement.GetRawText());
            }
            if (xid == Version && json is not null) { entity[field] = JsonNode.Parse(json); }
            return GraphFixture.Encode(record);
        });
        return tree;
    }
}
