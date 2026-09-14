using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryConstraintTests
{
    [Test]
    public async Task EnforcesConstraintSubsetsAndDefaults()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"schemagroups":{"singular":"schemagroup",
              "attributes":{"format":"string"},
              "constraints":{"schemas.format":{"default":"jsonschema/draft-07","enum":["jsonschema/draft-07"],"equals":"format"}},
              "resources":{"schemas":{"singular":"schema","attributes":{
                "format":{"type":"string","enum":["avro/1.9","jsonschema/draft-07"],"required":true,"default":"avro/1.9"}
              }}}
            }}}
            """));
        var constraint = model.Groups["schemagroups"].Constraints["schemas.format"];
        await Assert.That(constraint.DefaultValue.GetString()).IsEqualTo("jsonschema/draft-07");
        await Assert.That(constraint.EnumValues.Count).IsEqualTo(1);
        await Assert.That(constraint.EnumValues[0].GetString()).IsEqualTo("jsonschema/draft-07");
        await Assert.That(constraint.EqualsAttribute).IsEqualTo("format");
        await Assert.That(constraint.AttributePath[0]).IsEqualTo("format");
        await Assert.That(model.Groups["schemagroups"].Resources["schemas"].Attributes["format"].DefaultValue.GetString())
            .IsEqualTo("avro/1.9");
    }

    [Test]
    [Arguments("""{"enum":["outside"]}""")]
    [Arguments("""{"default":"outside"}""")]
    [Arguments("""{"enum":["b"]}""")]
    [Arguments("""{"equals":"number"}""")]
    [Arguments("""{"equals":"missing"}""")]
    [Arguments("""{"enum":"a"}""")]
    [Arguments("""{"unknown":true}""")]
    public async Task ConstraintsCannotWidenTypesOrLoseEffectiveDefaults(string constraint)
    {
        var source = RegistryJson.Parse("""
            {"groups":{"gs":{"singular":"g","attributes":{"number":"integer"},
              "constraints":{"rs.value":
            """ + constraint + """
            },"resources":{"rs":{"singular":"r","attributes":{"value":{
              "type":"string","enum":["a","b"],"required":true,"default":"a"}}}}}}}
            """);
        await Assert.That(TestErrors.Capture(() => RegistryModel.Compile(source)).Code).IsEqualTo("model_error");
    }

    [Test]
    public async Task ConstraintDefaultDoesNotChangeSharedResourceDefinition()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"gs":{"singular":"g","constraints":{"rs.value":{"default":"a","equals":""}},
              "resources":{"rs":{"singular":"r","attributes":{"value":"string"}}}}}}
            """));
        var group = model.Groups["gs"];
        await Assert.That(group.Resources["rs"].Attributes["value"].Required).IsFalse();
        await Assert.That(group.Constraints["rs.value"].DefaultValue.GetString()).IsEqualTo("a");
        await Assert.That(group.Constraints["rs.value"].EqualsAttribute).IsNull();
    }
}
