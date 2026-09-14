using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class MessagePropertyDeclarationTests
{
    [Test]
    [Arguments("boolean", "true")]
    [Arguments("integer", "42")]
    [Arguments("number", "1.25")]
    [Arguments("any", "null")]
    [Arguments("any", """{"nested":null,"numbers":[1,2]}""")]
    [Arguments("binary", "\"AAH/\"")]
    [Arguments("timestamp", "\"0000-01-01T00:00:00Z\"")]
    [Arguments("uri", "\"urn:device:42\"")]
    [Arguments("duration", "\"P999999999999999999999D\"")]
    [Arguments("duration", "\"-P1Y2M3DT4H5M6.75S\"")]
    [Arguments("symbol", "\"Alpha_42\"")]
    [Arguments("stringified integer", "\"-12345678901234567890\"")]
    [Arguments("integer", "2147483647")]
    [Arguments("integer", "-2147483648")]
    public async Task TypedCloudEventsPropertyValuesRetainTheirActualJsonTypesAndNullPresence(string type, string value)
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        var input = Definition("CloudEvents/1.0", null, "count", type, JsonNode.Parse(value));
        var result = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", input.ToJsonString());
        var declaration = result.Metadata!.RootElement.GetProperty("envelopemetadata").GetProperty("count");

        await Assert.That(declaration.GetProperty("type").GetString()).IsEqualTo(type);
        await Assert.That(declaration.TryGetProperty("value", out var actual)).IsTrue();
        using var expected = JsonDocument.Parse(value);
        await Assert.That(actual.ValueKind).IsEqualTo(expected.RootElement.ValueKind);
        await Assert.That(actual.GetRawText()).IsEqualTo(value);
    }

    [Test]
    [Arguments("application-properties")]
    [Arguments("message-annotations")]
    [Arguments("delivery-annotations")]
    [Arguments("footer")]
    public async Task AmqpMapsPreserveSymbolNamesAndTypedValues(string section)
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        var input = Definition(null, section, "Upper_Case", "boolean", JsonValue.Create(false));
        var result = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", input.ToJsonString());
        var declaration = result.Metadata!.RootElement.GetProperty("protocoloptions").GetProperty(section).GetProperty("Upper_Case");

        await Assert.That(declaration.GetProperty("value").GetBoolean()).IsFalse();
        await Assert.That(declaration.GetProperty("type").GetString()).IsEqualTo("boolean");
    }

    [Test]
    public async Task PropertySpecificationReferencesAndSubjectUriRefinementAreValidDeclarations()
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        var result = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", """
            {"envelope":"CloudEvents/1.0","envelopemetadata":{
              "subject":{"type":"uri","value":"urn:device:42","specurl":"../subject-spec"},
              "optional":{"value":""}
            }}
            """);
        var metadata = result.Metadata!.RootElement.GetProperty("envelopemetadata");
        await Assert.That(metadata.GetProperty("subject").GetProperty("specurl").GetString()).IsEqualTo("../subject-spec");
        await Assert.That(metadata.GetProperty("subject").GetProperty("value").GetString()).IsEqualTo("urn:device:42");
        await Assert.That(metadata.GetProperty("optional").GetProperty("value").GetString()).IsEqualTo("");
    }

    [Test]
    [Arguments("boolean", "\"true\"")]
    [Arguments("integer", "1.25")]
    [Arguments("number", "\"1.25\"")]
    [Arguments("binary", "\"not-base64\"")]
    [Arguments("symbol", "\"has space\"")]
    [Arguments("timestamp", "\"2026-02-30T00:00:00Z\"")]
    [Arguments("uri", "\"bad uri\"")]
    [Arguments("unknown-type", "\"text\"")]
    [Arguments("boolean", "null")]
    [Arguments("number", "1e400")]
    [Arguments("duration", "\"P\"")]
    [Arguments("duration", "\"P1DT\"")]
    [Arguments("duration", "\"P1.5M\"")]
    [Arguments("duration", "\"PT1M2H\"")]
    [Arguments("stringified integer", "\"1.5\"")]
    [Arguments("integer", "2147483648")]
    [Arguments("integer", "-2147483649")]
    [Arguments("uri", "\"relative/path\"")]
    [Arguments("string", "\"bad\\nvalue\"")]
    [Arguments("string", "\"\\uFDD0\"")]
    public async Task MismatchedTypedConstraintsRejectWithoutPublication(string type, string value)
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        var input = Definition("CloudEvents/1.0", null, "count", type, JsonNode.Parse(value));
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", input.ToJsonString()), "invalid_attribute");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("messagegroupscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task DeclarationDefaultsAreDomainDefaultsWithoutInventingAbsentAttributes()
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        var result = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", """
            {"envelope":"CloudEvents/1.0","envelopemetadata":{
              "specversion":{},"id":{},"source":{},"type":{},"subject":{},"time":{},"extension":{}
            }}
            """);
        var declarations = result.Metadata!.RootElement.GetProperty("envelopemetadata");
        await Assert.That(declarations.GetProperty("specversion").GetProperty("value").GetString()).IsEqualTo("1.0");
        await Assert.That(declarations.GetProperty("subject").GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(declarations.GetProperty("source").GetProperty("type").GetString()).IsEqualTo("uritemplate");
        await Assert.That(declarations.GetProperty("time").GetProperty("value").GetString()).IsEqualTo("0000-01-01T00:00:00Z");
        await Assert.That(declarations.GetProperty("id").GetProperty("required").GetBoolean()).IsTrue();
        await Assert.That(declarations.GetProperty("extension").GetProperty("required").GetBoolean()).IsFalse();
        await Assert.That(declarations.GetProperty("extension").TryGetProperty("value", out _)).IsFalse();
        await Assert.That(declarations.TryGetProperty("dataschema", out _)).IsFalse();
    }

    [Test]
    [Arguments("""{"id":{"required":false}}""")]
    [Arguments("""{"specversion":{"value":"2.0"}}""")]
    [Arguments("""{"time":{"type":"string","value":"text"}}""")]
    [Arguments("""{"subject":{"type":"integer","value":42}}""")]
    [Arguments("""{"BadName":{"value":"text"}}""")]
    [Arguments("""{"valid":{"required":"true"}}""")]
    [Arguments("""{"valid":{"specurl":"bad uri"}}""")]
    [Arguments("""{"valid":42}""")]
    [Arguments("""{"id":{"value":""}}""")]
    [Arguments("""{"source":{"value":""}}""")]
    [Arguments("""{"subject":{"value":""}}""")]
    [Arguments("""{"dataschema":{"value":""}}""")]
    [Arguments("""{"valid":{"type":null}}""")]
    [Arguments("""{"valid":{"description":null}}""")]
    public async Task OpaqueCoreBoundariesDoNotWaiveDeclarationAndEnvelopeRules(string declarations)
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            """{"envelope":"CloudEvents/1.0","envelopemetadata":""" + declarations + "}"), "invalid_attribute");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidTypedPatchPreservesTheStoredLiteralNullAndOutbox()
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            Definition("CloudEvents/1.0", null, "nullable", "any", null).ToJsonString());
        var before = (await Send(engine, RegistryAction.Read, "/messagegroups/g/messages/m")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/messagegroups/g/messages/m", """
            {"envelopemetadata":{"nullable":{"type":"boolean","value":null}}}
            """), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/messagegroups/g/messages/m")).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
    }

    [Test]
    [Arguments("AMQP/1.0", """{"header":{"priority":256}}""", """{"header":{"priority":255}}""")]
    [Arguments("AMQP/1.0", """{"header":{"ttl":-1}}""", """{"header":{"ttl":4294967295}}""")]
    [Arguments("AMQP/1.0", """{"header":{"delivery-count":4294967296}}""", """{"header":{"delivery-count":0}}""")]
    [Arguments("MQTT/3.1.1", """{"qos":3}""", """{"qos":2}""")]
    [Arguments("MQTT/5.0", """{"payload_format_indicator":2}""", """{"payload_format_indicator":1}""")]
    [Arguments("MQTT/5.0", """{"message_expiry_interval":-1}""", """{"message_expiry_interval":4294967295}""")]
    public async Task NativeProtocolIntegerRangesCannotBeExpandedByGenericCoreIntegers(
        string protocol, string invalid, string valid)
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.Message);
        string Metadata(string options) => "{\"protocol\":\"" + protocol + "\",\"protocoloptions\":" + options + "}";
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", Metadata(invalid)), "invalid_attribute");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        var result = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", Metadata(valid));
        await Assert.That(result.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(result.Metadata!.RootElement.GetProperty("protocol").GetString()).IsEqualTo(protocol);
    }

    [Test]
    public async Task CompletionPreservesOpaquePayloadSchemasInsteadOfTreatingThemAsDeclarations()
    {
        var input = RegistryJson.Parse("""
            {"dataschemaformat":"JsonSchema/draft-07","dataschema":{
              "type":"object","properties":{"example":{"type":"any","value":null,"required":"schema-specific"}}
            }}
            """);
        var completed = RegistryDomainRules.CompleteMessageMetadata(input);
        await Assert.That(completed.RootElement.GetProperty("dataschema").GetRawText())
            .IsEqualTo("""{"type":"object","properties":{"example":{"type":"any","value":null,"required":"schema-specific"}}}""");
    }

    [Test]
    public async Task CompletionEnforcesTheExpandedOutputByteBudgetAtTheExactBoundary()
    {
        var input = RegistryJson.Parse("""{"envelope":"CloudEvents/1.0","envelopemetadata":{"time":{}}}""");
        const string expected = """{"envelope":"CloudEvents/1.0","envelopemetadata":{"time":{"type":"timestamp","required":false,"value":"0000-01-01T00:00:00Z"}}}""";
        var bytes = System.Text.Encoding.UTF8.GetByteCount(expected);
        await Assert.That(RegistryDomainRules.CompleteMessageMetadata(input, new() { MaxBytes = bytes }).RootElement.GetRawText())
            .IsEqualTo(expected);
        var error = await Assert.That(() => RegistryDomainRules.CompleteMessageMetadata(input, new() { MaxBytes = bytes - 1 }))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected the output limit to reject.");
        await Assert.That(error.Diagnostic.Code).IsEqualTo("byte_limit");
    }

    [Test]
    public async Task CompletionIsCancellableAndReturnsIndependentMetadata()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var input = RegistryJson.Parse("""{"envelope":"CloudEvents/1.0","envelopemetadata":{"x":{"type":"any","value":null}}}""");
        await Assert.That(() => RegistryDomainRules.CompleteMessageMetadata(input, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
        var result = RegistryDomainRules.CompleteMessageMetadata(input);
        await Assert.That(input.RootElement.GetProperty("envelopemetadata").GetProperty("x").TryGetProperty("required", out _)).IsFalse();
        await Assert.That(result.RootElement.GetProperty("envelopemetadata").GetProperty("x").GetProperty("required").GetBoolean()).IsFalse();
        await Assert.That(result.RootElement.GetProperty("envelopemetadata").GetProperty("x").GetProperty("value").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    private static JsonObject Definition(string? envelope, string? section, string name, string type, JsonNode? value)
    {
        var declaration = new JsonObject { ["type"] = type, ["value"] = value };
        return envelope is not null
            ? new JsonObject { ["envelope"] = envelope, ["envelopemetadata"] = new JsonObject { [name] = declaration } }
            : new JsonObject
            {
                ["protocol"] = "AMQP/1.0",
                ["protocoloptions"] = new JsonObject { [section!] = new JsonObject { [name] = declaration } },
            };
    }
}
