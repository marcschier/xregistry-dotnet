using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;

namespace XRegistry.Server.Tests;

public class RegistryReadonlyValidationEpochTests
{
    [Test]
    public async Task MetaOnlyValidationRecomputationPersistsWithoutUpdatingVersionEpochOrEvents()
    {
        var store = new InMemoryRegistryPersistence();
        var clock = new RegistryDeprecationLifecycleTests.RemovalClock();
        var engine = new RegistryEngine(new()
        {
            RegistryId = "validation-epoch",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse("""
                {"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema",
                  "validateformat":true,"validatecompatibility":true}}}}}
                """)),
            ResourceValidator = new BuiltInRegistryResourceValidator(),
            TimeProvider = clock
        }, store, new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/teams/g/schemas/s$details",
            """{"format":"Avro/1.11.0","schema":"int"}""");
        var before = (await Send(engine, RegistryAction.Read, "/teams/g/schemas/s/versions/1$details")).Metadata!.RootElement;
        await Assert.That(before.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(before.TryGetProperty("compatibilityvalidated", out _)).IsFalse();
        var metaBefore = (await Send(engine, RegistryAction.Read, "/teams/g/schemas/s/meta")).Metadata!.RootElement;
        clock.Utc += TimeSpan.FromSeconds(1);
        var result = await Send(engine, RegistryAction.Patch, "/teams/g/schemas/s/meta", """{"compatibility":"backward"}""");
        var after = (await Send(engine, RegistryAction.Read, "/teams/g/schemas/s/versions/1$details")).Metadata!.RootElement;
        await Assert.That(after.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();
        await Assert.That(after.GetProperty("epoch").GetRawText()).IsEqualTo(before.GetProperty("epoch").GetRawText());
        await Assert.That(after.GetProperty("modifiedat").GetString()).IsEqualTo(before.GetProperty("modifiedat").GetString());
        await Assert.That(result.Metadata!.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(metaBefore.GetProperty("epoch").GetInt32() + 1);
        var batch = (await engine.ReadEventBatchesAsync(Writer())).Single(item => item.CorrelationId == result.CorrelationId);
        var events = batch.Events.RootElement.EnumerateArray().ToArray();
        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].GetProperty("type").GetString()).IsEqualTo("io.xregistry.resource.updated");
        using var persisted = await store.ReadSnapshotAsync();
        var stored = persisted.Find("/teams/g/schemas/s/versions/1")!.Metadata.RootElement.GetProperty("attributes");
        await Assert.That(stored.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();
        await Assert.That(stored.GetProperty("epoch").GetRawText()).IsEqualTo(before.GetProperty("epoch").GetRawText());
        clock.Utc += TimeSpan.FromSeconds(1);
        await Send(engine, RegistryAction.Patch, "/teams/g/schemas/s/meta", """{"compatibility":null}""");
        var cleared = (await Send(engine, RegistryAction.Read, "/teams/g/schemas/s/versions/1$details")).Metadata!.RootElement;
        await Assert.That(cleared.TryGetProperty("compatibilityvalidated", out _)).IsFalse();
        await Assert.That(cleared.TryGetProperty("compatibilityvalidatedreason", out _)).IsFalse();
        await Assert.That(cleared.GetProperty("epoch").GetRawText()).IsEqualTo(before.GetProperty("epoch").GetRawText());
        clock.Utc += TimeSpan.FromSeconds(1);
        await Send(engine, RegistryAction.Patch, "/teams/g/schemas/s/versions/1$details", """{"description":"user update"}""");
        var changed = (await Send(engine, RegistryAction.Read, "/teams/g/schemas/s/versions/1$details")).Metadata!.RootElement;
        await Assert.That(changed.GetProperty("epoch").GetInt32()).IsEqualTo(before.GetProperty("epoch").GetInt32() + 1);
        await Assert.That(changed.GetProperty("modifiedat").GetString()).IsNotEqualTo(before.GetProperty("modifiedat").GetString());
    }

    [Test]
    public async Task ReadonlyValidationDoesNotChangeModifiedTimeVersionOrdering()
    {
        var clock = new RegistryDeprecationLifecycleTests.RemovalClock();
        var engine = new RegistryEngine(new()
        {
            RegistryId = "validation-order",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse("""
                {"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","versionmode":"modifiedat",
                  "validateformat":true,"validatecompatibility":true}}}}}
                """)),
            ResourceValidator = new BuiltInRegistryResourceValidator(),
            TimeProvider = clock
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/teams/g/schemas/s/versions/z-old$details",
            """{"format":"Avro/1.11.0","schema":"int"}""");
        clock.Utc += TimeSpan.FromSeconds(1);
        await Send(engine, RegistryAction.Replace, "/teams/g/schemas/s/versions/a-new$details",
            """{"format":"Avro/1.11.0","schema":"int"}""");
        var before = (await Send(engine, RegistryAction.Read, "/teams/g/schemas/s$details")).Metadata!.RootElement;
        await Assert.That(before.GetProperty("versionid").GetString()).IsEqualTo("a-new");
        clock.Utc += TimeSpan.FromSeconds(1);
        await Send(engine, RegistryAction.Patch, "/teams/g/schemas/s/meta", """{"compatibility":"backward"}""");
        var after = (await Send(engine, RegistryAction.Read, "/teams/g/schemas/s$details")).Metadata!.RootElement;
        await Assert.That(after.GetProperty("versionid").GetString()).IsEqualTo("a-new");
        await Assert.That(after.GetProperty("epoch").GetRawText()).IsEqualTo(before.GetProperty("epoch").GetRawText());
        await Assert.That(after.GetProperty("modifiedat").GetString()).IsEqualTo(before.GetProperty("modifiedat").GetString());
    }
}
