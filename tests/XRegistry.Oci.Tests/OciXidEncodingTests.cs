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

public class OciXidEncodingTests
{
    private const string Resource = "/dirs/group:one/files/item@stable";
    private const string Version = Resource + "/versions/v:1";
    private const string EncodedVersion = "/dirs/group%3Aone/files/item%40stable/versions/v%3A1";
    private static readonly string[] RoutingAnnotations = ["xid", "lower", "upper"];
    private static readonly string[] VersionIdentifiers = ["versionid", "ancestorid", "defaultversionid"];
    private static readonly string[] OrderedGroupIds = ["a0", "a:one", "a1", "a2", "b@two", "c0", "d0", "d@four"];

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NoncanonicalRoutingKeysAndBoundsAreRejectedWithoutRewritingPinnedObjects(bool boundary)
    {
        var tree = MemoryLayout.Load();
        if (boundary)
        {
            SplitLeaf(tree, "/dirs", "/dirs/m%61");
        }
        else
        {
            GraphFixture.Node(tree, "collection", "/dirs", node =>
            {
                var entries = node["manifests"]!.AsArray();
                entries.Single(entry => entry!["annotations"]![GraphFixture.Prefix + "xid"]!.GetValue<string>() == "/dirs/main")!
                    ["annotations"]![GraphFixture.Prefix + "xid"] = "/dirs/m%61in";
            });
        }
        var original = tree.Files.ToDictionary(entry => entry.Key, entry => Convert.ToHexString(entry.Value), StringComparer.Ordinal);
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Check.Error(() => snapshot.ValidateAsync().AsTask(), FederationErrorCode.InvalidPackage);
        await Assert.That(tree.Files.Count).IsEqualTo(original.Count);
        await Assert.That(tree.Files.All(entry => Convert.ToHexString(entry.Value) == original[entry.Key])).IsTrue();
    }

    [Test]
    [Arguments("group%3aone")]
    [Arguments("Group%3Aone")]
    public async Task FullValidationRejectsSiblingUriAliasesEvenWhenTheirDescriptorsShareADigest(string duplicateId)
    {
        var tree = RenamedFixture(false);
        GraphFixture.Node(tree, "collection", "/dirs", node =>
        {
            var entries = node["manifests"]!.AsArray();
            var duplicate = entries.Single(entry => entry!["annotations"]![GraphFixture.Prefix + "xid"]!.GetValue<string>() == "/dirs/group%3Aone")!.DeepClone();
            duplicate["annotations"]![GraphFixture.Prefix + "xid"] = "/dirs/" + duplicateId;
            entries.Add(duplicate);
            node["manifests"] = new JsonArray(entries.OrderBy(entry =>
                entry!["annotations"]![GraphFixture.Prefix + "xid"]!.GetValue<string>(), StringComparer.Ordinal)
                .Select(entry => entry!.DeepClone()).ToArray());
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Check.Error(() => snapshot.ValidateAsync().AsTask(), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task DescriptorAndConfigUriSpellingsCanDifferWithoutChangingIdentityOrPins()
    {
        var tree = RenamedFixture(false);
        GraphFixture.Rewrite(tree, (node, media) =>
        {
            if (media == GraphFixture.Config)
            {
                node["entity"]!["xid"] = Encode(node["entity"]!["xid"]!.GetValue<string>());
                if (node["entity"]!["xref"] is JsonNode xref) { node["entity"]!["xref"] = Encode(xref.GetValue<string>()); }
            }
            return GraphFixture.Encode(node);
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var pin = snapshot.RootDigest;
        var result = await snapshot.ReadAsync(new(FederationOperation.Document, Version));
        await Assert.That(result.SelectedXid).IsEqualTo(EncodedVersion);
        await Assert.That(result.Context.Revision).IsEqualTo(pin);
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(result.Document!))).IsEqualTo("{\"type\":\"string\"}\n");
        var validation = await snapshot.ValidateAsync();
        await Assert.That(validation.RootDigest).IsEqualTo(pin);
        await Assert.That(validation.Configs).IsEqualTo(25);
    }

    [Test]
    public async Task ProducerCanonicalizesCoreXidsButNotDocumentBytesUrlsOrExtensionStrings()
    {
        var captured = GraphFixture.Capture(RenamedFixture(false));
        const string url = "https://documents.example.org/a%3ab/@literal?token=%25";
        for (var index = 0; index < captured.Records.Count; index++)
        {
            var record = captured.Records[index];
            if (record.RootElement.GetProperty("entity").GetProperty("xid").GetString() != Version) { continue; }
            var changed = JsonNode.Parse(record.RootElement.GetRawText())!;
            changed["document"]!["base"] = url;
            changed["document"]!["origin"] = url;
            changed["entity"]!["extra"]!["xid"] = "/opaque/%253A/not-a-Core-link";
            captured.Records[index] = RegistryJson.Parse(changed.ToJsonString());
        }
        var package = await OciSnapshotWriter.CreateAsync(new(captured.Records, captured.Documents), new() { MaxDescriptorsPerIndex = 2 });
        await using var snapshot = await OciSnapshot.OpenAsync(package.CreateReader(), package.RootDigest);
        var result = await snapshot.ReadAsync(new(FederationOperation.Document, Version));
        await Assert.That(result.SelectedXid).IsEqualTo(EncodedVersion);
        await Assert.That(result.Target).IsEqualTo(Version);
        await Assert.That(result.Document!.Base).IsEqualTo(url);
        await Assert.That(result.Document.Origin).IsEqualTo(url);
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(result.Document))).IsEqualTo("{\"type\":\"string\"}\n");
        var metadata = await snapshot.ReadAsync(new(FederationOperation.Entity, EncodedVersion));
        await Assert.That(metadata.Value.GetProperty("xid").GetString()).IsEqualTo(EncodedVersion);
        await Assert.That(metadata.Value.GetProperty("extra").GetProperty("xid").GetString()).IsEqualTo("/opaque/%253A/not-a-Core-link");
    }

    [Test]
    public async Task EquivalentInputXidSpellingsProduceTheSameCanonicalGraph()
    {
        var literal = GraphFixture.Capture(RenamedFixture(false));
        var encoded = GraphFixture.Capture(RenamedFixture(true));
        var first = await OciSnapshotWriter.CreateAsync(new(literal.Records, literal.Documents));
        var second = await OciSnapshotWriter.CreateAsync(new(encoded.Records, encoded.Documents));
        await Assert.That(first.RootDigest).IsEqualTo(second.RootDigest);
        await Assert.That(first.Validation.Configs).IsEqualTo(25);
    }

    [Test]
    [Arguments("group%3aone", "group:one")]
    [Arguments("Group%3Aone", "Group:one")]
    public async Task ProducerRejectsIdentityCollisionsHiddenByPercentSpelling(string duplicateXid, string duplicateId)
    {
        var captured = GraphFixture.Capture(RenamedFixture(false));
        var group = captured.Records.Single(record => record.RootElement.GetProperty("entity").GetProperty("xid").GetString() == "/dirs/group:one");
        var duplicate = JsonNode.Parse(group.RootElement.GetRawText())!;
        duplicate["entity"]!["xid"] = "/dirs/" + duplicateXid;
        duplicate["entity"]!["dirid"] = duplicateId;
        captured.Records.Add(RegistryJson.Parse(duplicate.ToJsonString()));
        await Check.Error(() => OciSnapshotWriter.CreateAsync(new(captured.Records, captured.Documents)).AsTask(),
            FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ProducerRejectsDocumentKeysAliasingTheSameVersion()
    {
        var captured = GraphFixture.Capture(RenamedFixture(false));
        captured.Documents.Add(EncodedVersion, captured.Documents[Version]);
        await Check.Error(() => OciSnapshotWriter.CreateAsync(new(captured.Records, captured.Documents)).AsTask(),
            FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task CanonicalUriKeysKeepBytewiseShardOrderingAndBoundedAliasLookup()
    {
        var records = AuthoredCapture.Groups(OrderedGroupIds.Length);
        for (var index = 0; index < OrderedGroupIds.Length; index++)
        {
            var record = JsonNode.Parse(records[index + 1].RootElement.GetRawText())!;
            record["entity"]!["itemid"] = OrderedGroupIds[index];
            record["entity"]!["xid"] = "/items/" + OrderedGroupIds[index];
            records[index + 1] = RegistryJson.Parse(record.ToJsonString());
        }
        var package = await OciSnapshotWriter.CreateAsync(new(records), new() { MaxDescriptorsPerIndex = 2 });
        var reader = new TraceReader(package.CreateReader());
        await using var snapshot = await OciSnapshot.OpenAsync(reader, package.RootDigest);
        var selected = await snapshot.ReadAsync(new(FederationOperation.Entity, "/items/a:one"));
        await Assert.That(selected.Value.GetProperty("itemid").GetString()).IsEqualTo("a:one");
        await Assert.That(selected.Value.GetProperty("xid").GetString()).IsEqualTo("/items/a%3Aone");
        await Assert.That(reader.Reads.Count < package.Validation.Objects).IsTrue();
        var leaves = new List<(string Digest, string[] Keys)>();
        foreach (var item in package.Objects.Where(item => item.MediaType == GraphFixture.Index))
        {
            using var stream = item.OpenRead();
            using var node = JsonDocument.Parse(stream);
            var annotations = node.RootElement.GetProperty("annotations");
            if (annotations.GetProperty(GraphFixture.Prefix + "kind").GetString() != "collection" ||
                annotations.GetProperty(GraphFixture.Prefix + "mode").GetString() != "leaf") { continue; }
            var keys = node.RootElement.GetProperty("manifests").EnumerateArray()
                .Select(entry => entry.GetProperty("annotations").GetProperty(GraphFixture.Prefix + "xid").GetString()!).ToArray();
            leaves.Add((item.Digest, keys));
            await Assert.That(string.Join(",", keys)).IsEqualTo(string.Join(",", keys.Order(StringComparer.Ordinal)));
            await Assert.That(keys.All(key => !key.Contains(':', StringComparison.Ordinal) && !key.Contains('@', StringComparison.Ordinal))).IsTrue();
        }
        await Assert.That(leaves.Count).IsEqualTo(4);
        var canonicalLeaf = leaves.Single(leaf => leaf.Keys.Contains("/items/a%3Aone", StringComparer.Ordinal)).Digest;
        var literalCandidateLeaf = leaves.Single(leaf => leaf.Keys.Contains("/items/a2", StringComparer.Ordinal)).Digest;
        await Assert.That(reader.Reads.Contains("manifest:" + canonicalLeaf)).IsTrue();
        await Assert.That(reader.Reads.Contains("manifest:" + literalCandidateLeaf)).IsFalse();
        await Assert.That(leaves.Count(leaf => reader.Reads.Contains("manifest:" + leaf.Digest))).IsEqualTo(1);
        await Assert.That(reader.Reads.Count(path => path.StartsWith("blob:", StringComparison.Ordinal))).IsEqualTo(2);
        var collection = await snapshot.ReadAsync(new(FederationOperation.Collection, "/items"));
        await Assert.That(collection.Value.GetProperty("a:one").GetProperty("self").GetString()).IsEqualTo("#/a:one");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LegacyNoncanonicalShardKeysRequireExplicitMigration(bool groupConstraint)
    {
        const string owner = "/dirs/%6dain/files/sample";
        const string target = owner + "/versions/%761";
        var tree = MemoryLayout.Load();
        GraphFixture.Rewrite(tree, (node, media) =>
        {
            if (groupConstraint && media == GraphFixture.Config)
            {
                var xid = node["entity"]!["xid"]!.GetValue<string>();
                if (xid == "/")
                {
                    var source = node["modelresolved"]!;
                    source["groups"]!["dirs"]!["attributes"] = new JsonObject { ["site"] = new JsonObject { ["type"] = "string" } };
                    source["groups"]!["dirs"]!["resources"]!["files"]!["attributes"]!["site"] = new JsonObject { ["type"] = "string" };
                    source["groups"]!["dirs"]!["constraints"] = new JsonObject { ["files.site"] = new JsonObject { ["equals"] = "site" } };
                    node["entity"]!["modelsource"] = source.DeepClone();
                    node["entity"]!["model"] = JsonNode.Parse(RegistryModel.Compile(
                        RegistryJson.Parse(source.ToJsonString())).EffectiveModel.RootElement.GetRawText());
                }
                if (xid == "/dirs/main") { node["entity"]!["site"] = "east"; }
                if (node["kind"]!.GetValue<string>() == "version" && xid.StartsWith("/dirs/main/files/", StringComparison.Ordinal))
                {
                    node["entity"]!["site"] = "east";
                }
            }
            RewriteXids(node, media, path => path.Replace("/dirs/main", "/dirs/%6dain", StringComparison.Ordinal)
                .Replace("/versions/v1", "/versions/%761", StringComparison.Ordinal));
            if (media == GraphFixture.Index &&
                node["annotations"]![GraphFixture.Prefix + "mode"]?.GetValue<string>() == "leaf")
            {
                node["manifests"] = new JsonArray(node["manifests"]!.AsArray().OrderBy(entry =>
                    entry!["annotations"]![GraphFixture.Prefix + "xid"]!.GetValue<string>(), StringComparer.Ordinal)
                    .Select(entry => entry!.DeepClone()).ToArray());
            }
            return GraphFixture.Encode(node);
        });
        SplitLeaf(tree, "/dirs", "/dirs/a");
        SplitLeaf(tree, owner + "/versions", owner + "/versions/a");
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Collection, "/dirs")).AsTask(),
            FederationErrorCode.InvalidPackage);
        await Check.Error(() => snapshot.ValidateAsync().AsTask(), FederationErrorCode.InvalidPackage);
        await Assert.That(new FederationReadRequest(FederationOperation.Document, target).Target).IsEqualTo(target);
        await Assert.That(tree.Reads.Contains("blobs/sha256/" + GraphFixture.Binary)).IsFalse();
    }

    [Test]
    [Arguments("/dirs/Group%3Aone/files/item%40stable/versions/v%3A1")]
    [Arguments("/dirs/group%3Aone/files/Item%40stable/versions/v%3A1")]
    [Arguments("/dirs/group%3Aone/files/item%40stable/versions/V%3A1")]
    public async Task EscapedSelectorIdentityRemainsCaseSensitive(string target)
    {
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(RenamedFixture(true), "offline");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Document, target)).AsTask(),
            FederationErrorCode.NotFound);
    }

    [Test]
    [Arguments(128, false)]
    [Arguments(128, true)]
    [Arguments(129, false)]
    [Arguments(129, true)]
    public async Task EncodedSelectorsKeepCoreDecodedAndEncodedComponentBounds(int length, bool escaped)
    {
        var records = AuthoredCapture.Groups(1);
        var record = JsonNode.Parse(records[1].RootElement.GetRawText())!;
        var id = new string('a', 128);
        record["entity"]!["itemid"] = id;
        record["entity"]!["xid"] = "/items/" + id;
        records[1] = RegistryJson.Parse(record.ToJsonString());
        var package = await OciSnapshotWriter.CreateAsync(new(records));
        var reader = new TraceReader(package.CreateReader());
        await using var snapshot = await OciSnapshot.OpenAsync(reader, package.RootDigest);
        var target = "/items/" + (escaped ? string.Concat(Enumerable.Repeat("%61", length)) : new string('a', length));
        if (length == 128)
        {
            var result = await snapshot.ReadAsync(new(FederationOperation.Entity, target));
            await Assert.That(result.Target).IsEqualTo(target);
            await Assert.That(result.SelectedXid).IsEqualTo("/items/" + id);
            await Assert.That(result.Value.GetProperty("itemid").GetString()).IsEqualTo(id);
        }
        else
        {
            var before = reader.Reads.Count;
            await Check.Error(async () => { await snapshot.ReadAsync(new(FederationOperation.Entity, target)); },
                FederationErrorCode.InvalidPackage);
            await Assert.That(reader.Reads.Count).IsEqualTo(before);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LiteralAndEscapedSelectorsResolveTheSameStoredColonAndAtIdentity(bool encodedGraph)
    {
        var tree = RenamedFixture(encodedGraph);
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var first = await snapshot.ReadAsync(new(FederationOperation.Document, Version));
        var second = await snapshot.ReadAsync(new(FederationOperation.Document, EncodedVersion));
        await Assert.That(first.Target).IsEqualTo(Version);
        await Assert.That(second.Target).IsEqualTo(EncodedVersion);
        await Assert.That(first.SelectedXid).IsEqualTo(encodedGraph ? EncodedVersion : Version);
        await Assert.That(second.SelectedXid).IsEqualTo(first.SelectedXid);
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(first.Document!))).IsEqualTo("{\"type\":\"string\"}\n");
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(second.Document!))).IsEqualTo("{\"type\":\"string\"}\n");
        await Assert.That(tree.Reads.Count(path => path.EndsWith(
            "85803e087e684bdab3e5d6c2dd1af627da83382db625be9a42aea3d4d06539be", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(tree.Reads.Count < 55).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MetadataUsesDecodedIdsAndLocalPointersButPreservesStoredXidSpelling(bool encodedGraph)
    {
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(RenamedFixture(encodedGraph), "offline");
        var result = await snapshot.ReadAsync(new(FederationOperation.Entity,
            "/dirs/group%3aone/files/item%40stable/%6deta"));
        await Assert.That(result.Target).IsEqualTo("/dirs/group%3aone/files/item%40stable/%6deta");
        await Assert.That(result.JsonPointer).IsEqualTo("/meta");
        await Assert.That(result.SelectedXid).IsEqualTo((encodedGraph ? Encode(Resource) : Resource) + "/meta");
        await Assert.That(result.Value.GetProperty("fileid").GetString()).IsEqualTo("item@stable");
        await Assert.That(result.Value.GetProperty("xid").GetString()).IsEqualTo(encodedGraph ? Encode(Resource) : Resource);
        var version = result.Value.GetProperty("versions").GetProperty("v:1");
        await Assert.That(version.GetProperty("versionid").GetString()).IsEqualTo("v:1");
        await Assert.That(version.GetProperty("xid").GetString()).IsEqualTo(encodedGraph ? EncodedVersion : Version);
        await Assert.That(version.GetProperty("self").GetString()).IsEqualTo("#/versions/v:1");
        await Assert.That(result.Value.GetProperty("meta").GetProperty("defaultversionurl").GetString()).IsEqualTo("#/versions/v:1");
        await Assert.That(OciMetadataTests.Resolve(result.Value, result.Value.GetProperty("meta")
            .GetProperty("defaultversionurl").GetString()!).GetProperty("versionid").GetString()).IsEqualTo("v:1");
        var collection = await snapshot.ReadAsync(new(FederationOperation.Collection, "/d%69rs/group%3Aone/%66iles"));
        await Assert.That(collection.Value.GetProperty("item@stable").GetProperty("fileid").GetString()).IsEqualTo("item@stable");
    }

    [Test]
    public async Task EscapedAliasSelectorsRetainSourceMetadataAndResolveOneDocumentHop()
    {
        const string alias = "/dirs/group%3aone/files/%61lias";
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(RenamedFixture(true), "offline");
        var metadata = await snapshot.ReadAsync(new(FederationOperation.Entity, alias));
        await Assert.That(metadata.Target).IsEqualTo(alias);
        await Assert.That(metadata.SelectedXid).IsEqualTo("/dirs/group%3Aone/files/alias");
        await Assert.That(metadata.Value.GetProperty("meta").GetProperty("xref").GetString()).IsEqualTo(Encode(Resource));
        await Assert.That(metadata.Value.TryGetProperty("versions", out _)).IsFalse();
        var target = alias + "/%76ersions/v%3a1";
        var document = await snapshot.ReadAsync(new(FederationOperation.Document, target));
        await Assert.That(document.Target).IsEqualTo(target);
        await Assert.That(document.SelectedXid).IsEqualTo(EncodedVersion);
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(document.Document!))).IsEqualTo("{\"type\":\"string\"}\n");
    }

    [Test]
    public async Task FederationEnvelopeRetainsStoredCollectionIdentityAndDecodedNavigation()
    {
        const string target = "/d%69rs/group%3aone/%66iles";
        var tree = RenamedFixture(true);
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        IFederationReadSource source = snapshot;
        var request = new FederationReadRequest(FederationOperation.Collection, target);
        var result = await source.ReadAsync(request);
        await Assert.That(request.Target).IsEqualTo(target);
        await Assert.That(result.SelectedXid).IsEqualTo("/dirs/group%3Aone/files");
        await Assert.That(result.Metadata.GetProperty("xid").GetString()).IsEqualTo("/dirs/group%3Aone/files");
        await Assert.That(result.Metadata.GetProperty("complete").GetBoolean()).IsTrue();
        var entities = result.Metadata.GetProperty("entities");
        await Assert.That(entities.EnumerateObject().Count()).IsEqualTo(6);
        var resource = entities.GetProperty("item@stable");
        await Assert.That(resource.GetProperty("fileid").GetString()).IsEqualTo("item@stable");
        await Assert.That(resource.GetProperty("xid").GetString()).IsEqualTo(Encode(Resource));
        await Assert.That(resource.GetProperty("self").GetString()).IsEqualTo("#/entities/item@stable");
        var version = OciMetadataTests.Resolve(result.Metadata, resource.GetProperty("meta").GetProperty("defaultversionurl").GetString()!);
        await Assert.That(version.GetProperty("xid").GetString()).IsEqualTo(EncodedVersion);
        await Assert.That(version.GetProperty("versionid").GetString()).IsEqualTo("v:1");
        await Assert.That(tree.Reads.Contains(
            "blobs/sha256/85803e087e684bdab3e5d6c2dd1af627da83382db625be9a42aea3d4d06539be")).IsFalse();
    }

    [Test]
    [Arguments("%2f")]
    [Arguments("%5c")]
    [Arguments("%25")]
    [Arguments("%253A")]
    [Arguments("%")]
    [Arguments("%GG")]
    [Arguments("%C0%AF")]
    [Arguments("%ED%A0%80")]
    public async Task EscapedSelectorsRejectSeparatorsDoubleDecodingAndInvalidEncoding(string encoded)
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var before = tree.Reads.Count;
        await Check.Error(async () =>
        {
            await snapshot.ReadAsync(new(FederationOperation.Document, "/dirs/main/files/item" + encoded + "/versions/v1"));
        }, FederationErrorCode.InvalidPackage);
        await Assert.That(tree.Reads.Count).IsEqualTo(before);
    }

    [Test]
    [Arguments("%2f")]
    [Arguments("%5c")]
    [Arguments("%25")]
    [Arguments("%253A")]
    [Arguments("%")]
    [Arguments("%GG")]
    [Arguments("%C0%AF")]
    [Arguments("%ED%A0%80")]
    public async Task MalformedStoredUriKeysFailBeforeAcquiringAnyCollectionMember(string encoded)
    {
        var tree = MemoryLayout.Load();
        string[] memberDigests = [];
        GraphFixture.Node(tree, "collection", "/dirs", node =>
        {
            var entries = node["manifests"]!.AsArray();
            memberDigests = entries.Select(entry => entry!["digest"]!.GetValue<string>()).ToArray();
            entries.Single(entry => entry!["annotations"]![GraphFixture.Prefix + "xid"]!.GetValue<string>() == "/dirs/main")!
                ["annotations"]![GraphFixture.Prefix + "xid"] = "/dirs/group" + encoded + "one";
            node["manifests"] = new JsonArray(entries.OrderBy(entry =>
                entry!["annotations"]![GraphFixture.Prefix + "xid"]!.GetValue<string>(), StringComparer.Ordinal)
                .Select(entry => entry!.DeepClone()).ToArray());
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Collection, "/dirs")).AsTask(),
            FederationErrorCode.InvalidPackage);
        await Assert.That(memberDigests.Length).IsEqualTo(2);
        foreach (var digest in memberDigests)
        {
            await Assert.That(tree.Reads.Contains("blobs/sha256/" + digest[7..])).IsFalse();
        }
    }

    [Test]
    public async Task EscapedSelectorRetainsRequestSpellingAndSelectsTheFrozenIdentity()
    {
        const string target = "/dirs/main/files/%73ample/versions/v1";
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var result = await snapshot.ReadAsync(new(FederationOperation.Document, target));
        await Assert.That(result.Target).IsEqualTo(target);
        await Assert.That(result.SelectedXid).IsEqualTo("/dirs/main/files/sample/versions/v1");
        await Assert.That(result.Context.Revision).IsEqualTo(OciBootstrapTests.Offline);
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(result.Document!))).IsEqualTo("{\"type\":\"string\"}\n");
        await Assert.That(tree.Reads[^1]).IsEqualTo(
            "blobs/sha256/85803e087e684bdab3e5d6c2dd1af627da83382db625be9a42aea3d4d06539be");
    }

    internal static MemoryLayout RenamedFixture(bool encoded)
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Rewrite(tree, (node, media) =>
        {
            RewriteXids(node, media, path => Map(path, media != GraphFixture.Config || encoded));
            if (media == GraphFixture.Config)
            {
                var entity = node["entity"]!.AsObject();
                if (entity["dirid"]?.GetValue<string>() == "main") { entity["dirid"] = "group:one"; }
                if (entity["fileid"]?.GetValue<string>() == "sample") { entity["fileid"] = "item@stable"; }
                foreach (var field in VersionIdentifiers)
                {
                    if (entity[field] is JsonNode value)
                    {
                        entity[field] = value.GetValue<string>().Replace("v1", "v:1", StringComparison.Ordinal)
                            .Replace("v2", "v:2", StringComparison.Ordinal);
                    }
                }
            }
            return GraphFixture.Encode(node);
        });
        return tree;
    }

    private static void RewriteXids(JsonObject node, string media, Func<string, string> map)
    {
        if (media == GraphFixture.Config)
        {
            var entity = node["entity"]!.AsObject();
            entity["xid"] = map(entity["xid"]!.GetValue<string>());
            if (entity["xref"] is JsonNode xref) { entity["xref"] = map(xref.GetValue<string>()); }
            return;
        }
        Annotations(node);
        if (node["manifests"] is JsonArray entries) { foreach (var edge in entries) { Annotations(edge!.AsObject()); } }
        if (node["config"] is JsonObject config) { Annotations(config); }
        if (node["layers"] is JsonArray layers) { foreach (var layer in layers) { Annotations(layer!.AsObject()); } }

        void Annotations(JsonObject value)
        {
            if (value["annotations"] is not JsonObject annotations) { return; }
            foreach (var name in RoutingAnnotations)
            {
                var key = GraphFixture.Prefix + name;
                if (annotations[key] is not JsonNode old) { continue; }
                var path = old.GetValue<string>();
                annotations[key] = path.Length == 0 ? path : map(path);
            }
        }
    }

    private static void SplitLeaf(MemoryLayout tree, string xid, string boundary)
    {
        GraphFixture.Node(tree, "collection", xid, node =>
        {
            var entries = node["manifests"]!.AsArray();
            var left = entries.Where(entry => string.CompareOrdinal(
                entry!["annotations"]![GraphFixture.Prefix + "xid"]!.GetValue<string>(), boundary) < 0).ToArray();
            var right = entries.Except(left).ToArray();
            if (node["annotations"]![GraphFixture.Prefix + "mode"]!.GetValue<string>() != "leaf" ||
                left.Length == 0 || right.Length == 0)
            {
                throw new InvalidOperationException("The independently authored partition needs two nonempty leaves.");
            }
            var lower = Shard(left, "", boundary);
            var upper = Shard(right, boundary, "");
            node["annotations"]![GraphFixture.Prefix + "mode"] = "branch";
            node["manifests"] = new JsonArray(lower, upper);

            JsonObject Shard(JsonNode?[] children, string lowerBound, string upperBound)
            {
                var shard = node.DeepClone();
                shard["annotations"]![GraphFixture.Prefix + "lower"] = lowerBound;
                shard["annotations"]![GraphFixture.Prefix + "upper"] = upperBound;
                shard["manifests"] = new JsonArray(children.Select(child => child!.DeepClone()).ToArray());
                var bytes = GraphFixture.Encode(shard);
                var digest = GraphFixture.Hash(bytes);
                tree.Files.Add("blobs/sha256/" + digest[7..], bytes);
                return new()
                {
                    ["mediaType"] = GraphFixture.Index,
                    ["artifactType"] = node["artifactType"]!.DeepClone(),
                    ["digest"] = digest,
                    ["size"] = bytes.LongLength,
                    ["annotations"] = new JsonObject
                    {
                        [GraphFixture.Prefix + "role"] = "shard",
                        [GraphFixture.Prefix + "xid"] = xid,
                        [GraphFixture.Prefix + "lower"] = lowerBound,
                        [GraphFixture.Prefix + "upper"] = upperBound,
                    },
                };
            }
        });
    }

    private static string Map(string path, bool encoded)
    {
        var changed = path.Replace("/dirs/main", "/dirs/group:one", StringComparison.Ordinal)
            .Replace("/files/sample", "/files/item@stable", StringComparison.Ordinal)
            .Replace("/versions/v1", "/versions/v:1", StringComparison.Ordinal)
            .Replace("/versions/v2", "/versions/v:2", StringComparison.Ordinal);
        return encoded ? Encode(changed) : changed;
    }

    private static string Encode(string path) => path == "/" ? "/" :
        "/" + string.Join('/', path[1..].Split('/').Select(Uri.EscapeDataString));

    private sealed class TraceReader(IOciObjectReader inner) : IOciObjectReader
    {
        internal List<string> Reads { get; } = [];
        public NativeRegistryContext Context => inner.Context;
        public ValueTask<OciObjectResponse?> OpenManifestAsync(string reference, CancellationToken cancellationToken = default)
        {
            Reads.Add("manifest:" + reference);
            return inner.OpenManifestAsync(reference, cancellationToken);
        }
        public ValueTask<OciObjectResponse?> OpenBlobAsync(string digest, CancellationToken cancellationToken = default)
        {
            Reads.Add("blob:" + digest);
            return inner.OpenBlobAsync(digest, cancellationToken);
        }
    }
}
