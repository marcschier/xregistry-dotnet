using System.Security.Cryptography;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Storage.File.Tests;

public class BackupAndIndexedReadTests
{
    [Test]
    public async Task IndexedReadsAreOrdinalAndTreatSqlSyntaxAsAnOpaqueKey()
    {
        using var directory = new TestDirectory();
        using var store = LocalFileStore.Initialize(directory.Path);
        using var candidate = await store.PrepareAsync(0,
        [
            StorageMutation.Put("A", """{"value":1}"""u8.ToArray()),
            StorageMutation.Put("a", """{"value":2}"""u8.ToArray()),
            StorageMutation.Put("' OR 1=1 --", """{"value":3}"""u8.ToArray())
        ]);
        store.Commit(candidate);

        using var upper = store.ReadSnapshot("A");
        using var lower = store.ReadSnapshot("a");
        using var sql = store.ReadSnapshot("' OR 1=1 --");
        using var missing = store.ReadSnapshot("missing");
        await Assert.That(upper.Records.Single().Metadata.GetProperty("value").GetInt32()).IsEqualTo(1);
        await Assert.That(lower.Records.Single().Metadata.GetProperty("value").GetInt32()).IsEqualTo(2);
        await Assert.That(sql.Records.Single().Metadata.GetProperty("value").GetInt32()).IsEqualTo(3);
        await Assert.That(missing.Generation).IsEqualTo(1L);
        await Assert.That(missing.Records.Count).IsEqualTo(0);
        await Assert.That(() => missing.OpenDocument("missing")).Throws<KeyNotFoundException>();
    }

    [Test]
    public async Task PointReadDoesNotOpenUnrelatedDocumentsButStillRejectsSelectedCorruption()
    {
        using var directory = new TestDirectory();
        using var store = LocalFileStore.Initialize(directory.Path);
        using var good = new MemoryStream([1, 2, 3]);
        using var other = new MemoryStream([4, 5, 6]);
        using var candidate = await store.PrepareAsync(0,
        [
            StorageMutation.Put("good", "{}"u8.ToArray(), good),
            StorageMutation.Put("other", "{}"u8.ToArray(), other)
        ]);
        store.Commit(candidate);
        var otherHash = Convert.ToHexString(SHA256.HashData(new byte[] { 4, 5, 6 })).ToLowerInvariant();
        System.IO.File.Delete(Path.Combine(directory.Path, "blobs", otherHash + ".blob"));

        using var point = store.ReadSnapshot("good");
        await Assert.That(point.Records.Single().Key).IsEqualTo("good");
        using var content = point.OpenDocument("good");
        await Assert.That(content.ReadByte()).IsEqualTo(1);
        await Assert.That(() => store.ReadSnapshot("other")).Throws<StorageException>();
        await Assert.That(() => store.ReadSnapshot()).Throws<StorageException>();
    }

    [Test]
    public async Task PointSnapshotPinsItsDocumentAcrossReplacementAndCollection()
    {
        using var directory = new TestDirectory();
        using var store = LocalFileStore.Initialize(directory.Path);
        using var body = new MemoryStream([9, 8, 7]);
        using (var candidate = await store.PrepareAsync(0, [StorageMutation.Put("key", "{}"u8.ToArray(), body)]))
        {
            store.Commit(candidate);
        }
        using var point = store.ReadSnapshot("key");
        using (var candidate = await store.PrepareAsync(1, [StorageMutation.Delete("key")]))
        {
            store.Commit(candidate);
        }

        await Assert.That(store.CollectOrphans()).IsEqualTo(0);
        using var content = point.OpenDocument("key");
        using var output = new MemoryStream();
        await content.CopyToAsync(output);
        await Assert.That(Convert.ToHexString(output.ToArray())).IsEqualTo("090807");
        content.Dispose();
        point.Dispose();
        await Assert.That(store.CollectOrphans()).IsEqualTo(1);
    }

    [Test]
    public async Task BackupPreservesGenerationExactMetadataAndDocumentsButExcludesUncommittedAndOrphanedBytes()
    {
        using var source = new TestDirectory();
        using var destination = new TestDirectory();
        using var store = LocalFileStore.Initialize(source.Path);
        using var first = new MemoryStream([0, 255, 13, 10]);
        using var second = new MemoryStream([0, 255, 13, 10]);
        using var empty = new MemoryStream([]);
        using var obsolete = new MemoryStream([77]);
        using (var candidate = await store.PrepareAsync(0,
        [
            StorageMutation.Put("first", """{"epoch":184467440737095516160}"""u8.ToArray(), first),
            StorageMutation.Put("second", "{}"u8.ToArray(), second),
            StorageMutation.Put("empty", "{}"u8.ToArray(), empty),
            StorageMutation.Put("absent", "{}"u8.ToArray()),
            StorageMutation.Put("obsolete", "{}"u8.ToArray(), obsolete)
        ]))
        {
            store.Commit(candidate);
        }
        using (var candidate = await store.PrepareAsync(1, [StorageMutation.Delete("obsolete")]))
        {
            store.Commit(candidate);
        }
        using var pendingBody = new MemoryStream([88]);
        using var pending = await store.PrepareAsync(2, [StorageMutation.Put("pending", "{}"u8.ToArray(), pendingBody)]);

        await Assert.That(await store.CreateBackupAsync(destination.Path)).IsEqualTo(2L);
        using var backup = LocalFileStore.Open(destination.Path);
        using var snapshot = backup.ReadSnapshot();
        await Assert.That(snapshot.Generation).IsEqualTo(2L);
        await Assert.That(snapshot.Records.Count).IsEqualTo(4);
        await Assert.That(snapshot.Records.Single(record => record.Key == "first")
            .Metadata.GetProperty("epoch").GetRawText()).IsEqualTo("184467440737095516160");
        using var content = snapshot.OpenDocument("first");
        using var output = new MemoryStream();
        await content.CopyToAsync(output);
        await Assert.That(Convert.ToHexString(output.ToArray())).IsEqualTo("00FF0D0A");
        await Assert.That(snapshot.Records.Single(record => record.Key == "absent").Document).IsNull();
        await Assert.That(snapshot.Records.Single(record => record.Key == "empty").Document!.Length).IsEqualTo(0L);
        await Assert.That(Directory.GetFiles(Path.Combine(destination.Path, "blobs")).Length).IsEqualTo(2);
        await Assert.That(Directory.GetFiles(Path.Combine(destination.Path, "staging")).Length).IsEqualTo(0);
        await Assert.That(Directory.GetFiles(Path.Combine(source.Path, "staging")).Length).IsEqualTo(1);

        using var changed = await backup.PrepareAsync(2, [StorageMutation.Put("absent", """{"backup":true}"""u8.ToArray())]);
        await Assert.That(backup.Commit(changed)).IsEqualTo(3L);
        using var original = store.ReadSnapshot("absent");
        await Assert.That(original.Generation).IsEqualTo(2L);
        await Assert.That(original.Records.Single().Metadata.EnumerateObject().Count()).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyBackupIsAValidIndependentGenerationZeroStore()
    {
        using var source = new TestDirectory();
        using var destination = new TestDirectory();
        using var store = LocalFileStore.Initialize(source.Path);

        await Assert.That(await store.CreateBackupAsync(destination.Path)).IsEqualTo(0L);
        using var backup = LocalFileStore.Open(destination.Path);
        using var snapshot = backup.ReadSnapshot();
        await Assert.That(snapshot.Generation).IsEqualTo(0L);
        await Assert.That(snapshot.Records.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BackupNeverOverwritesAnExistingDestinationOrItsEvidence()
    {
        using var source = new TestDirectory();
        using var destination = new TestDirectory();
        using var store = LocalFileStore.Initialize(source.Path);
        var evidence = Path.Combine(destination.Path, "retain.txt");
        System.IO.File.WriteAllText(evidence, "existing operator evidence");

        await Assert.That(async () => await store.CreateBackupAsync(destination.Path)).Throws<StorageException>();
        await Assert.That(System.IO.File.ReadAllText(evidence)).IsEqualTo("existing operator evidence");
        await Assert.That(Directory.GetFileSystemEntries(destination.Path).Length).IsEqualTo(1);
        using var state = store.ReadSnapshot();
        await Assert.That(state.Generation).IsEqualTo(0L);
    }

    [Test]
    public async Task BackupRejectsAnOverlappingDirectory()
    {
        using var source = new TestDirectory();
        using var store = LocalFileStore.Initialize(source.Path);
        var nested = Path.Combine(source.Path, "nested-backup");
        Directory.CreateDirectory(nested);

        await Assert.That(async () => await store.CreateBackupAsync(source.Path)).Throws<StorageException>();
        await Assert.That(async () => await store.CreateBackupAsync(nested)).Throws<StorageException>();
        await Assert.That(Directory.GetFileSystemEntries(nested).Length).IsEqualTo(0);
    }

    [Test]
    public async Task CancelledBackupDoesNotCreateDestinationState()
    {
        using var source = new TestDirectory();
        using var destination = new TestDirectory();
        using var store = LocalFileStore.Initialize(source.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.That(async () => await store.CreateBackupAsync(destination.Path, cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(Directory.GetFileSystemEntries(destination.Path).Length).IsEqualTo(0);
        using var state = store.ReadSnapshot();
        await Assert.That(state.Generation).IsEqualTo(0L);
    }

    [Test]
    public async Task CorruptCommittedDataCannotProduceAReadyBackup()
    {
        using var source = new TestDirectory();
        using var destination = new TestDirectory();
        using var store = LocalFileStore.Initialize(source.Path);
        using var body = new MemoryStream([1, 2, 3]);
        using var candidate = await store.PrepareAsync(0, [StorageMutation.Put("key", "{}"u8.ToArray(), body)]);
        store.Commit(candidate);
        var blob = Directory.GetFiles(Path.Combine(source.Path, "blobs")).Single();
        System.IO.File.WriteAllBytes(blob, [7, 8, 9]);

        await Assert.That(async () => await store.CreateBackupAsync(destination.Path)).Throws<StorageException>();
        await Assert.That(Directory.GetFileSystemEntries(destination.Path).Length).IsEqualTo(0);
        await Assert.That(System.IO.File.Exists(Path.Combine(source.Path, "store.ready"))).IsTrue();
    }
}
