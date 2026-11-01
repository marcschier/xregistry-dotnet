// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class MessageEndpointRuntimeRulesTests
{
    [Test]
    [Arguments("true", true)]
    [Arguments("false", true)]
    [Arguments("null", false)]
    [Arguments("0", false)]
    [Arguments("1", false)]
    [Arguments("\"true\"", false)]
    [Arguments("{}", false)]
    [Arguments("[]", false)]
    [Arguments("""{"value":true}""", false)]
    public async Task MqttRetainMustBeANativeBooleanForBothProtocolVersions(string value, bool valid)
    {
        foreach (var protocol in new[] { "MQTT/3.1.1", "MQTT/5.0", "mqtt/5.0" })
        {
            var input = RegistryJson.Parse(new JsonObject
            {
                ["protocol"] = protocol,
                ["protocoloptions"] = new JsonObject { ["retain"] = JsonNode.Parse(value) }
            }.ToJsonString());
            if (valid)
            {
                var completed = RegistryDomainRules.CompleteMessageMetadata(input);
                await Assert.That(completed.RootElement.GetProperty("protocoloptions").GetProperty("retain").GetBoolean())
                    .IsEqualTo(value == "true");
                await Assert.That(completed.RootElement.GetProperty("protocol").GetString()).IsEqualTo(protocol);
            }
            else
            {
                var error = await Assert.That(() => RegistryDomainRules.CompleteMessageMetadata(input)).Throws<RegistryException>();
                await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid_attribute");
                await Assert.That(error.Diagnostic.Path).IsEqualTo("/protocoloptions/retain");
            }
        }
    }

    [Test]
    [Arguments("[]", "/protocoloptions/header")]
    [Arguments("true", "/protocoloptions/header")]
    [Arguments("\"header\"", "/protocoloptions/header")]
    [Arguments("{}", "")]
    [Arguments("""{"durable":true,"first-acquirer":false}""", "")]
    [Arguments("""{"durable":false,"first-acquirer":true}""", "")]
    [Arguments("""{"durable":"true"}""", "/protocoloptions/header/durable")]
    [Arguments("""{"first-acquirer":1}""", "/protocoloptions/header/first-acquirer")]
    [Arguments("""{"durable":{"value":true}}""", "/protocoloptions/header/durable")]
    [Arguments("""{"first-acquirer":null}""", "/protocoloptions/header/first-acquirer")]
    public async Task AmqpHeaderRetainsItsObjectAndNativeBooleanScalarShapes(string header, string errorPath)
    {
        var input = RegistryJson.Parse("""{"protocol":"AMQP/1.0","protocoloptions":{"header":""" + header + "}}");
        if (errorPath.Length != 0)
        {
            var error = await Assert.That(() => RegistryDomainRules.CompleteMessageMetadata(input)).Throws<RegistryException>();
            await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid_attribute");
            await Assert.That(error.Diagnostic.Path).IsEqualTo(errorPath);
        }
        else
        {
            var completed = RegistryDomainRules.CompleteMessageMetadata(input);
            await Assert.That(completed.RootElement.GetProperty("protocoloptions").GetProperty("header").GetRawText())
                .IsEqualTo(header);
        }
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"dataschemauri":null}""")]
    public async Task AbsentAndClearedSchemaUriDoNotRequireAFormat(string metadata)
    {
        var input = RegistryJson.Parse(metadata);
        var completed = RegistryDomainRules.CompleteMessageMetadata(input);
        await Assert.That(completed.RootElement.TryGetProperty("dataschemaformat", out _)).IsFalse();
        await Assert.That(completed.RootElement.GetRawText()).IsEqualTo(input.RootElement.GetRawText());
    }

    [Test]
    [Arguments("bad uri")]
    [Arguments("schemas/%")]
    [Arguments("schemas/%xz")]
    [Arguments("https://schemas.example\\event")]
    [Arguments("https://[invalid/event")]
    [Arguments("schemas/\nevent")]
    public async Task MessageSchemaUriDomainChecksRejectMalformedReferencesBeforeSelection(string reference)
    {
        var input = RegistryJson.Parse(new JsonObject
        {
            ["dataschemaformat"] = "JSONSchema/draft-07", ["dataschemauri"] = reference
        }.ToJsonString());
        var error = await Assert.That(() => RegistryDomainRules.CompleteMessageMetadata(input)).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(error.Diagnostic.Path).IsEqualTo("/dataschemauri");
    }

    [Test]
    [Arguments("")]
    [Arguments("../schemas/event.json#/$defs/Event")]
    [Arguments("#/$defs/Event")]
    [Arguments("/schemas/event")]
    [Arguments("urn:example:event")]
    [Arguments("https://schemas.example/event?q=value#Event")]
    [Arguments("schemas/%E2%82%AC")]
    public async Task MessageSchemaUriDomainChecksPreserveRelativeAbsoluteAndEmptyReferences(string reference)
    {
        var input = RegistryJson.Parse(new JsonObject
        {
            ["dataschemaformat"] = "JSONSchema/draft-07", ["dataschemauri"] = reference
        }.ToJsonString());
        var completed = RegistryDomainRules.CompleteMessageMetadata(input);
        await Assert.That(completed.RootElement.GetProperty("dataschemauri").GetString()).IsEqualTo(reference);
    }

    [Test]
    [Arguments("""{"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"binary"},"envelopemetadata":{"datacontenttype":{"value":"TEXT/PLAIN; CHARSET=UTF-8"}}}""")]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"headers":[{"name":"Content-Type","value":"TEXT/PLAIN; CHARSET=UTF-8"}]}}""")]
    [Arguments("""{"protocol":"NATS","protocoloptions":{"headers":[{"name":"Content-Type","value":"TEXT/PLAIN; CHARSET=UTF-8"}]}}""")]
    [Arguments("""{"protocol":"AMQP/1.0","protocoloptions":{"properties":{"content-type":{"value":"TEXT/PLAIN; CHARSET=UTF-8"}}}}""")]
    [Arguments("""{"protocol":"MQTT/5.0","protocoloptions":{"content_type":"TEXT/PLAIN; CHARSET=UTF-8"}}""")]
    [Arguments("""{"protocol":"KAFKA","protocoloptions":{"headers":{"content-type":{"name":"content-type","value":"TEXT/PLAIN; CHARSET=UTF-8"}}}}""")]
    public async Task CharsetComparisonAppliesToEachMessageContentTypeDeclaration(string metadata)
    {
        var node = JsonNode.Parse(metadata)!.AsObject();
        node["datacontenttype"] = "text/plain; charset=utf-8";
        var input = RegistryJson.Parse(node.ToJsonString());
        var completed = RegistryDomainRules.CompleteMessageMetadata(input);
        await Assert.That(completed.RootElement.GetProperty("datacontenttype").GetString()).IsEqualTo("text/plain; charset=utf-8");
        node["datacontenttype"] = "text/plain; charset=us-ascii";
        var invalid = RegistryJson.Parse(node.ToJsonString());
        var error = await Assert.That(() => RegistryDomainRules.ValidateMessageMetadata(invalid.RootElement)).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid_attribute");
    }

    [Test]
    [Arguments("text/plain; charset=utf-8", "TEXT/Plain; CHARSET=UTF-8", true)]
    [Arguments("text/plain; charset=utf-8", "text/plain; charset=\"UTF-8\"", true)]
    [Arguments("text/plain; charset=\"uTf-8\"", "text/plain; charset=utf-8", true)]
    [Arguments("text/plain; charset=utf-8; profile=Case", "TEXT/PLAIN; PROFILE=Case; CHARSET=UTF-8", true)]
    [Arguments("text/plain; charset=utf-8", "text/plain; charset=us-ascii", false)]
    [Arguments("text/plain; charset=utf-8; profile=Case", "text/plain; charset=UTF-8; profile=case", false)]
    [Arguments("text/plain; charset=utf-8", "text/plain", false)]
    [Arguments("multipart/mixed; boundary=Case", "multipart/mixed; boundary=case", false)]
    [Arguments("text/plain; charset=utf-8; charset=us-ascii", "text/plain; charset=US-ASCII; charset=UTF-8", true)]
    public async Task ContentTypeCharsetComparisonPreservesOtherParameterSemantics(
        string topLevel, string header, bool valid)
    {
        var input = RegistryJson.Parse(new JsonObject
        {
            ["datacontenttype"] = topLevel,
            ["protocol"] = "HTTP",
            ["protocoloptions"] = new JsonObject
            {
                ["headers"] = new JsonArray(new JsonObject
                {
                    ["name"] = "Content-Type", ["value"] = header
                })
            }
        }.ToJsonString());
        if (!valid)
        {
            var error = await Assert.That(() => RegistryDomainRules.CompleteMessageMetadata(input)).Throws<RegistryException>();
            await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid_attribute");
            await Assert.That(error.Diagnostic.Path).IsEqualTo("/protocoloptions/headers/0/value");
            return;
        }
        var completed = RegistryDomainRules.CompleteMessageMetadata(input);
        await Assert.That(completed.RootElement.GetProperty("datacontenttype").GetString()).IsEqualTo(topLevel);
        await Assert.That(completed.RootElement.GetProperty("protocoloptions").GetProperty("headers")[0].GetProperty("value").GetString())
            .IsEqualTo(header);
    }

    [Test]
    [Arguments("HTTP")]
    [Arguments("AMQP/1.0")]
    [Arguments("MQTT/3.1.1")]
    [Arguments("MQTT/5.0")]
    [Arguments("KAFKA")]
    [Arguments("NATS")]
    public async Task MessageProtocolRulesRequireAnObjectAcrossBindings(string protocol)
    {
        await Rejects($$$"""{"protocol":"{{{protocol}}}"}""", "/protocoloptions");
        var valid = RegistryJson.Parse($$$"""{"protocol":"{{{protocol}}}","protocoloptions":{}}""");
        await Assert.That(() => RegistryDomainRules.ValidateMessageMetadata(valid.RootElement)).ThrowsNothing();
    }

    [Test]
    public async Task MessageEnvelopeRulesRequireMetadataWithoutRequiringAnEnvelope()
    {
        await Rejects("""{"envelope":"CloudEvents/1.0"}""", "/envelopemetadata");
        var envelope = RegistryJson.Parse("""{"envelope":"CloudEvents/1.0","envelopemetadata":{}}""");
        await Assert.That(() => RegistryDomainRules.ValidateMessageMetadata(envelope.RootElement)).ThrowsNothing();
        var payloadOnly = RegistryJson.Parse("""{"dataschemaformat":"JSONSchema/draft-07","dataschema":{"type":"object"}}""");
        await Assert.That(() => RegistryDomainRules.ValidateMessageMetadata(payloadOnly.RootElement)).ThrowsNothing();
    }

    [Test]
    [Arguments("""{"dataschema":{}}""", "/dataschemaformat")]
    [Arguments("""{"dataschemauri":"https://schemas.example/a.json"}""", "/dataschemaformat")]
    [Arguments("""{"dataschemaformat":"JSONSchema/draft-07","dataschema":{},"dataschemauri":"https://schemas.example/a.json"}""", "/dataschemauri")]
    [Arguments("""
        {"envelope":"CloudEvents/1.0","envelopemetadata":{"dataschema":{"value":"https://schemas.example/b.json"}},
         "dataschemaformat":"JSONSchema/draft-07","dataschemauri":"https://schemas.example/a.json"}
        """, "/envelopemetadata/dataschema/value")]
    public Task MessageSchemaRulesDiagnoseEachIndependentConstraint(string metadata, string path) => Rejects(metadata, path);

    [Test]
    [Arguments("{}")]
    [Arguments("""{"dataschemaformat":"JSONSchema/draft-07","dataschema":{"type":"object"}}""")]
    [Arguments("""{"dataschemaformat":"JSONSchema/draft-07","dataschemauri":"https://schemas.example/a.json"}""")]
    [Arguments("""
        {"envelope":"CloudEvents/1.0","envelopemetadata":{"dataschema":{"value":"https://schemas.example/a.json"}},
         "dataschemaformat":"JSONSchema/draft-07","dataschemauri":"https://schemas.example/a.json"}
        """)]
    public async Task MessageSchemaRulesAcceptNearestValidDeclarations(string metadata)
    {
        var value = RegistryJson.Parse(metadata);
        await Assert.That(() => RegistryDomainRules.ValidateMessageMetadata(value.RootElement)).ThrowsNothing();
    }

    [Test]
    [Arguments("""{"protocol":"","protocoloptions":{}}""", "/protocol")]
    [Arguments("""{"envelope":"","envelopemetadata":{}}""", "/envelope")]
    [Arguments("""{"dataschemaformat":""}""", "/dataschemaformat")]
    [Arguments("""{"envelope":"/1.0","envelopemetadata":{}}""", "/envelope")]
    [Arguments("""{"dataschemaformat":"JSONSchema"}""", "/dataschemaformat")]
    [Arguments("""{"envelope":"CloudEvents/1.0","envelopemetadata":{"subject":{"description":""}}}""", "/envelopemetadata/subject/description")]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"headers":[{"name":"x-name","description":"","value":"x"}]}}""", "/protocoloptions/headers/0/description")]
    [Arguments("""{"protocol":"AMQP/1.0","protocoloptions":{"application-properties":{"count":{"description":"","value":"one"}}}}""", "/protocoloptions/application-properties/count/description")]
    [Arguments("""{"protocol":3,"protocoloptions":{}}""", "/protocol")]
    [Arguments("[]", "")]
    public Task MessageStringsEnforceOnlyDeclaredDomainConstraints(string metadata, string path) => Rejects(metadata, path);

    [Test]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"method":"two words"}}""", "/protocoloptions/method")]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"headers":[{"name":"bad name","value":"x"}]}}""", "/protocoloptions/headers/0/name")]
    [Arguments("""{"protocol":"KAFKA","protocoloptions":{"key":"a","key_base64":"YQ=="}}""", "/protocoloptions/key_base64")]
    [Arguments("""{"protocol":"NATS","protocoloptions":{"subject":"{+topic}"}}""", "/protocoloptions/subject")]
    [Arguments("""{"protocol":"NATS","protocoloptions":{"subject":"{topic.part}"}}""", "/protocoloptions/subject")]
    [Arguments("""{"envelope":"CloudEvents/1.0","envelopemetadata":{"source":{"type":"uritemplate","value":"{topic*}"}}}""", "/envelopemetadata/source/value")]
    public Task MessageProtocolAndTemplateRulesReturnExactPaths(string metadata, string path) => Rejects(metadata, path);

    [Test]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"method":"MY-METHOD","headers":[{"name":"X-{name}","value":"{value}"}]}}""")]
    [Arguments("""{"protocol":"KAFKA","protocoloptions":{"key":"device-{id}"}}""")]
    [Arguments("""{"protocol":"KAFKA","protocoloptions":{"key_base64":"YQ=="}}""")]
    [Arguments("""{"protocol":"NATS","protocoloptions":{"subject":"orders.{tenant}.created"}}""")]
    [Arguments("""{"envelope":"CloudEvents/1.0","envelopemetadata":{"source":{"type":"uritemplate","value":"/systems/{id}"}}}""")]
    public async Task MessageProtocolAndTemplateRulesAcceptPermittedConstraints(string metadata)
    {
        var input = RegistryJson.Parse(metadata);
        await Assert.That(() => RegistryDomainRules.ValidateMessageMetadata(input.RootElement)).ThrowsNothing();
    }

    [Test]
    [Arguments("""{"datacontenttype":"not a media type"}""", "/datacontenttype")]
    [Arguments("""{"datacontenttype":"text/plain; missing"}""", "/datacontenttype")]
    [Arguments("""{"datacontenttype":"application/json","protocol":"HTTP","protocoloptions":{"headers":[{"name":"content-type","value":"application/xml"}]}}""", "/protocoloptions/headers/0/value")]
    [Arguments("""{"datacontenttype":"application/json","protocol":"AMQP/1.0","protocoloptions":{"properties":{"content-type":{"value":"application/xml"}}}}""", "/protocoloptions/properties/content-type/value")]
    [Arguments("""{"datacontenttype":"application/json","protocol":"MQTT/5.0","protocoloptions":{"content_type":"application/xml"}}""", "/protocoloptions/content_type")]
    [Arguments("""{"datacontenttype":"application/json","protocol":"NATS","protocoloptions":{"headers":[{"name":"Content-Type","value":"application/xml"}]}}""", "/protocoloptions/headers/0/value")]
    [Arguments("""{"datacontenttype":"application/json","protocol":"KAFKA","protocoloptions":{"headers":{"content-type":{"name":"content-type","value":"application/xml"}}}}""", "/protocoloptions/headers/content-type/value")]
    public Task MessageMediaTypeFailuresIdentifyTheirDeclaration(string metadata, string path) => Rejects(metadata, path);

    [Test]
    public async Task EndpointMetadataSeparatesEnvelopeConstraintsFromProtocolExecution()
    {
        var invalid = RegistryJson.Parse("""
            {"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"binary","format":"application/json"}}
            """);
        var failure = await Assert.That(() => RegistryDomainRules.ValidateEndpointMetadata(invalid.RootElement))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a domain diagnostic.");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/envelopeoptions/format");
        var valid = RegistryJson.Parse("""
            {"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"structured","format":"application/json"},
             "protocol":"MQTT/5.0","protocoloptions":{"topic":"a/#","topicfilter":"a/+"}}
            """);
        await Assert.That(() => RegistryDomainRules.ValidateEndpointMetadata(valid.RootElement)).ThrowsNothing();
    }

    [Test]
    [Arguments("""{"envelope":""}""", "/envelope")]
    [Arguments("""{"protocol":""}""", "/protocol")]
    public async Task MessageGroupSelectorsHaveDirectDomainDiagnostics(string metadata, string path)
    {
        var input = RegistryJson.Parse(metadata);
        var failure = await Assert.That(() => RegistryDomainRules.ValidateMessageGroupMetadata(input.RootElement))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a domain diagnostic.");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(path);
    }

    [Test]
    public async Task MessageSchemaReferenceUsesExplicitHostIdentityWithoutAcquiringTargets()
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.CloudEvents);
        var calls = 0;
        string Self(RegistryPath path, RegistryResourceDefinition schema)
        {
            calls++;
            if (path.EscapedPath != "/schemagroups/g/schemas/s" || !schema.HasDocument)
            {
                throw new InvalidOperationException("Unexpected schema projection.");
            }
            return "https://registry.example/catalog/schemagroups/g/schemas/s$details";
        }

        var wrongType = RegistryJson.Parse("""{"dataschemaxid":"/messagegroups/g/messages/m"}""");
        var kindFailure = await Assert.That(() => RegistryDomainRules.ValidateMessageSchemaReference(wrongType.RootElement, model, Self))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a schema kind diagnostic.");
        await Assert.That(kindFailure.Diagnostic.Path).IsEqualTo("/dataschemaxid");
        await Assert.That(calls).IsEqualTo(0);

        var dangling = RegistryJson.Parse("""{"dataschemaxid":"/schemagroups/g/schemas/s"}""");
        await Assert.That(() => RegistryDomainRules.ValidateMessageSchemaReference(dangling.RootElement, model, Self)).ThrowsNothing();
        await Assert.That(calls).IsEqualTo(0);

        var mismatched = RegistryJson.Parse("""{"dataschemaxid":"/schemagroups/g/schemas/s","dataschemauri":"https://other.example/s"}""");
        var identityFailure = await Assert.That(() => RegistryDomainRules.ValidateMessageSchemaReference(mismatched.RootElement, model, Self))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a schema identity diagnostic.");
        await Assert.That(identityFailure.Diagnostic.Path).IsEqualTo("/dataschemauri");
        await Assert.That(calls).IsEqualTo(1);

        var valid = RegistryJson.Parse("""{"dataschemaxid":"/schemagroups/g/schemas/s","dataschemauri":"https://registry.example/catalog/schemagroups/g/schemas/s$details"}""");
        await Assert.That(() => RegistryDomainRules.ValidateMessageSchemaReference(valid.RootElement, model, Self)).ThrowsNothing();
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task MessageSchemaReferenceAllowsAFormatSelectorOnlyAfterExactDocumentIdentityMatches()
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.CloudEvents);
        const string self = "https://registry.example/catalog/schemagroups/g/schemas/s$details";
        var selectedObject = RegistryJson.Parse("""
            {"dataschemaxid":"/schemagroups/g/schemas/s",
             "dataschemauri":"https://registry.example/catalog/schemagroups/g/schemas/s$details#Metrics"}
            """);

        await Assert.That(() => RegistryDomainRules.ValidateMessageSchemaReference(
            selectedObject.RootElement, model, (_, _) => self)).ThrowsNothing();

        var wrongDocument = RegistryJson.Parse("""
            {"dataschemaxid":"/schemagroups/g/schemas/s",
             "dataschemauri":"https://registry.example/catalog/schemagroups/other/schemas/s$details#Metrics"}
            """);
        var failure = await Assert.That(() => RegistryDomainRules.ValidateMessageSchemaReference(
                wrongDocument.RootElement, model, (_, _) => self))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an owner-identity diagnostic.");

        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/dataschemauri");
    }

    [Test]
    [Arguments(RegistryModelKind.Message, """{"protocol":"HTTP"}""", """{"protocol":"http"}""", true)]
    [Arguments(RegistryModelKind.Message, """{"protocol":"HTTP"}""", "{}", true)]
    [Arguments(RegistryModelKind.Message, """{"protocol":"HTTP"}""", """{"protocol":"NATS"}""", false)]
    [Arguments(RegistryModelKind.Message, """{"envelope":"myspec/1.0"}""", """{"envelope":"myspec/1.0.1"}""", false)]
    [Arguments(RegistryModelKind.Endpoint, """{"envelope":"myspec"}""", """{"envelope":"MySpec/1.0"}""", true)]
    [Arguments(RegistryModelKind.Endpoint, """{"envelope":"myspec/1.0"}""", """{"envelope":"myspec/1.0.1"}""", true)]
    [Arguments(RegistryModelKind.Endpoint, """{"envelope":"myspec/1.0"}""", """{"envelope":"myspec/1.01"}""", false)]
    [Arguments(RegistryModelKind.Endpoint, """{"envelope":"myspec/1.0"}""", """{"envelope":"myspec"}""", false)]
    [Arguments(RegistryModelKind.Endpoint, """{"protocol":"AMQP"}""", """{"protocol":"amqp/1.0"}""", true)]
    [Arguments(RegistryModelKind.Endpoint, """{"protocol":"MQTT"}""", """{"protocol":"MQTT/5.0"}""", true)]
    public async Task DomainGroupRelationsRespectSelectorAndPrecisionBoundaries(
        RegistryModelKind kind, string groupMetadata, string messageMetadata, bool valid)
    {
        var model = BuiltInRegistryModels.Compile(kind);
        var group = model.Groups[kind == RegistryModelKind.Message ? "messagegroups" : "endpoints"];
        var resource = group.Resources["messages"];
        await Assert.That(RegistryDomainRules.HasMessageGroupContract(group, resource)).IsTrue();
        var parent = RegistryJson.Parse(groupMetadata);
        var message = RegistryJson.Parse(messageMetadata);
        Action validate = () => RegistryDomainRules.ValidateMessageGroupConstraints(message.RootElement, group, resource, parent.RootElement);
        if (valid)
        {
            await Assert.That(validate).ThrowsNothing();
        }
        else
        {
            var failure = await Assert.That(validate).Throws<RegistryException>()
                ?? throw new InvalidOperationException("Expected a group contract diagnostic.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_attribute");
            await Assert.That(failure.Diagnostic.Path).IsEqualTo(parent.RootElement.TryGetProperty("protocol", out _) ? "/protocol" : "/envelope");
        }
    }

    [Test]
    public async Task OrdinaryGroupAndResourceNamesDoNotOptIntoDomainContracts()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"messagegroups":{"singular":"messagegroup","resources":{"messages":{"singular":"message","hasdocument":false}}}}}
            """));
        var group = model.Groups["messagegroups"];
        var resource = group.Resources["messages"];
        await Assert.That(RegistryDomainRules.HasMessageGroupContract(group, resource)).IsFalse();
        await Assert.That(() => RegistryDomainRules.ValidateMessageGroupConstraints(
            RegistryJson.Parse("""{"protocol":"NATS"}""").RootElement, group, resource,
            RegistryJson.Parse("""{"protocol":"HTTP"}""").RootElement)).ThrowsNothing();
    }

    [Test]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"headers":[{"name":"X-Json","value":"{\"a\":1}"}]}}""")]
    [Arguments("""
        {"datacontenttype":"application/example; first=one; second=\"two three\"",
         "protocol":"HTTP","protocoloptions":{"headers":[{"name":"Content-Type","value":"APPLICATION/EXAMPLE; SECOND=\"two three\"; FIRST=one"}]}}
        """)]
    [Arguments("""
        {"envelope":"CloudEvents/1.0","envelopemetadata":{"dataschema":{"value":"https://schemas.example/{name}"}},
         "dataschemaformat":"JSONSchema/draft-07","dataschemauri":"https://schemas.example/a.json"}
        """)]
    public async Task ContextFreeRulesPreserveLiteralTextAndUnresolvedTemplates(string metadata)
    {
        var input = RegistryJson.Parse(metadata);
        await Assert.That(() => RegistryDomainRules.ValidateMessageMetadata(input.RootElement)).ThrowsNothing();
    }

    [Test]
    public async Task DomainValidationCancellationPrecedesMetadataAndHostProjection()
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.CloudEvents);
        var group = model.Groups["messagegroups"];
        var resource = group.Resources["messages"];
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;
        var input = RegistryJson.Parse("{}");
        var calls = 0;
        string Self(RegistryPath _, RegistryResourceDefinition __)
        {
            calls++;
            return "https://registry.example/schema";
        }
        Action[] actions =
        [
            () => RegistryDomainRules.ValidateMessageMetadata(input.RootElement, token),
            () => RegistryDomainRules.ValidateMessageGroupMetadata(input.RootElement, token),
            () => RegistryDomainRules.ValidateEndpointMetadata(input.RootElement, token),
            () => RegistryDomainRules.ValidateMessageSchemaReference(input.RootElement, model, Self, token),
            () => RegistryDomainRules.ValidateMessageGroupConstraints(input.RootElement, group, resource, input.RootElement, token)
        ];
        foreach (var action in actions)
        {
            await Assert.That(action).Throws<OperationCanceledException>();
        }
        await Assert.That(calls).IsEqualTo(0);
    }

    private static async Task Rejects(string metadata, string path)
    {
        var value = RegistryJson.Parse(metadata);
        var failure = await Assert.That(() => RegistryDomainRules.ValidateMessageMetadata(value.RootElement))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a domain diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(path);
    }
}
