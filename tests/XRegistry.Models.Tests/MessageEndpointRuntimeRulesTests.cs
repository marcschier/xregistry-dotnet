using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class MessageEndpointRuntimeRulesTests
{
    [Test]
    [Arguments("HTTP")]
    [Arguments("AMQP/1.0")]
    [Arguments("MQTT/3.1.1")]
    [Arguments("MQTT/5.0")]
    [Arguments("KAFKA")]
    [Arguments("NATS")]
    public async Task MessageProtocolRulesRequireAnObjectAcrossBindings(string protocol)
    {
        await Rejects($$$"""{"protocol":"{{{protocol}}}"}""", "/protocoloptions");
        var valid = RegistryJson.Parse($$$"""{"protocol":"{{{protocol}}}","protocoloptions":{}}""");
        await Assert.That(() => RegistryDomainRules.ValidateMessageMetadata(valid.RootElement)).ThrowsNothing();
    }

    [Test]
    public async Task MessageEnvelopeRulesRequireMetadataWithoutRequiringAnEnvelope()
    {
        await Rejects("""{"envelope":"CloudEvents/1.0"}""", "/envelopemetadata");
        var envelope = RegistryJson.Parse("""{"envelope":"CloudEvents/1.0","envelopemetadata":{}}""");
        await Assert.That(() => RegistryDomainRules.ValidateMessageMetadata(envelope.RootElement)).ThrowsNothing();
        var payloadOnly = RegistryJson.Parse("""{"dataschemaformat":"JSONSchema/draft-07","dataschema":{"type":"object"}}""");
        await Assert.That(() => RegistryDomainRules.ValidateMessageMetadata(payloadOnly.RootElement)).ThrowsNothing();
    }

    [Test]
    [Arguments("""{"dataschema":{}}""", "/dataschemaformat")]
    [Arguments("""{"dataschemauri":"https://schemas.example/a.json"}""", "/dataschemaformat")]
    [Arguments("""{"dataschemaformat":"JSONSchema/draft-07","dataschema":{},"dataschemauri":"https://schemas.example/a.json"}""", "/dataschemauri")]
    [Arguments("""
        {"envelope":"CloudEvents/1.0","envelopemetadata":{"dataschema":{"value":"https://schemas.example/b.json"}},
         "dataschemaformat":"JSONSchema/draft-07","dataschemauri":"https://schemas.example/a.json"}
        """, "/envelopemetadata/dataschema/value")]
    public Task MessageSchemaRulesDiagnoseEachIndependentConstraint(string metadata, string path) => Rejects(metadata, path);

    [Test]
    [Arguments("{}")]
    [Arguments("""{"dataschemaformat":"JSONSchema/draft-07","dataschema":{"type":"object"}}""")]
    [Arguments("""{"dataschemaformat":"JSONSchema/draft-07","dataschemauri":"https://schemas.example/a.json"}""")]
    [Arguments("""
        {"envelope":"CloudEvents/1.0","envelopemetadata":{"dataschema":{"value":"https://schemas.example/a.json"}},
         "dataschemaformat":"JSONSchema/draft-07","dataschemauri":"https://schemas.example/a.json"}
        """)]
    public async Task MessageSchemaRulesAcceptNearestValidDeclarations(string metadata)
    {
        var value = RegistryJson.Parse(metadata);
        await Assert.That(() => RegistryDomainRules.ValidateMessageMetadata(value.RootElement)).ThrowsNothing();
    }

    [Test]
    [Arguments("""{"protocol":"","protocoloptions":{}}""", "/protocol")]
    [Arguments("""{"envelope":"","envelopemetadata":{}}""", "/envelope")]
    [Arguments("""{"dataschemaformat":""}""", "/dataschemaformat")]
    [Arguments("""{"envelope":"/1.0","envelopemetadata":{}}""", "/envelope")]
    [Arguments("""{"dataschemaformat":"JSONSchema"}""", "/dataschemaformat")]
    [Arguments("""{"envelope":"CloudEvents/1.0","envelopemetadata":{"subject":{"description":""}}}""", "/envelopemetadata/subject/description")]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"headers":[{"name":"x-name","description":"","value":"x"}]}}""", "/protocoloptions/headers/0/description")]
    [Arguments("""{"protocol":"AMQP/1.0","protocoloptions":{"application-properties":{"count":{"description":"","value":"one"}}}}""", "/protocoloptions/application-properties/count/description")]
    [Arguments("""{"protocol":3,"protocoloptions":{}}""", "/protocol")]
    [Arguments("[]", "")]
    public Task MessageStringsEnforceOnlyDeclaredDomainConstraints(string metadata, string path) => Rejects(metadata, path);

    [Test]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"method":"two words"}}""", "/protocoloptions/method")]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"headers":[{"name":"bad name","value":"x"}]}}""", "/protocoloptions/headers/0/name")]
    [Arguments("""{"protocol":"KAFKA","protocoloptions":{"key":"a","key_base64":"YQ=="}}""", "/protocoloptions/key_base64")]
    [Arguments("""{"protocol":"NATS","protocoloptions":{"subject":"{+topic}"}}""", "/protocoloptions/subject")]
    [Arguments("""{"protocol":"NATS","protocoloptions":{"subject":"{topic.part}"}}""", "/protocoloptions/subject")]
    [Arguments("""{"envelope":"CloudEvents/1.0","envelopemetadata":{"source":{"type":"uritemplate","value":"{topic*}"}}}""", "/envelopemetadata/source/value")]
    public Task MessageProtocolAndTemplateRulesReturnExactPaths(string metadata, string path) => Rejects(metadata, path);

    [Test]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"method":"MY-METHOD","headers":[{"name":"X-{name}","value":"{value}"}]}}""")]
    [Arguments("""{"protocol":"KAFKA","protocoloptions":{"key":"device-{id}"}}""")]
    [Arguments("""{"protocol":"KAFKA","protocoloptions":{"key_base64":"YQ=="}}""")]
    [Arguments("""{"protocol":"NATS","protocoloptions":{"subject":"orders.{tenant}.created"}}""")]
    [Arguments("""{"envelope":"CloudEvents/1.0","envelopemetadata":{"source":{"type":"uritemplate","value":"/systems/{id}"}}}""")]
    public async Task MessageProtocolAndTemplateRulesAcceptPermittedConstraints(string metadata)
    {
        var input = RegistryJson.Parse(metadata);
        await Assert.That(() => RegistryDomainRules.ValidateMessageMetadata(input.RootElement)).ThrowsNothing();
    }

    [Test]
    [Arguments("""{"datacontenttype":"not a media type"}""", "/datacontenttype")]
    [Arguments("""{"datacontenttype":"text/plain; missing"}""", "/datacontenttype")]
    [Arguments("""{"datacontenttype":"application/json","protocol":"HTTP","protocoloptions":{"headers":[{"name":"content-type","value":"application/xml"}]}}""", "/protocoloptions/headers/0/value")]
    [Arguments("""{"datacontenttype":"application/json","protocol":"AMQP/1.0","protocoloptions":{"properties":{"content-type":{"value":"application/xml"}}}}""", "/protocoloptions/properties/content-type/value")]
    [Arguments("""{"datacontenttype":"application/json","protocol":"MQTT/5.0","protocoloptions":{"content_type":"application/xml"}}""", "/protocoloptions/content_type")]
    [Arguments("""{"datacontenttype":"application/json","protocol":"NATS","protocoloptions":{"headers":[{"name":"Content-Type","value":"application/xml"}]}}""", "/protocoloptions/headers/0/value")]
    [Arguments("""{"datacontenttype":"application/json","protocol":"KAFKA","protocoloptions":{"headers":{"content-type":{"name":"content-type","value":"application/xml"}}}}""", "/protocoloptions/headers/content-type/value")]
    public Task MessageMediaTypeFailuresIdentifyTheirDeclaration(string metadata, string path) => Rejects(metadata, path);

    [Test]
    public async Task EndpointMetadataSeparatesEnvelopeConstraintsFromProtocolExecution()
    {
        var invalid = RegistryJson.Parse("""
            {"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"binary","format":"application/json"}}
            """);
        var failure = await Assert.That(() => RegistryDomainRules.ValidateEndpointMetadata(invalid.RootElement))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a domain diagnostic.");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/envelopeoptions/format");
        var valid = RegistryJson.Parse("""
            {"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"structured","format":"application/json"},
             "protocol":"MQTT/5.0","protocoloptions":{"topic":"a/#","topicfilter":"a/+"}}
            """);
        await Assert.That(() => RegistryDomainRules.ValidateEndpointMetadata(valid.RootElement)).ThrowsNothing();
    }

    [Test]
    [Arguments("""{"envelope":""}""", "/envelope")]
    [Arguments("""{"protocol":""}""", "/protocol")]
    public async Task MessageGroupSelectorsHaveDirectDomainDiagnostics(string metadata, string path)
    {
        var input = RegistryJson.Parse(metadata);
        var failure = await Assert.That(() => RegistryDomainRules.ValidateMessageGroupMetadata(input.RootElement))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a domain diagnostic.");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(path);
    }

    [Test]
    public async Task MessageSchemaReferenceUsesExplicitHostIdentityWithoutAcquiringTargets()
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.CloudEvents);
        var calls = 0;
        string Self(RegistryPath path, RegistryResourceDefinition schema)
        {
            calls++;
            if (path.EscapedPath != "/schemagroups/g/schemas/s" || !schema.HasDocument)
            {
                throw new InvalidOperationException("Unexpected schema projection.");
            }
            return "https://registry.example/catalog/schemagroups/g/schemas/s$details";
        }

        var wrongType = RegistryJson.Parse("""{"dataschemaxid":"/messagegroups/g/messages/m"}""");
        var kindFailure = await Assert.That(() => RegistryDomainRules.ValidateMessageSchemaReference(wrongType.RootElement, model, Self))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a schema kind diagnostic.");
        await Assert.That(kindFailure.Diagnostic.Path).IsEqualTo("/dataschemaxid");
        await Assert.That(calls).IsEqualTo(0);

        var dangling = RegistryJson.Parse("""{"dataschemaxid":"/schemagroups/g/schemas/s"}""");
        await Assert.That(() => RegistryDomainRules.ValidateMessageSchemaReference(dangling.RootElement, model, Self)).ThrowsNothing();
        await Assert.That(calls).IsEqualTo(0);

        var mismatched = RegistryJson.Parse("""{"dataschemaxid":"/schemagroups/g/schemas/s","dataschemauri":"https://other.example/s"}""");
        var identityFailure = await Assert.That(() => RegistryDomainRules.ValidateMessageSchemaReference(mismatched.RootElement, model, Self))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a schema identity diagnostic.");
        await Assert.That(identityFailure.Diagnostic.Path).IsEqualTo("/dataschemauri");
        await Assert.That(calls).IsEqualTo(1);

        var valid = RegistryJson.Parse("""{"dataschemaxid":"/schemagroups/g/schemas/s","dataschemauri":"https://registry.example/catalog/schemagroups/g/schemas/s$details"}""");
        await Assert.That(() => RegistryDomainRules.ValidateMessageSchemaReference(valid.RootElement, model, Self)).ThrowsNothing();
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    [Arguments(RegistryModelKind.Message, """{"protocol":"HTTP"}""", """{"protocol":"http"}""", true)]
    [Arguments(RegistryModelKind.Message, """{"protocol":"HTTP"}""", "{}", true)]
    [Arguments(RegistryModelKind.Message, """{"protocol":"HTTP"}""", """{"protocol":"NATS"}""", false)]
    [Arguments(RegistryModelKind.Message, """{"envelope":"myspec/1.0"}""", """{"envelope":"myspec/1.0.1"}""", false)]
    [Arguments(RegistryModelKind.Endpoint, """{"envelope":"myspec"}""", """{"envelope":"MySpec/1.0"}""", true)]
    [Arguments(RegistryModelKind.Endpoint, """{"envelope":"myspec/1.0"}""", """{"envelope":"myspec/1.0.1"}""", true)]
    [Arguments(RegistryModelKind.Endpoint, """{"envelope":"myspec/1.0"}""", """{"envelope":"myspec/1.01"}""", false)]
    [Arguments(RegistryModelKind.Endpoint, """{"envelope":"myspec/1.0"}""", """{"envelope":"myspec"}""", false)]
    [Arguments(RegistryModelKind.Endpoint, """{"protocol":"AMQP"}""", """{"protocol":"amqp/1.0"}""", true)]
    [Arguments(RegistryModelKind.Endpoint, """{"protocol":"MQTT"}""", """{"protocol":"MQTT/5.0"}""", true)]
    public async Task DomainGroupRelationsRespectSelectorAndPrecisionBoundaries(
        RegistryModelKind kind, string groupMetadata, string messageMetadata, bool valid)
    {
        var model = BuiltInRegistryModels.Compile(kind);
        var group = model.Groups[kind == RegistryModelKind.Message ? "messagegroups" : "endpoints"];
        var resource = group.Resources["messages"];
        await Assert.That(RegistryDomainRules.HasMessageGroupContract(group, resource)).IsTrue();
        var parent = RegistryJson.Parse(groupMetadata);
        var message = RegistryJson.Parse(messageMetadata);
        Action validate = () => RegistryDomainRules.ValidateMessageGroupConstraints(message.RootElement, group, resource, parent.RootElement);
        if (valid)
        {
            await Assert.That(validate).ThrowsNothing();
        }
        else
        {
            var failure = await Assert.That(validate).Throws<RegistryException>()
                ?? throw new InvalidOperationException("Expected a group contract diagnostic.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_attribute");
            await Assert.That(failure.Diagnostic.Path).IsEqualTo(parent.RootElement.TryGetProperty("protocol", out _) ? "/protocol" : "/envelope");
        }
    }

    [Test]
    public async Task OrdinaryGroupAndResourceNamesDoNotOptIntoDomainContracts()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"messagegroups":{"singular":"messagegroup","resources":{"messages":{"singular":"message","hasdocument":false}}}}}
            """));
        var group = model.Groups["messagegroups"];
        var resource = group.Resources["messages"];
        await Assert.That(RegistryDomainRules.HasMessageGroupContract(group, resource)).IsFalse();
        await Assert.That(() => RegistryDomainRules.ValidateMessageGroupConstraints(
            RegistryJson.Parse("""{"protocol":"NATS"}""").RootElement, group, resource,
            RegistryJson.Parse("""{"protocol":"HTTP"}""").RootElement)).ThrowsNothing();
    }

    [Test]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"headers":[{"name":"X-Json","value":"{\"a\":1}"}]}}""")]
    [Arguments("""
        {"datacontenttype":"application/example; first=one; second=\"two three\"",
         "protocol":"HTTP","protocoloptions":{"headers":[{"name":"Content-Type","value":"APPLICATION/EXAMPLE; SECOND=\"two three\"; FIRST=one"}]}}
        """)]
    [Arguments("""
        {"envelope":"CloudEvents/1.0","envelopemetadata":{"dataschema":{"value":"https://schemas.example/{name}"}},
         "dataschemaformat":"JSONSchema/draft-07","dataschemauri":"https://schemas.example/a.json"}
        """)]
    public async Task ContextFreeRulesPreserveLiteralTextAndUnresolvedTemplates(string metadata)
    {
        var input = RegistryJson.Parse(metadata);
        await Assert.That(() => RegistryDomainRules.ValidateMessageMetadata(input.RootElement)).ThrowsNothing();
    }

    [Test]
    public async Task DomainValidationCancellationPrecedesMetadataAndHostProjection()
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.CloudEvents);
        var group = model.Groups["messagegroups"];
        var resource = group.Resources["messages"];
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;
        var input = RegistryJson.Parse("{}");
        var calls = 0;
        string Self(RegistryPath _, RegistryResourceDefinition __)
        {
            calls++;
            return "https://registry.example/schema";
        }
        Action[] actions =
        [
            () => RegistryDomainRules.ValidateMessageMetadata(input.RootElement, token),
            () => RegistryDomainRules.ValidateMessageGroupMetadata(input.RootElement, token),
            () => RegistryDomainRules.ValidateEndpointMetadata(input.RootElement, token),
            () => RegistryDomainRules.ValidateMessageSchemaReference(input.RootElement, model, Self, token),
            () => RegistryDomainRules.ValidateMessageGroupConstraints(input.RootElement, group, resource, input.RootElement, token)
        ];
        foreach (var action in actions)
        {
            await Assert.That(action).Throws<OperationCanceledException>();
        }
        await Assert.That(calls).IsEqualTo(0);
    }

    private static async Task Rejects(string metadata, string path)
    {
        var value = RegistryJson.Parse(metadata);
        var failure = await Assert.That(() => RegistryDomainRules.ValidateMessageMetadata(value.RootElement))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a domain diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(path);
    }
}
