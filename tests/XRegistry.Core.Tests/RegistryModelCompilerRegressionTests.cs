// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryModelCompilerRegressionTests
{
    private const string ResourceModel = """
        {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false}}}}}
        """;

    [Test]
    [Arguments("meta", false)]
    [Arguments("metaurl", false)]
    [Arguments("versions", false)]
    [Arguments("versionsurl", false)]
    [Arguments("versionscount", false)]
    [Arguments("meta", true)]
    [Arguments("metaurl", true)]
    [Arguments("versions", true)]
    [Arguments("versionsurl", true)]
    [Arguments("versionscount", true)]
    public async Task ConditionalVersionSiblingsCannotShadowResourceFields(string name, bool nested)
    {
        var siblings = new JsonObject { [name] = new JsonObject { ["type"] = "string" } };
        if (nested)
        {
            siblings = new JsonObject { ["detail"] = Conditional(siblings) };
        }

        var source = JsonNode.Parse(ResourceModel)!.AsObject();
        source["groups"]!["gs"]!["resources"]!["rs"]!["attributes"] =
            new JsonObject { ["mode"] = Conditional(siblings) };
        var diagnostic = TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString())));
        var prefix = "/groups/gs/resources/rs/attributes/mode/ifvalues/on/siblingattributes";

        await Assert.That(diagnostic.Code).IsEqualTo("model_error");
        await Assert.That(diagnostic.Path).IsEqualTo(prefix +
            (nested ? "/detail/ifvalues/on/siblingattributes" : "") + "/" + name);
    }

    [Test]
    public async Task ConditionalObjectChildrenKeepTheirOwnNameScope()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","attributes":{
              "mode":{"type":"string","ifvalues":{"on":{"siblingattributes":{
                "payload":{"type":"object","attributes":{"metaurl":"string","versions":"integer"}}
              }}}}
            }}}}}}
            """));
        var payload = model.Groups["gs"].Resources["rs"].Attributes["mode"].IfValues["on"]["payload"];

        await Assert.That(payload.Attributes["metaurl"].Type).IsEqualTo(RegistryValueType.String);
        await Assert.That(payload.Attributes["versions"].Type).IsEqualTo(RegistryValueType.Integer);
        await Assert.That(model.Groups["gs"].Resources["rs"].ResourceAttributes["metaurl"].ReadOnly).IsTrue();
    }

    [Test]
    [Arguments("array")]
    [Arguments("map")]
    public async Task ConditionalCollectionItemsKeepTheirOwnNameScope(string type)
    {
        var source = JsonNode.Parse(ResourceModel)!.AsObject();
        var payload = new JsonObject
        {
            ["type"] = type,
            ["item"] = new JsonObject
            {
                ["type"] = "object",
                ["attributes"] = new JsonObject
                {
                    ["metaurl"] = Conditional(new JsonObject { ["versions"] = new JsonObject { ["type"] = "integer" } })
                }
            }
        };
        source["groups"]!["gs"]!["resources"]!["rs"]!["attributes"] =
            new JsonObject { ["mode"] = Conditional(new JsonObject { ["payload"] = payload }) };
        var model = RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString()));
        var item = model.Groups["gs"].Resources["rs"].Attributes["mode"].IfValues["on"]["payload"].Item!;

        await Assert.That(item.Type).IsEqualTo(RegistryValueType.Object);
        await Assert.That(item.Attributes["metaurl"].IfValues["on"]["versions"].Type).IsEqualTo(RegistryValueType.Integer);
    }

    [Test]
    public async Task ConditionalNameChecksKeepMutuallyExclusiveBranchesIndependent()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"attributes":{"mode":{"type":"string","ifvalues":{
              "first":{"siblingattributes":{"detail":{"type":"string","ifvalues":{
                "on":{"siblingattributes":{"shared":"integer"}}
              }}}},
              "second":{"siblingattributes":{"shared":"string"}}
            }}}}
            """));

        await Assert.That(model.Attributes["mode"].IfValues["first"]["detail"].IfValues["on"]["shared"].Type)
            .IsEqualTo(RegistryValueType.Integer);
        await Assert.That(model.Attributes["mode"].IfValues["second"]["shared"].Type)
            .IsEqualTo(RegistryValueType.String);
    }

    [Test]
    [Arguments("root", "documentation", "bad uri %zz")]
    [Arguments("group", "documentation", "https://example.test/%Q0")]
    [Arguments("resource", "documentation", "a b")]
    [Arguments("group", "icon", "")]
    [Arguments("resource", "icon", "")]
    [Arguments("group", "icon", "https://[broken")]
    [Arguments("resource", "icon", "icons\\image.svg")]
    [Arguments("group", "modelcompatiblewith", "bad uri %zz")]
    [Arguments("resource", "modelcompatiblewith", "http://host/\nvalue")]
    public async Task InvalidModelAnnotationReferencesAreRejected(string scope, string name, string value)
    {
        var source = JsonNode.Parse(ResourceModel)!.AsObject();
        var (owner, pointer) = AnnotationOwner(source, scope);
        owner[name] = value;
        var diagnostic = TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString())));

        await Assert.That(diagnostic.Code).IsEqualTo("model_error");
        await Assert.That(diagnostic.Path).IsEqualTo(pointer + "/" + name);
    }

    [Test]
    [Arguments("root", "documentation", "../guide?q=x#part")]
    [Arguments("root", "documentation", "")]
    [Arguments("root", "documentation", "/guides/%E2%98%83")]
    [Arguments("group", "documentation", "?view=full")]
    [Arguments("group", "icon", "../icons/icon.svg")]
    [Arguments("group", "icon", "//cdn.example.test/icon.svg")]
    [Arguments("resource", "icon", "/icons/image.svg?color=red#shape")]
    [Arguments("resource", "icon", "#sprite")]
    [Arguments("group", "modelcompatiblewith", "urn:example:model:1")]
    [Arguments("resource", "modelcompatiblewith", "../models/base.json")]
    [Arguments("resource", "modelcompatiblewith", "./models/a:b")]
    [Arguments("resource", "modelcompatiblewith", "")]
    public async Task ValidAnnotationReferencesArePreservedWithoutResolution(string scope, string name, string value)
    {
        var source = JsonNode.Parse(ResourceModel)!.AsObject();
        var (owner, _) = AnnotationOwner(source, scope);
        owner[name] = value;
        var resolver = new NeverResolve();
        var model = RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString()), new() { Resolver = resolver });
        var effective = scope switch
        {
            "root" => model.EffectiveModel.RootElement,
            "group" => model.EffectiveModel.RootElement.GetProperty("groups").GetProperty("gs"),
            _ => model.EffectiveModel.RootElement.GetProperty("groups").GetProperty("gs")
                .GetProperty("resources").GetProperty("rs")
        };

        await Assert.That(effective.GetProperty(name).GetString()).IsEqualTo(value);
        await Assert.That(resolver.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task NullAnnotationsRemainAbsentOnlyInTheEffectiveModel()
    {
        var source = RegistryJson.Parse("""
            {"description":"","documentation":null,"groups":{"gs":{"singular":"g","icon":null,
              "modelcompatiblewith":null,"resources":{"rs":{"singular":"r","icon":null}}}}}
            """);
        var model = RegistryModel.Compile(source);

        await Assert.That(model.Source.GetPresence("documentation")).IsEqualTo(JsonPresence.Null);
        await Assert.That(model.EffectiveModel.GetPresence("documentation")).IsEqualTo(JsonPresence.Absent);
        await Assert.That(model.Annotations.Description).IsEqualTo("");
        await Assert.That(model.Groups["gs"].Annotations.Icon).IsNull();
        await Assert.That(model.Groups["gs"].Annotations.ModelCompatibleWith).IsNull();
        await Assert.That(model.Groups["gs"].Resources["rs"].Annotations.Icon).IsNull();
    }

    [Test]
    public async Task ImportedSystemOverlaysRetainImmutableAndSharedTypeIdentity()
    {
        var source = RegistryJson.Parse("""
            {"groups":{
              "cs":{"singular":"c","ximportresources":["/bs/rs"],
                "attributes":{"rsurl":{"type":"url","required":true,"readonly":true,"immutable":true,
                  "description":"kept overlay"}},
                "constraints":{"rs.color":{"default":"blue"}}},
              "bs":{"singular":"b","ximportresources":["/as/rs"]},
              "as":{"singular":"a","resources":{"rs":{"singular":"r","hasdocument":false,
                "attributes":{"color":{"type":"string","required":true,"default":"red","enum":["red","blue"]}}}}}
            }}
            """);
        var model = RegistryModel.Compile(source, new() { MaxResourceImportDepth = 2 });
        var imported = model.Groups["cs"];
        var original = model.Groups["as"].Resources["rs"];

        await Assert.That(ReferenceEquals(original, imported.Resources["rs"])).IsTrue();
        await Assert.That(ReferenceEquals(original, model.Groups["bs"].Resources["rs"])).IsTrue();
        await Assert.That(imported.Attributes["rsurl"].IsSystemDefined).IsTrue();
        await Assert.That(imported.Attributes["rsurl"].Immutable).IsTrue();
        await Assert.That(imported.Attributes["rsurl"].ReadOnly).IsTrue();
        await Assert.That(imported.Attributes["rsurl"].Required).IsTrue();
        await Assert.That(imported.Attributes["rsurl"].Description).IsEqualTo("kept overlay");
        await Assert.That(imported.Constraints["rs.color"].DefaultValue.GetString()).IsEqualTo("blue");
        await Assert.That(original.Attributes["color"].DefaultValue.GetString()).IsEqualTo("red");
        await Assert.That(model.Source.RootElement.GetProperty("groups").GetProperty("cs")
            .GetProperty("attributes").GetProperty("rsurl").GetProperty("immutable").GetBoolean()).IsTrue();
        var effective = model.EffectiveModel.RootElement.GetProperty("groups").GetProperty("cs");
        await Assert.That(effective.GetProperty("attributes").GetProperty("rsurl").GetProperty("immutable").GetBoolean()).IsTrue();
        await Assert.That(effective.TryGetProperty("ximportresources", out _)).IsFalse();
    }

    [Test]
    public async Task DelayedImportBaselinesDoNotAuthorizeExtensionImmutable()
    {
        var diagnostic = TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"as":{"singular":"a","resources":{"rs":{"singular":"r"}}},
              "bs":{"singular":"b","ximportresources":["/as/rs"],
                "attributes":{"customurl":{"type":"url","immutable":true}}}}}
            """)));

        await Assert.That(diagnostic.Code).IsEqualTo("model_error");
        await Assert.That(diagnostic.Path).IsEqualTo("/groups/bs/attributes/customurl/immutable");
    }

    [Test]
    public async Task ImportedImmutableCollectionOverlaysSurviveFrozenSourceRecompilation()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"as":{"singular":"a","resources":{"rs":{"singular":"r","hasdocument":false}}},
              "bs":{"singular":"b","ximportresources":["/as/rs"]}}}
            """));
        var frozen = JsonNode.Parse(model.EffectiveModel.RootElement.GetRawText())!.AsObject();
        var imported = frozen["groups"]!["bs"]!;
        imported["resources"] = new JsonObject();
        imported["ximportresources"] = new JsonArray("/as/rs");
        var recompiled = RegistryModel.Compile(RegistryJson.Parse(frozen.ToJsonString()));

        await Assert.That(imported["attributes"]!["rsurl"]!["immutable"]!.GetValue<bool>()).IsTrue();
        await Assert.That(recompiled.Groups["bs"].Attributes["rsurl"].Immutable).IsTrue();
        await Assert.That(recompiled.Groups["bs"].Attributes["rsurl"].IsSystemDefined).IsTrue();
        await Assert.That(ReferenceEquals(recompiled.Groups["as"].Resources["rs"], recompiled.Groups["bs"].Resources["rs"])).IsTrue();
        await Assert.That(recompiled.Source.RootElement.GetProperty("groups").GetProperty("bs")
            .GetProperty("ximportresources")[0].GetString()).IsEqualTo("/as/rs");
    }

    [Test]
    [Arguments("type", "\"string\"")]
    [Arguments("readonly", "false")]
    [Arguments("required", "false")]
    [Arguments("immutable", "false")]
    public async Task ImportOverlayStillCannotWeakenSystemConstraints(string aspect, string value)
    {
        var source = JsonNode.Parse("""
            {"groups":{"as":{"singular":"a","resources":{"rs":{"singular":"r"}}},
              "bs":{"singular":"b","ximportresources":["/as/rs"],
                "attributes":{"rsurl":{"type":"url","immutable":true}}}}}
            """)!.AsObject();
        source["groups"]!["bs"]!["attributes"]!["rsurl"]![aspect] = JsonNode.Parse(value);
        var diagnostic = TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString())));

        await Assert.That(diagnostic.Code).IsEqualTo("model_error");
        await Assert.That(diagnostic.Path).IsEqualTo("/groups/bs/attributes/rsurl");
    }

    private static JsonObject Conditional(JsonObject siblings) => new()
    {
        ["type"] = "string",
        ["required"] = true,
        ["default"] = "off",
        ["ifvalues"] = new JsonObject { ["on"] = new JsonObject { ["siblingattributes"] = siblings } }
    };

    private static (JsonObject Owner, string Pointer) AnnotationOwner(JsonObject source, string scope) => scope switch
    {
        "root" => (source, ""),
        "group" => (source["groups"]!["gs"]!.AsObject(), "/groups/gs"),
        _ => (source["groups"]!["gs"]!["resources"]!["rs"]!.AsObject(), "/groups/gs/resources/rs")
    };

    private sealed class NeverResolve : IRegistryModelResolver
    {
        internal int Calls { get; private set; }

        public RegistryJson Resolve(Uri documentUri)
        {
            Calls++;
            throw new InvalidOperationException("Annotation validation must never acquire the referenced document.");
        }
    }
}
