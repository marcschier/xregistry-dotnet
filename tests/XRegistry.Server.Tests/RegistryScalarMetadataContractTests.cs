using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class RegistryScalarMetadataContractTests
{
    private const string ContractModel = """
        {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false}}}}}
        """;

    [Test]
    [Arguments("/gs/g", "name")]
    [Arguments("/gs/g", "documentation")]
    [Arguments("/gs/g", "icon")]
    [Arguments("/gs/g/rs/r", "format")]
    public async Task ReplaceRejectsEmptySystemFieldsWithoutPublication(string path, string name)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(ContractModel, store);
        var before = (await Send(engine, RegistryAction.Read, "/")).Metadata!;
        using var beforeSnapshot = await store.ReadSnapshotAsync();

        await ExpectCode(() => Send(engine, RegistryAction.Replace, path, "{\"" + name + "\":\"\"}"),
            "invalid_attribute");

        var after = (await Send(engine, RegistryAction.Read, "/")).Metadata!;
        await Assert.That(after.RootElement.GetRawText()).IsEqualTo(before.RootElement.GetRawText());
        await Assert.That(after.RootElement.GetProperty("gscount").GetInt32()).IsEqualTo(0);
        using var afterSnapshot = await store.ReadSnapshotAsync();
        await Assert.That(afterSnapshot.Generation).IsEqualTo(beforeSnapshot.Generation);
        await ExpectCode(() => Send(engine, RegistryAction.Read, path), "not_found");
    }

    [Test]
    [Arguments(4085, true)]
    [Arguments(4086, false)]
    [Arguments(4096, false)]
    public async Task ReplaceEnforcesTheInclusiveScalarLimitBeforePublication(int length, bool accepted)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(ContractModel, store);
        var before = (await Send(engine, RegistryAction.Read, "/")).Metadata!;
        using var beforeSnapshot = await store.ReadSnapshotAsync();
        var text = new string('x', length);
        var input = "{\"description\":\"" + text + "\"}";

        if (accepted)
        {
            var created = await Send(engine, RegistryAction.Replace, "/gs/g", input);
            await Assert.That(created.Kind).IsEqualTo(RegistryResultKind.Created);
            await Assert.That(created.Metadata!.RootElement.GetProperty("description").GetString()).IsEqualTo(text);
            var stored = (await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!;
            await Assert.That(stored.RootElement.GetProperty("description").GetString()).IsEqualTo(text);
        }
        else
        {
            await ExpectCode(() => Send(engine, RegistryAction.Replace, "/gs/g", input), "invalid_attribute");
            var after = (await Send(engine, RegistryAction.Read, "/")).Metadata!;
            await Assert.That(after.RootElement.GetRawText()).IsEqualTo(before.RootElement.GetRawText());
            await ExpectCode(() => Send(engine, RegistryAction.Read, "/gs/g"), "not_found");
        }

        using var afterSnapshot = await store.ReadSnapshotAsync();
        await Assert.That(afterSnapshot.Generation).IsEqualTo(beforeSnapshot.Generation + (accepted ? 1 : 0));
    }

    [Test]
    [Arguments("/", "name")]
    [Arguments("/gs/g", "icon")]
    [Arguments("/gs/g/rs/r", "documentation")]
    [Arguments("/gs/g/rs/r/versions/v1", "format")]
    public async Task PatchRejectsEmptySystemFieldsAcrossEntityEndpointsWithoutChangingState(string path, string name)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(ContractModel, store);
        await Send(engine, RegistryAction.Replace, path, Text(name, "before"));
        var before = (await Send(engine, RegistryAction.Read, path)).Metadata!;
        using var beforeSnapshot = await store.ReadSnapshotAsync();

        await ExpectCode(() => Send(engine, RegistryAction.Patch, path, Text(name, "")), "invalid_attribute");

        var after = (await Send(engine, RegistryAction.Read, path)).Metadata!;
        await Assert.That(after.RootElement.GetRawText()).IsEqualTo(before.RootElement.GetRawText());
        using var afterSnapshot = await store.ReadSnapshotAsync();
        await Assert.That(afterSnapshot.Generation).IsEqualTo(beforeSnapshot.Generation);
    }

    [Test]
    [Arguments("/gs/g", "name")]
    [Arguments("/gs/g/rs/r", "format")]
    public async Task PatchPreservesLegalEmptyDescriptionAndExplicitSystemNullDeletion(string path, string name)
    {
        var engine = Create(ContractModel);
        await Send(engine, RegistryAction.Replace, path, Text(name, "before"));
        var patched = await Send(engine, RegistryAction.Patch, path,
            "{\"description\":\"\",\"" + name + "\":null}");

        await Assert.That(patched.Metadata!.RootElement.GetProperty("description").GetString()).IsEqualTo("");
        await Assert.That(patched.Metadata.GetPresence(name)).IsEqualTo(JsonPresence.Absent);
        var stored = (await Send(engine, RegistryAction.Read, path)).Metadata!;
        await Assert.That(stored.RootElement.GetProperty("description").GetString()).IsEqualTo("");
        await Assert.That(stored.GetPresence(name)).IsEqualTo(JsonPresence.Absent);
    }

    [Test]
    public async Task ReadonlySystemInputIsIgnoredBeforeNonemptyAndSizeChecks()
    {
        var engine = Create("""
            {"groups":{"gs":{"singular":"g","attributes":{
              "name":{"type":"string","readonly":true,"required":true,"default":"server"},
              "kind":{"type":"string","readonly":true,"required":true,"default":"on","ifvalues":{
                "on":{"siblingattributes":{"extra":{"type":"string","readonly":true,"required":true,"default":"kept"}}}
              }}}}}}
            """);
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"description":""}""");
        var patched = await Send(engine, RegistryAction.Patch, "/gs/g", new JsonObject
        {
            ["name"] = "",
            ["kind"] = new string('x', 8192),
            ["extra"] = new string('x', 8192),
            ["description"] = ""
        }.ToJsonString());

        await Assert.That(patched.Metadata!.RootElement.GetProperty("name").GetString()).IsEqualTo("server");
        await Assert.That(patched.Metadata.RootElement.GetProperty("kind").GetString()).IsEqualTo("on");
        await Assert.That(patched.Metadata.RootElement.GetProperty("extra").GetString()).IsEqualTo("kept");
        var stored = (await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!;
        await Assert.That(stored.RootElement.GetProperty("description").GetString()).IsEqualTo("");
        await Assert.That(stored.RootElement.GetProperty("extra").GetString()).IsEqualTo("kept");
    }

    [Test]
    public async Task SameNamedCustomFieldsAndLongMapItemsRetainTheirOwnContracts()
    {
        var engine = Create("""
            {"documentation":"","groups":{"gs":{"singular":"g","attributes":{
              "format":"string","body":{"type":"object","attributes":{
                "name":"string","format":"string","documentation":"url","icon":"url"}}}}}}
            """);
        var text = new string('x', 8192);
        var metadata = new JsonObject
        {
            ["description"] = "",
            ["format"] = "",
            ["labels"] = new JsonObject { ["name"] = "", ["long.value"] = text },
            ["body"] = new JsonObject { ["name"] = "", ["format"] = "", ["documentation"] = "", ["icon"] = "" }
        };
        await Send(engine, RegistryAction.Replace, "/gs/g", metadata.ToJsonString());
        var stored = (await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!.RootElement;

        await Assert.That(stored.GetProperty("description").GetString()).IsEqualTo("");
        await Assert.That(stored.GetProperty("format").GetString()).IsEqualTo("");
        await Assert.That(stored.GetProperty("labels").GetProperty("name").GetString()).IsEqualTo("");
        await Assert.That(stored.GetProperty("labels").GetProperty("long.value").GetString()).IsEqualTo(text);
        foreach (var name in new[] { "name", "format", "documentation", "icon" })
        {
            await Assert.That(stored.GetProperty("body").GetProperty(name).GetString()).IsEqualTo("");
        }
    }

    [Test]
    [Arguments("name", 0)]
    [Arguments("description", 4086)]
    public async Task InvalidRuntimeDefaultsRejectWithoutPublishingGeneratedEntities(string name, int length)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create("{\"groups\":{\"gs\":{\"singular\":\"g\",\"attributes\":{\"" + name +
            "\":{\"type\":\"string\",\"required\":true,\"default\":\"" + new string('x', length) + "\"}}}}}", store);
        var before = (await Send(engine, RegistryAction.Read, "/")).Metadata!;
        using var beforeSnapshot = await store.ReadSnapshotAsync();

        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/gs/g", "{}"), "invalid_attribute");

        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(before.RootElement.GetRawText());
        using var afterSnapshot = await store.ReadSnapshotAsync();
        await Assert.That(afterSnapshot.Generation).IsEqualTo(beforeSnapshot.Generation);
    }

    [Test]
    public async Task RetainedConditionalPatchAppliesTheScalarLimitWithoutLosingContext()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create("""
            {"groups":{"gs":{"singular":"g","attributes":{
              "kind":{"type":"string","ifvalues":{"on":{"siblingattributes":{"extra":"string"}}}}
            }}}}
            """, store);
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"kind":"on","extra":"before"}""");
        var text = new string('x', 4091);
        var accepted = await Send(engine, RegistryAction.Patch, "/gs/g", Text("extra", text));
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("kind").GetString()).IsEqualTo("on");
        await Assert.That(accepted.Metadata.RootElement.GetProperty("extra").GetString()).IsEqualTo(text);
        using var beforeSnapshot = await store.ReadSnapshotAsync();

        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/gs/g", Text("extra", text + "x")), "invalid_attribute");

        var stored = (await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!;
        await Assert.That(stored.RootElement.GetRawText()).IsEqualTo(accepted.Metadata.RootElement.GetRawText());
        using var afterSnapshot = await store.ReadSnapshotAsync();
        await Assert.That(afterSnapshot.Generation).IsEqualTo(beforeSnapshot.Generation);
    }

    [Test]
    public async Task InvalidNestedScalarRollsBackAllSiblingCollectionWrites()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(ContractModel, store);
        var before = (await Send(engine, RegistryAction.Read, "/")).Metadata!;
        using var beforeSnapshot = await store.ReadSnapshotAsync();
        var input = "{\"gs\":{\"good\":{\"description\":\"valid\"},\"bad\":{\"description\":\"" +
            new string('x', 4086) + "\"}}}";

        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/", input), "invalid_attribute");

        var after = (await Send(engine, RegistryAction.Read, "/")).Metadata!;
        await Assert.That(after.RootElement.GetRawText()).IsEqualTo(before.RootElement.GetRawText());
        await Assert.That(after.RootElement.GetProperty("gscount").GetInt32()).IsEqualTo(0);
        using var afterSnapshot = await store.ReadSnapshotAsync();
        await Assert.That(afterSnapshot.Generation).IsEqualTo(beforeSnapshot.Generation);
    }

    private static string Text(string name, string text) => new JsonObject { [name] = text }.ToJsonString();
}
