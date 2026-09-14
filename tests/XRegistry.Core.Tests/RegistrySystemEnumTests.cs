using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistrySystemEnumTests
{
    [Test]
    [Arguments("BACKWARD", RegistryMetadataMode.CompleteEntity)]
    [Arguments("BaCkWaRd_TrAnSiTiVe", RegistryMetadataMode.CompleteEntity)]
    [Arguments("FORWARD", RegistryMetadataMode.CompleteEntity)]
    [Arguments("FoRwArD_TrAnSiTiVe", RegistryMetadataMode.CompleteEntity)]
    [Arguments("FULL", RegistryMetadataMode.CompleteEntity)]
    [Arguments("FuLl_TrAnSiTiVe", RegistryMetadataMode.CompleteEntity)]
    [Arguments("BACKWARD", RegistryMetadataMode.ClientInput)]
    [Arguments("BaCkWaRd_TrAnSiTiVe", RegistryMetadataMode.ClientInput)]
    [Arguments("FORWARD", RegistryMetadataMode.ClientInput)]
    [Arguments("FoRwArD_TrAnSiTiVe", RegistryMetadataMode.ClientInput)]
    [Arguments("FULL", RegistryMetadataMode.ClientInput)]
    [Arguments("FuLl_TrAnSiTiVe", RegistryMetadataMode.ClientInput)]
    public async Task SystemCompatibilityEnumUsesCaseInsensitiveMembershipWithoutRewritingValues(string value,
        RegistryMetadataMode mode)
    {
        var result = RegistryMetadataValidator.Validate(RegistryJson.Parse("{\"compatibility\":\"" + value + "\"}"),
            SystemCompatibility(), new() { Mode = mode });

        await Assert.That(result.Metadata.RootElement.GetProperty("compatibility").GetString()).IsEqualTo(value);
        await Assert.That(result.Obligations.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("registry")]
    [Arguments("group")]
    [Arguments("version")]
    [Arguments("nested")]
    public async Task ExtensionCompatibilityEnumsRemainCaseSensitiveAcrossMetadataPlanes(string plane)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"attributes":{
              "compatibility":{"type":"string","enum":["backward"]},
              "body":{"type":"object","attributes":{"compatibility":{"type":"string","enum":["backward"]}}}
            },"groups":{"gs":{"singular":"g",
              "attributes":{"compatibility":{"type":"string","enum":["backward"]}},
              "resources":{"rs":{"singular":"r","attributes":{"compatibility":{"type":"string","enum":["backward"]}}}}
            }}}
            """));
        var definitions = plane switch
        {
            "registry" => model.Attributes,
            "group" => model.Groups["gs"].Attributes,
            "version" => model.Groups["gs"].Resources["rs"].Attributes,
            "nested" => model.Attributes["body"].Attributes,
            _ => throw new ArgumentOutOfRangeException(nameof(plane))
        };
        var options = new RegistryMetadataValidationOptions { Mode = RegistryMetadataMode.ClientInput };
        var accepted = RegistryMetadataValidator.Validate(RegistryJson.Parse("""{"compatibility":"backward"}"""), definitions, options);
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"compatibility":"BACKWARD"}"""), definitions, options));

        await Assert.That(definitions["compatibility"].IsSystemDefined).IsFalse();
        await Assert.That(accepted.Metadata.RootElement.GetProperty("compatibility").GetString()).IsEqualTo("backward");
        await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo("/compatibility");
    }

    [Test]
    [Arguments("\"SIDEWAYS\"")]
    [Arguments("\"\"")]
    [Arguments("1")]
    public async Task SystemCompatibilityStillRejectsUnknownModesAndWrongTypes(string value)
    {
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("{\"compatibility\":" + value + "}"), SystemCompatibility()));

        await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo("/compatibility");
    }

    [Test]
    public async Task SystemCompatibilityNullInputStillRequestsDeletion()
    {
        var result = RegistryMetadataValidator.Validate(RegistryJson.Parse("""{"compatibility":null}"""),
            SystemCompatibility(), new() { Mode = RegistryMetadataMode.ClientInput });

        await Assert.That(result.Metadata.RootElement.GetProperty("compatibility").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    private static Dictionary<string, RegistryAttributeDefinition> SystemCompatibility()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r"}}}}}
            """));
        return new Dictionary<string, RegistryAttributeDefinition>(StringComparer.Ordinal)
        {
            ["compatibility"] = model.Groups["gs"].Resources["rs"].MetaAttributes["compatibility"]
        };
    }
}
