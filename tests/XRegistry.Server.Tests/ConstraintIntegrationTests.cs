// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class ConstraintIntegrationTests
{
    private const string ConstrainedModel = """
        {"groups":{"teams":{"singular":"team","attributes":{"color":"string"},
          "constraints":{"notes.color":{"equals":"color"}},
          "resources":{"notes":{"singular":"note","hasdocument":false,"attributes":{
            "color":{"type":"string","enum":["red","blue"]},
            "shared":{"type":"integer","matchversions":true},
            "settings":{"type":"object","attributes":{"enabled":{"type":"boolean","required":true,"default":true}}},
            "*":{"type":"string"}
          }}}}}}
        """;

    [Test]
    public async Task ConstraintsAndMatchVersionsAreEnforcedAcrossAllVersions()
    {
        var engine = Create(ConstrainedModel);
        await Send(engine, RegistryAction.Replace, "/teams/g", """{"color":"red"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"color":"red","shared":1,"other":"allowed"}""");
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"color":"blue","shared":1}"""), "constraint_failure");
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"color":"red","shared":2}"""), "mismatched_version_attribute");
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"color":"red"}"""), "mismatched_version_attribute");
        var second = await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"color":"red","shared":1.0}""");
        await Assert.That(second.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("2");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g", """{"color":"blue"}"""), "constraint_failure");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g")).Metadata!.RootElement.GetProperty("color").GetString()).IsEqualTo("red");
    }

    [Test]
    public async Task GroupInstanceConstraintsApplyDefaultsAndCannotWidenTheModel()
    {
        var engine = Create(ConstrainedModel);
        await Send(engine, RegistryAction.Replace, "/teams/g", """
            {"color":"red","constraints":{"notes.color":{"enum":["red"],"default":"red"}}}
            """);
        var result = await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", "{}");
        await Assert.That(result.Metadata!.RootElement.GetProperty("color").GetString()).IsEqualTo("red");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g", """
            {"constraints":{"notes.color":{"enum":["not-in-model"]}}}
            """), "invalid_attribute");
        var group = await Send(engine, RegistryAction.Read, "/teams/g");
        await Assert.That(group.Metadata!.RootElement.GetProperty("constraints").GetProperty("notes.color").GetProperty("enum")[0].GetString()).IsEqualTo("red");
    }
}
