using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciRoutingAndStateTests
{
    [Test]
    public async Task AnExactShardBoundarySelectsTheRightIntervalWithoutReadingTheLeftPayloads()
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var result = await snapshot.ReadAsync(new(FederationOperation.Collection, "/emptygroups"));
        await Check.Json(result.Value, "{}");
        await Assert.That(result.SelectedXid).IsEqualTo("/emptygroups");
        await Assert.That(tree.Reads.Contains("blobs/sha256/7321cbe88ee2dca9c70fb3f4020cb6811676547a5f688befdc6f7e153a4b91c6")).IsFalse();
        await Assert.That(tree.Reads.Count).IsEqualTo(8);
    }

    [Test]
    [Arguments("duplicate")]
    [Arguments("reverse")]
    [Arguments("out-of-range")]
    [Arguments("empty-shard")]
    public async Task InvalidLeafKeysAndEmptyShardsCannotBecomeSuccessfulEnumeration(string mutation)
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Node(tree, "collection", "/dirs/main/files", node =>
        {
            var annotations = node["annotations"]!;
            if (annotations[GraphFixture.Prefix + "mode"]!.GetValue<string>() != "leaf") { return; }
            var entries = node["manifests"]!.AsArray();
            switch (mutation)
            {
                case "duplicate": entries.Add(entries[0]!.DeepClone()); break;
                case "reverse":
                    if (entries.Count > 1)
                    {
                        var first = entries[0]!.DeepClone();
                        entries[0] = entries[1]!.DeepClone();
                        entries[1] = first;
                    }
                    break;
                case "out-of-range": entries[0]!["annotations"]![GraphFixture.Prefix + "xid"] = "/dirs/main/files/z-last"; break;
                case "empty-shard": entries.Clear(); break;
            }
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Collection, "/dirs/main/files")).AsTask(),
            FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ARequiredMissingShardCannotBeReportedAsAUniqueLabelMatch()
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Node(tree, "collection", "/dirs/main/files", node =>
        {
            if (node["annotations"]![GraphFixture.Prefix + "mode"]!.GetValue<string>() != "branch") { return; }
            node["manifests"]![1]!["digest"] = "sha256:" + new string('a', 64);
            node["manifests"]![1]!["size"] = 1;
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Collection, "/dirs/main/files",
            new("stage", "Ready"))).AsTask(), FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("duplicate-case")]
    [Arguments("retention")]
    [Arguments("two-roots")]
    [Arguments("matchversions")]
    public async Task ExhaustiveValidationEnforcesCrossVersionAndCoreIdentityConstraints(string mutation)
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Rewrite(tree, (record, media) =>
        {
            if (media != GraphFixture.Config) { return GraphFixture.Encode(record); }
            var xid = record["entity"]!["xid"]!.GetValue<string>();
            if (xid == "/")
            {
                var source = record["modelresolved"]!;
                var resource = source["groups"]!["dirs"]!["resources"]!["files"]!;
                if (mutation == "retention") { resource["maxversions"] = 1; }
                if (mutation == "two-roots") { resource["singleversionroot"] = true; }
                if (mutation == "matchversions") { resource["attributes"]!["stable"] = new JsonObject { ["type"] = "string", ["matchversions"] = true }; }
                record["entity"]!["modelsource"] = source.DeepClone();
                record["entity"]!["model"] = JsonNode.Parse(RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString())).EffectiveModel.RootElement.GetRawText());
            }
            if (xid == OciDocumentTests.Sample + "/versions/v2" && mutation == "two-roots") { record["entity"]!["ancestorid"] = "v2"; }
            if (record["kind"]!.GetValue<string>() == "version" && xid.StartsWith(OciDocumentTests.Sample + "/", StringComparison.Ordinal) &&
                mutation == "matchversions") { record["entity"]!["stable"] = xid.EndsWith("/v1", StringComparison.Ordinal) ? "first" : "changed"; }
            return GraphFixture.Encode(record);
        });
        if (mutation == "duplicate-case")
        {
            GraphFixture.Node(tree, "collection", OciDocumentTests.Sample + "/versions", node =>
            {
                var edge = node["manifests"]![0]!.DeepClone();
                edge["annotations"]![GraphFixture.Prefix + "xid"] = OciDocumentTests.Sample + "/versions/V1";
                node["manifests"]!.AsArray().Insert(0, edge);
            });
        }
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Check.Error(() => snapshot.ValidateAsync().AsTask(), FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("R\u00c9SUM\u00c9", true)]
    [Arguments("re\u0301sume\u0301", false)]
    [Arguments("r*", false)]
    public async Task LabelValuesAreLiteralCaseInsensitiveAndNeverUnicodeNormalized(string value, bool matches)
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Record(tree, "/dirs/main", record => record["entity"]!["labels"]!["name"] = "r\u00e9sum\u00e9");
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var request = new FederationReadRequest(FederationOperation.Collection, "/dirs", new("name", value));
        if (matches)
        {
            var result = await snapshot.ReadAsync(request);
            await Assert.That(result.SelectedXid).IsEqualTo("/dirs/main");
        }
        else { await Check.Error(() => snapshot.ReadAsync(request).AsTask(), FederationErrorCode.NotFound); }
    }

    [Test]
    public async Task ConcurrentReadAndDisposalAreRejectedUntilTheOwningReadCompletes()
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.BeforeRead = async (_, cancellationToken) =>
        {
            tree.BeforeRead = null;
            opened.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        };
        var read = snapshot.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample)).AsTask();
        await opened.Task;
        try
        {
            await Assert.That(async () => { await snapshot.ReadAsync(new(FederationOperation.Model, "/")); }).Throws<InvalidOperationException>();
            await Assert.That(async () => { await snapshot.DisposeAsync(); }).Throws<InvalidOperationException>();
        }
        finally { release.SetResult(); }
        var result = await read;
        await Assert.That(result.Document!.Length).IsEqualTo(18L);
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    [Test]
    public async Task EmptyRegistryAndEmptyDocumentsMapProduceALegalCompleteSnapshot()
    {
        var root = AuthoredCapture.Registry(RegistryJson.Parse("{}"));
        var package = await OciSnapshotWriter.CreateAsync(new([root]));
        await Assert.That(package.Validation.Objects).IsEqualTo(5);
        await Assert.That(package.Validation.Documents).IsEqualTo(0);
        await using var snapshot = await OciSnapshot.OpenAsync(package.CreateReader(), package.RootDigest);
        var result = await snapshot.ReadAsync(new(FederationOperation.Entity, "/"));
        await Assert.That(result.Value.GetProperty("registryid").GetString()).IsEqualTo("authored");
        await Assert.That(result.Value.GetProperty("self").GetString()).IsEqualTo("#");
        await Assert.That(result.Value.GetProperty("modelsource").EnumerateObject().Count()).IsEqualTo(0);
    }
}
