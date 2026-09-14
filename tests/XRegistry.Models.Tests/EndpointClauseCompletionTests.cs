using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class EndpointClauseCompletionTests
{
    [Test]
    [Arguments("type")]
    [Arguments("mechanism")]
    [Arguments("resourceuri")]
    [Arguments("authorityuri")]
    public async Task AuthoredAuthorizationStringMembersCannotBecomeOpaqueNullValues(string field)
    {
        var input = RegistryJson.Parse($$$"""
            {"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[{"{{{field}}}":null}]}}
            """);
        var failure = await Assert.That(() => EndpointDefinition.ValidateAuthored(input))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected the declared authorization string kind.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/protocoloptions/authorization/0/" + field);
    }

    [Test]
    [Arguments("""{"usage":["producer"],"envelopeoptions":[]}""", "/envelopeoptions")]
    [Arguments("""{"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"Binary"}}""", "/envelopeoptions/mode")]
    [Arguments("""{"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":1}}""", "/envelopeoptions/mode")]
    [Arguments("""{"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"structured","format":false}}""", "/envelopeoptions/format")]
    public async Task AuthoredEnvelopeOptionsEnforceCommonShapeAndModeWithoutAModel(string json, string path)
    {
        var failure = await Assert.That(() => EndpointDefinition.ValidateAuthored(RegistryJson.Parse(json)))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a common Endpoint envelope diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(path);
    }

    [Test]
    [Arguments("""{"usage":["producer"]}""")]
    [Arguments("""{"usage":["producer"],"envelope":"CloudEvents/1.0"}""")]
    [Arguments("""{"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{}}""")]
    [Arguments("""{"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"binary"}}""")]
    [Arguments("""{"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"structured","format":"application/cloudevents+json"}}""")]
    [Arguments("""{"usage":["producer"],"envelope":"vendor/1","envelopeoptions":{"custom":"{+literal}"}}""")]
    public async Task CommonEnvelopeValidationPreservesOptionalAndExtensionDeclarations(string json)
    {
        var metadata = RegistryJson.Parse(json);
        var before = metadata.RootElement.GetRawText();
        await Assert.That(() => EndpointDefinition.ValidateAuthored(metadata)).ThrowsNothing();
        await Assert.That(metadata.RootElement.GetRawText()).IsEqualTo(before);
    }
}
