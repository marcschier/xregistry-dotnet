// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Publication;

namespace XRegistry.Publication.Tests;

public class PublicationOpenRecordTests
{
    private static readonly byte[] ManifestBytes = """
        {
          "format": "xregistry-publication/1",
          "scope": "urn:example:release",
          "capture": "assembled",
          "completeness": "offline-complete",
          "registry": {
            "reference": {
              "registry": {
                "binding": "http",
                "endpoint": "https://publisher.example.test"
              },
              "version": "/publications/team/objects/registry/versions/content"
            },
            "digest": "sha256:d7e50157645e98260f0fa754e6940c59ab5b4f2d36e79b8fa3b08130b4e7a3e6",
            "size": 790
          },
          "documents": []
        }
        """u8.ToArray();

    [Test]
    public async Task ValidatesTheOpenRecordAgainstItsExactManifestAndChannelHead()
    {
        var manifest = PublicationManifest.Parse(ManifestBytes);
        var snapshot = SnapshotDescriptor(manifest);
        var openJson = CreateOpenRecord(manifest, snapshot);
        var open = PublicationOpenRecord.Parse(openJson);

        open.ValidateAgainstManifest(manifest);
        open.ValidateSelectedHead(snapshot);
        await Assert.That(open.Scope).IsEqualTo(manifest.Scope);
        await Assert.That(open.Completeness).IsEqualTo(manifest.Completeness);
        await Assert.That(open.Channel!.Epoch).IsEqualTo(new System.Numerics.BigInteger(12));
        await Assert.That(open.Channel.Resource).IsEqualTo("/publications/team/channels/stable");
    }

    [Test]
    public async Task RejectsOpenRecordsThatMismatchManifestOrSelectedHead()
    {
        var manifest = PublicationManifest.Parse(ManifestBytes);
        var snapshot = SnapshotDescriptor(manifest);
        var json = JsonNode.Parse(CreateOpenRecord(manifest, snapshot))!.AsObject();
        json["scope"] = "urn:example:other";
        var mismatchedOpen = PublicationOpenRecord.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
        var contextError = await Assert.That(() => mismatchedOpen.ValidateAgainstManifest(manifest))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected scope mismatch.");
        await Assert.That(contextError.Code).IsEqualTo("open_context_mismatch");

        var wrongHead = snapshot with { Digest = "sha256:" + new string('0', 64) };
        var headError = await Assert.That(() => PublicationOpenRecord.Parse(CreateOpenRecord(manifest, snapshot))
                .ValidateSelectedHead(wrongHead))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected selected head mismatch.");
        await Assert.That(headError.Code).IsEqualTo("open_head_mismatch");
    }

    [Test]
    public async Task RejectsSourceAccessContextThatDiffersFromItsPinnedSnapshotReference()
    {
        var sourceAccess = new PublicationRegistryAccess("http", "https://source.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        var snapshotAccess = new PublicationRegistryAccess("http", "https://other.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        var sourceSnapshot = new PublicationByteDescriptor(
            new PublicationVersionReference(snapshotAccess, "/publications/source/snapshots/pinned/versions/manifest"),
            "sha256:" + new string('a', 64), 1);
        var json = JsonNode.Parse(CreateOpenRecord(PublicationManifest.Parse(ManifestBytes),
            SnapshotDescriptor(PublicationManifest.Parse(ManifestBytes))))!.AsObject();
        json["sources"] = new JsonArray(new JsonObject
        {
            ["registry"] = new JsonObject
            {
                ["binding"] = sourceAccess.Binding,
                ["endpoint"] = sourceAccess.Endpoint
            },
            ["snapshot"] = new JsonObject
            {
                ["reference"] = new JsonObject
                {
                    ["registry"] = new JsonObject
                    {
                        ["binding"] = snapshotAccess.Binding,
                        ["endpoint"] = snapshotAccess.Endpoint
                    },
                    ["version"] = sourceSnapshot.Reference.Version
                },
                ["digest"] = sourceSnapshot.Digest,
                ["size"] = (long)sourceSnapshot.Size
            }
        });

        var error = await Assert.That(() =>
                PublicationOpenRecord.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected source context rejection.");

        await Assert.That(error.Code).IsEqualTo("source_context_mismatch");
    }

    [Test]
    public async Task RejectsNegativeEpochAndSnapshotIdentityMismatch()
    {
        var manifest = PublicationManifest.Parse(ManifestBytes);
        var snapshot = SnapshotDescriptor(manifest);
        var json = JsonNode.Parse(CreateOpenRecord(manifest, snapshot))!.AsObject();
        json["channel"]!["epoch"] = -1;
        var epochError = await Assert.That(() =>
                PublicationOpenRecord.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected negative epoch rejection.");
        await Assert.That(epochError.Code).IsEqualTo("invalid_integer");

        var wrongSnapshot = snapshot with
        {
            Reference = snapshot.Reference with
            {
                Version = "/publications/team/snapshots/" + new string('0', 64) + "/versions/manifest"
            }
        };
        var identityError = await Assert.That(() => manifest.ValidateSnapshotDescriptor(wrongSnapshot))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected Snapshot ID mismatch.");
        await Assert.That(identityError.Code).IsEqualTo("snapshot_identity_mismatch");
    }

    [Test]
    public async Task ReferenceComparisonDecodesXidsButPreservesVersionAndAccessContextIdentity()
    {
        var empty = new PublicationRegistryAccess("http", "https://publisher.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        var parameters = new PublicationRegistryAccess("http", "https://publisher.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        var encoded = new PublicationVersionReference(empty, "/groups/main/resources/item/versions/v%3A1");
        var decoded = new PublicationVersionReference(parameters, "/groups/main/resources/item/versions/v%3a1");
        var differentVersionCase = new PublicationVersionReference(empty, "/groups/main/resources/item/versions/V%3A1");

        await Assert.That(PublicationReferenceComparer.ReferencesEqual(encoded, decoded)).IsTrue();
        await Assert.That(PublicationReferenceComparer.ReferencesEqual(encoded, differentVersionCase)).IsFalse();
        await Assert.That(PublicationReferenceComparer.ResourceReferencesEqual(empty,
            "/publications/TEAM/channels/Stable", parameters, "/publications/team/channels/stable")).IsTrue();

        var otherEndpoint = new PublicationRegistryAccess("http", "https://publisher.example.test:443",
            new Dictionary<string, string>(StringComparer.Ordinal));
        await Assert.That(PublicationReferenceComparer.ReferencesEqual(encoded,
            new PublicationVersionReference(otherEndpoint, decoded.Version))).IsFalse();
    }

    [Test]
    public async Task GitReferenceComparisonUsesItsDefaultPathAndLowercaseCommitIdentity()
    {
        var noParameters = new PublicationRegistryAccess("git", "https://git.example.test/repo.git",
            new Dictionary<string, string>(StringComparer.Ordinal));
        var explicitDefaultPath = new PublicationRegistryAccess("git", "https://git.example.test/repo.git",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["path"] = "xregistry"
            });
        var upperOid = new PublicationRegistryAccess("git", "https://git.example.test/repo.git",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["path"] = "xregistry",
                ["revision"] = new string('A', 40)
            });
        var lowerOid = new PublicationRegistryAccess("git", "https://git.example.test/repo.git",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["path"] = "xregistry",
                ["revision"] = new string('a', 40)
            });
        var rootPath = new PublicationRegistryAccess("git", "https://git.example.test/repo.git",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["path"] = "",
                ["revision"] = new string('a', 40)
            });
        const string version = "/groups/main/resources/item/versions/v1";

        await Assert.That(PublicationReferenceComparer.ReferencesEqual(
            new(noParameters, version), new(explicitDefaultPath, version))).IsTrue();
        await Assert.That(PublicationReferenceComparer.ReferencesEqual(
            new(upperOid, version), new(lowerOid, version))).IsTrue();
        await Assert.That(PublicationReferenceComparer.ReferencesEqual(
            new(lowerOid, version), new(rootPath, version))).IsFalse();
    }

    private static PublicationByteDescriptor SnapshotDescriptor(PublicationManifest manifest)
    {
        var bytes = manifest.GetExactBytes();
        var access = new PublicationRegistryAccess("http", "https://publisher.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        return new PublicationByteDescriptor(
            new PublicationVersionReference(access,
                $"/publications/team/snapshots/{manifest.SnapshotId}/versions/manifest"),
            "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length);
    }

    private static byte[] CreateOpenRecord(PublicationManifest manifest, PublicationByteDescriptor snapshot)
    {
        var root = JsonNode.Parse(ManifestBytes)!.AsObject();
        var registry = root["registry"]!.DeepClone();
        var document = new JsonObject
        {
            ["format"] = "xregistry-open/1",
            ["scope"] = manifest.Scope,
            ["snapshot"] = new JsonObject
            {
                ["reference"] = new JsonObject
                {
                    ["registry"] = new JsonObject
                    {
                        ["binding"] = "http",
                        ["endpoint"] = "https://publisher.example.test"
                    },
                    ["version"] = snapshot.Reference.Version
                },
                ["digest"] = snapshot.Digest,
                ["size"] = checked((long)snapshot.Size)
            },
            ["registry"] = registry,
            ["completeness"] = manifest.Completeness,
            ["channel"] = new JsonObject
            {
                ["reference"] = new JsonObject
                {
                    ["registry"] = new JsonObject
                    {
                        ["binding"] = "http",
                        ["endpoint"] = "https://publisher.example.test"
                    },
                    ["resource"] = "/publications/team/channels/stable"
                },
                ["epoch"] = 12
            },
            ["sources"] = new JsonArray()
        };
        return Encoding.UTF8.GetBytes(document.ToJsonString());
    }

    [Test]
    [Arguments("binding")]
    [Arguments("endpoint")]
    [Arguments("parameter-key")]
    [Arguments("parameter-value")]
    [Arguments("parameter-missing")]
    [Arguments("parameter-extra")]
    public async Task SourceParsingRejectsEachIndependentlyDifferentAccessContextField(string field)
    {
        var json = SourceContextRecord();
        var access = json["sources"]![0]!["registry"]!;
        switch (field)
        {
            case "binding":
                access["binding"] = "git";
                break;
            case "endpoint":
                access["endpoint"] = "https://other.example.test";
                break;
            case "parameter-key":
                access["parameters"]!.AsObject().Remove("tenant");
                access["parameters"]!["Tenant"] = "blue";
                break;
            case "parameter-value":
                access["parameters"]!["tenant"] = "Blue";
                break;
            case "parameter-missing":
                access["parameters"]!.AsObject().Remove("tenant");
                break;
            case "parameter-extra":
                access["parameters"]!["extra"] = "value";
                break;
        }

        var error = await Assert.That(() =>
                PublicationOpenRecord.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected source context rejection.");

        await Assert.That(error.Code).IsEqualTo("source_context_mismatch");
        await Assert.That(error.Path).IsEqualTo("/sources/0/registry");
    }

    [Test]
    public async Task SourceParsingAcceptsExactAccessContextAndRetainsPinnedDescriptor()
    {
        var open = PublicationOpenRecord.Parse(Encoding.UTF8.GetBytes(SourceContextRecord().ToJsonString()));

        await Assert.That(open.Sources.Count).IsEqualTo(1);
        var source = open.Sources[0];
        await Assert.That(source.Registry.Binding).IsEqualTo("http");
        await Assert.That(source.Registry.Endpoint).IsEqualTo("https://source.example.test");
        await Assert.That(source.Registry.Parameters.Count).IsEqualTo(2);
        await Assert.That(source.Registry.Parameters["tenant"]).IsEqualTo("blue");
        await Assert.That(source.Registry.Parameters["region"]).IsEqualTo("west");
        await Assert.That(source.Snapshot.Reference.Registry.Binding).IsEqualTo("http");
        await Assert.That(source.Snapshot.Reference.Registry.Endpoint).IsEqualTo("https://source.example.test");
        await Assert.That(source.Snapshot.Reference.Registry.Parameters.Count).IsEqualTo(2);
        await Assert.That(source.Snapshot.Reference.Registry.Parameters["tenant"]).IsEqualTo("blue");
        await Assert.That(source.Snapshot.Reference.Registry.Parameters["region"]).IsEqualTo("west");
        await Assert.That(source.Snapshot.Reference.Version)
            .IsEqualTo("/publications/source/snapshots/pinned/versions/manifest");
        await Assert.That(source.Snapshot.Digest).IsEqualTo("sha256:" + new string('a', 64));
        await Assert.That(source.Snapshot.Size).IsEqualTo(new System.Numerics.BigInteger(37));
    }

    private static JsonObject SourceContextRecord()
    {
        var manifest = PublicationManifest.Parse(ManifestBytes);
        var json = JsonNode.Parse(CreateOpenRecord(manifest, SnapshotDescriptor(manifest)))!.AsObject();
        var access = new JsonObject
        {
            ["binding"] = "http",
            ["endpoint"] = "https://source.example.test",
            ["parameters"] = new JsonObject { ["tenant"] = "blue", ["region"] = "west" }
        };
        json["sources"] = new JsonArray(new JsonObject
        {
            ["registry"] = access.DeepClone(),
            ["snapshot"] = new JsonObject
            {
                ["reference"] = new JsonObject
                {
                    ["registry"] = access.DeepClone(),
                    ["version"] = "/publications/source/snapshots/pinned/versions/manifest"
                },
                ["digest"] = "sha256:" + new string('a', 64),
                ["size"] = 37
            }
        });
        return json;
    }
}
