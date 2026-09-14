using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;

namespace XRegistry.Models.Tests;

public class EndpointAuthoredModelTests
{
    [Test]
    [Arguments("""{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"endpoints":[{"uri":"https://api.example/{tenant}/events"}]}}""")]
    [Arguments("""{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"query":{"{parameter}":"{tenant}"}}}""")]
    [Arguments("""{"usage":["consumer"],"protocol":"AMQP/1.0","protocoloptions":{"distribution-mode":"{mode}"}}""")]
    public async Task AuthoredTemplatesAreRepresentableWithoutWeakeningTheCoreModel(string metadata)
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Endpoint);
        var input = RegistryJson.Parse(metadata);
        var result = RegistryMetadataValidator.Validate(input, model.Groups["endpoints"].Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput, Model = model });

        await Assert.That(result.Metadata.RootElement.GetProperty("protocoloptions").GetRawText())
            .IsEqualTo(input.RootElement.GetProperty("protocoloptions").GetRawText());
        await Assert.That(model.Groups["endpoints"].Attributes["protocol"].IfValues["HTTP"]["protocoloptions"].Type)
            .IsEqualTo(RegistryValueType.Any);
    }
}
