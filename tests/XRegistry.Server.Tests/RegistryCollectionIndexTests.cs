using System.Globalization;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;
using static XRegistry.Server.Tests.RegistryScalabilityTests;

namespace XRegistry.Server.Tests;

public class RegistryCollectionIndexTests
{
    [Test]
    public async Task OneRequestDiscoversEachCollectionOnceWithoutVisitingUnchangedVersionSubtrees()
    {
        var persistence = new ObservedPersistence();
        var engine = Create(RegistryScalabilityTests.Model, persistence);
        await Seed(engine, 1000);
        persistence.Collections.Clear();
        persistence.Members.Clear();
        await Send(engine, RegistryAction.Patch, "/", """
            {"dirs":{"perf":{"records":{
              "r000000":{"payload":"first"},"r000999":{"payload":"last"}
            }}}}
            """);
        await Assert.That(persistence.Collections.Values.All(static count => count == 1)).IsTrue();
        await Assert.That(persistence.Collections["/dirs/perf/records"]).IsEqualTo(1);
        await Assert.That(persistence.Members["/dirs/perf/records"]).IsEqualTo(1000);
        await Assert.That(persistence.Collections.Keys.Where(static key => key.EndsWith("/versions", StringComparison.Ordinal)).ToArray())
            .IsEquivalentTo(["/dirs/perf/records/r000000/versions", "/dirs/perf/records/r000999/versions"], StringComparer.Ordinal);
        await Assert.That((await Send(engine, RegistryAction.Read, "/dirs/perf/records/r000500")).Metadata!.RootElement
            .GetProperty("payload").GetString()).IsEqualTo("deterministic-native-server-performance-fixture");
    }

    [Test]
    public async Task MembershipCachesTrackRetentionDeletionStickyDefaultsAndFreshRequestIdentityReuse()
    {
        var model = """
            {"groups":{"dirs":{"singular":"dir","resources":{"records":{"singular":"record",
              "hasdocument":false,"maxversions":2}}}}}
            """;
        var engine = Create(model);
        await Send(engine, RegistryAction.Replace, "/dirs/g/records/r", "{}");
        await Send(engine, RegistryAction.Post, "/dirs/g/records/r", "{}");
        await Send(engine, RegistryAction.Post, "/dirs/g/records/r", "{}");
        var retained = await Send(engine, RegistryAction.Read, "/dirs/g/records/r/versions");
        await Assert.That(Ids(retained)).IsEqualTo("2,3");
        await Assert.That(retained.Page!.TotalCount).IsEqualTo(2UL);
        await Assert.That(retained.Metadata!.RootElement.GetProperty("2").GetProperty("ancestorid").GetString()).IsEqualTo("2");
        await Send(engine, RegistryAction.Patch, "/dirs/g/records/r/meta", """{"defaultversionid":"3"}""");
        await Send(engine, RegistryAction.Delete, "/dirs/g/records/r/versions", """{"3":{}}""");
        var remaining = await Send(engine, RegistryAction.Read, "/dirs/g/records/r/meta");
        await Assert.That(remaining.Metadata!.RootElement.GetProperty("defaultversionid").GetString()).IsEqualTo("2");
        await Assert.That(remaining.Metadata.RootElement.GetProperty("defaultversionsticky").GetBoolean()).IsFalse();
        var generated = await Send(engine, RegistryAction.Post, "/dirs/g/records/r", "{}");
        await Assert.That(generated.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("4");
        await Assert.That(Ids(await Send(engine, RegistryAction.Read, "/dirs/g/records/r/versions"))).IsEqualTo("2,4");
        await Send(engine, RegistryAction.Delete, "/dirs/g/records/r/versions");
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/dirs/g/records/r"), "not_found");
        await Assert.That((await Send(engine, RegistryAction.Read, "/dirs/g")).Metadata!.RootElement.GetProperty("recordscount").GetInt32()).IsEqualTo(0);
        await Send(engine, RegistryAction.Replace, "/dirs/g/records/R", "{}");
        await Assert.That((await Send(engine, RegistryAction.Read, "/dirs/g/records/R")).Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("1");
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/dirs/g/records", """{"r":{}}"""), "mismatched_id");
    }

    [Test]
    public async Task ManualAncestryAndGeneratedIdCollisionsRemainBoundedWithoutSkippingCycleChecks()
    {
        var engine = Create(RegistryScalabilityTests.Model);
        var versions = new JsonObject();
        for (var index = 1; index <= 250; index++)
        {
            var id = index.ToString(CultureInfo.InvariantCulture);
            versions[id] = new JsonObject { ["ancestorid"] = Math.Max(1, index - 1).ToString(CultureInfo.InvariantCulture) };
        }

        await Send(engine, RegistryAction.Post, "/dirs/g/records/r/versions", versions.ToJsonString());
        var created = await Send(engine, RegistryAction.Post, "/dirs/g/records/r", "{}");
        await Assert.That(created.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("251");
        await Assert.That(created.Metadata.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo("250");
        var before = await Send(engine, RegistryAction.Read, "/dirs/g/records/r/versions/1");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/dirs/g/records/r/versions/1",
            """{"ancestorid":"251"}"""), "ancestor_circular_reference");
        var after = await Send(engine, RegistryAction.Read, "/dirs/g/records/r/versions/1");
        await Assert.That(after.Metadata!.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo("1");
        await Assert.That(Epoch(after)).IsEqualTo(Epoch(before));
        await Assert.That((await Send(engine, RegistryAction.Read, "/dirs/g/records/r")).Metadata!.RootElement.GetProperty("versionscount").GetInt32())
            .IsEqualTo(251);
    }

    [Test]
    public async Task RealCollectionDiscoveryStillExhaustsFiniteWorkAndRollsBackBeforePublication()
    {
        var persistence = new InMemoryRegistryPersistence();
        var engine = Create(RegistryScalabilityTests.Model, persistence);
        await Seed(engine, 100);
        var generation = await Generation(persistence);
        var root = await Send(engine, RegistryAction.Read, "/");
        var bounded = Create(RegistryScalabilityTests.Model, persistence, limits: new() { MaxEntityOperations = 50 });
        await ExpectCode(() => Send(bounded, RegistryAction.Patch, "/", """
            {"name":"must not publish","dirs":{"perf":{"records":{"r000000":{"payload":"must not publish"}}}}}
            """), "operation_limit");
        await Assert.That(await Generation(persistence)).IsEqualTo(generation);
        await Assert.That(Epoch(await Send(engine, RegistryAction.Read, "/"))).IsEqualTo(Epoch(root));
        await Assert.That((await Send(engine, RegistryAction.Read, "/dirs/perf/records/r000000")).Metadata!.RootElement.GetProperty("payload").GetString())
            .IsEqualTo("deterministic-native-server-performance-fixture");
    }

    [Test]
    public async Task RetainedCollectionRecordsRemainInsideTheWorkingSetBudget()
    {
        var persistence = new InMemoryRegistryPersistence();
        var engine = Create(RegistryScalabilityTests.Model, persistence);
        await Seed(engine, 100);
        var generation = await Generation(persistence);
        var bounded = Create(RegistryScalabilityTests.Model, persistence, limits: new() { MaxWorkingSetBytes = 25_000 });
        await ExpectCode(() => Send(bounded, RegistryAction.Patch, "/", """
            {"dirs":{"perf":{"records":{"r000000":{"payload":"must not publish"}}}}}
            """), "operation_limit");
        await Assert.That(await Generation(persistence)).IsEqualTo(generation);
        await Assert.That((await Send(engine, RegistryAction.Read, "/dirs/perf/records/r000000")).Metadata!.RootElement.GetProperty("payload").GetString())
            .IsEqualTo("deterministic-native-server-performance-fixture");
    }

    private static string Ids(RegistryResult result) =>
        string.Join(',', result.Metadata!.RootElement.EnumerateObject().Select(static property => property.Name));

    private sealed class ObservedPersistence : IRegistryPersistence
    {
        private readonly InMemoryRegistryPersistence _inner = new();
        internal Dictionary<string, int> Collections { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, int> Members { get; } = new(StringComparer.Ordinal);
        public bool IsReadOnly => false;
        public async ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default) =>
            new Snapshot(this, await _inner.ReadSnapshotAsync(cancellationToken));
        public ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration, IReadOnlyList<RegistryMutation> mutations,
            CancellationToken cancellationToken = default) => _inner.PrepareAsync(expectedGeneration, mutations, cancellationToken);

        private sealed class Snapshot(ObservedPersistence owner, IRegistrySnapshot inner) : IRegistrySnapshot
        {
            public long Generation => inner.Generation;
            public RegistryRecord? Find(string key) => inner.Find(key);
            public IEnumerable<RegistryRecord> GetChildren(string collectionKey)
            {
                owner.Collections[collectionKey] = owner.Collections.GetValueOrDefault(collectionKey) + 1;
                foreach (var record in inner.GetChildren(collectionKey))
                {
                    owner.Members[collectionKey] = owner.Members.GetValueOrDefault(collectionKey) + 1;
                    yield return record;
                }
            }

            public IEnumerable<RegistryRecord> EnumerateRecords() => inner.EnumerateRecords();
            public Stream OpenDocument(string key, CancellationToken cancellationToken = default) => inner.OpenDocument(key, cancellationToken);
            public void Dispose() => inner.Dispose();
        }
    }
}
