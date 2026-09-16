// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class RegistryDeprecationLifecycleTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RemovalBeforeEffectiveIsRejectedWithoutPublishingAnyEntities(bool resource)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Clocked(store, new());
        var path = resource ? "/teams/g/notes/n" : "/teams/g";
        var body = resource
            ? """{"meta":{"deprecated":{"effective":"2030-01-02T00:00:00Z","removal":"2030-01-01T00:00:00Z"}}}"""
            : """{"deprecated":{"effective":"2030-01-02T00:00:00Z","removal":"2030-01-01T00:00:00Z"}}""";
        await ExpectCode(() => Send(engine, RegistryAction.Replace, path, body), "invalid_attribute");
        using var snapshot = await store.ReadSnapshotAsync();
        await Assert.That(snapshot.Generation).IsEqualTo(0L);
        await Assert.That(snapshot.EnumerateRecords().Count()).IsEqualTo(0);
    }

    [Test]
    [Arguments("/teams/g", false)]
    [Arguments("/teams/g/notes/n", true)]
    [Arguments("/teams/g", true)]
    [Arguments("/teams/g/notes/n/versions/1", true)]
    public async Task FutureRemovalPromisesPreventDirectCascadeAndLastVersionDeletion(string path, bool resource)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Clocked(store, new());
        await CreateProtected(engine, resource);
        using var before = await store.ReadSnapshotAsync();
        var eventCount = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(engine, RegistryAction.Delete, path), "bad_request");
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(eventCount);
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g")).Metadata!.RootElement.GetProperty("notescount").GetInt32())
            .IsEqualTo(1);
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n")).Metadata!.RootElement.GetProperty("versionid").GetString())
            .IsEqualTo("1");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeletionAtThePromisedInstantSucceeds(bool resource)
    {
        var clock = new RemovalClock();
        var engine = Clocked(new InMemoryRegistryPersistence(), clock);
        await CreateProtected(engine, resource);
        clock.Utc = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await Send(engine, RegistryAction.Delete, resource ? "/teams/g/notes/n" : "/teams/g");
        await ExpectCode(() => Send(engine, RegistryAction.Read, resource ? "/teams/g/notes/n" : "/teams/g"), "not_found");
    }

    [Test]
    [Arguments("2030-01-01T02:00:00+02:00", "2030-01-01T00:00:00Z")]
    [Arguments("2030-01-01T00:00:00.123456788Z", "2030-01-01T00:00:00.123456789Z")]
    public async Task DeprecationOrderingUsesNormalizedInstantsAndFullFractionPrecision(string effective, string removal)
    {
        var engine = Clocked(new InMemoryRegistryPersistence(), new());
        var result = await Send(engine, RegistryAction.Replace, "/teams/g",
            "{\"deprecated\":{\"effective\":\"" + effective + "\",\"removal\":\"" + removal + "\"}}");
        await Assert.That(result.Metadata!.RootElement.GetProperty("deprecated").GetProperty("removal").GetString()).IsEqualTo(removal);
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g", """
            {"deprecated":{"effective":"2030-01-01T00:00:00.123456789Z","removal":"2030-01-01T00:00:00.123456788Z"}}
            """), "invalid_attribute");
    }

    [Test]
    public async Task RemovalPromiseRemainsMutableAndDeletingAnUnprotectedVersionDoesNotRemoveItsResource()
    {
        var engine = Clocked(new InMemoryRegistryPersistence(), new());
        await CreateProtected(engine, resource: true);
        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", "{}");
        await Send(engine, RegistryAction.Delete, "/teams/g/notes/n/versions/1");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n")).Metadata!.RootElement.GetProperty("versionscount").GetInt32())
            .IsEqualTo(1);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", """{"deprecated":{"removal":null}}""");
        await Send(engine, RegistryAction.Delete, "/teams/g/notes/n");
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/teams/g/notes/n"), "not_found");
    }

    [Test]
    public async Task SubtickRemovalPromisesAreNotRoundedDownToTheCurrentTime()
    {
        var clock = new RemovalClock { Utc = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var engine = Clocked(new InMemoryRegistryPersistence(), clock);
        await Send(engine, RegistryAction.Replace, "/teams/g",
            """{"deprecated":{"removal":"2030-01-01T00:00:00.000000001Z"}}""");
        await ExpectCode(() => Send(engine, RegistryAction.Delete, "/teams/g"), "bad_request");
        clock.Utc = clock.Utc.AddTicks(1);
        await Send(engine, RegistryAction.Delete, "/teams/g");
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/teams/g"), "not_found");
    }

    [Test]
    public async Task RemovalPromisesDoNotBypassIdentityGuardsOrBecomeReadonlyIgnore()
    {
        var engine = Clocked(new InMemoryRegistryPersistence(), new());
        await CreateProtected(engine, resource: true);
        await ExpectCode(() => Send(engine, RegistryAction.Delete, "/teams/g/notes/n",
            """{"noteid":"different"}"""), "mismatched_id");
        await ExpectCode(() => Send(engine, RegistryAction.Delete, "/teams/g", null,
            new KeyValuePair<string, string?>("ignore", "readonly")), "bad_request");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g")).Metadata!.RootElement.GetProperty("notescount").GetInt32())
            .IsEqualTo(1);
    }

    private static Task<RegistryResult> CreateProtected(RegistryEngine engine, bool resource) => Send(engine,
        RegistryAction.Replace, "/teams/g", resource
            ? """{"notes":{"n":{"meta":{"deprecated":{"removal":"2030-01-01T00:00:00Z"}}}}}"""
            : """{"deprecated":{"removal":"2030-01-01T00:00:00Z"},"notes":{"n":{}}}""").AsTask();

    private static RegistryEngine Clocked(IRegistryPersistence store, RemovalClock clock) => new(new()
    {
        RegistryId = "deprecation",
        PublicRoot = new Uri("https://registry.example"),
        Model = RegistryModel.Compile(RegistryJson.Parse(Model)),
        TimeProvider = clock
    }, store, new PermitPolicy());

    internal sealed class RemovalClock : TimeProvider
    {
        internal DateTimeOffset Utc { get; set; } = new(2029, 12, 31, 23, 59, 59, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}
