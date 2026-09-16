// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class EndpointDefinitionProtocolTests
{
    [Test]
    [Arguments("HTTP", """{"method":"POST","headers":[{"name":"X-A","value":"one"},{"name":"x-a","value":"two"}],"query":{"name":"value"},"apikeyname":"x-api-key","apikeyin":"header","plainscheme":"basic","plainusernamefield":"user","plainpasswordfield":"pass"}""")]
    [Arguments("AMQP/1.0", """{"node":"orders","durable":false,"link-properties":{"a":"b"},"connection-properties":{"a":"b"},"distribution-mode":"copy","connection-capabilities":["one"],"node-capabilities":["two"],"source-filters":{"selector":{"count":123456789012345678901234567890}},"dynamic":true,"terminus-durability":"unsettled-state","expiry-policy":"never","timeout":0,"sender-settle-mode":"mixed","receiver-settle-mode":"second"}""")]
    [Arguments("MQTT/3.1.1", """{"topic":"orders/created","qos":2e0,"retain":true,"cleansession":false,"willtopic":"client/status","willmessage":"/messagegroups/g/messages/will"}""")]
    [Arguments("MQTT/5.0", """{"topic":"orders/created","qos":0,"retain":false,"cleanstart":true,"sessionexpiryinterval":4294967295,"nolocal":false,"retainaspublished":true,"retainhandling":2,"willtopic":"client/status","willmessage":"/endpoints/e/messages/will"}""")]
    [Arguments("KAFKA", """{"topic":"orders","acks":-1,"key":"key","partition":123456789012345678901234567890,"headers":{"type":"order"},"keyserializer":"custom.key","valueserializer":"custom.value"}""")]
    [Arguments("NATS", """{"subject":"orders.created"}""")]
    public async Task DeclaredProtocolOptionTypesPreserveTheirExactJson(string protocol, string options)
    {
        var result = EndpointDefinition.Materialize(Input(protocol, options));
        await Assert.That(result.Resolved.RootElement.GetProperty("protocoloptions").GetRawText()).IsEqualTo(options);
    }

    [Test]
    [Arguments("HTTP", """{"headers":[{"value":"missing-name"}]}""", "/protocoloptions/headers/0/name")]
    [Arguments("HTTP", """{"headers":[{"name":"X-A"}]}""", "/protocoloptions/headers/0/value")]
    [Arguments("HTTP", """{"headers":{}}""", "/protocoloptions/headers")]
    [Arguments("HTTP", """{"headers":[1]}""", "/protocoloptions/headers/0")]
    [Arguments("HTTP", """{"headers":[{"name":"bad name","value":"one"}]}""", "/protocoloptions/headers/0/name")]
    [Arguments("HTTP", """{"headers":[{"name":"X-A","value":"one\r\ntwo"}]}""", "/protocoloptions/headers/0/value")]
    [Arguments("HTTP", """{"method":"two words"}""", "/protocoloptions/method")]
    [Arguments("HTTP", """{"query":{"a":3}}""", "/protocoloptions/query/a")]
    [Arguments("HTTP", """{"apikeyin":"cookie"}""", "/protocoloptions/apikeyin")]
    [Arguments("HTTP", """{"plainscheme":"bearer"}""", "/protocoloptions/plainscheme")]
    [Arguments("AMQP", """{"durable":"false"}""", "/protocoloptions/durable")]
    [Arguments("AMQP", """{"timeout":-1}""", "/protocoloptions/timeout")]
    [Arguments("AMQP", """{"timeout":0.1}""", "/protocoloptions/timeout")]
    [Arguments("AMQP", """{"link-properties":{"x":false}}""", "/protocoloptions/link-properties/x")]
    [Arguments("AMQP", """{"connection-capabilities":["ok",1]}""", "/protocoloptions/connection-capabilities/1")]
    [Arguments("AMQP", """{"distribution-mode":"MOVE"}""", "/protocoloptions/distribution-mode")]
    [Arguments("AMQP", """{"terminus-durability":"durable"}""", "/protocoloptions/terminus-durability")]
    [Arguments("AMQP", """{"expiry-policy":"sometimes"}""", "/protocoloptions/expiry-policy")]
    [Arguments("AMQP", """{"sender-settle-mode":"first"}""", "/protocoloptions/sender-settle-mode")]
    [Arguments("AMQP", """{"receiver-settle-mode":"mixed"}""", "/protocoloptions/receiver-settle-mode")]
    [Arguments("MQTT", """{"qos":3}""", "/protocoloptions/qos")]
    [Arguments("MQTT", """{"qos":1.0000000000000000000000000001}""", "/protocoloptions/qos")]
    [Arguments("MQTT", """{"sessionexpiryinterval":4294967296}""", "/protocoloptions/sessionexpiryinterval")]
    [Arguments("MQTT", """{"retainhandling":-1}""", "/protocoloptions/retainhandling")]
    [Arguments("MQTT/3.1.1", """{"cleansession":"true"}""", "/protocoloptions/cleansession")]
    [Arguments("KAFKA", """{"acks":2}""", "/protocoloptions/acks")]
    [Arguments("KAFKA", """{"acks":-2}""", "/protocoloptions/acks")]
    [Arguments("KAFKA", """{"partition":0.01}""", "/protocoloptions/partition")]
    [Arguments("KAFKA", """{"headers":[{"x":"y"}]}""", "/protocoloptions/headers")]
    [Arguments("NATS", """{"subject":false}""", "/protocoloptions/subject")]
    [Arguments("NATS", """{"deployed":"false"}""", "/protocoloptions/deployed")]
    [Arguments("HTTP", "null", "/protocoloptions")]
    public Task WrongTypesRequiredMembersAndEnumsFailAtTheirResolvedPaths(string protocol, string options, string path) =>
        Rejects(Input(protocol, options), path);

    [Test]
    public async Task StringEnumsAreCheckedAfterExpansionButBooleansAndNumbersAreNotCoerced()
    {
        var arguments = RegistryJson.Parse("""{"mode":"copy","qos":"1","flag":"false"}""");
        var valid = EndpointDefinition.Materialize(Input("AMQP/1.0", """{"distribution-mode":"{mode}"}"""), arguments);
        await Assert.That(valid.GetProtocolOption("distribution-mode").GetString()).IsEqualTo("copy");
        await Rejects(Input("MQTT", """{"qos":"{qos}"}"""), "/protocoloptions/qos", arguments);
        await Rejects(Input("AMQP", """{"durable":"{flag}"}"""), "/protocoloptions/durable", arguments);
        await Rejects(Input("AMQP", """{"distribution-mode":"{mode}"}"""), "/protocoloptions/distribution-mode",
            RegistryJson.Parse("""{"mode":"invalid"}"""));
    }

    [Test]
    public async Task DefaultsAreConsumerInterpretationsAndNeverRewriteTheAuthoredMetadata()
    {
        var http = EndpointDefinition.Materialize(Input("http", "{}"));
        await Assert.That(http.Protocol).IsEqualTo("HTTP");
        await Assert.That(http.IsDeployed).IsTrue();
        await Assert.That(http.GetProtocolOption("method").GetString()).IsEqualTo("POST");
        await Assert.That(http.GetProtocolOption("apikeyin").GetString()).IsEqualTo("header");
        await Assert.That(http.GetProtocolOption("plainscheme").GetString()).IsEqualTo("basic");
        await Assert.That(http.Resolved.RootElement.GetProperty("protocoloptions").GetRawText()).IsEqualTo("{}");
        await Assert.That(http.GetProtocolOption("unspecified").ValueKind).IsEqualTo(JsonValueKind.Undefined);

        var amqp = EndpointDefinition.Materialize(Input("AMQP", "{}"));
        await Assert.That(amqp.Protocol).IsEqualTo("AMQP/1.0");
        await Assert.That(amqp.GetProtocolOption("durable").GetBoolean()).IsFalse();
        await Assert.That(amqp.GetProtocolOption("distribution-mode").GetString()).IsEqualTo("move");
        var mqtt = EndpointDefinition.Materialize(Input("MQTT", """{"deployed":false}"""));
        await Assert.That(mqtt.Protocol).IsEqualTo("MQTT/5.0");
        await Assert.That(mqtt.GetProtocolOption("qos").GetInt32()).IsEqualTo(0);
        await Assert.That(mqtt.GetProtocolOption("retain").GetBoolean()).IsFalse();
        await Assert.That(mqtt.IsDeployed).IsFalse();
        await Assert.That(mqtt.GetProtocolOption("cleansession").ValueKind).IsEqualTo(JsonValueKind.Undefined);
        var legacy = EndpointDefinition.Materialize(Input("MQTT/3.1.1", "{}"));
        await Assert.That(legacy.GetProtocolOption("cleansession").GetBoolean()).IsTrue();
        var kafka = EndpointDefinition.Materialize(Input("KAFKA", "{}"));
        await Assert.That(kafka.GetProtocolOption("acks").GetInt32()).IsEqualTo(1);
        await Assert.That(kafka.GetProtocolOption("keyserializer").ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task ConsumerEnvelopeChecksDoNotTreatDescriptiveRoleCellsAsProhibitions()
    {
        var allowed = EndpointDefinition.Materialize(Input("MQTT/5.0", """{"topic":"orders","nolocal":true}"""));
        await Assert.That(allowed.GetProtocolOption("nolocal").GetBoolean()).IsTrue();
        await Rejects(RegistryJson.Parse("""
            {"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"Binary"}}
            """), "/envelopeoptions/mode");
        await Rejects(RegistryJson.Parse("""
            {"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"binary","format":"application/json"}}
            """), "/envelopeoptions/format");
        var structured = EndpointDefinition.Materialize(RegistryJson.Parse("""
            {"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"structured","format":"application/json"}}
            """));
        await Assert.That(structured.Resolved.RootElement.GetProperty("envelopeoptions").GetProperty("format").GetString())
            .IsEqualTo("application/json");
    }

    private static RegistryJson Input(string protocol, string options) => RegistryJson.Parse(
        $$$"""{"usage":["producer"],"protocol":"{{{protocol}}}","protocoloptions":{{{options}}}}""");

    private static async Task Rejects(RegistryJson input, string path, RegistryJson? arguments = null)
    {
        var failure = await Assert.That(() => EndpointDefinition.Materialize(input, arguments)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a resolved-value diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(path);
    }
}
