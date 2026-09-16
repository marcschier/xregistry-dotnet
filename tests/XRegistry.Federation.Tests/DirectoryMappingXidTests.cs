// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class DirectoryMappingXidTests
{
    private const string Resource = "/documents/main/assets/CON";
    private const string Version = Resource + "/versions/a:b@c.";
    private const string Selector = "/d%6fcuments/%6dain/%61ssets/CON/%76ersions/a%3ab%40c.";
    private const string RenamedResource = "/documents/group:one/assets/item@stable";
    private const string StoredResource = "/%64ocuments/group%3Aone/%61ssets/item%40stable";

    [Test]
    [Arguments("%2f")]
    [Arguments("%5c")]
    [Arguments("%25")]
    [Arguments("%253A")]
    [Arguments("%")]
    [Arguments("%GG")]
    [Arguments("%C0%AF")]
    [Arguments("%ED%A0%80")]
    public async Task InvalidSelectorsFailBeforeAcquisitionAndStoredEscapesFailBeforeMemberReads(string escape)
    {
        var tree = MemoryTreeReader.Load();
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var before = tree.Reads.Count;
        await Check.Error(async () => { await mapping.ReadAsync(new(FederationOperation.Entity, "/documents/group" + escape)); },
            FederationErrorCode.InvalidPackage);
        await Assert.That(tree.Reads.Count).IsEqualTo(before);
        var changed = MappingUriFixture.Rewrite(tree.Files, node =>
        {
            if (node["kind"]!.GetValue<string>() != "collection" || node["xid"]!.GetValue<string>() != "/documents") { return; }
            var entries = node["entries"]!.AsArray();
            entries.Single(entry => entry!["xid"]!.GetValue<string>() == "/documents/main")!["xid"] = "/documents/group" + escape;
            node["entries"] = new JsonArray(entries.OrderBy(entry => entry!["xid"]!.GetValue<string>(), StringComparer.Ordinal)
                .Select(entry => entry!.DeepClone()).ToArray());
        });
        var invalid = Tree(changed);
        await using var malformed = await DirectoryMapping.OpenAsync(invalid);
        await Check.Error(() => malformed.ReadAsync(new(FederationOperation.Collection, "/documents")).AsTask(),
            FederationErrorCode.InvalidPackage);
        await Assert.That(invalid.Reads.Any(path => path.StartsWith("records/", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("%6dain")]
    [Arguments("%4dain")]
    public async Task SiblingUriIdentityCollisionsCannotProduceACompleteCollection(string duplicate)
    {
        var tree = Tree(MappingUriFixture.Rewrite(MemoryTreeReader.Load().Files, node =>
        {
            if (node["kind"]!.GetValue<string>() != "collection" || node["xid"]!.GetValue<string>() != "/documents") { return; }
            var entries = node["entries"]!.AsArray();
            var entry = entries.Single(value => value!["xid"]!.GetValue<string>() == "/documents/main")!.DeepClone();
            entry["xid"] = "/documents/" + duplicate;
            entries.Add(entry);
            node["count"] = entries.Count;
            node["entries"] = new JsonArray(entries.OrderBy(value => value!["xid"]!.GetValue<string>(), StringComparer.Ordinal)
                .Select(value => value!.DeepClone()).ToArray());
        }));
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Collection, "/documents")).AsTask(),
            FederationErrorCode.InvalidPackage);
        await Assert.That(tree.Reads.Any(path => path.StartsWith("records/", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task EncodedAliasAndExternalDescriptorKeepOpaqueLocatorsAndStoredTarget()
    {
        var files = MappingUriFixture.Renamed(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mapping"), true);
        const string uri = "relative%3a.doc?part=%25";
        const string baseUri = "https://content.example/root%40base/";
        var tree = Tree(MappingUriFixture.Rewrite(files, node =>
        {
            if (node["entity"]?["xid"]?.GetValue<string>() == "/") { node["snapshot"]!["completeness"] = "linked"; }
            if (node["entity"]?["xid"]?.GetValue<string>() != StoredResource + "/%76ersions/v%3A1") { return; }
            node["entity"]!["asseturl"] = uri;
            node["document"] = new JsonObject { ["kind"] = "external", ["uri"] = uri, ["base"] = baseUri };
        }));
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var alias = await mapping.ReadAsync(new(FederationOperation.Entity, "/%6dirrors/local/assets/%63opy"));
        await Assert.That(alias.Metadata.GetProperty("entity").GetProperty("meta").GetProperty("xref").GetString()).IsEqualTo(StoredResource);
        await Assert.That(alias.Metadata.GetProperty("entity").TryGetProperty("versions", out _)).IsFalse();
        var document = await mapping.ReadAsync(new(FederationOperation.Document, "/mirrors/local/assets/copy/%76ersions/v%3a1"));
        await Assert.That(document.SelectedXid).IsEqualTo(StoredResource + "/%76ersions/v%3A1");
        await Check.Json(document.ExternalDocument, """
            {"kind":"external","uri":"relative%3a.doc?part=%25","base":"https://content.example/root%40base/"}
            """);
        await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Document, "/mirrors/local/assets/chain")).AsTask(),
            FederationErrorCode.NotFound);
        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Document, "/mirrors/local/assets/dangling")).AsTask(),
            FederationErrorCode.NotFound);
    }

    [Test]
    public async Task RewrittenMappingKeepsDigestChecksAndCaseSensitiveSelection()
    {
        var tree = Tree(MappingUriFixture.Renamed(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mapping"), true));
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Entity,
            "/documents/Group%3Aone/assets/item%40stable")).AsTask(), FederationErrorCode.NotFound);
        tree.Files["records/na.json"] = [0xff];
        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Entity, RenamedResource + "/versions/v%3A1")).AsTask(),
            FederationErrorCode.IntegrityError);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RewrittenStoredUrisPreserveIdsBytesAndResponseLocalMetaPointers(bool escaped)
    {
        var tree = new MemoryTreeReader();
        foreach (var pair in MappingUriFixture.Renamed(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mapping"), escaped))
        {
            tree.Files.Add(pair.Key, pair.Value);
        }
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var resource = escaped ? StoredResource : RenamedResource;
        var version = resource + (escaped ? "/%76ersions/v%3A1" : "/versions/v:1");
        var request = new FederationReadRequest(FederationOperation.Document,
            "/documents/group%3aone/assets/item%40stable/versions/v%3a1");
        var document = await mapping.ReadAsync(request);
        await Assert.That(document.SelectedXid).IsEqualTo(version);
        await Assert.That(request.Target).IsEqualTo("/documents/group%3aone/assets/item%40stable/versions/v%3a1");
        await Assert.That(await DirectoryMappingTests.ReadHex(document.Document!)).IsEqualTo("7B2268656C6C6F223A22776F726C64227D0A");
        var meta = await mapping.ReadAsync(new(FederationOperation.Entity, RenamedResource + "/%6deta"));
        await Assert.That(meta.SelectedXid).IsEqualTo(resource + (escaped ? "/%6deta" : "/meta"));
        await Assert.That(meta.Metadata.GetProperty("entity").GetProperty("assetid").GetString()).IsEqualTo("item@stable");
        await Assert.That(meta.Metadata.GetProperty("related").GetProperty("defaultversion").GetProperty("xid").GetString()).IsEqualTo(version);
        var collection = await mapping.ReadAsync(new(FederationOperation.Collection, RenamedResource + "/%76ersions"));
        var versions = collection.Metadata.GetProperty("entities");
        await Assert.That(versions.GetProperty("v:1").GetProperty("versionid").GetString()).IsEqualTo("v:1");
        await Assert.That(versions.GetProperty("v:1").GetProperty("self").GetString()).IsEqualTo("#/entities/v%3A1");
        await Assert.That(versions.GetProperty("v:2").GetProperty("versionid").GetString()).IsEqualTo("v:2");
        await Assert.That(tree.Reads.All(path => path == "registry.json" || path.StartsWith("records/", StringComparison.Ordinal) ||
            path.StartsWith("indexes/", StringComparison.Ordinal) || path.StartsWith("documents/", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments(FederationOperation.Entity)]
    [Arguments(FederationOperation.Document)]
    public async Task FrozenEscapedSelectorsKeepStoredIdentityAndExactBytes(FederationOperation operation)
    {
        var tree = MemoryTreeReader.Load();
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var request = new FederationReadRequest(operation, Selector);
        var result = await mapping.ReadAsync(request);
        await Assert.That(request.Target).IsEqualTo(Selector);
        await Assert.That(result.SelectedXid).IsEqualTo(Version);
        await Assert.That(result.Context.RootSha256).IsEqualTo("7e4c37ca61b2b875ee055a8fc67e5c90c48b344689823a12dc1fb0a6e33232bf");
        if (operation == FederationOperation.Document)
        {
            await Assert.That(await DirectoryMappingTests.ReadHex(result.Document!)).IsEqualTo("0001FF7F0A");
        }
        else
        {
            var entity = result.Metadata.GetProperty("entity");
            await Assert.That(entity.GetProperty("versionid").GetString()).IsEqualTo("a:b@c.");
            await Assert.That(entity.GetProperty("xid").GetString()).IsEqualTo(Version);
            await Assert.That(entity.GetProperty("self").GetString()).IsEqualTo("#/entity");
            await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
        }
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    [Test]
    public async Task FrozenEscapedCollectionHasDecodedKeysAndResolvingPointers()
    {
        var tree = MemoryTreeReader.Load();
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var result = await mapping.ReadAsync(new(FederationOperation.Collection,
            "/documents/%6dain/assets/CON/versi%6fns"));
        await Assert.That(result.SelectedXid).IsEqualTo(Resource + "/versions");
        await Assert.That(result.Metadata.GetProperty("complete").GetBoolean()).IsTrue();
        var entities = result.Metadata.GetProperty("entities");
        await Assert.That(entities.EnumerateObject().Count()).IsEqualTo(1);
        var version = entities.GetProperty("a:b@c.");
        await Assert.That(version.GetProperty("xid").GetString()).IsEqualTo(Version);
        await Assert.That(version.GetProperty("self").GetString()).IsEqualTo("#/entities/a%3Ab%40c.");
        await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
    }

    private static MemoryTreeReader Tree(IReadOnlyDictionary<string, byte[]> files)
    {
        var tree = new MemoryTreeReader();
        foreach (var pair in files) { tree.Files.Add(pair.Key, pair.Value); }
        return tree;
    }
}
