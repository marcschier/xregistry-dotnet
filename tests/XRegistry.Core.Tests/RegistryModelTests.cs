// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Numerics;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryModelTests
{
    [Test]
    public async Task CompilesIndependentMinimalSpecModel()
    {
        // core/sample-model.json, frozen 1.0-rc4.
        var source = RegistryJson.Parse("""{"groups":{"dirs":{"singular":"dir","resources":{"files":{"singular":"file"}}}}}""");
        var model = RegistryModel.Compile(source);
        var group = model.Groups["dirs"];
        var resource = group.Resources["files"];

        await Assert.That(group.Plural).IsEqualTo("dirs");
        await Assert.That(group.Singular).IsEqualTo("dir");
        await Assert.That(resource.Plural).IsEqualTo("files");
        await Assert.That(resource.Singular).IsEqualTo("file");
        await Assert.That(resource.MaxVersions).IsEqualTo(BigInteger.Zero);
        await Assert.That(resource.HasDocument).IsTrue();
        await Assert.That(resource.SetVersionId).IsTrue();
        await Assert.That(resource.VersionMode).IsEqualTo("manual");
        await Assert.That(resource.SingleVersionRoot).IsFalse();
        await Assert.That(model.Attributes["specversion"].DefaultValue.GetString()).IsEqualTo("1.0-rc4");
        await Assert.That(model.Source.RootElement.GetProperty("groups").GetProperty("dirs")
            .TryGetProperty("plural", out _)).IsFalse();
        await Assert.That(model.EffectiveModel.RootElement.GetProperty("groups").GetProperty("dirs")
            .GetProperty("plural").GetString()).IsEqualTo("dirs");
        await Assert.That(resource.Attributes["versionid"].Required).IsTrue();
        await Assert.That(resource.MetaAttributes["defaultversionsticky"].DefaultValue.GetBoolean()).IsFalse();
    }

    [Test]
    public async Task PreservesSourceAndOwnsEffectiveDefinitions()
    {
        RegistryModel model;
        using (var document = JsonDocument.Parse(
            """{"attributes":{"large":{"type":"integer","required":true,"default":9007199254740993},"unused":{"type":"string","default":null}}}"""))
        {
            model = RegistryModel.Compile(RegistryJson.FromElement(document.RootElement));
        }

        await Assert.That(model.Attributes["large"].DefaultValue.GetRawText()).IsEqualTo("9007199254740993");
        await Assert.That(model.Attributes["unused"].DefaultValue.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(model.Source.RootElement.GetProperty("attributes").GetProperty("unused")
            .GetProperty("default").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(model.EffectiveModel.RootElement.GetProperty("attributes").GetProperty("large")
            .GetProperty("name").GetString()).IsEqualTo("large");
        await Assert.That(() => ((IDictionary<string, RegistryAttributeDefinition>)model.Attributes)
            .Add("bad", model.Attributes["large"])).Throws<NotSupportedException>();
    }

    [Test]
    public async Task UsesNormativeNameLengthsRatherThanDerivedSchemaLengths()
    {
        var group = new string('g', 57);
        var singular = new string('s', 63);
        var model = RegistryModel.Compile(RegistryJson.Parse(
            $$$$"""{"groups":{"{{{{group}}}}":{"singular":"{{{{singular}}}}"}}}"""));
        await Assert.That(model.Groups[group].Singular).IsEqualTo(singular);
        await Assert.That(TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(
            $$$$"""{"groups":{"{{{{new string('g', 58)}}}}":{"singular":"g"}}}"""))).Path)
            .IsEqualTo("/groups/" + new string('g', 58) + "/plural");
        await Assert.That(TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(
            $$$$"""{"groups":{"g":{"singular":"{{{{new string('s', 64)}}}}"}}}"""))).Path)
            .IsEqualTo("/groups/g/singular");
        var resource = CompileResource($$""""{"singular":"{{new string('s', 57)}}"}"""");
        await Assert.That(resource.Singular.Length).IsEqualTo(57);
        await Assert.That(TestErrors.Capture(() => CompileResource(
            $$""""{"singular":"{{new string('s', 58)}}"}"""")).Path)
            .IsEqualTo("/groups/dirs/resources/files/singular");
        await Assert.That(RegistryModel.Compile(RegistryJson.Parse("""{"attributes":{"_extension":"string"}}"""))
            .Attributes["_extension"].Type).IsEqualTo(RegistryValueType.String);
    }

    [Test]
    [Arguments("""{"attributes":{"a":{"type":"array"}}}""", "/attributes/a/item", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"map","item":{"type":"string","required":true}}}}""", "/attributes/a/item/required", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","item":{"type":"string"}}}}""", "/attributes/a/item", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","attributes":{}}}}""", "/attributes/a/attributes", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"object","namecharset":"unknown"}}}""", "/attributes/a/namecharset", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","namecharset":"strict"}}}""", "/attributes/a/namecharset", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","name":"b"}}}""", "/attributes/a/name", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"STRING"}}}""", "/attributes/a/type", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"binary"}}}""", "/attributes/a/type", "unsupported_model_type")]
    [Arguments("""{"attributes":{"a":{"type":"string","required":"true"}}}""", "/attributes/a/required", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","default":"x"}}}""", "/attributes/a/required", "model_required_true")]
    [Arguments("""{"attributes":{"a":{"type":"object","required":true,"default":{}}}}""", "/attributes/a/default", "model_scalar_default")]
    [Arguments("""{"attributes":{"a":{"type":"integer","required":true,"default":1.5}}}""", "/attributes/a/default", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","required":true,"default":"c","enum":["a","b"]}}}""", "/attributes/a/default", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"integer","enum":[1,"2"]}}}""", "/attributes/a/enum/1", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"array","item":{"type":"integer"},"enum":[]}}}""", "/attributes/a/enum", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","immutable":true}}}""", "/attributes/a/immutable", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","matchversions":true}}}""", "/attributes/a/matchversions", "model_error")]
    [Arguments("""{"attributes":{"*":{"type":"any","required":true}}}""", "/attributes/*", "model_error")]
    [Arguments("""{"attributes":{"*":{"type":"any","readonly":true}}}""", "/attributes/*", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","target":"/dirs/files"}}}""", "/attributes/a/target", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"urlabsolute","target":"/dirs/files"}}}""", "/attributes/a/target", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"uri","target":"/dirs/files/meta"}}}""", "/attributes/a/target", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","ifvalues":{"^future":{"siblingattributes":{}}}}}}""", "/attributes/a/ifvalues/^future", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","ifvalues":{"":{"siblingattributes":{}}}}}}""", "/attributes/a/ifvalues/", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","ifvalues":{"A":{"siblingattributes":{}},"a":{"siblingattributes":{}}}}}}""", "/attributes/a/ifvalues/a", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","enum":["x"],"ifvalues":{"y":{"siblingattributes":{}}}}}}""", "/attributes/a/ifvalues/y", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","ifvalues":{"x":{"siblingattributes":{"a":"string"}}}}}}""", "/attributes/a/ifvalues/x/siblingattributes/a", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"map","item":{"type":"string"},"ifvalues":{}}}}""", "/attributes/a/ifvalues", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","future":true}}}""", "/attributes/a/future", "model_error")]
    [Arguments("""{"future":true}""", "/future", "model_error")]
    public async Task RejectsInvalidModelAspectsWithExactPointers(string json, string expectedPath, string code)
    {
        var diagnostic = TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(json)));
        await Assert.That(diagnostic.Code).IsEqualTo(code);
        await Assert.That(diagnostic.Path).IsEqualTo(expectedPath);
    }

    [Test]
    [Arguments("""{"attributes":{"epoch":{"type":"string"}}}""")]
    [Arguments("""{"attributes":{"self":{"readonly":false}}}""")]
    [Arguments("""{"attributes":{"specversion":{"required":false}}}""")]
    [Arguments("""{"attributes":{"specversion":{"default":null}}}""")]
    [Arguments("""{"groups":{"model":{"singular":"m"}}}""")]
    [Arguments("""{"groups":{"as":{"singular":"a"},"a":{"singular":"b"}}}""")]
    [Arguments("""{"groups":{"as":{"plural":"bs","singular":"a"}}}""")]
    [Arguments("""{"groups":{"dirs":{"singular":"dir","resources":{"files":{"singular":"file","resourceattributes":{"custom":"string"}}}}}}""")]
    public async Task EnforcesNamingAndSystemAttributeOverlayRules(string json)
    {
        await Assert.That(TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(json))).Code).IsEqualTo("model_error");
    }

    [Test]
    public async Task CompilesNestedItemsAndStaticVersionMatchObligations()
    {
        var resource = CompileResource("""
            {"singular":"file","attributes":{
              "properties":{"type":"object","namecharset":"EXTENDED","attributes":{
                "a-b":{"type":"integer","matchversions":true},
                "bag":{"type":"array","item":{"type":"map","item":{"type":"object","attributes":{"x":"string"}}}}
              }}
            }}
            """);
        await Assert.That(resource.Attributes["properties"].NameCharset).IsEqualTo("extended");
        await Assert.That(resource.Attributes["properties"].Attributes["a-b"].MatchVersions).IsTrue();
        await Assert.That(resource.Attributes["properties"].Attributes["bag"].Item!.Item!.Attributes["x"].Type)
            .IsEqualTo(RegistryValueType.String);
        await Assert.That(TestErrors.Capture(() => CompileResource("""
            {"singular":"file","attributes":{"bag":{"type":"array","item":{"type":"object",
              "attributes":{"a":{"type":"integer","matchversions":true}}}}}}
            """)).Path).IsEqualTo("/groups/dirs/resources/files/attributes/bag/item/attributes/a/matchversions");
    }

    [Test]
    [Arguments("manual", false)]
    [Arguments("createdat", true)]
    [Arguments("MODIFIEDAT", true)]
    [Arguments("SemVer", true)]
    public async Task EnforcesVersionAndDocumentControls(string mode, bool singleRoot)
    {
        var resource = CompileResource($$"""
            {"singular":"file","maxversions":9007199254740993,"versionmode":"{{mode}}",
             "hasdocument":false,"setversionid":false,"validateformat":true,
             "validatecompatibility":true,"strictvalidation":true}
            """);
        await Assert.That(resource.MaxVersions.ToString(System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo("9007199254740993");
        await Assert.That(resource.SingleVersionRoot).IsEqualTo(singleRoot);
        await Assert.That(resource.HasDocument).IsFalse();
        await Assert.That(resource.SetVersionId).IsFalse();
        await Assert.That(resource.ValidateFormat).IsTrue();
        await Assert.That(resource.ValidateCompatibility).IsTrue();
        await Assert.That(resource.StrictValidation).IsTrue();
        await Assert.That(resource.Attributes.ContainsKey("file")).IsFalse();
        await Assert.That(resource.Attributes.ContainsKey("fileurl")).IsFalse();
        await Assert.That(resource.Attributes.ContainsKey("filebase64")).IsFalse();
    }

    [Test]
    [Arguments("""{"singular":"file","versionmode":"random"}""", "versionmode")]
    [Arguments("""{"singular":"file","versionmode":"createdat","singleversionroot":false}""", "singleversionroot")]
    [Arguments("""{"singular":"file","validatecompatibility":true}""", "validatecompatibility")]
    [Arguments("""{"singular":"file","maxversions":-1}""", "maxversions")]
    [Arguments("""{"singular":"file","maxversions":0.5}""", "maxversions")]
    [Arguments("""{"singular":"file","maxversions":"1"}""", "maxversions")]
    [Arguments("""{"singular":"file","hasdocument":1}""", "hasdocument")]
    [Arguments("""{"singular":"file","typemap":{"a**b":"json"}}""", "typemap/a**b")]
    [Arguments("""{"singular":"file","typemap":{"a":"yaml"}}""", "typemap/a")]
    public async Task RejectsInconsistentResourceControls(string resource, string suffix)
    {
        var diagnostic = TestErrors.Capture(() => CompileResource(resource));
        await Assert.That(diagnostic.Code).IsEqualTo("model_error");
        await Assert.That(diagnostic.Path).IsEqualTo("/groups/dirs/resources/files/" + suffix);
    }

    [Test]
    public async Task TypeMapUsesImplicitDefaultsAndConflictingWildcardRules()
    {
        var resource = CompileResource("""{"singular":"file","typemap":{"text/*":"STRING","text/mine":"json"}}""");
        await Assert.That(resource.ResolveDocumentFormat("APPLICATION/JSON; charset=utf-8")).IsEqualTo(RegistryDocumentFormat.Json);
        await Assert.That(resource.ResolveDocumentFormat("application/problem+json")).IsEqualTo(RegistryDocumentFormat.Json);
        await Assert.That(resource.ResolveDocumentFormat("TEXT/PLAIN")).IsEqualTo(RegistryDocumentFormat.String);
        await Assert.That(resource.ResolveDocumentFormat("text/mine")).IsEqualTo(RegistryDocumentFormat.Binary);
        await Assert.That(resource.ResolveDocumentFormat("application/unknown")).IsEqualTo(RegistryDocumentFormat.Binary);
        var overridden = CompileResource("""{"singular":"file","typemap":{"Application/JSON":"binary"}}""");
        await Assert.That(overridden.ResolveDocumentFormat("application/json")).IsEqualTo(RegistryDocumentFormat.Binary);
    }

    private static RegistryResourceDefinition CompileResource(string resource) =>
        RegistryModel.Compile(RegistryJson.Parse(
            """{"groups":{"dirs":{"singular":"dir","resources":{"files":""" + resource + "}}}}")).Groups["dirs"].Resources["files"];
}
