using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciGraphFailureTests
{
    [Test]
    [Arguments(256, false)]
    [Arguments(257, true)]
    public async Task LayoutIndexDescriptorCountHasAnExactInclusiveLimit(int count, bool exceeds)
    {
        var tree = MemoryLayout.Load();
        var index = JsonNode.Parse(tree.Files["index.json"])!;
        var entries = index["manifests"]!.AsArray();
        while (entries.Count < count)
        {
            var other = GraphFixture.Descriptor("/unrelated");
            other.Remove("annotations");
            other["artifactType"] = "application/example";
            entries.Add((JsonNode)other);
        }
        tree.Files["index.json"] = GraphFixture.Encode(index);
        if (exceeds)
        {
            await Check.Error(() => OciSnapshot.OpenLayoutAsync(tree, "offline").AsTask(), FederationErrorCode.LimitExceeded);
            await Assert.That(tree.Reads.Count).IsEqualTo(2);
        }
        else
        {
            await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
            await Assert.That(snapshot.RootDigest).IsEqualTo(OciBootstrapTests.Offline);
        }
    }

    [Test]
    [Arguments(256, FederationErrorCode.NotFound)]
    [Arguments(257, FederationErrorCode.LimitExceeded)]
    public async Task CollectionIndexCountIncludesEveryDescriptorWithoutTruncating(int count, FederationErrorCode expected)
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Node(tree, "collection", "/dirs", node =>
        {
            node["annotations"]![GraphFixture.Prefix + "mode"] = "leaf";
            node["annotations"]![GraphFixture.Prefix + "lower"] = "";
            node["annotations"]![GraphFixture.Prefix + "upper"] = "";
            node["manifests"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)
                GraphFixture.Descriptor("/dirs/a" + i.ToString("D3", CultureInfo.InvariantCulture))).ToArray());
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Entity, "/dirs/absent")).AsTask(), expected);
    }

    [Test]
    [Arguments(1_048_576, false)]
    [Arguments(1_048_577, true)]
    public async Task RootIndexLimitCountsExactUtf8IncludingWhitespaceAndMultibyteText(int size, bool exceeds)
    {
        var tree = MemoryLayout.Load();
        var original = Encoding.UTF8.GetString(tree.Files["blobs/sha256/" + OciBootstrapTests.Offline[7..]]);
        var json = original.Replace("\"annotations\": {", "\"annotations\": {\"example.text\":\"\u00e9\",", StringComparison.Ordinal);
        var encoded = Encoding.UTF8.GetBytes(json);
        var bytes = new byte[size];
        encoded.CopyTo(bytes, 0);
        bytes.AsSpan(encoded.Length).Fill((byte)' ');
        bytes[^1] = (byte)'\n';
        GraphFixture.RepointRoot(tree, bytes);
        await Assert.That(Encoding.UTF8.GetCharCount(bytes) < bytes.Length).IsTrue();
        if (exceeds)
        {
            await Check.Error(() => OciSnapshot.OpenLayoutAsync(tree, "offline").AsTask(), FederationErrorCode.LimitExceeded);
        }
        else
        {
            await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
            await Assert.That(snapshot.RootDigest).IsEqualTo(GraphFixture.Hash(bytes));
        }
    }

    [Test]
    [Arguments("schema", FederationErrorCode.InvalidPackage)]
    [Arguments("artifact", FederationErrorCode.InvalidPackage)]
    [Arguments("subject", FederationErrorCode.InvalidPackage)]
    [Arguments("annotation", FederationErrorCode.InvalidPackage)]
    [Arguments("annotation-role", FederationErrorCode.InvalidPackage)]
    [Arguments("version", FederationErrorCode.UnsupportedVersion)]
    [Arguments("kind", FederationErrorCode.UnsupportedBinding)]
    [Arguments("urls", FederationErrorCode.InvalidPackage)]
    [Arguments("data", FederationErrorCode.InvalidPackage)]
    [Arguments("platform", FederationErrorCode.InvalidPackage)]
    [Arguments("tag", FederationErrorCode.InvalidPackage)]
    [Arguments("control-order", FederationErrorCode.InvalidPackage)]
    [Arguments("control-count", FederationErrorCode.InvalidPackage)]
    [Arguments("edge-artifact", FederationErrorCode.InvalidPackage)]
    [Arguments("edge-xid", FederationErrorCode.InvalidPackage)]
    [Arguments("edge-kind", FederationErrorCode.UnsupportedBinding)]
    [Arguments("size999", FederationErrorCode.IntegrityError)]
    public async Task ProfileRulesRejectMalformedRootAndControlEdges(string mutation, FederationErrorCode code)
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Node(tree, "registry", "/", node =>
        {
            var edge = node["manifests"]![0]!;
            switch (mutation)
            {
                case "schema": node["schemaVersion"] = 1; break;
                case "artifact": node["artifactType"] = "application/wrong"; break;
                case "subject": node["subject"] = edge.DeepClone(); break;
                case "annotation": node["annotations"]![GraphFixture.Prefix + "unknown"] = "x"; break;
                case "annotation-role": node["annotations"]![GraphFixture.Prefix + "role"] = "unknown"; break;
                case "version": node["annotations"]![GraphFixture.Prefix + "version"] = "2"; break;
                case "kind": node["annotations"]![GraphFixture.Prefix + "kind"] = "future-kind"; break;
                case "urls": edge["urls"] = new JsonArray("https://unauthorized.invalid/"); break;
                case "data": edge["data"] = "e30="; break;
                case "platform": edge["platform"] = new JsonObject { ["os"] = "windows" }; break;
                case "tag": edge["annotations"]!["org.opencontainers.image.ref.name"] = "internal-tag"; break;
                case "control-order":
                    var first = node["manifests"]![0]!.DeepClone();
                    node["manifests"]![0] = node["manifests"]![1]!.DeepClone();
                    node["manifests"]![1] = first;
                    break;
                case "control-count": node["manifests"]!.AsArray().Add(edge.DeepClone()); break;
                case "edge-artifact": edge["artifactType"] = "application/wrong"; break;
                case "edge-xid": edge["annotations"]![GraphFixture.Prefix + "xid"] = "/dirs/main"; break;
                case "edge-kind": edge["annotations"]![GraphFixture.Prefix + "kind"] = "unknown"; break;
                case "size999": edge["size"] = 999; break;
                default: throw new InvalidOperationException("Unknown authored mutation.");
            }
        });
        await Check.Error(() => OciSnapshot.OpenLayoutAsync(tree, "offline").AsTask(), code);
    }

    [Test]
    [Arguments("gap")]
    [Arguments("overlap")]
    [Arguments("unsorted")]
    [Arguments("scope")]
    [Arguments("mixed")]
    [Arguments("target-bound")]
    public async Task InvalidRangePartitionsAreNotAbsence(string mutation)
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Node(tree, "collections", "/", node =>
        {
            if (node["annotations"]![GraphFixture.Prefix + "mode"]!.GetValue<string>() != "branch") { return; }
            var first = node["manifests"]![0]!;
            var second = node["manifests"]![1]!;
            switch (mutation)
            {
                case "gap": first["annotations"]![GraphFixture.Prefix + "upper"] = "/a"; break;
                case "overlap": second["annotations"]![GraphFixture.Prefix + "lower"] = "/a"; break;
                case "unsorted": node["manifests"]![0] = second.DeepClone(); break;
                case "scope": first["annotations"]![GraphFixture.Prefix + "upper"] = "/dirs/main/files"; break;
                case "mixed": first["annotations"]![GraphFixture.Prefix + "role"] = "collection"; break;
                case "target-bound":
                    first["annotations"]![GraphFixture.Prefix + "upper"] = "/g";
                    second["annotations"]![GraphFixture.Prefix + "lower"] = "/g";
                    break;
            }
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample)).AsTask(),
            FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task MissingUnvisitedPayloadFailsClosureNotSelectiveRead()
    {
        var tree = MemoryLayout.Load();
        tree.Files.Remove("blobs/sha256/" + GraphFixture.Binary);
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var result = await snapshot.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample));
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(result.Document!))).IsEqualTo("{\"type\":\"string\"}\n");
        await Check.Error(() => snapshot.ValidateAsync().AsTask(), FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("8b12b7aede7c57cede44697e325f2936960583fa02ef734194a493749872df06", false)]
    [Arguments(GraphFixture.RegistryConfig, false)]
    [Arguments(GraphFixture.RegistryConfig, true)]
    public async Task MissingAndCorruptRequiredBootstrapObjectsAreDistinct(string digest, bool corrupt)
    {
        var tree = MemoryLayout.Load();
        if (corrupt) { tree.Files["blobs/sha256/" + digest] = [0xff]; }
        else { tree.Files.Remove("blobs/sha256/" + digest); }
        await Check.Error(() => OciSnapshot.OpenLayoutAsync(tree, "offline").AsTask(),
            corrupt ? FederationErrorCode.IntegrityError : FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("duplicate")]
    [Arguments("utf8")]
    [Arguments("nan")]
    [Arguments("surrogate")]
    public async Task StrictJsonFailuresAreReportedAfterIndependentReaddressing(string invalid)
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Rewrite(tree, (node, media) =>
        {
            if (media != GraphFixture.Config || node["kind"]!.GetValue<string>() != "registry") { return GraphFixture.Encode(node); }
            return invalid switch
            {
                "duplicate" => """{"kind":"registry","kind":"registry"}"""u8.ToArray(),
                "utf8" => [0xff],
                "nan" => """{"epoch":NaN}"""u8.ToArray(),
                "surrogate" => """{"x":"\uD800"}"""u8.ToArray(),
                _ => throw new InvalidOperationException(),
            };
        });
        await Check.Error(() => OciSnapshot.OpenLayoutAsync(tree, "offline").AsTask(), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task MovingATagCannotChangeTheSelectedRootOrRepairMissingDescendants()
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var index = JsonNode.Parse(tree.Files["index.json"])!;
        index["manifests"]![0]!["digest"] = OciBootstrapTests.Linked;
        tree.Files["index.json"] = GraphFixture.Encode(index);
        var result = await snapshot.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample + "/versions/v2"));
        await Assert.That(result.Context.Revision).IsEqualTo(OciBootstrapTests.Offline);
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(result.Document!))).IsEqualTo("{\"type\":\"number\"}\n");
        tree.Files.Remove("blobs/sha256/" + GraphFixture.Binary);
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Document, "/dirs/main/files/binary")).AsTask(),
            FederationErrorCode.InvalidPackage);
        await Assert.That(tree.Reads.Count(p => p == "index.json")).IsEqualTo(1);
    }
}
