using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciContextTests
{
    [Test]
    [Arguments("/dirs/main/files/sample/versions/v2")]
    [Arguments("/imports/shared/files/alias/versions/v2")]
    public async Task FederationExternalDocumentsUseTheSharedDescriptorWithoutChangingNativeReads(string target)
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "linked");
        var request = new FederationReadRequest(FederationOperation.Document, target);
        var result = await ((IFederationReadSource)snapshot).ReadAsync(request);
        await Check.Json(result.ExternalDocument, """
            {"kind":"external","uri":"https://documents.example.org/sample-v2.json"}
            """);
        await Assert.That(result.SelectedXid).IsEqualTo("/dirs/main/files/sample/versions/v2");
        await Assert.That(result.Context.Revision).IsEqualTo(OciBootstrapTests.Linked);
        await Assert.That(result.Context.Source).IsEqualTo("file:///fixtures/oci/");
        await Assert.That(result.Document).IsNull();
        await Assert.That(result.Metadata.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Undefined);
        await Assert.That(tree.Reads.Contains(
            "blobs/sha256/44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a")).IsFalse();
        await Check.Error(() => snapshot.ReadAsync(request).AsTask(), FederationErrorCode.Unavailable);
        var nativeDescriptor = await snapshot.ReadExternalDocumentDescriptorAsync(target);
        await Check.Json(nativeDescriptor.ExternalDocument, """
            {"mode":"external","url":"https://documents.example.org/sample-v2.json","contenttype":"application/schema+json"}
            """);
        await Assert.That(nativeDescriptor.Target).IsEqualTo(target);
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
        await snapshot.DisposeAsync();
        await Check.Json(result.ExternalDocument, """
            {"kind":"external","uri":"https://documents.example.org/sample-v2.json"}
            """);
    }

    [Test]
    public async Task FederationAcquisitionUnavailableIsNotRetriedAsAnExternalDescriptor()
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "linked");
        var reads = tree.Reads.Count;
        var unavailable = new FederationException(FederationErrorCode.Unavailable, "Injected acquisition failure.");
        tree.BeforeRead = (_, _) => ValueTask.FromException(unavailable);
        var error = await Check.Error(() => ((IFederationReadSource)snapshot).ReadAsync(
            new(FederationOperation.Document, "/dirs/main/files/sample/versions/v2")).AsTask(), FederationErrorCode.Unavailable);
        await Assert.That(ReferenceEquals(error, unavailable)).IsTrue();
        await Assert.That(tree.Reads.Count).IsEqualTo(reads + 1);
    }

    [Test]
    public async Task ExplicitExternalDescriptorRetainsSnapshotAndResolvedIdentityWithoutAcquisition()
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "linked");
        var result = await snapshot.ReadExternalDocumentDescriptorAsync("/imports/shared/files/alias/versions/v2");
        await Assert.That(result.Target).IsEqualTo("/imports/shared/files/alias/versions/v2");
        await Assert.That(result.SelectedXid).IsEqualTo(OciDocumentTests.Sample + "/versions/v2");
        await Assert.That(result.Context.Revision).IsEqualTo(OciBootstrapTests.Linked);
        await Assert.That(result.Document).IsNull();
        await Check.Json(result.ExternalDocument, """
            {"mode":"external","url":"https://documents.example.org/sample-v2.json","contenttype":"application/schema+json"}
            """);
        await Assert.That(tree.Reads.All(p => p is "oci-layout" or "index.json" || p.StartsWith("blobs/sha256/", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task EmbeddedBaseAndOriginAreNotSnapshotLocatorsAndOpaqueBytesRemainUnchanged()
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Record(tree, OciDocumentTests.Sample + "/versions/v1", node =>
        {
            node["document"]!["base"] = "https://documents.example.org/original/";
            node["document"]!["origin"] = "https://documents.example.org/original/schema.json";
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var result = await snapshot.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample));
        await Assert.That(result.Document!.Base).IsEqualTo("https://documents.example.org/original/");
        await Assert.That(result.Document.Origin).IsEqualTo("https://documents.example.org/original/schema.json");
        await Assert.That(result.Context.Source).IsEqualTo("file:///fixtures/oci/");
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(result.Document))).IsEqualTo("{\"type\":\"string\"}\n");
    }

    [Test]
    public async Task FederationSourceMaterializesCompleteEnvelopesRatherThanExposingStorageConfigs()
    {
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(MemoryLayout.Load(), "offline");
        IFederationReadSource source = snapshot;
        var collection = await source.ReadAsync(new(FederationOperation.Collection, "/dirs/main/notes"));
        await Assert.That(collection.Metadata.GetProperty("complete").GetBoolean()).IsTrue();
        var entity = collection.Metadata.GetProperty("entities").GetProperty("info");
        await Assert.That(entity.GetProperty("self").GetString()).IsEqualTo("#/entities/info");
        await Assert.That(entity.TryGetProperty("formatversion", out _)).IsFalse();
        var selectedMeta = await source.ReadAsync(new(FederationOperation.Entity, OciDocumentTests.Sample + "/meta"));
        var meta = selectedMeta.Metadata.GetProperty("entity");
        await Assert.That(meta.GetProperty("xid").GetString()).IsEqualTo(OciDocumentTests.Sample + "/meta");
        var version = OciMetadataTests.Resolve(selectedMeta.Metadata, meta.GetProperty("defaultversionurl").GetString()!);
        await Assert.That(version.GetProperty("versionid").GetString()).IsEqualTo("v1");
    }

    [Test]
    [Arguments("bytes", 53_021, false)]
    [Arguments("bytes", 53_020, true)]
    [Arguments("objects", 5, false)]
    [Arguments("objects", 4, true)]
    [Arguments("requests", 5, false)]
    [Arguments("requests", 4, true)]
    [Arguments("object-size", 50_423, false)]
    [Arguments("object-size", 50_422, true)]
    public async Task BootstrapBudgetsHaveInclusiveExactLimits(string bound, int limit, bool exceeds)
    {
        var budget = new FederationReadBudget(new(
            maxObjectBytes: bound == "object-size" ? limit : 16 * 1024 * 1024,
            maxTotalBytes: bound == "bytes" ? limit : 64 * 1024 * 1024,
            maxRequests: bound == "requests" ? limit : 4096,
            maxObjects: bound == "objects" ? limit : 4096));
        var tree = MemoryLayout.Load();
        if (exceeds)
        {
            await Check.Error(() => OciSnapshot.OpenLayoutAsync(tree, "offline", budget).AsTask(), FederationErrorCode.LimitExceeded);
        }
        else
        {
            await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline", budget);
            await Assert.That(budget.BytesRead).IsEqualTo(53_021L);
            await Assert.That(budget.Requests).IsEqualTo(5L);
            await Assert.That(budget.ObjectsRead).IsEqualTo(5L);
        }
    }

    [Test]
    public async Task WorkDepthAndResultExhaustionNeverReturnPartialSuccess()
    {
        await Check.Error(() => OciSnapshot.OpenLayoutAsync(MemoryLayout.Load(), "offline",
            new(new(maxWork: 1))).AsTask(), FederationErrorCode.LimitExceeded);
        await using var depth = await OciSnapshot.OpenLayoutAsync(MemoryLayout.Load(), "offline", new(new(maxDepth: 2)));
        await Check.Error(() => depth.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample)).AsTask(),
            FederationErrorCode.LimitExceeded);
        await using var result = await OciSnapshot.OpenLayoutAsync(MemoryLayout.Load(), "offline", new(new(maxResultBytes: 17)));
        await Check.Error(() => result.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample)).AsTask(),
            FederationErrorCode.LimitExceeded);
        await Check.Error(() => result.ReadAsync(new(FederationOperation.Collection, "/dirs")).AsTask(),
            FederationErrorCode.LimitExceeded);
    }

    [Test]
    public async Task CancellationReentryAndDisposalKeepOwnershipAndAllowLaterSequentialReads()
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        using var cancellation = new CancellationTokenSource();
        tree.BeforeRead = async (_, _) =>
        {
            tree.BeforeRead = null;
            await Assert.That(async () => { await snapshot.ReadAsync(new(FederationOperation.Model, "/")); }).Throws<InvalidOperationException>();
            await Assert.That(() => snapshot.DisposeAsync().AsTask()).Throws<InvalidOperationException>();
            cancellation.Cancel();
        };
        await Assert.That(async () =>
        {
            await snapshot.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample),
            cancellation.Token);
        }).Throws<OperationCanceledException>();
        var result = await snapshot.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample));
        await snapshot.DisposeAsync();
        await Assert.That(async () => { await snapshot.ReadAsync(new(FederationOperation.Model, "/")); }).Throws<ObjectDisposedException>();
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(result.Document!))).IsEqualTo("{\"type\":\"string\"}\n");
        await using var first = result.Document!.OpenRead();
        await using var second = result.Document.OpenRead();
        await Assert.That(first.ReadByte()).IsEqualTo((int)'{');
        await Assert.That(second.Position).IsEqualTo(0L);
        await Assert.That(first.CanWrite).IsFalse();
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    [Test]
    public async Task ChangedAcquisitionContextFailsEvenWhenTheReadWouldUseOnlyCachedMetadata()
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        tree.Context = new("file", "file:///different/root/");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Model, "/")).AsTask(),
            FederationErrorCode.InconsistentSnapshot);
        await Assert.That(tree.Reads.Count).IsEqualTo(5);
    }

    [Test]
    public async Task ProducerResolutionSignalUsesTheActualFieldAndDoesNotCauseCatalogRetraversal()
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Record(tree, "/", node =>
            node["entity"]!["capabilities"]!["federation"] = new JsonObject { ["resolution"] = "producer" });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Assert.That(FederationCapabilities.GetResolutionOwner(snapshot.Capabilities)).IsEqualTo(FederationResolutionOwner.Producer);
        var count = tree.Reads.Count;
        var result = await snapshot.ReadAsync(new(FederationOperation.Capabilities, "/"));
        await Assert.That(result.Value.GetProperty("federation").GetProperty("resolution").GetString()).IsEqualTo("producer");
        await Assert.That(tree.Reads.Count).IsEqualTo(count);
    }
}
