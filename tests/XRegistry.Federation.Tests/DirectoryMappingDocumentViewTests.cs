// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class DirectoryMappingDocumentViewTests
{
    private const string Item = "/documents/main/assets/item";

    [Test]
    public async Task RootViewUsesOnlyRealResponseLocalNavigationAndKeepsCaptureContextOutsideEntities()
    {
        var tree = MemoryTreeReader.Load();
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var result = await mapping.ReadAsync(new(FederationOperation.Entity, "/"));
        var root = result.Metadata.GetProperty("entity");
        var group = root.GetProperty("documents").GetProperty("main");
        var resource = group.GetProperty("assets").GetProperty("item");
        var meta = resource.GetProperty("meta");
        var version = resource.GetProperty("versions").GetProperty("v1");

        await Assert.That(root.GetProperty("self").GetString()).IsEqualTo("#/entity");
        await Assert.That(root.GetProperty("documentsurl").GetString()).IsEqualTo("#/entity/documents");
        await Assert.That(root.GetProperty("documentscount").GetInt32()).IsEqualTo(1);
        await Assert.That(group.GetProperty("self").GetString()).IsEqualTo("#/entity/documents/main");
        await Assert.That(resource.GetProperty("metaurl").GetString()).IsEqualTo("#/entity/documents/main/assets/item/meta");
        await Assert.That(resource.GetProperty("versionsurl").GetString()).IsEqualTo("#/entity/documents/main/assets/item/versions");
        await Assert.That(resource.GetProperty("versionscount").GetInt32()).IsEqualTo(2);
        await Assert.That(meta.GetProperty("self").GetString()).IsEqualTo("#/entity/documents/main/assets/item/meta");
        await Assert.That(meta.GetProperty("defaultversionurl").GetString()).IsEqualTo("#/entity/documents/main/assets/item/versions/v1");
        await Assert.That(version.GetProperty("self").GetString()).IsEqualTo("#/entity/documents/main/assets/item/versions/v1");
        await Assert.That(version.GetProperty("purpose").GetString()).IsEqualTo("primary");
        await Assert.That(resource.TryGetProperty("purpose", out _)).IsFalse();
        await Assert.That(resource.TryGetProperty("versionid", out _)).IsFalse();
        await Assert.That(root.TryGetProperty("snapshot", out _)).IsFalse();
        await Assert.That(result.Metadata.GetProperty("snapshot").GetProperty("scope").GetString()).IsEqualTo("/");
        await Assert.That(result.Metadata.GetProperty("source").GetProperty("revision").GetString()).IsEqualTo("fixture-1");
        await NoDocuments(tree);
    }

    [Test]
    public async Task StandaloneMetaIncludesItsActualDefaultVersionInTheRelatedEnvelope()
    {
        var tree = MemoryTreeReader.Load();
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var result = await mapping.ReadAsync(new(FederationOperation.Entity, Item + "/meta"));
        var meta = result.Metadata.GetProperty("entity");
        var version = result.Metadata.GetProperty("related").GetProperty("defaultversion");

        await Assert.That(meta.GetProperty("self").GetString()).IsEqualTo("#/entity");
        await Assert.That(meta.GetProperty("defaultversionurl").GetString()).IsEqualTo("#/related/defaultversion");
        await Assert.That(version.GetProperty("self").GetString()).IsEqualTo("#/related/defaultversion");
        await Assert.That(version.GetProperty("xid").GetString()).IsEqualTo(Item + "/versions/v1");
        await Assert.That(version.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(version.GetProperty("isdefault").GetBoolean()).IsTrue();
        await NoDocuments(tree);
    }

    [Test]
    [Arguments("copy", Item)]
    [Arguments("chain", "/mirrors/local/assets/copy")]
    [Arguments("dangling", "/documents/main/assets/missing")]
    public async Task ResourceAndMetaAliasViewsRemainUnexpandedAndKeepTheSourcesOwnIdentity(string id, string target)
    {
        var tree = MemoryTreeReader.Load();
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var xid = "/mirrors/local/assets/" + id;
        var resource = (await mapping.ReadAsync(new(FederationOperation.Entity, xid))).Metadata.GetProperty("entity");
        var metaResult = (await mapping.ReadAsync(new(FederationOperation.Entity, xid + "/meta"))).Metadata;
        var meta = metaResult.GetProperty("entity");

        await Assert.That(resource.GetProperty("xid").GetString()).IsEqualTo(xid);
        await Assert.That(resource.GetProperty("assetid").GetString()).IsEqualTo(id);
        await Assert.That(resource.GetProperty("meta").GetProperty("xref").GetString()).IsEqualTo(target);
        await Assert.That(resource.TryGetProperty("versions", out _)).IsFalse();
        await Assert.That(resource.TryGetProperty("labels", out _)).IsFalse();
        await Assert.That(meta.GetProperty("xref").GetString()).IsEqualTo(target);
        await Assert.That(meta.GetProperty("xid").GetString()).IsEqualTo(xid + "/meta");
        await Assert.That(meta.TryGetProperty("defaultversionid", out _)).IsFalse();
        await Assert.That(metaResult.TryGetProperty("related", out _)).IsFalse();
        await NoDocuments(tree);
    }

    [Test]
    [Arguments(FederationOperation.Collection, "/mirrors/local/assets/copy/versions")]
    [Arguments(FederationOperation.Entity, "/mirrors/local/assets/copy/versions/v1")]
    public async Task AliasVersionViewsRetainTheCoreCannotDocXrefDiagnostic(FederationOperation operation, string xid)
    {
        var tree = MemoryTreeReader.Load();
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var error = await Check.Error(() => mapping.ReadAsync(new(operation, xid)).AsTask(), FederationErrorCode.UnsupportedOperation);

        await Assert.That(error.Diagnostic).IsEqualTo("cannot_doc_xref");
        await NoDocuments(tree);
    }

    [Test]
    [Arguments("/documents/main/assets", "stage", "PRODUCTION", Item)]
    [Arguments("/documents/main/assets", "note", "", Item)]
    [Arguments("/mirrors/local/assets", "stage", "production", "/mirrors/local/assets/copy")]
    public async Task LiteralLabelSelectionUsesTheDefaultVersionButReturnsTheDocumentViewIdentity(
        string collection, string label, string value, string expected)
    {
        var tree = MemoryTreeReader.Load();
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var result = await mapping.ReadAsync(new(FederationOperation.Collection, collection, new(label, value)));

        await Assert.That(result.SelectedXid).IsEqualTo(expected);
        await Assert.That(result.Metadata.GetProperty("entity").GetProperty("xid").GetString()).IsEqualTo(expected);
        await Assert.That(result.Metadata.GetProperty("entity").GetProperty("self").GetString()).IsEqualTo("#/entity");
        if (expected.StartsWith("/mirrors/", StringComparison.Ordinal))
        {
            await Assert.That(result.Metadata.GetProperty("entity").GetProperty("meta").GetProperty("xref").GetString()).IsEqualTo(Item);
            await Assert.That(result.Metadata.GetProperty("entity").TryGetProperty("versions", out _)).IsFalse();
        }
        await NoDocuments(tree);
    }

    [Test]
    [Arguments("stage", "development")]
    [Arguments("stage", "prod*")]
    public async Task NondefaultLabelsAndWildcardLookingValuesDoNotSelectResources(string label, string value)
    {
        var tree = MemoryTreeReader.Load();
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Collection, "/documents/main/assets", new(label, value))).AsTask(),
            FederationErrorCode.NotFound);
        await NoDocuments(tree);
    }

    [Test]
    public async Task InvalidLabelKeyIsRejectedBeforeAnyReaderAccess()
    {
        var tree = MemoryTreeReader.Load();
        await Check.Error(() => _ = new FederationReadRequest(FederationOperation.Collection, "/documents/main/assets",
            new("Stage", "production")), FederationErrorCode.InvalidPackage);
        await Assert.That(tree.Reads.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SelectionCannotHideAnInvalidNonmatchingSiblingBehindAUniqueValidMatch()
    {
        var tree = Changed(record =>
        {
            if (record["entity"]?["xid"]?.GetValue<string>() == "/documents/main/assets/CON")
            {
                record["entity"]!["unexpected"] = true;
            }
        });
        await using var mapping = await DirectoryMapping.OpenAsync(tree);

        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Collection, "/documents/main/assets",
            new("stage", "production"))).AsTask(), FederationErrorCode.InvalidPackage);
        await NoDocuments(tree);
    }

    [Test]
    public async Task TwoEffectiveDefaultMatchesAreAmbiguousRatherThanIndexOrderSelection()
    {
        var tree = Changed(record =>
        {
            if (record["entity"]?["xid"]?.GetValue<string>() == "/documents/main/assets/CON/versions/a:b@c.")
            {
                record["entity"]!["labels"] = JsonNode.Parse("""{"stage":"production"}""");
            }
        });
        await using var mapping = await DirectoryMapping.OpenAsync(tree);

        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Collection, "/documents/main/assets",
            new("stage", "production"))).AsTask(), FederationErrorCode.Ambiguous);
        await NoDocuments(tree);
    }

    private static MemoryTreeReader Changed(Action<JsonObject> change)
    {
        var tree = new MemoryTreeReader();
        foreach (var pair in MappingUriFixture.Rewrite(MemoryTreeReader.Load().Files, change)) { tree.Files.Add(pair.Key, pair.Value); }
        return tree;
    }

    private static async Task NoDocuments(MemoryTreeReader tree)
    {
        await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }
}
