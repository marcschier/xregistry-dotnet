// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class MessageEndpointRuntimeTests
{
    [Test]
    public async Task MessageConditionalOptionsAlreadyHaveObjectShape()
    {
        var engine = CreateDomain(RegistryModelKind.Message);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            """{"protocol":"HTTP","protocoloptions":[]}"""), "invalid_attribute");
        var unchanged = (await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement;
        await Assert.That(unchanged.GetProperty("messagegroupscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    [Arguments("HTTP")]
    [Arguments("AMQP/1.0")]
    [Arguments("MQTT/3.1.1")]
    [Arguments("MQTT/5.0")]
    [Arguments("KAFKA")]
    [Arguments("NATS")]
    public async Task MessageProtocolSelectionRequiresOptionsBeforePublishing(string protocol)
    {
        var engine = CreateDomain(RegistryModelKind.Message);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            $$$"""{"protocol":"{{{protocol}}}"}"""), "invalid_attribute");
        var unchanged = (await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement;
        await Assert.That(unchanged.GetProperty("messagegroupscount").GetInt32()).IsEqualTo(0);
        await Assert.That(unchanged.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);

        var created = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            $$$"""{"protocol":"{{{protocol}}}","protocoloptions":{}}""");
        await Assert.That(created.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(created.Metadata!.RootElement.GetProperty("protocol").GetString()).IsEqualTo(protocol);
        await Assert.That(created.Metadata.RootElement.GetProperty("protocoloptions").GetRawText()).IsEqualTo("{}");
    }

    [Test]
    public async Task MessageEnvelopeSelectionRequiresMetadataBeforePublishing()
    {
        var engine = CreateDomain(RegistryModelKind.Message);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            """{"envelope":"CloudEvents/1.0"}"""), "invalid_attribute");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        var valid = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            """{"envelope":"CloudEvents/1.0","envelopemetadata":{}}""");
        await Assert.That(valid.Metadata!.RootElement.GetProperty("envelopemetadata").GetRawText()).IsEqualTo("{}");

        var custom = Create("""
            {"groups":{"messagegroups":{"singular":"messagegroup","resources":{"messages":{
              "singular":"message","hasdocument":false,"attributes":{"envelope":{"type":"string"},"protocol":{"type":"string"}}
            }}}}}
            """);
        var ordinary = await Send(custom, RegistryAction.Replace, "/messagegroups/g/messages/m",
            """{"envelope":"CloudEvents/1.0","protocol":"HTTP"}""");
        await Assert.That(ordinary.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(ordinary.Metadata!.RootElement.TryGetProperty("envelopemetadata", out _)).IsFalse();
        await Assert.That(ordinary.Metadata.RootElement.TryGetProperty("protocoloptions", out _)).IsFalse();
    }

    [Test]
    [Arguments("""{"dataschema":{"type":"object"}}""", """{"dataschemaformat":"JSONSchema/draft-07","dataschema":{"type":"object"}}""")]
    [Arguments("""{"dataschemauri":"https://schemas.example/a.json"}""", """{"dataschemaformat":"JSONSchema/draft-07","dataschemauri":"https://schemas.example/a.json"}""")]
    [Arguments("""{"dataschemaformat":"JSONSchema/draft-07","dataschema":{},"dataschemauri":"https://schemas.example/a.json"}""",
        """{"dataschemaformat":"JSONSchema/draft-07","dataschemauri":"https://schemas.example/a.json"}""")]
    public async Task MessageSchemaDeclarationsAreValidatedAtomically(string invalid, string valid)
    {
        var engine = CreateDomain(RegistryModelKind.Message);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", invalid), "invalid_attribute");
        await AssertEmptyMessageRegistry(engine);
        var created = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", valid);
        await Assert.That(created.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(created.Metadata!.RootElement.GetProperty("dataschemaformat").GetString()).IsEqualTo("JSONSchema/draft-07");

        var before = (await Send(engine, RegistryAction.Read, "/messagegroups/g/messages/m")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/messagegroups/g/messages/m",
            """{"dataschemaformat":null}"""), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/messagegroups/g/messages/m")).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
    }

    [Test]
    public async Task EnvelopeSchemaDeclarationsMustAgreeWithMessageSchemaUri()
    {
        var engine = CreateDomain(RegistryModelKind.Message);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", """
            {"envelope":"CloudEvents/1.0","envelopemetadata":{"dataschema":{"value":"https://schemas.example/b.json"}},
             "dataschemaformat":"JSONSchema/draft-07","dataschemauri":"https://schemas.example/a.json"}
            """), "invalid_attribute");
        await AssertEmptyMessageRegistry(engine);
        var accepted = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", """
            {"envelope":"CloudEvents/1.0","envelopemetadata":{"dataschema":{"value":"https://schemas.example/a.json"}},
             "dataschemaformat":"JSONSchema/draft-07","dataschemauri":"https://schemas.example/a.json"}
            """);
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("envelopemetadata").GetProperty("dataschema")
            .GetProperty("value").GetString()).IsEqualTo("https://schemas.example/a.json");
    }

    [Test]
    [Arguments(RegistryModelKind.Message, "/messagegroups/g", """{"envelope":""}""", """{"envelope":"CloudEvents/1.0"}""", "envelope", "CloudEvents/1.0")]
    [Arguments(RegistryModelKind.Message, "/messagegroups/g", """{"protocol":""}""", """{"protocol":"MQTT/5.0"}""", "protocol", "MQTT/5.0")]
    [Arguments(RegistryModelKind.Message, "/messagegroups/g/messages/m", """{"dataschemaformat":""}""", """{"dataschemaformat":"JSONSchema/draft-07"}""", "dataschemaformat", "JSONSchema/draft-07")]
    [Arguments(RegistryModelKind.Message, "/messagegroups/g/messages/m",
        """{"envelope":"CloudEvents/1.0","envelopemetadata":{"subject":{"description":""}}}""",
        """{"envelope":"CloudEvents/1.0","envelopemetadata":{"subject":{"description":" "}}}""", "envelopemetadata/subject/description", " ")]
    [Arguments(RegistryModelKind.Endpoint, "/endpoints/e", """{"usage":["producer"],"channel":""}""",
        """{"usage":["producer"],"channel":" "}""", "channel", " ")]
    [Arguments(RegistryModelKind.Endpoint, "/endpoints/e", """{"usage":["producer"],"envelope":""}""",
        """{"usage":["producer"],"envelope":"CloudEvents"}""", "envelope", "CloudEvents")]
    [Arguments(RegistryModelKind.Endpoint, "/endpoints/e", """{"usage":["producer"],"protocol":""}""",
        """{"usage":["producer"],"protocol":"HTTP"}""", "protocol", "HTTP")]
    [Arguments(RegistryModelKind.Endpoint, "/endpoints/e",
        """{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[{"type":""}]}}""",
        """{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[{"type":"APIKey"}]}}""", "protocoloptions/authorization/0/type", "APIKey")]
    [Arguments(RegistryModelKind.Endpoint, "/endpoints/e",
        """{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[{"type":"SASL","mechanism":""}]}}""",
        """{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[{"type":"SASL","mechanism":"PLAIN"}]}}""", "protocoloptions/authorization/0/mechanism", "PLAIN")]
    [Arguments(RegistryModelKind.Endpoint, "/endpoints/e",
        """{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[{"resourceuri":""}]}}""",
        """{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[{"resourceuri":"scope"}]}}""", "protocoloptions/authorization/0/resourceuri", "scope")]
    [Arguments(RegistryModelKind.Endpoint, "/endpoints/e",
        """{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[{"authorityuri":""}]}}""",
        """{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[{"authorityuri":"auth"}]}}""", "protocoloptions/authorization/0/authorityuri", "auth")]
    public async Task DomainStringConstraintsRejectOnlyOwnedFields(
        RegistryModelKind kind, string path, string invalid, string valid, string field, string expected)
    {
        var engine = CreateDomain(kind);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, path, invalid), "invalid_attribute");
        var empty = (await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement;
        await Assert.That(empty.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        var accepted = await Send(engine, RegistryAction.Replace, path, valid);
        var value = accepted.Metadata!.RootElement;
        foreach (var component in field.Split('/'))
        {
            value = value.ValueKind == System.Text.Json.JsonValueKind.Array
                ? value[int.Parse(component, System.Globalization.CultureInfo.InvariantCulture)] : value.GetProperty(component);
        }
        await Assert.That(value.GetString()).IsEqualTo(expected);
    }

    [Test]
    public async Task DomainStringsLeaveOrdinaryCustomModelsAndInlineSchemasUntouched()
    {
        var custom = Create("""
            {"groups":{"messagegroups":{"singular":"messagegroup","attributes":{"envelope":{"type":"string"},"protocol":{"type":"string"}},
              "resources":{"messages":{"singular":"message","hasdocument":false,
                "attributes":{"dataschemaformat":{"type":"string"},"dataschema":{"type":"any"}}}}},
              "endpoints":{"singular":"endpoint","attributes":{"channel":{"type":"string"},"envelope":{"type":"string"},"protocol":{"type":"string"}}}}}
            """);
        var group = await Send(custom, RegistryAction.Replace, "/messagegroups/g", """{"envelope":"","protocol":""}""");
        await Assert.That(group.Metadata!.RootElement.GetProperty("envelope").GetString()).IsEqualTo("");
        var endpoint = await Send(custom, RegistryAction.Replace, "/endpoints/e", """{"channel":"","envelope":"","protocol":""}""");
        await Assert.That(endpoint.Metadata!.RootElement.GetProperty("channel").GetString()).IsEqualTo("");

        var engine = CreateDomain(RegistryModelKind.Message);
        var message = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", """
            {"description":"","dataschemaformat":"JSONSchema/draft-07",
             "dataschema":{"description":"","properties":{"protocol":{"const":""}}}}
            """);
        await Assert.That(message.Metadata!.RootElement.GetProperty("description").GetString()).IsEqualTo("");
        await Assert.That(message.Metadata.RootElement.GetProperty("dataschema").GetRawText())
            .IsEqualTo("""{"description":"","properties":{"protocol":{"const":""}}}""");
    }

    [Test]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"method":"two words"}}""",
        """{"protocol":"HTTP","protocoloptions":{"method":"MY-METHOD"}}""")]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"headers":[{"name":"bad name","value":"x"}]}}""",
        """{"protocol":"HTTP","protocoloptions":{"headers":[{"name":"X-Name","value":"x"}]}}""")]
    [Arguments("""{"protocol":"KAFKA","protocoloptions":{"key":"a","key_base64":"YQ=="}}""",
        """{"protocol":"KAFKA","protocoloptions":{"key_base64":"YQ=="}}""")]
    public async Task MessageProtocolConstraintsHaveNearestValidCounterparts(string invalid, string valid)
    {
        var engine = CreateDomain(RegistryModelKind.Message);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", invalid), "invalid_attribute");
        await AssertEmptyMessageRegistry(engine);
        var accepted = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", valid);
        await Assert.That(accepted.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("protocol").GetString())
            .IsEqualTo(RegistryJson.Parse(valid).RootElement.GetProperty("protocol").GetString());
    }

    [Test]
    [Arguments("{+topic}")]
    [Arguments("{topic.part}")]
    [Arguments("{topic*}")]
    [Arguments("{topic:2}")]
    public async Task MessageTemplatesRequireLevelOneWithoutRestrictingCore(string invalid)
    {
        var metadata = $$$"""{"protocol":"NATS","protocoloptions":{"subject":"{{{invalid}}}"}}""";
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Message);
        await Assert.That(() => RegistryMetadataValidator.Validate(RegistryJson.Parse(metadata),
            model.Groups["messagegroups"].Resources["messages"].Attributes,
            new() { Model = model, Mode = RegistryMetadataMode.ClientInput })).ThrowsNothing();

        var engine = CreateDomain(RegistryModelKind.Message);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", metadata), "invalid_attribute");
        await AssertEmptyMessageRegistry(engine);
        var accepted = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            """{"protocol":"NATS","protocoloptions":{"subject":"orders.{tenant}.created"}}""");
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("protocoloptions").GetProperty("subject").GetString())
            .IsEqualTo("orders.{tenant}.created");
    }

    [Test]
    [Arguments("""{"datacontenttype":"not a media type"}""")]
    [Arguments("""{"datacontenttype":"application/json","protocol":"HTTP","protocoloptions":{"headers":[{"name":"Content-Type","value":"application/xml"}]}}""")]
    [Arguments("""{"datacontenttype":"application/example; profile=A","protocol":"HTTP","protocoloptions":{"headers":[{"name":"Content-Type","value":"APPLICATION/EXAMPLE; PROFILE=a"}]}}""")]
    [Arguments("""{"datacontenttype":"application/json","envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"binary"},"envelopemetadata":{"datacontenttype":{"value":"application/xml"}}}""")]
    public async Task MessageContentTypesRespectSyntaxAndDuplicateSemantics(string invalid)
    {
        var engine = CreateDomain(RegistryModelKind.Message);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", invalid), "invalid_attribute");
        await AssertEmptyMessageRegistry(engine);
        var valid = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", """
            {"datacontenttype":"text/plain; charset=utf-8","envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"binary"},
             "envelopemetadata":{"datacontenttype":{"value":"TEXT/Plain; CharSet=\"utf-8\""}},
             "protocol":"HTTP","protocoloptions":{"headers":[{"name":"Content-Type","value":"Text/Plain; CHARSET=utf-8"}]}}
            """);
        await Assert.That(valid.Metadata!.RootElement.GetProperty("datacontenttype").GetString()).IsEqualTo("text/plain; charset=utf-8");
    }

    [Test]
    public async Task StructuredEnvelopeAndPayloadTypesRemainDistinct()
    {
        var engine = CreateDomain(RegistryModelKind.Message);
        var value = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", """
            {"datacontenttype":"application/cloudevents+json","envelope":"CloudEvents/1.0",
             "envelopeoptions":{"mode":"structured","format":"application/cloudevents+json"},
             "envelopemetadata":{"datacontenttype":{"value":"application/json"}},
             "protocol":"HTTP","protocoloptions":{"headers":[{"name":"Content-Type","value":"application/cloudevents+json"}]}}
            """);
        await Assert.That(value.Metadata!.RootElement.GetProperty("datacontenttype").GetString()).IsEqualTo("application/cloudevents+json");
        await Assert.That(value.Metadata.RootElement.GetProperty("envelopemetadata").GetProperty("datacontenttype").GetProperty("value").GetString())
            .IsEqualTo("application/json");
    }

    [Test]
    public async Task EndpointEnvelopeOptionsRespectBinaryFormatConstraint()
    {
        var engine = CreateDomain(RegistryModelKind.Endpoint);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/endpoints/e", """
            {"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"binary","format":"application/json"}}
            """), "invalid_attribute");
        var empty = (await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement;
        await Assert.That(empty.GetProperty("endpointscount").GetInt32()).IsEqualTo(0);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        await Send(engine, RegistryAction.Replace, "/endpoints/e",
            """{"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"binary"}}""");
        var before = (await Send(engine, RegistryAction.Read, "/endpoints/e")).Metadata!.RootElement.GetRawText();
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/endpoints/e",
            """{"envelopeoptions":{"mode":"binary","format":"application/json"}}"""), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/endpoints/e")).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
        var valid = await Send(engine, RegistryAction.Patch, "/endpoints/e",
            """{"envelopeoptions":{"mode":"structured","format":"application/json"}}""");
        await Assert.That(valid.Metadata!.RootElement.GetProperty("envelopeoptions").GetProperty("format").GetString()).IsEqualTo("application/json");
    }

    [Test]
    [Arguments("""{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"endpoints":[{"uri":"ftp://example.test"}]}}""")]
    [Arguments("""{"usage":["consumer"],"protocol":"AMQP/1.0","protocoloptions":{"endpoints":[{"uri":"https://example.test"}]}}""")]
    [Arguments("""{"usage":["producer"],"protocol":"MQTT/3.1.1","protocoloptions":{"topic":"a/#","topicfilter":"a/+"}}""")]
    [Arguments("""{"usage":["producer"],"protocol":"MQTT/5.0","protocoloptions":{"topic":"a/#","topicfilter":"a/+","sharedsubscriptiongroup":"$share/a"}}""")]
    [Arguments("""{"usage":["consumer"],"protocol":"KAFKA","protocoloptions":{"autooffsetreset":"earliest"}}""")]
    [Arguments("""{"usage":["producer"],"protocol":"NATS","protocoloptions":{"subject":"a.*","subjectfilter":"a.>","queuegroup":"q"}}""")]
    public async Task EndpointApplicationProtocolSemanticsRemainClientOwned(string metadata)
    {
        var engine = CreateDomain(RegistryModelKind.Endpoint);
        var accepted = await Send(engine, RegistryAction.Replace, "/endpoints/e", metadata);
        await Assert.That(accepted.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("protocol").GetString())
            .IsEqualTo(RegistryJson.Parse(metadata).RootElement.GetProperty("protocol").GetString());
    }

    [Test]
    [Arguments("/messagegroups/g/messages/not-schema")]
    [Arguments("/schemagroups/g")]
    [Arguments("/schemagroups/g/schemas/s/versions/1")]
    [Arguments("/schemagroups/g/schemas/s/meta")]
    public async Task MessageSchemaReferencesRequireSchemaResources(string xid)
    {
        var engine = CreateDomain(RegistryModelKind.CloudEvents);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            $$$"""{"dataschemaxid":"{{{xid}}}"}"""), "invalid_attribute");
        await AssertEmptyMessageRegistry(engine);
        var accepted = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            """{"dataschemaxid":"/schemagroups/not-created/schemas/future"}""");
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("dataschemaxid").GetString())
            .IsEqualTo("/schemagroups/not-created/schemas/future");
    }

    [Test]
    public async Task MessageSchemaReferencesPreserveLocalResourceIdentity()
    {
        var engine = CreateDomain(RegistryModelKind.CloudEvents);
        var schema = await Send(engine, RegistryAction.Replace, "/schemagroups/g/schemas/s$details",
            """{"format":"JSONSchema/draft-07","schema":{"type":"object"}}""");
        var self = schema.Metadata!.RootElement.GetProperty("self").GetString()!;
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", """
            {"dataschemaxid":"/schemagroups/g/schemas/s","dataschemaformat":"JSONSchema/draft-07",
             "dataschemauri":"https://unrelated.example/s"}
            """), "invalid_attribute");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        var accepted = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            $$$"""{"dataschemaxid":"/schemagroups/g/schemas/s","dataschemaformat":"JSONSchema/draft-07","dataschemauri":"{{{self}}}"}""");
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("dataschemauri").GetString()).IsEqualTo(self);
        var before = (await Send(engine, RegistryAction.Read, "/messagegroups/g/messages/m")).Metadata!.RootElement.GetRawText();
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/messagegroups/g/messages/m",
            """{"dataschemauri":"https://unrelated.example/other"}"""), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/messagegroups/g/messages/m")).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
    }

    [Test]
    public async Task DomainValidationRunsAfterReadonlyInputHasBeenIgnored()
    {
        var engine = Create("""
            {"groups":{"messagegroups":{"singular":"messagegroup","resources":{"messages":{
              "singular":"message","hasdocument":false,
              "modelcompatiblewith":"https://xregistry.io/xreg/domains/message/specs/model.json",
              "attributes":{"dataschemaformat":{"type":"string","readonly":true,"required":true,"default":"JSONSchema/draft-07"}}
            }}}}}
            """);
        var created = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", """{"dataschemaformat":""}""");
        await Assert.That(created.Metadata!.RootElement.GetProperty("dataschemaformat").GetString()).IsEqualTo("JSONSchema/draft-07");
        var patched = await Send(engine, RegistryAction.Patch, "/messagegroups/g/messages/m", """{"dataschemaformat":null}""");
        await Assert.That(patched.Metadata!.RootElement.GetProperty("dataschemaformat").GetString()).IsEqualTo("JSONSchema/draft-07");
    }

    private static async Task AssertEmptyMessageRegistry(RegistryEngine engine)
    {
        var root = (await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement;
        await Assert.That(root.GetProperty("messagegroupscount").GetInt32()).IsEqualTo(0);
        await Assert.That(root.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
    }

    internal static RegistryEngine CreateDomain(RegistryModelKind kind, IRegistryPersistence? persistence = null,
        IRegistryAuthorizationPolicy? policy = null) =>
        new(new RegistryEngineOptions
        {
            RegistryId = "message-endpoint-runtime",
            PublicRoot = new Uri("https://registry.example/catalog"),
            Model = BuiltInRegistryModels.Compile(kind),
            AllowAnonymousReads = true
        }, persistence ?? new InMemoryRegistryPersistence(), policy ?? new PermitPolicy());
}
