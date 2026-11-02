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
    [Arguments("tm:extends", "extends", "uav:minVersion")]
    [Arguments("tm:submodel", "submodel", "uav:minVersion")]
    [Arguments("type", "type", "uav:minVersion")]
    [Arguments("td:extends", "td-extends", "uav:minVersion")]
    [Arguments("td:subthing", "td-subthing", "uav:minVersion")]
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
    [Arguments("tm:ref", "ref", "uav:minVersion")]
    [Arguments("td:ref", "td-ref", "uav:minVersion")]
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
    [Arguments("tm:extends", "extends", "td:minVersion")]
    [Arguments("td:extends", "td-extends", "tm:minVersion")]
    [Arguments("type", "type", "td:minVersion")]
    public async Task CrossKindFloorCannotBeAuthoredButRawReplayBypassesIt(string relation, string kind, string term)
    {
        using var document = JsonDocument.Parse($$"""{"rel":"{{relation}}","href":"urn:example:thing","{{term}}":"1.2.0"}""");
        var parserFailure = await Assert.That(() => WotReferenceParser.Parse(document.RootElement)).Throws<WotVersionException>();
        await Assert.That(parserFailure!.Code).IsEqualTo("invalid-floor");
        var reference = new WotReferenceRequest(kind, "urn:example:thing", "1.2.0", term);
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
    public async Task InvalidStringFloorCannotBeAuthoredButRawReplayBypassesIt(string floor)
    {
        using var document = JsonDocument.Parse($$"""{"td:ref":"urn:example:thing","td:minVersion":"{{floor}}"}""");
        var parserFailure = await Assert.That(() => WotReferenceParser.Parse(document.RootElement)).Throws<WotVersionException>();
        await Assert.That(parserFailure!.Code).IsEqualTo("invalid-version");
        var reference = new WotReferenceRequest("td-ref", "urn:example:thing", floor, "td:minVersion");
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

    [Test]
    [Arguments("tm:minVersion")]
    [Arguments("td:minVersion")]
    public async Task BindingFloorAliasCannotCoexistWithLegacyFloor(string legacyTerm)
    {
        using var source = JsonDocument.Parse($$"""{"td:ref":"urn:example:thing","uav:minVersion":"1.0.0","{{legacyTerm}}":"1.0.0"}""");
        var failure = await Assert.That(() => WotReferenceParser.Parse(source.RootElement)).Throws<WotVersionException>();
        await Assert.That(failure!.Code).IsEqualTo("invalid-floor");
    }

    [Test]
    [Arguments("\"urn:example:line\"")]
    [Arguments("null")]
    [Arguments("false")]
    public async Task SemVerParserRejectsCompatibilityLineRatherThanIgnoringIt(string line)
    {
        using var source = JsonDocument.Parse($$"""{"td:ref":"urn:example:thing","uav:compatibilityLine":{{line}}}""");
        var failure = await Assert.That(() => WotReferenceParser.Parse(source.RootElement)).Throws<WotVersionException>();
        await Assert.That(failure!.Code).IsEqualTo("invalid-line");
    }

    [Test]
    [Arguments("tm:minVersion", "null")]
    [Arguments("td:minVersion", "null")]
    [Arguments("uav:minVersion", "null")]
    [Arguments("uav:minVersion", "42")]
    [Arguments("uav:minVersion", "false")]
    public async Task ExplicitNonStringFloorIsRejectedAsAuthoredInput(string term, string value)
    {
        using var source = JsonDocument.Parse($$"""{"td:ref":"urn:example:thing","{{term}}":{{value}}}""");
        var failure = await Assert.That(() => WotReferenceParser.Parse(source.RootElement)).Throws<WotVersionException>();
        await Assert.That(failure!.Code).IsEqualTo("invalid-version");
    }

    [Test]
    [Arguments("tm:ref", "2.0.0")]
    [Arguments("td:ref", "1.2.0")]
    public async Task BindingFloorAliasUsesTargetKindAndRetainsDocumentBytes(string member, string expectedVersion)
    {
        var json = $$"""{"{{member}}":"urn:example:thing","uav:minVersion":"1.1.0"}""";
        using var source = JsonDocument.Parse(json);
        var reference = WotReferenceParser.Parse(source.RootElement);
        WotResourceSnapshot[] resources = [new("urn:example:thing", false, "2.0.0",
            [new("1.2.0", "authored", "lower"), new("2.0.0", "authored", "higher")])];
        var result = WotVersionResolver.Resolve(resources, reference, "origin");
        await Assert.That(result.VersionId).IsEqualTo(expectedVersion);
        await Assert.That(result.ContentDigest).IsEqualTo(member == "tm:ref" ? "higher" : "lower");
        await Assert.That(source.RootElement.GetRawText()).IsEqualTo(json);
    }

    [Test]
    public async Task BindingTypeFloorAliasCombinesWithModelVersion()
    {
        using var source = JsonDocument.Parse("""{"rel":"type","href":"urn:example:model","uav:minVersion":"1.1.0"}""");
        var reference = WotReferenceParser.Parse(source.RootElement);
        WotResourceSnapshot[] resources = [new("urn:example:model", true, "1.2.0", [new("1.2.0", "authored", "bytes")])];
        var context = new WotDocumentVersionContext("1.3.0", [new("urn:example:model", "1.1.0")]);
        var failure = await Assert.That(() => WotVersionResolver.Resolve(resources, reference, "origin", context))
            .Throws<WotVersionException>();
        await Assert.That(failure!.Code).IsEqualTo("unsatisfied");
        await Assert.That(WotVersionResolver.Resolve(resources, reference, "origin", context, new(VersionId: "1.2.0")))
            .IsEqualTo(new WotResolutionResult("urn:example:model", "1.2.0", "bytes", "origin"));
    }

    [Test]
    public async Task BindingFloorDoesNotInferIncrementalProfileFromDecimalRevision()
    {
        using var source = JsonDocument.Parse("""{"td:ref":"urn:example:thing","uav:minVersion":"42"}""");
        var parserFailure = await Assert.That(() => WotReferenceParser.Parse(source.RootElement)).Throws<WotVersionException>();
        await Assert.That(parserFailure!.Code).IsEqualTo("invalid-version");
        var reference = new WotReferenceRequest("td-ref", "urn:example:thing", "42", "uav:minVersion");
        WotResourceSnapshot[] resources = [new("urn:example:thing", false, "1.2.0", [new("1.2.0", "authored", "bytes")])];
        var failure = await Assert.That(() => WotVersionResolver.Resolve(resources, reference, "origin"))
            .Throws<WotVersionException>();
        await Assert.That(failure!.Code).IsEqualTo("invalid-version");
        await Assert.That(WotVersionResolver.Resolve(resources, reference, "origin", pin: new(VersionId: "1.2.0")))
            .IsEqualTo(new WotResolutionResult("urn:example:thing", "1.2.0", "bytes", "origin"));
    }
}
