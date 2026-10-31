// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class RegistryInheritedConstraintTests
{
    [Test]
    [Arguments("{}")]
    [Arguments("""{"enum":null}""")]
    [Arguments("""{"enum":[]}""")]
    public async Task EmptyOrAbsentInstanceEnumsRetainTheInheritedModelRestriction(string facet)
    {
        const string model = """
            {"groups":{"gs":{"singular":"g","constraints":{"rs.color":{"enum":["red"]}},
              "resources":{"rs":{"singular":"r","hasdocument":false,
                "attributes":{"color":{"type":"string","enum":["red","blue"]}}}}}}}
            """;
        var engine = Create(model);
        await Send(engine, RegistryAction.Replace, "/gs/g", "{\"constraints\":{\"rs.color\":" + facet + "}}");
        var accepted = await Send(engine, RegistryAction.Replace, "/gs/g/rs/red", """{"color":"red"}""");
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("color").GetString()).IsEqualTo("red");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/gs/g/rs/blue", """{"color":"blue"}"""), "constraint_failure");
        await Assert.That((await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!.RootElement.GetProperty("rscount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"equals":null}""")]
    [Arguments("""{"equals":""}""")]
    public async Task EmptyOrAbsentInstanceEqualsRetainsTheInheritedGroupComparison(string facet)
    {
        const string model = """
            {"groups":{"gs":{"singular":"g","attributes":{"color":{"type":"string"}},
              "constraints":{"rs.color":{"equals":"color"}},
              "resources":{"rs":{"singular":"r","hasdocument":false,"attributes":{"color":{"type":"string"}}}}}}}
            """;
        var engine = Create(model);
        await Send(engine, RegistryAction.Replace, "/gs/g", "{\"color\":\"red\",\"constraints\":{\"rs.color\":" + facet + "}}");
        await Send(engine, RegistryAction.Replace, "/gs/g/rs/red", """{"color":"red"}""");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/gs/g/rs/blue", """{"color":"blue"}"""), "constraint_failure");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/gs/g", """{"color":"blue"}"""), "constraint_failure");
        await Assert.That((await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!.RootElement.GetProperty("color").GetString()).IsEqualTo("red");
    }

    [Test]
    [Arguments("""{"enum":["red"]}""", true, false)]
    [Arguments("""{"enum":["red","blue"]}""", true, true)]
    [Arguments("""{"enum":["red","green"]}""", false, false)]
    [Arguments("""{"enum":["red","blue","green"]}""", false, false)]
    public async Task GroupInstanceEnumsMustBeSubsetsOfTypeAndResourceEnums(string facet, bool valid, bool allowsBlue)
    {
        const string model = """
            {"groups":{"gs":{"singular":"g","constraints":{"rs.color":{"enum":["red","blue"]}},
              "resources":{"rs":{"singular":"r","hasdocument":false,
                "attributes":{"color":{"type":"string","enum":["red","blue","green"]}}}}}}}
            """;
        var engine = Create(model);
        if (!valid)
        {
            await ExpectCode(
                () => Send(engine, RegistryAction.Replace, "/gs/g", "{\"constraints\":{\"rs.color\":" + facet + "}}"),
                "invalid_attribute");
            return;
        }

        await Send(engine, RegistryAction.Replace, "/gs/g", "{\"constraints\":{\"rs.color\":" + facet + "}}");
        await Send(engine, RegistryAction.Replace, "/gs/g/rs/red", """{"color":"red"}""");
        if (allowsBlue)
        {
            await Send(engine, RegistryAction.Replace, "/gs/g/rs/blue", """{"color":"blue"}""");
        }
        else
        {
            await ExpectCode(
                () => Send(engine, RegistryAction.Replace, "/gs/g/rs/blue", """{"color":"blue"}"""),
                "constraint_failure");
        }
    }

    [Test]
    [Arguments("{}", "red", true)]
    [Arguments("""{"default":null}""", "red", true)]
    [Arguments("""{"default":"blue"}""", "blue", true)]
    [Arguments("""{"default":"green"}""", null, false)]
    [Arguments("""{"enum":["red"],"default":"blue"}""", null, false)]
    public async Task GroupInstanceDefaultsRetainOrNarrowTheInheritedConstraint(string facet, string? expected, bool valid)
    {
        const string model = """
            {"groups":{"gs":{"singular":"g","constraints":{"rs.color":{"enum":["red","blue"],"default":"red"}},
              "resources":{"rs":{"singular":"r","hasdocument":false,
                "attributes":{"color":{"type":"string","enum":["red","blue","green"]}}}}}}}
            """;
        var engine = Create(model);
        if (!valid)
        {
            await ExpectCode(
                () => Send(engine, RegistryAction.Replace, "/gs/g", "{\"constraints\":{\"rs.color\":" + facet + "}}"),
                "invalid_attribute");
            return;
        }

        await Send(engine, RegistryAction.Replace, "/gs/g", "{\"constraints\":{\"rs.color\":" + facet + "}}");
        var resource = await Send(engine, RegistryAction.Replace, "/gs/g/rs/r", "{}");
        await Assert.That(resource.Metadata!.RootElement.GetProperty("color").GetString()).IsEqualTo(expected);
    }

    [Test]
    [Arguments("color", true)]
    [Arguments("other", false)]
    public async Task GroupInstanceEqualsMustMatchTheTypeExpression(string expression, bool valid)
    {
        const string model = """
            {"groups":{"gs":{"singular":"g","attributes":{"color":{"type":"string"},"other":{"type":"string"}},
              "constraints":{"rs.color":{"equals":"color"}},
              "resources":{"rs":{"singular":"r","hasdocument":false,"attributes":{"color":{"type":"string"}}}}}}}
            """;
        var engine = Create(model);
        var body = "{\"color\":\"red\",\"other\":\"blue\",\"constraints\":{\"rs.color\":{\"equals\":\"" + expression + "\"}}}";
        if (!valid)
        {
            await ExpectCode(() => Send(engine, RegistryAction.Replace, "/gs/g", body), "invalid_attribute");
            return;
        }

        await Send(engine, RegistryAction.Replace, "/gs/g", body);
        await Send(engine, RegistryAction.Replace, "/gs/g/rs/red", """{"color":"red"}""");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/gs/g/rs/blue", """{"color":"blue"}"""), "constraint_failure");
    }

    [Test]
    public async Task GroupInstanceMayIntroduceEqualsForATypeConstraintWithoutEquals()
    {
        const string model = """
            {"groups":{"gs":{"singular":"g","attributes":{"color":{"type":"string"}},
              "constraints":{"rs.color":{"enum":["red","blue"]}},
              "resources":{"rs":{"singular":"r","hasdocument":false,
                "attributes":{"color":{"type":"string","enum":["red","blue"]}}}}}}}
            """;
        var engine = Create(model);
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"color":"red","constraints":{"rs.color":{"equals":"color"}}}""");
        await Send(engine, RegistryAction.Replace, "/gs/g/rs/red", """{"color":"red"}""");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/gs/g/rs/blue", """{"color":"blue"}"""), "constraint_failure");
    }
}
