// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class BoundaryTests
{
    [Test]
    public async Task DirectOperationMetadataBudgetsApplyBeforeEpochArithmetic()
    {
        var engine = Create(limits: new() { Json = new() { MaxNumberCharacters = 3 } });
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/", """{"epoch":1000}"""), "number_limit");
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentOperationBudgetRejectsExcessWithoutQueueing()
    {
        var engine = Create(limits: new() { MaxConcurrentOperations = 1 });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = engine.ExecuteAsync(new(RegistryAction.Patch, RegistryPath.Parse("/")) { Metadata = RegistryJson.Parse("{}") },
            new RegistryOperationContext(Writer().Caller)
            {
                PrepareResponseAsync = async (_, ct) =>
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(ct);
                }
            }).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await ExpectCode(() => Send(engine, RegistryAction.Read, "/"), "server_busy");
        }
        finally
        {
            release.SetResult();
        }

        await pending;
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationDuringResponsePreparationLeavesNoChangesOrEvents()
    {
        var engine = Create();
        using var canceled = new CancellationTokenSource();
        await Assert.That(async () => await engine.ExecuteAsync(new(RegistryAction.Patch, RegistryPath.Parse("/"))
        {
            Metadata = RegistryJson.Parse("""{"name":"not committed"}""")
        }, new RegistryOperationContext(Writer().Caller)
        {
            PrepareResponseAsync = async (_, _) => await canceled.CancelAsync()
        }, canceled.Token)).Throws<OperationCanceledException>();
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
    }

    [Test]
    public async Task AnonymousReadsCanBeDisabledAndOutboxNeverAllowsAnonymousReaders()
    {
        var engine = new RegistryEngine(new()
        {
            RegistryId = "private",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse(Model))
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        await ExpectCode(() => engine.ExecuteAsync(new(RegistryAction.Read, RegistryPath.Parse("/")), Reader()), "unauthorized");
        await Assert.That(async () => await Create().ReadEventBatchesAsync(Reader())).Throws<RegistryException>();
    }

    [Test]
    public async Task HugeValidEpochGuardAdvancesWithoutNarrowing()
    {
        var engine = new RegistryEngine(new()
        {
            RegistryId = "huge",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse(Model)),
            InitialMetadata = RegistryJson.Parse("""{"epoch":184467440737095516160000}""")
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        var result = await Send(engine, RegistryAction.Patch, "/", """{"epoch":184467440737095516160000}""");
        await Assert.That(result.Metadata!.RootElement.GetProperty("epoch").GetRawText()).IsEqualTo("184467440737095516160001");
    }

    [Test]
    public async Task ExplicitTimestampsArePreservedAndEqualOrNullModifiedAtTouchesNow()
    {
        var clock = new FixedClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var engine = new RegistryEngine(new()
        {
            RegistryId = "clock",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse(Model)),
            TimeProvider = clock
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/teams/g", """{"createdat":"2040-01-01T00:00:00Z"}""");
        var changed = await Send(engine, RegistryAction.Patch, "/teams/g", """{"modifiedat":"2020-01-01T02:00:00+02:00"}""");
        await Assert.That(changed.Metadata!.RootElement.GetProperty("modifiedat").GetString()).IsEqualTo("2020-01-01T00:00:00Z");
        await Assert.That(changed.Metadata.RootElement.GetProperty("createdat").GetString()).IsEqualTo("2040-01-01T00:00:00Z");
        clock.Now = new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var explicitCreated = await Send(engine, RegistryAction.Patch, "/teams/g", """{"createdat":"2050-01-01T00:00:00Z"}""");
        await Assert.That(explicitCreated.Metadata!.RootElement.GetProperty("createdat").GetString()).IsEqualTo("2050-01-01T00:00:00Z");
        await Assert.That(explicitCreated.Metadata.RootElement.GetProperty("modifiedat").GetString()).IsEqualTo("2031-01-01T00:00:00.0000000Z");
        clock.Now = new DateTimeOffset(2032, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var equal = await Send(engine, RegistryAction.Patch, "/teams/g", """{"modifiedat":"2031-01-01T00:00:00Z"}""");
        await Assert.That(equal.Metadata!.RootElement.GetProperty("modifiedat").GetString()).IsEqualTo("2032-01-01T00:00:00.0000000Z");
        clock.Now = new DateTimeOffset(2033, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var cleared = await Send(engine, RegistryAction.Patch, "/teams/g", """{"createdat":null,"modifiedat":null}""");
        await Assert.That(cleared.Metadata!.RootElement.GetProperty("createdat").GetString()).IsEqualTo("2033-01-01T00:00:00.0000000Z");
        await Assert.That(cleared.Metadata.RootElement.GetProperty("modifiedat").GetString()).IsEqualTo("2033-01-01T00:00:00.0000000Z");
        clock.Now = new DateTimeOffset(2034, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var omitted = await Send(engine, RegistryAction.Patch, "/teams/g", "{}");
        await Assert.That(omitted.Metadata!.RootElement.GetProperty("createdat").GetString()).IsEqualTo("2033-01-01T00:00:00.0000000Z");
        await Assert.That(omitted.Metadata.RootElement.GetProperty("modifiedat").GetString()).IsEqualTo("2034-01-01T00:00:00.0000000Z");
    }

    [Test]
    public async Task EntitiesCreatedInOneRequestShareTheSameCurrentTimestamp()
    {
        var clock = new AdvancingClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var engine = new RegistryEngine(new()
        {
            RegistryId = "timestamps",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse(Model)),
            TimeProvider = clock
        }, new InMemoryRegistryPersistence(), new PermitPolicy());

        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"name":"created"}""");
        var group = (await Send(engine, RegistryAction.Read, "/teams/g")).Metadata!.RootElement;
        var resource = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
        var version = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/1")).Metadata!.RootElement;
        var createdAt = group.GetProperty("createdat").GetString();
        var modifiedAt = group.GetProperty("modifiedat").GetString();

        await Assert.That(createdAt).IsNotNullOrEmpty();
        await Assert.That(modifiedAt).IsEqualTo(createdAt);
        await Assert.That(resource.GetProperty("createdat").GetString()).IsEqualTo(createdAt);
        await Assert.That(resource.GetProperty("modifiedat").GetString()).IsEqualTo(modifiedAt);
        await Assert.That(version.GetProperty("createdat").GetString()).IsEqualTo(createdAt);
        await Assert.That(version.GetProperty("modifiedat").GetString()).IsEqualTo(modifiedAt);
    }

    [Test]
    public async Task ParentModifiedAtTracksCollectionMembershipButNotExistingChildUpdates()
    {
        var clock = new AdvancingClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var engine = new RegistryEngine(new()
        {
            RegistryId = "timestamps",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse(Model)),
            TimeProvider = clock
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        async Task<RegistryResult> Read(string path) => await Send(engine, RegistryAction.Read, path);
        static DateTimeOffset ModifiedAt(RegistryResult result) =>
            result.Metadata!.RootElement.GetProperty("modifiedat").GetDateTimeOffset();

        var rootBefore = await Read("/");
        await Send(engine, RegistryAction.Replace, "/teams/g", "{}");
        var rootAfterGroup = await Read("/");
        var groupAfterCreate = await Read("/teams/g");
        await Assert.That(ModifiedAt(rootAfterGroup)).IsGreaterThan(ModifiedAt(rootBefore));

        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"name":"first"}""");
        var groupAfterResource = await Read("/teams/g");
        var resourceAfterCreate = await Read("/teams/g/notes/n/meta");
        await Assert.That(ModifiedAt(groupAfterResource)).IsGreaterThan(ModifiedAt(groupAfterCreate));
        await Assert.That(ModifiedAt(await Read("/"))).IsEqualTo(ModifiedAt(rootAfterGroup));

        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"v2","name":"second"}""");
        var resourceAfterVersion = await Read("/teams/g/notes/n/meta");
        var groupAfterVersion = await Read("/teams/g");
        await Assert.That(ModifiedAt(resourceAfterVersion)).IsGreaterThan(ModifiedAt(resourceAfterCreate));
        await Assert.That(ModifiedAt(groupAfterVersion)).IsEqualTo(ModifiedAt(groupAfterResource));

        var versionBeforeUpdate = await Read("/teams/g/notes/n/versions/v2");
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/v2", """{"name":"updated"}""");
        var versionAfterUpdate = await Read("/teams/g/notes/n/versions/v2");
        var resourceAfterUpdate = await Read("/teams/g/notes/n/meta");
        await Assert.That(ModifiedAt(versionAfterUpdate)).IsGreaterThan(ModifiedAt(versionBeforeUpdate));
        await Assert.That(ModifiedAt(resourceAfterUpdate)).IsEqualTo(ModifiedAt(resourceAfterVersion));
        await Assert.That(ModifiedAt(await Read("/teams/g"))).IsEqualTo(ModifiedAt(groupAfterVersion));

        await Send(engine, RegistryAction.Delete, "/teams/g/notes/n/versions/1");
        var resourceAfterVersionRemoval = await Read("/teams/g/notes/n/meta");
        await Assert.That(ModifiedAt(resourceAfterVersionRemoval)).IsGreaterThan(ModifiedAt(resourceAfterUpdate));

        await Send(engine, RegistryAction.Delete, "/teams/g/notes/n");
        var groupAfterResourceRemoval = await Read("/teams/g");
        await Assert.That(ModifiedAt(groupAfterResourceRemoval)).IsGreaterThan(ModifiedAt(groupAfterVersion));
        await Assert.That(ModifiedAt(await Read("/"))).IsEqualTo(ModifiedAt(rootAfterGroup));

        await Send(engine, RegistryAction.Delete, "/teams/g");
        await Assert.That(ModifiedAt(await Read("/"))).IsGreaterThan(ModifiedAt(rootAfterGroup));
    }

    [Test]
    public async Task ReadonlyPersistenceRejectsBeforeAnyBackendOrPayloadAccess()
    {
        var engine = Create(persistence: new ReadonlyPersistence());
        using var body = new MemoryStream([1, 2, 3]);
        await ExpectCode(() => engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/teams/g/files/f"))
        {
            Document = body
        }, Writer()), "readonly");
        await Assert.That(body.Position).IsEqualTo(0L);
    }

    [Test]
    public async Task ResponseBudgetRejectsBeforeCommitAndWorkingSetIsBounded()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(persistence: store, limits: new() { MaxResponseBytes = 16 });
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/teams/g", "{}"), "too_large");
        var recovered = Create(persistence: store);
        await Assert.That((await Send(recovered, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
        var tinyWorkingSet = Create(persistence: store, limits: new() { MaxWorkingSetBytes = 1 });
        await ExpectCode(() => Send(tinyWorkingSet, RegistryAction.Read, "/"), "operation_limit");
    }

    [Test]
    public async Task DeniedDefaultVersionIsNeverLeakedThroughResourceProjection()
    {
        var store = new InMemoryRegistryPersistence();
        await Send(Create(persistence: store), RegistryAction.Replace, "/teams/g/notes/n", """{"name":"secret"}""");
        var restricted = Create(persistence: store, policy: new HideVersions());
        await ExpectCode(() => Send(restricted, RegistryAction.Read, "/teams/g/notes/n"), "forbidden");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AdvancingClock(DateTimeOffset start) : TimeProvider
    {
        private int _seconds;
        public override DateTimeOffset GetUtcNow() => start.AddSeconds(++_seconds);
    }

    private sealed class ReadonlyPersistence : IRegistryPersistence
    {
        public bool IsReadOnly => true;
        public ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A rejected write must not read persistence.");
        public ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration, IReadOnlyList<RegistryMutation> mutations,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("A readonly backend must never prepare.");
    }

    private sealed class HideVersions : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(access != RegistryAccess.Read || path.Kind != RegistryPathKind.Version);
    }
}
