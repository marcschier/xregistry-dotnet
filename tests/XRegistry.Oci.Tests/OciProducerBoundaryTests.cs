// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciProducerBoundaryTests
{
    [Test]
    [Arguments(3402, 1)]
    [Arguments(3401, 2)]
    public async Task WriterSplitsImmediatelyBeforeTheIndependentExactEncodedIndexBoundary(int limit, int expectedLeaves)
    {
        var package = await OciSnapshotWriter.CreateAsync(new(AuthoredCapture.Groups(10)), new() { MaxIndexBytes = limit });
        var leaves = 0;
        foreach (var item in package.Objects.Where(o => o.MediaType == GraphFixture.Index))
        {
            await using var stream = item.OpenRead();
            using var parsed = await JsonDocument.ParseAsync(stream);
            var annotation = parsed.RootElement.GetProperty("annotations");
            if (annotation.GetProperty(GraphFixture.Prefix + "kind").GetString() == "collection" &&
                annotation.GetProperty(GraphFixture.Prefix + "mode").GetString() == "leaf")
            {
                leaves++;
                if (limit == 3402) { await Assert.That(item.Size).IsEqualTo(3402L); }
            }
        }
        await Assert.That(leaves).IsEqualTo(expectedLeaves);
        await Assert.That(package.Validation.Configs).IsEqualTo(11);
    }

    [Test]
    [Arguments(256)]
    [Arguments(257)]
    [Arguments(999)]
    public async Task WriterPartitionsLargeCollectionsWithoutA256EntityCap(int count)
    {
        var package = await OciSnapshotWriter.CreateAsync(new(AuthoredCapture.Groups(count)));
        var leaves = 0;
        var entries = 0;
        foreach (var item in package.Objects.Where(o => o.MediaType == GraphFixture.Index))
        {
            await using var stream = item.OpenRead();
            using var node = await JsonDocument.ParseAsync(stream);
            var descriptors = node.RootElement.GetProperty("manifests");
            await Assert.That(descriptors.GetArrayLength() <= 256).IsTrue();
            await Assert.That(item.Size <= 1_048_576).IsTrue();
            var annotations = node.RootElement.GetProperty("annotations");
            if (annotations.GetProperty(GraphFixture.Prefix + "kind").GetString() == "collection" &&
                annotations.GetProperty(GraphFixture.Prefix + "mode").GetString() == "leaf")
            {
                leaves++;
                entries += descriptors.GetArrayLength();
            }
        }
        await Assert.That(entries).IsEqualTo(count);
        await Assert.That(leaves).IsEqualTo(count == 256 ? 1 : count == 257 ? 2 : 4);
        await using var snapshot = await OciSnapshot.OpenAsync(package.CreateReader(), package.RootDigest,
            new(new(maxObjects: 8192, maxRequests: 8192)));
        var collection = await snapshot.ReadAsync(new(FederationOperation.Collection, "/items"));
        await Assert.That(collection.Value.EnumerateObject().Count()).IsEqualTo(count);
        await Assert.That(collection.Value.GetProperty("g0000").GetProperty("self").GetString()).IsEqualTo("#/g0000");
        await Assert.That(collection.Value.GetProperty("g" + (count - 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture))
            .GetProperty("itemid").GetString()).IsEqualTo("g" + (count - 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task WriterSplitsByExactBytesBelowTheDescriptorLimitAndRejectsImpossibleEntries()
    {
        var input = new OciSnapshotInput(AuthoredCapture.Groups(50, 128));
        var package = await OciSnapshotWriter.CreateAsync(input, new() { MaxIndexBytes = 2_000 });
        var collectionLeaves = 0;
        foreach (var item in package.Objects.Where(o => o.MediaType == GraphFixture.Index))
        {
            await Assert.That(item.Size <= 2_000).IsTrue();
            await using var stream = item.OpenRead();
            using var parsed = await JsonDocument.ParseAsync(stream);
            var annotations = parsed.RootElement.GetProperty("annotations");
            if (annotations.GetProperty(GraphFixture.Prefix + "kind").GetString() == "collection" &&
                annotations.GetProperty(GraphFixture.Prefix + "mode").GetString() == "leaf")
            {
                collectionLeaves++;
                await Assert.That(parsed.RootElement.GetProperty("manifests").GetArrayLength() < 50).IsTrue();
            }
        }
        await Assert.That(collectionLeaves > 1).IsTrue();
        await Assert.That(package.Validation.Configs).IsEqualTo(51);
        await Check.Error(() => OciSnapshotWriter.CreateAsync(input, new() { MaxIndexBytes = 200 }).AsTask(),
            FederationErrorCode.LimitExceeded);
    }

    [Test]
    public async Task OpaqueOciLookingDomainBytesAreNotParsedCompressedOrRewritten()
    {
        var capture = GraphFixture.Capture(MemoryLayout.Load());
        const string payload = " \r\n{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.index.v1+json\",\"subject\":{},\"urls\":[\"https://never-fetch.invalid/\"]}\r\n ";
        capture.Documents[OciDocumentTests.Sample + "/versions/v1"] = new FederationDocument(Encoding.UTF8.GetBytes(payload));
        var package = await OciSnapshotWriter.CreateAsync(new(capture.Records, capture.Documents));
        await using var snapshot = await OciSnapshot.OpenAsync(package.CreateReader(), package.RootDigest);
        var document = await snapshot.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample));
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(document.Document!))).IsEqualTo(payload);
        await Assert.That(document.Document!.Length).IsEqualTo(Encoding.UTF8.GetByteCount(payload));
    }

    [Test]
    public async Task NontrivialJsonPointerEscapingIsLocalToTheReturnedDocument()
    {
        var records = AuthoredCapture.Groups(1);
        var group = JsonNode.Parse(records[1].RootElement.GetRawText())!;
        group["entity"]!["itemid"] = "a~b";
        group["entity"]!["xid"] = "/items/a~b";
        records[1] = RegistryJson.Parse(group.ToJsonString());
        var package = await OciSnapshotWriter.CreateAsync(new(records));
        await using var snapshot = await OciSnapshot.OpenAsync(package.CreateReader(), package.RootDigest);
        var registry = await snapshot.ReadAsync(new(FederationOperation.Entity, "/"));
        var entity = registry.Value.GetProperty("items").GetProperty("a~b");
        await Assert.That(entity.GetProperty("self").GetString()).IsEqualTo("#/items/a~0b");
        await Assert.That(OciMetadataTests.Resolve(registry.Value, entity.GetProperty("self").GetString()!)
            .GetProperty("itemid").GetString()).IsEqualTo("a~b");
    }

    [Test]
    public async Task ContradictoryCapturedDocumentContextIsAnInconsistentSnapshot()
    {
        var capture = GraphFixture.Capture(MemoryLayout.Load());
        capture.Documents[OciDocumentTests.Sample + "/versions/v1"] = new FederationDocument("{\"type\":\"string\"}\n"u8,
            "application/wrong", "https://unexpected.example.org/");
        await Check.Error(() => OciSnapshotWriter.CreateAsync(new(capture.Records, capture.Documents)).AsTask(),
            FederationErrorCode.InconsistentSnapshot);
    }
}
