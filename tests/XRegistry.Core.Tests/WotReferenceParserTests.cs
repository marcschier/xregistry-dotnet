// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Wot;

namespace XRegistry.Core.Tests;

public class WotReferenceParserTests
{
    [Test]
    [Arguments("tm:extends", "extends", "tm:minVersion")]
    [Arguments("tm:submodel", "submodel", "tm:minVersion")]
    [Arguments("type", "type", "tm:minVersion")]
    [Arguments("td:extends", "td-extends", "td:minVersion")]
    [Arguments("td:subthing", "td-subthing", "td:minVersion")]
    public async Task LinkFloorIsNormalizedWithoutChangingSource(string relation, string kind, string term)
    {
        var json = $$"""{"rel":"{{relation}}","href":"urn:example:thing","{{term}}":"1.2.0"}""";
        using var document = JsonDocument.Parse(json);
        var reference = WotReferenceParser.Parse(document.RootElement);
        await Assert.That(reference).IsEqualTo(new WotReferenceRequest(kind, "urn:example:thing", "1.2.0", term));
        await Assert.That(document.RootElement.GetRawText()).IsEqualTo(json);
    }

    [Test]
    [Arguments("td:ref", "td-ref", "td:minVersion")]
    [Arguments("tm:ref", "ref", "tm:minVersion")]
    public async Task DefinitionReferenceUsesSiblingFloorAndRemovesFragment(string member, string kind, string term)
    {
        using var document = JsonDocument.Parse($$"""{"{{member}}":"../definitions#/properties/status","{{term}}":"1.2.0"}""");
        var reference = WotReferenceParser.Parse(document.RootElement, new Uri("https://example.org/things/lamp"));
        await Assert.That(reference).IsEqualTo(new WotReferenceRequest(kind, "https://example.org/definitions", "1.2.0", term));
    }

    [Test]
    public async Task UnflooredDefinitionReferenceRetainsOrdinalDocumentIdentity()
    {
        using var document = JsonDocument.Parse("""{"td:ref":"urn:Example:Thing#/properties/status"}""");
        await Assert.That(WotReferenceParser.Parse(document.RootElement))
            .IsEqualTo(new WotReferenceRequest("td-ref", "urn:Example:Thing"));
    }

    [Test]
    [Arguments("tm:extends", "td:minVersion")]
    [Arguments("td:extends", "tm:minVersion")]
    [Arguments("type", "td:minVersion")]
    public async Task CrossKindFloorIsReportedDuringResolutionButBypassedByPin(string relation, string term)
    {
        using var document = JsonDocument.Parse($$"""{"rel":"{{relation}}","href":"urn:example:thing","{{term}}":"1.2.0"}""");
        var reference = WotReferenceParser.Parse(document.RootElement);
        WotResourceSnapshot[] resources = [new("urn:example:thing", false, "1.2.0", [new("1.2.0", "authored", "digest")])];
        WotVersionException? failure = null;
        try
        {
            _ = WotVersionResolver.Resolve(resources, reference, "origin");
        }
        catch (WotVersionException exception)
        {
            failure = exception;
        }

        await Assert.That(failure?.Code).IsEqualTo("invalid-floor");
        var pinned = WotVersionResolver.Resolve(resources, reference, "origin", pin: new(VersionId: "1.2.0"));
        await Assert.That(pinned).IsEqualTo(new WotResolutionResult("urn:example:thing", "1.2.0", "digest", "origin"));
    }

    [Test]
    [Arguments("/definitions#/status", "https://example.org/definitions")]
    [Arguments("//other.example/definitions#/status", "https://other.example/definitions")]
    [Arguments("#/status", "https://example.org/things/lamp")]
    public async Task RelativeReferencesUseUriRatherThanFilePathSemantics(string target, string expected)
    {
        using var document = JsonDocument.Parse($$"""{"td:ref":"{{target}}","td:minVersion":"1.2.0"}""");
        var reference = WotReferenceParser.Parse(document.RootElement, new Uri("https://example.org/things/lamp"));
        await Assert.That(reference.Target).IsEqualTo(expected);
        await Assert.That(reference.MinimumVersion).IsEqualTo("1.2.0");
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("0.0.0-1")]
    [Arguments("1.2.0+build")]
    public async Task InvalidStringFloorIsDeferredUntilUnpinnedResolution(string floor)
    {
        using var document = JsonDocument.Parse($$"""{"td:ref":"urn:example:thing","td:minVersion":"{{floor}}"}""");
        var reference = WotReferenceParser.Parse(document.RootElement);
        WotResourceSnapshot[] resources = [new("urn:example:thing", false, "1.2.0", [new("1.2.0", "authored", "digest")])];
        WotVersionException? failure = null;
        try
        {
            _ = WotVersionResolver.Resolve(resources, reference, "origin");
        }
        catch (WotVersionException exception)
        {
            failure = exception;
        }

        await Assert.That(failure?.Code).IsEqualTo("invalid-version");
        var pinned = WotVersionResolver.Resolve(resources, reference, "origin", pin: new(VersionId: "1.2.0"));
        await Assert.That(pinned.VersionId).IsEqualTo("1.2.0");
    }

    [Test]
    [Arguments("[]")]
    [Arguments("{}")]
    [Arguments("{\"rel\":\"alternate\",\"href\":\"urn:example:thing\"}")]
    [Arguments("{\"rel\":\"td:extends\"}")]
    [Arguments("{\"td:ref\":1}")]
    [Arguments("{\"td:ref\":\"urn:example:thing\",\"tm:ref\":\"urn:example:model\"}")]
    [Arguments("{\"td:ref\":\"../definitions\"}")]
    public async Task InvalidReferenceShapeOrMissingBaseIsRejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        await Assert.That(() => WotReferenceParser.Parse(document.RootElement)).Throws<ArgumentException>();
    }
}
