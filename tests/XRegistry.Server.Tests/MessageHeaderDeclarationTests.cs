// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class MessageHeaderDeclarationTests
{
    [Test]
    [Arguments("HTTP")]
    [Arguments("NATS")]
    [Arguments("MQTT/5.0")]
    [Arguments("KAFKA")]
    public async Task HeaderDeclarationsSupportCommonTypeRequiredAndSpecificationFields(string protocol)
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        var input = Input(protocol, """{"name":"X-Count","type":"stringified integer","value":"-42","required":true,"specurl":"../headers/count"}""");
        var result = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", input);
        var declaration = Header(result.Metadata!, protocol);
        await Assert.That(declaration.GetProperty("type").GetString()).IsEqualTo("stringified integer");
        await Assert.That(declaration.GetProperty("value").GetString()).IsEqualTo("-42");
        await Assert.That(declaration.GetProperty("required").GetBoolean()).IsTrue();
        await Assert.That(declaration.GetProperty("specurl").GetString()).IsEqualTo("../headers/count");
    }

    [Test]
    [Arguments("HTTP")]
    [Arguments("NATS")]
    [Arguments("MQTT/5.0")]
    [Arguments("KAFKA")]
    public async Task HeaderDefaultsApplyOnlyToPresentDeclarations(string protocol)
    {
        var input = RegistryJson.Parse(Input(protocol, """{"name":"X-Value","value":""}"""));
        var completed = RegistryDomainRules.CompleteMessageMetadata(input);
        var header = Header(completed, protocol);
        await Assert.That(header.GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(header.GetProperty("required").GetBoolean()).IsFalse();
        await Assert.That(header.GetProperty("value").GetString()).IsEqualTo("");
        await Assert.That(Header(input, protocol).TryGetProperty("type", out _)).IsFalse();
        var empty = RegistryDomainRules.CompleteMessageMetadata(RegistryJson.Parse("{\"protocol\":\"" + protocol + "\",\"protocoloptions\":{}}"));
        await Assert.That(empty.RootElement.GetProperty("protocoloptions").EnumerateObject().Count()).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"name":"X-A","type":null}""")]
    [Arguments("""{"name":"X-A","required":null}""")]
    [Arguments("""{"name":"X-A","description":null}""")]
    [Arguments("""{"name":"X-A","value":null}""")]
    [Arguments("""{"name":"X-A","type":"boolean","value":true}""")]
    [Arguments("""{"name":"X-A","type":"integer","value":42}""")]
    [Arguments("""{"name":"X-A","type":"stringified integer","value":"4.2"}""")]
    [Arguments("""{"name":"X-A","type":"binary","value":"AA=="}""")]
    [Arguments("""{"type":"string","value":"missing-name"}""")]
    [Arguments("""{"name":"X-A","unknown":true}""")]
    public async Task HeaderValidationRejectsInvalidShapesAndNonTextualWireRefinements(string declaration)
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", Input("HTTP", declaration)),
            "invalid_attribute");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"name":"binary","type":"binary","value":"AAH/"}""", JsonValueKind.String)]
    [Arguments("""{"name":"nullable","type":"any","value":null}""", JsonValueKind.Null)]
    public async Task KafkaByteHeaderDeclarationsRetainBase64AndExplicitNull(string declaration, JsonValueKind kind)
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        var result = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", Input("KAFKA", declaration));
        var header = Header(result.Metadata!, "KAFKA");
        await Assert.That(header.TryGetProperty("value", out var value)).IsTrue();
        await Assert.That(value.ValueKind).IsEqualTo(kind);
        if (kind == JsonValueKind.String) { await Assert.That(value.GetString()).IsEqualTo("AAH/"); }
    }

    [Test]
    public async Task HeaderMaterializationUsesTheSameDefaultsAndNeverDropsInvalidNullMembers()
    {
        var definition = new MessageDefinition(RegistryJson.Parse(Input("HTTP", """{"name":"X-A","type":"symbol","value":"COUNT_1"}""")),
            new("https://registry.example/message"));
        var result = await MessageDefinitionMaterializer.MaterializeAsync(definition);
        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(Header(result.Metadata, "HTTP").GetProperty("type").GetString()).IsEqualTo("symbol");
        await Assert.That(Header(result.Metadata, "HTTP").GetProperty("required").GetBoolean()).IsFalse();
        var invalid = new MessageDefinition(RegistryJson.Parse(Input("HTTP", """{"name":"X-A","type":null}""")), definition.Location);
        await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(invalid)).Throws<RegistryException>();
    }

    [Test]
    public async Task HeaderCompletionKeepsExactByteBoundsAndDuplicateArrayOrder()
    {
        var input = RegistryJson.Parse("""
            {"protocol":"HTTP","protocoloptions":{"headers":[{"name":"X-A","value":"one"},{"name":"X-A","value":"two"}]}}
            """);
        var expected = RegistryDomainRules.CompleteMessageMetadata(input);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(expected.RootElement.GetRawText());
        var exact = RegistryDomainRules.CompleteMessageMetadata(input, new() { MaxBytes = bytes });
        await Assert.That(exact.RootElement.GetRawText()).IsEqualTo(expected.RootElement.GetRawText());
        var headers = exact.RootElement.GetProperty("protocoloptions").GetProperty("headers");
        await Assert.That(headers.GetArrayLength()).IsEqualTo(2);
        await Assert.That(headers[0].GetProperty("value").GetString()).IsEqualTo("one");
        await Assert.That(headers[1].GetProperty("value").GetString()).IsEqualTo("two");
        var failure = await Assert.That(() => RegistryDomainRules.CompleteMessageMetadata(input, new() { MaxBytes = bytes - 1 }))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected the exact output byte limit.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("byte_limit");
    }

    [Test]
    public async Task RelativeBaseMaterializationPreservesSignificantEmptyRegistryRootSegments()
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Message);
        var root = new Uri("https://registry.example/root//");
        var definition = new MessageDefinition(RegistryJson.Parse("""{"basemessage":"/messagegroups/g/messages/base"}"""),
            new Uri(root, "messagegroups/g/messages/derived"), root, model);
        var result = await MessageDefinitionMaterializer.MaterializeAsync(definition, (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (request.TargetUri.AbsoluteUri != "https://registry.example/root//messagegroups/g/messages/base")
            {
                throw new InvalidOperationException("The selected Registry root must not lose its empty path segment.");
            }
            return ValueTask.FromResult(MessageDefinitionSourceResult.Unresolved(MessageDefinitionSourceStatus.NotFound));
        });
        await Assert.That(result.IsComplete).IsFalse();
        await Assert.That(result.UnresolvedStatus).IsEqualTo(MessageDefinitionSourceStatus.NotFound);
    }

    private static string Input(string protocol, string declaration)
    {
        JsonNode headers = protocol == "KAFKA"
            ? new JsonObject { ["entry"] = JsonNode.Parse(declaration) }
            : new JsonArray(JsonNode.Parse(declaration));
        return new JsonObject
        {
            ["protocol"] = protocol,
            ["protocoloptions"] = new JsonObject { [protocol == "MQTT/5.0" ? "user_properties" : "headers"] = headers }
        }.ToJsonString();
    }

    private static JsonElement Header(RegistryJson metadata, string protocol)
    {
        var headers = metadata.RootElement.GetProperty("protocoloptions").GetProperty(protocol == "MQTT/5.0" ? "user_properties" : "headers");
        return protocol == "KAFKA" ? headers.GetProperty("entry") : headers[0];
    }
}
