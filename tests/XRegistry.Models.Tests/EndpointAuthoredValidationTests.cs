using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class EndpointAuthoredValidationTests
{
    [Test]
    [Arguments("HTTP", "[]", "/protocoloptions")]
    [Arguments("HTTP", """{"query":{"{parameter}":42}}""", "/protocoloptions/query/{parameter}")]
    [Arguments("HTTP", """{"headers":[{"name":42,"value":"{value}"}]}""", "/protocoloptions/headers/0/name")]
    [Arguments("HTTP", """{"endpoints":[{"uri":true}]}""", "/protocoloptions/endpoints/0/uri")]
    [Arguments("HTTP", """{"authorization":null}""", "/protocoloptions/authorization")]
    [Arguments("AMQP/1.0", """{"durable":"{enabled}"}""", "/protocoloptions/durable")]
    [Arguments("AMQP/1.0", """{"connection-capabilities":[true]}""", "/protocoloptions/connection-capabilities/0")]
    [Arguments("MQTT/5.0", """{"qos":"{qos}"}""", "/protocoloptions/qos")]
    [Arguments("KAFKA", """{"endpoints":[{"bootstrap.servers":["{host}:9092",false]}]}""",
        "/protocoloptions/endpoints/0/bootstrap.servers/1")]
    [Arguments("NATS", """{"subject":{}}""", "/protocoloptions/subject")]
    public async Task AuthoredAdmissionRejectsKnownNativeKindMismatches(string protocol, string options, string path)
    {
        var input = Input(protocol, options);
        var failure = await Assert.That(() => EndpointDefinition.ValidateAuthored(input))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an authored shape diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(path);
    }

    [Test]
    [Arguments("HTTP", """{"endpoints":[{"uri":"https://192.0.2.1:1/{tenant.name}"}],"query":{"{parameter}":"{tenant.name}"},"apikeyin":"{carrier}"}""")]
    [Arguments("AMQP/1.0", """{"distribution-mode":"{mode}","node":"orders/{tenant}","durable":false,"timeout":1e3}""")]
    [Arguments("MQTT/5.0", """{"topic":"invalid/#","topicfilter":"invalid/+","qos":3,"sessionexpiryinterval":4294967296,"deployed":false}""")]
    [Arguments("HTTP", """{"endpoints":[{"uri":"not an HTTP URI/{tenant}"}],"method":"bad method","apikeyin":"not-an-enum"}""")]
    [Arguments("KAFKA", """{"autooffsetreset":"invalid","enableautocommit":false,"acks":0.5}""")]
    [Arguments("NATS", """{"subject":"invalid.*","subjectfilter":"invalid.>","queuegroup":"workers"}""")]
    public async Task AuthoredAdmissionPreservesUnresolvedValuesAndConsumerOnlyViolations(string protocol, string options)
    {
        var input = Input(protocol, options);
        var before = input.RootElement.GetRawText();
        await Assert.That(() => EndpointDefinition.ValidateAuthored(input)).ThrowsNothing();
        await Assert.That(input.RootElement.GetRawText()).IsEqualTo(before);
        await Assert.That(input.RootElement.GetProperty("protocoloptions").GetRawText()).IsEqualTo(options);
    }

    [Test]
    [Arguments("""{"query":{"{+parameter}":"value"}}""", "/protocoloptions/query/{+parameter}")]
    [Arguments("""{"query":{"parameter":"{a,b}"}}""", "/protocoloptions/query/parameter")]
    [Arguments("""{"query":{"parameter":"{a:2}"}}""", "/protocoloptions/query/parameter")]
    [Arguments("""{"endpoints":[{"uri":"https://example.com/{a*}"}]}""", "/protocoloptions/endpoints/0/uri")]
    [Arguments("""{"endpoints":[{"u{ri}":"https://example.com"}]}""", "/protocoloptions/endpoints/0/u{ri}")]
    [Arguments("""{"headers":[{"{field}":"value"}]}""", "/protocoloptions/headers/0/{field}")]
    [Arguments("""{"authorization":[{"{field}":"SASL"}]}""", "/protocoloptions/authorization/0/{field}")]
    public async Task AuthoredAdmissionUsesLevelOneGrammarAndLiteralRecordMembers(string options, string path)
    {
        var input = Input("HTTP", options);
        var failure = await Assert.That(() => EndpointDefinition.ValidateAuthored(input))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an authored template diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_endpoint_template");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(path);
    }

    [Test]
    public async Task AuthoredValidationIsBoundedCancellableAndDoesNotNeedBindings()
    {
        var input = Input("HTTP", """{"query":{"{x}":"{x}"}}""");
        await Assert.That(() => EndpointDefinition.ValidateAuthored(input,
            new EndpointTemplateOptions { MaxVariables = 1, MaxExpansions = 2, MaxExpansionBytes = 0 })).ThrowsNothing();
        var occurrence = await Assert.That(() => EndpointDefinition.ValidateAuthored(input,
            new EndpointTemplateOptions { MaxExpansions = 1 })).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected the authored occurrence budget.");
        await Assert.That(occurrence.Diagnostic.Code).IsEqualTo("template_expansion_limit");
        var variables = await Assert.That(() => EndpointDefinition.ValidateAuthored(
            Input("HTTP", """{"query":{"{x}":"{y}"}}"""),
            new EndpointTemplateOptions { MaxVariables = 1 })).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected the distinct-variable budget.");
        await Assert.That(variables.Diagnostic.Code).IsEqualTo("template_variable_limit");
        var bytes = System.Text.Encoding.UTF8.GetByteCount(input.RootElement.GetRawText());
        await Assert.That(() => EndpointDefinition.ValidateAuthored(input,
            new EndpointTemplateOptions { JsonLimits = new RegistryJsonLimits { MaxBytes = bytes } })).ThrowsNothing();
        var size = await Assert.That(() => EndpointDefinition.ValidateAuthored(input,
            new EndpointTemplateOptions { JsonLimits = new RegistryJsonLimits { MaxBytes = bytes - 1 } })).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected the authored input byte budget.");
        await Assert.That(size.Diagnostic.Code).IsEqualTo("byte_limit");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => EndpointDefinition.ValidateAuthored(RegistryJson.Parse("[]"),
            cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task AuthoredDynamicMapKeysAreNotGuessedAndPotentialCollisionsRemainUnresolved()
    {
        var input = Input("HTTP", """{"{option}":"{value}","query":{"{key}":"one","literal":"two"}}""");
        await Assert.That(() => EndpointDefinition.ValidateAuthored(input)).ThrowsNothing();
        await Assert.That(input.RootElement.GetProperty("protocoloptions").GetProperty("{option}").GetString()).IsEqualTo("{value}");
        await Assert.That(input.RootElement.GetProperty("protocoloptions").GetProperty("query").GetProperty("{key}").GetString()).IsEqualTo("one");
        var collision = await Assert.That(() => EndpointDefinition.Materialize(input,
            RegistryJson.Parse("""{"option":"method","value":"POST","key":"literal"}"""))).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected the resolved collision.");
        await Assert.That(collision.Diagnostic.Code).IsEqualTo("template_key_collision");
    }

    [Test]
    public async Task AuthoredValidationLeavesAbstractOptionsAndOtherMetadataUntouched()
    {
        var input = RegistryJson.Parse("""
            {"usage":["consumer"],"protocol":"KAFKA","description":"Keep {+description}",
             "messages":{"m":{"description":"Keep {+message}"}}}
            """);
        await Assert.That(() => EndpointDefinition.ValidateAuthored(input)).ThrowsNothing();
        await Assert.That(input.RootElement.TryGetProperty("protocoloptions", out _)).IsFalse();
        await Assert.That(input.RootElement.GetProperty("messages").GetProperty("m").GetProperty("description").GetString())
            .IsEqualTo("Keep {+message}");
    }

    [Test]
    public async Task ServerDomainAdapterKeepsStandardAttributeDiagnosticsAndOriginalCause()
    {
        var failure = await Assert.That(() => RegistryDomainRules.ValidateEndpointMetadata(
            Input("HTTP", """{"query":{"{+key}":"value"}}""").RootElement)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a standard domain diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/protocoloptions/query/{+key}");
        await Assert.That(failure.InnerException is RegistryException { Diagnostic.Code: "invalid_endpoint_template" }).IsTrue();
    }

    private static RegistryJson Input(string protocol, string options) =>
        RegistryJson.Parse($$$"""{"usage":["producer"],"protocol":"{{{protocol}}}","protocoloptions":{{{options}}}}""");
}
