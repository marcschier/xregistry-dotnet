// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;

namespace XRegistry.Models.Tests;

public class MessageEndpointModelCorrectionTests
{
    [Test]
    public async Task BaseMessageUsesTheCanonicalSpecificationNameAndPreservesItsTarget()
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Message);
        var attributes = model.Groups["messagegroups"].Resources["messages"].Attributes;
        var result = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"basemessage":"/messagegroups/g/messages/base"}"""), attributes,
            new() { Mode = RegistryMetadataMode.ClientInput, Model = model });

        await Assert.That(result.Metadata.RootElement.GetProperty("basemessage").GetString()).IsEqualTo("/messagegroups/g/messages/base");
        await Assert.That(attributes["basemessage"].Target).IsEqualTo("/messagegroups/messages[/versions]");
        await Assert.That(attributes.ContainsKey("basemessageuri")).IsFalse();
    }

    [Test]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"status":"204"}}""", "status", JsonValueKind.String, "204")]
    [Arguments("""{"protocol":"HTTP","protocoloptions":{"query":{"foo":"bar"}}}""", "query", JsonValueKind.Object, "{\"foo\":\"bar\"}")]
    [Arguments("""{"protocol":"NATS","protocoloptions":{"subject":"orders","reply-to":"replies"}}""", "reply-to", JsonValueKind.String, "replies")]
    public async Task CompiledProtocolDeclarationsMatchTheNormativeFieldNamesAndShapes(
        string input, string field, JsonValueKind kind, string expected)
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Message);
        var result = RegistryMetadataValidator.Validate(RegistryJson.Parse(input),
            model.Groups["messagegroups"].Resources["messages"].Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput, Model = model });
        var actual = result.Metadata.RootElement.GetProperty("protocoloptions").GetProperty(field);

        await Assert.That(actual.ValueKind).IsEqualTo(kind);
        await Assert.That(kind == JsonValueKind.String ? actual.GetString() : actual.GetRawText()).IsEqualTo(expected);
    }

    [Test]
    public async Task AmqpSubjectRequiredDeclarationDefaultsToFalseRatherThanCloudEventsRules()
    {
        var metadata = RegistryDomainRules.CompleteMessageMetadata(RegistryJson.Parse(
            """{"protocol":"AMQP/1.0","protocoloptions":{"properties":{"subject":{"value":"orders"}}}}"""))
            .RootElement.GetProperty("protocoloptions").GetProperty("properties");

        await Assert.That(metadata.GetProperty("subject").GetProperty("required").GetBoolean()).IsFalse();
        await Assert.That(metadata.GetProperty("subject").GetProperty("value").GetString()).IsEqualTo("orders");
    }

    [Test]
    public async Task EndpointMessageGroupsReferencesNameGroupsRatherThanMessages()
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Endpoint);
        var attributes = model.Groups["endpoints"].Attributes;
        await Assert.That(attributes["messagegroups"].Item!.Target).IsEqualTo("/messagegroups");
        var result = RegistryMetadataValidator.Validate(RegistryJson.Parse("""{"messagegroups":["/messagegroups/orders"]}"""),
            attributes, new() { Mode = RegistryMetadataMode.ClientInput, Model = model });
        await Assert.That(result.Metadata.RootElement.GetProperty("messagegroups")[0].GetString()).IsEqualTo("/messagegroups/orders");
        await Assert.That(result.Obligations.Count).IsEqualTo(1);
        await Assert.That(result.Obligations[0].Kind).IsEqualTo("target");
    }

    [Test]
    [Arguments("HTTP/1.1", "method", "POST")]
    [Arguments("HTTP/2", "status", "201")]
    [Arguments("HTTP/3", "method", "PUT")]
    public async Task VersionedHttpSelectorsAdmitTypedProtocolOptions(string protocol, string field, string value)
    {
        var metadata = RegistryJson.Parse(
            "{\"protocol\":" + JsonSerializer.Serialize(protocol) +
            ",\"protocoloptions\":{\"headers\":[{\"name\":\"content-type\",\"value\":\"application/json\"}],\"query\":{\"tenant\":\"north\"},\"path\":\"/orders/{orderid}\",\"" +
            field + "\":" + JsonSerializer.Serialize(value) + "}}");
        var authoredOptions = metadata.RootElement.GetProperty("protocoloptions").GetRawText();
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Message);
        var result = RegistryMetadataValidator.Validate(metadata,
            model.Groups["messagegroups"].Resources["messages"].Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput, Model = model });

        await Assert.That(result.Metadata.RootElement.GetProperty("protocol").GetString()).IsEqualTo(protocol);
        await Assert.That(result.Metadata.RootElement.GetProperty("protocoloptions").GetRawText())
            .IsEqualTo(authoredOptions);
        await Assert.That(result.Metadata.RootElement.GetProperty("protocoloptions")
            .GetProperty("path").GetString()).IsEqualTo("/orders/{orderid}");
        await Assert.That(result.Metadata.RootElement.GetProperty("protocoloptions")
            .GetProperty(field).GetString()).IsEqualTo(value);
    }

    [Test]
    [Arguments("HTTP/1.1")]
    [Arguments("HTTP/2")]
    [Arguments("HTTP/3")]
    public async Task VersionedHttpSelectorsRejectInvalidTypedPath(string protocol)
    {
        var metadata = RegistryJson.Parse(
            "{\"protocol\":" + JsonSerializer.Serialize(protocol) +
            ",\"protocoloptions\":{\"headers\":[],\"query\":{\"tenant\":\"north\"},\"path\":17,\"method\":\"POST\"}}");
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Message);

        var failure = await Assert.That(() => RegistryMetadataValidator.Validate(metadata,
            model.Groups["messagegroups"].Resources["messages"].Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput, Model = model })).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected an invalid typed Message HTTP path diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/protocoloptions/path");
    }
}
