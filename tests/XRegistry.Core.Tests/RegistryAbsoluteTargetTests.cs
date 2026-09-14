using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryAbsoluteTargetTests
{
    [Test]
    [Arguments("uri", "https://outside.invalid/unrelated/path", true)]
    [Arguments("uri", "urn:external:thing", true)]
    [Arguments("uri", "x:/not/a/registry", false)]
    [Arguments("url", "https://outside.invalid/unrelated/path", true)]
    [Arguments("url", "https://registry.example/unknown/type", false)]
    public async Task AbsoluteUriAndUrlValuesAreExemptFromTargetObligations(string type, string value, bool withModel)
    {
        var model = Model(type);
        var result = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("{\"reference\":\"" + value + "\"}"), model.Attributes["body"].Attributes,
            new() { Model = withModel ? model : null });

        await Assert.That(result.Obligations.Count).IsEqualTo(0);
        await Assert.That(result.Metadata.RootElement.GetProperty("reference").GetString()).IsEqualTo(value);
    }

    [Test]
    [Arguments("uri")]
    [Arguments("url")]
    public async Task RelativeTargetsRetainTheirExplicitHostContextObligation(string type)
    {
        var model = Model(type);
        var result = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"reference":"/gs/g/rs/r/versions/v1"}"""), model.Attributes["body"].Attributes,
            new() { Model = model });
        await Assert.That(result.Obligations.Count).IsEqualTo(1);
        await Assert.That(result.Obligations[0].Kind).IsEqualTo("target");
        await Assert.That(result.Obligations[0].Path).IsEqualTo("/reference");
    }

    [Test]
    [Arguments("uri", "https://bad host/")]
    [Arguments("url", "https://bad host/")]
    [Arguments("xid", "https://outside.invalid/gs/g/rs/r/versions/v1")]
    public async Task TheAbsoluteExemptionDoesNotRelaxUriOrXidSyntax(string type, string value)
    {
        var model = Model(type);
        var error = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("{\"reference\":\"" + value + "\"}"), model.Attributes["body"].Attributes,
            new() { Model = model }));
        await Assert.That(error.Code).IsEqualTo("invalid_attribute");
        await Assert.That(error.Path).IsEqualTo("/reference");
    }

    private static RegistryModel Model(string type) => RegistryModel.Compile(RegistryJson.Parse("""
        {"attributes":{"body":{"type":"object","attributes":{"reference":{"type":"$TYPE","target":"/gs/rs/versions"}}}},
         "groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false}}}}}
        """.Replace("$TYPE", type, StringComparison.Ordinal)));
}
