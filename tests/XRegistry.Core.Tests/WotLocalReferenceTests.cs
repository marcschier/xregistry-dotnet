// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Wot;

namespace XRegistry.Core.Tests;

public class WotLocalReferenceTests
{
    private const string Target = "https://example.org/things/pump";
    private const string Origin = "https://registry.example";

    [Test]
    [Arguments("tm:ref", "#/properties/status")]
    [Arguments("td:ref", "#/properties/status")]
    [Arguments("tm:ref", "pump#/properties/status")]
    [Arguments("td:ref", "https://example.org/things/pump#/properties/status")]
    public async Task ParserResolvesLocalReferenceAgainstExactContainingRevision(string member, string target)
    {
        var json = $$"""{"{{member}}":"{{target}}"}""";
        using var source = JsonDocument.Parse(json);
        var reference = WotReferenceParser.Parse(source.RootElement, new Uri(Target + "#old-fragment"));
        var context = new WotDocumentVersionContext(null, [])
        {
            ContainingVersion = new("0.0.0-1", "generated", "containing-bytes")
        };
        WotResourceSnapshot[] resources = [new(Target, false, "9.0.0", [new("9.0.0", "authored", "newer-bytes")])];

        await Assert.That(reference.IsDocumentLocal).IsTrue();
        await Assert.That(WotVersionResolver.Resolve(resources, reference, Origin, context))
            .IsEqualTo(new WotResolutionResult(Target, "0.0.0-1", "containing-bytes", Origin));
        await Assert.That(source.RootElement.GetRawText()).IsEqualTo(json);
    }

    [Test]
    [Arguments("ref", "1.2.0", null, "1.2.0", null)]
    [Arguments("ref", "1.2.0", "9.0.0", "1.3.0", "unsatisfied")]
    [Arguments("ref", null, "9.0.0", "1.0.0", "unsatisfied")]
    [Arguments("ref", "v1.2", null, "1.0.0", "invalid-version")]
    [Arguments("ref", "1.2.0+build", null, "1.0.0", "invalid-version")]
    [Arguments("ref", "2.0.0-rc.1", null, "1.0.0", null)]
    [Arguments("td-ref", "9.0.0", "1.2.0", "1.2.0", null)]
    [Arguments("td-ref", "9.0.0", "1.2.0", "1.3.0", "unsatisfied")]
    [Arguments("td-ref", "9.0.0", null, "1.0.0", "unsatisfied")]
    [Arguments("td-ref", null, "legacy", "1.0.0", "invalid-version")]
    [Arguments("td-ref", null, "1.2.0+build", "1.0.0", "invalid-version")]
    [Arguments("td-ref", null, "2.0.0", "1.0.0", "unsatisfied")]
    [Arguments("td-ref", null, "2.0.0-rc.1", "1.0.0", "unsatisfied")]
    [Arguments("td-ref", null, "0.9.4", "0.8.0", null)]
    [Arguments("td-ref", null, "1.0.0-rc.1", "0.8.0", "unsatisfied")]
    [Arguments("td-ref", null, "1.2.0-rc.1", "1.1.0", null)]
    [Arguments("ref", "invalid", null, null, null)]
    [Arguments("td-ref", null, "invalid", null, null)]
    public async Task LocalFloorChecksAuthoredVersionWithoutSelectingAnotherVersion(string kind,
        string? modelVersion, string? instanceVersion, string? floor, string? expectedError)
    {
        var reference = new WotReferenceRequest(kind, Target, floor) { IsDocumentLocal = true };
        var context = new WotDocumentVersionContext("99.0.0", [])
        {
            ContainingVersion = new("0.0.0-7", "generated", "containing-bytes", modelVersion, instanceVersion)
        };
        WotResolutionResult? result = null;
        WotVersionException? failure = null;
        try
        {
            result = WotVersionResolver.Resolve([], reference, Origin, context);
        }
        catch (WotVersionException exception)
        {
            failure = exception;
        }

        await Assert.That(failure?.Code).IsEqualTo(expectedError);
        await Assert.That(result).IsEqualTo(expectedError is null
            ? new WotResolutionResult(Target, "0.0.0-7", "containing-bytes", Origin)
            : null);
    }

    [Test]
    [Arguments("ref", "td:minVersion", "1.0.0", "invalid-floor")]
    [Arguments("td-ref", "tm:minVersion", "1.0.0", "invalid-floor")]
    [Arguments("ref", "tm:minVersion", "0.0.0-1", "invalid-version")]
    [Arguments("td-ref", "td:minVersion", "1.0.0+build", "invalid-version")]
    public async Task LocalReferenceRetainsFloorValidation(string kind, string term, string floor, string expectedError)
    {
        var reference = new WotReferenceRequest(kind, Target, floor, term) { IsDocumentLocal = true };
        var context = new WotDocumentVersionContext(null, []) { ContainingVersion = new("1.0.0", "authored", "bytes") };
        var failure = await Assert.That(() => WotVersionResolver.Resolve([], reference, Origin, context))
            .Throws<WotVersionException>();
        await Assert.That(failure!.Code).IsEqualTo(expectedError);
    }

    [Test]
    public async Task LocalReferenceRequiresContainingRevisionAndRejectsPinsOrLinkKinds()
    {
        var reference = new WotReferenceRequest("ref", Target) { IsDocumentLocal = true };
        var context = new WotDocumentVersionContext(null, []) { ContainingVersion = new("1.0.0", "authored", "bytes") };
        await Assert.That(() => WotVersionResolver.Resolve([], reference, Origin)).Throws<ArgumentException>();
        await Assert.That(() => WotVersionResolver.Resolve([], reference, Origin, new(null, []))).Throws<ArgumentException>();
        await Assert.That(() => WotVersionResolver.Resolve([], reference, Origin, context, new(Digest: "bytes"))).Throws<ArgumentException>();
        await Assert.That(() => WotVersionResolver.Resolve([], reference, Origin, context, new(VersionId: "1.0.0"))).Throws<ArgumentException>();
        await Assert.That(() => WotVersionResolver.Resolve([], reference with { Kind = "extends" }, Origin, context)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("tm:ref", "1.2.0", null)]
    [Arguments("td:ref", null, "1.2.0")]
    public async Task BindingFloorAliasChecksContainingRevision(string member, string? modelVersion, string? instanceVersion)
    {
        using var source = JsonDocument.Parse($$"""{"{{member}}":"#/status","uav:minVersion":"1.3.0"}""");
        var reference = WotReferenceParser.Parse(source.RootElement, new Uri(Target));
        var context = new WotDocumentVersionContext(null, [])
        {
            ContainingVersion = new("1.2.0", "authored", "containing-bytes", modelVersion, instanceVersion)
        };
        WotResourceSnapshot[] resources = [new(Target, false, "1.5.0", [new("1.5.0", "authored", "newer-bytes")])];
        var failure = await Assert.That(() => WotVersionResolver.Resolve(resources, reference, Origin, context))
            .Throws<WotVersionException>();
        await Assert.That(failure!.Code).IsEqualTo("unsatisfied");
    }

    [Test]
    [Arguments("#/status")]
    [Arguments("urn:example:model#/status")]
    public async Task UrnDefinitionReferenceRecognizesContainingDocument(string target)
    {
        using var source = JsonDocument.Parse($$"""{"tm:ref":"{{target}}"}""");
        var reference = WotReferenceParser.Parse(source.RootElement, new Uri("urn:example:model"));
        await Assert.That(reference.Target).IsEqualTo("urn:example:model");
        await Assert.That(reference.IsDocumentLocal).IsTrue();
    }

    [Test]
    [Arguments("other#/status")]
    [Arguments("pump?revision=other#/status")]
    [Arguments("https://other.example/things/pump#/status")]
    [Arguments("https://example.org/things/Pump#/status")]
    public async Task ExternalReferenceIsNotMarkedDocumentLocal(string target)
    {
        using var source = JsonDocument.Parse($$"""{"td:ref":"{{target}}"}""");
        var reference = WotReferenceParser.Parse(source.RootElement, new Uri(Target));
        await Assert.That(reference.IsDocumentLocal).IsFalse();
    }
}
