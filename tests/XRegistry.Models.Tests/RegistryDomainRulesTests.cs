using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class RegistryDomainRulesTests
{
    [Test]
    [Arguments("""{"usage":["producer"],"protocol":"HTTP"}""")]
    [Arguments("""{"usage":["consumer"],"protocol":"KAFKA"}""")]
    [Arguments("""{"usage":["subscriber"],"protocol":"HTTP"}""")]
    [Arguments("""{"usage":["subscriber","consumer"],"protocol":"MQTT/5.0"}""")]
    [Arguments("""{"usage":["consumer","subscriber"],"protocol":"AMQP/1.0"}""")]
    [Arguments("""{"usage":["subscriber","consumer"],"protocol":"NATS"}""")]
    public async Task AllowedEndpointRolesAreAcceptedWithoutInventingNetworkBehavior(string input)
    {
        var metadata = RegistryJson.Parse(input);
        await Assert.That(() => RegistryDomainRules.ValidateEndpointUsage(metadata.RootElement)).ThrowsNothing();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"usage":[]}""")]
    [Arguments("""{"usage":["unknown"]}""")]
    [Arguments("""{"usage":["Producer"]}""")]
    [Arguments("""{"usage":["consumer","consumer"]}""")]
    [Arguments("""{"usage":["producer","consumer"],"protocol":"MQTT/5.0"}""")]
    [Arguments("""{"usage":["subscriber","consumer"],"protocol":"HTTP"}""")]
    [Arguments("""{"usage":["subscriber","consumer"],"protocol":"KAFKA"}""")]
    [Arguments("""{"usage":["subscriber","consumer"],"protocol":"invented"}""")]
    [Arguments("""{"usage":["subscriber","consumer","producer"],"protocol":"NATS"}""")]
    public async Task InvalidRoleSetsFailTheUnchangedNormativeDomainRule(string input)
    {
        var exception = await Assert.That(() => RegistryDomainRules.ValidateEndpointUsage(
            RegistryJson.Parse(input).RootElement)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("The expected domain-rule exception was not returned.");
        await Assert.That(exception.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(exception.Diagnostic.Path).IsEqualTo("/usage");
    }
}
