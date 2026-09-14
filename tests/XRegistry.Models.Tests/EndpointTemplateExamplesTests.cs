using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class EndpointTemplateExamplesTests
{
    [Test]
    public async Task HttpTemplateExampleProducesTheDocumentedUri()
    {
        var authored = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP",
             "protocoloptions":{"endpoints":[{"uri":"https://api.example/{tenant}/events"}]}}
            """);
        var endpoint = EndpointDefinition.Materialize(authored,
            RegistryJson.Parse("""{"tenant":"north/west"}"""));
        var uri = endpoint.GetProtocolOption("endpoints")[0].GetProperty("uri").GetString();

        await Assert.That(uri).IsEqualTo("https://api.example/north%2Fwest/events");
        await Assert.That(endpoint.GetProtocolOption("method").GetString()).IsEqualTo("POST");
        await Assert.That(endpoint.Authored.RootElement.GetProperty("protocoloptions").GetProperty("endpoints")[0]
            .GetProperty("uri").GetString()).IsEqualTo("https://api.example/{tenant}/events");
    }

    [Test]
    public async Task MqttTemplateExampleProducesTheDocumentedSharedFilter()
    {
        var authored = RegistryJson.Parse("""
            {"usage":["subscriber","consumer"],"protocol":"MQTT",
             "protocoloptions":{"endpoints":[{"uri":"mqtts://broker.example"}],
              "topicfilter":"{tenant}/orders/+","sharedsubscriptiongroup":"{group}"}}
            """);
        var endpoint = EndpointDefinition.Materialize(authored,
            RegistryJson.Parse("""{"tenant":"north/west","group":"workers"}"""));

        await Assert.That(endpoint.MqttSubscriptionFilter).IsEqualTo("$share/workers/north%2Fwest/orders/+");
        await Assert.That(endpoint.Protocol).IsEqualTo("MQTT/5.0");
    }

    [Test]
    public async Task MessageSelectionExampleUsesOnlyExplicitlySuppliedGroups()
    {
        var authored = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP","messagegroups":["/messagegroups/orders"]}
            """);
        var supplied = RegistryJson.Parse("""
            {"/messagegroups/orders":{"messages":{"order.created":{"messageid":"order.created","description":"An order was created"}}}}
            """);
        var endpoint = EndpointDefinition.Materialize(authored,
            options: new EndpointTemplateOptions { MessageGroups = supplied });
        var message = endpoint.SelectMessage("order.created", "/messagegroups/orders");

        await Assert.That(message.Metadata.RootElement.GetProperty("description").GetString()).IsEqualTo("An order was created");
        await Assert.That(message.GroupReference).IsEqualTo("/messagegroups/orders");
    }
}
