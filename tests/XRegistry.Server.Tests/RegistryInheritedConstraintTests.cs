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
}
