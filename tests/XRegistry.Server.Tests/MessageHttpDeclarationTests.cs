using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class MessageHttpDeclarationTests
{
    [Test]
    [Arguments("""{"status":""}""")]
    [Arguments("""{"status":"99"}""")]
    [Arguments("""{"status":"600"}""")]
    [Arguments("""{"status":"2O0"}""")]
    [Arguments("""{"status":"200 "}""")]
    [Arguments("""{"status":"\u0662\u0660\u0660"}""")]
    [Arguments("""{"status":"200","method":"GET"}""")]
    [Arguments("""{"status":"{+status}"}""")]
    public async Task InvalidResponseDeclarationsCannotPublishOrReplaceValidMessageMetadata(string options)
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            """{"protocol":"HTTP","protocoloptions":""" + options + "}"), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);

        await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            """{"protocol":"HTTP","protocoloptions":{"status":"204","query":{"foo":"bar"}}}""");
        var before = (await Send(engine, RegistryAction.Read, "/messagegroups/g/messages/m")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/messagegroups/g/messages/m",
            """{"protocoloptions":""" + options + "}"), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/messagegroups/g/messages/m")).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
    }

    [Test]
    [Arguments("100")]
    [Arguments("204")]
    [Arguments("599")]
    [Arguments("{status}")]
    public async Task ValidResponseDeclarationsAndQueryMapsRoundTrip(string status)
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        var result = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            $$$$"""{"protocol":"HTTP","protocoloptions":{"status":"{{{{status}}}}","query":{"foo":"bar","empty":""}}}""");
        var options = result.Metadata!.RootElement.GetProperty("protocoloptions");

        await Assert.That(options.GetProperty("status").GetString()).IsEqualTo(status);
        await Assert.That(options.GetProperty("query").GetProperty("foo").GetString()).IsEqualTo("bar");
        await Assert.That(options.GetProperty("query").GetProperty("empty").GetString()).IsEqualTo("");
        await Assert.That(options.TryGetProperty("method", out _)).IsFalse();
    }
}
