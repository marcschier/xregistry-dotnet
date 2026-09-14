using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Server;

namespace XRegistry.Server.Tests;

public class PersistenceTests
{
    [Test]
    public async Task StaleCandidatesCannotPublishAndAbandonedCandidatesHaveNoEffects()
    {
        var store = new InMemoryRegistryPersistence();
        using var snapshot = await store.ReadSnapshotAsync();
        using var a = await store.PrepareAsync(snapshot.Generation, [RegistryMutation.Put("/items/a", RegistryJson.Parse("{}"))]);
        using var b = await store.PrepareAsync(snapshot.Generation, [RegistryMutation.Put("/items/b", RegistryJson.Parse("{}"))]);
        await a.CommitAsync();
        await Assert.That(async () => await b.CommitAsync()).Throws<RegistryConcurrencyException>();
        using var current = await store.ReadSnapshotAsync();
        await Assert.That(current.Find("/items/a")).IsNotNull();
        await Assert.That(current.Find("/items/b")).IsNull();
        using (await store.PrepareAsync(current.Generation, [RegistryMutation.Delete("/items/a")]))
        {
        }

        using var unchanged = await store.ReadSnapshotAsync();
        await Assert.That(unchanged.Generation).IsEqualTo(1L);
        await Assert.That(unchanged.Find("/items/a")).IsNotNull();
    }

    [Test]
    public async Task EmptyDocumentRemovalAndLeasesRemainDistinctAfterSnapshotDisposal()
    {
        var store = new InMemoryRegistryPersistence();
        using var emptyBody = new MemoryStream([]);
        using var initial = await store.PrepareAsync(0, [RegistryMutation.PutDocument("/items/a", RegistryJson.Parse("{}"), emptyBody)]);
        await initial.CommitAsync();
        var snapshot = await store.ReadSnapshotAsync();
        using var lease = snapshot.OpenDocument("/items/a");
        await Assert.That(snapshot.Find("/items/a")!.HasDocument).IsTrue();
        snapshot.Dispose();
        await Assert.That(lease.Length).IsEqualTo(0L);
        using var removal = await store.PrepareAsync(1, [RegistryMutation.PutWithoutDocument("/items/a", RegistryJson.Parse("{}"))]);
        await removal.CommitAsync();
        using var current = await store.ReadSnapshotAsync();
        await Assert.That(current.Find("/items/a")!.HasDocument).IsFalse();
        await Assert.That(() => current.OpenDocument("/items/a")).Throws<InvalidOperationException>();
        await Assert.That(lease.Length).IsEqualTo(0L);
    }

    [Test]
    public async Task CancellationAndExactDocumentQuotaRejectWithoutPublishing()
    {
        var store = new InMemoryRegistryPersistence(maxDocumentBytes: 3);
        using var valid = new MemoryStream([1, 2, 3]);
        using var first = await store.PrepareAsync(0, [RegistryMutation.PutDocument("/items/a", RegistryJson.Parse("{}"), valid)]);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.That(async () => await first.CommitAsync(cancel.Token)).Throws<OperationCanceledException>();
        using var tooLarge = new MemoryStream([1, 2, 3, 4]);
        await Assert.That(async () => await store.PrepareAsync(0, [RegistryMutation.PutDocument("/items/b", RegistryJson.Parse("{}"), tooLarge)]))
            .Throws<RegistryException>();
        using var snapshot = await store.ReadSnapshotAsync();
        await Assert.That(snapshot.Generation).IsEqualTo(0L);
        await Assert.That(snapshot.GetChildren("/items").Count()).IsEqualTo(0);
        await first.CommitAsync();
        using var committed = await store.ReadSnapshotAsync();
        using var bytes = committed.OpenDocument("/items/a");
        await Assert.That(bytes.Length).IsEqualTo(3L);
    }

    [Test]
    public async Task SnapshotsPinRecordsAndDocumentsAcrossAtomicCommits()
    {
        var store = new InMemoryRegistryPersistence();
        using var empty = await store.ReadSnapshotAsync();
        using var input = new MemoryStream([0, 255, 123, 0]);
        using var first = await store.PrepareAsync(empty.Generation,
            [RegistryMutation.PutDocument("/groups/a", RegistryJson.Parse("""{"epoch":0}"""), input)]);
        await Assert.That(empty.Find("/groups/a")).IsNull();
        await Assert.That(await first.CommitAsync()).IsEqualTo(1L);
        using var pinned = await store.ReadSnapshotAsync();
        using var replacement = await store.PrepareAsync(pinned.Generation,
            [RegistryMutation.Put("/groups/a", RegistryJson.Parse("""{"epoch":1}""")),
             RegistryMutation.Put("/groups/b", RegistryJson.Parse("""{"epoch":0}"""))]);
        await replacement.CommitAsync();
        using var current = await store.ReadSnapshotAsync();
        await Assert.That(pinned.Find("/groups/a")!.Metadata.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That(current.Find("/groups/a")!.Metadata.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(1);
        await Assert.That(current.GetChildren("/groups").Count()).IsEqualTo(2);
        using var originalBytes = pinned.OpenDocument("/groups/a");
        using var currentBytes = current.OpenDocument("/groups/a");
        await Assert.That(originalBytes.ReadByte()).IsEqualTo(0);
        await Assert.That(originalBytes.ReadByte()).IsEqualTo(255);
        await Assert.That(currentBytes.Length).IsEqualTo(4L);
        using var exact = new MemoryStream();
        await currentBytes.CopyToAsync(exact);
        await Assert.That(Convert.ToBase64String(exact.ToArray())).IsEqualTo("AP97AA==");
        await Assert.That(empty.Find("/groups/b")).IsNull();
    }
}
