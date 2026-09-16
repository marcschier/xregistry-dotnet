// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class RegistryModelMutationSemanticsTests
{
    private const string ModelSource = """
        {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false,
          "attributes":{"color":{"type":"string","enum":["red","blue"]}}}}}}}
        """;

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExistingTypeSingularNamesCannotChangeEvenWithoutInstances(bool resource)
    {
        var persistence = new InMemoryRegistryPersistence();
        var engine = Create(ModelSource, persistence);
        await Send(engine, RegistryAction.Replace, "/modelsource", ModelSource);
        using var before = await persistence.ReadSnapshotAsync();
        var changed = JsonNode.Parse(ModelSource)!;
        var type = resource ? changed["groups"]!["gs"]!["resources"]!["rs"]! : changed["groups"]!["gs"]!;
        type["singular"] = "other";
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/modelsource", changed.ToJsonString()), "model_error");
        using var after = await persistence.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        var retained = (await Send(engine, RegistryAction.Read, "/model")).Metadata!.RootElement.GetProperty("groups").GetProperty("gs");
        await Assert.That((resource ? retained.GetProperty("resources").GetProperty("rs") : retained)
            .GetProperty("singular").GetString()).IsEqualTo(resource ? "r" : "g");
    }

    [Test]
    [Arguments("enum")]
    [Arguments("required")]
    [Arguments("matchversions")]
    [Arguments("isdefault")]
    public async Task ExistingVersionFailuresDuringModelReplacementUseModelComplianceError(string kind)
    {
        var persistence = new InMemoryRegistryPersistence();
        var engine = Create(ModelSource, persistence);
        await Send(engine, RegistryAction.Replace, "/gs/g/rs/r", """{"color":"red"}""");
        if (kind is "matchversions" or "isdefault")
        {
            await Send(engine, RegistryAction.Post, "/gs/g/rs/r", """{"color":"blue"}""");
        }
        using var before = await persistence.ReadSnapshotAsync();
        var changed = JsonNode.Parse(ModelSource)!;
        var attributes = changed["groups"]!["gs"]!["resources"]!["rs"]!["attributes"]!;
        if (kind == "enum") { attributes["color"]!["enum"] = new JsonArray("blue"); }
        else if (kind == "required") { attributes["requiredfield"] = new JsonObject { ["type"] = "string", ["required"] = true }; }
        else if (kind == "matchversions") { attributes["color"]!["matchversions"] = true; }
        else { attributes["isdefault"] = new JsonObject { ["matchversions"] = true }; }
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/modelsource", changed.ToJsonString()), "model_compliance_error");
        using var after = await persistence.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        await Assert.That((await Send(engine, RegistryAction.Read, "/gs/g/rs/r")).Metadata!.RootElement
            .GetProperty("color").GetString()).IsEqualTo(kind is "matchversions" or "isdefault" ? "blue" : "red");
        var retained = (await Send(engine, RegistryAction.Read, "/modelsource")).Metadata!.RootElement;
        await Assert.That(retained.GetProperty("groups").GetProperty("gs").GetProperty("resources").GetProperty("rs")
            .GetProperty("attributes").GetProperty("color").GetProperty("enum").GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task MatchVersionsUsesTheActualDefaultMembershipRatherThanStagedPlaceholderValues()
    {
        const string model = """
            {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false,
              "attributes":{"isdefault":{"matchversions":true}}}}}}}
            """;
        var persistence = new InMemoryRegistryPersistence();
        var engine = Create(model, persistence);
        await Send(engine, RegistryAction.Replace, "/gs/g/rs/r/versions/v1", "{}");
        using var before = await persistence.ReadSnapshotAsync();
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/gs/g/rs/r/versions/v2", "{}"), "mismatched_version_attribute");
        using var after = await persistence.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        var retained = (await Send(engine, RegistryAction.Read, "/gs/g/rs/r")).Metadata!.RootElement;
        await Assert.That(retained.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(retained.GetProperty("versionscount").GetInt32()).IsEqualTo(1);
        await Assert.That((await Send(engine, RegistryAction.Read, "/gs/g/rs/r/versions/v1")).Metadata!.RootElement
            .GetProperty("isdefault").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task DefaultMembershipMatchingUsesOnlyTheVersionsRetainedAtCommit()
    {
        const string model = """
            {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false,"maxversions":1,
              "attributes":{"isdefault":{"matchversions":true}}}}}}}
            """;
        var engine = Create(model);
        await Send(engine, RegistryAction.Replace, "/gs/g/rs/r/versions/v1", "{}");
        await Send(engine, RegistryAction.Replace, "/gs/g/rs/r/versions/v2", "{}");
        var retained = (await Send(engine, RegistryAction.Read, "/gs/g/rs/r")).Metadata!.RootElement;
        await Assert.That(retained.GetProperty("versionid").GetString()).IsEqualTo("v2");
        await Assert.That(retained.GetProperty("versionscount").GetInt32()).IsEqualTo(1);
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/gs/g/rs/r/versions/v1"), "not_found");
    }
}
