using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;
using static XRegistry.Server.Tests.RegistryQueryTests;

namespace XRegistry.Server.Tests;

public class RegistrySharedQueryAdapterTests
{
    [Test]
    public async Task LocalQueryAdapterBuildsDefaultAndMetaFactsBeforeDocumentViewProjection()
    {
        var engine = Create(QueryModel);
        await Send(engine, RegistryAction.Replace, "/fleets/g/items/r", """{"rank":9007199254740993}""");
        await Send(engine, RegistryAction.Post, "/fleets/g/items/r", """{"versionid":"2","rank":0}""");
        await Send(engine, RegistryAction.Patch, "/fleets/g/items/r/meta", """{"defaultversionid":"1"}""");
        await Send(engine, RegistryAction.Replace, "/fleets/g/items/other", """{"rank":2}""");
        var result = await Send(engine, RegistryAction.Read, "/fleets/g/items", null,
            new("filter", "rank>9007199254740992,meta.defaultversionid=1,meta.defaultversionsticky=true"),
            new("sort", "rank=desc"), new("doc", null), new("inline", "meta,versions"));

        await Assert.That(result.Page!.TotalCount).IsEqualTo(1UL);
        await Assert.That(result.Metadata!.RootElement.EnumerateObject().Single().Name).IsEqualTo("r");
        var resource = result.Metadata.RootElement.GetProperty("r");
        await Assert.That(resource.TryGetProperty("rank", out _)).IsFalse();
        await Assert.That(resource.GetProperty("self").GetString()).IsEqualTo("#/r");
        await Assert.That(resource.GetProperty("meta").GetProperty("defaultversionid").GetString()).IsEqualTo("1");
        await Assert.That(resource.GetProperty("versions").GetProperty("1").GetProperty("rank").GetRawText()).IsEqualTo("9007199254740993");
        await Assert.That(resource.GetProperty("versions").GetProperty("2").GetProperty("rank").GetInt32()).IsEqualTo(0);
        await Assert.That(resource.GetProperty("meta").GetProperty("defaultversionurl").GetString()).IsEqualTo("#/r/versions/1");
    }

    [Test]
    [Arguments("meta.xref=null")]
    [Arguments("meta.xref!=null")]
    public async Task LocalDanglingAliasQueryDoesNotInterpretDeniedMetaAsMissingNull(string filter)
    {
        var policy = new RegistryPaginationQueryTests.QueryPolicy();
        var engine = Create(QueryModel, policy: policy);
        await Send(engine, RegistryAction.Replace, "/fleets/g/items/alias",
            """{"meta":{"xref":"/fleets/other/items/missing"}}""");
        policy.Revoked.Add("/fleets/g/items/alias/meta");
        var denied = await Send(engine, RegistryAction.Read, "/fleets/g/items", null, new KeyValuePair<string, string?>("filter", filter));
        await Assert.That(denied.Page!.TotalCount).IsEqualTo(0UL);
        await Assert.That(denied.Metadata!.RootElement.EnumerateObject().Count()).IsEqualTo(0);
        policy.Revoked.Clear();
        var visible = await Send(engine, RegistryAction.Read, "/fleets/g/items", null, new KeyValuePair<string, string?>("filter", "meta.xref!=null"));
        await Assert.That(visible.Metadata!.RootElement.EnumerateObject().Single().Name).IsEqualTo("alias");
    }

    [Test]
    [Arguments("/fleets/g/items/r/versions")]
    [Arguments("/fleets/g/items/r/versions/1")]
    public async Task LocalVersionQueriesDoNotRequirePermissionToReadTheResourceProjection(string path)
    {
        var policy = new RegistryPaginationQueryTests.QueryPolicy();
        var engine = Create(QueryModel, policy: policy);
        await Send(engine, RegistryAction.Replace, "/fleets/g/items/r", """{"name":"visible version"}""");
        policy.Revoked.Add("/fleets/g/items/r");
        policy.Revoked.Add("/fleets/g/items/r/meta");
        var ordinary = await Send(engine, RegistryAction.Read, "/fleets/g/items/r/versions/1");
        await Assert.That(ordinary.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("1");

        var filtered = await Send(engine, RegistryAction.Read, path, null,
            new KeyValuePair<string, string?>("filter", "versionid=1,isdefault=true"));
        var version = path.EndsWith("/versions", StringComparison.Ordinal)
            ? filtered.Metadata!.RootElement.GetProperty("1") : filtered.Metadata!.RootElement;
        await Assert.That(version.GetProperty("name").GetString()).IsEqualTo("visible version");
        await Assert.That(version.GetProperty("isdefault").GetBoolean()).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SharedQueryDeadlineReachesDocumentStorageReadsAndReleasesTheLease(bool queryDocument)
    {
        var duration = TimeSpan.FromMilliseconds(100);
        var clock = new RegistryPaginationQueryTests.ManualQueryClock();
        var persistence = new DelayedDocumentPersistence();
        var engine = new RegistryEngine(new()
        {
            RegistryId = "deadline",
            PublicRoot = new Uri("https://registry.example/catalog"),
            Model = RegistryModel.Compile(RegistryJson.Parse(QueryModel)),
            QueryLimits = new() { MaxDuration = duration },
            TimeProvider = clock
        }, persistence, new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/fleets/g/docs/r$details",
            """{"contenttype":"text/plain","doc":"hello"}""");
        persistence.DelayReads = true;
        using var callerDeadline = new CancellationTokenSource();
        var operation = engine.ExecuteAsync(new(RegistryAction.Read, RegistryPath.Parse("/fleets/g/docs"))
        {
            Parameters = queryDocument ? [new("filter", "doc=hello")] : [new("inline", "doc")]
        }, Writer(), callerDeadline.Token).AsTask();
        await persistence.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(duration);
        await ExpectCode(async () => await operation.WaitAsync(TimeSpan.FromSeconds(10)), "server_busy");
        await Assert.That(callerDeadline.IsCancellationRequested).IsFalse();
        await Assert.That(persistence.LeaseReleased).IsTrue();
    }

    private sealed class DelayedDocumentPersistence : IRegistryPersistence
    {
        private readonly InMemoryRegistryPersistence _inner = new();
        internal bool DelayReads { get; set; }
        internal bool LeaseReleased { get; set; }
        internal TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsReadOnly => false;
        public async ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default) =>
            new Snapshot(this, await _inner.ReadSnapshotAsync(cancellationToken));
        public ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration, IReadOnlyList<RegistryMutation> mutations,
            CancellationToken cancellationToken = default) => _inner.PrepareAsync(expectedGeneration, mutations, cancellationToken);

        private sealed class Snapshot(DelayedDocumentPersistence owner, IRegistrySnapshot inner) : IRegistrySnapshot
        {
            public long Generation => inner.Generation;
            public RegistryRecord? Find(string key) => inner.Find(key);
            public IEnumerable<RegistryRecord> GetChildren(string collectionKey) => inner.GetChildren(collectionKey);
            public IEnumerable<RegistryRecord> EnumerateRecords() => inner.EnumerateRecords();
            public Stream OpenDocument(string key, CancellationToken cancellationToken = default) =>
                owner.DelayReads ? new DelayedStream(owner) : inner.OpenDocument(key, cancellationToken);
            public void Dispose() => inner.Dispose();
        }

        private sealed class DelayedStream(DelayedDocumentPersistence owner) : MemoryStream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                owner.ReadEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }

            protected override void Dispose(bool disposing)
            {
                owner.LeaseReleased = true;
                base.Dispose(disposing);
            }
        }
    }
}
