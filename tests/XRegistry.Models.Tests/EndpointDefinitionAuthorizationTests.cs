// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class EndpointDefinitionAuthorizationTests
{
    [Test]
    public async Task AuthorizationMembersRejectEveryInvalidNativeKindAndEmptyDeclaration()
    {
        foreach (var value in new[] { "null", "true", "42", "\"entry\"", "{}" })
        {
            await Rejects($$"""{"authorization":{{value}}}""", "/protocoloptions/authorization", "invalid_attribute");
        }
        foreach (var value in new[] { "null", "true", "42", "\"entry\"", "[]" })
        {
            await Rejects($$"""{"authorization":[{{value}}]}""", "/protocoloptions/authorization/0", "invalid_attribute");
        }
        foreach (var field in new[] { "type", "mechanism", "resourceuri", "authorityuri" })
        {
            foreach (var value in new[] { "null", "true", "42", "{}", "[]", "\"\"" })
            {
                var member = field == "type" ? "" : "\"type\":\"SASL\",";
                await Rejects($$"""{"authorization":[{ {{member}}"{{field}}":{{value}} }]}""",
                    "/protocoloptions/authorization/0/" + field, "invalid_attribute");
            }
        }
        foreach (var field in new[] { "resourceuri", "authorityuri" })
        {
            foreach (var value in new[] { "bad uri", "https://example.com/%ZZ", "https://[bad]/auth" })
            {
                await Rejects($$"""{"authorization":[{"{{field}}":"{{value}}"}]}""",
                    "/protocoloptions/authorization/0/" + field, "invalid_attribute");
            }
        }
        await Rejects("""{"authorization":[{"type":"sasl","mechanism":"PLAIN"}]}""",
            "/protocoloptions/authorization/0/mechanism", "invalid_attribute");
    }

    [Test]
    public async Task AuthorizationAlternativesKeepOptionalSelectorsExtensionsAndPreferenceOrder()
    {
        var empty = EndpointDefinition.Materialize(RegistryJson.Parse(
            """{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[]}}"""));
        await Assert.That(empty.GetProtocolOption("authorization").GetArrayLength()).IsEqualTo(0);
        await Assert.That(empty.DeferredChecks.Count(check => check.Code == "authorization_configuration")).IsEqualTo(0);
        var input = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[
             {"type":"OAuth2","resourceuri":"urn:orders:west","authorityuri":"../issuer"},
             {"type":"SASL"},{"vendor-selection":{"profile":"corporate"}},
             {"type":"Vendor/2","authorityuri":"/auth/vendor"}]}}
            """);
        var before = input.RootElement.GetRawText();
        var result = EndpointDefinition.Materialize(input);
        await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(result.GetProtocolOption("authorization").GetRawText()),
            JsonNode.Parse(input.RootElement.GetProperty("protocoloptions").GetProperty("authorization").GetRawText()))).IsTrue();
        await Assert.That(result.GetProtocolOption("authorization")[1].TryGetProperty("mechanism", out _)).IsFalse();
        await Assert.That(result.GetProtocolOption("authorization")[2].TryGetProperty("type", out _)).IsFalse();
        await Assert.That(result.DeferredChecks.Where(check => check.Code == "authorization_configuration")
            .Select(check => check.Path).ToArray()).IsEquivalentTo([
                "/protocoloptions/authorization/0", "/protocoloptions/authorization/1",
                "/protocoloptions/authorization/2", "/protocoloptions/authorization/3"
            ], StringComparer.Ordinal);
        await Assert.That(input.RootElement.GetRawText()).IsEqualTo(before);
    }

    [Test]
    public async Task AuthorizationTemplatesRemainMetadataAndRelativeReferencesAreNotAcquired()
    {
        var input = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP","protocoloptions":{"endpoints":[{"uri":"https://192.0.2.1:1/{tenant}"}],
             "authorization":[{"type":"{type}","mechanism":"SCRAM-SHA-256","resourceuri":"urn:orders:{tenant}","authorityuri":"../auth/{tenant}"},
              {"authorityuri":"/auth/custom","vendor-mechanism":"corporate"}]}}
            """);
        var result = EndpointDefinition.Materialize(input, RegistryJson.Parse("""{"type":"SASL","tenant":"west"}"""));
        await Assert.That(result.GetProtocolOption("authorization")[0].GetProperty("resourceuri").GetString()).IsEqualTo("urn:orders:west");
        await Assert.That(result.GetProtocolOption("authorization")[0].GetProperty("authorityuri").GetString()).IsEqualTo("../auth/west");
        await Assert.That(result.GetProtocolOption("authorization")[1].TryGetProperty("type", out _)).IsFalse();
        await Assert.That(result.DeferredChecks.Count(check => check.Code == "authorization_configuration")).IsEqualTo(2);
    }

    [Test]
    [Arguments("""{"authorization":null}""", "/protocoloptions/authorization")]
    [Arguments("""{"authorization":[{}]}""", "/protocoloptions/authorization/0")]
    [Arguments("""{"authorization":[{"type":""}]}""", "/protocoloptions/authorization/0/type")]
    [Arguments("""{"authorization":[{"type":"Plain","mechanism":"PLAIN"}]}""", "/protocoloptions/authorization/0/mechanism")]
    [Arguments("""{"authorization":[{"mechanism":"PLAIN"}]}""", "/protocoloptions/authorization/0/mechanism")]
    [Arguments("""{"authorization":[{"type":"SASL","mechanism":""}]}""", "/protocoloptions/authorization/0/mechanism")]
    [Arguments("""{"authorization":[{"type":"OAuth2","resourceuri":""}]}""", "/protocoloptions/authorization/0/resourceuri")]
    [Arguments("""{"authorization":[{"type":"OAuth2","authorityuri":"bad uri"}]}""", "/protocoloptions/authorization/0/authorityuri")]
    public Task AuthorizationUsesOnlyItsActualRequiredAndConditionalConstraints(string options, string path) =>
        Rejects(options, path, "invalid_attribute");

    [Test]
    public async Task OptionalAuthorizationSelectorsAndPathTextAreNotInventedCredentials()
    {
        var input = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[
             {"type":"SASL"},{"authorityuri":"docs/http://alice@example.com/info","vendor-mechanism":"custom"}]}}
            """);
        var result = EndpointDefinition.Materialize(input);
        await Assert.That(result.GetProtocolOption("authorization")[0].TryGetProperty("mechanism", out _)).IsFalse();
        await Assert.That(result.GetProtocolOption("authorization")[1].GetProperty("authorityuri").GetString())
            .IsEqualTo("docs/http://alice@example.com/info");
        await Assert.That(result.DeferredChecks.Count(check => check.Code == "authorization_configuration")).IsEqualTo(2);
    }

    [Test]
    [Arguments("""{"username":"alice"}""", "/protocoloptions/username")]
    [Arguments("""{"authorization":[{"type":"Plain","password":"secret"}]}""", "/protocoloptions/authorization/0/password")]
    [Arguments("""{"sasl.jaas.config":"credential configuration"}""", "/protocoloptions/sasl.jaas.config")]
    [Arguments("""{"headers":[{"name":"Authorization","value":"Bearer secret"}]}""", "/protocoloptions/headers/0/value")]
    [Arguments("""{"headers":[{"name":"Cookie","value":"session=secret"}]}""", "/protocoloptions/headers/0/value")]
    [Arguments("""{"apikeyname":"x-token","headers":[{"name":"X-Token","value":"secret"}],"authorization":[{"type":"APIKey"}]}""",
        "/protocoloptions/headers/0/value")]
    [Arguments("""{"apikeyname":"key","apikeyin":"query","query":{"key":"secret"},"authorization":[{"type":"APIKey"}]}""",
        "/protocoloptions/query/key")]
    [Arguments("""{"plainscheme":"query","plainusernamefield":"user","query":{"user":"alice"},"authorization":[{"type":"Plain"}]}""",
        "/protocoloptions/query/user")]
    [Arguments("""{"endpoints":[{"uri":"https://alice:secret@example.com"}]}""", "/protocoloptions/endpoints/0/uri")]
    [Arguments("""{"endpoints":[{"uri":"https://example.com?access_token=secret"}]}""", "/protocoloptions/endpoints/0/uri")]
    [Arguments("""{"endpoints":[{"uri":"https://example.com?%61ccess_token=secret"}]}""", "/protocoloptions/endpoints/0/uri")]
    public Task RecognizedCredentialCarriersAreNeverReturnedAsEndpointConfiguration(string options, string path) =>
        Rejects(options, path, "endpoint_credentials");

    [Test]
    public async Task SuppliedCredentialValuesDoNotLeakThroughFailuresOrMutateAuthoredInput()
    {
        var input = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP","protocoloptions":{"headers":[{"name":"Authorization","value":"Bearer {credential}"}]}}
            """);
        var failure = await Assert.That(() => EndpointDefinition.Materialize(input,
            RegistryJson.Parse("""{"credential":"TOP-SECRET"}"""))).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a credential diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("endpoint_credentials");
        await Assert.That(failure.Message.Contains("TOP-SECRET", StringComparison.Ordinal)).IsFalse();
        await Assert.That(input.RootElement.GetProperty("protocoloptions").GetProperty("headers")[0].GetProperty("value").GetString())
            .IsEqualTo("Bearer {credential}");
    }

    [Test]
    public async Task CloudEventsFormatsCheckDeclaredHttpContentTypesAndDeferRuntimeEvidence()
    {
        var valid = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP","envelope":"CloudEvents/1.0",
             "envelopeoptions":{"mode":"structured","format":"application/cloudevents+json"},
             "protocoloptions":{"headers":[{"name":"Content-Type","value":"application/cloudevents+json"}]}}
            """);
        var result = EndpointDefinition.Materialize(valid);
        await Assert.That(result.DeferredChecks.Any(check => check.Code == "envelope_content_type")).IsTrue();
        var invalid = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP","envelope":"CloudEvents/1.0",
             "envelopeoptions":{"mode":"structured","format":"application/cloudevents+json"},
             "protocoloptions":{"headers":[{"name":"Content-Type","value":"application/xml"}]}}
            """);
        var failure = await Assert.That(() => EndpointDefinition.Materialize(invalid)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected an envelope contract diagnostic.");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/protocoloptions/headers/0/value");
    }

    private static async Task Rejects(string options, string path, string code)
    {
        var input = RegistryJson.Parse($$$"""{"usage":["producer"],"protocol":"HTTP","protocoloptions":{{{options}}}}""");
        var failure = await Assert.That(() => EndpointDefinition.Materialize(input)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected an authorization diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo(code);
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(path);
    }
}
