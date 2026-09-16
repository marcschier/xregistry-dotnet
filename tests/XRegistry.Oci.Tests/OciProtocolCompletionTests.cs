// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciProtocolCompletionTests
{
    [Test]
    [Arguments("node")]
    [Arguments("descriptor")]
    [Arguments("layout")]
    public async Task UnknownNonProfileAnnotationsRemainNonAuthoritativeIncludingEmptyKeys(string placement)
    {
        var tree = MemoryLayout.Load();
        if (placement == "layout")
        {
            var layout = JsonNode.Parse(tree.Files["index.json"])!.AsObject();
            layout["annotations"] = new JsonObject { [""] = "", ["example.metadata"] = """{"registryid":"not-authoritative"}""" };
            tree.Files["index.json"] = GraphFixture.Encode(layout);
        }
        else
        {
            GraphFixture.Node(tree, "registry", "/", node =>
            {
                var annotations = placement == "node" ? node["annotations"]! : node["manifests"]![0]!["annotations"]!;
                annotations[""] = "";
                annotations["example.metadata"] = """{"registryid":"not-authoritative"}""";
            });
        }

        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var validation = await snapshot.ValidateAsync();
        var metadata = await snapshot.ReadAsync(new(FederationOperation.Entity, "/"));

        await Assert.That(validation.Configs).IsEqualTo(25);
        await Assert.That(metadata.Value.GetProperty("registryid").GetString()).IsEqualTo("fixture-offline");
        await Assert.That(metadata.Value.TryGetProperty("example.metadata", out _)).IsFalse();
    }

    [Test]
    public async Task UnsupportedApiViewFailsWithoutInventingNavigationOrAcquiringMoreObjects()
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var reads = tree.Reads.Count;
        var request = new FederationReadRequest(FederationOperation.Entity, "/dirs/main",
            representation: FederationRepresentation.ApiView);

        await Check.Error(() => snapshot.ReadAsync(request).AsTask(), FederationErrorCode.UnsupportedOperation);
        await Check.Error(() => ((IFederationReadSource)snapshot).ReadAsync(request).AsTask(), FederationErrorCode.UnsupportedOperation);

        await Assert.That(tree.Reads.Count).IsEqualTo(reads);
    }

    [Test]
    public async Task MissingSelectedRootIsNotFoundRatherThanAMissingDescendantPackageError()
    {
        var tree = MemoryLayout.Load();
        tree.Files.Remove("blobs/sha256/" + OciBootstrapTests.Offline[7..]);

        await Check.Error(() => OciSnapshot.OpenLayoutAsync(tree, "offline").AsTask(), FederationErrorCode.NotFound);

        await Assert.That(tree.Reads.Count).IsEqualTo(3);
        await Assert.That(tree.Reads[^1]).IsEqualTo("blobs/sha256/" + OciBootstrapTests.Offline[7..]);
    }
}
