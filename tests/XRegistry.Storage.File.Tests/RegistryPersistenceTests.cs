// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Server;

namespace XRegistry.Storage.File.Tests;

public class RegistryPersistenceTests
{
    [Test]
    public async Task PreservingSeveralDocumentsDoesNotConsumeOneOpenStreamPerMutation()
    {
        using var directory = new TestDirectory();
        using var store = LocalFileStore.Initialize(directory.Path, new() { MaxOpenDocumentStreams = 1 });
        using var persistence = new LocalRegistryPersistence(store);
        using var first = new MemoryStream([1]);
        using var second = new MemoryStream([2]);
        using (var created = await persistence.PrepareAsync(0,
        [
            RegistryMutation.PutDocument("/docs/a", RegistryJson.Parse("{}"), first),
            RegistryMutation.PutDocument("/docs/b", RegistryJson.Parse("{}"), second)
        ]))
        {
            await created.CommitAsync();
        }
        using (var updated = await persistence.PrepareAsync(1,
        [
            RegistryMutation.Put("/docs/a", RegistryJson.Parse("""{"name":"a"}""")),
            RegistryMutation.Put("/docs/b", RegistryJson.Parse("""{"name":"b"}"""))
        ]))
        {
            await updated.CommitAsync();
        }
        using var snapshot = await persistence.ReadSnapshotAsync();
        using (var one = snapshot.OpenDocument("/docs/a"))
        {
            await Assert.That(one.ReadByte()).IsEqualTo(1);
        }
        using (var two = snapshot.OpenDocument("/docs/b"))
        {
            await Assert.That(two.ReadByte()).IsEqualTo(2);
        }
        await Assert.That(Directory.GetFiles(Path.Combine(directory.Path, "staging")).Length).IsEqualTo(0);
    }

    [Test]
    public async Task MetadataIndexCreationDoesNotReadEveryDocumentButDocumentAccessStillVerifiesIntegrity()
    {
        using var directory = new TestDirectory();
        using var store = LocalFileStore.Initialize(directory.Path);
        using var body = new MemoryStream([1, 2, 3]);
        using (var created = await store.PrepareAsync(0,
        [
            StorageMutation.Put("/docs/metadata", "{}"u8.ToArray()),
            StorageMutation.Put("/docs/blob", "{}"u8.ToArray(), body)
        ]))
        {
            store.Commit(created);
        }
        System.IO.File.Delete(Directory.GetFiles(Path.Combine(directory.Path, "blobs")).Single());
        using var persistence = new LocalRegistryPersistence(store);
        using var snapshot = await persistence.ReadSnapshotAsync();
        await Assert.That(snapshot.Find("/docs/metadata")!.HasDocument).IsFalse();
        await Assert.That(snapshot.Find("/docs/blob")!.HasDocument).IsTrue();
        await Assert.That(() => snapshot.OpenDocument("/docs/blob")).Throws<StorageException>();
    }

    [Test]
    public async Task PreserveRemoveReplaceAndEmptyContentSurviveAdapterRestartExactly()
    {
        using var directory = new TestDirectory();
        using (var store = LocalFileStore.Initialize(directory.Path))
        using (var persistence = new LocalRegistryPersistence(store))
        {
            using var document = new MemoryStream([0, 255, 10, 13]);
            using (var created = await persistence.PrepareAsync(0,
                [RegistryMutation.PutDocument("/items/one", RegistryJson.Parse("""{"epoch":184467440737095516160}"""), document)]))
            {
                await Assert.That(await created.CommitAsync()).IsEqualTo(1L);
            }
            using (var changed = await persistence.PrepareAsync(1,
                [RegistryMutation.Put("/items/one", RegistryJson.Parse("""{"epoch":184467440737095516161}"""))]))
            {
                await changed.CommitAsync();
            }
            using var snapshot = await persistence.ReadSnapshotAsync();
            using var stream = snapshot.OpenDocument("/items/one");
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            await Assert.That(Convert.ToHexString(output.ToArray())).IsEqualTo("00FF0A0D");
            await Assert.That(document.CanRead).IsTrue();
        }
        using var reopened = LocalFileStore.Open(directory.Path);
        using var adapter = new LocalRegistryPersistence(reopened);
        using (var snapshot = await adapter.ReadSnapshotAsync())
        {
            await Assert.That(snapshot.Generation).IsEqualTo(2L);
            await Assert.That(snapshot.Find("/items/one")!.Metadata.RootElement.GetProperty("epoch").GetRawText())
                .IsEqualTo("184467440737095516161");
        }
        using (var candidate = await adapter.PrepareAsync(2,
            [RegistryMutation.PutWithoutDocument("/items/one", RegistryJson.Parse("{}"))]))
        {
            await candidate.CommitAsync();
        }
        using (var missing = await adapter.ReadSnapshotAsync())
        {
            await Assert.That(missing.Find("/items/one")!.HasDocument).IsFalse();
            await Assert.That(() => missing.OpenDocument("/items/one")).Throws<InvalidOperationException>();
        }
        using var empty = new MemoryStream([]);
        using (var candidate = await adapter.PrepareAsync(3,
            [RegistryMutation.PutDocument("/items/one", RegistryJson.Parse("{}"), empty)]))
        {
            await candidate.CommitAsync();
        }
        using var present = await adapter.ReadSnapshotAsync();
        await Assert.That(present.Find("/items/one")!.HasDocument).IsTrue();
        using var emptyRead = present.OpenDocument("/items/one");
        await Assert.That(emptyRead.ReadByte()).IsEqualTo(-1);
    }

    [Test]
    public async Task SnapshotsKeepOneGenerationAcrossReplacementAndIndexOnlyImmediateOrdinalChildren()
    {
        using var directory = new TestDirectory();
        using var store = LocalFileStore.Initialize(directory.Path);
        using var persistence = new LocalRegistryPersistence(store);
        using var oldContent = new MemoryStream([1]);
        using (var initial = await persistence.PrepareAsync(0,
            [RegistryMutation.PutDocument("/docs/a", RegistryJson.Parse("""{"value":"old"}"""), oldContent)]))
        {
            await initial.CommitAsync();
        }
        using var old = await persistence.ReadSnapshotAsync();
        using var newContent = new MemoryStream([2]);
        using (var changed = await persistence.PrepareAsync(1,
        [
            RegistryMutation.PutDocument("/docs/a", RegistryJson.Parse("""{"value":"new"}"""), newContent),
            RegistryMutation.Put("/docs/nested/b", RegistryJson.Parse("{}")),
            RegistryMutation.Put("/docs-other/c", RegistryJson.Parse("{}"))
        ]))
        {
            await changed.CommitAsync();
        }
        using var current = await persistence.ReadSnapshotAsync();
        await Assert.That(old.Generation).IsEqualTo(1L);
        await Assert.That(current.Generation).IsEqualTo(2L);
        await Assert.That(old.Find("/docs/nested/b")).IsNull();
        await Assert.That(old.Find("/docs/a")!.Metadata.RootElement.GetProperty("value").GetString()).IsEqualTo("old");
        await Assert.That(current.Find("/docs/a")!.Metadata.RootElement.GetProperty("value").GetString()).IsEqualTo("new");
        await Assert.That(current.GetChildren("/docs").Single().Key).IsEqualTo("/docs/a");
        await Assert.That(current.GetChildren("/docs/nested").Single().Key).IsEqualTo("/docs/nested/b");
        await Assert.That(current.GetChildren("/DOCS").Count()).IsEqualTo(0);
        using var oldBytes = old.OpenDocument("/docs/a");
        using var newBytes = current.OpenDocument("/docs/a");
        await Assert.That(oldBytes.ReadByte()).IsEqualTo(1);
        await Assert.That(newBytes.ReadByte()).IsEqualTo(2);
    }

    [Test]
    public async Task DirectStoreMutationRefreshesOnlyNewSnapshotsAndRejectsStaleCandidates()
    {
        using var directory = new TestDirectory();
        using var store = LocalFileStore.Initialize(directory.Path);
        using var persistence = new LocalRegistryPersistence(store);
        using var old = await persistence.ReadSnapshotAsync();
        using var stale = await persistence.PrepareAsync(0, [RegistryMutation.Put("/old", RegistryJson.Parse("{}"))]);
        using (var direct = await store.PrepareAsync(0, [StorageMutation.Put("/new", "{}"u8.ToArray())]))
        {
            store.Commit(direct);
        }
        await Assert.That(async () => await stale.CommitAsync()).Throws<RegistryConcurrencyException>();
        using var current = await persistence.ReadSnapshotAsync();
        await Assert.That(current.Generation).IsEqualTo(1L);
        await Assert.That(current.Find("/new")!.Key).IsEqualTo("/new");
        await Assert.That(current.Find("/old")).IsNull();
        await Assert.That(old.Find("/new")).IsNull();
    }

    [Test]
    public async Task SnapshotAndDocumentLeasesOutliveAnOwningAdapterWithoutBlockingFinalReopen()
    {
        using var directory = new TestDirectory();
        var store = LocalFileStore.Initialize(directory.Path);
        using var persistence = new LocalRegistryPersistence(store, ownsStore: true);
        using var content = new MemoryStream([8, 9]);
        using (var candidate = await persistence.PrepareAsync(0,
            [RegistryMutation.PutDocument("/doc", RegistryJson.Parse("{}"), content)]))
        {
            await candidate.CommitAsync();
        }
        using var snapshot = await persistence.ReadSnapshotAsync();
        persistence.Dispose();
        using var read = snapshot.OpenDocument("/doc");
        snapshot.Dispose();
        await Assert.That(read.ReadByte()).IsEqualTo(8);
        await Assert.That(read.ReadByte()).IsEqualTo(9);
        read.Dispose();
        using var reopened = LocalFileStore.Open(directory.Path);
        await Assert.That(reopened.ReadGeneration()).IsEqualTo(1L);
    }

    [Test]
    public async Task SnapshotQuotaIsExplicitAndReleasedWithoutClosingCallerOwnedStore()
    {
        using var directory = new TestDirectory();
        using var store = LocalFileStore.Initialize(directory.Path);
        using var persistence = new LocalRegistryPersistence(store, maxSnapshots: 1);
        using var first = await persistence.ReadSnapshotAsync();
        var error = await Assert.That(async () => await persistence.ReadSnapshotAsync()).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("server_busy");
        first.Dispose();
        using var replacement = await persistence.ReadSnapshotAsync();
        await Assert.That(replacement.Generation).IsEqualTo(0L);
        persistence.Dispose();
        await Assert.That(store.ReadGeneration()).IsEqualTo(0L);
    }

    [Test]
    public async Task FileBackedEngineRestartsWithFrozenCustomModelExactBytesAndCommittedOutbox()
    {
        using var directory = new TestDirectory();
        const string model = """{"groups":{"teams":{"singular":"team","resources":{"files":{"singular":"file"}}}}}""";
        var context = new RegistryOperationContext(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "fixture")], "fixture")));
        string? correlation;
        using (var store = LocalFileStore.Initialize(directory.Path))
        using (var persistence = new LocalRegistryPersistence(store))
        {
            var engine = Engine(persistence, "{}");
            await engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/modelsource"))
            {
                Metadata = RegistryJson.Parse(model)
            }, context);
            using var content = new MemoryStream([0, 255, 127, 13, 10]);
            var created = await engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/teams/a/files/b/versions/v1"))
            {
                Document = content,
                ContentType = "application/octet-stream"
            }, context);
            correlation = created.CorrelationId;
            await Assert.That(created.Kind).IsEqualTo(RegistryResultKind.Created);
            await Assert.That(content.CanRead).IsTrue();
        }
        using var reopened = LocalFileStore.Open(directory.Path);
        using var adapter = new LocalRegistryPersistence(reopened);
        var restored = Engine(adapter, "{}");
        var result = await restored.ExecuteAsync(
            new(RegistryAction.Read, RegistryPath.Parse("/teams/a/files/b/versions/v1")), context);
        using var bytes = result.Document!.OpenRead();
        using var output = new MemoryStream();
        await bytes.CopyToAsync(output);
        await Assert.That(Convert.ToHexString(output.ToArray())).IsEqualTo("00FF7F0D0A");
        using var durable = await adapter.ReadSnapshotAsync();
        await Assert.That(durable.Find("$events/" + correlation) is not null).IsTrue();
        await Assert.That(durable.Find("$correlations/" + correlation) is not null).IsTrue();
        var before = durable.Generation;
        await Assert.That(async () => await restored.ExecuteAsync(
            new(RegistryAction.Replace, RegistryPath.Parse("/"))
            {
                Metadata = RegistryJson.Parse("""{"teams":{"valid":{"name":"valid"},"bad":{"undeclared":1}}}""")
            }, context)).Throws<RegistryException>();
        await Assert.That(reopened.ReadGeneration()).IsEqualTo(before);
        var unchanged = await restored.ExecuteAsync(new(RegistryAction.Read, RegistryPath.Parse("/teams")), context);
        await Assert.That(unchanged.Metadata!.RootElement.EnumerateObject().Single().Name).IsEqualTo("a");
    }

    private static RegistryEngine Engine(IRegistryPersistence persistence, string model) => new(new()
    {
        RegistryId = "durable",
        PublicRoot = new Uri("https://registry.example/root"),
        Model = RegistryModel.Compile(RegistryJson.Parse(model))
    }, persistence, new Permit());

    private sealed class Permit : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }
}
