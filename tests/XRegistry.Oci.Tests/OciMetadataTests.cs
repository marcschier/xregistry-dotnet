// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciMetadataTests
{
    [Test]
    [Arguments("offline", 99, 4)]
    [Arguments("linked", 98, 3)]
    public async Task FrozenClosureMatchesIndependentInventory(string reference, int objects, int documents)
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, reference);
        var result = await snapshot.ValidateAsync();
        await Assert.That(result.Objects).IsEqualTo(objects);
        await Assert.That(result.Indexes).IsEqualTo(44);
        await Assert.That(result.Manifests).IsEqualTo(25);
        await Assert.That(result.Configs).IsEqualTo(25);
        await Assert.That(result.Documents).IsEqualTo(documents);
        await Assert.That(result.RootDigest).IsEqualTo(reference == "offline" ? OciBootstrapTests.Offline : OciBootstrapTests.Linked);
        await Assert.That(tree.Reads.Count).IsEqualTo(objects + 2);
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    [Test]
    public async Task StandaloneMetaPointersResolveInsideReturnedDocument()
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var result = await snapshot.ReadAsync(new(FederationOperation.Entity, OciDocumentTests.Sample + "/meta"));
        await Assert.That(result.JsonPointer).IsEqualTo("/meta");
        await Check.Json(result.Value, """
            {
              "fileid":"sample","xid":"/dirs/main/files/sample","self":"#","metaurl":"#/meta",
              "meta":{"createdat":"2026-09-01T12:00:00Z","defaultversionid":"v1","defaultversionsticky":true,
                "defaultversionurl":"#/versions/v1","epoch":1,"fileid":"sample","modifiedat":"2026-09-01T12:00:00Z",
                "readonly":false,"self":"#/meta","xid":"/dirs/main/files/sample/meta"},
              "versions":{
                "v1":{"ancestorid":"v1","contenttype":"application/schema+json","createdat":"2026-09-01T12:00:00Z","epoch":1,
                  "extra":{"enabled":true,"nested":{"self":"extension data, not navigation"}},"fileid":"sample","isdefault":true,
                  "labels":{"empty":"","stage":"Ready"},"modifiedat":"2026-09-01T12:00:00Z","self":"#/versions/v1",
                  "versionid":"v1","xid":"/dirs/main/files/sample/versions/v1"},
                "v2":{"ancestorid":"v1","contenttype":"application/schema+json","createdat":"2026-09-01T12:00:00Z","epoch":1,
                  "fileid":"sample","isdefault":false,"modifiedat":"2026-09-01T12:00:00Z","self":"#/versions/v2",
                  "versionid":"v2","xid":"/dirs/main/files/sample/versions/v2"}},
              "versionscount":2,"versionsurl":"#/versions"
            }
            """);
        var selected = Resolve(result.Value, "#" + result.JsonPointer);
        var defaultVersion = Resolve(result.Value, selected.GetProperty("defaultversionurl").GetString()!);
        await Assert.That(defaultVersion.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(result.Value.TryGetProperty("labels", out _)).IsFalse();
        await Assert.That(result.Value.TryGetProperty("contenttype", out _)).IsFalse();
        await Assert.That(tree.Reads.Contains(
            "blobs/sha256/85803e087e684bdab3e5d6c2dd1af627da83382db625be9a42aea3d4d06539be")).IsFalse();
    }

    [Test]
    [Arguments("/dirs/main/files/alias", "/dirs/main/files/sample")]
    [Arguments("/dirs/main/files/dangling", "/dirs/missing/files/sample")]
    [Arguments("/dirs/main/files/chain", "/dirs/main/files/alias")]
    public async Task AliasMetadataRemainsUnexpandedEvenWhenUnresolvable(string target, string xref)
    {
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(MemoryLayout.Load(), "offline");
        var result = await snapshot.ReadAsync(new(FederationOperation.Entity, target));
        await Assert.That(result.Value.GetProperty("xid").GetString()).IsEqualTo(target);
        await Assert.That(result.Value.GetProperty("meta").GetProperty("xref").GetString()).IsEqualTo(xref);
        await Assert.That(string.Join(",", result.Value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)))
            .IsEqualTo("fileid,meta,metaurl,self,xid");
        await Assert.That(result.Value.GetProperty("meta").EnumerateObject().Count()).IsEqualTo(4);
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Entity, target + "/versions/v1")).AsTask(),
            FederationErrorCode.UnsupportedOperation);
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Collection, target + "/versions")).AsTask(),
            FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    public async Task RegistryViewPreservesImportedAndEmptyModelCollections()
    {
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(MemoryLayout.Load(), "offline");
        var result = await snapshot.ReadAsync(new(FederationOperation.Entity, "/"));
        await Assert.That(result.Value.GetProperty("registryid").GetString()).IsEqualTo("fixture-offline");
        await Assert.That(result.Value.GetProperty("emptygroupscount").GetInt32()).IsEqualTo(0);
        await Check.Json(result.Value.GetProperty("emptygroups"), "{}");
        var empty = result.Value.GetProperty("dirs").GetProperty("empty");
        await Check.Json(empty.GetProperty("files"), "{}");
        await Check.Json(empty.GetProperty("notes"), "{}");
        var imported = result.Value.GetProperty("imports").GetProperty("shared");
        await Assert.That(imported.GetProperty("filescount").GetInt32()).IsEqualTo(1);
        await Assert.That(imported.GetProperty("filesurl").GetString()).IsEqualTo("#/imports/shared/files");
        await Assert.That(Resolve(result.Value, "#/imports/shared/files/alias/meta").GetProperty("xref").GetString())
            .IsEqualTo("/dirs/main/files/sample");
        var model = await snapshot.ReadAsync(new(FederationOperation.Model, "/"));
        await Assert.That(model.Value.GetProperty("resolvedmodelsource").GetProperty("groups").GetProperty("imports")
            .GetProperty("ximportresources")[0].GetString()).IsEqualTo("/dirs/files");
    }

    [Test]
    public async Task CompleteLabelSelectionChecksLaterShardsAndDefaultLabels()
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Collection, "/dirs/main/files",
            new("stage", "rEaDy"))).AsTask(), FederationErrorCode.Ambiguous);
        var empty = await snapshot.ReadAsync(new(FederationOperation.Collection, "/dirs", new("empty", "")));
        await Assert.That(empty.SelectedXid).IsEqualTo("/dirs/main");
        var version = await snapshot.ReadAsync(new(FederationOperation.Collection, OciDocumentTests.Sample + "/versions",
            new("empty", "")));
        await Assert.That(version.SelectedXid).IsEqualTo(OciDocumentTests.Sample + "/versions/v1");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Collection, OciDocumentTests.Sample + "/versions",
            new("absent", ""))).AsTask(), FederationErrorCode.NotFound);
        await Assert.That(tree.Reads.Contains(
            "blobs/sha256/85803e087e684bdab3e5d6c2dd1af627da83382db625be9a42aea3d4d06539be")).IsFalse();
    }

    internal static JsonElement Resolve(JsonElement root, string pointer)
    {
        if (pointer == "#") { return root; }
        foreach (var token in pointer[2..].Split('/'))
        {
            root = root.GetProperty(token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        }
        return root;
    }
}
