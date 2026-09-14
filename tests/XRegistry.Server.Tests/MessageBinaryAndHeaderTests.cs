using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class MessageBinaryAndHeaderTests
{
    [Test]
    [Arguments("MQTT/5.0", "correlation_data", "not-base64")]
    [Arguments("MQTT/5.0", "correlation_data", "AA")]
    [Arguments("MQTT/5.0", "correlation_data", "AB==")]
    [Arguments("MQTT/5.0", "correlation_data", "{value}")]
    [Arguments("MQTT/5.0", "correlation_data", "AA==\n")]
    [Arguments("KAFKA", "key_base64", "not-base64")]
    [Arguments("KAFKA", "key_base64", "AA")]
    [Arguments("KAFKA", "key_base64", "AB==")]
    [Arguments("KAFKA", "key_base64", "{value}")]
    [Arguments("KAFKA", "key_base64", "AA==\n")]
    public async Task BinaryDeclarationsRejectNoncanonicalBase64BeforePublishing(string protocol, string field, string value)
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            Binary(protocol, field, value)), "invalid_attribute");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement
            .GetProperty("messagegroupscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    [Arguments("MQTT/5.0", "correlation_data", "")]
    [Arguments("MQTT/5.0", "correlation_data", "AA==")]
    [Arguments("MQTT/5.0", "correlation_data", "AAH/")]
    [Arguments("KAFKA", "key_base64", "")]
    [Arguments("KAFKA", "key_base64", "AA==")]
    [Arguments("KAFKA", "key_base64", "AAH/")]
    public async Task BinaryDeclarationsPreserveTheirExactCanonicalBytesIncludingEmpty(string protocol, string field, string value)
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        var result = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", Binary(protocol, field, value));
        await Assert.That(result.Metadata!.RootElement.GetProperty("protocoloptions").GetProperty(field).GetString()).IsEqualTo(value);
        var stored = await Send(engine, RegistryAction.Read, "/messagegroups/g/messages/m");
        await Assert.That(stored.Metadata!.RootElement.GetProperty("protocoloptions").GetProperty(field).GetString()).IsEqualTo(value);
    }

    [Test]
    [Arguments("HTTP", "headers", false)]
    [Arguments("NATS", "headers", false)]
    [Arguments("MQTT/5.0", "user_properties", false)]
    [Arguments("KAFKA", "headers", true)]
    public async Task HeaderDeclarationsRetainTheCommonSpecificationReference(string protocol, string field, bool map)
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        var declaration = new JsonObject { ["name"] = "x-note", ["value"] = "value", ["specurl"] = "../header-definition" };
        JsonNode collection = map ? new JsonObject { ["note"] = declaration } : new JsonArray((JsonNode)declaration);
        var input = new JsonObject { ["protocol"] = protocol, ["protocoloptions"] = new JsonObject { [field] = collection } };
        await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", input.ToJsonString());
        var stored = (await Send(engine, RegistryAction.Read, "/messagegroups/g/messages/m")).Metadata!.RootElement
            .GetProperty("protocoloptions").GetProperty(field);
        await Assert.That((map ? stored.GetProperty("note") : stored[0]).GetProperty("specurl").GetString())
            .IsEqualTo("../header-definition");
    }

    [Test]
    public async Task MqttContentTypeIsAMediaTypeStringRatherThanASymbolOrUri()
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        var result = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", """
            {"protocol":"MQTT/5.0","protocoloptions":{"content_type":"application/json; charset=utf-8"}}
            """);
        await Assert.That(result.Metadata!.RootElement.GetProperty("protocoloptions").GetProperty("content_type").GetString())
            .IsEqualTo("application/json; charset=utf-8");
    }

    private static string Binary(string protocol, string field, string value) =>
        new JsonObject { ["protocol"] = protocol, ["protocoloptions"] = new JsonObject { [field] = value } }.ToJsonString();
}
