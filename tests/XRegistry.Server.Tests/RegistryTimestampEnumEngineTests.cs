using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;

namespace XRegistry.Server.Tests;

public class RegistryTimestampEnumEngineTests
{
    [Test]
    public async Task TimestampEnumSurvivesRepeatedEngineCompletionAndNullReset()
    {
        var engine = Create("""
            {"groups":{"gs":{"singular":"g","attributes":{
              "t":{"type":"timestamp","required":true,"default":"2026-09-11T14:00:00.123456789+02:00",
                "enum":["2026-09-11T14:00:00.123456789+02:00"]}
            }}}}
            """);
        var created = await Send(engine, RegistryAction.Replace, "/gs/g", "{}");
        await Assert.That(created.Metadata!.RootElement.GetProperty("t").GetString()).IsEqualTo("2026-09-11T12:00:00.123456789Z");

        var omitted = await Send(engine, RegistryAction.Patch, "/gs/g", "{}");
        await Assert.That(omitted.Metadata!.RootElement.GetProperty("t").GetString()).IsEqualTo("2026-09-11T12:00:00.123456789Z");
        var reset = await Send(engine, RegistryAction.Patch, "/gs/g", """{"t":null}""");
        await Assert.That(reset.Metadata!.RootElement.GetProperty("t").GetString()).IsEqualTo("2026-09-11T12:00:00.123456789Z");
        await Send(engine, RegistryAction.Patch, "/gs/g", """{"t":"2026-09-11T07:00:00.123456789-05:00"}""");

        var stored = (await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!.RootElement;
        await Assert.That(stored.GetProperty("t").GetString()).IsEqualTo("2026-09-11T12:00:00.123456789Z");
    }
}
