// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class MessageMaterializationRawDeclarationsTests
{
    [Test]
    [Arguments("HTTP", "headers", false)]
    [Arguments("NATS", "headers", false)]
    [Arguments("MQTT/5.0", "user_properties", false)]
    [Arguments("KAFKA", "headers", true)]
    public async Task ProtocolHeaderSpecificationUrisUseTheSameDomainSyntaxChecks(
        string protocol, string field, bool map)
    {
        foreach (var reference in new[] { "../specs/header", "urn:example:header", "bad uri", "specs/%xz" })
        {
            var declaration = new JsonObject { ["name"] = "X-Test", ["specurl"] = reference };
            var headers = map ? (JsonNode)new JsonObject { ["example"] = declaration } : new JsonArray(declaration);
            var input = RegistryJson.Parse(new JsonObject
            {
                ["protocol"] = protocol, ["protocoloptions"] = new JsonObject { [field] = headers }
            }.ToJsonString());
            if (reference is "bad uri" or "specs/%xz")
            {
                var error = await Assert.That(() => RegistryDomainRules.CompleteMessageMetadata(input)).Throws<RegistryException>();
                await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid_attribute");
                await Assert.That(error.Diagnostic.Path).IsEqualTo("/protocoloptions/" + field + (map ? "/example" : "/0") + "/specurl");
            }
            else
            {
                var completed = RegistryDomainRules.CompleteMessageMetadata(input);
                var values = completed.RootElement.GetProperty("protocoloptions").GetProperty(field);
                var property = map ? values.GetProperty("example") : values[0];
                await Assert.That(property.GetProperty("specurl").GetString()).IsEqualTo(reference);
                await Assert.That(property.GetProperty("name").GetString()).IsEqualTo("X-Test");
            }
        }
    }

    [Test]
    [Arguments("envelopemetadata")]
    [Arguments("properties")]
    [Arguments("application-properties")]
    [Arguments("message-annotations")]
    [Arguments("delivery-annotations")]
    [Arguments("footer")]
    public async Task OpaquePropertySpecificationReferencesUseCoreUriSyntaxWithoutAcquisition(string section)
    {
        var name = section == "properties" ? "subject" : "example";
        foreach (var reference in new[] { "../specs/property", "/specs/property", "#part", "urn:example:property",
            "https://specs.example/property?q=value#part", "specs/%E2%82%AC" })
        {
            var declarations = new JsonObject { [name] = new JsonObject { ["specurl"] = reference } };
            var definition = new MessageDefinition(Metadata(section, declarations.ToJsonString()),
                new Uri("https://registry.example/message"), model: Model(section, opaque: true, defaults: false));
            var calls = 0;
            var result = await MessageDefinitionMaterializer.MaterializeAsync(definition, (_, _) =>
            {
                calls++;
                throw new UnauthorizedAccessException("Specification references must not be acquired.");
            });
            var path = SectionPath(section) + "/" + name + "/specurl";
            await Assert.That(Declarations(result.Metadata.RootElement, section).GetProperty(name)
                .GetProperty("specurl").GetString()).IsEqualTo(reference);
            await Assert.That(result.PropertySources[path].Location).IsEqualTo(definition.Location);
            await Assert.That(result.IsComplete).IsTrue();
            await Assert.That(calls).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("envelopemetadata")]
    [Arguments("properties")]
    [Arguments("application-properties")]
    [Arguments("message-annotations")]
    [Arguments("delivery-annotations")]
    [Arguments("footer")]
    public async Task OpaquePropertySpecificationReferencesCannotBypassUriValidation(string section)
    {
        var name = section == "properties" ? "subject" : "example";
        foreach (var reference in new[] { "bad uri", "https://specs.example/%", "https://specs.example/%xz",
            @"https://specs.example\property", "specs\nproperty", "https://[invalid/property" })
        {
            var declarations = new JsonObject { [name] = new JsonObject { ["specurl"] = reference } };
            var definition = new MessageDefinition(Metadata(section, declarations.ToJsonString()),
                new Uri("https://registry.example/message"), model: Model(section, opaque: true, defaults: false));
            var calls = 0;
            var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(definition, (_, _) =>
            {
                calls++;
                throw new UnauthorizedAccessException("Specification references must not be acquired.");
            })).Throws<RegistryException>();
            await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid_attribute");
            await Assert.That(error.Diagnostic.Path).IsEqualTo(SectionPath(section) + "/" + name + "/specurl");
            await Assert.That(calls).IsEqualTo(0);
        }
    }

    [Test]
    public async Task InheritedSpecificationReferencesPreserveDeclaringOriginsAcrossOverlay()
    {
        var model = Model("envelopemetadata", opaque: true, defaults: false);
        var baseline = new MessageDefinition(Metadata("envelopemetadata",
            """{"example":{"specurl":"../specs/property","description":"base"}}"""),
            new Uri("https://base.example/messages/base"), model: model);
        var authored = JsonNode.Parse(Metadata("envelopemetadata",
            """{"example":{"description":"child"}}""").RootElement.GetRawText())!.AsObject();
        authored["basemessage"] = baseline.Location.AbsoluteUri;
        var root = new MessageDefinition(RegistryJson.Parse(authored.ToJsonString()),
            new Uri("https://child.example/messages/child"), model: model);
        var calls = 0;
        var result = await MessageDefinitionMaterializer.MaterializeAsync(root, (request, _) =>
        {
            calls++;
            if (request.TargetUri != baseline.Location) { throw new InvalidOperationException("Unexpected source."); }
            return ValueTask.FromResult(MessageDefinitionSourceResult.Found(baseline));
        });
        var property = result.Metadata.RootElement.GetProperty("envelopemetadata").GetProperty("example");
        await Assert.That(property.GetProperty("specurl").GetString()).IsEqualTo("../specs/property");
        await Assert.That(property.GetProperty("description").GetString()).IsEqualTo("child");
        await Assert.That(result.PropertySources["/envelopemetadata/example/specurl"].Location).IsEqualTo(baseline.Location);
        await Assert.That(result.PropertySources["/envelopemetadata/example/description"].Location).IsEqualTo(root.Location);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("envelopemetadata", false)]
    [Arguments("envelopemetadata", true)]
    [Arguments("application-properties", false)]
    [Arguments("application-properties", true)]
    [Arguments("message-annotations", false)]
    [Arguments("message-annotations", true)]
    [Arguments("delivery-annotations", false)]
    [Arguments("delivery-annotations", true)]
    [Arguments("footer", false)]
    [Arguments("footer", true)]
    public async Task ModeledAnyDoesNotEraseLiteralNullOrInventAnAbsentValue(string section, bool opaque)
    {
        var metadata = Metadata(section, """
            {"literal":{"type":"any","value":null},"unconstrained":{"type":"any"}}
            """);
        var definition = new MessageDefinition(metadata, new Uri("https://registry.example/message"),
            model: Model(section, opaque, defaults: true));

        var result = await MessageDefinitionMaterializer.MaterializeAsync(definition);

        var declarations = Declarations(result.Metadata.RootElement, section);
        await Assert.That(declarations.GetProperty("literal").TryGetProperty("value", out var value)).IsTrue();
        await Assert.That(value.ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(declarations.GetProperty("unconstrained").TryGetProperty("value", out _)).IsFalse();
        await Assert.That(declarations.GetProperty("literal").GetProperty("type").GetString()).IsEqualTo("any");
        await Assert.That(declarations.GetProperty("literal").GetProperty("required").GetBoolean()).IsFalse();
        await Assert.That(Declarations(definition.Metadata.RootElement, section).GetProperty("literal")
            .TryGetProperty("required", out _)).IsFalse();
    }

    [Test]
    [Arguments("envelopemetadata", false)]
    [Arguments("envelopemetadata", true)]
    [Arguments("application-properties", false)]
    [Arguments("application-properties", true)]
    public async Task DomainDefaultsDoNotDependOnNestedModelDefaults(string section, bool opaque)
    {
        var result = await MessageDefinitionMaterializer.MaterializeAsync(
            new(Metadata(section, """{"example":{"value":"literal"}}"""), new Uri("https://registry.example/message"),
                model: Model(section, opaque, defaults: false)));
        var property = Declarations(result.Metadata.RootElement, section).GetProperty("example");
        await Assert.That(property.GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(property.GetProperty("required").GetBoolean()).IsFalse();
        await Assert.That(property.GetProperty("value").GetString()).IsEqualTo("literal");
    }

    [Test]
    [Arguments("envelopemetadata", """{"type":"boolean","value":null}""", "value")]
    [Arguments("application-properties", """{"type":"boolean","value":null}""", "value")]
    [Arguments("envelopemetadata", """{"type":null,"value":"literal"}""", "type")]
    [Arguments("application-properties", """{"type":null,"value":"literal"}""", "type")]
    [Arguments("envelopemetadata", """{"required":null,"value":"literal"}""", "required")]
    [Arguments("application-properties", """{"required":null,"value":"literal"}""", "required")]
    public async Task InvalidNullDeclarationsCannotBeNormalizedIntoValidConstraints(string section, string declaration, string field)
    {
        var declarations = new JsonObject { ["example"] = JsonNode.Parse(declaration) };
        var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(
            new(Metadata(section, declarations.ToJsonString()), new Uri("https://registry.example/message"),
                model: Model(section, opaque: false, defaults: true)))).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(error.Diagnostic.Path).IsEqualTo(SectionPath(section) + "/example/" + field);
    }

    [Test]
    [Arguments("envelopemetadata")]
    [Arguments("application-properties")]
    public async Task InheritedLiteralNullSurvivesAChildOverlayWithoutAValue(string section)
    {
        var model = Model(section, opaque: false, defaults: true);
        var baseline = new MessageDefinition(Metadata(section, """{"example":{"type":"any","value":null}}"""),
            new Uri("https://base.example/message"), model: model);
        var authored = JsonNode.Parse(Metadata(section, """{"example":{"description":"child"}}""").RootElement.GetRawText())!.AsObject();
        authored["basemessage"] = baseline.Location.AbsoluteUri;
        var root = new MessageDefinition(RegistryJson.Parse(authored.ToJsonString()), new Uri("https://derived.example/message"), model: model);
        var result = await MessageDefinitionMaterializer.MaterializeAsync(root,
            (_, _) => ValueTask.FromResult(MessageDefinitionSourceResult.Found(baseline)));
        var property = Declarations(result.Metadata.RootElement, section).GetProperty("example");
        await Assert.That(property.TryGetProperty("value", out var value)).IsTrue();
        await Assert.That(value.ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(property.GetProperty("description").GetString()).IsEqualTo("child");
        await Assert.That(result.PropertySources[SectionPath(section) + "/example/value"].Location.AbsoluteUri)
            .IsEqualTo("https://base.example/message");
        await Assert.That(result.PropertySources[SectionPath(section) + "/example/description"].Location.AbsoluteUri)
            .IsEqualTo("https://derived.example/message");
    }

    [Test]
    [Arguments("envelopemetadata")]
    [Arguments("application-properties")]
    public async Task ChildLiteralNullOverridesAnInheritedValueWithoutDeletingTheConstraint(string section)
    {
        var model = Model(section, opaque: false, defaults: true);
        var baseline = new MessageDefinition(Metadata(section, """{"example":{"type":"any","value":7}}"""),
            new Uri("https://base.example/message"), model: model);
        var authored = JsonNode.Parse(Metadata(section, """{"example":{"value":null}}""").RootElement.GetRawText())!.AsObject();
        authored["basemessage"] = baseline.Location.AbsoluteUri;
        var result = await MessageDefinitionMaterializer.MaterializeAsync(
            new(RegistryJson.Parse(authored.ToJsonString()), new Uri("https://derived.example/message"), model: model),
            (_, _) => ValueTask.FromResult(MessageDefinitionSourceResult.Found(baseline)));
        var property = Declarations(result.Metadata.RootElement, section).GetProperty("example");
        await Assert.That(property.GetProperty("value").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(property.GetProperty("type").GetString()).IsEqualTo("any");
        await Assert.That(result.PropertySources[SectionPath(section) + "/example/value"].Location.AbsoluteUri)
            .IsEqualTo("https://derived.example/message");
    }

    [Test]
    public async Task OpaqueFixedAmqpPropertiesUseTheirDomainSpecificDefaults()
    {
        var result = await MessageDefinitionMaterializer.MaterializeAsync(new(Metadata("properties", """
            {"user-id":{"value":"AA=="},"to":{"value":"urn:destination"},
             "absolute-expiry-time":{"value":"2030-01-01T00:00:00Z"},"group-sequence":{"value":7}}
            """), new Uri("https://registry.example/message"), model: Model("properties", opaque: true, defaults: false)));
        var properties = Declarations(result.Metadata.RootElement, "properties");
        await Assert.That(properties.GetProperty("user-id").GetProperty("type").GetString()).IsEqualTo("binary");
        await Assert.That(properties.GetProperty("to").GetProperty("type").GetString()).IsEqualTo("uritemplate");
        await Assert.That(properties.GetProperty("absolute-expiry-time").GetProperty("type").GetString()).IsEqualTo("timestamp");
        await Assert.That(properties.GetProperty("group-sequence").GetProperty("type").GetString()).IsEqualTo("integer");
        foreach (var property in properties.EnumerateObject())
        {
            await Assert.That(property.Value.GetProperty("required").GetBoolean()).IsFalse();
        }
    }

    [Test]
    public async Task OrdinaryTopLevelModelDefaultsStillPrecedeCrossAspectValidation()
    {
        using var packaged = BuiltInRegistryModels.LoadSource(RegistryModelKind.Message);
        var source = JsonNode.Parse(packaged.RootElement.GetRawText())!.AsObject();
        source["groups"]!["messagegroups"]!["resources"]!["messages"]!["attributes"]!["dataschemaformat"]!["default"] =
            "JSONSchema/draft-07";
        source["groups"]!["messagegroups"]!["resources"]!["messages"]!["attributes"]!["dataschemaformat"]!["required"] = true;
        var model = RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString()));
        var result = await MessageDefinitionMaterializer.MaterializeAsync(new(
            RegistryJson.Parse("""{"dataschema":{"type":"object"}}"""), new Uri("https://registry.example/message"), model: model));
        await Assert.That(result.Metadata.RootElement.GetProperty("dataschemaformat").GetString()).IsEqualTo("JSONSchema/draft-07");
        await Assert.That(result.Metadata.RootElement.GetProperty("dataschema").GetProperty("type").GetString()).IsEqualTo("object");
    }

    private static string SectionPath(string section) =>
        section == "envelopemetadata" ? "/envelopemetadata" : "/protocoloptions/" + section;

    private static RegistryJson Metadata(string section, string declarations)
    {
        var data = section == "envelopemetadata"
            ? new JsonObject { ["envelope"] = "CloudEvents/1.0", ["envelopemetadata"] = JsonNode.Parse(declarations) }
            : new JsonObject
            {
                ["protocol"] = "AMQP/1.0",
                ["protocoloptions"] = new JsonObject { [section] = JsonNode.Parse(declarations) }
            };
        return RegistryJson.Parse(data.ToJsonString());
    }

    private static JsonElement Declarations(JsonElement metadata, string section) =>
        section == "envelopemetadata" ? metadata.GetProperty(section) : metadata.GetProperty("protocoloptions").GetProperty(section);

    private static RegistryModel Model(string section, bool opaque, bool defaults)
    {
        using var packaged = BuiltInRegistryModels.LoadSource(RegistryModelKind.Message);
        var source = JsonNode.Parse(packaged.RootElement.GetRawText())!.AsObject();
        var attributes = source["groups"]!["messagegroups"]!["resources"]!["messages"]!["attributes"]!;
        var target = section == "envelopemetadata"
            ? attributes["envelope"]!["ifvalues"]!["CloudEvents/1.0"]!["siblingattributes"]!["envelopemetadata"]!.AsObject()
            : attributes["protocol"]!["ifvalues"]!["AMQP/1.0"]!["siblingattributes"]!["protocoloptions"]!["attributes"]![section]!.AsObject();
        target["type"] = opaque ? "any" : "object";
        if (opaque)
        {
            target.Remove("attributes");
        }
        else
        {
            var type = new JsonObject { ["type"] = "string", ["required"] = true };
            var required = new JsonObject { ["type"] = "boolean", ["required"] = true };
            if (defaults)
            {
                type["default"] = "string";
                required["default"] = false;
            }
            target["attributes"] = new JsonObject
            {
                ["*"] = new JsonObject
                {
                    ["type"] = "object",
                    ["attributes"] = new JsonObject
                    {
                        ["type"] = type,
                        ["required"] = required,
                        ["value"] = new JsonObject { ["type"] = "any" },
                        ["description"] = new JsonObject { ["type"] = "string" },
                        ["specurl"] = new JsonObject { ["type"] = "uri" }
                    }
                }
            };
        }
        return RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString()));
    }
}
