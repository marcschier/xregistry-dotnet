// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class EndpointTemplateMaterializationTests
{
    [Test]
    public async Task LevelOneExpansionUsesOneValueAcrossUriMapKeysAndArrayStrings()
    {
        var authored = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP","description":"Keep {tenant}",
             "protocoloptions":{"endpoints":[{"uri":"https://api.example/{tenant}/events"}],
              "headers":[{"name":"X-Tenant","value":"tenant={tenant}"}],
              "query":{"{parameter}":"{tenant}","literal":"%2f"}}}
            """);
        var arguments = RegistryJson.Parse("""{"tenant":"North / West%2F","parameter":"filter"}""");

        var definition = EndpointDefinition.Materialize(authored, arguments);

        await Assert.That(definition.Resolved.RootElement.GetProperty("protocoloptions").GetRawText()).IsEqualTo(
            """{"endpoints":[{"uri":"https://api.example/North%20%2F%20West%252F/events"}],"headers":[{"name":"X-Tenant","value":"tenant=North%20%2F%20West%252F"}],"query":{"filter":"North%20%2F%20West%252F","literal":"%2f"}}""");
        await Assert.That(definition.Authored.RootElement.GetProperty("protocoloptions").GetProperty("query")
            .GetProperty("{parameter}").GetString()).IsEqualTo("{tenant}");
        await Assert.That(definition.Resolved.RootElement.GetProperty("description").GetString()).IsEqualTo("Keep {tenant}");
        await Assert.That(arguments.RootElement.GetProperty("tenant").GetString()).IsEqualTo("North / West%2F");
    }

    [Test]
    [Arguments("{+x}")]
    [Arguments("{#x}")]
    [Arguments("{.x}")]
    [Arguments("{/x}")]
    [Arguments("{;x}")]
    [Arguments("{?x}")]
    [Arguments("{&x}")]
    [Arguments("{x,y}")]
    [Arguments("{x:2}")]
    [Arguments("{x*}")]
    [Arguments("{x-y}")]
    [Arguments("{x..y}")]
    [Arguments("{x.}")]
    [Arguments("{x%2}")]
    [Arguments("{=x}")]
    [Arguments("{}")]
    [Arguments("{{x}}")]
    [Arguments("{x")]
    [Arguments("x}")]
    public async Task OnlySingleUnmodifiedLevelOneExpressionsAreAccepted(string template)
    {
        var input = RegistryJson.Create(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("usage");
            writer.WriteStringValue("producer");
            writer.WriteEndArray();
            writer.WriteString("protocol", "HTTP");
            writer.WriteStartObject("protocoloptions");
            writer.WriteString("extension", template);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
        var failure = await Assert.That(() => EndpointDefinition.Materialize(input,
            RegistryJson.Parse("""{"x":"hello","y":"world"}"""))).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a template diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_endpoint_template");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/protocoloptions/extension");
    }

    [Test]
    public async Task EndpointVariableNamesFollowRfc6570RatherThanMessageSymbolNames()
    {
        var input = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP",
             "protocoloptions":{"extension":"{tenant.name}:{%74enant}:{tenant}:{Tenant}:{a%2Eb}"}}
            """);
        var result = EndpointDefinition.Materialize(input,
            RegistryJson.Parse("""{"tenant.name":"a","%74enant":"b","tenant":"c","Tenant":"d","a%2Eb":"e"}"""));
        await Assert.That(result.Resolved.RootElement.GetProperty("protocoloptions").GetProperty("extension").GetString())
            .IsEqualTo("a:b:c:d:e");
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"x":null}""")]
    [Arguments("""{"X":"wrong-case"}""")]
    public async Task MissingVariablesFailInsteadOfBecomingEmptyStrings(string arguments)
    {
        var input = RegistryJson.Parse("""{"usage":["producer"],"protocoloptions":{"x":"before-{x}-after"}}""");
        var failure = await Assert.That(() => EndpointDefinition.Materialize(input, RegistryJson.Parse(arguments)))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an undefined-variable diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("undefined_template_variable");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/protocoloptions/x");
    }

    [Test]
    [Arguments("""{"x":[]}""")]
    [Arguments("""{"x":{}}""")]
    [Arguments("""{"x":true}""")]
    [Arguments("""{"x":12}""")]
    public async Task LevelOneBindingsDoNotInjectCompositeOrTypedJson(string arguments)
    {
        var input = RegistryJson.Parse("""{"usage":["producer"],"protocoloptions":{"extension":"{x}"}}""");
        var failure = await Assert.That(() => EndpointDefinition.Materialize(input, RegistryJson.Parse(arguments)))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a binding diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_template_arguments");
    }

    [Test]
    public async Task EmptyValuesUnicodeAndPercentEscapesExpandWithoutRecursion()
    {
        var input = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP",
             "protocoloptions":{"extension":["O{empty}X","%2f/{text}","{nested}","{unreserved}"]}}
            """);
        var result = EndpointDefinition.Materialize(input, RegistryJson.Parse("""
            {"empty":"","text":"\u00e9/\ud83d\ude80%2F","nested":"{other}","unreserved":"aAZ09-._~"}
            """));
        await Assert.That(result.Resolved.RootElement.GetProperty("protocoloptions").GetProperty("extension").GetRawText())
            .IsEqualTo("""["OX","%2f/%C3%A9%2F%F0%9F%9A%80%252F","%7Bother%7D","aAZ09-._~"]""");
    }

    [Test]
    public async Task AuthoredNumbersBooleansAndCallerOwnedBuffersRemainUnchanged()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("""
            {"usage":["producer"],"protocoloptions":{"extension":{"huge":123456789012345678901234567890,
             "exponent":1.2300e+100,"negativezero":-0.0,"flag":false,"nothing":null,"text":"{x}"}}}
            """);
        RegistryJson input;
        using (var document = JsonDocument.Parse(bytes))
        {
            input = RegistryJson.FromElement(document.RootElement);
        }
        Array.Fill(bytes, (byte)0);
        var result = EndpointDefinition.Materialize(input, RegistryJson.Parse("""{"x":"value"}"""));
        await Assert.That(result.Resolved.RootElement.GetProperty("protocoloptions").GetProperty("extension").GetRawText())
            .IsEqualTo("""{"huge":123456789012345678901234567890,"exponent":1.2300e+100,"negativezero":-0.0,"flag":false,"nothing":null,"text":"value"}""");
        await Assert.That(result.Authored.RootElement.GetProperty("protocoloptions").GetProperty("extension")
            .GetProperty("text").GetString()).IsEqualTo("{x}");
    }

    [Test]
    public async Task ExpandedMapKeysNeverOverwriteAnEarlierEntry()
    {
        var input = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP","protocoloptions":{"query":{"{x}":"first","same":"second"}}}
            """);
        var failure = await Assert.That(() => EndpointDefinition.Materialize(input, RegistryJson.Parse("""{"x":"same"}""")))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a collision diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("template_key_collision");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/protocoloptions/query/same");
        await Assert.That(input.RootElement.GetProperty("protocoloptions").GetProperty("query").GetProperty("same").GetString())
            .IsEqualTo("second");
    }

    [Test]
    public async Task CancellationPrecedesTemplateAndMetadataEvaluation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.That(() => EndpointDefinition.Materialize(RegistryJson.Parse("[]"),
            cancellationToken: source.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task UriTemplateLiteralsEncodeUnicodeButPreserveExistingPercentTriplets()
    {
        var input = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP","protocoloptions":{"endpoints":[{"uri":"https://example.com/caf\u00e9/%2f/{name}"}]}}
            """);
        var result = EndpointDefinition.Materialize(input, RegistryJson.Parse("""{"name":"\u00e9%2F"}"""));
        await Assert.That(result.GetProtocolOption("endpoints")[0].GetProperty("uri").GetString())
            .IsEqualTo("https://example.com/caf%C3%A9/%2f/%C3%A9%252F");
        await Assert.That(result.Authored.RootElement.GetProperty("protocoloptions").GetProperty("endpoints")[0]
            .GetProperty("uri").GetString()).IsEqualTo("https://example.com/caf\u00e9/%2f/{name}");
    }

    [Test]
    [Arguments("""{"endpoints":[{"u{part}":"https://example.com"}]}""", "/protocoloptions/endpoints/0/u{part}")]
    [Arguments("""{"headers":[{"{part}":"X-A","value":"one"}]}""", "/protocoloptions/headers/0/{part}")]
    [Arguments("""{"authorization":[{"{part}":"SASL"}]}""", "/protocoloptions/authorization/0/{part}")]
    public async Task TemplateMapKeysCannotRenameDeclaredRecordMembers(string options, string path)
    {
        var input = RegistryJson.Parse($$$"""{"usage":["producer"],"protocol":"HTTP","protocoloptions":{{{options}}}}""");
        var failure = await Assert.That(() => EndpointDefinition.Materialize(input, RegistryJson.Parse("""{"part":"ri"}""")))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a structural member diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_endpoint_template");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(path);
    }

    [Test]
    public async Task UriTemplatePercentTripletsCannotBeAssembledFromSeparateExpressions()
    {
        var input = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP","protocoloptions":{"endpoints":[{"uri":"https://example.com/%{hex}"}]}}
            """);
        var failure = await Assert.That(() => EndpointDefinition.Materialize(input, RegistryJson.Parse("""{"hex":"2F"}""")))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an incomplete literal percent triplet.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_endpoint_template");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/protocoloptions/endpoints/0/uri");
    }
}
