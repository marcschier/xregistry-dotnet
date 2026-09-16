// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class MessageMaterializationConstraintsTests
{
    [Test]
    [Arguments("""{"dataschemaformat":"JSONSchema/draft-07","dataschema":{}}""",
        """{"dataschemauri":"https://schema.example/new"}""", "/dataschemauri")]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"method":"POST"}}""",
        """{"protocoloptions":{"status":"200"}}""", "/protocoloptions/status")]
    [Arguments("""{"protocol":"KAFKA","protocoloptions":{"key":"key"}}""",
        """{"protocoloptions":{"key_base64":"a2V5"}}""", "/protocoloptions/key_base64")]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"headers":[{"name":"Content-Type","value":"text/plain"}]}}""",
        """{"datacontenttype":"application/json"}""", "/protocoloptions/headers/0/value")]
    [Arguments("""{"envelope":"CloudEvents/1.0","envelopemetadata":{"datacontenttype":{"value":"text/plain"}},"envelopeoptions":{"mode":"binary"}}""",
        """{"datacontenttype":"application/json"}""", "/envelopemetadata/datacontenttype/value")]
    [Arguments("""{"envelope":"CloudEvents/1.0","envelopemetadata":{"dataschema":{"value":"https://schema.example/old"}}}""",
        """{"dataschemaformat":"JSONSchema/draft-07","dataschemauri":"https://schema.example/new"}""", "/envelopemetadata/dataschema/value")]
    public async Task CompositionRejectsContradictoryInheritedConstraints(string baseline, string overlay, string path)
    {
        var exception = await Assert.That(async () => await Compose(baseline, overlay)).Throws<RegistryException>();
        await Assert.That(exception!.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(exception.Diagnostic.Path).IsEqualTo(path);
    }

    [Test]
    public async Task PropertyDefaultsApplyAfterTheCompleteBaseOverlay()
    {
        var result = await Compose("""
            {"envelope":"CloudEvents/1.0",
             "envelopemetadata":{"subject":{"type":"uri","required":true,"value":"urn:base"}},
             "protocol":"HTTP","protocoloptions":{"headers":[{"name":"X-Trace","value":"trace"}]}}
            """, """
            {"envelopemetadata":{"subject":{"value":"urn:child"},"extension":{"value":"kept"},"time":{}}}
            """);
        var envelope = result.Metadata.RootElement.GetProperty("envelopemetadata");
        await Assert.That(envelope.GetProperty("subject").GetProperty("type").GetString()).IsEqualTo("uri");
        await Assert.That(envelope.GetProperty("subject").GetProperty("required").GetBoolean()).IsTrue();
        await Assert.That(envelope.GetProperty("subject").GetProperty("value").GetString()).IsEqualTo("urn:child");
        await Assert.That(envelope.GetProperty("extension").GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(envelope.GetProperty("extension").GetProperty("required").GetBoolean()).IsFalse();
        await Assert.That(envelope.GetProperty("time").GetProperty("value").GetString()).IsEqualTo("0000-01-01T00:00:00Z");
        await Assert.That(result.Metadata.RootElement.GetProperty("protocoloptions").GetProperty("headers")[0]
            .GetProperty("required").GetBoolean()).IsFalse();
    }

    [Test]
    [Arguments("{}", MessageDefinitionBindingKind.Unspecified)]
    [Arguments("""{"dataschemaformat":"JSONSchema/draft-07","dataschema":{"type":"object"}}""", MessageDefinitionBindingKind.Unspecified)]
    [Arguments("""{"envelope":"CloudEvents/1.0","envelopemetadata":{}}""", MessageDefinitionBindingKind.EnvelopeDefined)]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{}}""", MessageDefinitionBindingKind.ExplicitProtocol)]
    [Arguments("""{"envelope":"CloudEvents/1.0","envelopemetadata":{},"protocol":"AMQP/1.0","protocoloptions":{}}""", MessageDefinitionBindingKind.ExplicitProtocol)]
    public async Task BindingPrecedenceDoesNotInventAnImplicitConcreteProtocol(string json, MessageDefinitionBindingKind expected)
    {
        var result = await MessageDefinitionMaterializer.MaterializeAsync(
            new(RegistryJson.Parse(json), new Uri("https://registry.example/messages/message")));
        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Constraints.BindingKind).IsEqualTo(expected);
        if (expected != MessageDefinitionBindingKind.ExplicitProtocol)
        {
            await Assert.That(result.Constraints.Protocol).IsNull();
        }
    }

    [Test]
    public async Task StructuredEnvelopeKeepsPayloadAndWireContentTypesDistinct()
    {
        var result = await Compose("""
            {"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"structured","format":"application/cloudevents+json"},
             "envelopemetadata":{"datacontenttype":{"value":"application/json"}},
             "dataschemaformat":"JSONSchema/draft-07","dataschema":{"type":"object"}}
            """, """
            {"datacontenttype":"application/cloudevents+json","protocol":"HTTP",
             "protocoloptions":{"headers":[{"name":"Content-Type","value":"application/cloudevents+json"}]}}
            """);
        await Assert.That(result.Constraints.RepresentationContentType).IsEqualTo("application/cloudevents+json");
        await Assert.That(result.Constraints.PayloadContentType).IsEqualTo("application/json");
        await Assert.That(result.Constraints.BindingKind).IsEqualTo(MessageDefinitionBindingKind.ExplicitProtocol);
    }

    [Test]
    public async Task OpaqueSchemaAndExtensionValuesAreNotInterpretedAsDeclarations()
    {
        var input = RegistryJson.Parse("""
            {"dataschemaformat":"JSONSchema/draft-07",
             "dataschema":{"type":"object","properties":{"basemessage":{"default":"https://do-not-read.example"},
               "envelope":{"default":"not/an-envelope"}},"const":{"value":null,"number":18446744073709551617}},
             "extension":{"protocol":"HTTP","protocoloptions":{"method":"BAD METHOD"},"value":1.234567890123456789e-20}}
            """);
        var calls = 0;
        var result = await MessageDefinitionMaterializer.MaterializeAsync(new(input, new Uri("https://registry.example/message"),
                model: MessageMaterializationTests.ExtensionModel()),
            (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("Opaque data must not become a source reference.");
            });
        await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(result.Metadata.RootElement.GetProperty("dataschema").GetRawText()),
            JsonNode.Parse(input.RootElement.GetProperty("dataschema").GetRawText()))).IsTrue();
        await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(result.Metadata.RootElement.GetProperty("extension").GetRawText()),
            JsonNode.Parse(input.RootElement.GetProperty("extension").GetRawText()))).IsTrue();
        await Assert.That(result.Metadata.RootElement.GetProperty("dataschema").GetProperty("const").GetProperty("number").GetRawText())
            .IsEqualTo("18446744073709551617");
        await Assert.That(result.Metadata.RootElement.GetProperty("extension").GetProperty("value").GetRawText())
            .IsEqualTo("1.234567890123456789e-20");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task InheritedReferencesRetainTheirDeclaringRegistryAndBaseUriContext()
    {
        var baseline = new MessageDefinition(RegistryJson.Parse("""
            {"dataschemaformat":"JSONSchema/draft-07","dataschemauri":"schemas/event.json",
             "extension":{"specurl":"specs/base"}}
            """), new Uri("https://base.example/registry/messages/base"), new Uri("https://base.example/registry"));
        var original = new MessageDefinition(RegistryJson.Parse("""
            {"basemessage":"https://base.example/registry/messages/base",
             "extension":{"child":"local"}}
            """), new Uri("https://child.example/registry/messages/child"), new Uri("https://child.example/registry"),
            MessageMaterializationTests.ExtensionModel());
        var result = await MessageDefinitionMaterializer.MaterializeAsync(original,
            (_, _) => ValueTask.FromResult(MessageDefinitionSourceResult.Found(baseline)));
        await Assert.That(result.PropertySources["/dataschemauri"].Location.AbsoluteUri)
            .IsEqualTo("https://base.example/registry/messages/base");
        await Assert.That(result.PropertySources["/dataschemauri"].RegistryRoot!.AbsoluteUri)
            .IsEqualTo("https://base.example/registry");
        await Assert.That(result.PropertySources["/extension/specurl"].Location.AbsoluteUri)
            .IsEqualTo("https://base.example/registry/messages/base");
        await Assert.That(result.PropertySources["/extension/child"].Location.AbsoluteUri)
            .IsEqualTo("https://child.example/registry/messages/child");
        await Assert.That(result.Constraints.DataSchemaUri).IsEqualTo("schemas/event.json");
        await Assert.That(result.Original.Location.AbsoluteUri).IsEqualTo("https://child.example/registry/messages/child");
    }

    [Test]
    public async Task ImpliedCloudEventsRequirementsAndPayloadReferencesBecomeExplicitConstraints()
    {
        var result = await MessageDefinitionMaterializer.MaterializeAsync(new(RegistryJson.Parse("""
            {"envelope":"CloudEvents/1.0","envelopemetadata":{},"envelopeoptions":{"mode":"binary"},
             "dataschemaformat":"JsonSchema/draft-07","dataschemauri":"https://schema.example/event"}
            """), new Uri("https://registry.example/message")));
        var expected = JsonNode.Parse("""
            {"specversion":{"type":"string","required":true,"value":"1.0"},
             "id":{"type":"string","required":true},"type":{"type":"string","required":true},
             "source":{"type":"uritemplate","required":true},
             "time":{"type":"timestamp","required":false,"value":"0000-01-01T00:00:00Z"},
             "dataschema":{"value":"https://schema.example/event","type":"uritemplate","required":false},
             "datacontenttype":{"value":"application/json","type":"string","required":false}}
            """);
        await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(result.Constraints.EnvelopeMetadata.GetRawText()), expected)).IsTrue();
        await Assert.That(result.Constraints.PayloadContentType).IsEqualTo("application/json");
        await Assert.That(result.Constraints.RepresentationContentType).IsEqualTo("application/json");
        await Assert.That(result.PropertySources["/envelopemetadata/dataschema/value"].Location.AbsoluteUri)
            .IsEqualTo("https://registry.example/message");
    }

    [Test]
    public async Task StructuredFormatSelectsTheOuterTypeWithoutReplacingTheInferredPayloadType()
    {
        var result = await MessageDefinitionMaterializer.MaterializeAsync(new(RegistryJson.Parse("""
            {"envelope":"CloudEvents/1.0","envelopemetadata":{},
             "envelopeoptions":{"mode":"structured","format":"application/cloudevents+json"},
             "dataschemaformat":"JSONSchema/draft-07","dataschemauri":"https://schema.example/event"}
            """), new Uri("https://registry.example/message")));
        await Assert.That(result.Constraints.RepresentationContentType).IsEqualTo("application/cloudevents+json");
        await Assert.That(result.Constraints.PayloadContentType).IsEqualTo("application/json");
    }

    [Test]
    public async Task InlineSchemaInferenceNeverInventsAnAcquisitionUri()
    {
        var calls = 0;
        var result = await MessageDefinitionMaterializer.MaterializeAsync(new(RegistryJson.Parse("""
            {"envelope":"CloudEvents/1.0","envelopemetadata":{},
             "dataschemaformat":"JSONSchema/draft-07","dataschema":{"type":"object"}}
            """), new Uri("https://registry.example/message")), (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("An inline schema does not authorize a metadata read.");
        });
        await Assert.That(result.Constraints.EnvelopeMetadata.TryGetProperty("dataschema", out _)).IsFalse();
        await Assert.That(result.Obligations.Any(static obligation => obligation.Kind == "message_schema_uri")).IsTrue();
        await Assert.That(result.Constraints.DataSchema.GetProperty("type").GetString()).IsEqualTo("object");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnknownSchemaFormatInferenceIsCallerDefinedOrExplicitlyUnresolved(bool provideMapping)
    {
        var result = await MessageDefinitionMaterializer.MaterializeAsync(new(RegistryJson.Parse("""
            {"envelope":"CloudEvents/1.0","envelopemetadata":{},
             "dataschemaformat":"Custom/1","dataschemauri":"urn:example:schema"}
            """), new Uri("https://registry.example/message")),
            options: new() { PayloadContentTypeResolver = provideMapping ? static _ => "application/x-example" : null });
        if (provideMapping)
        {
            await Assert.That(result.Constraints.PayloadContentType).IsEqualTo("application/x-example");
            await Assert.That(result.Obligations.Any(static obligation => obligation.Kind == "message_payload_content_type")).IsFalse();
        }
        else
        {
            await Assert.That(result.Constraints.PayloadContentType).IsNull();
            await Assert.That(result.Obligations.Any(static obligation => obligation.Kind == "message_payload_content_type")).IsTrue();
        }
    }

    [Test]
    [Arguments("HTTP")]
    [Arguments("NATS")]
    public async Task AHeaderWithoutAValueDoesNotHideALaterContentTypeConstraint(string protocol)
    {
        var metadata = RegistryJson.Parse(new JsonObject
        {
            ["protocol"] = protocol,
            ["protocoloptions"] = JsonNode.Parse("""
                {"headers":[{"name":"Content-Type"},{"name":"content-type","value":"application/json"}]}
                """)
        }.ToJsonString());
        var result = await MessageDefinitionMaterializer.MaterializeAsync(new(metadata, new Uri("https://registry.example/message")));
        await Assert.That(result.Constraints.RepresentationContentType).IsEqualTo("application/json");
        await Assert.That(result.Constraints.ProtocolOptions.GetProperty("headers").GetArrayLength()).IsEqualTo(2);
    }

    private static ValueTask<MessageMaterializationResult> Compose(string baseline, string overlay)
    {
        var authored = JsonNode.Parse(overlay)!.AsObject();
        authored["basemessage"] = "https://base.example/message";
        return MessageDefinitionMaterializer.MaterializeAsync(
            new(RegistryJson.Parse(authored.ToJsonString()), new Uri("https://derived.example/message")),
            (_, _) => ValueTask.FromResult(MessageDefinitionSourceResult.Found(
                new(RegistryJson.Parse(baseline), new Uri("https://base.example/message")))));
    }
}
