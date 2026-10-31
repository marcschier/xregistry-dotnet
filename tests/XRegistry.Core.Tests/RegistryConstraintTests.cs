// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryConstraintTests
{
    [Test]
    [Arguments("""{"groups":{"gs":{"singular":"g","constraints":{"rs.values":{}},"resources":{"rs":{"singular":"r","attributes":{"values":{"type":"array","item":{"type":"string"}}}}}}}}""", "/groups/gs/constraints/rs.values")]
    [Arguments("""{"groups":{"gs":{"singular":"g","constraints":{"rs.values.key":{}},"resources":{"rs":{"singular":"r","attributes":{"values":{"type":"map","item":{"type":"string"}}}}}}}}""", "/groups/gs/constraints/rs.values.key")]
    [Arguments("""{"groups":{"gs":{"singular":"g","constraints":{"rs.extra":{}},"resources":{"rs":{"singular":"r","attributes":{"*":"string"}}}}}}""", "/groups/gs/constraints/rs.extra")]
    [Arguments("""{"groups":{"gs":{"singular":"g","constraints":{"rs.dynamic":{}},"resources":{"rs":{"singular":"r","attributes":{"mode":{"type":"string","ifvalues":{"on":{"siblingattributes":{"dynamic":"string"}}}}}}}}}}""", "/groups/gs/constraints/rs.dynamic")]
    [Arguments("""{"groups":{"gs":{"singular":"g","constraints":{"r.value":{}},"resources":{"rs":{"singular":"r","attributes":{"value":"string"}}}}}}""", "/groups/gs/constraints/r.value")]
    public async Task ConstraintsRejectArrayMapWildcardAndConditionalAttributes(string json, string expectedPath)
    {
        var error = TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(json)));
        await Assert.That(error.Code).IsEqualTo("model_error");
        await Assert.That(error.Path).IsEqualTo(expectedPath);
    }

    [Test]
    public async Task ConstraintPathsResolveQuotedExtendedObjectAttributes()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"gs":{"singular":"g","constraints":{"rs.payload['extra-name']":{}},
              "resources":{"rs":{"singular":"r","attributes":{"payload":{"type":"object","namecharset":"extended",
                "attributes":{"extra-name":"string"}}}}}}}}
            """));
        var constraint = model.Groups["gs"].Constraints["rs.payload['extra-name']"];
        await Assert.That(constraint.AttributePath.Count).IsEqualTo(2);
        await Assert.That(constraint.AttributePath[0]).IsEqualTo("payload");
        await Assert.That(constraint.AttributePath[1]).IsEqualTo("extra-name");
    }

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
