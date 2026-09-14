using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Storage.File.Tests;

public class DocumentCommitTests
{
    [Test]
    public async Task BinaryAndEmptyDocumentsSurviveReopenExactly()
    {
        using var directory = new TestDirectory();
        var bytes = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        using var binary = new MemoryStream(bytes);
        using var empty = new MemoryStream([]);
        using (var store = LocalFileStore.Initialize(directory.Path))
        {
            using var candidate = await store.PrepareAsync(0,
            [
                StorageMutation.Put("binary", "{\"contenttype\":\"application/octet-stream\"}"u8.ToArray(), binary),
                StorageMutation.Put("empty", "{}"u8.ToArray(), empty),
                StorageMutation.Put("absent", "{}"u8.ToArray()),
            ]);
            await Assert.That(store.Commit(candidate)).IsEqualTo(1L);
            await Assert.That(binary.CanRead).IsTrue();
            await Assert.That(empty.CanRead).IsTrue();
        }

        using var reopened = LocalFileStore.Open(directory.Path);
        using var snapshot = reopened.ReadSnapshot();
        var binaryRecord = snapshot.Records.Single(record => record.Key == "binary");
        var emptyRecord = snapshot.Records.Single(record => record.Key == "empty");
        await Assert.That(binaryRecord.Document!.Sha256).IsEqualTo("40aff2e9d2d8922e47afd4648e6967497158785fbd1da870e7110266bf944880");
        await Assert.That(binaryRecord.Document.Length).IsEqualTo(256L);
        await Assert.That(emptyRecord.Document!.Sha256).IsEqualTo("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        await Assert.That(emptyRecord.Document.Length).IsEqualTo(0L);
        await Assert.That(snapshot.Records.Single(record => record.Key == "absent").Document).IsNull();

        using var binaryRead = snapshot.OpenDocument("binary");
        using var actual = new MemoryStream();
        await binaryRead.CopyToAsync(actual);
        await Assert.That(Convert.ToHexString(actual.ToArray())).IsEqualTo(Convert.ToHexString(bytes));
        using var emptyRead = snapshot.OpenDocument("empty");
        await Assert.That(emptyRead.Length).IsEqualTo(0L);
        await Assert.That(emptyRead.ReadByte()).IsEqualTo(-1);
        await Assert.That(() => snapshot.OpenDocument("absent")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task StaleCandidateDoesNotPublishDocumentAndDisposalRemovesStaging()
    {
        using var directory = new TestDirectory();
        using var store = LocalFileStore.Initialize(directory.Path);
        using var body = new MemoryStream([1, 2, 3]);
        using var stale = await store.PrepareAsync(0, [StorageMutation.Put("stale", "{}"u8.ToArray(), body)]);
        using var first = await store.PrepareAsync(0, [StorageMutation.Put("current", "{}"u8.ToArray())]);
        await Assert.That(store.Commit(first)).IsEqualTo(1L);

        await Assert.That(() => store.Commit(stale)).Throws<StorageException>();
        stale.Dispose();

        using var snapshot = store.ReadSnapshot();
        await Assert.That(snapshot.Records.Count).IsEqualTo(1);
        await Assert.That(snapshot.Records[0].Key).IsEqualTo("current");
        await Assert.That(Directory.GetFiles(Path.Combine(directory.Path, "blobs")).Length).IsEqualTo(0);
        await Assert.That(Directory.GetFiles(Path.Combine(directory.Path, "staging")).Length).IsEqualTo(0);
    }

    [Test]
    public async Task SameDigestIsDeduplicatedWithoutLosingReferences()
    {
        using var directory = new TestDirectory();
        using var store = LocalFileStore.Initialize(directory.Path);
        using var first = new MemoryStream([1, 2, 3]);
        using var second = new MemoryStream([1, 2, 3]);
        using var candidate = await store.PrepareAsync(0,
        [
            StorageMutation.Put("one", "{}"u8.ToArray(), first),
            StorageMutation.Put("two", "{}"u8.ToArray(), second)
        ]);
        store.Commit(candidate);
        using var snapshot = store.ReadSnapshot();

        await Assert.That(snapshot.Records.Count).IsEqualTo(2);
        await Assert.That(snapshot.Records[0].Document).IsEqualTo(snapshot.Records[1].Document);
        await Assert.That(Directory.GetFiles(Path.Combine(directory.Path, "blobs")).Length).IsEqualTo(1);
        await Assert.That(Directory.GetFiles(Path.Combine(directory.Path, "staging")).Length).IsEqualTo(0);
    }

    [Test]
    public async Task SnapshotAndStreamLeasesSurviveReplacementCollectionAndStoreDisposal()
    {
        using var directory = new TestDirectory();
        using var store = LocalFileStore.Initialize(directory.Path);
        using var body = new MemoryStream([9, 8, 7]);
        using var initial = await store.PrepareAsync(0, [StorageMutation.Put("key", "{}"u8.ToArray(), body)]);
        store.Commit(initial);
        using var snapshot = store.ReadSnapshot();
        using var retained = snapshot.OpenDocument("key");
        using var removal = await store.PrepareAsync(1, [StorageMutation.Delete("key")]);
        store.Commit(removal);

        await Assert.That(store.CollectOrphans()).IsEqualTo(0);
        snapshot.Dispose();
        await Assert.That(store.CollectOrphans()).IsEqualTo(0);
        store.Dispose();
        using var bytes = new MemoryStream();
        await retained.CopyToAsync(bytes);
        await Assert.That(Convert.ToHexString(bytes.ToArray())).IsEqualTo("090807");
        retained.Dispose();

        using var reopened = LocalFileStore.Open(directory.Path);
        await Assert.That(reopened.CollectOrphans()).IsEqualTo(1);
        await Assert.That(Directory.GetFiles(Path.Combine(directory.Path, "blobs")).Length).IsEqualTo(0);
    }

    [Test]
    public async Task CorruptOrMissingCommittedBytesAreNeverReturnedAsEmptyDocuments()
    {
        using var directory = new TestDirectory();
        using (var store = LocalFileStore.Initialize(directory.Path))
        {
            using var body = new MemoryStream([1, 2, 3]);
            using var candidate = await store.PrepareAsync(0, [StorageMutation.Put("key", "{}"u8.ToArray(), body)]);
            store.Commit(candidate);
        }

        var blob = Directory.GetFiles(Path.Combine(directory.Path, "blobs")).Single();
        System.IO.File.WriteAllBytes(blob, [9, 9, 9]);
        await Assert.That(() => LocalFileStore.Open(directory.Path)).Throws<StorageException>();
        System.IO.File.Delete(blob);
        await Assert.That(() => LocalFileStore.Open(directory.Path)).Throws<StorageException>();
        await Assert.That(System.IO.File.Exists(Path.Combine(directory.Path, "store.ready"))).IsTrue();
    }

    [Test]
    public async Task DocumentAndTemporaryBudgetsAreInclusiveAndFailuresCleanStaging()
    {
        using var directory = new TestDirectory();
        using var store = LocalFileStore.Initialize(directory.Path, new FileStoreLimits
        {
            MaxDocumentBytes = 3,
            MaxTemporaryBytes = 3
        });
        using var exact = new MemoryStream([1, 2, 3]);
        using (var candidate = await store.PrepareAsync(0, [StorageMutation.Put("exact", "{}"u8.ToArray(), exact)]))
        {
            store.Commit(candidate);
        }

        using var above = new MemoryStream([1, 2, 3, 4]);
        await Assert.That(async () =>
        {
            using var rejected = await store.PrepareAsync(1, [StorageMutation.Put("above", "{}"u8.ToArray(), above)]);
        }).Throws<StorageException>();
        using var snapshot = store.ReadSnapshot();
        await Assert.That(snapshot.Generation).IsEqualTo(1L);
        await Assert.That(snapshot.Records.Single().Key).IsEqualTo("exact");
        await Assert.That(Directory.GetFiles(Path.Combine(directory.Path, "staging")).Length).IsEqualTo(0);
    }

    [Test]
    public async Task CancelledPreparationLeavesBorrowedStreamsOpenAndNoPublishedState()
    {
        using var directory = new TestDirectory();
        using var store = LocalFileStore.Initialize(directory.Path);
        using var cancellation = new CancellationTokenSource();
        using var body = new CancelAfterRead(cancellation);
        await Assert.That(async () =>
        {
            using var candidate = await store.PrepareAsync(0,
                [StorageMutation.Put("key", "{}"u8.ToArray(), body)], cancellation.Token);
        }).Throws<OperationCanceledException>();
        using var snapshot = store.ReadSnapshot();
        await Assert.That(snapshot.Generation).IsEqualTo(0L);
        await Assert.That(snapshot.Records.Count).IsEqualTo(0);
        await Assert.That(body.CanRead).IsTrue();
        await Assert.That(Directory.GetFiles(Path.Combine(directory.Path, "staging")).Length).IsEqualTo(0);
    }

    [Test]
    public async Task OrphanCollectionNeverDeletesForeignFiles()
    {
        using var directory = new TestDirectory();
        using var store = LocalFileStore.Initialize(directory.Path);
        var foreign = Path.Combine(directory.Path, "blobs", "operator-notes.txt");
        System.IO.File.WriteAllText(foreign, "retain this evidence");

        await Assert.That(store.CollectOrphans()).IsEqualTo(0);
        await Assert.That(System.IO.File.ReadAllText(foreign)).IsEqualTo("retain this evidence");
    }

    private sealed class CancelAfterRead(CancellationTokenSource cancellation) : MemoryStream(new byte[] { 1, 2, 3 })
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = await base.ReadAsync(buffer, cancellationToken);
            cancellation.Cancel();
            return result;
        }
    }
}
