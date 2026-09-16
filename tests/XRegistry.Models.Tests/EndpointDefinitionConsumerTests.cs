// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class EndpointDefinitionConsumerTests
{
    [Test]
    [Arguments("HTTP", "http://example.com")]
    [Arguments("http", "https://example.com:443/a%2Fb?query=value")]
    [Arguments("AMQP", "amqp://example.com/area/queue")]
    [Arguments("AMQP/1.0", "amqps://example.com/(site.example)/queue")]
    [Arguments("MQTT", "mqtt://example.com/orders/created")]
    [Arguments("MQTT/3.1.1", "mqtts://example.com/orders/%252B")]
    [Arguments("MQTT/5.0", "tcp://example.com")]
    [Arguments("MQTT", "ssl://example.com:8883")]
    [Arguments("MQTT", "wss://example.com:443")]
    [Arguments("NATS", "nats://example.com:4222")]
    [Arguments("NATS", "tls://[::1]:4222")]
    [Arguments("NATS", "ws://example.com:80")]
    [Arguments("NATS", "nats://example.com:1")]
    [Arguments("NATS", "nats://example.com:65535")]
    public async Task PermittedEndpointAddressFormsRemainInAuthoredPreferenceOrder(string protocol, string address)
    {
        var input = Input(protocol, $$$"""{"endpoints":[{"uri":"{{{address}}}"},{"uri":"{{{address}}}","priority":2}]}""");
        var result = EndpointDefinition.Materialize(input);
        await Assert.That(result.GetProtocolOption("endpoints")[0].GetProperty("uri").GetString()).IsEqualTo(address);
        await Assert.That(result.GetProtocolOption("endpoints")[1].GetProperty("priority").GetInt32()).IsEqualTo(2);
    }

    [Test]
    [Arguments("HTTP", "ftp://example.com")]
    [Arguments("HTTP", "/relative")]
    [Arguments("HTTP", "https://")]
    [Arguments("HTTP", "https://example.com/a b")]
    [Arguments("HTTP", "https://example.com/%ZZ")]
    [Arguments("HTTP", "https://example.com/[unescaped]")]
    [Arguments("HTTP", "https://example.com?query=[unescaped]")]
    [Arguments("HTTP", "https://example.com/%0d%0a")]
    [Arguments("HTTP", "https://example.com/#fragment")]
    [Arguments("HTTP", "https://example.com:65536")]
    [Arguments("HTTP", "file:///C:/secret")]
    [Arguments("AMQP", "https://example.com/queue")]
    [Arguments("AMQP", "amqp://example.com/%00")]
    [Arguments("MQTT", "https://example.com")]
    [Arguments("MQTT", "tcp://example.com/")]
    [Arguments("MQTT", "ssl://example.com/topic")]
    [Arguments("MQTT", "wss://example.com/mqtt")]
    [Arguments("MQTT", "mqtt://example.com/orders/%2B")]
    [Arguments("MQTT", "mqtt://example.com/orders/%23")]
    [Arguments("MQTT", "mqtt://example.com/%FF")]
    [Arguments("NATS", "wss://example.com:443")]
    [Arguments("NATS", "nats://example.com")]
    [Arguments("NATS", "ws://example.com")]
    [Arguments("NATS", "nats://example.com:")]
    [Arguments("NATS", "nats://example.com:0")]
    public async Task UnsafeOrWrongProtocolAddressesAreErrorsAfterRendering(string protocol, string address)
    {
        await Rejects(Input(protocol, $$$"""{"endpoints":[{"uri":"{{{address}}}"}]}"""),
            "/protocoloptions/endpoints/0/uri", "invalid_endpoint_address");
    }

    [Test]
    [Arguments("HTTP", """{"endpoints":[{}]}""", "/protocoloptions/endpoints/0/uri")]
    [Arguments("AMQP", """{"endpoints":[{"uri":false}]}""", "/protocoloptions/endpoints/0/uri")]
    [Arguments("MQTT", """{"endpoints":{}}""", "/protocoloptions/endpoints")]
    [Arguments("NATS", """{"endpoints":[1]}""", "/protocoloptions/endpoints/0")]
    [Arguments("KAFKA", """{"endpoints":[{}]}""", "/protocoloptions/endpoints/0/bootstrap.servers")]
    [Arguments("KAFKA", """{"endpoints":[{"bootstrap.servers":[]}]}""", "/protocoloptions/endpoints/0/bootstrap.servers")]
    [Arguments("KAFKA", """{"endpoints":[{"bootstrap.servers":["broker:9092",false]}]}""", "/protocoloptions/endpoints/0/bootstrap.servers/1")]
    public Task EachAddressEntryRequiresItsProtocolSpecificShape(string protocol, string options, string path) =>
        Rejects(Input(protocol, options), path);

    [Test]
    [Arguments("broker.example:9092")]
    [Arguments("SSL://broker.example:9093")]
    [Arguments("PLAINTEXT://broker.example:9092")]
    [Arguments("SASL_SSL://broker.example:9093")]
    [Arguments("[::1]:9092")]
    public async Task KafkaBootstrapAddressesSupportTheDraftsHostAndListenerForms(string address)
    {
        var result = EndpointDefinition.Materialize(Input("KAFKA",
            $$$"""{"endpoints":[{"bootstrap.servers":["{{{address}}}"]}]}"""));
        await Assert.That(result.GetProtocolOption("endpoints")[0].GetProperty("bootstrap.servers")[0].GetString()).IsEqualTo(address);
    }

    [Test]
    [Arguments("broker")]
    [Arguments(":9092")]
    [Arguments("broker:0")]
    [Arguments("broker:65536")]
    [Arguments("SSL://broker:9092/topic")]
    [Arguments("broker:9092,other:9092")]
    public Task MalformedKafkaBootstrapAddressesAreNotSilentlyIgnored(string address) =>
        Rejects(Input("KAFKA", $$$"""{"endpoints":[{"bootstrap.servers":["{{{address}}}"]}]}"""),
            "/protocoloptions/endpoints/0/bootstrap.servers/0", "invalid_endpoint_address");

    [Test]
    [Arguments("MQTT", """{"topic":""}""", "/protocoloptions/topic")]
    [Arguments("MQTT", """{"topic":"orders/+"}""", "/protocoloptions/topic")]
    [Arguments("MQTT/3.1.1", """{"topic":"orders/#"}""", "/protocoloptions/topic")]
    [Arguments("MQTT", """{"willtopic":"will/+"}""", "/protocoloptions/willtopic")]
    [Arguments("MQTT", """{"topic":"orders","topicfilter":"orders/#"}""", "/protocoloptions/topicfilter")]
    [Arguments("MQTT", """{"topicfilter":"orders/#/tail"}""", "/protocoloptions/topicfilter")]
    [Arguments("MQTT", """{"topicfilter":"orders/part+"}""", "/protocoloptions/topicfilter")]
    [Arguments("MQTT", """{"sharedsubscriptiongroup":"workers"}""", "/protocoloptions/sharedsubscriptiongroup")]
    [Arguments("MQTT", """{"topicfilter":"orders/#","sharedsubscriptiongroup":"$share/workers"}""", "/protocoloptions/sharedsubscriptiongroup")]
    [Arguments("MQTT", """{"topicfilter":"orders/#","sharedsubscriptiongroup":"a/b"}""", "/protocoloptions/sharedsubscriptiongroup")]
    [Arguments("MQTT", """{"topicfilter":"orders/#","sharedsubscriptiongroup":""}""", "/protocoloptions/sharedsubscriptiongroup")]
    [Arguments("NATS", """{"subject":""}""", "/protocoloptions/subject")]
    [Arguments("NATS", """{"subject":"orders.*"}""", "/protocoloptions/subject")]
    [Arguments("NATS", """{"subject":"orders.>"}""", "/protocoloptions/subject")]
    [Arguments("NATS", """{"subject":"orders..created"}""", "/protocoloptions/subject")]
    [Arguments("NATS", """{"subject":"orders","subjectfilter":"orders.*"}""", "/protocoloptions/subjectfilter")]
    [Arguments("NATS", """{"subjectfilter":"orders.>.created"}""", "/protocoloptions/subjectfilter")]
    [Arguments("NATS", """{"subjectfilter":"orders.part*"}""", "/protocoloptions/subjectfilter")]
    [Arguments("NATS", """{"queuegroup":"workers"}""", "/protocoloptions/queuegroup")]
    public Task MqttAndNatsAddressingConstraintsAreConsumerFailures(string protocol, string options, string path) =>
        Rejects(Input(protocol, options), path);

    [Test]
    public async Task MqttTopicUtf8LengthHasTheProtocolsExact65535ByteBoundary()
    {
        var allowed = new string('a', 65535);
        var result = EndpointDefinition.Materialize(Input("MQTT", $$$"""{"topic":"{{{allowed}}}"}"""));
        await Assert.That(result.GetProtocolOption("topic").GetString()).IsEqualTo(allowed);
        await Rejects(Input("MQTT", $$$"""{"topic":"{{{allowed}}}a"}"""), "/protocoloptions/topic");
    }

    [Test]
    public async Task AmqpNodeOverrideIsOpaqueMetadataAndNeverBypassesUriChecks()
    {
        var input = Input("AMQP", """{"node":"work {tenant}","endpoints":[{"uri":"amqp://broker.example/old/path"}]}""");
        var result = EndpointDefinition.Materialize(input, RegistryJson.Parse("""{"tenant":"west / east"}"""));
        await Assert.That(result.GetProtocolOption("node").GetString()).IsEqualTo("work west%20%2F%20east");
        await Assert.That(result.DeferredChecks.Any(check =>
            check.Code == "amqp_node_resolution" && check.Path == "/protocoloptions/node")).IsTrue();
        await Rejects(Input("AMQP", """{"node":"valid","endpoints":[{"uri":"amqp://broker.example/bad path"}]}"""),
            "/protocoloptions/endpoints/0/uri", "invalid_endpoint_address");
    }

    [Test]
    public async Task MqttSharedFilterUsesResolvedOptionLiteralsWithoutASecondExpansion()
    {
        var input = Input("MQTT", """{"topicfilter":"orders/{tenant}/+/#","sharedsubscriptiongroup":"{workers}"}""",
            """["subscriber","consumer"]""");
        var result = EndpointDefinition.Materialize(input, RegistryJson.Parse("""{"tenant":"a/b","workers":"west"}"""));
        await Assert.That(result.MqttSubscriptionFilter).IsEqualTo("$share/west/orders/a%2Fb/+/#");
        await Assert.That(result.GetProtocolOption("topicfilter").GetString()).IsEqualTo("orders/a%2Fb/+/#");
        var plain = EndpointDefinition.Materialize(Input("MQTT/3.1.1", """{"topicfilter":"orders/+/created"}""", """["subscriber"]"""));
        await Assert.That(plain.MqttSubscriptionFilter).IsEqualTo("orders/+/created");
        var nats = EndpointDefinition.Materialize(Input("NATS", """{"subjectfilter":"orders.*.created","queuegroup":"workers"}""",
            """["subscriber","consumer"]"""));
        await Assert.That(nats.GetProtocolOption("subjectfilter").GetString()).IsEqualTo("orders.*.created");
    }

    [Test]
    public async Task InvalidTopicCanBeStoredDeclarativelyButFailsConsumerMaterialization()
    {
        var input = Input("MQTT", """{"topic":"orders/#"}""");
        await Assert.That(() => RegistryDomainRules.ValidateEndpointMetadata(input.RootElement)).ThrowsNothing();
        await Rejects(input, "/protocoloptions/topic");
        await Assert.That(input.RootElement.GetProperty("protocoloptions").GetProperty("topic").GetString()).IsEqualTo("orders/#");
    }

    [Test]
    public async Task KafkaExplicitRoleRulesApplyEvenToFalseValuesAndAbstractDefinitions()
    {
        await Rejects(Input("KAFKA", "{}", """["consumer"]"""), "/protocoloptions/consumergroup");
        await Rejects(Input("KAFKA", """{"consumergroup":""}""", """["consumer"]"""), "/protocoloptions/consumergroup");
        await Rejects(Input("KAFKA", """{"consumergroup":"existing"}""", """["subscriber"]"""), "/protocoloptions/consumergroup");
        await Rejects(Input("KAFKA", """{"enableautocommit":false}"""), "/protocoloptions/enableautocommit");
        await Rejects(Input("KAFKA", """{"autooffsetreset":"earliest"}""", """["subscriber"]"""), "/protocoloptions/autooffsetreset");
        await Rejects(RegistryJson.Parse("""{"usage":["consumer"],"protocol":"KAFKA"}"""), "/protocoloptions/consumergroup");
        var consumer = EndpointDefinition.Materialize(Input("KAFKA",
            """{"consumergroup":"existing","autooffsetreset":"none","enableautocommit":false}""", """["consumer"]"""));
        await Assert.That(consumer.GetProtocolOption("consumergroup").GetString()).IsEqualTo("existing");
        await Assert.That(consumer.GetProtocolOption("enableautocommit").GetBoolean()).IsFalse();
        var subscriber = EndpointDefinition.Materialize(Input("KAFKA", "{}", """["subscriber"]"""));
        await Assert.That(subscriber.GetProtocolOption("consumergroup").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Undefined);
    }

    [Test]
    public async Task AbstractAndExtensionProtocolsExposeDeferredChecksInsteadOfPretendingToBeConnectable()
    {
        var abstractEndpoint = EndpointDefinition.Materialize(RegistryJson.Parse("""{"usage":["producer"]}"""));
        await Assert.That(abstractEndpoint.DeferredChecks.Select(check => check.Code).ToArray())
            .IsEquivalentTo(["protocol_contract", "endpoint_address"], StringComparer.Ordinal);
        var extension = EndpointDefinition.Materialize(Input("BunnyMQ/0.9.1", """{"endpoints":[{"socket":"local"}]}"""));
        await Assert.That(extension.DeferredChecks.Any(check => check.Code == "protocol_contract")).IsTrue();
        await Assert.That(extension.GetProtocolOption("endpoints")[0].GetProperty("socket").GetString()).IsEqualTo("local");
    }

    private static RegistryJson Input(string protocol, string options, string usage = """["producer"]""") => RegistryJson.Parse(
        $$$"""{"usage":{{{usage}}},"protocol":"{{{protocol}}}","protocoloptions":{{{options}}}}""");

    private static async Task Rejects(RegistryJson input, string path, string code = "invalid_attribute")
    {
        var failure = await Assert.That(() => EndpointDefinition.Materialize(input)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a consumer diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo(code);
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(path);
    }
}
