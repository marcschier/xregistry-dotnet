// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;
using static XRegistry.Server.Tests.RegistryScalabilityTests;

namespace XRegistry.Server.Tests;

public class RegistryConstraintSchedulingTests
{
    private const string Model = """
        {"groups":{"dirs":{"singular":"dir","attributes":{"region":{"type":"object","attributes":{"code":"string"}}},
          "constraints":{"records.shade":{"equals":"region.code"}},
          "resources":{"records":{"singular":"record","hasdocument":false,"attributes":{"shade":"string"}}}}}}
        """;

    [Test]
    public async Task UnrelatedGroupMetadataDoesNotTouchVersionsButNestedEqualsChangesRevalidateUntouchedResources()
    {
        var persistence = new InMemoryRegistryPersistence();
        var engine = Create(Model, persistence);
        await Send(engine, RegistryAction.Replace, "/dirs/g", """{"region":{"code":"red"}}""");
        await Send(engine, RegistryAction.Post, "/dirs/g/records", """{"a":{"shade":"red"},"b":{"shade":"red"}}""");
        var before = await Send(engine, RegistryAction.Read, "/dirs/g/records/b");
        await Send(engine, RegistryAction.Patch, "/", """{"dirs":{"g":{"name":"unrelated metadata"}}}""");
        await Assert.That(Epoch(await Send(engine, RegistryAction.Read, "/dirs/g/records/b"))).IsEqualTo(Epoch(before));
        var generation = await Generation(persistence);
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/", """
            {"dirs":{"g":{"region":{"code":"blue"},"records":{"a":{"shade":"blue"}}}}}
            """), "constraint_failure");
        await Assert.That(await Generation(persistence)).IsEqualTo(generation);
        await Assert.That((await Send(engine, RegistryAction.Read, "/dirs/g/records/a")).Metadata!.RootElement.GetProperty("shade").GetString())
            .IsEqualTo("red");
        await Assert.That((await Send(engine, RegistryAction.Read, "/dirs/g")).Metadata!.RootElement
            .GetProperty("region").GetProperty("code").GetString()).IsEqualTo("red");
        await Send(engine, RegistryAction.Patch, "/dirs/g", """
            {"region":{"code":"blue"},"records":{"a":{"shade":"blue"},"b":{"shade":"blue"}}}
            """);
        await Assert.That((await Send(engine, RegistryAction.Read, "/dirs/g/records/b")).Metadata!.RootElement.GetProperty("shade").GetString())
            .IsEqualTo("blue");
    }

    [Test]
    public async Task ChangedGroupConstraintDefaultsStillAuthorizeEveryIndirectVersionMutation()
    {
        var policy = new VersionPolicy();
        var persistence = new InMemoryRegistryPersistence();
        var engine = Create(policy: policy, persistence: persistence);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", "{}");
        var before = await Send(engine, RegistryAction.Read, "/teams/g/notes/n");
        var generation = await Generation(persistence);
        policy.Deny = true;
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g", """
            {"constraints":{"notes.description":{"default":"indirect default"}}}
            """), "forbidden");
        await Assert.That(await Generation(persistence)).IsEqualTo(generation);
        var current = await Send(engine, RegistryAction.Read, "/teams/g/notes/n");
        await Assert.That(current.Metadata!.RootElement.TryGetProperty("description", out _)).IsFalse();
        await Assert.That(Epoch(current)).IsEqualTo(Epoch(before));
    }

    [Test]
    public async Task ChangedGroupConstraintDefaultsCannotMutateReadonlyResources()
    {
        var engine = await RegistryReadonlyIgnoreTests.SeedReadonly();
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g", """
            {"constraints":{"notes.description":{"default":"indirect default"}}}
            """), "readonly");
        var locked = await Send(engine, RegistryAction.Read, "/teams/g/notes/locked");
        var open = await Send(engine, RegistryAction.Read, "/teams/g/notes/open");
        await Assert.That(locked.Metadata!.RootElement.TryGetProperty("description", out _)).IsFalse();
        await Assert.That(open.Metadata!.RootElement.TryGetProperty("description", out _)).IsFalse();
    }

    [Test]
    [Arguments("/dirs/g/records/b")]
    [Arguments("/dirs/g/records/b/versions")]
    public async Task MembershipDependentConstraintsSeeDeletionOfTheLastVersionBeforePublication(string path)
    {
        var model = """
            {"groups":{"dirs":{"singular":"dir","constraints":{"records.owners":{"equals":"recordscount"}},
              "resources":{"records":{"singular":"record","hasdocument":false,"attributes":{"owners":"uinteger"}}}}}}
            """;
        var persistence = new InMemoryRegistryPersistence();
        var engine = Create(model, persistence);
        await Send(engine, RegistryAction.Post, "/dirs/g/records", """{"a":{"owners":2},"b":{"owners":2}}""");
        var generation = await Generation(persistence);
        await ExpectCode(() => Send(engine, RegistryAction.Delete, path), "constraint_failure");
        await Assert.That(await Generation(persistence)).IsEqualTo(generation);
        await Assert.That((await Send(engine, RegistryAction.Read, "/dirs/g")).Metadata!.RootElement.GetProperty("recordscount").GetInt32())
            .IsEqualTo(2);
        await Assert.That((await Send(engine, RegistryAction.Read, "/dirs/g/records/b")).Metadata!.RootElement.GetProperty("versionid").GetString())
            .IsEqualTo("1");
    }

    private sealed class VersionPolicy : IRegistryAuthorizationPolicy
    {
        internal bool Deny { get; set; }
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(!Deny ||
                access != RegistryAccess.Update || path.Kind != RegistryPathKind.Version);
    }
}
