using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class RegistryNestedVersionPrecedenceTests
{
    [Test]
    [Arguments("null")]
    [Arguments("42")]
    [Arguments("{}")]
    [Arguments("\"different\"")]
    public async Task NestedCurrentDefaultIgnoresTheEntireTopLevelVersionAttributeSet(string ignoredId)
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"versionid":"v1","name":"before"}""");
        var result = await Send(engine, RegistryAction.Replace, "/teams/g/notes/n",
            "{\"versionid\":" + ignoredId + ",\"name\":{},\"epoch\":999,\"versions\":{\"v1\":{\"name\":\"nested wins\"}}}");
        await Assert.That(result.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(result.Metadata.RootElement.GetProperty("name").GetString()).IsEqualTo("nested wins");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/v1")).Metadata!.RootElement
            .GetProperty("name").GetString()).IsEqualTo("nested wins");
    }

    [Test]
    [Arguments("""{"noteid":"wrong","versionid":null,"versions":{"v1":{"name":"not written"}}}""")]
    [Arguments("""{"versionid":null,"versions":{"v1":{"versionid":"wrong","name":"not written"}}}""")]
    public async Task IgnoredVersionAttributesDoNotDisableResourceOrNestedVersionIdentityGuards(string body)
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"versionid":"v1","name":"before"}""");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/teams/g/notes/n", body), "mismatched_id");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n")).Metadata!.RootElement
            .GetProperty("name").GetString()).IsEqualTo("before");
    }
}
