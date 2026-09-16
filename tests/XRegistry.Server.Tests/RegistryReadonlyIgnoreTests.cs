// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;
using static XRegistry.Server.Tests.RegistryCapabilityTests;

namespace XRegistry.Server.Tests;

public class RegistryReadonlyIgnoreTests
{
    [Test]
    public async Task ReadonlyIgnoreOmitsOnlyLockedResourcesAndPreservesOtherEpochGuards()
    {
        var engine = await SeedReadonly();
        var result = await Send(engine, RegistryAction.Patch, "/teams/g/notes", """
            {"locked":{"name":"not applied","epoch":999,"meta":{"readonly":false}},"open":{"epoch":0,"name":"updated"}}
            """, new KeyValuePair<string, string?>("ignore", "readonly"));
        await Assert.That(result.Metadata!.RootElement.EnumerateObject().Single().Name).IsEqualTo("open");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/locked")).Metadata!.RootElement.GetProperty("name").GetString()).IsEqualTo("locked");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/locked/meta")).Metadata!.RootElement.GetProperty("readonly").GetBoolean()).IsTrue();
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes", """
            {"locked":{"name":"ignored"},"open":{"epoch":0,"name":"stale"}}
            """, new KeyValuePair<string, string?>("ignore", "readonly")), "mismatched_epoch");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/open")).Metadata!.RootElement.GetProperty("name").GetString()).IsEqualTo("updated");
    }

    [Test]
    [Arguments("/teams/g/notes/locked")]
    [Arguments("/teams/g/notes/locked/meta")]
    [Arguments("/teams/g/notes/locked/versions")]
    [Arguments("/teams/g/notes/locked/versions/1")]
    public async Task IgnoringTheOnlyTargetedReadonlyResourceIsBadFlag(string path)
    {
        var engine = await SeedReadonly();
        var action = path.EndsWith("/versions", StringComparison.Ordinal) ? RegistryAction.Post : RegistryAction.Patch;
        await ExpectCode(() => Send(engine, action, path, "{}", new KeyValuePair<string, string?>("ignore", "readonly")), "bad_flag");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("*")]
    [Arguments("readonly,epoch")]
    public async Task AllIgnoreSpellingsSkipReadonlyButDoNotEraseNestedIdentityChecks(string? ignore)
    {
        var engine = await SeedReadonly();
        var result = await Send(engine, RegistryAction.Patch, "/teams/g/notes", """
            {"locked":{"notdefined":{}},"open":{"epoch":999,"name":"changed"}}
            """, new KeyValuePair<string, string?>("ignore", ignore));
        await Assert.That(result.Metadata!.RootElement.EnumerateObject().Single().Name).IsEqualTo("open");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes", """
            {"locked":{},"open":{"noteid":"wrong"}}
            """, new KeyValuePair<string, string?>("ignore", ignore)), "mismatched_id");
    }

    [Test]
    public async Task NestedAndRepeatedReadonlyIgnorePreserveTheLockedSubtree()
    {
        var engine = await SeedReadonly();
        var response = await Send(engine, RegistryAction.Post, "/teams/g", """
            {"notes":{"locked":{"name":"ignored"},"open":{"epoch":999,"name":"updated"}}}
            """, new("ignore", "readonly"), new("ignore", "epoch"));
        await Assert.That(response.Metadata!.RootElement.GetProperty("notes").EnumerateObject().Single().Name).IsEqualTo("open");
        await Send(engine, RegistryAction.Delete, "/teams/g/notes", null, new KeyValuePair<string, string?>("ignore", "readonly"));
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes")).Metadata!.RootElement.EnumerateObject().Single().Name).IsEqualTo("locked");
        await ExpectCode(() => Send(engine, RegistryAction.Delete, "/teams/g", null,
            new KeyValuePair<string, string?>("ignore", "readonly")), "bad_flag");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/locked")).Metadata!.RootElement.GetProperty("name").GetString()).IsEqualTo("locked");
    }

    [Test]
    public async Task UnsupportedReadonlyIgnoreAndMalformedCollectionEntriesReject()
    {
        var engine = await SeedReadonly();
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes",
            """{"locked":null}""", new KeyValuePair<string, string?>("ignore", "readonly")), "bad_request");
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"ignores":["epoch"]}""");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes",
            """{"locked":{},"open":{}}""", new KeyValuePair<string, string?>("ignore", "readonly")), "bad_ignore");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes",
            """{"locked":{}}"""), "readonly");
    }
    internal static async Task<RegistryEngine> SeedReadonly()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = MutableEngine(store);
        await Send(engine, RegistryAction.Post, "/teams/g/notes", """{"locked":{"name":"locked"},"open":{"name":"open"}}""");
        using var snapshot = await store.ReadSnapshotAsync();
        var record = snapshot.Find("/teams/g/notes/locked")!;
        var metadata = JsonNode.Parse(record.Metadata.RootElement.GetRawText())!.AsObject();
        metadata["attributes"]!["readonly"] = true;
        using var candidate = await store.PrepareAsync(snapshot.Generation,
            [RegistryMutation.Put(record.Key, RegistryJson.Parse(metadata.ToJsonString()))]);
        await candidate.CommitAsync();
        return engine;
    }
}
