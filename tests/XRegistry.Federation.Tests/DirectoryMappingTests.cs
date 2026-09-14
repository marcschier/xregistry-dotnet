using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class DirectoryMappingTests
{
    private const string Item = "/documents/main/assets/item";
    private const string Version = Item + "/versions/v1";

    [Test]
    public async Task FrozenVersionMetadataIsDetachedAndSelective()
    {
        var tree = MemoryTreeReader.Load();
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var result = await mapping.ReadAsync(new(FederationOperation.Entity, Version));

        await Check.Json(result.Metadata, """
            {"kind":"version","entity":{
              "epoch":1,"createdat":"2026-09-04T00:00:00Z","modifiedat":"2026-09-04T00:00:00Z",
              "xid":"/documents/main/assets/item/versions/v1","assetid":"item","versionid":"v1",
              "isdefault":true,"ancestorid":"v1","labels":{"stage":"production","note":""},
              "contenttype":"application/json","purpose":"primary","self":"#/entity"}}
            """);
        await Assert.That(string.Join(",", tree.Reads)).IsEqualTo(
            "registry.json,indexes/n3.json,records/n4.json,indexes/n4.json,records/n8.json,records/n9.json,indexes/n6.json,records/na.json");
        await Assert.That(tree.DisposedStreams).IsEqualTo(8);
        await mapping.DisposeAsync();
        await Assert.That(result.Metadata.GetProperty("entity").GetProperty("purpose").GetString()).IsEqualTo("primary");
    }

    [Test]
    [Arguments(Item, "7B2268656C6C6F223A22776F726C64227D0A", "v1")]
    [Arguments(Version, "7B2268656C6C6F223A22776F726C64227D0A", "v1")]
    [Arguments(Item + "/versions/v2", "", "v2")]
    [Arguments("/documents/main/assets/CON/versions/a:b@c.", "0001FF7F0A", "a:b@c.")]
    public async Task FrozenDefaultAndExplicitDocumentsKeepExactBytes(string target, string hex, string selectedVersion)
    {
        var tree = MemoryTreeReader.Load();
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var result = await mapping.ReadAsync(new(FederationOperation.Document, target));
        await Assert.That(result.SelectedXid).IsEqualTo(target.Contains("/versions/", StringComparison.Ordinal)
            ? target : target + "/versions/" + selectedVersion);
        await Assert.That(result.Document!.Length).IsEqualTo(hex.Length / 2L);
        await Assert.That(await ReadHex(result.Document)).IsEqualTo(hex);
        await mapping.DisposeAsync();
        await Assert.That(await ReadHex(result.Document)).IsEqualTo(hex);
        await Assert.That(tree.Reads.Count(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    [Test]
    public async Task MetadataOnlyDiffersFromEmptyBinary()
    {
        var tree = MemoryTreeReader.Load();
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Document,
            "/documents/main/notes/item")).AsTask(), FederationErrorCode.UnsupportedOperation);
        await Assert.That(string.Join(",", tree.Reads)).IsEqualTo("registry.json");
        var empty = await mapping.ReadAsync(new(FederationOperation.Document, Item + "/versions/v2"));
        await Assert.That(empty.Document!.Length).IsEqualTo(0L);
        await Assert.That(empty.Document.ContentType).IsEqualTo("application/octet-stream");
    }

    [Test]
    public async Task FrozenRootHashAndRevisionArePreserved()
    {
        var tree = MemoryTreeReader.Load();
        tree.Context = new("git", "https://git.test/repo", "0123456789abcdef0123456789abcdef01234567", true);
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var model = await mapping.ReadAsync(new(FederationOperation.Model, "/"));
        var capabilities = await mapping.ReadAsync(new(FederationOperation.Capabilities, "/"));
        await Assert.That(mapping.Context.RootSha256).IsEqualTo("7e4c37ca61b2b875ee055a8fc67e5c90c48b344689823a12dc1fb0a6e33232bf");
        await Assert.That(model.Context.Revision).IsEqualTo("0123456789abcdef0123456789abcdef01234567");
        await Assert.That(model.Metadata.GetProperty("modelsource").GetProperty("groups")
            .GetProperty("mirrors").GetProperty("ximportresources")[0].GetString()).IsEqualTo("/documents/assets");
        await Assert.That(FederationCapabilities.GetResolutionOwner(capabilities.Metadata))
            .IsEqualTo(FederationResolutionOwner.Consumer);
        await Assert.That(string.Join(",", tree.Reads)).IsEqualTo("registry.json");
    }

    [Test]
    public async Task MissingRootObjectAndXidAreDistinct()
    {
        await Check.Error(() => DirectoryMapping.OpenAsync(new MemoryTreeReader()).AsTask(),
            FederationErrorCode.NotFound);
        var tree = MemoryTreeReader.Load();
        tree.Files.Remove("documents/n1.bin");
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Document, Item)).AsTask(),
            FederationErrorCode.InvalidPackage);
        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Entity, "/documents/main/assets/ITEM")).AsTask(),
            FederationErrorCode.NotFound);
        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Entity, "/unknown/main")).AsTask(),
            FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task IntegrityPrecedesParsing()
    {
        var tree = MemoryTreeReader.Load();
        tree.Files["records/na.json"] = [0xff];
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Entity, Version)).AsTask(),
            FederationErrorCode.IntegrityError);
        await Assert.That(tree.Reads[^1]).IsEqualTo("records/na.json");
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    [Test]
    public async Task WalkthroughUsesExistingRootRelativeDocumentNames()
    {
        var tree = MemoryTreeReader.Load("Walkthrough");
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var result = await mapping.ReadAsync(new(FederationOperation.Document, "/documents/main/assets/widget"));
        await Assert.That(await ReadHex(result.Document!)).IsEqualTo("7B2274797065223A22737472696E67227D0A");
        await Assert.That(tree.Reads[^1]).IsEqualTo("source/specs/widget.json");
        await Assert.That(tree.Reads.Contains("source/specs/source/specs/widget.json")).IsFalse();
    }

    internal static async Task<string> ReadHex(FederationDocument document)
    {
        await using var stream = document.OpenRead();
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        return Convert.ToHexString(bytes.ToArray());
    }
}
