// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;

namespace XRegistry.Models.Tests;

public class EndpointAuthoredModelTests
{
    [Test]
    [Arguments("""{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"endpoints":[{"uri":"https://api.example/{tenant}/events"}]}}""")]
    [Arguments("""{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"query":{"{parameter}":"{tenant}"}}}""")]
    [Arguments("""{"usage":["consumer"],"protocol":"AMQP/1.0","protocoloptions":{"distribution-mode":"{mode}"}}""")]
    public async Task AuthoredTemplatesAreRepresentableWithoutWeakeningTheCoreModel(string metadata)
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Endpoint);
        var input = RegistryJson.Parse(metadata);
        var result = RegistryMetadataValidator.Validate(input, model.Groups["endpoints"].Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput, Model = model });

        await Assert.That(result.Metadata.RootElement.GetProperty("protocoloptions").GetRawText())
            .IsEqualTo(input.RootElement.GetProperty("protocoloptions").GetRawText());
        await Assert.That(model.Groups["endpoints"].Attributes["protocol"].IfValues["HTTP"]["protocoloptions"].Type)
            .IsEqualTo(RegistryValueType.Any);
    }

    [Test]
    [Arguments("HTTP/1.1")]
    [Arguments("HTTP/2")]
    [Arguments("HTTP/3")]
    public async Task VersionedHttpEndpointOptionsPassAuthoredModelAdmission(string protocol)
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Endpoint);
        var input = RegistryJson.Parse($$$"""
            {"usage":["consumer"],"protocol":"{{{protocol}}}","protocoloptions":{"method":"GET","endpoints":[{"uri":"https://api.example/events"}]}}
            """);
        var result = RegistryMetadataValidator.Validate(input, model.Groups["endpoints"].Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput, Model = model });

        await Assert.That(result.Metadata.RootElement.GetProperty("protocol").GetString()).IsEqualTo(protocol);
        await Assert.That(result.Metadata.RootElement.GetProperty("protocoloptions").GetProperty("method").GetString())
            .IsEqualTo("GET");
    }

    [Test]
    [Arguments("HTTP/1.1")]
    [Arguments("HTTP/2")]
    [Arguments("HTTP/3")]
    public async Task VersionedHttpSelectorsAreAdmittedAndPreserved(string protocol)
    {
        var metadata = RegistryJson.Parse(
            "{\"usage\":[\"producer\"],\"protocol\":" + JsonSerializer.Serialize(protocol) +
            ",\"protocoloptions\":{\"headers\":{\"content-type\":\"application/json\"},\"query\":{\"tenant\":\"north\"},\"path\":\"/orders/{orderid}\",\"method\":\"POST\"}}");
        var authoredOptions = metadata.RootElement.GetProperty("protocoloptions").GetRawText();
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Endpoint);
        var result = RegistryMetadataValidator.Validate(metadata, model.Groups["endpoints"].Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput, Model = model });

        await Assert.That(result.Metadata.RootElement.GetProperty("protocol").GetString()).IsEqualTo(protocol);
        await Assert.That(result.Metadata.RootElement.GetProperty("protocoloptions").GetRawText())
            .IsEqualTo(authoredOptions);
        await Assert.That(result.Metadata.RootElement.GetProperty("usage")[0].GetString()).IsEqualTo("producer");
    }
}
