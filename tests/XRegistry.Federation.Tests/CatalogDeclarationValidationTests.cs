// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class CatalogDeclarationValidationTests
{
    [Test]
    [Arguments("http", "https://registry.example/root", """{"future":true}""")]
    [Arguments("git", "https://git.example/repo", """{"revision":"refs/heads/main","future":true}""")]
    [Arguments("oci", "oci://host/team/repo", """{"reference":"stable","future":true}""")]
    [Arguments("file", "file:///catalog", """{"layout":"document-tree","future":true}""")]
    public async Task UnknownSelectedParametersStillFailWithoutDroppingThemOrFallingBack(string name, string endpoint, string parameters)
    {
        var description = Description(name, endpoint, parameters);
        await Assert.That(description.Advertisements[0].Parameters.GetProperty("future").GetBoolean()).IsTrue();
        await Check.Error(() => description.Select(new(["http", "git", "oci", "file"])),
            FederationErrorCode.UnsupportedOperation);
        await Check.Error(description.Advertisements[0].ValidateBinding, FederationErrorCode.UnsupportedOperation);
        var explicitAlternative = description.Select(new(["http", "git", "oci", "file"], originalIndex: 1));
        await Assert.That(explicitAlternative.OriginalIndex).IsEqualTo(1);
        await Assert.That(explicitAlternative.Endpoint).IsEqualTo("https://fallback.example/root");
        await Assert.That(description.Advertisements[0].Parameters.GetRawText()).IsEqualTo(parameters);
    }

    [Test]
    [Arguments("oci://host/team/../repo")]
    [Arguments("oci://host/team/%72epo")]
    [Arguments("urn:x")]
    public async Task OciDeclarationErrorsAreReportedOnlyAtSelectedBindingValidation(string endpoint)
    {
        var description = Description("oci", endpoint, """{"reference":"stable"}""");
        await Assert.That(description.Advertisements[0].Endpoint).IsEqualTo(endpoint);
        await Check.Error(description.Advertisements[0].ValidateBinding, FederationErrorCode.InvalidPackage);
        await Check.Error(() => description.Select(new(["oci", "http"])), FederationErrorCode.InvalidPackage);
        await Assert.That(description.Select(new(["http"])).OriginalIndex).IsEqualTo(1);
    }

    [Test]
    [Arguments("""{"registrytypes":["schema"]}""")]
    [Arguments("""{"federationprofiles":[{"name":"future","endpoint":"/relative"}]}""")]
    [Arguments("""{"federationprofiles":[{"name":"future","endpoint":"https://user@registry.example/root"}]}""")]
    public async Task MalformedCommonDataFailsBeforeSelectionPolicy(string json)
    {
        var policyCalls = 0;
        await Check.Error(() => Parse(json).Select(new(["http"], accessPolicy: _ =>
        {
            policyCalls++;
            return AdvertisementAccess.Allow;
        })), FederationErrorCode.InvalidPackage);
        await Assert.That(policyCalls).IsEqualTo(0);
    }

    [Test]
    public async Task WebsiteAndDescriptiveLinksDoNotBecomeAdvertisements()
    {
        var description = Parse("""
            {"weburl":"https://registry.example/root","authority":"urn:example:authority",
             "relationships":[{"type":"depends-on","target":"/categories/missing/registries/missing"}]}
            """);
        await Assert.That(description.Advertisements.Count).IsEqualTo(0);
        await Check.Error(() => description.Select(new(["http", "git", "oci", "file"])), FederationErrorCode.UnsupportedBinding);
    }

    [Test]
    public async Task ValidDeclarationDoesNotOverrideCallerPolicy()
    {
        var description = Description("file", "file:///catalog", """{"layout":"document-tree"}""");
        await Check.Error(() => description.Select(new(["file", "http"], accessPolicy: _ => AdvertisementAccess.Deny)),
            FederationErrorCode.PolicyDenied);
        await Assert.That(description.Advertisements[0].Parameters.GetProperty("layout").GetString()).IsEqualTo("document-tree");
    }

    [Test]
    public async Task SharedValidationKeepsCallerCancellation()
    {
        await Assert.That(() => CatalogDescription.Parse("{}"u8.ToArray(), cancellationToken: new CancellationToken(canceled: true)))
            .Throws<OperationCanceledException>();
    }

    private static CatalogDescription Description(string name, string endpoint, string parameters) =>
        Parse(new JsonObject
        {
            ["federationprofiles"] = new JsonArray(
                new JsonObject { ["name"] = name, ["endpoint"] = endpoint, ["parameters"] = JsonNode.Parse(parameters) },
                new JsonObject { ["name"] = "http", ["endpoint"] = "https://fallback.example/root", ["priority"] = 9 })
        }.ToJsonString());

    private static CatalogDescription Parse(string json) => CatalogDescription.Parse(Encoding.UTF8.GetBytes(json));
}
